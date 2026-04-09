using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

[Serializable]
public class InferResponse
{
    public string letter;
    public float confidence;
    public string text;
    public string status_hint;
    public string model;
}

/// <summary>
/// HoloLens inference client:
/// - Captures camera frames (WebCamTexture) or an override Texture source
/// - Crops ROI on-device (center crop for smoke tests)
/// - Resizes to 224x224
/// - JPEG-encodes and POSTs multipart form-data to /infer
/// - Parses JSON and updates UI with debounce/confidence threshold
/// - Exposes optional spell endpoint methods for UI buttons
/// </summary>
[DefaultExecutionOrder(-40)]
public class SignInferenceClient : MonoBehaviour
{
    private const string WorkingStatusText = "Sign language mode is active and working.";

    [Header("API")]
    [Tooltip("Use LAN IP for HoloLens tests, e.g. http://172.16.23.67:8010 (or localhost on same machine).")]
    [SerializeField] private string baseUrl = "http://172.16.23.67:8010";
    [SerializeField] private bool spell = true;
    [SerializeField] private string sessionId = "";
    [SerializeField] private float requestTimeoutSeconds = 4f;

    [Header("Capture")]
    [Tooltip("If enabled and available, uses WebCamTexture (PV camera) as source.")]
    [SerializeField] private bool useWebCamTexture = true;
    [Tooltip("If true, only allow WebCamTexture capture on HoloLens device builds (never desktop editor/webcam).")]
    [SerializeField] private bool hololensCameraOnly = true;
    [Tooltip("Editor-only debug: allow desktop webcam in Unity Editor while keeping device builds HoloLens-focused.")]
    [SerializeField] private bool allowEditorDesktopCamera = true;
    [Tooltip("Optional texture source if not using WebCamTexture (e.g. RenderTexture converted elsewhere).")]
    [SerializeField] private Texture overrideSource;
    [SerializeField] private int targetSize = 224;
    [SerializeField] private int jpegQuality = 70;
    [SerializeField, Range(0.25f, 1f)] private float centerCropScale = 0.65f;

    [Header("Rate control")]
    [Tooltip("Inference requests per second, independent from camera FPS.")]
    [SerializeField] private float requestFps = 8f;
    [Tooltip("When enabled, sends the first inference request as soon as startup capture is ready.")]
    [SerializeField] private bool startCapturingOnLaunch = true;
    [Tooltip("Skip frame while one request is running.")]
    [SerializeField] private bool dropIfRequestInFlight = true;
    [Tooltip("Only attempt inference every Nth update tick (1 = every tick).")]
    [SerializeField] private int sendEveryNthFrame = 1;

    [Header("Optional change gating")]
    [Tooltip("If enabled, skips sending when ROI is very similar to last sent ROI.")]
    [SerializeField] private bool skipSimilarFrames = false;
    [Tooltip("Lower = stricter change required. 0.02-0.08 is a practical range.")]
    [SerializeField, Range(0.001f, 0.2f)] private float similarityThreshold = 0.04f;
    [Tooltip("How aggressively to downsample before similarity check.")]
    [SerializeField] private int similaritySampleSize = 16;

    [Header("Model tuning (optional fields)")]
    [SerializeField] private int stableFrames = 0;
    [SerializeField] private int pauseMs = 0;
    [SerializeField] private float minConf = 0f;
    [SerializeField] private int noiseFrames = 0;
    [SerializeField] private int wordPauseMs = 0;

    [Header("UI")]
    [SerializeField] private Text letterText;
    [SerializeField] private Text serverText;
    [SerializeField] private Text statusHintText;
    [SerializeField] private float confidenceThreshold = 0.5f;
    [SerializeField] private float uiDebounceSeconds = 0.12f;
    [SerializeField] private bool useServerTextAsAuthoritative = true;

