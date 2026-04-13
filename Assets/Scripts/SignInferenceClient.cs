using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.UIElements;

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
/// - Crops ROI on-device: optional hand ROI (OpenXR hands + AR Foundation PV projection) or center crop
/// - Resizes to 224x224
/// - JPEG-encodes and POSTs multipart form-data to /infer (field name <c>image</c>; filename <c>hand.jpg</c> in hand ROI mode)
/// - Parses JSON and updates UI with debounce/confidence threshold
/// - Exposes optional spell endpoint methods for UI buttons
/// </summary>
[DefaultExecutionOrder(-40)]
public class SignInferenceClient : MonoBehaviour
{
    [Header("API")]
    [Tooltip("If true, Awake sets the URL for this platform (Editor/PC: 127.0.0.1; UWP device: LAN IP). Turn off to use baseUrl from the inspector.")]
    [SerializeField] private bool usePlatformDefaultApiUrl = true;
    [Tooltip("Overridden at runtime when usePlatformDefaultApiUrl is true. HoloLens: use your PC Wi‑Fi IP if it changes.")]
    [SerializeField] private string baseUrl = "http://127.0.0.1:8010";
    [SerializeField] private bool spell = true;
    [SerializeField] private string sessionId = "";
    [SerializeField] private float requestTimeoutSeconds = 4f;

    [Header("Capture")]
    [Tooltip("If enabled and available, uses WebCamTexture (PV camera) as source.")]
    [SerializeField] private bool useWebCamTexture = true;
    [Tooltip("If true, only allow WebCamTexture capture on HoloLens device builds (never desktop editor/webcam).")]
    [SerializeField] private bool hololensCameraOnly = true;
    [Tooltip("Editor-only debug: allow desktop webcam in Unity Editor while keeping device builds HoloLens-focused.")]
#pragma warning disable 0414 // Only read in UNITY_EDITOR branch of IsCameraAllowedForCurrentRuntime()
    [SerializeField] private bool allowEditorDesktopCamera = true;
#pragma warning restore 0414
    [Header("HoloLens camera")]
    [Tooltip("If WebCamTexture.Play fails (e.g. HRESULT 0xC00D3EA3 — camera preempted), retry this many times.")]
    [SerializeField] private int webCamStartMaxAttempts = 6;
    [SerializeField] private float webCamRetryDelaySeconds = 1.5f;
    [Tooltip("Optional texture source if not using WebCamTexture (e.g. RenderTexture converted elsewhere).")]
    [SerializeField] private Texture overrideSource;
    [SerializeField] private int targetSize = 224;
    [SerializeField] private int jpegQuality = 70;
    [SerializeField, Range(0.25f, 1f)] private float centerCropScale = 0.65f;

    [Header("Hand ROI + PV (HoloLens 2)")]
    [Tooltip("Use OpenXR hand joints + AR Foundation locatable-camera projection (see SignLanguageHandRoiPipeline). When off, uses center crop.")]
    [SerializeField] private bool useHandRoiInference;
    [SerializeField] private SignLanguageHandRoiPipeline handRoiPipeline;
    [Tooltip("Multipart filename for /infer when hand ROI is used (spec: hand.jpg).")]
    [SerializeField] private string handRoiMultipartFileName = "hand.jpg";

    [Header("Rate control")]
    [Tooltip("If false, sign capture stays idle until enabled from UI/code.")]
    [SerializeField] private bool signCaptureActive = true;
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
    [Tooltip("If no legacy Text is assigned, write status to UI Toolkit label 'subtitle-text'.")]
    [SerializeField] private bool useSubtitleLabelFallback = true;
    [SerializeField] private float confidenceThreshold = 0.5f;
    [SerializeField] private float uiDebounceSeconds = 0.12f;
    [SerializeField] private bool useServerTextAsAuthoritative = true;

