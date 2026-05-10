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
    private readonly bool _preferLocalThenApiFallback;
    private readonly bool _useItalianWinRtSpeech;

    private VoiceManager _dictation;
#if UNITY_WSA && !UNITY_EDITOR
    private ItalianWinRtSpeechRecognizer _italianWinRt;
#endif
    private Coroutine _finalizeSentenceCo;
    private Coroutine _apiHealthWatchdogCo;
    /// <summary>Normalized ASR source text last handed to NMT (or shown raw when NMT off).</summary>
    private string _lastTranslatedSourceSentence = string.Empty;
    /// <summary>Current phrase caption buffer (not full conversation history). Mirrors overlap merge from <see cref="HololensAsrManager"/>.</summary>
    private string _currentUtteranceTranscript = string.Empty;
    private int _latestAcceptedApiChunkId;
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
    private float _lastBargeInAt = -999f;

    public Action OnListeningStarted;
    public Action<string> OnHypothesis;
    public Action<string> OnSentenceCompleted;
    public Action<string> OnError;
    /// <summary>Fired when mic level crosses up (user likely speaking again). Clears Italian / idle text quickly.</summary>
    public Action OnSpeechBargeIn;

    /// <summary>Record ASR source sentence after NMT completes (or raw path) so UI does not re-translate the same utterance.</summary>
    public void NotifyTranslationSourceHandled(string sourceSentence)
    {
        _lastTranslatedSourceSentence = NormalizeTranscriptForDedupe(sourceSentence ?? string.Empty);
    }

    /// <summary>True when merged HoloLens caption differs from the last source sentence sent/handled for NMT.</summary>
    public bool TryGetUntranslatedMergedAsrUtterance(out string sourceForNmt)
    {
        sourceForNmt = null;
        if (!_usingApi || _disposed || HololensAsrManager.Instance == null)
        {
            return false;
        }

        string rolled = NormalizeTranscriptForDedupe(HololensAsrManager.Instance.CurrentUtteranceTranscript ?? string.Empty);
        if (string.IsNullOrWhiteSpace(rolled))
        {
            return false;
        }

        if (HololensAsrManager.Instance.UtteranceAwaitingAsrContinuation)
        {
            return false;
        }

        string lt = _lastTranslatedSourceSentence ?? string.Empty;
        if (string.Equals(lt, rolled, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        sourceForNmt = rolled;
        return true;
    }

    public HybridVoiceManager(
        MonoBehaviour coroutineHost,
        string primaryApiUrl,
        int fallbackAfterConsecutiveApiFailures = 5,
        float phraseEndSilenceSeconds = 0.85f,
        bool forceLocalDictationOnly = false,
        bool enableApiSilenceFallback = false,
        bool enableApiFailureFallback = false,
        bool enableApiEmptyResponseFallback = false,
        bool preferLocalThenApiFallback = false,
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
        _preferLocalThenApiFallback = preferLocalThenApiFallback;
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

        if (_preferLocalThenApiFallback)
        {
            Debug.Log("[HybridVoice] Local dictation primary mode active (API fallback on local error).");
            StartDictationOnly(localPrimaryWithApiFallback: true);
            return;
        }

        if (string.IsNullOrEmpty(_primaryApiUrl))
        {
            Debug.Log("[HybridVoice] No ASR API URL set — using HoloLens / Windows dictation only.");
            StartDictationOnly();
            return;
        }

        StartApiMode();
    }

    private void StartApiMode()
    {
        if (string.IsNullOrEmpty(_primaryApiUrl))
        {
            StartDictationOnly();
            return;
        }

        EnsureAsrManager();
        HololensAsrManager.Instance.SetApiUrl(_primaryApiUrl);
        HololensAsrManager.Instance.OnTextUpdatedDetailed -= OnApiTextUpdatedDetailed;
        HololensAsrManager.Instance.OnTextUpdatedDetailed += OnApiTextUpdatedDetailed;
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
        HololensAsrManager.Instance.OnUtteranceContextCleared -= OnHololensUtteranceContextCleared;
        HololensAsrManager.Instance.OnUtteranceContextCleared += OnHololensUtteranceContextCleared;

        _usingApi = true;
        _consecutiveApiFailures = 0;
        _currentUtteranceTranscript = string.Empty;
        _latestAcceptedApiChunkId = 0;
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

    private void OnHololensUtteranceContextCleared(string reason)
    {
        if (!_usingApi || _disposed)
        {
            return;
        }

        _currentUtteranceTranscript = string.Empty;
        _latestAcceptedApiChunkId = 0;
        _lastHypothesisText = string.Empty;
        _lastTranslatedSourceSentence = string.Empty;
    }

    private void OnMicLevelForBargeIn(float level)
    {
        if (!_usingApi || _disposed) return;
        // Slightly more sensitive thresholds for HoloLens mics so soft speech keeps the phrase alive.
        const float loud = 0.075f;
        const float quiet = 0.025f;

        if (_micWasQuiet && level >= loud)
        {
            _micWasQuiet = false;
            _lastSpeechAt = Time.realtimeSinceStartup;
            _lastBargeInAt = Time.realtimeSinceStartup;
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

    private void OnApiTextUpdatedDetailed(string text, bool isFinal)
    {
        if (!_usingApi || _disposed) return;
        MainThreadDispatcher.RunOnMainThread(() =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            int chunkId = HololensAsrManager.Instance != null
                ? HololensAsrManager.Instance.LatestAcceptedChunkId
                : 0;

            _lastTranscriptAt = Time.realtimeSinceStartup;
            // HololensAsrManager already overlap-merges chunks into one current-utterance string — use it as source of truth (avoid double-merge tail-only bugs).
            string newText = NormalizeTranscriptForDedupe(text);
            if (string.IsNullOrWhiteSpace(newText))
            {
                return;
            }

            if (chunkId > 0)
            {
                _latestAcceptedApiChunkId = Mathf.Max(_latestAcceptedApiChunkId, chunkId);
            }

            string oldUtterance = _currentUtteranceTranscript;
            _currentUtteranceTranscript = newText;
            LogAsrPipelineHybrid(
                $"[ASR MERGE] old='{EscapeQuotesForAsrLog(oldUtterance)}' new='{EscapeQuotesForAsrLog(newText)}' merged='{EscapeQuotesForAsrLog(_currentUtteranceTranscript)}'");
            string hypothesis = NormalizeTranscriptForDedupe(_currentUtteranceTranscript);
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
            LogAsrPipelineHybrid($"[ASR DISPLAY] text='{EscapeQuotesForAsrLog(hypothesis)}'");
            OnHypothesis?.Invoke(hypothesis);
            if (_finalizeSentenceCo != null)
            {
                _host.StopCoroutine(_finalizeSentenceCo);
                _finalizeSentenceCo = null;
            }

            float finalizeDelay = isFinal ? 0.08f : _phraseEndSilenceSeconds;
            _finalizeSentenceCo = _host.StartCoroutine(FinalizeSentenceAfterPause(finalizeDelay));
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

        // If the user resumed speech right around the cutoff boundary, give ASR a short grace period
        // so we avoid splitting one human sentence into two fragments.
        const float resumeGraceSeconds = 0.75f;
        float now = Time.realtimeSinceStartup;
        if (now - _lastBargeInAt <= resumeGraceSeconds)
        {
            _finalizeSentenceCo = _host.StartCoroutine(FinalizeSentenceAfterPause(resumeGraceSeconds));
            yield break;
        }
        var asr = HololensAsrManager.Instance;
        if (asr != null)
        {
            if (asr.UtteranceAwaitingAsrContinuation)
            {
                _finalizeSentenceCo = _host.StartCoroutine(FinalizeSentenceAfterPause(0.45f));
                yield break;
            }

            if (asr.ShouldDeferSentenceGapDueToPipelineOrBuffer())
            {
                _finalizeSentenceCo = _host.StartCoroutine(FinalizeSentenceAfterPause(0.55f));
                yield break;
            }

            // Don't finalize while a chunk is still being decoded remotely.
            if (asr.IsApiRequestInFlight)
            {
                _finalizeSentenceCo = _host.StartCoroutine(FinalizeSentenceAfterPause(0.55f));
                yield break;
            }

            // If mic still indicates recent speech, give ASR one extra grace window.
            if (now - _lastSpeechAt <= 1.2f)
            {
                _finalizeSentenceCo = _host.StartCoroutine(FinalizeSentenceAfterPause(0.45f));
                yield break;
            }
        }

        string rolled = asr != null
            ? NormalizeTranscriptForDedupe(asr.CurrentUtteranceTranscript ?? string.Empty)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(rolled))
        {
            yield break;
        }

        string candidate = rolled;
        bool duplicate = string.Equals(_lastCommittedSentence, candidate, StringComparison.OrdinalIgnoreCase);
        if (!duplicate)
        {
            _lastCommittedSentence = candidate;
            _lastCommittedAt = Time.realtimeSinceStartup;
            OnSentenceCompleted?.Invoke(candidate);
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

    private static string EscapeQuotesForAsrLog(string s, int maxLen = 200)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        string t = s.Replace("\\", "\\\\").Replace("\r", " ").Replace("\n", " ").Replace("'", "\\'");
        return t.Length > maxLen ? t.Substring(0, maxLen) + "…" : t;
    }

    /// <summary>Mirrors <see cref="HololensAsrManager"/> pipeline logs — twin lines so Device Portal grep finds either prefix.</summary>
    private static void LogAsrPipelineHybrid(string line)
    {
        Debug.Log(line);
        Debug.Log("[ASR] " + line);
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
            HololensAsrManager.Instance.OnTextUpdatedDetailed -= OnApiTextUpdatedDetailed;
            HololensAsrManager.Instance.OnApiRequestFinished -= OnApiRequestFinished;
            HololensAsrManager.Instance.OnMicLevelUpdated -= OnMicLevelForBargeIn;
            HololensAsrManager.Instance.OnMicrophoneNotReady -= OnUnityMicNotReady;
            HololensAsrManager.Instance.OnMicrophoneReady -= OnUnityMicReady;
            HololensAsrManager.Instance.OnRepeatedEmptySuccessfulApiResponses -= OnRepeatedEmptySuccessfulApiResponses;
            HololensAsrManager.Instance.OnUtteranceContextCleared -= OnHololensUtteranceContextCleared;
            HololensAsrManager.Instance.StopAsr();
        }

        _currentUtteranceTranscript = string.Empty;
        _latestAcceptedApiChunkId = 0;
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

    private void StartDictationOnly(bool localPrimaryWithApiFallback = false)
    {
        if (_dictation != null) return;

        _dictation = new VoiceManager();
        _dictation.OnListeningStarted += () => OnListeningStarted?.Invoke();
        _dictation.OnHypothesis += (p) => OnHypothesis?.Invoke(p);
        _dictation.OnSentenceCompleted += (t) => OnSentenceCompleted?.Invoke(t);
        _dictation.OnError += (e) =>
        {
            OnError?.Invoke(e);
            if (localPrimaryWithApiFallback && !_disposed && !string.IsNullOrWhiteSpace(_primaryApiUrl))
            {
                Debug.LogWarning("[HybridVoice] Local dictation error -> switching to API fallback.");
                _dictation?.Dispose();
                _dictation = null;
                StartApiMode();
            }
        };
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
            HololensAsrManager.Instance.OnTextUpdatedDetailed -= OnApiTextUpdatedDetailed;
            HololensAsrManager.Instance.OnApiRequestFinished -= OnApiRequestFinished;
            HololensAsrManager.Instance.OnMicLevelUpdated -= OnMicLevelForBargeIn;
            HololensAsrManager.Instance.OnMicrophoneNotReady -= OnUnityMicNotReady;
            HololensAsrManager.Instance.OnMicrophoneReady -= OnUnityMicReady;
            HololensAsrManager.Instance.OnRepeatedEmptySuccessfulApiResponses -= OnRepeatedEmptySuccessfulApiResponses;
            HololensAsrManager.Instance.OnUtteranceContextCleared -= OnHololensUtteranceContextCleared;
            HololensAsrManager.Instance.StopAsr();
        }

        _currentUtteranceTranscript = string.Empty;
        _latestAcceptedApiChunkId = 0;
#if UNITY_WSA && !UNITY_EDITOR
        _italianWinRt?.Dispose();
        _italianWinRt = null;
#endif

        _dictation?.Dispose();
        _dictation = null;
    }
}
