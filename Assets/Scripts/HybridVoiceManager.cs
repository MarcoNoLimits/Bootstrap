using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Tries custom ASR HTTP API first (same contract as <see cref="HololensAsrManager"/>:
/// POST float32 PCM, <c>X-Sample-Rate</c> header). On repeated failures, falls back to
/// HoloLens / Windows <see cref="UnityEngine.Windows.Speech.DictationRecognizer"/> via <see cref="VoiceManager"/>.
/// Exposes the same events as <see cref="VoiceManager"/> so <see cref="WizardOfOzClient"/> wiring stays unchanged.
/// </summary>
public sealed class HybridVoiceManager : IDisposable
{
    private readonly MonoBehaviour _host;
    private readonly string _primaryApiUrl;
    private readonly int _fallbackAfterConsecutiveApiFailures;
    /// <summary>Silence after the last transcript update before firing <see cref="OnSentenceCompleted"/> (mirrors phrase-finalization pause; Windows dictation is typically ~0.5–1.2s).</summary>
    private readonly float _phraseEndSilenceSeconds;

    private VoiceManager _dictation;
    private Coroutine _finalizeSentenceCo;
    private Coroutine _apiHealthWatchdogCo;
    private string _pendingTranslationText;
    private bool _disposed;
    private bool _usingApi;
    private int _consecutiveApiFailures;
    private bool _micWasQuiet = true;
    private float _lastTranscriptAt;
    private float _lastSpeechAt;

    public Action OnListeningStarted;
    public Action<string> OnHypothesis;
    public Action<string> OnSentenceCompleted;
    public Action<string> OnError;
    /// <summary>Fired when mic level crosses up (user likely speaking again). Clears Italian / idle text quickly.</summary>
    public Action OnSpeechBargeIn;

    public HybridVoiceManager(
        MonoBehaviour coroutineHost,
        string primaryApiUrl,
        int fallbackAfterConsecutiveApiFailures = 5,
        float phraseEndSilenceSeconds = 0.9f)
    {
        _host = coroutineHost;
        _primaryApiUrl = primaryApiUrl != null ? primaryApiUrl.Trim() : string.Empty;
        _fallbackAfterConsecutiveApiFailures = Mathf.Max(1, fallbackAfterConsecutiveApiFailures);
        _phraseEndSilenceSeconds = Mathf.Clamp(phraseEndSilenceSeconds, 0.35f, 3f);
    }

    public void Start()
    {
        if (_disposed) return;

        if (string.IsNullOrEmpty(_primaryApiUrl))
        {
            Debug.Log("[HybridVoice] No ASR API URL set — using HoloLens / Windows dictation only.");
            StartDictationOnly();
            return;
        }

        EnsureAsrManager();
        HololensAsrManager.Instance.SetApiUrl(_primaryApiUrl);
        HololensAsrManager.Instance.OnTextUpdated -= OnApiTextUpdated;
        HololensAsrManager.Instance.OnTextUpdated += OnApiTextUpdated;
        HololensAsrManager.Instance.OnApiRequestFinished -= OnApiRequestFinished;
        HololensAsrManager.Instance.OnApiRequestFinished += OnApiRequestFinished;
        HololensAsrManager.Instance.OnMicLevelUpdated -= OnMicLevelForBargeIn;
        HololensAsrManager.Instance.OnMicLevelUpdated += OnMicLevelForBargeIn;
        HololensAsrManager.Instance.OnMicrophoneNotReady -= OnUnityMicNotReady;
        HololensAsrManager.Instance.OnMicrophoneNotReady += OnUnityMicNotReady;
        HololensAsrManager.Instance.OnMicrophoneReady -= OnUnityMicReady;
        HololensAsrManager.Instance.OnMicrophoneReady += OnUnityMicReady;

        _usingApi = true;
        _consecutiveApiFailures = 0;
        _micWasQuiet = true;
        _lastTranscriptAt = Time.realtimeSinceStartup;
        _lastSpeechAt = Time.realtimeSinceStartup - 999f;
        if (_apiHealthWatchdogCo != null)
        {
            _host.StopCoroutine(_apiHealthWatchdogCo);
            _apiHealthWatchdogCo = null;
        }
        _apiHealthWatchdogCo = _host.StartCoroutine(ApiHealthWatchdog());
        Debug.Log($"[HybridVoice] API mode active. URL starts with: {_primaryApiUrl.Substring(0, Mathf.Min(48, _primaryApiUrl.Length))}…");
        HololensAsrManager.Instance.StartAsr();
        if (!HololensAsrManager.Instance.IsRunning)
        {
            // No device / null clip — OnMicrophoneNotReady already scheduled fallback.
            return;
        }
    }

    private void OnUnityMicReady()
    {
        Debug.Log("[HybridVoice] Mic ready — showing Listening state.");
        MainThreadDispatcher.RunOnMainThread(() => OnListeningStarted?.Invoke());
    }

    private void EnsureAsrManager()
    {
        if (HololensAsrManager.Instance != null) return;
        var go = new GameObject("HololensAsrManager");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<HololensAsrManager>();
    }

    private void OnUnityMicNotReady()
    {
        Debug.LogWarning("[HybridVoice] Unity Microphone failed — switching to HoloLens dictation (same as before API).");
        MainThreadDispatcher.RunOnMainThread(() =>
        {
            OnError?.Invoke("Unity Microphone did not start. Using HoloLens dictation.");
            SwitchToDictationFallback();
        });
    }

    private void OnApiRequestFinished(bool success)
    {
        if (!_usingApi || _disposed) return;
        if (success)
        {
            _consecutiveApiFailures = 0;
            return;
        }

        _consecutiveApiFailures++;
        if (_consecutiveApiFailures >= _fallbackAfterConsecutiveApiFailures)
        {
            Debug.LogWarning(
                "[HybridVoice] Custom ASR API failed repeatedly — switching to HoloLens / Windows dictation.");
            MainThreadDispatcher.RunOnMainThread(() =>
                OnError?.Invoke("ASR API not reachable. Switched to HoloLens dictation fallback."));
            SwitchToDictationFallback();
        }
    }

    private void OnMicLevelForBargeIn(float level)
    {
        if (!_usingApi || _disposed) return;
        const float loud = 0.11f;
        const float quiet = 0.035f;

        if (_micWasQuiet && level >= loud)
        {
            _micWasQuiet = false;
            _lastSpeechAt = Time.realtimeSinceStartup;
            HololensAsrManager.Instance?.ClearTranscriptContext();
            MainThreadDispatcher.RunOnMainThread(() => OnSpeechBargeIn?.Invoke());
        }
        else if (level >= loud)
        {
            _lastSpeechAt = Time.realtimeSinceStartup;
        }
        else if (level < quiet)
        {
            _micWasQuiet = true;
        }
    }

    private void OnApiTextUpdated(string text)
    {
        if (!_usingApi || _disposed) return;
        MainThreadDispatcher.RunOnMainThread(() =>
        {
            if (string.IsNullOrEmpty(text)) return;
            _lastTranscriptAt = Time.realtimeSinceStartup;
            OnHypothesis?.Invoke(text);
            _pendingTranslationText = text;
            if (_finalizeSentenceCo != null)
            {
                _host.StopCoroutine(_finalizeSentenceCo);
                _finalizeSentenceCo = null;
            }

            _finalizeSentenceCo = _host.StartCoroutine(FinalizeSentenceAfterPause(_phraseEndSilenceSeconds));
        });
    }

    private IEnumerator ApiHealthWatchdog()
    {
        while (!_disposed && _usingApi)
        {
            yield return new WaitForSecondsRealtime(1f);
            if (_disposed || !_usingApi) yield break;

            float now = Time.realtimeSinceStartup;
            bool userSpeakingRecently = now - _lastSpeechAt <= 3.0f;
            bool noTranscriptTooLong = now - _lastTranscriptAt >= 9.0f;
            if (userSpeakingRecently && noTranscriptTooLong)
            {
                MainThreadDispatcher.RunOnMainThread(() =>
                    OnError?.Invoke("ASR API is not returning speech while mic is active. Switched to HoloLens dictation fallback."));
                SwitchToDictationFallback();
                yield break;
            }
        }
    }

    private IEnumerator FinalizeSentenceAfterPause(float pauseSeconds)
    {
        yield return new WaitForSecondsRealtime(pauseSeconds);
        _finalizeSentenceCo = null;
        if (_disposed || !_usingApi) yield break;
        if (!string.IsNullOrEmpty(_pendingTranslationText))
        {
            var t = _pendingTranslationText;
            _pendingTranslationText = null;
            OnSentenceCompleted?.Invoke(t);
            HololensAsrManager.Instance?.ClearTranscriptContext();
        }
    }

    private void SwitchToDictationFallback()
    {
        if (_disposed) return;
        _usingApi = false;
        if (_finalizeSentenceCo != null)
        {
            _host.StopCoroutine(_finalizeSentenceCo);
            _finalizeSentenceCo = null;
        }
        if (_apiHealthWatchdogCo != null)
        {
            _host.StopCoroutine(_apiHealthWatchdogCo);
            _apiHealthWatchdogCo = null;
        }

        if (HololensAsrManager.Instance != null)
        {
            HololensAsrManager.Instance.OnTextUpdated -= OnApiTextUpdated;
            HololensAsrManager.Instance.OnApiRequestFinished -= OnApiRequestFinished;
            HololensAsrManager.Instance.OnMicLevelUpdated -= OnMicLevelForBargeIn;
            HololensAsrManager.Instance.OnMicrophoneNotReady -= OnUnityMicNotReady;
            HololensAsrManager.Instance.OnMicrophoneReady -= OnUnityMicReady;
            HololensAsrManager.Instance.StopAsr();
        }

        StartDictationOnly();
    }

    private void StartDictationOnly()
    {
        if (_dictation != null) return;

        _dictation = new VoiceManager();
        _dictation.OnListeningStarted += () => OnListeningStarted?.Invoke();
        _dictation.OnHypothesis += (p) => OnHypothesis?.Invoke(p);
        _dictation.OnSentenceCompleted += (t) => OnSentenceCompleted?.Invoke(t);
        _dictation.OnError += (e) => OnError?.Invoke(e);
        _dictation.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_finalizeSentenceCo != null && _host != null)
        {
            _host.StopCoroutine(_finalizeSentenceCo);
            _finalizeSentenceCo = null;
        }
        if (_apiHealthWatchdogCo != null && _host != null)
        {
            _host.StopCoroutine(_apiHealthWatchdogCo);
            _apiHealthWatchdogCo = null;
        }

        if (HololensAsrManager.Instance != null)
        {
            HololensAsrManager.Instance.OnTextUpdated -= OnApiTextUpdated;
            HololensAsrManager.Instance.OnApiRequestFinished -= OnApiRequestFinished;
            HololensAsrManager.Instance.OnMicLevelUpdated -= OnMicLevelForBargeIn;
            HololensAsrManager.Instance.OnMicrophoneNotReady -= OnUnityMicNotReady;
            HololensAsrManager.Instance.OnMicrophoneReady -= OnUnityMicReady;
            HololensAsrManager.Instance.StopAsr();
        }

        _dictation?.Dispose();
        _dictation = null;
    }
}
