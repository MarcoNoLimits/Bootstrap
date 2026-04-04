using System;
using System.Collections;
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

    [Header("ASR API")]
    [SerializeField] private string _asrApiUrl = "https://thedeezat-asr-hearing-impaired-api.hf.space/audio";
    [SerializeField] private int _sampleRate = 16000;
    [SerializeField] private float _chunkSeconds = 0.9f;
    [SerializeField] private float _sendWindowSeconds = 8.0f;
    [SerializeField] private int _clipLengthSeconds = 30;

    private AudioClip _micClip;
    private string _micDevice;
    private Coroutine _captureCoroutine;
    private int _lastMicSample;
    private readonly StringBuilder _latestText = new StringBuilder();
    private bool _requestInFlight;
    private byte[] _pendingFloat32Bytes;

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

    public void StartAsr()
    {
        if (IsRunning) return;
        if (Microphone.devices == null || Microphone.devices.Length == 0)
        {
            Debug.LogError("[ASR] No microphone device available.");
            return;
        }

        _micDevice = Microphone.devices[0];
        _micClip = Microphone.Start(_micDevice, true, _clipLengthSeconds, _sampleRate);
        _lastMicSample = 0;
        _latestText.Length = 0;
        CurrentMicLevel = 0f;
        IsRunning = true;

        if (_captureCoroutine != null) StopCoroutine(_captureCoroutine);
        _captureCoroutine = StartCoroutine(CaptureAndUploadLoop());
        Debug.Log("[ASR] Microphone capture started.");
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
        Debug.Log("[ASR] Microphone capture stopped.");
    }

    private IEnumerator CaptureAndUploadLoop()
    {
        while (IsRunning)
        {
            if (_micClip == null || string.IsNullOrEmpty(_micDevice) || !Microphone.IsRecording(_micDevice))
            {
                yield return null;
                continue;
            }

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

            int minSamplesToSend = Mathf.RoundToInt(_sampleRate * _chunkSeconds);
            if (deltaSamples < minSamplesToSend)
            {
                yield return null;
                continue;
            }

            int windowSamples = Mathf.RoundToInt(_sampleRate * _sendWindowSeconds);
            float[] chunk = ExtractLatestSamples(currentPos, windowSamples, totalSamples);
            _lastMicSample = currentPos;

            byte[] float32Bytes = Float32ToBytes(chunk);
            QueueSend(float32Bytes);
            yield return null;
        }
    }

    private void QueueSend(byte[] float32Bytes)
    {
        if (float32Bytes == null || float32Bytes.Length == 0) return;
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
        using (UnityWebRequest req = new UnityWebRequest(_asrApiUrl, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(float32Bytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/octet-stream");
            req.SetRequestHeader("X-Sample-Rate", _sampleRate.ToString());
            req.timeout = 12;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[ASR] API error: " + req.error);
            }
            else
            {
                string text = ExtractText(req.downloadHandler.text);
                text = CleanHallucinatedPrefix(text);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string next = NormalizeCase(text);
                    string prev = _latestText.ToString();
                    if (ShouldAcceptTranscript(prev, next))
                    {
                        _latestText.Length = 0;
                        _latestText.Append(next);
                        OnTextUpdated?.Invoke(_latestText.ToString());
                    }
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
        string raw = response.Trim();

        if (!raw.StartsWith("{")) return raw;
        Match m = Regex.Match(
            raw,
            "\"(?:text|transcript|transcription|result)\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return Regex.Unescape(m.Groups["v"].Value).Trim();
        }

        return string.Empty;
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
        int prevWords = previous.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
        int nextWords = next.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
        if (nextWords <= 2 && prevWords >= 4)
        {
            return false;
        }
        if (next.Length < Mathf.FloorToInt(previous.Length * 0.78f) && !Regex.IsMatch(next, "[.!?]$"))
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

