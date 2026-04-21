using System;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;
#if UNITY_WSA && !UNITY_EDITOR
using System.Threading.Tasks;
#endif

/// <summary>
/// Tries custom ASR HTTP API first (same contract as <see cref="HololensAsrManager"/>:
/// POST float32 PCM, <c>X-Sample-Rate</c> header). On repeated failures, falls back to
/// HoloLens / Windows <see cref="UnityEngine.Windows.Speech.DictationRecognizer"/> via <see cref="VoiceManager"/>.
/// Exposes the same events as <see cref="VoiceManager"/> so <see cref="WizardOfOzClient"/> wiring stays unchanged.
/// </summary>
public sealed class HybridVoiceManager : IDisposable
{
    /// <summary>Shown on subtitle when falling back to device dictation (not an error).</summary>
    public const string AsrFallbackUserMessage = "Switching to another ASR model";

    private readonly MonoBehaviour _host;
    private readonly string _primaryApiUrl;
    private readonly int _fallbackAfterConsecutiveApiFailures;
    /// <summary>Silence after the last transcript update before firing <see cref="OnSentenceCompleted"/> (mirrors phrase-finalization pause; Windows dictation is typically ~0.5–1.2s).</summary>
    private readonly float _phraseEndSilenceSeconds;
    private readonly bool _forceLocalDictationOnly;
    private readonly bool _enableApiSilenceFallback;
    private readonly bool _enableApiFailureFallback;
    private readonly bool _enableApiEmptyResponseFallback;
    private readonly bool _useItalianWinRtSpeech;

