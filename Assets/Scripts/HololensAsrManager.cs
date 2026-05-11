using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

public class HololensAsrManager : MonoBehaviour
{
    /// <summary>Client-only pre-queue gates (does not change Space/server preprocess thresholds).</summary>
    private const float AsrDropRmsHard = 0.010f;
    private const float AsrDropRmsSoft = 0.018f;
    private const float AsrDropSilenceRatio = 0.75f;
    private const float AsrSendStrongRmsLog = 0.020f;
    private const float AsrBorderlineRmsSend = 0.018f;
    private const float AsrBorderlinePeakSend = 0.035f;

    /// <summary>Italian client-side pre-queue drop (Space thresholds unchanged).</summary>
    private const float ItDropRmsHard = 0.008f;
    private const float ItDropSilenceRatio = 0.92f;
    private const float ItDropRmsSoftCap = 0.012f;
    private const float ItSendMinRms = 0.012f;
    private const float ItSendMinPeak = 0.035f;

    private const float SentenceGapSilenceSeconds = 1.5f;
    private const float MicQuietHoldRms = 0.012f;
    private const float MicSpeechResumeRms = 0.016f;

    public static HololensAsrManager Instance { get; private set; }

    public bool IsRunning { get; private set; }
    public float CurrentMicLevel { get; private set; }
    /// <summary>True while POST /audio is in flight. Remote cold starts can exceed 60s; UI should not treat this as "no speech".</summary>
    public bool IsApiRequestInFlight => _requestInFlight;

    /// <summary>Overlap merge ended with trailing ellipsis — wait for continuation chunks before sentence-gap clear / NMT finalize.</summary>
    public bool UtteranceAwaitingAsrContinuation => _utteranceAwaitingContinuation;

    /// <summary>Defer sentence-gap clear while HTTP or queued chunks exist, or a full chunk of mic audio is still buffered.</summary>
    public bool ShouldDeferSentenceGapDueToPipelineOrBuffer()
    {
        if (_requestInFlight || _pendingFloat32Queue.Count > 0)
        {
            return true;
        }

        int hz = _lastBufferedClipHz > 0 ? _lastBufferedClipHz : _sampleRate;
        float chunkSec = Mathf.Clamp(Mathf.Min(_fixedSendIntervalSeconds, _fixedMaxChunkSeconds), 2f, 3f);
        int samplesNeeded = Mathf.Max(1, Mathf.RoundToInt(hz * chunkSec));
        return _lastBufferedDeltaSamples >= samplesNeeded;
    }

    public delegate void TextUpdatedHandler(string text);
    public delegate void TextUpdatedDetailedHandler(string text, bool isFinal);
    public event TextUpdatedHandler OnTextUpdated;
    public event TextUpdatedDetailedHandler OnTextUpdatedDetailed;
    public event Action<float> OnMicLevelUpdated;
    /// <summary>Fired after each HTTP attempt; <paramref name="success"/> is true when the response was received and parsed.</summary>
    public event Action<bool> OnApiRequestFinished;
    /// <summary>Unity <see cref="Microphone"/> never reached a recording state (permissions, device, or platform).</summary>
    public event Action OnMicrophoneNotReady;
    /// <summary>Fired once when <see cref="Microphone.IsRecording"/> becomes true and capture begins.</summary>
    public event Action OnMicrophoneReady;
    /// <summary>Fired when many consecutive HTTP 200 responses parse to empty text (HF shape mismatch or silent chunks) — use to fall back to local dictation.</summary>
    public event Action OnRepeatedEmptySuccessfulApiResponses;
    /// <summary>Fired when the overlap-merged current-utterance buffer is cleared (sentence gap, language change, manual reset, new capture).</summary>
    public event Action<string> OnUtteranceContextCleared;
    [Header("ASR API")]
    [Tooltip("Transcribe URL: POST raw float32 PCM mono (little-endian), Content-Type application/octet-stream, header X-Sample-Rate matching _sampleRate (e.g. 16000). Response JSON { \"text\": \"...\" }.")]
    [SerializeField] private string _asrApiUrl = "https://thedeezat-asr-hearing-impaired-api.hf.space/audio";
    [Tooltip("Write detailed ASR logs to persistentDataPath/asr_debug.log. Keep off for faster runtime on device.")]
    [SerializeField] private bool _writeAsrDebugFile = true;
    [SerializeField] private int _sampleRate = 16000;
    [Header("Fixed-window streaming (HoloLens)")]
    [Tooltip("Send each POST using this many seconds of mic audio (clamped ≤ Max Chunk Seconds).")]
    [SerializeField] private float _fixedSendIntervalSeconds = 2.5f;
    [Tooltip("Overlap carried into the next chunk.")]
    [SerializeField] private float _fixedOverlapSeconds = 0.3f;
    [Tooltip("Hard cap on encoded chunk duration.")]
    [SerializeField] private float _fixedMaxChunkSeconds = 3.0f;
    [SerializeField] private float _chunkSeconds = 2.5f;
    [SerializeField] private float _sendWindowSeconds = 3.0f;
    [SerializeField] private float _chunkOverlapSeconds = 0.3f;
    [Tooltip("Max float32 chunks queued while one POST is in flight (speech is buffered, not dropped).")]
    [SerializeField] private int _maxPendingAudioChunks = 2048;
    [Tooltip("VAD threshold floor for mic RMS.")]
    [SerializeField] private float _vadMinNoiseFloor = 0.006f;
    [Tooltip("Speech threshold multiplier over rolling noise RMS.")]
    [SerializeField] private float _vadSpeechMultiplier = 2.8f;
    [Tooltip("Skip POST when chunk RMS is below this threshold (client-side silence guard).")]
    [SerializeField] private float _minChunkRmsToSend = 0.0074f;
    [Tooltip("Minimum fraction of chunk samples that must look like speech energy before POST.")]
    [SerializeField] private float _minSpeechFrameRatioToSend = 0.14f;
    [Tooltip("Enable software high-pass before VAD/send checks (attenuates wind/rumble).")]
    [SerializeField] private bool _enableHighPassFilter = true;
    [Tooltip("High-pass cutoff in Hz (80-100 recommended for speech robustness).")]
    [SerializeField] private float _highPassCutoffHz = 90f;
    [Tooltip("Minimum voiced-frame ratio to accept chunk as human speech.")]
    [SerializeField] private float _minVoicedFrameRatioToSend = 0.22f;
    [Tooltip("Seconds used at startup to sample room noise floor before adapting VAD aggressively.")]
    [SerializeField] private float _noiseFloorWarmupSeconds = 1.2f;
    [SerializeField] private float _forcedLanguageHardCapSeconds = 3.0f;
    [SerializeField] private int _clipLengthSeconds = 30;
    [Header("Microphone selection")]
    [Tooltip("If set, prefer a microphone whose device name contains this text (case-insensitive).")]
    [SerializeField] private string _preferredMicNameContains = "";
    [Tooltip("After this many consecutive HTTP 200 responses with empty parsed text, raise OnRepeatedEmptySuccessfulApiResponses (HybridVoice switches to dictation).")]
    [SerializeField] private int _emptyHttp200StreakBeforeFallback = 10;
    [Header("Input level (matches HF Space preprocess: quiet audio is dropped server-side)")]
    [Tooltip("Boost quiet microphone PCM so RMS passes the Space ASR_MIN_RMS gate (~0.007 after server high-pass). Disable only for debugging.")]
    [SerializeField] private bool _adaptiveInputGain = true;
    [SerializeField] private float _adaptiveGainTargetRms = 0.038f;
    [SerializeField] private float _adaptiveGainMax = 8f;
    [Tooltip("HF cold start / long clips; UnityWebRequest uses seconds.")]
    [SerializeField] private int _requestTimeoutSeconds = 8;
    [Tooltip("When enabled, print per-chunk send/response telemetry for API debugging.")]
    [SerializeField] private bool _logPerChunkTelemetry = true;
    [Tooltip("If enabled, save each outgoing ASR chunk as WAV in persistentDataPath/asr_sent_audio for Device Portal debugging.")]
    [SerializeField] private bool _saveOutgoingAsrAudio = true;
    [Tooltip("Maximum saved ASR chunk WAV files to keep on device (older files are deleted).")]
    [SerializeField] private int _maxSavedOutgoingAsrAudioFiles = 300;
    [Tooltip("Reject weak short tail chunks for a brief window after a strong sentence.")]
    [SerializeField] private bool _rejectWeakShortTailAfterStrongSentence = false;
    [SerializeField] private float _tailRejectWindowSeconds = 1.25f;
    [SerializeField] private float _tailRejectMaxChunkSeconds = 1.15f;
    [SerializeField] private float _tailRejectWeakRms = 0.0175f;
    [SerializeField] private int _strongSentenceMinChars = 12;
    [SerializeField] private float _strongSentenceMinRms = 0.021f;
    [Tooltip("Optional language hint for /audio API: english, italian, or auto/empty.")]
    [SerializeField] private string _forcedLanguage = "";
    [Header("HF client telemetry")]
    [Tooltip("POST HoloLens pipeline lines to Space /client_log (HF stdout). Fire-and-forget; never blocks ASR.")]
    [SerializeField] private bool _sendClientLogsToServer = true;
    [Tooltip("Optional Space origin (no path). Empty = derive from ASR URL (strip /audio).")]
    [SerializeField] private string _clientLogSpaceRootOverride = "";
    [Tooltip("Mirror [ASR RAW RESPONSE] / MERGE / DISPLAY / CLEAR / BUFFER to Unity Console + optional local ASR debug file. HF /client_log is independent (Send Client Logs To Server). Turn off on device builds if you only want HF logs.")]
    [SerializeField] private bool _logAsrPipelineLocal = true;
    [Tooltip("Append session_id/chunk_id to [client] lines and JSON; also enable via env DEBUG_VERBOSE_IDS=1.")]
    [SerializeField] private bool _debugVerboseTelemetryIds = false;
    private bool _warnedNonAudioEndpoint;

    /// <summary>Last <c>text_en</c> / <c>english</c> from Italian pipeline JSON, if present.</summary>
    public string LastEnglishFromApi { get; private set; } = "";

    /// <summary>
    /// Active sentence only (overlap-merge across chunks in one utterance). Cleared on manual reset, language change, capture restart,
    /// or ~1.2s sustained mic quiet followed by new speech (sentence boundary).
    /// </summary>
    public string CurrentUtteranceTranscript => _currentUtteranceSb.ToString();

    /// <summary>Alias of <see cref="CurrentUtteranceTranscript"/> for backwards compatibility.</summary>
    public string RollingTranscript => CurrentUtteranceTranscript;

    public int LatestAcceptedChunkId => _latestResponseSeqApplied;

    private AudioClip _micClip;
    private string _micDevice;
    private Coroutine _captureCoroutine;
    private int _lastMicSample;
    /// <summary>Overlap-merged current phrase only — never assign chunk-only text here; use merged transcript for UI.</summary>
    private readonly StringBuilder _currentUtteranceSb = new StringBuilder();
    private bool _requestInFlight;
    private struct PendingChunk
    {
        public byte[] Bytes;
        public float SpeechMsTelemetry;
        public float SilenceRatioTelemetry;
    }
    /// <summary>Float32 LE mono chunks queued while one POST is in flight (never drops queued audio).</summary>
    private readonly Queue<PendingChunk> _pendingFloat32Queue = new Queue<PendingChunk>(8);
    private int _chunksUploaded;
    private int _requestAttemptCount;
    private int _requestSuccessCount;
    private int _requestFailureCount;
    private int _requestParseEmptyCount;
    private bool _loggedFirstChunk;
    private bool _loggedResample;
    private float _lastEmptyTranscriptLogTime = -999f;
    private float _lastAsrBufferLogRealtime = -999f;
    private float _lastBufferedMsForTelemetry = -99999f;
    private int _lastBufferInFlightForTelemetry = -1;
    private float _lastMicTelemetryRealtime = -999f;
    private float _micWindowPeakAbs;
    private float _lastChunkAdaptiveGain = 1f;
    private static string _logFilePath;
    private float _lastMicRms = 0f;
    private float _noiseRms = 0.01f;
    private float _asrStartedAtRealtime;