    [Header("Debug")]
    [Tooltip("If enabled, saves occasional captured ROI JPGs to persistentDataPath/sign_debug.")]
    [SerializeField] private bool saveDebugFrames = false;
    [Tooltip("Save one debug frame every N send attempts.")]
    [SerializeField] private int saveEveryNSends = 20;

    private WebCamTexture _webCamTexture;
    private Texture2D _workingFrame;
    private Texture2D _roiTexture;
    private bool _requestInFlight;
    private float _nextRequestAt;
    private string _pendingLetter;
    private string _lastAppliedLetter;
    private float _nextUiApplyAt;
    private string _lastServerText;
    private Color32[] _lastRoiSample;
    private int _frameTickCounter;
    private bool _hasShownWorkingStatus;
    private bool _loggedFirstRequest;
    private bool _loggedFirstSuccess;
    private int _captureFrameCount;
    private int _sendAttemptCount;
    private int _sendSuccessCount;
    private string _lastSendState = "Idle";
    private float _lastSendAt = -1f;
    private string _debugFrameDir;
    private string _lastRenderedStatus;
    private float _nextStatusLogAt;

    public event Action<InferResponse> OnInferResponse;
    public event Action<string> OnNetworkError;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoStart()
    {
        if (FindObjectOfType<SignInferenceClient>() != null)
        {
            return;
        }

        var go = new GameObject("SIGN_INFERENCE_CLIENT");
        go.AddComponent<SignInferenceClient>();
        DontDestroyOnLoad(go);
        Debug.Log("[SignInferenceClient] Auto-started.");
    }

