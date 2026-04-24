using System;
using System.Collections;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

public class HololensAsrManager : MonoBehaviour
{
    public static HololensAsrManager Instance { get; private set; }

    public bool IsRunning { get; private set; }
    public float CurrentMicLevel { get; private set; }
    /// <summary>True while POST /audio is in flight. Remote cold starts can exceed 60s; UI should not treat this as "no speech".</summary>
    public bool IsApiRequestInFlight => _requestInFlight;

    public delegate void TextUpdatedHandler(string text);
    public event TextUpdatedHandler OnTextUpdated;
    public event Action<float> OnMicLevelUpdated;
    /// <summary>Fired after each HTTP attempt; <paramref name="success"/> is true when the response was received and parsed.</summary>
    public event Action<bool> OnApiRequestFinished;
    /// <summary>Unity <see cref="Microphone"/> never reached a recording state (permissions, device, or platform).</summary>
    public event Action OnMicrophoneNotReady;
    /// <summary>Fired once when <see cref="Microphone.IsRecording"/> becomes true and capture begins.</summary>
    public event Action OnMicrophoneReady;
    /// <summary>Fired when many consecutive HTTP 200 responses parse to empty text (HF shape mismatch or silent chunks) — use to fall back to local dictation.</summary>
    public event Action OnRepeatedEmptySuccessfulApiResponses;
    [Header("ASR API")]
    [Tooltip("Transcribe URL: POST raw float32 PCM mono (little-endian), Content-Type application/octet-stream, header X-Sample-Rate matching _sampleRate (e.g. 16000). Response JSON { \"text\": \"...\" }.")]
    [SerializeField] private string _asrApiUrl = "https://thedeezat-asr-hearing-impaired-api.hf.space/audio";
    [Tooltip("Write detailed ASR logs to persistentDataPath/asr_debug.log. Keep off for faster runtime on device.")]
    [SerializeField] private bool _writeAsrDebugFile = false;
    [SerializeField] private int _sampleRate = 16000;
    [Tooltip("Minimum seconds of new audio before each POST. Lower values improve realtime response.")]
    [SerializeField] private float _chunkSeconds = 1.8f;
    [Tooltip("Sliding window length uploaded each request. Smaller windows reduce repeated transcripts.")]
    [SerializeField] private float _sendWindowSeconds = 2.2f;
    [Tooltip("Skip POST when chunk RMS is below this threshold (client-side silence guard).")]
    [SerializeField] private float _minChunkRmsToSend = 0.0065f;
    [SerializeField] private int _clipLengthSeconds = 30;
    [Header("Microphone selection")]
    [Tooltip("If set, prefer a microphone whose device name contains this text (case-insensitive).")]
    [SerializeField] private string _preferredMicNameContains = "";
    [Tooltip("After this many consecutive HTTP 200 responses with empty parsed text, raise OnRepeatedEmptySuccessfulApiResponses (HybridVoice switches to dictation).")]
    [SerializeField] private int _emptyHttp200StreakBeforeFallback = 10;
    [Header("Input level (matches HF Space preprocess: quiet audio is dropped server-side)")]
    [Tooltip("Boost quiet microphone PCM so RMS passes the Space ASR_MIN_RMS gate (~0.007 after server high-pass). Disable only for debugging.")]
    [SerializeField] private bool _adaptiveInputGain = false;
    [SerializeField] private float _adaptiveGainTargetRms = 0.038f;
    [SerializeField] private float _adaptiveGainMax = 8f;
    [Tooltip("HF cold start / long clips; UnityWebRequest uses seconds.")]
    [SerializeField] private int _requestTimeoutSeconds = 120;
    [Tooltip("Optional language hint for /audio API: english, italian, or auto/empty.")]
    [SerializeField] private string _forcedLanguage = "";
    private bool _warnedNonAudioEndpoint;

    /// <summary>Last <c>text_en</c> / <c>english</c> from Italian pipeline JSON, if present.</summary>
    public string LastEnglishFromApi { get; private set; } = "";