    private static bool _quittingHookRegistered;
    private int _requestSeq;
    private int _emptyHttp200Streak;
    /// <summary>Bumped when capture stops or mic fails so late HTTPS responses cannot mutate captions after session end.</summary>
    private int _captureGeneration;
    /// <summary>RMS of the float chunk last queued for POST (debug §5).</summary>
    private float _lastPostedChunkRms;
    /// <summary>Sample count of the chunk last queued for POST.</summary>
    private int _lastPostedChunkSamples;
    private float _lastPostedSpeechFrameRatio;
    private float _lastPostedVoicedFrameRatio;
    private float _lastStrongSentenceRealtime = -999f;
    private int _latestResponseSeqApplied;
    private string _asrAudioDumpDir;
    private bool _loggedAsrAudioDumpDir;
    private int _savedAudioSeq;
    private string _telemetrySessionId = "";

    /// <summary>Filled before QueueSend for compact SEND telemetry.</summary>
    private float _telemetrySendSpeechMs;
    private float _telemetrySendSilenceRatio = -1f;

    /// <summary>Realtime when a non-empty caption was last applied to <see cref="_currentUtteranceSb"/>.</summary>
    private float _lastNonEmptyDisplayRealtime = -999f;

    /// <summary>Realtime when mic or queued chunk last indicated speech activity (sentence-gap detector).</summary>
    private float _lastSpeechActivityRealtime = -999f;

    private float _micQuietSegmentSinceRealtime = -1f;
    private bool _sentenceGapArmClearOnNextSpeechOnset;
    private bool _utteranceAwaitingContinuation;
    private int _lastBufferedDeltaSamples;
    private int _lastBufferedClipHz;

    /// <summary>
    /// Italian forced-decode is prone to hallucinating on HoloLens room tone when we boost/send marginal audio.
    /// HF browser tests use cleaner push-to-mic levels than far-field capture + adaptive gain.
    /// </summary>
    private bool IsItalianStrictNoiseGuard =>
        string.Equals(_forcedLanguage, "italian", StringComparison.OrdinalIgnoreCase);

    private float EffectiveVadNoiseFloor() =>
        IsItalianStrictNoiseGuard ? Mathf.Max(_vadMinNoiseFloor, 0.009f) : _vadMinNoiseFloor;

    private float EffectiveVadSpeechMultiplier() =>
        IsItalianStrictNoiseGuard ? Mathf.Max(_vadSpeechMultiplier, 3.55f) : _vadSpeechMultiplier;

    private void ResetUtteranceBoundaryTracking()
    {
        _micQuietSegmentSinceRealtime = -1f;
        _sentenceGapArmClearOnNextSpeechOnset = false;
    }

    private static bool CaptionLooksIncompleteContinuation(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return false;
        }