    [Header("Debug")]
    [Tooltip("If enabled, saves occasional captured ROI JPGs to persistentDataPath/sign_debug.")]
    [SerializeField] private bool saveDebugFrames = false;
    [Tooltip("Save one debug frame every N send attempts.")]
    [SerializeField] private int saveEveryNSends = 20;
    [Tooltip("If enabled, saves the first sent inference JPEGs exactly as posted to /infer.")]
    [SerializeField] private bool saveFirstSentFrames = true;
    [Tooltip("How many sent inference frames to save for visual inspection.")]
    [SerializeField] private int saveFirstSentFramesCount = 30;

    private WebCamTexture _webCamTexture;
    private Coroutine _webCamBootstrapCo;
    /// <summary>User-facing line when PV camera cannot start (preemption, permissions, etc.).</summary>
    private string _cameraUserMessage = "";
    private Texture2D _workingFrame;
    private Texture2D _handCropReadback;
    private Texture2D _roiTexture;
    private bool _requestInFlight;
    private float _nextRequestAt;
    private string _pendingLetter;
    private string _lastAppliedLetter;
    private float _nextUiApplyAt;
    private string _lastServerText;
    private Color32[] _lastRoiSample;
    private int _frameTickCounter;
    private bool _loggedFirstRequest;
    private bool _loggedFirstSuccess;
    private int _captureFrameCount;
    private int _sendAttemptCount;
    private int _sendSuccessCount;
    private int _skippedHandRoiFrames;
    private int _handRoiLogCounter;
    private bool _warnedMissingHandPipeline;
    private string _lastMultipartFileName = "frame.jpg";
    private string _lastSendState = "Idle";
    private float _lastSendAt = -1f;
    private string _debugFrameDir;
    private string _sentFramesDir;
    private int _savedSentFramesCount;
    private bool _loggedSentFramesTargetPath;
    private Label _subtitleLabel;
    private Label _mainHudCaptionLabel;
    private string _inferCaptionLine = "";
    private bool _applicationIsQuitting;

    /// <summary>Same string as the on-screen sign caption (letter, spell text, hint). <see cref="XRDebugLogger"/> reads this for <c>xr-debug-hud</c> — never send/capture counters.</summary>
    public static string LiveCaptionForHud { get; private set; } = "";

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
        if (usePlatformDefaultApiUrl)
        {
            ApplyPlatformDefaultBaseUrl();
        }

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
        saveFirstSentFramesCount = Mathf.Max(0, saveFirstSentFramesCount);
        webCamStartMaxAttempts = Mathf.Max(1, webCamStartMaxAttempts);
        webCamRetryDelaySeconds = Mathf.Max(0.25f, webCamRetryDelaySeconds);