    private VoiceManager _dictation;
#if UNITY_WSA && !UNITY_EDITOR
    private ItalianWinRtSpeechRecognizer _italianWinRt;
#endif
    private Coroutine _finalizeSentenceCo;
    private Coroutine _apiHealthWatchdogCo;
    private string _pendingTranslationText;
    private string _lastCommittedSentence;
    private float _lastCommittedAt = -999f;
    private string _lastHypothesisText;
    private float _lastHypothesisAt = -999f;
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
        float phraseEndSilenceSeconds = 0.85f,
        bool forceLocalDictationOnly = false,
        bool enableApiSilenceFallback = false,
        bool enableApiFailureFallback = false,
        bool enableApiEmptyResponseFallback = false,
        bool useItalianWinRtSpeech = false)
    {
        _host = coroutineHost;
        _primaryApiUrl = primaryApiUrl != null ? primaryApiUrl.Trim() : string.Empty;
        _fallbackAfterConsecutiveApiFailures = Mathf.Max(1, fallbackAfterConsecutiveApiFailures);
        _phraseEndSilenceSeconds = Mathf.Clamp(phraseEndSilenceSeconds, 0.35f, 3f);
        _forceLocalDictationOnly = forceLocalDictationOnly;
        _enableApiSilenceFallback = enableApiSilenceFallback;
        _enableApiFailureFallback = enableApiFailureFallback;
        _enableApiEmptyResponseFallback = enableApiEmptyResponseFallback;
        _useItalianWinRtSpeech = useItalianWinRtSpeech;
    }

    public void Start()
    {
        if (_disposed) return;

        if (_forceLocalDictationOnly)
        {
            Debug.Log("[HybridVoice] Local dictation-only mode active.");
#if UNITY_WSA && !UNITY_EDITOR
            if (_useItalianWinRtSpeech)
            {
                StartItalianWinRt();
                return;
            }
#endif
            StartDictationOnly();
            return;
        }

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
        HololensAsrManager.Instance.OnRepeatedEmptySuccessfulApiResponses -= OnRepeatedEmptySuccessfulApiResponses;
        HololensAsrManager.Instance.OnRepeatedEmptySuccessfulApiResponses += OnRepeatedEmptySuccessfulApiResponses;

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
        if (_enableApiSilenceFallback)
        {
            _apiHealthWatchdogCo = _host.StartCoroutine(ApiHealthWatchdog());
        }
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
        MainThreadDispatcher.RunOnMainThread(SwitchToDictationFallback);
    }

    private void OnRepeatedEmptySuccessfulApiResponses()
    {
        if (!_usingApi || _disposed) return;
        if (!_enableApiEmptyResponseFallback)
        {
            // HTTP 200 with {"text":""} is valid for quiet/short chunks; keep API active and avoid caption spam.
            Debug.Log("[HybridVoice] ASR returned repeated empty text chunks; continuing API mode.");
            return;
        }
        Debug.LogWarning(
            "[HybridVoice] Remote ASR returned HTTP 200 but no usable transcript repeatedly — switching to local dictation.");
        MainThreadDispatcher.RunOnMainThread(SwitchToDictationFallback);
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
            _consecutiveApiFailures = 0;
            if (!_enableApiFailureFallback)
            {
                MainThreadDispatcher.RunOnMainThread(() =>
                    OnError?.Invoke("ASR API had repeated failures but remains active. Retrying..."));
                return;
            }
            Debug.LogWarning(
                "[HybridVoice] Custom ASR API failed repeatedly — switching to HoloLens / Windows dictation.");
            MainThreadDispatcher.RunOnMainThread(SwitchToDictationFallback);
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
            string hypothesis = NormalizeTranscriptForDedupe(text);
            if (string.IsNullOrEmpty(hypothesis))
            {
                return;
            }
            if (string.Equals(_lastHypothesisText, hypothesis, StringComparison.OrdinalIgnoreCase)
                && Time.realtimeSinceStartup - _lastHypothesisAt < 1.5f)
            {
                return;
            }
            _lastHypothesisText = hypothesis;
            _lastHypothesisAt = Time.realtimeSinceStartup;
            OnHypothesis?.Invoke(hypothesis);
            _pendingTranslationText = hypothesis;
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
                // Do not auto-deactivate API ASR on silence/transcript stalls.
                // Keep listening and only surface a non-fatal hint.
                MainThreadDispatcher.RunOnMainThread(() =>
                    OnError?.Invoke("ASR is still listening. No transcript yet; keep speaking clearly."));
                _lastTranscriptAt = now;
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
            string normalized = NormalizeTranscriptForDedupe(t);
            if (!string.IsNullOrEmpty(normalized))
            {
                bool duplicate = string.Equals(_lastCommittedSentence, normalized, StringComparison.OrdinalIgnoreCase)
                    || (Time.realtimeSinceStartup - _lastCommittedAt < 1.2f
                        && normalized.StartsWith(_lastCommittedSentence ?? "", StringComparison.OrdinalIgnoreCase)
                        && normalized.Length <= (_lastCommittedSentence ?? "").Length + 4);
                if (!duplicate)
                {
                    _lastCommittedSentence = normalized;
                    _lastCommittedAt = Time.realtimeSinceStartup;
                    OnSentenceCompleted?.Invoke(normalized);
                }
            }
            HololensAsrManager.Instance?.ClearTranscriptContext();
            // Mic/API capture never stopped; re-signal listening so UI does not look idle after a pause.
            MainThreadDispatcher.RunOnMainThread(() => OnListeningStarted?.Invoke());
        }
    }

    private static string NormalizeTranscriptForDedupe(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = Regex.Replace(text.Trim(), "\\s+", " ");
        normalized = Regex.Replace(normalized, "\\b(\\w+)(\\s+\\1\\b){1,}", "$1", RegexOptions.IgnoreCase);
        return normalized.Trim(' ', '.', ',', ';', ':');
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
            HololensAsrManager.Instance.OnRepeatedEmptySuccessfulApiResponses -= OnRepeatedEmptySuccessfulApiResponses;
            HololensAsrManager.Instance.StopAsr();
        }

        StartDictationOnly();
    }

#if UNITY_WSA && !UNITY_EDITOR
    private void StartItalianWinRt()
    {
        if (_italianWinRt != null)
        {
            return;
        }

        if (!ItalianWinRtSpeechRecognizer.IsItalianSpeechEngineAvailable())
        {
            Debug.LogWarning("[HybridVoice] it-IT speech engine not on device — Italian unavailable.");
            OnError?.Invoke("Italian unavailable");
            return;
        }

        _italianWinRt = new ItalianWinRtSpeechRecognizer();
        _italianWinRt.OnListeningStarted += () => OnListeningStarted?.Invoke();
        _italianWinRt.OnHypothesis += (p) => OnHypothesis?.Invoke(p);
        _italianWinRt.OnSentenceCompleted += (t) => OnSentenceCompleted?.Invoke(t);
        _italianWinRt.OnError += (e) => OnError?.Invoke(e);
        _ = StartItalianWinRtAsync();
    }

    private async Task StartItalianWinRtAsync()
    {
        if (_italianWinRt == null || _disposed)
        {
            return;
        }

        await _italianWinRt.StartAsync();
    }
#endif

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
            HololensAsrManager.Instance.OnRepeatedEmptySuccessfulApiResponses -= OnRepeatedEmptySuccessfulApiResponses;
            HololensAsrManager.Instance.StopAsr();
        }

#if UNITY_WSA && !UNITY_EDITOR
        _italianWinRt?.Dispose();
        _italianWinRt = null;
#endif

        _dictation?.Dispose();
        _dictation = null;
    }
}