        string t = caption.TrimEnd();
        return (t.Length >= 3 && t.EndsWith("...", StringComparison.Ordinal))
               || (t.Length >= 1 && t.EndsWith("…", StringComparison.Ordinal));
    }

    private void NotifyNonEmptyCaptionDisplayed(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        _lastNonEmptyDisplayRealtime = now;
        _lastSpeechActivityRealtime = now;
    }

    /// <summary>
    /// After ≥ <see cref="SentenceGapSilenceSeconds"/> of quiet mic while a caption exists, arm a one-shot clear before the next utterance.
    /// </summary>
    private void TickUtteranceBoundaryMicQuietArm(float now)
    {
        if (_utteranceAwaitingContinuation)
        {
            return;
        }

        if (_lastMicRms >= MicSpeechResumeRms)
        {
            _lastSpeechActivityRealtime = now;
            _micQuietSegmentSinceRealtime = -1f;
            return;
        }

        if (_lastMicRms <= MicQuietHoldRms)
        {
            if (_micQuietSegmentSinceRealtime < 0f)
            {
                _micQuietSegmentSinceRealtime = now;
            }

            if (_currentUtteranceSb.Length > 0
                && (now - _micQuietSegmentSinceRealtime) >= SentenceGapSilenceSeconds)
            {
                _sentenceGapArmClearOnNextSpeechOnset = true;
            }
        }
    }

    /// <param name="chunkPassedSendGate">True when a chunk passed client gates and will be queued (soft mic resume).</param>
    private void TrySentenceGapClearOnSpeechResume(bool chunkPassedSendGate)
    {
        if (!_sentenceGapArmClearOnNextSpeechOnset || _currentUtteranceSb.Length == 0)
        {
            return;
        }

        if (_utteranceAwaitingContinuation)
        {
            return;
        }

        if (ShouldDeferSentenceGapDueToPipelineOrBuffer())
        {
            return;
        }

        if (_lastMicRms >= MicSpeechResumeRms || chunkPassedSendGate)
        {
            ClearTranscriptContext("sentence_gap_1500ms");
        }
    }

    private bool ComputeShouldDropChunk(float chunkRms, float chunkPeakAbs, float silenceRatio)
    {
        if (IsItalianStrictNoiseGuard)
        {
            bool drop =
                chunkRms < ItDropRmsHard || (silenceRatio > ItDropSilenceRatio && chunkRms < ItDropRmsSoftCap);
            if (chunkRms >= ItSendMinRms && chunkPeakAbs >= ItSendMinPeak)
            {
                drop = false;
            }

            if (chunkPeakAbs >= ItSendMinPeak && chunkRms >= ItDropRmsHard)
            {
                drop = false;
            }

            return drop;
        }

        bool shouldDrop =
            chunkRms < AsrDropRmsHard || (chunkRms < AsrDropRmsSoft && silenceRatio > AsrDropSilenceRatio);
        if (chunkRms >= AsrBorderlineRmsSend && chunkPeakAbs >= AsrBorderlinePeakSend)
        {
            shouldDrop = false;
        }

        return shouldDrop;
    }

    private void ApplyAdaptiveGainForCurrentLanguage(float[] chunk)
    {
        _lastChunkAdaptiveGain = 1f;
        if (!_adaptiveInputGain || chunk == null || chunk.Length == 0)
        {
            return;
        }

        float target = _adaptiveGainTargetRms;
        float maxG = _adaptiveGainMax;
        if (IsItalianStrictNoiseGuard)
        {
            target = Mathf.Min(target, 0.028f);
            maxG = Mathf.Min(maxG, 3.25f);
        }

        _lastChunkAdaptiveGain = ApplyAdaptiveGain(chunk, target, maxG);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        if (string.IsNullOrEmpty(_telemetrySessionId))
        {
            _telemetrySessionId = Guid.NewGuid().ToString("N");
        }
    }

    private static void AsrFileLog(string line)
    {
        try
        {
            if (string.IsNullOrEmpty(_logFilePath))
                _logFilePath = Path.Combine(Application.persistentDataPath, "asr_debug.log");
            File.AppendAllText(_logFilePath, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + "\n");
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>Logs to Unity + asr_debug.log only (never drives HoloLens subtitle).</summary>
    private void EmitStatus(string line)
    {
        string full = "[ASR] " + line;
        Debug.Log(full);
        if (_writeAsrDebugFile)
        {
            AsrFileLog(full);
        }
    }

    private bool TelemetryVerboseIds =>
        _debugVerboseTelemetryIds
        || string.Equals(Environment.GetEnvironmentVariable("DEBUG_VERBOSE_IDS"), "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable CLEAR reason token for compact logs.</summary>
    private static string CompactClearReason(string sanitizedToken)
    {
        if (string.IsNullOrEmpty(sanitizedToken))
        {
            return "unspecified";
        }

        if (sanitizedToken.IndexOf("confirmed_silence", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "silence";
        }

        if (sanitizedToken.IndexOf("manual", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "manual";
        }

        if (sanitizedToken.IndexOf("start_capture", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "capture";
        }

        if (sanitizedToken.IndexOf("sentence_gap", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "sentence_gap";
        }

        return sanitizedToken.Length > 28 ? sanitizedToken.Substring(0, 28) : sanitizedToken;
    }

    private string AppendVerboseIdsSuffix(string compactLine, int chunkId)
    {
        if (!TelemetryVerboseIds || string.IsNullOrEmpty(compactLine))
        {
            return compactLine;
        }

        string sid = _telemetrySessionId ?? "";
        return $"{compactLine} session_id={sid} chunk_id={chunkId.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// <summary>Public wrapper so non-ASR-loop callers (Wizard / Hybrid NMT pipeline) can ship telemetry through the same channels.</summary>
    public void LogPipelineTelemetryLine(string compactLine, string telemetryEvent)
    {
        LogClientTelemetry(compactLine, telemetryEvent, Mathf.Max(0, LatestAcceptedChunkId));
    }

    /// Compact <c>[client] …</c> lines for Unity (optional) and HF <c>/client_log</c> (fire-and-forget JSON).
    /// </summary>
    private void LogClientTelemetry(string compactLine, string telemetryEvent, int chunkId)
    {
        if (string.IsNullOrEmpty(compactLine))
        {
            return;
        }

        string printed = AppendVerboseIdsSuffix(compactLine, chunkId);

        if (_logAsrPipelineLocal)
        {
            Debug.Log(printed);
            if (_writeAsrDebugFile)
            {
                AsrFileLog(printed);
            }
        }

        if (!_sendClientLogsToServer || string.IsNullOrEmpty(_telemetrySessionId))
        {
            return;
        }

        string url = EffectiveClientLogUrl();
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        StartCoroutine(CoPostClientLogFireAndForget(url, telemetryEvent, printed, chunkId));
    }

    private void MaybeLogBufferTelemetry(float bufferedMs, int inFlight)
    {
        if (!_sendClientLogsToServer && !_logAsrPipelineLocal)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now - _lastAsrBufferLogRealtime < 1f)
        {
            return;
        }

        bool msJump = Mathf.Abs(bufferedMs - _lastBufferedMsForTelemetry) >= 1000f;
        bool flightChanged = inFlight != _lastBufferInFlightForTelemetry;
        float rms = _lastMicRms;
        float peak = _micWindowPeakAbs;
        bool suspicious = rms < 0.0025f || rms > 0.085f || peak > 0.35f;
        if (!msJump && !flightChanged && !suspicious)
        {
            return;
        }

        _lastAsrBufferLogRealtime = now;
        _lastBufferedMsForTelemetry = bufferedMs;
        _lastBufferInFlightForTelemetry = inFlight;

        string compact = TelemetryVerboseIds
            ? $"[client] BUFFER ms={bufferedMs:F0} in_flight={inFlight} rms={rms:F3} peak={peak:F3}"
            : $"[client] BUFFER ms={bufferedMs:F0} rms={rms:F3} peak={peak:F3}";
        LogClientTelemetry(compact, "BUFFER", Mathf.Max(0, LatestAcceptedChunkId));
    }

    private void MaybeLogMicTelemetry(int currentPos, int totalSamples)
    {
        if (!_sendClientLogsToServer && !_logAsrPipelineLocal)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now - _lastMicTelemetryRealtime < 2f)
        {
            return;
        }

        _lastMicTelemetryRealtime = now;
        bool rec = !string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(_micDevice);
        string compact = TelemetryVerboseIds
            ? $"[client] MIC recording={(rec ? "True" : "False")} pos={currentPos} samples={totalSamples} rms={_lastMicRms:F3} peak={_micWindowPeakAbs:F3} gain={_lastChunkAdaptiveGain:F2}"
            : $"[client] MIC rms={_lastMicRms:F3} peak={_micWindowPeakAbs:F3} gain={_lastChunkAdaptiveGain:F2}";
        LogClientTelemetry(compact, "MIC", Mathf.Max(0, LatestAcceptedChunkId));
    }

    private void LogSendTelemetry(int requestSeq, string lang, float chunkSeconds, float rms, float peak)
    {
        string langTok = TelemetrySanitizeToken(lang);
        if (string.IsNullOrEmpty(langTok))
        {
            langTok = "english";
        }

        float sratio = _telemetrySendSilenceRatio >= 0f ? _telemetrySendSilenceRatio : 0f;
        string compact = TelemetryVerboseIds
            ? $"[client] SEND send_id={requestSeq} lang={langTok} speech_ms={_telemetrySendSpeechMs:F0} silence_ratio={sratio:F2} chunk_s={chunkSeconds:F2} rms={rms:F3} peak={peak:F3}"
            : $"[client] SEND lang={langTok} chunk_s={chunkSeconds:F2} rms={rms:F3} silence_ratio={sratio:F2}";
        LogClientTelemetry(compact, "SEND", requestSeq);
    }

    private static string TelemetrySanitizeToken(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return "";
        }

        return Regex.Replace(s.Trim(), "\\s+", "_");
    }

    private static string EscapeJsonTelemetry(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        return raw
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private string EffectiveClientLogUrl()
    {
        string root = (_clientLogSpaceRootOverride ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(root))
        {
            return root.TrimEnd('/') + "/client_log";
        }

        return DeriveClientLogUrlFromAsrAudioUrl(_asrApiUrl);
    }

    private static string DeriveClientLogUrlFromAsrAudioUrl(string asrAudioUrl)
    {
        if (string.IsNullOrWhiteSpace(asrAudioUrl))
        {
            return "";
        }

        string u = asrAudioUrl.Trim();
        int idx = u.IndexOf("/audio", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return u.Substring(0, idx).TrimEnd('/') + "/client_log";
        }

        idx = u.IndexOf("/transcribe", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return u.Substring(0, idx).TrimEnd('/') + "/client_log";
        }

        int lastSlash = u.LastIndexOf('/');
        if (lastSlash > "https://x".Length)
        {
            return u.Substring(0, lastSlash).TrimEnd('/') + "/client_log";
        }

        return u.TrimEnd('/') + "/client_log";
    }

    private IEnumerator CoPostClientLogFireAndForget(string url, string telemetryEvent, string line, int chunkId)
    {
        string sid = EscapeJsonTelemetry(_telemetrySessionId ?? "");
        string ev = EscapeJsonTelemetry(telemetryEvent ?? "");
        string ln = EscapeJsonTelemetry(line ?? "");
        string json = TelemetryVerboseIds
            ? "{\"device\":\"hololens\",\"session_id\":\"" + sid +
              "\",\"chunk_id\":" + chunkId.ToString(CultureInfo.InvariantCulture) +
              ",\"event\":\"" + ev + "\",\"line\":\"" + ln + "\"}"
            : "{\"device\":\"hololens\",\"event\":\"" + ev + "\",\"line\":\"" + ln + "\"}";
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 4;
            yield return req.SendWebRequest();
        }
    }

    private static string AsrLogSnippetForQuotes(string s, int maxLen = 200)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        string t = s.Replace("\\", "\\\\").Replace("\r", " ").Replace("\n", " ").Replace("'", "\\'");
        if (t.Length > maxLen)
        {
            return t.Substring(0, maxLen) + "…";
        }

        return t;
    }

    private void SaveOutgoingAudioChunkForDebug(byte[] float32Bytes, float chunkRms, int sampleCount)
    {
        if (!_saveOutgoingAsrAudio || float32Bytes == null || float32Bytes.Length == 0)
            return;

        try
        {
            if (string.IsNullOrEmpty(_asrAudioDumpDir))
                _asrAudioDumpDir = Path.Combine(Application.persistentDataPath, "asr_sent_audio");
            Directory.CreateDirectory(_asrAudioDumpDir);

            int nextSeq = System.Threading.Interlocked.Increment(ref _savedAudioSeq);
            string fileName = $"asr_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{nextSeq:D5}_{_forcedLanguage}.wav";
            string filePath = Path.Combine(_asrAudioDumpDir, fileName);
            byte[] wavBytes = Float32ToWavPcm16(float32Bytes, _sampleRate);
            File.WriteAllBytes(filePath, wavBytes ?? Array.Empty<byte>());

            if (!_loggedAsrAudioDumpDir)
            {
                _loggedAsrAudioDumpDir = true;
                EmitStatus("ASR outgoing audio dump dir: " + _asrAudioDumpDir);
            }

            int maxKeep = Mathf.Max(20, _maxSavedOutgoingAsrAudioFiles);
            string[] wavFiles = Directory.GetFiles(_asrAudioDumpDir, "*.wav");
            if (wavFiles.Length > maxKeep)
            {
                Array.Sort(wavFiles, StringComparer.OrdinalIgnoreCase);
                int deleteCount = wavFiles.Length - maxKeep;
                for (int i = 0; i < deleteCount; i++)
                {
                    try { File.Delete(wavFiles[i]); } catch { /* ignore cleanup errors */ }
                }
            }

            if (_logPerChunkTelemetry)
            {
                float chunkSeconds = _sampleRate > 0 ? (float)sampleCount / _sampleRate : 0f;
                EmitStatus($"Saved outgoing ASR chunk: {fileName} chunk_s={chunkSeconds:F2} rms={chunkRms:F5}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ASR] Failed to save outgoing ASR chunk wav: " + ex.Message);
        }
    }

    /// <summary>Logs §5-style fields when debug file logging is on (text length, server timings, local RMS, backlog).</summary>
    private void EmitAsrResponseDiagnostics(string requestId, string rawBody, string parsedText, int captureGenAtSend, int payloadBodyBytes)
    {
        TryReadJsonNumberAfterKey(rawBody, "infer_ms", out double inferMs);
        TryReadJsonNumberAfterKey(rawBody, "total_ms", out double totalMs);
        TryReadJsonNumberAfterKey(rawBody, "chunk_duration_s", out double chunkDurS);
        int pendingQ = _pendingFloat32Queue.Count;
        string tl = (parsedText ?? string.Empty).Length.ToString();
        string ib = inferMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tb = totalMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string cb = chunkDurS.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string rms = _lastPostedChunkRms.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        EmitStatus(
            $"{requestId} diag text_len={tl} infer_ms={ib} total_ms={tb} chunk_duration_s={cb} payload_bytes={payloadBodyBytes} " +
            $"posted_rms={rms} posted_samples={_lastPostedChunkSamples} pending_q={pendingQ} capture_gen_send={captureGenAtSend}");
    }

    private static bool TryReadJsonNumberAfterKey(string raw, string key, out double value)
    {
        value = 0;
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(key)) return false;
        Match m = Regex.Match(
            raw,
            "\"" + Regex.Escape(key) + "\"\\s*:\\s*([-+]?(?:\\d*\\.?\\d+)(?:[eE][-+]?\\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) return false;
        return double.TryParse(
            m.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>Optional GET /health per API doc (same host as POST /audio).</summary>
    private static string DeriveHealthUrl(string audioPostUrl)
    {
        if (string.IsNullOrWhiteSpace(audioPostUrl)) return null;
        string u = audioPostUrl.TrimEnd('/');
        if (u.EndsWith("/audio", StringComparison.OrdinalIgnoreCase))
            return u.Substring(0, u.Length - 6) + "/health";
        int i = u.LastIndexOf('/');
        return i > 8 ? u.Substring(0, i) + "/health" : u + "/health";
    }

    private IEnumerator CoCheckHealthEndpoint()
    {
        string healthUrl = DeriveHealthUrl(_asrApiUrl);
        if (string.IsNullOrEmpty(healthUrl))
            yield break;

        EmitStatus("GET " + healthUrl);
        using (UnityWebRequest req = UnityWebRequest.Get(healthUrl))
        {
            req.timeout = 25;
            req.SetRequestHeader("User-Agent", "Unity-HoloLens-ASR/1.0");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                EmitStatus("/health failed: " + req.error + " code=" + req.responseCode);
            }
            else
            {
                string body = req.downloadHandler?.text ?? "";
                string shortBody = body.Length > 140 ? body.Substring(0, 140) + "…" : body;
                EmitStatus("/health OK: " + shortBody);
            }
        }
    }

    /// <summary>Override the inspector URL at runtime (e.g. Wizard of Oz primary ASR).</summary>
    public void SetApiUrl(string url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            _asrApiUrl = url.Trim();
            _warnedNonAudioEndpoint = false;
        }
    }

    public void SetForcedLanguage(string forcedLanguage)
    {
        string next = (forcedLanguage ?? string.Empty).Trim().ToLowerInvariant();
        if (next == "auto")
        {
            next = string.Empty;
        }

        if (!string.Equals(_forcedLanguage, next, StringComparison.OrdinalIgnoreCase))
        {
            ClearTranscriptContext("language_mode_change");
        }

        _forcedLanguage = next;
    }

    /// <summary>
    /// Runtime tuning for different ASR backends (e.g. ENG vs ITA). Values are clamped to safe ranges.
    /// </summary>
    public void SetRuntimeTuning(
        float chunkSeconds,
        float sendWindowSeconds,
        float minChunkRmsToSend,
        float adaptiveGainTargetRms,
        float adaptiveGainMax)
    {
        _chunkSeconds = Mathf.Clamp(chunkSeconds, 2f, 3f);
        _sendWindowSeconds = Mathf.Clamp(sendWindowSeconds, _chunkSeconds + 0.2f, 6.0f);
        _chunkOverlapSeconds = Mathf.Clamp(_chunkOverlapSeconds, 0.25f, 0.35f);
        _fixedSendIntervalSeconds = Mathf.Clamp(chunkSeconds, 2f, 2.8f);
        _fixedMaxChunkSeconds = Mathf.Clamp(chunkSeconds, _fixedSendIntervalSeconds, 3f);
        _fixedOverlapSeconds = Mathf.Clamp(_chunkOverlapSeconds, 0.25f, 0.35f);
        _minChunkRmsToSend = Mathf.Clamp(minChunkRmsToSend, 0f, 0.03f);
        _adaptiveGainTargetRms = Mathf.Clamp(adaptiveGainTargetRms, 0.01f, 0.2f);
        _adaptiveGainMax = Mathf.Clamp(adaptiveGainMax, 1f, 24f);
    }

    /// <summary>Clears the current-utterance transcript (not full history). Does not reset chunk sequence counters.</summary>
    public void ClearTranscriptContext(string reason = "unspecified")
    {
        int cid = Mathf.Max(0, LatestAcceptedChunkId);
        string r = TelemetrySanitizeToken(reason);
        LogClientTelemetry($"[client] CLEAR reason={CompactClearReason(r)}", "CLEAR", cid);
        _currentUtteranceSb.Length = 0;
        LastEnglishFromApi = "";
        _lastNonEmptyDisplayRealtime = -999f;
        _utteranceAwaitingContinuation = false;
        ResetUtteranceBoundaryTracking();
        OnUtteranceContextCleared?.Invoke(reason);
    }

    public void StartAsr()
    {
        if (IsRunning) return;
        if (Microphone.devices == null || Microphone.devices.Length == 0)
        {
            if (_writeAsrDebugFile)
            {
                if (string.IsNullOrEmpty(_logFilePath))
                    _logFilePath = Path.Combine(Application.persistentDataPath, "asr_debug.log");
                EmitStatus("No microphone devices. Log: " + _logFilePath);
            }
            else
            {
                EmitStatus("No microphone devices.");
            }
            Debug.LogError("[ASR] No microphone device available.");
            OnMicrophoneNotReady?.Invoke();
            return;
        }

        _micDevice = SelectMicrophoneDevice();
        _chunksUploaded = 0;
        _loggedFirstChunk = false;
        if (_writeAsrDebugFile)
        {
            if (string.IsNullOrEmpty(_logFilePath))
                _logFilePath = Path.Combine(Application.persistentDataPath, "asr_debug.log");
            EmitStatus("Debug file logging enabled: " + _logFilePath);
        }

        EmitStatus($"Microphone.Start device='{_micDevice}' requestHz={_sampleRate} clipLen={_clipLengthSeconds}s");
        _micClip = Microphone.Start(_micDevice, true, _clipLengthSeconds, _sampleRate);
        if (_micClip == null)
        {
            EmitStatus("Microphone.Start returned null — enable Microphone capability + OS privacy.");
            Debug.LogError("[ASR] Microphone.Start returned null — check UWP Microphone capability and privacy settings.");
            OnMicrophoneNotReady?.Invoke();
            return;
        }

        _lastMicSample = 0;
        _telemetrySessionId = Guid.NewGuid().ToString("N");
        _requestSeq = 0;
        _latestResponseSeqApplied = 0;
        _lastBufferedMsForTelemetry = -99999f;
        _lastBufferInFlightForTelemetry = -1;
        _lastAsrBufferLogRealtime = -999f;
        _lastMicTelemetryRealtime = -999f;
        ClearTranscriptContext("start_capture");
        _emptyHttp200Streak = 0;
        _chunkSeconds = Mathf.Clamp(_chunkSeconds, 2f, 3f);
        _sendWindowSeconds = Mathf.Clamp(_sendWindowSeconds, _chunkSeconds + 0.2f, 6.0f);
        _chunkOverlapSeconds = Mathf.Clamp(_chunkOverlapSeconds, 0.25f, 0.35f);
        _forcedLanguageHardCapSeconds = Mathf.Clamp(_forcedLanguageHardCapSeconds, 2.5f, 4.0f);
        _fixedSendIntervalSeconds = Mathf.Clamp(_fixedSendIntervalSeconds, 2f, 2.8f);
        _fixedMaxChunkSeconds = Mathf.Clamp(_fixedMaxChunkSeconds, _fixedSendIntervalSeconds, 3f);
        _fixedOverlapSeconds = Mathf.Clamp(_fixedOverlapSeconds, 0.25f, 0.35f);
        _vadMinNoiseFloor = Mathf.Clamp(_vadMinNoiseFloor, 0.001f, 0.05f);
        _vadSpeechMultiplier = Mathf.Clamp(_vadSpeechMultiplier, 1.2f, 6.0f);
        _highPassCutoffHz = Mathf.Clamp(_highPassCutoffHz, 60f, 180f);
        _minVoicedFrameRatioToSend = Mathf.Clamp01(_minVoicedFrameRatioToSend);
        _noiseFloorWarmupSeconds = Mathf.Clamp(_noiseFloorWarmupSeconds, 0f, 4f);
        _noiseRms = 0.01f;
        _lastStrongSentenceRealtime = -999f;
        _asrStartedAtRealtime = Time.realtimeSinceStartup;
        CurrentMicLevel = 0f;
        IsRunning = true;

        StartCoroutine(CoCheckHealthEndpoint());

        if (_captureCoroutine != null) StopCoroutine(_captureCoroutine);
        _captureCoroutine = StartCoroutine(CaptureAndUploadLoop());
        EmitStatus("Capture waiting for IsRecording…");
    }

    private string SelectMicrophoneDevice()
    {
        string[] devices = Microphone.devices ?? Array.Empty<string>();
        if (devices.Length == 0) return null;

        var listed = new StringBuilder();
        for (int i = 0; i < devices.Length; i++)
        {
            if (i > 0) listed.Append(" | ");
            listed.Append(i).Append(":").Append(devices[i]);
        }
        EmitStatus("Detected microphone devices: " + listed);

        string needle = (_preferredMicNameContains ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(needle))
        {
            for (int i = 0; i < devices.Length; i++)
            {
                string d = devices[i] ?? string.Empty;
                if (d.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    EmitStatus($"Using preferred microphone match '{needle}': {d}");
                    return d;
                }
            }
            EmitStatus($"Preferred microphone '{needle}' not found. Falling back to default index 0.");
        }

        EmitStatus("Using default microphone index 0: " + devices[0]);
        return devices[0];
    }

    public void StopAsr()
    {
        if (!IsRunning) return;
        _captureGeneration++;
        IsRunning = false;

        if (_captureCoroutine != null)
        {
            StopCoroutine(_captureCoroutine);
            _captureCoroutine = null;
        }

        if (!string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(_micDevice))
        {
            Microphone.End(_micDevice);
        }

        _micClip = null;
        _micDevice = null;
        _lastMicSample = 0;
        _requestInFlight = false;
        _pendingFloat32Queue.Clear();
        _lastBufferedDeltaSamples = 0;
        _lastBufferedClipHz = 0;
        CurrentMicLevel = 0f;
        OnMicLevelUpdated?.Invoke(CurrentMicLevel);
        EmitStatus("Microphone capture stopped.");
    }

    private IEnumerator CaptureAndUploadLoop()
    {
        float waitMic = Time.realtimeSinceStartup;
        while (IsRunning &&
               (_micClip == null || string.IsNullOrEmpty(_micDevice) || !Microphone.IsRecording(_micDevice)))
        {
            if (Time.realtimeSinceStartup - waitMic > 8f)
            {
                EmitStatus(
                    "Timeout: mic not recording in 8s. Privacy→Microphone. See " + _logFilePath);
                Debug.LogError(
                    "[ASR] Timeout: Microphone never entered recording state (8s). Device='" + _micDevice + "'.");
                _captureGeneration++;
                IsRunning = false;
                if (!string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(_micDevice))
                {
                    Microphone.End(_micDevice);
                }

                _micClip = null;
                _micDevice = null;
                _requestInFlight = false;
                _pendingFloat32Queue.Clear();
                _captureCoroutine = null;
                OnMicrophoneNotReady?.Invoke();
                yield break;
            }

            yield return null;
        }

        int clipHz = _micClip.frequency > 0 ? _micClip.frequency : _sampleRate;
        EmitStatus($"Mic recording OK. clipHz={clipHz} → API float32 @{_sampleRate}Hz (matches X-Sample-Rate).");
        OnMicrophoneReady?.Invoke();

        while (IsRunning)
        {
            if (_micClip == null || string.IsNullOrEmpty(_micDevice) || !Microphone.IsRecording(_micDevice))
            {
                Debug.LogWarning("[ASR] Mic stopped mid-session.");
                EmitStatus("send-loop skip: microphone not recording or clip/device missing.");
                yield return null;
                continue;
            }

            clipHz = _micClip.frequency > 0 ? _micClip.frequency : _sampleRate;

            int currentPos = Microphone.GetPosition(_micDevice);
            if (currentPos < 0)
            {
                EmitStatus("send-loop skip: Microphone.GetPosition returned < 0.");
                yield return null;
                continue;
            }

            int totalSamples = _micClip.samples;
            int deltaSamples = currentPos - _lastMicSample;
            if (deltaSamples < 0) deltaSamples += totalSamples;

            _lastBufferedDeltaSamples = deltaSamples;
            _lastBufferedClipHz = clipHz;

            float bufferedMsForLog = (deltaSamples / (float)clipHz) * 1000f;
            UpdateMicLevel(currentPos, totalSamples);
            float nowRealtime = Time.realtimeSinceStartup;
            TickUtteranceBoundaryMicQuietArm(nowRealtime);
            TrySentenceGapClearOnSpeechResume(chunkPassedSendGate: false);
            MaybeLogBufferTelemetry(bufferedMsForLog, IsApiRequestInFlight ? 1 : 0);
            MaybeLogMicTelemetry(currentPos, totalSamples);

            float chunkSec = Mathf.Clamp(Mathf.Min(_fixedSendIntervalSeconds, _fixedMaxChunkSeconds), 2f, 3f);
            int chunkSamples = Mathf.RoundToInt(clipHz * chunkSec);
            int overlapSamples = Mathf.RoundToInt(clipHz * Mathf.Clamp(_fixedOverlapSeconds, 0.25f, 0.35f));
            overlapSamples = Mathf.Clamp(overlapSamples, 0, Mathf.Max(0, chunkSamples - 1));

            if (deltaSamples < chunkSamples)
            {
                yield return null;
                continue;
            }

            float[] chunk = ExtractSamples(_lastMicSample, chunkSamples, totalSamples);
            int advance = chunkSamples - overlapSamples;
            int nextLast = _lastMicSample + advance;
            while (nextLast >= totalSamples)
            {
                nextLast -= totalSamples;
            }

            if (clipHz != _sampleRate)
            {
                if (!_loggedResample)
                {
                    _loggedResample = true;
                    Debug.Log($"[ASR] Resampling {clipHz}Hz → {_sampleRate}Hz for API (X-Sample-Rate must match body).");
                }

                chunk = ResampleLinear(chunk, clipHz, _sampleRate);
            }

            if (_enableHighPassFilter)
            {
                ApplyHighPassFilter(chunk, _sampleRate, Mathf.Clamp(_highPassCutoffHz, 60f, 180f));
            }

            ApplyAdaptiveGainForCurrentLanguage(chunk);

            float chunkRms = ChunkRms(chunk);
            float chunkPeakAbs = ChunkPeakAbs(chunk);
            float speechThreshold = Mathf.Max(EffectiveVadNoiseFloor(), _noiseRms * EffectiveVadSpeechMultiplier());
            float speechFrameRatio = EstimateSpeechFrameRatio(chunk, speechThreshold);
            float voicedFrameRatio = EstimateVoicedFrameRatio(chunk, _sampleRate, 85f, 300f);
            float silenceAmpGate = Mathf.Max(0.008f, EffectiveVadNoiseFloor());
            float silenceRatio = SilenceSampleRatio(chunk, silenceAmpGate);
            float chunkDurMsApi = _sampleRate > 0 ? (chunk.Length / (float)_sampleRate) * 1000f : 0f;

            bool shouldDrop = ComputeShouldDropChunk(chunkRms, chunkPeakAbs, silenceRatio);

            if (shouldDrop)
            {
                LogClientTelemetry(
                    $"[client] DROP_SILENCE silence_ratio={silenceRatio:F2} rms={chunkRms:F3} peak={chunkPeakAbs:F3}",
                    "DROP_SILENCE",
                    Mathf.Max(0, LatestAcceptedChunkId));
                _lastMicSample = nextLast;
                yield return null;
                continue;
            }

            TrySentenceGapClearOnSpeechResume(chunkPassedSendGate: true);

            if (chunkRms >= AsrSendStrongRmsLog)
            {
                LogClientTelemetry(
                    $"[client] SEND rms={chunkRms:F3} silence_ratio={silenceRatio:F2} peak={chunkPeakAbs:F3}",
                    "SEND",
                    Mathf.Max(0, LatestAcceptedChunkId));
            }

            float speechMsTelemetry = (1f - Mathf.Clamp01(silenceRatio)) * chunkDurMsApi;
            _telemetrySendSpeechMs = speechMsTelemetry;
            _telemetrySendSilenceRatio = silenceRatio;

            byte[] float32Bytes = Float32ToBytes(chunk);
            SaveOutgoingAudioChunkForDebug(float32Bytes, chunkRms, chunk.Length);

            _lastMicSample = nextLast;
            _lastPostedChunkRms = chunkRms;
            _lastPostedChunkSamples = chunk.Length;
            _lastPostedSpeechFrameRatio = speechFrameRatio;
            _lastPostedVoicedFrameRatio = voicedFrameRatio;
            _lastSpeechActivityRealtime = Time.realtimeSinceStartup;
            QueueSend(float32Bytes, silenceRatio);
            yield return null;
        }
    }

    /// <summary>
    /// Raises quiet speech so the Space <c>preprocess_chunk</c> RMS check (ASR_MIN_RMS) does not reject the chunk as silence.
    /// Server applies 80 Hz high-pass then requires RMS ≥ ~0.007; HoloLens mics are often softer than desktop.
    /// Returns applied linear gain (1 when unchanged).
    /// </summary>
    private static float ApplyAdaptiveGain(float[] samples, float targetRms, float maxGain)
    {
        if (samples == null || samples.Length == 0)
        {
            return 1f;
        }

        if (targetRms <= 0f || maxGain < 1f)
        {
            return 1f;
        }

        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            sum += s * s;
        }

        float rms = (float)System.Math.Sqrt(sum / samples.Length);
        const float silence = 1e-7f;
        if (rms < silence || rms >= targetRms)
        {
            return 1f;
        }

        float g = Mathf.Min(maxGain, targetRms / rms);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Mathf.Clamp(samples[i] * g, -1f, 1f);
        }

        return g;
    }

    private static float ChunkRms(float[] samples)
    {
        if (samples == null || samples.Length == 0) return 0f;
        double sum = 0d;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static float ChunkPeakAbs(float[] samples)
    {
        if (samples == null || samples.Length == 0)
        {
            return 0f;
        }

        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
        }

        return peak;
    }

    private static float EstimateSpeechFrameRatio(float[] samples, float threshold)
    {
        if (samples == null || samples.Length == 0) return 0f;
        float t = Mathf.Max(1e-5f, threshold);
        int hits = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (Mathf.Abs(samples[i]) >= t) hits++;
        }

        return hits / (float)samples.Length;
    }

    private static void ApplyHighPassFilter(float[] samples, int sampleRate, float cutoffHz)
    {
        if (samples == null || samples.Length < 2 || sampleRate <= 0 || cutoffHz <= 0f) return;
        float dt = 1f / sampleRate;
        float rc = 1f / (2f * Mathf.PI * cutoffHz);
        float alpha = rc / (rc + dt);
        float prevY = samples[0];
        float prevX = samples[0];
        for (int i = 1; i < samples.Length; i++)
        {
            float x = samples[i];
            float y = alpha * (prevY + x - prevX);
            samples[i] = y;
            prevY = y;
            prevX = x;
        }
        samples[0] = 0f;
    }

    private static float EstimateVoicedFrameRatio(float[] samples, int sampleRate, float minPitchHz, float maxPitchHz)
    {
        if (samples == null || samples.Length == 0 || sampleRate <= 0) return 0f;
        int frameSize = Mathf.Clamp(sampleRate / 40, 256, 1024); // 25ms
        int hop = Mathf.Max(1, frameSize / 2);
        int minLag = Mathf.Clamp(Mathf.RoundToInt(sampleRate / Mathf.Max(1f, maxPitchHz)), 1, frameSize - 2);
        int maxLag = Mathf.Clamp(Mathf.RoundToInt(sampleRate / Mathf.Max(1f, minPitchHz)), minLag + 1, frameSize - 1);
        if (maxLag <= minLag) return 0f;

        int voiced = 0;
        int total = 0;
        for (int start = 0; start + frameSize < samples.Length; start += hop)
        {
            double energy = 0d;
            for (int i = 0; i < frameSize; i++)
            {
                float s = samples[start + i];
                energy += s * s;
            }
            if (energy < 1e-7d) continue;

            double bestCorr = 0d;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double corr = 0d;
                for (int i = lag; i < frameSize; i++)
                {
                    corr += samples[start + i] * samples[start + i - lag];
                }
                if (corr > bestCorr) bestCorr = corr;
            }

            total++;
            if (bestCorr / energy >= 0.12d) voiced++;
        }

        if (total <= 0) return 0f;
        return voiced / (float)total;
    }

    /// <summary>Linear resample so POST body matches <see cref="_sampleRate"/> and X-Sample-Rate header.</summary>
    private static float[] ResampleLinear(float[] input, int inputRate, int outputRate)
    {
        if (input == null || input.Length == 0) return input;
        if (inputRate == outputRate) return input;
        if (inputRate <= 0 || outputRate <= 0) return input;

        double ratio = (double)inputRate / outputRate;
        int outLen = Mathf.Max(1, (int)System.Math.Floor(input.Length / ratio));
        float[] output = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double srcIndex = i * ratio;
            int i0 = (int)System.Math.Floor(srcIndex);
            int i1 = Mathf.Min(i0 + 1, input.Length - 1);
            float t = (float)(srcIndex - i0);
            output[i] = Mathf.Lerp(input[i0], input[i1], t);
        }

        return output;
    }

    private static float SilenceSampleRatio(float[] samples, float silenceThreshold)
    {
        if (samples == null || samples.Length == 0 || silenceThreshold <= 0f)
        {
            return samples == null || samples.Length == 0 ? 1f : 0f;
        }

        int silent = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (Mathf.Abs(samples[i]) < silenceThreshold)
            {
                silent++;
            }
        }

        return silent / (float)samples.Length;
    }

    private void QueueSend(byte[] float32Bytes, float silenceRatioForChunk)
    {
        if (float32Bytes == null || float32Bytes.Length == 0)
        {
            EmitStatus("send-loop early return: QueueSend received empty payload.");
            return;
        }
        if (!_loggedFirstChunk)
        {
            _loggedFirstChunk = true;
            int samples = float32Bytes.Length / 4;
            EmitStatus($"POST /audio first chunk: {float32Bytes.Length} bytes ({samples} float32 LE mono)");
        }

        if (_requestInFlight)
        {
            EnqueuePendingFloat32(float32Bytes, _telemetrySendSpeechMs, silenceRatioForChunk);
            return;
        }

        StartCoroutine(SendChunkToApi(float32Bytes, silenceRatioForChunk));
    }

    private void EnqueuePendingFloat32(byte[] float32Bytes, float speechMsTelemetry, float silenceRatioTelemetry)
    {
        if (float32Bytes == null || float32Bytes.Length == 0)
        {
            EmitStatus("send-loop early return: enqueue skipped empty payload.");
            return;
        }

        int warnEvery = Mathf.Max(512, _maxPendingAudioChunks);
        if (_pendingFloat32Queue.Count >= warnEvery && (_pendingFloat32Queue.Count % warnEvery) == 0)
        {
            EmitStatus($"WARNING: pending ASR queue depth={_pendingFloat32Queue.Count} (no drops; catch-up may lag).");
        }

        _pendingFloat32Queue.Enqueue(new PendingChunk
        {
            Bytes = float32Bytes,
            SpeechMsTelemetry = speechMsTelemetry,
            SilenceRatioTelemetry = silenceRatioTelemetry,
        });
    }

    /// <summary>Takes the next queued body in order.</summary>
    private PendingChunk? TryTakePendingPayload()
    {
        if (_pendingFloat32Queue.Count == 0) return null;
        return _pendingFloat32Queue.Dequeue();
    }

    private void UpdateMicLevel(int currentPos, int totalSamples)
    {
        const int window = 512;
        float[] tmp = new float[window];
        int start = currentPos - window;
        if (start < 0) start += totalSamples;

        if (start + window <= totalSamples)
        {
            _micClip.GetData(tmp, start);
        }
        else
        {
            int first = totalSamples - start;
            float[] a = new float[first];
            float[] b = new float[window - first];
            _micClip.GetData(a, start);
            _micClip.GetData(b, 0);
            Array.Copy(a, 0, tmp, 0, a.Length);
            Array.Copy(b, 0, tmp, a.Length, b.Length);
        }

        float sum = 0f;
        for (int i = 0; i < tmp.Length; i++)
        {
            float s = tmp[i];
            sum += s * s;
        }

        float peakAbs = 0f;
        for (int i = 0; i < tmp.Length; i++)
        {
            peakAbs = Mathf.Max(peakAbs, Mathf.Abs(tmp[i]));
        }

        float rms = Mathf.Sqrt(sum / tmp.Length);
        _micWindowPeakAbs = peakAbs;
        _lastMicRms = rms;
        CurrentMicLevel = Mathf.Clamp01(rms * 7f);
        OnMicLevelUpdated?.Invoke(CurrentMicLevel);
    }

    private float[] ExtractSamples(int start, int count, int totalSamples)
    {
        float[] data = new float[count];
        if (start + count <= totalSamples)
        {
            _micClip.GetData(data, start);
            return data;
        }

        int first = totalSamples - start;
        float[] a = new float[first];
        float[] b = new float[count - first];
        _micClip.GetData(a, start);
        _micClip.GetData(b, 0);
        Array.Copy(a, 0, data, 0, a.Length);
        Array.Copy(b, 0, data, a.Length, b.Length);
        return data;
    }

    private float[] ExtractLatestSamples(int endPos, int count, int totalSamples)
    {
        if (count <= 0) return Array.Empty<float>();
        if (count > totalSamples) count = totalSamples;
        int start = endPos - count;
        if (start < 0) start += totalSamples;
        return ExtractSamples(start, count, totalSamples);
    }

    private static float ChunkRmsFromFloat32Bytes(byte[] float32Bytes)
    {
        if (float32Bytes == null || float32Bytes.Length < 4) return 0f;
        int samples = float32Bytes.Length / 4;
        if (samples <= 0) return 0f;
        double sum = 0d;
        for (int i = 0; i < samples; i++)
        {
            float s = BitConverter.ToSingle(float32Bytes, i * 4);
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples);
    }

    private static float ChunkPeakAbsFromFloat32Bytes(byte[] float32Bytes)
    {
        if (float32Bytes == null || float32Bytes.Length < 4)
        {
            return 0f;
        }

        int samples = float32Bytes.Length / 4;
        float peak = 0f;
        for (int i = 0; i < samples; i++)
        {
            peak = Mathf.Max(peak, Mathf.Abs(BitConverter.ToSingle(float32Bytes, i * 4)));
        }

        return peak;
    }

    private IEnumerator SendChunkToApi(byte[] float32Bytes, float chunkSilenceRatio)
    {
        _requestInFlight = true;
        int captureGenAtSend = _captureGeneration;
        int requestSeq = System.Threading.Interlocked.Increment(ref _requestSeq);
        string requestId = "asr-" + requestSeq.ToString("D5");
        _requestAttemptCount++;
        int payloadSamples = float32Bytes != null ? (float32Bytes.Length / 4) : 0;
        float payloadChunkSeconds = _sampleRate > 0 ? (float)payloadSamples / _sampleRate : 0f;
        float payloadRms = ChunkRmsFromFloat32Bytes(float32Bytes);
        string mode = IsItalianStrictNoiseGuard ? "italian" : "english_or_auto";
        bool micRecording = !string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(_micDevice);
        EmitStatus(
            $"[ASR ENTER SEND] mode={mode} forcedLanguage={_forcedLanguage} recording={micRecording} " +
            $"samples={payloadSamples} rms={payloadRms:F5}");

        try
        {
            if (string.IsNullOrWhiteSpace(_asrApiUrl))
            {
                EmitStatus(requestId + " early return: ASR API URL is empty.");
                Debug.LogWarning("[ASR] " + requestId + " no API URL configured; skipping upload.");
                _requestFailureCount++;
                OnApiRequestFinished?.Invoke(false);
                yield break;
            }
            bool isGradioTranscribeCall =
                _asrApiUrl.IndexOf("/gradio_api/call/transcribe", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isGradioTranscribeCall
                && !_warnedNonAudioEndpoint
                && _asrApiUrl.IndexOf("/audio", StringComparison.OrdinalIgnoreCase) < 0)
            {
                _warnedNonAudioEndpoint = true;
                Debug.LogWarning(
                    "[ASR] API URL does not include /audio. This client sends raw float32 PCM and expects a POST /audio endpoint.");
            }

            if (float32Bytes.Length % 4 != 0)
            {
                EmitStatus(requestId + " early return: payload length not multiple of 4 bytes.");
                EmitStatus(requestId + " invalid body: length not multiple of 4 (float32).");
                _requestFailureCount++;
                OnApiRequestFinished?.Invoke(false);
                yield break;
            }

            if (isGradioTranscribeCall)
            {
                yield return SendChunkToItalianGradioApi(requestId, float32Bytes, captureGenAtSend, requestSeq, chunkSilenceRatio);
                yield break;
            }

            using (UnityWebRequest req = new UnityWebRequest(_asrApiUrl, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(float32Bytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/octet-stream");
                req.SetRequestHeader("X-Sample-Rate", _sampleRate.ToString());
                string forcedLang = IsItalianStrictNoiseGuard
                    ? "italian"
                    : (string.IsNullOrWhiteSpace(_forcedLanguage) ? "english" : _forcedLanguage);
                req.SetRequestHeader("X-Forced-Language", forcedLang);
                req.SetRequestHeader("User-Agent", "Unity-HoloLens-ASR/1.0");
                req.SetRequestHeader("X-Chunk-Id", requestSeq.ToString(System.Globalization.CultureInfo.InvariantCulture));
                req.timeout = Mathf.Clamp(_requestTimeoutSeconds, 2, 180);
                float payloadPeakAudio = ChunkPeakAbsFromFloat32Bytes(float32Bytes);
                LogSendTelemetry(requestSeq, forcedLang, payloadChunkSeconds, payloadRms, payloadPeakAudio);
                EmitStatus(
                    $"[ASR POST ATTEMPT] chunk_id={requestSeq} url={_asrApiUrl} forcedLanguage={forcedLang} samples={payloadSamples} " +
                    $"sr={_sampleRate} chunk_s={payloadChunkSeconds:F2} rms={payloadRms:F5}");
                if (_logPerChunkTelemetry)
                {
                    float chunkSeconds = _sampleRate > 0 ? (float)_lastPostedChunkSamples / _sampleRate : 0f;
                    EmitStatus(
                        $"[ASR SEND ATTEMPT] id={requestId} lang={forcedLang} sr={_sampleRate} samples={_lastPostedChunkSamples} " +
                        $"chunk_s={chunkSeconds:F2} rms={_lastPostedChunkRms:F5} speech_ratio={_lastPostedSpeechFrameRatio:F3} " +
                        $"voiced_ratio={_lastPostedVoicedFrameRatio:F3} bytes={float32Bytes.Length} url={_asrApiUrl}");
                }
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string errBody = req.downloadHandler?.text ?? "";
                    if (_logPerChunkTelemetry)
                    {
                        string bodyPreview = errBody.Length > 220 ? errBody.Substring(0, 220) + "…" : errBody;
                        EmitStatus(
                            $"[ASR RESPONSE] id={requestId} status={req.responseCode} error={req.error} body={bodyPreview}");
                    }
                    if (req.responseCode == 400 && !string.IsNullOrEmpty(errBody))
                        EmitStatus(requestId + " HTTP 400: " + (errBody.Length > 200 ? errBody.Substring(0, 200) + "…" : errBody));
                    else
                        EmitStatus(requestId + " POST /audio failed: " + req.error + " HTTP " + req.responseCode);
                    Debug.LogWarning("[ASR] " + requestId + " API HTTP failed: " + req.error + " code=" + req.responseCode);
                    _requestFailureCount++;
                    OnApiRequestFinished?.Invoke(false);
                }
                else
                {
                    string rawBody = req.downloadHandler?.text ?? string.Empty;
                    if (_logPerChunkTelemetry)
                    {
                        string bodyPreview = rawBody.Length > 220 ? rawBody.Substring(0, 220) + "…" : rawBody;
                        EmitStatus(
                            $"[ASR RESPONSE] id={requestId} status={req.responseCode} error= body={bodyPreview}");
                    }
                    if (captureGenAtSend != _captureGeneration)
                    {
                        EmitStatus(
                            $"{requestId} ignoring HTTP 200 (stale session; capture_gen_send={captureGenAtSend} now={_captureGeneration}).");
                        OnApiRequestFinished?.Invoke(true);
                        yield break;
                    }

                    _chunksUploaded++;
                    _requestSuccessCount++;
                    if (_chunksUploaded <= 3 || _chunksUploaded % 20 == 0)
                    {
                        string preview = rawBody.Length > 160 ? rawBody.Substring(0, 160) + "…" : rawBody;
                        EmitStatus($"{requestId} HTTP 200 chunk #{_chunksUploaded} resp: {preview}");
                    }

                    LastEnglishFromApi = ExtractEnglishSidecar(rawBody);

                    string text = ExtractText(rawBody);
                    text = CleanHallucinatedPrefix(text);
                    text = RemoveImmediateRepeatedWords(text);
                    text = RemoveRepeatedTailPhrases(text);
                    if (_writeAsrDebugFile)
                    {
                        EmitAsrResponseDiagnostics(requestId, rawBody, text, captureGenAtSend, float32Bytes.Length);
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        LogClientTelemetry($"[client] RAW text=''", "RAW", requestSeq);
                        EmitStatus($"{requestId} empty response ignored (no append).");
                        _requestParseEmptyCount++;
                        _emptyHttp200Streak++;
                        int threshold = Mathf.Max(3, _emptyHttp200StreakBeforeFallback);
                        if (_emptyHttp200Streak >= threshold)
                        {
                            _emptyHttp200Streak = 0;
                            Debug.LogWarning(
                                "[ASR] Many consecutive HTTP 200 responses with empty parsed text — check JSON shape vs ExtractText, or use dictation fallback.");
                            OnRepeatedEmptySuccessfulApiResponses?.Invoke();
                        }

                        // API contract: HTTP 200 + {"text":""} is valid when chunk is silent/too short per server.
                        if (Time.realtimeSinceStartup - _lastEmptyTranscriptLogTime >= 5f)
                        {
                            _lastEmptyTranscriptLogTime = Time.realtimeSinceStartup;
                            Debug.Log(
                                "[ASR] Empty transcript for this chunk (valid per API if quiet/short). " +
                                "If this repeats while speaking, check mic level and clipHz→16kHz resampling.");
                        }

                        if (_chunksUploaded <= 5 && rawBody.Length > 8)
                        {
                            string dbg = rawBody.Length > 500 ? rawBody.Substring(0, 500) + "…" : rawBody;
                            Debug.LogWarning("[ASR] Parse produced empty text; response sample: " + dbg);
                        }

                        OnApiRequestFinished?.Invoke(true);
                    }
                    else
                    {
                        LogClientTelemetry(
                            $"[client] RAW text='{AsrLogSnippetForQuotes(text)}'",
                            "RAW",
                            requestSeq);

                        _latestResponseSeqApplied = Mathf.Max(_latestResponseSeqApplied, requestSeq);
                        _emptyHttp200Streak = 0;
                        if (LooksLikeMarginalNoiseCaption(text, IsItalianStrictNoiseGuard))
                        {
                            string cap = _currentUtteranceSb.ToString();
                            LogClientTelemetry($"[client] DISPLAY text='{AsrLogSnippetForQuotes(cap)}'", "DISPLAY", requestSeq);
                            EmitStatus($"{requestId} skipped marginal decode (noise-shaped caption).");
                            OnApiRequestFinished?.Invoke(true);
                        }
                        else if (LooksLikeSilenceHallucinationCaption(text, _lastPostedChunkRms, IsItalianStrictNoiseGuard))
                        {
                            string cap = _currentUtteranceSb.ToString();
                            LogClientTelemetry($"[client] DISPLAY text='{AsrLogSnippetForQuotes(cap)}'", "DISPLAY", requestSeq);
                            EmitStatus($"{requestId} skipped low-energy ghost caption.");
                            OnApiRequestFinished?.Invoke(true);
                        }
                        else if (ShouldIgnoreOneWordTailJunk(_currentUtteranceSb.ToString(), text, chunkSilenceRatio))
                        {
                            string cap = _currentUtteranceSb.ToString();
                            LogClientTelemetry($"[client] DISPLAY text='{AsrLogSnippetForQuotes(cap)}'", "DISPLAY", requestSeq);
                            OnApiRequestFinished?.Invoke(true);
                        }
                        else
                        {
                            const bool isFinalResult = false;
                            string next = NormalizeCase(text);
                            string prev = _currentUtteranceSb.ToString();
                            string merged = MergeOverlappingTranscript(prev, next);
                            LogClientTelemetry(
                                $"[client] MERGE old='{AsrLogSnippetForQuotes(prev)}' new='{AsrLogSnippetForQuotes(next)}' merged='{AsrLogSnippetForQuotes(merged)}'",
                                "MERGE",
                                requestSeq);
                            bool skipUi = ShouldSkipMergedTranscriptUpdate(prev, merged);
                            if (!skipUi)
                            {
                                _currentUtteranceSb.Length = 0;
                                _currentUtteranceSb.Append(merged);
                                string mergedCaption = _currentUtteranceSb.ToString();
                                OnTextUpdated?.Invoke(mergedCaption);
                                OnTextUpdatedDetailed?.Invoke(mergedCaption, isFinalResult);
                                NotifyNonEmptyCaptionDisplayed(mergedCaption);
                                _utteranceAwaitingContinuation =
                                    CaptionLooksIncompleteContinuation(mergedCaption);
                            }
                            else
                            {
                                EmitStatus($"{requestId} filter skipped update (prev={prev.Length} merged={merged.Length}).");
                            }

                            string displayCaption = _currentUtteranceSb.ToString();
                            LogClientTelemetry($"[client] DISPLAY text='{AsrLogSnippetForQuotes(displayCaption)}'", "DISPLAY", requestSeq);

                            if (text.Trim().Length >= Mathf.Max(4, _strongSentenceMinChars)
                                && _lastPostedChunkRms >= Mathf.Max(0.001f, _strongSentenceMinRms))
                            {
                                _lastStrongSentenceRealtime = Time.realtimeSinceStartup;
                            }

                            OnApiRequestFinished?.Invoke(true);
                        }
                    }
                }
            }
        }
        finally
        {
            _requestInFlight = false;
            if ((_requestAttemptCount % 20) == 0)
            {
                EmitStatus(
                    $"summary attempts={_requestAttemptCount} ok={_requestSuccessCount} fail={_requestFailureCount} emptyText={_requestParseEmptyCount} inFlight={(IsApiRequestInFlight ? 1 : 0)}");
            }

            PendingChunk? nextPending = TryTakePendingPayload();
            if (IsRunning && nextPending.HasValue && nextPending.Value.Bytes != null && nextPending.Value.Bytes.Length > 0)
            {
                PendingChunk p = nextPending.Value;
                _telemetrySendSpeechMs = p.SpeechMsTelemetry;
                _telemetrySendSilenceRatio = p.SilenceRatioTelemetry;
                StartCoroutine(SendChunkToApi(p.Bytes, p.SilenceRatioTelemetry));
            }
        }
    }

    private IEnumerator SendChunkToItalianGradioApi(
        string requestId,
        byte[] float32Bytes,
        int captureGenAtSend,
        int requestSeq,
        float chunkSilenceRatio)
    {
        int gradioSamples = float32Bytes != null ? float32Bytes.Length / 4 : 0;
        float gradioChunkS = _sampleRate > 0 ? (float)gradioSamples / _sampleRate : 0f;
        float gradioRms = ChunkRmsFromFloat32Bytes(float32Bytes);
        float gradioPeak = ChunkPeakAbsFromFloat32Bytes(float32Bytes);
        string gradioLang = IsItalianStrictNoiseGuard
            ? "italian"
            : (string.IsNullOrWhiteSpace(_forcedLanguage) ? "english" : _forcedLanguage);
        LogSendTelemetry(requestSeq, gradioLang, gradioChunkS, gradioRms, gradioPeak);

        byte[] wavBytes = Float32ToWavPcm16(float32Bytes, _sampleRate);
        string dataUrl = "data:audio/wav;base64," + Convert.ToBase64String(wavBytes ?? Array.Empty<byte>());
        string payloadPrimary = "{\"data\":[{\"name\":\"chunk.wav\",\"data\":\"" + dataUrl + "\"}]}";
        string payloadFallback = "{\"data\":[\"" + dataUrl + "\"]}";
        string callBody = "";
        bool callOk = false;

        using (UnityWebRequest callReq = new UnityWebRequest(_asrApiUrl, UnityWebRequest.kHttpVerbPOST))
        {
            callReq.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payloadPrimary));
            callReq.downloadHandler = new DownloadHandlerBuffer();
            callReq.SetRequestHeader("Content-Type", "application/json");
            callReq.SetRequestHeader("User-Agent", "Unity-HoloLens-ASR/1.0");
            callReq.SetRequestHeader("X-Chunk-Id", requestSeq.ToString(System.Globalization.CultureInfo.InvariantCulture));
            callReq.timeout = Mathf.Clamp(_requestTimeoutSeconds, 2, 180);
            yield return callReq.SendWebRequest();

            callOk = callReq.result == UnityWebRequest.Result.Success;
            callBody = callReq.downloadHandler?.text ?? "";
        }

        string eventId = callOk ? TryReadJsonStringAfterKey(callBody, "event_id") : "";
        if (!callOk || string.IsNullOrWhiteSpace(eventId))
        {
            using (UnityWebRequest callReq2 = new UnityWebRequest(_asrApiUrl, UnityWebRequest.kHttpVerbPOST))
            {
                callReq2.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payloadFallback));
                callReq2.downloadHandler = new DownloadHandlerBuffer();
                callReq2.SetRequestHeader("Content-Type", "application/json");
                callReq2.SetRequestHeader("User-Agent", "Unity-HoloLens-ASR/1.0");
                callReq2.SetRequestHeader("X-Chunk-Id", requestSeq.ToString(System.Globalization.CultureInfo.InvariantCulture));
                callReq2.timeout = Mathf.Clamp(_requestTimeoutSeconds, 2, 180);
                yield return callReq2.SendWebRequest();

                callOk = callReq2.result == UnityWebRequest.Result.Success;
                callBody = callReq2.downloadHandler?.text ?? "";
                eventId = callOk ? TryReadJsonStringAfterKey(callBody, "event_id") : "";
            }
        }

        if (!callOk || string.IsNullOrWhiteSpace(eventId))
        {
            EmitStatus(requestId + " Gradio call failed or missing event_id.");
            _requestFailureCount++;
            OnApiRequestFinished?.Invoke(false);
            yield break;
        }

        string streamUrl = _asrApiUrl.TrimEnd('/') + "/" + eventId;
        using (UnityWebRequest streamReq = UnityWebRequest.Get(streamUrl))
        {
            streamReq.timeout = Mathf.Clamp(_requestTimeoutSeconds, 2, 180);
            streamReq.SetRequestHeader("User-Agent", "Unity-HoloLens-ASR/1.0");
            yield return streamReq.SendWebRequest();

            if (streamReq.result != UnityWebRequest.Result.Success)
            {
                EmitStatus(requestId + " Gradio stream failed: " + streamReq.error + " HTTP " + streamReq.responseCode);
                _requestFailureCount++;
                OnApiRequestFinished?.Invoke(false);
                yield break;
            }

            if (captureGenAtSend != _captureGeneration)
            {
                EmitStatus(
                    $"{requestId} Gradio: ignoring HTTP 200 (stale session; capture_gen_send={captureGenAtSend} now={_captureGeneration}).");
                OnApiRequestFinished?.Invoke(true);
                yield break;
            }

            _chunksUploaded++;
            _requestSuccessCount++;
            string streamBody = streamReq.downloadHandler?.text ?? "";
            bool parsedTuple = TryParseGradioItalianTuple(streamBody, out string it, out string en);
            if (!parsedTuple)
            {
                // Fallback: some Gradio variants return JSON objects/quoted payloads instead of a plain tuple.
                it = ExtractText(streamBody);
                en = ExtractEnglishSidecar(streamBody);
                parsedTuple = !string.IsNullOrWhiteSpace(it) || !string.IsNullOrWhiteSpace(en);
            }

            if (!parsedTuple)
            {
                _requestParseEmptyCount++;
                _emptyHttp200Streak++;
                if (Time.realtimeSinceStartup - _lastEmptyTranscriptLogTime >= 6f)
                {
                    _lastEmptyTranscriptLogTime = Time.realtimeSinceStartup;
                    string preview = streamBody.Length > 360 ? streamBody.Substring(0, 360) + "…" : streamBody;
                    Debug.LogWarning("[ASR] Italian Gradio parse failed. Sample: " + preview);
                }
                OnApiRequestFinished?.Invoke(false);
                yield break;
            }

            string rawBody = "{\"italian\":\"" + EscapeJsonString(it) + "\",\"english\":\"" + EscapeJsonString(en) + "\"}";
            LastEnglishFromApi = en ?? "";
            string text = ExtractText(rawBody);
            text = CleanHallucinatedPrefix(text);
            text = RemoveImmediateRepeatedWords(text);
            text = RemoveRepeatedTailPhrases(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                LogClientTelemetry($"[client] RAW text=''", "RAW", requestSeq);
                _requestParseEmptyCount++;
                _emptyHttp200Streak++;
                OnApiRequestFinished?.Invoke(true);
                yield break;
            }

            LogClientTelemetry(
                $"[client] RAW text='{AsrLogSnippetForQuotes(text)}'",
                "RAW",
                requestSeq);

            _latestResponseSeqApplied = Mathf.Max(_latestResponseSeqApplied, requestSeq);

            if (LooksLikeMarginalNoiseCaption(text, IsItalianStrictNoiseGuard))
            {
                string cap = _currentUtteranceSb.ToString();
                LogClientTelemetry($"[client] DISPLAY text='{AsrLogSnippetForQuotes(cap)}'", "DISPLAY", requestSeq);
                EmitStatus(requestId + " Gradio: skipped marginal decode.");
                OnApiRequestFinished?.Invoke(true);
                yield break;
            }

            if (LooksLikeSilenceHallucinationCaption(text, _lastPostedChunkRms, IsItalianStrictNoiseGuard))
            {
                string cap = _currentUtteranceSb.ToString();
                LogClientTelemetry($"[client] DISPLAY text='{AsrLogSnippetForQuotes(cap)}'", "DISPLAY", requestSeq);
                EmitStatus(requestId + " Gradio: skipped low-energy ghost caption.");
                OnApiRequestFinished?.Invoke(true);
                yield break;
            }

            if (ShouldIgnoreOneWordTailJunk(_currentUtteranceSb.ToString(), text, chunkSilenceRatio))
            {
                string cap = _currentUtteranceSb.ToString();
                LogClientTelemetry($"[client] DISPLAY text='{AsrLogSnippetForQuotes(cap)}'", "DISPLAY", requestSeq);
                OnApiRequestFinished?.Invoke(true);
                yield break;
            }

            _emptyHttp200Streak = 0;
            const bool isFinalResult = false;
            string next = NormalizeCase(text);
            string prev = _currentUtteranceSb.ToString();
            string merged = MergeOverlappingTranscript(prev, next);
            LogClientTelemetry(
                $"[client] MERGE old='{AsrLogSnippetForQuotes(prev)}' new='{AsrLogSnippetForQuotes(next)}' merged='{AsrLogSnippetForQuotes(merged)}'",
                "MERGE",
                requestSeq);
            bool skipUi = ShouldSkipMergedTranscriptUpdate(prev, merged);
            if (!skipUi)
            {
                _currentUtteranceSb.Length = 0;
                _currentUtteranceSb.Append(merged);
                string mergedCaption = _currentUtteranceSb.ToString();
                OnTextUpdated?.Invoke(mergedCaption);
                OnTextUpdatedDetailed?.Invoke(mergedCaption, isFinalResult);
                NotifyNonEmptyCaptionDisplayed(mergedCaption);
                _utteranceAwaitingContinuation =
                    CaptionLooksIncompleteContinuation(mergedCaption);
            }
            else
            {
                EmitStatus($"{requestId} Gradio: filter skipped update (prev={prev.Length} merged={merged.Length}).");
            }

            string displayCaption = _currentUtteranceSb.ToString();
            LogClientTelemetry($"[client] DISPLAY text='{AsrLogSnippetForQuotes(displayCaption)}'", "DISPLAY", requestSeq);

            if (text.Trim().Length >= Mathf.Max(4, _strongSentenceMinChars)
                && _lastPostedChunkRms >= Mathf.Max(0.001f, _strongSentenceMinRms))
            {
                _lastStrongSentenceRealtime = Time.realtimeSinceStartup;
            }

            OnApiRequestFinished?.Invoke(true);
        }
    }

    private static bool TryParseGradioItalianTuple(string body, out string italian, out string english)
    {
        italian = "";
        english = "";
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        Match jsonData = Regex.Match(
            body,
            "\"data\"\\s*:\\s*\\[\\s*\"(?<it>(?:\\\\.|[^\"])*)\"\\s*,\\s*\"(?<en>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (jsonData.Success)
        {
            italian = Regex.Unescape(jsonData.Groups["it"].Value).Trim();
            english = Regex.Unescape(jsonData.Groups["en"].Value).Trim();
            return true;
        }

        Match lineData = Regex.Match(
            body,
            "data:\\s*\\[\\s*\"(?<it>(?:\\\\.|[^\"])*)\"\\s*,\\s*\"(?<en>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (lineData.Success)
        {
            italian = Regex.Unescape(lineData.Groups["it"].Value).Trim();
            english = Regex.Unescape(lineData.Groups["en"].Value).Trim();
            return true;
        }

        return false;
    }

    private static string EscapeJsonString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static byte[] Float32ToWavPcm16(byte[] float32Bytes, int sampleRate)
    {
        int sampleCount = float32Bytes != null ? float32Bytes.Length / 4 : 0;
        short[] pcm = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float s = BitConverter.ToSingle(float32Bytes, i * 4);
            s = Mathf.Clamp(s, -1f, 1f);
            pcm[i] = (short)Mathf.RoundToInt(s * 32767f);
        }

        int dataBytes = pcm.Length * 2;
        using (var ms = new MemoryStream(44 + dataBytes))
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)1); // mono
            bw.Write(Mathf.Max(1, sampleRate));
            bw.Write(Mathf.Max(1, sampleRate) * 2);
            bw.Write((short)2); // block align
            bw.Write((short)16); // bits
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);
            for (int i = 0; i < pcm.Length; i++)
            {
                bw.Write(pcm[i]);
            }
            return ms.ToArray();
        }
    }

    private static byte[] Float32ToBytes(float[] samples)
    {
        byte[] bytes = new byte[samples.Length * 4];
        int offset = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            byte[] s = BitConverter.GetBytes(samples[i]);
            bytes[offset] = s[0];
            bytes[offset + 1] = s[1];
            bytes[offset + 2] = s[2];
            bytes[offset + 3] = s[3];
            offset += 4;
        }
        return bytes;
    }

    /// <summary>Unescapes a full JSON string literal (leading/trailing quotes included), e.g. a proxy body that is one quoted JSON object.</summary>
    private static string UnescapeOuterJsonString(string raw)
    {
        if (raw == null || raw.Length < 2 || raw[0] != '"' || raw[raw.Length - 1] != '"')
            return null;

        var sb = new StringBuilder();
        int i = 1;
        int end = raw.Length - 1;
        while (i < end)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < end)
            {
                char n = raw[i + 1];
                if (n == '"' || n == '\\' || n == '/') { sb.Append(n); i += 2; continue; }

                if (n == 'n') { sb.Append('\n'); i += 2; continue; }

                if (n == 'r') { sb.Append('\r'); i += 2; continue; }

                if (n == 't') { sb.Append('\t'); i += 2; continue; }

                if (n == 'b') { sb.Append('\b'); i += 2; continue; }

                if (n == 'f') { sb.Append('\f'); i += 2; continue; }

                if (n == 'u' && i + 5 < end)
                {
                    string hex = raw.Substring(i + 2, 4);
                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                    {
                        if (code >= 0 && code < 0xD800 || (code > 0xDFFF && code <= 0xFFFF))
                            sb.Append((char)code);
                        else if (code >= 0x10000 && code <= 0x10FFFF)
                            sb.Append(char.ConvertFromUtf32(code));
                        i += 6;
                        continue;
                    }
                }

                i += 2;
                continue;
            }

            if (c == '"')
                return null;

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Italian pipeline JSON often includes <c>text_en</c> (Flask) or <c>english</c> (Option A).</summary>
    private static string ExtractEnglishSidecar(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string t = TryReadJsonStringAfterKey(raw, "text_en");
        if (!string.IsNullOrEmpty(t)) return t.Trim();
        t = TryReadJsonStringAfterKey(raw, "english");
        if (!string.IsNullOrEmpty(t)) return t.Trim();
        return "";
    }

    private static string ExtractText(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return string.Empty;
        string raw = response.Trim().TrimStart('\uFEFF');

        // Some proxies return JSON as a quoted string: "{\"text\":\"hello\"}"
        if (raw.Length >= 4 && raw[0] == '"' && raw[raw.Length - 1] == '"')
        {
            string inner = UnescapeOuterJsonString(raw);
            if (!string.IsNullOrEmpty(inner))
            {
                string t = inner.TrimStart();
                if (t.StartsWith("{") || t.StartsWith("["))
                {
                    string nested = ExtractText(inner);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }
        }

        // Plain text (no JSON)
        if (!raw.StartsWith("{") && !raw.StartsWith("["))
        {
            return raw;
        }

        // Option A proxy: { "italian": "...", "english": "..." }
        Match mItalianKey = Regex.Match(
            raw,
            "\"italian\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (mItalianKey.Success)
        {
            return Regex.Unescape(mItalianKey.Groups["v"].Value).Trim();
        }

        // Standard keys: "text" | "transcript" | …
        Match m = Regex.Match(
            raw,
            "\"(?:text|transcript|transcription|result|output|prediction)\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return Regex.Unescape(m.Groups["v"].Value).Trim();
        }

        // Gradio: "data": ["..."] first element string
        Match mData1 = Regex.Match(
            raw,
            "\"data\"\\s*:\\s*\\[\\s*\"(?<v>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (mData1.Success)
        {
            return Regex.Unescape(mData1.Groups["v"].Value).Trim();
        }

        // Gradio: "data": [null, "..."] or [null,"..."]
        Match mDataNullFirst = Regex.Match(
            raw,
            "\"data\"\\s*:\\s*\\[\\s*null\\s*,\\s*\"(?<v>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (mDataNullFirst.Success)
        {
            return Regex.Unescape(mDataNullFirst.Groups["v"].Value).Trim();
        }

        // Gradio nested: "data": [["...", ...]] or [[null,"..."]]
        Match mDataNested = Regex.Match(
            raw,
            "\"data\"\\s*:\\s*\\[\\s*\\[\\s*\"(?<v>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (mDataNested.Success)
        {
            return Regex.Unescape(mDataNested.Groups["v"].Value).Trim();
        }

        Match mDataNestedNull = Regex.Match(
            raw,
            "\"data\"\\s*:\\s*\\[\\s*\\[\\s*null\\s*,\\s*\"(?<v>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (mDataNestedNull.Success)
        {
            return Regex.Unescape(mDataNestedNull.Groups["v"].Value).Trim();
        }

        Match mDataObj = Regex.Match(
            raw,
            "\"data\"\\s*:\\s*\\[\\s*\\{[^\\]]*\"(?:text|transcript)\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (mDataObj.Success)
        {
            return Regex.Unescape(mDataObj.Groups["v"].Value).Trim();
        }

        // Root JSON array: ["transcript"]
        Match mArr = Regex.Match(raw, "^\\s*\\[\\s*\"(?<v>(?:\\\\.|[^\"])*)\"");
        if (mArr.Success)
        {
            return Regex.Unescape(mArr.Groups["v"].Value).Trim();
        }

        // Last resort: first long quoted string after "text"
        string fallback = TryReadJsonStringAfterKey(raw, "text");
        if (!string.IsNullOrEmpty(fallback)) return fallback;

        fallback = TryReadJsonStringAfterKey(raw, "transcript");
        if (!string.IsNullOrEmpty(fallback)) return fallback;

        // Hugging Face / nested JSON: scan for longest quoted string among common keys (handles odd nesting).
        fallback = ExtractLongestQuotedStringForKnownKeys(raw);
        if (!string.IsNullOrEmpty(fallback)) return fallback;

        return string.Empty;
    }

    private static readonly string[] s_JsonTextKeys =
    {
        "italian", "text", "transcript", "transcription", "prediction", "result", "output", "generated_text", "message", "label", "value"
    };

    private static string ExtractLongestQuotedStringForKnownKeys(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        string best = "";
        for (int i = 0; i < s_JsonTextKeys.Length; i++)
        {
            string key = s_JsonTextKeys[i];
            MatchCollection matches = Regex.Matches(
                raw,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
                RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                if (!m.Success) continue;
                string s = Regex.Unescape(m.Groups[1].Value).Trim();
                if (s.Length > best.Length) best = s;
            }
        }

        return best;
    }

    /// <summary>Finds "key": "value" and returns value with basic escape handling.</summary>
    private static string TryReadJsonStringAfterKey(string raw, string key)
    {
        int keyIdx = raw.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0) return string.Empty;

        int colon = raw.IndexOf(':', keyIdx);
        if (colon < 0) return string.Empty;

        int i = colon + 1;
        while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
        if (i >= raw.Length || raw[i] != '"') return string.Empty;

        i++;
        var sb = new StringBuilder();
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                char n = raw[i + 1];
                if (n == '"' || n == '\\' || n == '/') { sb.Append(n); i += 2; continue; }

                if (n == 'n') { sb.Append('\n'); i += 2; continue; }

                if (n == 'r') { sb.Append('\r'); i += 2; continue; }

                if (n == 't') { sb.Append('\t'); i += 2; continue; }

                i += 2;
                continue;
            }

            if (c == '"') break;

            sb.Append(c);
            i++;
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string t = text.Trim();
        if (t.ToUpperInvariant() == t && Regex.IsMatch(t, "[A-Z]"))
        {
            t = t.ToLowerInvariant();
        }
        return char.ToUpperInvariant(t[0]) + t.Substring(1);
    }

    private static string CleanHallucinatedPrefix(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Trim();
    }

    private static string RemoveImmediateRepeatedWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string t = Regex.Replace(text.Trim(), "\\s+", " ");
        t = Regex.Replace(t, "\\b(\\w+)(\\s+\\1\\b){1,}\\b", "$1", RegexOptions.IgnoreCase);
        return t.Trim();
    }

    private static string RemoveRepeatedTailPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string t = Regex.Replace(text.Trim(), "\\s+", " ");
        t = Regex.Replace(t, "\\b(\\w+(?:\\s+\\w+){1,5})\\s+\\1\\b", "$1", RegexOptions.IgnoreCase);
        return t.Trim();
    }


    /// <summary>
    /// Far-field HoloLens noise often produces micro-captions/repeated tokens that rarely appear in HF browser tests.
    /// </summary>
    private static bool LooksLikeMarginalNoiseCaption(string text, bool italianMode)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string s = Regex.Replace(text.Trim(), "\\s+", " ");
        if (s.Length <= 2) return true;
        if (italianMode && Regex.IsMatch(s, "^[aeiouh]+$", RegexOptions.IgnoreCase)) return true;
        MatchCollection tok = Regex.Matches(s, "\\b\\w+\\b");
        if (tok.Count == 0) return true;
        if (tok.Count == 1 && tok[0].Value.Length <= 2) return true;
        if (tok.Count >= 2 && tok.Count <= 8)
        {
            string first = tok[0].Value;
            int same = 0;
            for (int i = 0; i < tok.Count; i++)
            {
                if (string.Equals(tok[i].Value, first, StringComparison.OrdinalIgnoreCase))
                    same++;
            }

            if (same == tok.Count && first.Length <= 6)
                return true;
        }

        if (!italianMode && tok.Count >= 3 && tok.Count <= 8)
        {
            int tiny = 0;
            for (int i = 0; i < tok.Count; i++)
            {
                if (tok[i].Value.Length <= 2) tiny++;
            }

            if (tiny >= tok.Count - 1) return true;
        }

        return false;
    }

    /// <summary>Whisper often emits these on near-silence; applies even for phrase-final chunks when RMS is low.</summary>
    private static bool LooksLikeSilenceHallucinationCaption(string text, float chunkRms, bool italianMode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (chunkRms > 0.018f)
        {
            return false;
        }

        string s = Regex.Replace(text.Trim(), "\\s+", " ").ToLowerInvariant();
        if (s == "you" || s == "thank you" || s == "thankyou" || s == "thanks" || s == "bye")
        {
            return true;
        }

        if (!italianMode && (s == "yeah" || s == "uh" || s == "um" || s == "oh" || s == "okay" || s == "ok"))
        {
            return true;
        }

        if (italianMode && (s == "grazie" || s == "prego" || s == "sì" || s == "si" || s == "ciao"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a rolling caption: each POST returns a partial line for overlapping audio windows.
    /// Strips the longest suffix-of-previous / prefix-of-new overlap so words are not doubled (HF CPU contract).
    /// </summary>
    private static string MergeOverlappingTranscript(string previousFull, string incomingChunk)
    {
        if (string.IsNullOrWhiteSpace(incomingChunk)) return previousFull ?? string.Empty;
        string n = incomingChunk.Trim();
        if (string.IsNullOrWhiteSpace(n)) return previousFull ?? string.Empty;
        if (string.IsNullOrWhiteSpace(previousFull)) return n;
        string p = previousFull.TrimEnd();
        if (p.Length == 0) return n;

        if (p.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 && n.Length >= 8)
            return p;

        if (n.Length <= p.Length && p.EndsWith(n, StringComparison.OrdinalIgnoreCase))
            return p;

        if (TryMergeBySharedWordPrefix(p, n, out string wordMerged))
            return wordMerged;

        const int minCharOverlap = 3;
        int maxO = Mathf.Min(p.Length, n.Length);
        int best = 0;
        for (int len = maxO; len >= minCharOverlap; len--)
        {
            if (string.Compare(p, p.Length - len, n, 0, len, StringComparison.OrdinalIgnoreCase) == 0)
            {
                best = len;
                break;
            }
        }

        if (best <= 0 && maxO >= 1)
        {
            for (int len = Mathf.Min(maxO, minCharOverlap - 1); len >= 1; len--)
            {
                if (string.Compare(p, p.Length - len, n, 0, len, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    best = len;
                    break;
                }
            }
        }

        if (best > 0)
        {
            string tail = n.Length > best ? n.Substring(best).TrimStart() : string.Empty;
            if (tail.Length == 0) return p;
            return (p + " " + tail).Trim();
        }

        return (p + " " + n).Trim();
    }

    /// <summary>
    /// Match repeated tail words at end of previous transcript with prefix words of the new chunk; append only new continuation.
    /// </summary>
    private static bool TryMergeBySharedWordPrefix(string previousTrimmed, string incomingTrimmed, out string merged)
    {
        merged = null;
        if (string.IsNullOrWhiteSpace(previousTrimmed) || string.IsNullOrWhiteSpace(incomingTrimmed))
            return false;

        string[] pw = Regex.Split(previousTrimmed.Trim(), "\\s+");
        string[] nw = Regex.Split(incomingTrimmed.Trim(), "\\s+");
        if (pw.Length == 0 || nw.Length == 0) return false;

        int maxK = Mathf.Min(pw.Length, nw.Length, 16);
        for (int k = maxK; k >= 1; k--)
        {
            bool match = true;
            for (int i = 0; i < k; i++)
            {
                if (!string.Equals(pw[pw.Length - k + i], nw[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (!match)
                continue;

            var sb = new StringBuilder();
            for (int i = 0; i < pw.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(pw[i]);
            }

            for (int i = k; i < nw.Length; i++)
            {
                sb.Append(' ');
                sb.Append(nw[i]);
            }

            merged = sb.ToString().Trim();
            return true;
        }

        return false;
    }

    private static int CountLatinWords(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return 0;
        }

        return Regex.Matches(s.Trim(), "\\b\\w+\\b").Count;
    }

    /// <summary>
    /// Whisper often returns a one-word tail on high-silence chunks; ignore when we already hold a longer utterance.
    /// </summary>
    private static bool ShouldIgnoreOneWordTailJunk(string currentUtterance, string incomingText, float chunkSilenceRatio)
    {
        const float silenceGate = 0.45f;
        if (string.IsNullOrWhiteSpace(currentUtterance?.Trim()))
        {
            return false;
        }

        if (!(chunkSilenceRatio > silenceGate))
        {
            return false;
        }

        return CountLatinWords(incomingText) <= 1;
    }

    private static bool ShouldSkipMergedTranscriptUpdate(string previous, string merged)
    {
        if (string.IsNullOrWhiteSpace(merged)) return true;
        string prevT = (previous ?? string.Empty).Trim();
        string mergedT = merged.Trim();
        if (string.Equals(prevT, mergedT, StringComparison.OrdinalIgnoreCase)) return true;

        // Keep legitimate tail growth (common when ASR emits progressive phrase extensions).
        if (!string.IsNullOrEmpty(prevT)
            && mergedT.StartsWith(prevT, StringComparison.OrdinalIgnoreCase)
            && mergedT.Length > prevT.Length)
        {
            return false;
        }

        // Full-window decode that subsumes prior caption (common after out-of-order responses).
        if (!string.IsNullOrEmpty(prevT)
            && mergedT.Length > prevT.Length
            && mergedT.IndexOf(prevT, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return IsLikelyHallucination(mergedT);
    }

    private static bool IsLikelyHallucination(string next)
    {
        if (string.IsNullOrWhiteSpace(next)) return true;
        string n = next.Trim().ToLowerInvariant();
        if (n.Length < 2) return true;
        int words = Regex.Matches(n, "\\b\\w+\\b").Count;
        if (words >= 5)
        {
            // If too many repeated tokens in one short line, it's often a decode hallucination.
            MatchCollection wc = Regex.Matches(n, "\\b\\w+\\b");
            var seen = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < wc.Count; i++)
            {
                string w = wc[i].Value;
                if (!seen.ContainsKey(w)) seen[w] = 0;
                seen[w]++;
            }
            foreach (var kv in seen)
            {
                if (kv.Value >= 4) return true;
            }
        }
        return false;
    }

    private void OnDestroy()
    {
        StopAsr();

        if (Instance == this)
        {
            Instance = null;
        }
    }
}