    private AudioClip _micClip;
    private string _micDevice;
    private Coroutine _captureCoroutine;
    private int _lastMicSample;
    private readonly StringBuilder _latestText = new StringBuilder();
    private bool _requestInFlight;
    private byte[] _pendingFloat32Bytes;
    private int _chunksUploaded;
    private int _requestAttemptCount;
    private int _requestSuccessCount;
    private int _requestFailureCount;
    private int _requestParseEmptyCount;
    private bool _loggedFirstChunk;
    private bool _loggedResample;
    private float _lastEmptyTranscriptLogTime = -999f;
    private static string _logFilePath;

    private static bool _quittingHookRegistered;
    private int _requestSeq;
    private int _emptyHttp200Streak;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
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
        _forcedLanguage = (forcedLanguage ?? string.Empty).Trim().ToLowerInvariant();
        if (_forcedLanguage == "auto")
        {
            _forcedLanguage = string.Empty;
        }
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
        _chunkSeconds = Mathf.Clamp(chunkSeconds, 0.4f, 2.5f);
        _sendWindowSeconds = Mathf.Clamp(sendWindowSeconds, _chunkSeconds + 0.2f, 6.0f);
        _minChunkRmsToSend = Mathf.Clamp(minChunkRmsToSend, 0f, 0.03f);
        _adaptiveGainTargetRms = Mathf.Clamp(adaptiveGainTargetRms, 0.01f, 0.2f);
        _adaptiveGainMax = Mathf.Clamp(adaptiveGainMax, 1f, 24f);
    }

    /// <summary>Clears the previous transcript used for deduplication. Call after a phrase is finalized or on new speech so the next utterance is not rejected.</summary>
    public void ClearTranscriptContext()
    {
        _latestText.Length = 0;
        LastEnglishFromApi = "";
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
        _latestText.Length = 0;
        LastEnglishFromApi = "";
        _emptyHttp200Streak = 0;
        _chunkSeconds = Mathf.Clamp(_chunkSeconds, 0.4f, 2.5f);
        _sendWindowSeconds = Mathf.Clamp(_sendWindowSeconds, _chunkSeconds + 0.25f, 6.0f);
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
        _pendingFloat32Bytes = null;
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
                IsRunning = false;
                if (!string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(_micDevice))
                {
                    Microphone.End(_micDevice);
                }

                _micClip = null;
                _micDevice = null;
                _requestInFlight = false;
                _pendingFloat32Bytes = null;
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
                yield return null;
                continue;
            }

            clipHz = _micClip.frequency > 0 ? _micClip.frequency : _sampleRate;

            int currentPos = Microphone.GetPosition(_micDevice);
            if (currentPos < 0)
            {
                yield return null;
                continue;
            }

            int totalSamples = _micClip.samples;
            int deltaSamples = currentPos - _lastMicSample;
            if (deltaSamples < 0) deltaSamples += totalSamples;

            UpdateMicLevel(currentPos, totalSamples);

            // Must use clip sample rate — NOT _sampleRate — or timing/window size is wrong vs Unity buffer.
            int minSamplesToSend = Mathf.RoundToInt(clipHz * _chunkSeconds);
            if (deltaSamples < minSamplesToSend)
            {
                yield return null;
                continue;
            }

            int windowSamples = Mathf.RoundToInt(clipHz * _sendWindowSeconds);
            int sendSamples = Mathf.Min(Mathf.Max(deltaSamples, minSamplesToSend), windowSamples);
            float[] chunk = ExtractSamples(_lastMicSample, sendSamples, totalSamples);
            _lastMicSample = currentPos;

            if (clipHz != _sampleRate)
            {
                if (!_loggedResample)
                {
                    _loggedResample = true;
                    Debug.Log($"[ASR] Resampling {clipHz}Hz → {_sampleRate}Hz for API (X-Sample-Rate must match body).");
                }

                chunk = ResampleLinear(chunk, clipHz, _sampleRate);
            }

            if (_adaptiveInputGain)
                ApplyAdaptiveGain(chunk, _adaptiveGainTargetRms, _adaptiveGainMax);

            if (ChunkRms(chunk) < Mathf.Max(0f, _minChunkRmsToSend))
            {
                // Empty transcripts are valid for quiet chunks; skip local silence to reduce no-op requests.
                yield return null;
                continue;
            }

            byte[] float32Bytes = Float32ToBytes(chunk);
            QueueSend(float32Bytes);
            yield return null;
        }
    }

    /// <summary>
    /// Raises quiet speech so the Space <c>preprocess_chunk</c> RMS check (ASR_MIN_RMS) does not reject the chunk as silence.
    /// Server applies 80 Hz high-pass then requires RMS ≥ ~0.007; HoloLens mics are often softer than desktop.
    /// </summary>
    private static void ApplyAdaptiveGain(float[] samples, float targetRms, float maxGain)
    {
        if (samples == null || samples.Length == 0) return;
        if (targetRms <= 0f || maxGain < 1f) return;

        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            sum += s * s;
        }

        float rms = (float)System.Math.Sqrt(sum / samples.Length);
        const float silence = 1e-7f;
        if (rms < silence || rms >= targetRms)
            return;

        float g = Mathf.Min(maxGain, targetRms / rms);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = Mathf.Clamp(samples[i] * g, -1f, 1f);
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

    private void QueueSend(byte[] float32Bytes)
    {
        if (float32Bytes == null || float32Bytes.Length == 0) return;
        if (!_loggedFirstChunk)
        {
            _loggedFirstChunk = true;
            int samples = float32Bytes.Length / 4;
            EmitStatus($"POST /audio first chunk: {float32Bytes.Length} bytes ({samples} float32 LE mono)");
        }

        if (_requestInFlight)
        {
            _pendingFloat32Bytes = float32Bytes;
            return;
        }

        StartCoroutine(SendChunkToApi(float32Bytes));
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

        float rms = Mathf.Sqrt(sum / tmp.Length);
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

    private IEnumerator SendChunkToApi(byte[] float32Bytes)
    {
        _requestInFlight = true;
        string requestId = "asr-" + System.Threading.Interlocked.Increment(ref _requestSeq).ToString("D5");
        _requestAttemptCount++;
        byte[] nextPending = null;

        try
        {
            if (string.IsNullOrWhiteSpace(_asrApiUrl))
            {
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
                EmitStatus(requestId + " invalid body: length not multiple of 4 (float32).");
                _requestFailureCount++;
                OnApiRequestFinished?.Invoke(false);
                yield break;
            }

            if (isGradioTranscribeCall)
            {
                yield return SendChunkToItalianGradioApi(requestId, float32Bytes);
                yield break;
            }

            using (UnityWebRequest req = new UnityWebRequest(_asrApiUrl, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(float32Bytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/octet-stream");
                req.SetRequestHeader("X-Sample-Rate", _sampleRate.ToString());
                if (!string.IsNullOrWhiteSpace(_forcedLanguage))
                {
                    req.SetRequestHeader("X-Forced-Language", _forcedLanguage);
                }
                req.SetRequestHeader("User-Agent", "Unity-HoloLens-ASR/1.0");
                req.timeout = Mathf.Clamp(_requestTimeoutSeconds, 30, 600);
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string errBody = req.downloadHandler?.text ?? "";
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
                    _chunksUploaded++;
                    _requestSuccessCount++;
                    string rawBody = req.downloadHandler?.text ?? string.Empty;
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
                    if (string.IsNullOrWhiteSpace(text))
                    {
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
                        _emptyHttp200Streak = 0;
                        string next = NormalizeCase(text);
                        string prev = _latestText.ToString();
                        if (ShouldAcceptTranscript(prev, next))
                        {
                            _latestText.Length = 0;
                            _latestText.Append(next);
                            OnTextUpdated?.Invoke(_latestText.ToString());
                        }
                        else
                        {
                            EmitStatus($"{requestId} filter skipped update (prev={prev.Length} next={next.Length}).");
                        }

                        OnApiRequestFinished?.Invoke(true);
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

            if (_pendingFloat32Bytes != null && _pendingFloat32Bytes.Length > 0)
            {
                nextPending = _pendingFloat32Bytes;
                _pendingFloat32Bytes = null;
            }
        }

        if (!IsRunning) yield break;
        if (nextPending != null && nextPending.Length > 0)
        {
            StartCoroutine(SendChunkToApi(nextPending));
        }
    }

    private IEnumerator SendChunkToItalianGradioApi(string requestId, byte[] float32Bytes)
    {
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
            callReq.timeout = Mathf.Clamp(_requestTimeoutSeconds, 30, 600);
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
                callReq2.timeout = Mathf.Clamp(_requestTimeoutSeconds, 30, 600);
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
            streamReq.timeout = Mathf.Clamp(_requestTimeoutSeconds, 30, 600);
            streamReq.SetRequestHeader("User-Agent", "Unity-HoloLens-ASR/1.0");
            yield return streamReq.SendWebRequest();

            if (streamReq.result != UnityWebRequest.Result.Success)
            {
                EmitStatus(requestId + " Gradio stream failed: " + streamReq.error + " HTTP " + streamReq.responseCode);
                _requestFailureCount++;
                OnApiRequestFinished?.Invoke(false);
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
                _requestParseEmptyCount++;
                _emptyHttp200Streak++;
                OnApiRequestFinished?.Invoke(true);
                yield break;
            }

            _emptyHttp200Streak = 0;
            string next = NormalizeCase(text);
            string prev = _latestText.ToString();
            if (ShouldAcceptTranscript(prev, next))
            {
                _latestText.Length = 0;
                _latestText.Append(next);
                OnTextUpdated?.Invoke(_latestText.ToString());
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

    private static bool ShouldAcceptTranscript(string previous, string next)
    {
        if (string.IsNullOrWhiteSpace(next)) return false;
        if (IsLikelyHallucination(next)) return false;
        if (string.IsNullOrWhiteSpace(previous)) return true;
        if (string.Equals(previous, next, StringComparison.Ordinal)) return false;
        string prevNorm = RemoveRepeatedTailPhrases(RemoveImmediateRepeatedWords(previous));
        string nextNorm = RemoveRepeatedTailPhrases(RemoveImmediateRepeatedWords(next));
        if (string.Equals(prevNorm, nextNorm, StringComparison.OrdinalIgnoreCase))
            return false;
        // Keep stable context: reject abrupt tiny rewrites that do not overlap previous phrase.
        if (previous.Length > 12 && next.Length < Mathf.FloorToInt(previous.Length * 0.65f)
            && !previous.StartsWith(next, StringComparison.OrdinalIgnoreCase)
            && !next.StartsWith(previous, StringComparison.OrdinalIgnoreCase))
            return false;
        // Reject only if the new text looks like a spurious shrink of the same line (same start, much shorter).
        if (next.Length < Mathf.FloorToInt(previous.Length * 0.78f)
            && !Regex.IsMatch(next, "[.!?]$")
            && previous.StartsWith(next.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Guard against loop-style hallucinations where a line doubles itself.
        string prevNorm2 = Regex.Replace(previous, "\\s+", " ").Trim();
        string nextNorm2 = Regex.Replace(next, "\\s+", " ").Trim();
        if (nextNorm2.Length > prevNorm2.Length * 1.4f
            && nextNorm2.StartsWith(prevNorm2, StringComparison.OrdinalIgnoreCase))
        {
            string tail = nextNorm2.Substring(prevNorm2.Length).TrimStart();
            if (!string.IsNullOrEmpty(tail)
                && (prevNorm2.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                    || tail.StartsWith(prevNorm2, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
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

