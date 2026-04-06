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

    public delegate void TextUpdatedHandler(string text);
    public event TextUpdatedHandler OnTextUpdated;
    public event Action<float> OnMicLevelUpdated;
    /// <summary>Fired after each HTTP attempt; <paramref name="success"/> is true when the response was received and parsed.</summary>
    public event Action<bool> OnApiRequestFinished;
    /// <summary>Unity <see cref="Microphone"/> never reached a recording state (permissions, device, or platform).</summary>
    public event Action OnMicrophoneNotReady;
    /// <summary>Fired once when <see cref="Microphone.IsRecording"/> becomes true and capture begins.</summary>
    public event Action OnMicrophoneReady;
    [Header("ASR API")]
    [Tooltip("Transcribe URL: POST raw float32 PCM mono (little-endian), Content-Type application/octet-stream, header X-Sample-Rate matching _sampleRate (e.g. 16000). Response JSON { \"text\": \"...\" }.")]
    [SerializeField] private string _asrApiUrl = "https://thedeezat-asr-hearing-impaired-api.hf.space/audio";
    [SerializeField] private int _sampleRate = 16000;
    [Tooltip("Minimum seconds of new audio before sending (in real time). Per API: very short chunks may return {\"text\":\"\"}.")]
    [SerializeField] private float _chunkSeconds = 1.0f;
    [SerializeField] private float _sendWindowSeconds = 8.0f;
    [SerializeField] private int _clipLengthSeconds = 30;

    private AudioClip _micClip;
    private string _micDevice;
    private Coroutine _captureCoroutine;
    private int _lastMicSample;
    private readonly StringBuilder _latestText = new StringBuilder();
    private bool _requestInFlight;
    private byte[] _pendingFloat32Bytes;
    private int _chunksUploaded;
    private bool _loggedFirstChunk;
    private bool _loggedResample;
    private float _lastEmptyTranscriptLogTime = -999f;
    private static string _logFilePath;

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
        AsrFileLog(full);
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
            _asrApiUrl = url.Trim();
    }

    /// <summary>Clears the previous transcript used for deduplication. Call after a phrase is finalized or on new speech so the next utterance is not rejected.</summary>
    public void ClearTranscriptContext()
    {
        _latestText.Length = 0;
    }

    public void StartAsr()
    {
        if (IsRunning) return;
        if (Microphone.devices == null || Microphone.devices.Length == 0)
        {
            if (string.IsNullOrEmpty(_logFilePath))
                _logFilePath = Path.Combine(Application.persistentDataPath, "asr_debug.log");
            EmitStatus("No microphone devices. Log: " + _logFilePath);
            Debug.LogError("[ASR] No microphone device available.");
            OnMicrophoneNotReady?.Invoke();
            return;
        }

        _micDevice = Microphone.devices[0];
        _chunksUploaded = 0;
        _loggedFirstChunk = false;
        if (string.IsNullOrEmpty(_logFilePath))
            _logFilePath = Path.Combine(Application.persistentDataPath, "asr_debug.log");
        EmitStatus("Full log (always written): " + _logFilePath);

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
        CurrentMicLevel = 0f;
        IsRunning = true;

        StartCoroutine(CoCheckHealthEndpoint());

        if (_captureCoroutine != null) StopCoroutine(_captureCoroutine);
        _captureCoroutine = StartCoroutine(CaptureAndUploadLoop());
        EmitStatus("Capture waiting for IsRecording…");
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
            windowSamples = Mathf.Min(windowSamples, totalSamples);
            float[] chunk = ExtractLatestSamples(currentPos, windowSamples, totalSamples);
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

            byte[] float32Bytes = Float32ToBytes(chunk);
            QueueSend(float32Bytes);
            yield return null;
        }
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
        if (string.IsNullOrWhiteSpace(_asrApiUrl))
        {
            Debug.LogWarning("[ASR] No API URL configured; skipping upload.");
            OnApiRequestFinished?.Invoke(false);
            _requestInFlight = false;
            if (!IsRunning) yield break;
            if (_pendingFloat32Bytes != null && _pendingFloat32Bytes.Length > 0)
            {
                byte[] next = _pendingFloat32Bytes;
                _pendingFloat32Bytes = null;
                StartCoroutine(SendChunkToApi(next));
            }

            yield break;
        }

        if (float32Bytes.Length % 4 != 0)
        {
            EmitStatus("Invalid body: length not multiple of 4 (float32).");
            OnApiRequestFinished?.Invoke(false);
            _requestInFlight = false;
            yield break;
        }

        using (UnityWebRequest req = new UnityWebRequest(_asrApiUrl, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(float32Bytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/octet-stream");
            req.SetRequestHeader("X-Sample-Rate", _sampleRate.ToString());
            req.SetRequestHeader("User-Agent", "Unity-HoloLens-ASR/1.0");
            req.timeout = 45;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string errBody = req.downloadHandler?.text ?? "";
                if (req.responseCode == 400 && !string.IsNullOrEmpty(errBody))
                    EmitStatus("HTTP 400: " + (errBody.Length > 200 ? errBody.Substring(0, 200) + "…" : errBody));
                else
                    EmitStatus("POST /audio failed: " + req.error + " HTTP " + req.responseCode);
                Debug.LogWarning("[ASR] API HTTP failed: " + req.error + " code=" + req.responseCode);
                OnApiRequestFinished?.Invoke(false);
            }
            else
            {
                _chunksUploaded++;
                string rawBody = req.downloadHandler?.text ?? string.Empty;
                if (_chunksUploaded <= 3 || _chunksUploaded % 20 == 0)
                {
                    string preview = rawBody.Length > 160 ? rawBody.Substring(0, 160) + "…" : rawBody;
                    EmitStatus($"HTTP 200 chunk #{_chunksUploaded} resp: {preview}");
                }

                string text = ExtractText(rawBody);
                text = CleanHallucinatedPrefix(text);
                if (string.IsNullOrWhiteSpace(text))
                {
                    // API contract: HTTP 200 + {"text":""} is valid when chunk is silent/too short per server.
                    if (Time.realtimeSinceStartup - _lastEmptyTranscriptLogTime >= 5f)
                    {
                        _lastEmptyTranscriptLogTime = Time.realtimeSinceStartup;
                        Debug.Log(
                            "[ASR] Empty transcript for this chunk (valid per API if quiet/short). " +
                            "If this repeats while speaking, check mic level and clipHz→16kHz resampling.");
                    }

                    OnApiRequestFinished?.Invoke(true);
                }
                else
                {
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
                        EmitStatus($"Filter skipped update (prev={prev.Length} next={next.Length}).");
                    }

                    OnApiRequestFinished?.Invoke(true);
                }
            }
        }

        _requestInFlight = false;
        if (!IsRunning) yield break;

        if (_pendingFloat32Bytes != null && _pendingFloat32Bytes.Length > 0)
        {
            byte[] next = _pendingFloat32Bytes;
            _pendingFloat32Bytes = null;
            StartCoroutine(SendChunkToApi(next));
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

    private static string ExtractText(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return string.Empty;
        string raw = response.Trim().TrimStart('\uFEFF');

        // Plain text (no JSON)
        if (!raw.StartsWith("{") && !raw.StartsWith("["))
        {
            return raw;
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

        return string.Empty;
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
        string t = text.Trim();
        t = Regex.Replace(t, "^(thank you[\\s,!.?:-]*)+", "", RegexOptions.IgnoreCase);
        return t.Trim();
    }

    private static bool ShouldAcceptTranscript(string previous, string next)
    {
        if (string.IsNullOrWhiteSpace(next)) return false;
        if (string.IsNullOrWhiteSpace(previous)) return true;
        if (string.Equals(previous, next, StringComparison.Ordinal)) return false;
        // Reject only if the new text looks like a spurious shrink of the same line (same start, much shorter).
        if (next.Length < Mathf.FloorToInt(previous.Length * 0.78f)
            && !Regex.IsMatch(next, "[.!?]$")
            && previous.StartsWith(next.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
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