        _roiTexture = new Texture2D(targetSize, targetSize, TextureFormat.RGB24, false);
        _debugFrameDir = Path.Combine(Application.persistentDataPath, "sign_debug");
        _sentFramesDir = ResolveSentFramesDirectory();
    }

    private void ApplyPlatformDefaultBaseUrl()
    {
#if UNITY_EDITOR
        baseUrl = "http://127.0.0.1:8010";
#elif UNITY_WSA && !UNITY_EDITOR
        baseUrl = "http://172.16.23.67:8010";
#else
        baseUrl = "http://127.0.0.1:8010";
#endif
    }

    private static void SetLiveCaptionForHud(string line)
    {
        LiveCaptionForHud = line ?? "";
    }

    private void Start()
    {
        if (useSubtitleLabelFallback)
        {
            StartCoroutine(BindToolkitCaptionLabelsWhenReady());
        }

        if (signCaptureActive && useWebCamTexture)
        {
            RequestWebCamStart();
        }

        if (signCaptureActive && startCapturingOnLaunch)
        {
            StartCoroutine(BeginCaptureOnLaunch());
        }
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused || _applicationIsQuitting)
        {
            return;
        }

        if (signCaptureActive && useWebCamTexture)
        {
            RequestWebCamStart();
        }
    }

    private void OnApplicationQuit()
    {
        _applicationIsQuitting = true;
        _requestInFlight = false;
        StopWebCamBootstrap();
        StopAllCoroutines();
        SetLiveCaptionForHud("");
        StopCameraCapture();
    }

    private void Update()
    {
        if (_applicationIsQuitting)
        {
            return;
        }

        if (!signCaptureActive)
        {
            UpdateIdleStatusHint();
            return;
        }

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

        if (!TryBuildJpegForInference(src, out byte[] jpegBytes))
        {
            return;
        }

        QueueInference(jpegBytes, "loop");
    }

    public void SetSignCaptureActive(bool active)
    {
        if (signCaptureActive == active) return;
        signCaptureActive = active;

        if (signCaptureActive)
        {
            if (useWebCamTexture)
            {
                RequestWebCamStart();
            }
            if (startCapturingOnLaunch)
            {
                StartCoroutine(BeginCaptureOnLaunch());
            }
        }
        else
        {
            StopWebCamBootstrap();
            StopCameraCapture();
            _requestInFlight = false;
            _lastSendState = "Idle";
            _inferCaptionLine = "";
            _cameraUserMessage = "";
            SetLiveCaptionForHud("");
            UpdateIdleStatusHint();
        }
    }

    private void OnDestroy()
    {
        _applicationIsQuitting = true;
        StopWebCamBootstrap();
        StopCameraCapture();

        if (_workingFrame != null)
        {
            Destroy(_workingFrame);
            _workingFrame = null;
        }

        if (_handCropReadback != null)
        {
            Destroy(_handCropReadback);
            _handCropReadback = null;
        }

        if (_roiTexture != null)
        {
            Destroy(_roiTexture);
            _roiTexture = null;
        }
    }

    private void StopWebCamBootstrap()
    {
        if (_webCamBootstrapCo != null)
        {
            StopCoroutine(_webCamBootstrapCo);
            _webCamBootstrapCo = null;
        }
    }

    /// <summary>
    /// Starts (or restarts) PV/webcam with retries. On HoloLens, 0xC00D3EA3 usually means MRC, Device Portal stream, or another immersive app holds the camera.
    /// </summary>
    private void RequestWebCamStart()
    {
        if (!useWebCamTexture || !signCaptureActive || _applicationIsQuitting)
        {
            return;
        }

        if (hololensCameraOnly && !IsCameraAllowedForCurrentRuntime())
        {
            Debug.LogWarning("[SignInferenceClient] Camera OFF: HoloLens-only camera mode is enabled. Skipping desktop webcam.");
            return;
        }

        StopWebCamBootstrap();
        _cameraUserMessage = "";
        _webCamBootstrapCo = StartCoroutine(CoBootstrapWebCam());
    }

    private IEnumerator CoBootstrapWebCam()
    {
        for (int attempt = 0; attempt < webCamStartMaxAttempts; attempt++)
        {
            if (!signCaptureActive || _applicationIsQuitting)
            {
                break;
            }

            StopCameraCapture();
            yield return null;

            if (WebCamTexture.devices.Length == 0)
            {
                _cameraUserMessage = "No camera device found. Assign overrideSource or check device permissions.";
                Debug.LogWarning("[SignInferenceClient] No camera devices found. Set overrideSource.");
                ApplyCaptionToSubtitle();
                break;
            }

            string dev = WebCamTexture.devices[0].name;
            Debug.Log($"[SignInferenceClient] WebCam attempt {attempt + 1}/{webCamStartMaxAttempts}: {dev}");

            _webCamTexture = new WebCamTexture(dev, 896, 504, 30);
            bool playThrew = false;
            Exception playEx = null;
            try
            {
                _webCamTexture.Play();
            }
            catch (Exception ex)
            {
                playThrew = true;
                playEx = ex;
            }

            if (playThrew)
            {
                Debug.LogWarning("[SignInferenceClient] WebCamTexture.Play: " + (playEx != null ? playEx.Message : ""));
                _cameraUserMessage = CameraErrorToUserMessage(playEx != null ? playEx.Message : "");
                ApplyCaptionToSubtitle();
                StopCameraCapture();
                yield return new WaitForSecondsRealtime(webCamRetryDelaySeconds);
                continue;
            }

            // Note: WebCamTexture.error exists only on newer Unity; poll isPlaying + width instead.
            float deadline = Time.time + 5f;
            while (Time.time < deadline && signCaptureActive && !_applicationIsQuitting)
            {
                if (_webCamTexture == null)
                {
                    break;
                }

                if (_webCamTexture.isPlaying && _webCamTexture.width > 16)
                {
                    _cameraUserMessage = "";
                    Debug.Log("[SignInferenceClient] Camera streaming: " + dev);
                    _webCamBootstrapCo = null;
                    ApplyCaptionToSubtitle();
                    yield break;
                }

                yield return null;
            }

            _cameraUserMessage =
                "Camera did not start in time. Another app may be using the PV camera — close Mixed Reality Capture, Device Portal live view, or other immersive apps, then wait or restart this app.";
            ApplyCaptionToSubtitle();
            StopCameraCapture();
            yield return new WaitForSecondsRealtime(webCamRetryDelaySeconds);
        }

        _webCamBootstrapCo = null;
        if (string.IsNullOrEmpty(_cameraUserMessage))
        {
            _cameraUserMessage =
                "Could not open camera after retries. Close apps using the camera (MRC, Device Portal preview) and try again.";
            ApplyCaptionToSubtitle();
        }
    }

    private static string CameraErrorToUserMessage(string error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return "Camera failed to start.";
        }

        if (error.IndexOf("C00D3EA3", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("preempted", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return
                "Camera busy (another immersive app is using it). Close Mixed Reality Capture, Device Portal live camera, or other XR apps; then use Sign again or restart.";
        }

        return "Camera: " + error;
    }

    private void StopCameraCapture()
    {
        if (_webCamTexture == null) return;
        try
        {
            if (_webCamTexture.isPlaying)
            {
                _webCamTexture.Stop();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SignInferenceClient] WebCamTexture.Stop: " + ex.Message);
        }

        try
        {
            Destroy(_webCamTexture);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SignInferenceClient] WebCamTexture Destroy: " + ex.Message);
        }

        _webCamTexture = null;
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
            float deadline = Time.time + 12f;
            while (GetActiveSourceTexture() == null && Time.time < deadline)
            {
                yield return null;
            }

            if (GetActiveSourceTexture() == null)
            {
                Debug.LogWarning("[SignInferenceClient] Startup: camera not ready after wait (see on-screen camera message if preempted).");
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

        if (!TryBuildJpegForInference(src, out byte[] jpegBytes))
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
        _lastMultipartFileName = useHandRoiInference && handRoiPipeline != null
            ? (string.IsNullOrEmpty(handRoiMultipartFileName) ? "hand.jpg" : handRoiMultipartFileName)
            : "frame.jpg";
        MaybeSaveDebugFrame(jpegBytes, tag);
        MaybeSaveFirstSentFrame(jpegBytes, tag);
        MaybeLogHandRoiStats(jpegBytes);
        StartCoroutine(PostInfer(jpegBytes));
    }

    private void MaybeLogHandRoiStats(byte[] jpegBytes)
    {
        if (!useHandRoiInference || handRoiPipeline == null)
        {
            return;
        }

        _handRoiLogCounter++;
        if ((_handRoiLogCounter % 120) != 0)
        {
            return;
        }

        RectInt roi = handRoiPipeline.LastRoi;
        int jpg = jpegBytes != null ? jpegBytes.Length : 0;
        Debug.Log(
            $"[SignInferenceClient] Hand ROI: {roi.width}x{roi.height} px, JPEG ~{jpg} bytes, skipped (no valid ROI) frames ~{_skippedHandRoiFrames} (cumulative).");
    }

    private bool TryBuildJpegForInference(Texture source, out byte[] jpegBytes)
    {
        if (useHandRoiInference)
        {
            if (handRoiPipeline == null)
            {
                if (!_warnedMissingHandPipeline)
                {
                    _warnedMissingHandPipeline = true;
                    Debug.LogWarning(
                        "[SignInferenceClient] useHandRoiInference is enabled but handRoiPipeline is not assigned; inference JPEGs are skipped. Assign SignLanguageHandRoiPipeline or disable useHandRoiInference.");
                }

                jpegBytes = null;
                return false;
            }

            return TryBuildJpegHandRoi(source, out jpegBytes);
        }

        return TryBuildJpegCrop(source, out jpegBytes);
    }

    /// <summary>
    /// Crops the PV/WebCam texture to the hand bounding box (padded), resizes to <see cref="targetSize"/>, JPEG-encodes.
    /// </summary>
    private bool TryBuildJpegHandRoi(Texture source, out byte[] jpegBytes)
    {
        jpegBytes = null;
        handRoiPipeline.SetPvTextureDimensions(source.width, source.height);
        if (!handRoiPipeline.TryGetHandRoiInPvPixels(out RectInt roi, out _))
        {
            _skippedHandRoiFrames++;
            return false;
        }

        int rw = roi.width;
        int rh = roi.height;
        if (rw <= 0 || rh <= 0)
        {
            return false;
        }

        var scale = new Vector2(rw / (float)source.width, rh / (float)source.height);
        var offset = new Vector2(roi.x / (float)source.width, roi.y / (float)source.height);

        RenderTexture rt = RenderTexture.GetTemporary(rw, rh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(source, rt, scale, offset);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;

        if (_handCropReadback == null || _handCropReadback.width != rw || _handCropReadback.height != rh)
        {
            if (_handCropReadback != null)
            {
                Destroy(_handCropReadback);
            }

            _handCropReadback = new Texture2D(rw, rh, TextureFormat.RGB24, false);
        }

        _handCropReadback.ReadPixels(new Rect(0, 0, rw, rh), 0, 0);
        _handCropReadback.Apply(false, false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        Color[] croppedPixels = _handCropReadback.GetPixels();
        _roiTexture.Reinitialize(targetSize, targetSize, TextureFormat.RGB24, false);
        _roiTexture.SetPixels(ScalePixelsBilinear(croppedPixels, rw, rh, targetSize, targetSize));
        _roiTexture.Apply(false, false);

        jpegBytes = _roiTexture.EncodeToJPG(jpegQuality);

        if (skipSimilarFrames && IsRoiTooSimilar(_roiTexture))
        {
            return false;
        }

        return jpegBytes != null && jpegBytes.Length > 0;
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
            new MultipartFormFileSection("image", jpegBytes, _lastMultipartFileName ?? "frame.jpg", "image/jpeg")
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

            if (_applicationIsQuitting)
            {
                _requestInFlight = false;
                yield break;
            }

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
                if (!TryParseInferResponse(json, out InferResponse response, out string parseErr))
                {
                    _lastSendState = "Bad JSON";
                    if (!string.IsNullOrEmpty(parseErr))
                    {
                        OnNetworkError?.Invoke(parseErr);
                    }
                }
                else
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
        _inferCaptionLine = FormatInferCaption(response);
        ApplyCaptionToSubtitle();

        if (response.confidence >= confidenceThreshold && !string.IsNullOrEmpty(response.letter))
        {
            _pendingLetter = response.letter;
            _nextUiApplyAt = Time.time + uiDebounceSeconds;
        }

        bool captionUsesToolkit =
            statusHintText != null || _subtitleLabel != null || _mainHudCaptionLabel != null;

        if (useServerTextAsAuthoritative && !string.IsNullOrEmpty(response.text))
        {
            if (!string.Equals(_lastServerText, response.text, StringComparison.Ordinal))
            {
                _lastServerText = response.text;
                if (serverText != null && !captionUsesToolkit)
                {
                    serverText.text = _lastServerText;
                }
            }
        }
    }

    private void UpdateStatusHint()
    {
        ApplyCaptionToSubtitle();
    }

    private void UpdateIdleStatusHint()
    {
        SetLiveCaptionForHud("");
        // Keep idle state silent so it does not overwrite ASR/transcription captions.
    }

    private void ApplyCaptionToSubtitle()
    {
        string caption;
        if (!string.IsNullOrEmpty(_inferCaptionLine))
        {
            caption = _inferCaptionLine;
        }
        else if (signCaptureActive && useWebCamTexture && !string.IsNullOrEmpty(_cameraUserMessage))
        {
            caption = _cameraUserMessage;
        }
        else if (GetActiveSourceTexture() == null)
        {
            caption = "Sign: camera not ready — check permissions or HoloLens PV.";
        }
        else
        {
            caption = "Sign: waiting for API…";
        }

        // Update every bound outlet (do not return early — main HUD caption must not be skipped when subtitle-text exists).
        if (statusHintText != null)
        {
            statusHintText.text = caption;
        }

        if (_subtitleLabel != null)
        {
            _subtitleLabel.text = caption;
            _subtitleLabel.style.display = DisplayStyle.Flex;
        }

        if (serverText != null)
        {
            serverText.text = caption;
        }

        if (_mainHudCaptionLabel != null)
        {
            _mainHudCaptionLabel.text = caption;
            _mainHudCaptionLabel.style.display = DisplayStyle.Flex;
        }

        SetLiveCaptionForHud(caption);
    }

    /// <summary>
    /// JsonUtility often fails on BOM-prefixed bodies or slightly non-standard JSON; fall back to field extraction.
    /// </summary>
    private static bool TryParseInferResponse(string raw, out InferResponse response, out string error)
    {
        response = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "empty /infer body";
            return false;
        }

        string json = ExtractJsonObject(raw.Trim());
        if (json.Length > 0 && json[0] == '\uFEFF')
        {
            json = json.Substring(1);
        }

        InferResponse r = null;
        try
        {
            r = JsonUtility.FromJson<InferResponse>(json);
        }
        catch (Exception ex)
        {
            error = "json parse failed: " + ex.Message;
        }

        if (r != null && InferResponseHasContent(r))
        {
            response = r;
            return true;
        }

        InferResponse manual = new InferResponse();
        manual.letter = ReadJsonStringField(json, "letter");
        manual.text = ReadJsonStringField(json, "text");
        manual.status_hint = ReadJsonStringField(json, "status_hint");
        manual.model = ReadJsonStringField(json, "model");
        string confStr = ReadJsonNumberField(json, "confidence");
        if (!string.IsNullOrEmpty(confStr)
            && float.TryParse(confStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float cf))
        {
            manual.confidence = cf;
        }

        if (InferResponseHasContent(manual))
        {
            response = manual;
            return true;
        }

        error = error ?? "could not read letter/text/status_hint from /infer JSON";
        return false;
    }

    private static bool InferResponseHasContent(InferResponse r)
    {
        if (r == null) return false;
        return !string.IsNullOrEmpty(r.letter)
            || !string.IsNullOrEmpty(r.text)
            || !string.IsNullOrEmpty(r.status_hint);
    }

    private static string ExtractJsonObject(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        int a = s.IndexOf('{');
        int b = s.LastIndexOf('}');
        if (a >= 0 && b > a)
        {
            return s.Substring(a, b - a + 1);
        }

        return s.Trim();
    }

    private static string ReadJsonStringField(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        i = json.IndexOf(':', i);
        if (i < 0) return null;
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i]))
        {
            i++;
        }

        if (i >= json.Length || json[i] != '"')
        {
            return null;
        }

        i++;
        var sb = new StringBuilder();
        while (i < json.Length)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                char e = json[i + 1];
                if (e == '"' || e == '\\')
                {
                    sb.Append(e);
                    i += 2;
                    continue;
                }

                if (e == 'n')
                {
                    sb.Append('\n');
                    i += 2;
                    continue;
                }
            }

            if (c == '"')
            {
                break;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string ReadJsonNumberField(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        i = json.IndexOf(':', i);
        if (i < 0) return null;
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i]))
        {
            i++;
        }

        int start = i;
        while (i < json.Length)
        {
            char c = json[i];
            if (char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E')
            {
                i++;
                continue;
            }

            break;
        }

        if (i == start) return null;
        return json.Substring(start, i - start);
    }

    private static string FormatInferCaption(InferResponse r)
    {
        if (r == null)
        {
            return "";
        }

        var parts = new List<string>();
        // Spell / sentence buffer is usually what users want to read first.
        if (!string.IsNullOrEmpty(r.text))
        {
            parts.Add(r.text.Trim());
        }

        if (!string.IsNullOrEmpty(r.letter))
        {
            parts.Add($"{r.letter} ({Mathf.Clamp01(r.confidence):P0})");
        }

        if (!string.IsNullOrEmpty(r.status_hint))
        {
            parts.Add(r.status_hint);
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "";
    }

    private IEnumerator BindToolkitCaptionLabelsWhenReady()
    {
        float deadline = Time.time + 8f;
        while (Time.time < deadline)
        {
            UIDocument[] docs = FindObjectsOfType<UIDocument>();
            for (int i = 0; i < docs.Length; i++)
            {
                var root = docs[i] != null ? docs[i].rootVisualElement : null;
                if (root == null) continue;
                if (_subtitleLabel == null)
                {
                    Label sub = root.Q<Label>("subtitle-text");
                    if (sub != null)
                    {
                        _subtitleLabel = sub;
                        Debug.Log("[SignInferenceClient] Bound subtitle-text for sign captions.");
                    }
                }

                if (_mainHudCaptionLabel == null)
                {
                    Label mainCap = root.Q<Label>("sign-inference-caption");
                    if (mainCap != null)
                    {
                        _mainHudCaptionLabel = mainCap;
                        Debug.Log("[SignInferenceClient] Bound sign-inference-caption on MainLayout.");
                    }
                }
            }

            if (_subtitleLabel != null && _mainHudCaptionLabel != null)
            {
                ApplyCaptionToSubtitle();
                yield break;
            }

            yield return null;
        }

        ApplyCaptionToSubtitle();
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

    private void MaybeSaveFirstSentFrame(byte[] jpegBytes, string tag)
    {
        if (!saveFirstSentFrames || saveFirstSentFramesCount <= 0 || jpegBytes == null || jpegBytes.Length == 0)
        {
            return;
        }

        if (_savedSentFramesCount >= saveFirstSentFramesCount)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(_sentFramesDir))
            {
                Directory.CreateDirectory(_sentFramesDir);
            }

            _savedSentFramesCount++;
            string path = Path.Combine(
                _sentFramesDir,
                $"infer_sent_{_savedSentFramesCount:D2}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{tag}.jpg");
            File.WriteAllBytes(path, jpegBytes);

            if (!_loggedSentFramesTargetPath)
            {
                _loggedSentFramesTargetPath = true;
                Debug.Log("[SignInferenceClient] Saving first sent inference frames to: " + _sentFramesDir);
            }

            if (_savedSentFramesCount == saveFirstSentFramesCount)
            {
                Debug.Log("[SignInferenceClient] Saved first " + saveFirstSentFramesCount + " sent inference frames.");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SignInferenceClient] Could not save sent inference frame: " + e.Message);
        }
    }

    private static string ResolveSentFramesDirectory()
    {
#if UNITY_EDITOR
        // Project root while running in editor.
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(projectRoot, "sent_infer_frames");
#else
        // Device/runtime fallback.
        return Path.Combine(Application.persistentDataPath, "sent_infer_frames");
#endif
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