    private void Awake()
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N");
        }

        targetSize = Mathf.Max(32, targetSize);
        jpegQuality = Mathf.Clamp(jpegQuality, 1, 100);
        requestFps = Mathf.Clamp(requestFps, 1f, 30f);
        sendEveryNthFrame = Mathf.Max(1, sendEveryNthFrame);
        requestTimeoutSeconds = Mathf.Max(1f, requestTimeoutSeconds);
        centerCropScale = Mathf.Clamp(centerCropScale, 0.25f, 1f);
        similaritySampleSize = Mathf.Clamp(similaritySampleSize, 8, 32);
        saveEveryNSends = Mathf.Max(1, saveEveryNSends);

        _roiTexture = new Texture2D(targetSize, targetSize, TextureFormat.RGB24, false);
        _debugFrameDir = Path.Combine(Application.persistentDataPath, "sign_debug");
    }

    private void Start()
    {
        if (useWebCamTexture)
        {
            StartWebCam();
        }

        if (startCapturingOnLaunch)
        {
            StartCoroutine(BeginCaptureOnLaunch());
        }
    }

    private void Update()
    {
        UpdateStatusHint();

        if (Time.time >= _nextUiApplyAt && !string.IsNullOrEmpty(_pendingLetter))
        {
            if (_pendingLetter != _lastAppliedLetter)
            {
                _lastAppliedLetter = _pendingLetter;
                if (letterText != null)
                {
                    letterText.text = _lastAppliedLetter;
                }
            }

            _pendingLetter = null;
        }

        if (Time.time < _nextRequestAt)
        {
            return;
        }

        _nextRequestAt = Time.time + (1f / requestFps);

        if (_requestInFlight && dropIfRequestInFlight)
        {
            return;
        }

        _frameTickCounter++;
        if ((_frameTickCounter % sendEveryNthFrame) != 0)
        {
            return;
        }

        Texture src = GetActiveSourceTexture();
        if (src == null)
        {
            return;
        }

        _captureFrameCount++;

        if (!TryBuildJpegCrop(src, out byte[] jpegBytes))
        {
            return;
        }

        QueueInference(jpegBytes, "loop");
    }

    private void OnDestroy()
    {
        if (_webCamTexture != null)
        {
            if (_webCamTexture.isPlaying)
            {
                _webCamTexture.Stop();
            }

            Destroy(_webCamTexture);
            _webCamTexture = null;
        }

        if (_workingFrame != null)
        {
            Destroy(_workingFrame);
            _workingFrame = null;
        }

        if (_roiTexture != null)
        {
            Destroy(_roiTexture);
            _roiTexture = null;
        }
    }

    private void StartWebCam()
    {
        if (hololensCameraOnly && !IsCameraAllowedForCurrentRuntime())
        {
            Debug.LogWarning("[SignInferenceClient] Camera OFF: HoloLens-only camera mode is enabled. Skipping desktop webcam.");
            return;
        }

        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogWarning("[SignInferenceClient] No camera devices found. Set overrideSource.");
            return;
        }

        string dev = WebCamTexture.devices[0].name;
        Debug.Log("[SignInferenceClient] Camera ON: Starting WebCamTexture device: " + dev);
        _webCamTexture = new WebCamTexture(dev, 896, 504, 30);
        _webCamTexture.Play();
    }

    private bool IsCameraAllowedForCurrentRuntime()
    {
        if (IsRunningOnHoloLens()) return true;
#if UNITY_EDITOR
        return allowEditorDesktopCamera;
#else
        return false;
#endif
    }

    private static bool IsRunningOnHoloLens()
    {
#if UNITY_WSA && !UNITY_EDITOR
        return true;
#else
        return false;
#endif
    }

    private IEnumerator BeginCaptureOnLaunch()
    {
        if (useWebCamTexture && hololensCameraOnly && !IsCameraAllowedForCurrentRuntime() && overrideSource == null)
        {
            Debug.LogWarning("[SignInferenceClient] Waiting for HoloLens PV camera. Run this on-device to start sign capture.");
            yield break;
        }

        if (useWebCamTexture)
        {
            float deadline = Time.time + 3f;
            while ((_webCamTexture == null || !_webCamTexture.isPlaying || _webCamTexture.width <= 16) && Time.time < deadline)
            {
                yield return null;
            }

            if (_webCamTexture == null || !_webCamTexture.isPlaying || _webCamTexture.width <= 16)
            {
                Debug.LogWarning("[SignInferenceClient] Startup capture wait timed out. Camera not ready yet.");
            }
        }
        else
        {
            yield return null;
        }

        _nextRequestAt = 0f;
        _frameTickCounter = Mathf.Max(0, sendEveryNthFrame - 1);
        TryQueueInferenceNow();
    }

    private void TryQueueInferenceNow()
    {
        if (_requestInFlight && dropIfRequestInFlight)
        {
            return;
        }

        Texture src = GetActiveSourceTexture();
        if (src == null)
        {
            return;
        }

        if (!TryBuildJpegCrop(src, out byte[] jpegBytes))
        {
            return;
        }

        QueueInference(jpegBytes, "launch");
    }

    private void QueueInference(byte[] jpegBytes, string tag)
    {
        _sendAttemptCount++;
        _lastSendState = "Sending";
        _lastSendAt = Time.time;
        MaybeSaveDebugFrame(jpegBytes, tag);
        StartCoroutine(PostInfer(jpegBytes));
    }

    private Texture GetActiveSourceTexture()
    {
        if (overrideSource != null)
        {
            return overrideSource;
        }

        if (_webCamTexture != null && _webCamTexture.isPlaying && _webCamTexture.width > 16)
        {
            return _webCamTexture;
        }

        return null;
    }

    private bool TryBuildJpegCrop(Texture source, out byte[] jpegBytes)
    {
        jpegBytes = null;
        Texture2D readable = ToReadableTexture(source);
        if (readable == null || readable.width <= 0 || readable.height <= 0)
        {
            return false;
        }

        int cropSide = Mathf.RoundToInt(Mathf.Min(readable.width, readable.height) * centerCropScale);
        cropSide = Mathf.Clamp(cropSide, 8, Mathf.Min(readable.width, readable.height));
        int startX = (readable.width - cropSide) / 2;
        int startY = (readable.height - cropSide) / 2;

        Color[] croppedPixels = readable.GetPixels(startX, startY, cropSide, cropSide);
        _roiTexture.Reinitialize(targetSize, targetSize, TextureFormat.RGB24, false);
        _roiTexture.SetPixels(ScalePixelsBilinear(croppedPixels, cropSide, cropSide, targetSize, targetSize));
        _roiTexture.Apply(false, false);

        jpegBytes = _roiTexture.EncodeToJPG(jpegQuality);

        if (skipSimilarFrames && IsRoiTooSimilar(_roiTexture))
        {
            return false;
        }

        return jpegBytes != null && jpegBytes.Length > 0;
    }

    private bool IsRoiTooSimilar(Texture2D roi)
    {
        Color32[] cur = SampleTexture(roi, similaritySampleSize, similaritySampleSize);
        if (cur == null || cur.Length == 0)
        {
            return false;
        }

        if (_lastRoiSample == null || _lastRoiSample.Length != cur.Length)
        {
            _lastRoiSample = cur;
            return false;
        }

        float diff = MeanAbsRgbDiff(cur, _lastRoiSample);
        bool tooSimilar = diff < similarityThreshold;
        if (!tooSimilar)
        {
            _lastRoiSample = cur;
        }

        return tooSimilar;
    }

    private static Color32[] SampleTexture(Texture2D src, int sampleW, int sampleH)
    {
        if (src == null || src.width <= 0 || src.height <= 0)
        {
            return null;
        }

        Color32[] outPixels = new Color32[sampleW * sampleH];
        float xRatio = (src.width - 1f) / Mathf.Max(1, sampleW - 1);
        float yRatio = (src.height - 1f) / Mathf.Max(1, sampleH - 1);

        for (int y = 0; y < sampleH; y++)
        {
            int sy = Mathf.RoundToInt(y * yRatio);
            for (int x = 0; x < sampleW; x++)
            {
                int sx = Mathf.RoundToInt(x * xRatio);
                outPixels[y * sampleW + x] = src.GetPixel(sx, sy);
            }
        }

        return outPixels;
    }

    private static float MeanAbsRgbDiff(Color32[] a, Color32[] b)
    {
        int n = Mathf.Min(a.Length, b.Length);
        if (n <= 0)
        {
            return 1f;
        }

        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float dr = Mathf.Abs(a[i].r - b[i].r) / 255f;
            float dg = Mathf.Abs(a[i].g - b[i].g) / 255f;
            float db = Mathf.Abs(a[i].b - b[i].b) / 255f;
            sum += (dr + dg + db) / 3f;
        }

        return sum / n;
    }

    private Texture2D ToReadableTexture(Texture src)
    {
        if (src is Texture2D t2d)
        {
            return t2d;
        }

        int w = src.width;
        int h = src.height;
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        if (_workingFrame == null || _workingFrame.width != w || _workingFrame.height != h)
        {
            if (_workingFrame != null)
            {
                Destroy(_workingFrame);
            }

            _workingFrame = new Texture2D(w, h, TextureFormat.RGB24, false);
        }

        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(src, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        _workingFrame.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        _workingFrame.Apply(false, false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return _workingFrame;
    }

    private static Color[] ScalePixelsBilinear(Color[] src, int srcW, int srcH, int dstW, int dstH)
    {
        Color[] dst = new Color[dstW * dstH];
        float xRatio = (srcW - 1f) / Mathf.Max(1, dstW - 1);
        float yRatio = (srcH - 1f) / Mathf.Max(1, dstH - 1);

        for (int y = 0; y < dstH; y++)
        {
            float sy = y * yRatio;
            int y0 = Mathf.FloorToInt(sy);
            int y1 = Mathf.Min(y0 + 1, srcH - 1);
            float yLerp = sy - y0;

            for (int x = 0; x < dstW; x++)
            {
                float sx = x * xRatio;
                int x0 = Mathf.FloorToInt(sx);
                int x1 = Mathf.Min(x0 + 1, srcW - 1);
                float xLerp = sx - x0;

                Color c00 = src[y0 * srcW + x0];
                Color c10 = src[y0 * srcW + x1];
                Color c01 = src[y1 * srcW + x0];
                Color c11 = src[y1 * srcW + x1];

                Color c0 = Color.Lerp(c00, c10, xLerp);
                Color c1 = Color.Lerp(c01, c11, xLerp);
                dst[y * dstW + x] = Color.Lerp(c0, c1, yLerp);
            }
        }

        return dst;
    }

    private IEnumerator PostInfer(byte[] jpegBytes)
    {
        _requestInFlight = true;

        string url = TrimTrailingSlash(baseUrl) + "/infer";
        if (!_loggedFirstRequest)
        {
            _loggedFirstRequest = true;
            Debug.Log("[SignInferenceClient] Sending first inference request to " + url);
        }
        List<IMultipartFormSection> form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("image", jpegBytes, "frame.jpg", "image/jpeg")
        };

        if (spell)
        {
            form.Add(new MultipartFormDataSection("spell", "true"));
            form.Add(new MultipartFormDataSection("session_id", sessionId));
        }

        if (stableFrames > 0) form.Add(new MultipartFormDataSection("stable_frames", stableFrames.ToString()));
        if (pauseMs > 0) form.Add(new MultipartFormDataSection("pause_ms", pauseMs.ToString()));
        if (minConf > 0f) form.Add(new MultipartFormDataSection("min_conf", minConf.ToString("0.###")));
        if (noiseFrames > 0) form.Add(new MultipartFormDataSection("noise_frames", noiseFrames.ToString()));
        if (wordPauseMs > 0) form.Add(new MultipartFormDataSection("word_pause_ms", wordPauseMs.ToString()));

        using (UnityWebRequest req = UnityWebRequest.Post(url, form))
        {
            req.timeout = Mathf.RoundToInt(requestTimeoutSeconds);
            UnityWebRequestAsyncOperation op = null;
            try
            {
                op = req.SendWebRequest();
            }
            catch (InvalidOperationException ex)
            {
                _lastSendState = "HTTP blocked";
                string err =
                    "HTTP blocked by Unity Player setting. Set Player > Other Settings > Allow downloads over HTTP = Always allowed. " +
                    ex.Message;
                Debug.LogError("[SignInferenceClient] " + err);
                OnNetworkError?.Invoke(err);
                _requestInFlight = false;
                yield break;
            }

            yield return op;

            if (req.result != UnityWebRequest.Result.Success)
            {
                string err = $"infer failed: {req.error}";
                Debug.LogWarning("[SignInferenceClient] " + err);
                _lastSendState = "Send failed";
                OnNetworkError?.Invoke(err);
            }
            else
            {
                _sendSuccessCount++;
                _lastSendState = "Send ok";
                string json = req.downloadHandler.text;
                InferResponse response = null;
                try
                {
                    response = JsonUtility.FromJson<InferResponse>(json);
                }
                catch (Exception e)
                {
                    _lastSendState = "Bad JSON";
                    OnNetworkError?.Invoke("json parse failed: " + e.Message);
                }

                if (response != null)
                {
                    if (!_loggedFirstSuccess)
                    {
                        _loggedFirstSuccess = true;
                        Debug.Log("[SignInferenceClient] First inference response received.");
                    }
                    HandleInferResponse(response);
                    OnInferResponse?.Invoke(response);
                }
            }
        }

        _requestInFlight = false;
    }

    private void HandleInferResponse(InferResponse response)
    {
        if (!_hasShownWorkingStatus && statusHintText != null)
        {
            statusHintText.text = WorkingStatusText;
            _hasShownWorkingStatus = true;
        }

        if (response.confidence >= confidenceThreshold && !string.IsNullOrEmpty(response.letter))
        {
            _pendingLetter = response.letter;
            _nextUiApplyAt = Time.time + uiDebounceSeconds;
        }

        if (useServerTextAsAuthoritative && !string.IsNullOrEmpty(response.text))
        {
            if (!string.Equals(_lastServerText, response.text, StringComparison.Ordinal))
            {
                _lastServerText = response.text;
                if (serverText != null)
                {
                    serverText.text = _lastServerText;
                }
            }
        }

        if (statusHintText != null && !string.IsNullOrEmpty(response.status_hint))
        {
            statusHintText.text = response.status_hint;
        }
    }

    private void UpdateStatusHint()
    {
        string source;
        if (overrideSource != null)
        {
            source = "Override";
        }
        else if (_webCamTexture != null && _webCamTexture.isPlaying && _webCamTexture.width > 16)
        {
            source = "HoloLensCam";
        }
        else
        {
            source = "NoCamera";
        }

        string lastAgo = _lastSendAt < 0f
            ? "-"
            : TimeSpan.FromSeconds(Mathf.Max(0f, Time.time - _lastSendAt)).ToString(@"ss\.f") + "s ago";

        string statusLine =
            $"Source:{source} Captured:{_captureFrameCount} Sent:{_sendAttemptCount}/{_sendSuccessCount} Last:{_lastSendState} ({lastAgo})";

        SetStatusText(statusLine);
    }

    private void SetStatusText(string statusLine)
    {
        Text target = statusHintText != null ? statusHintText : (serverText != null ? serverText : letterText);
        if (target != null)
        {
            target.text = statusLine;
            _lastRenderedStatus = statusLine;
            return;
        }

        if (_lastRenderedStatus != statusLine || Time.time >= _nextStatusLogAt)
        {
            Debug.Log("[SignInferenceClient] " + statusLine + " (Assign statusHintText/serverText/letterText to show on-screen)");
            _lastRenderedStatus = statusLine;
            _nextStatusLogAt = Time.time + 2f;
        }
    }

    private void MaybeSaveDebugFrame(byte[] jpegBytes, string tag)
    {
        if (!saveDebugFrames || jpegBytes == null || jpegBytes.Length == 0) return;
        if ((_sendAttemptCount % saveEveryNSends) != 0) return;

        try
        {
            if (!Directory.Exists(_debugFrameDir))
            {
                Directory.CreateDirectory(_debugFrameDir);
            }

            string path = Path.Combine(_debugFrameDir, $"frame_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{tag}.jpg");
            File.WriteAllBytes(path, jpegBytes);
            Debug.Log("[SignInferenceClient] Saved debug frame: " + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SignInferenceClient] Could not save debug frame: " + e.Message);
        }
    }

    public void SpellCommit() => StartCoroutine(PostSpellCommand("/spell/commit"));
    public void SpellSpace() => StartCoroutine(PostSpellCommand("/spell/space"));
    public void SpellBackspace() => StartCoroutine(PostSpellCommand("/spell/backspace"));
    public void SpellClear() => StartCoroutine(PostSpellCommand("/spell/clear"));

    private IEnumerator PostSpellCommand(string path)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N");
        }

        string url = TrimTrailingSlash(baseUrl) + path;
        List<IMultipartFormSection> form = new List<IMultipartFormSection>
        {
            new MultipartFormDataSection("session_id", sessionId)
        };

        using (UnityWebRequest req = UnityWebRequest.Post(url, form))
        {
            req.timeout = Mathf.RoundToInt(requestTimeoutSeconds);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string err = $"{path} failed: {req.error}";
                Debug.LogWarning("[SignInferenceClient] " + err);
                OnNetworkError?.Invoke(err);
            }
        }
    }

    private static string TrimTrailingSlash(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        while (s.EndsWith("/", StringComparison.Ordinal))
        {
            s = s.Substring(0, s.Length - 1);
        }

        return s;
    }
}
