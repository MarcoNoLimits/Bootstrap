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

/*
1. Memory cleanıng
2. do not show confıdence % 
3. better ui for no hand
4. no spacing 
5. after each word completed, the server should correct the word
*/


[Serializable]
public class InferResponse
{
    public string letter;
    public float confidence;
    public float top2_margin;
    public string detail;
    public string text;
    public string status_hint;
    public string model;
    /// <summary>Preferred backend flag for hand presence.</summary>
    public bool hand_detected;
    /// <summary>When true, server detected no hand.</summary>
    public bool no_hand;
}

/// <summary>
/// HoloLens inference client:
/// - Primary path: PV via <c>XRCpuImage</c> → optimized JPEG → POST raw bytes to backend <c>/predict</c> (<c>Content-Type: image/jpeg</c>).
/// - Legacy: WebCamTexture / HF Gradio Space / on-device hand ROI modes.
/// - Parses JSON (<c>predicted_letter</c>, <c>confidence</c>, <c>hand_detected</c>/<c>no_hand</c>) and updates UI.
/// </summary>
[DefaultExecutionOrder(-40)]
public class SignInferenceClient : MonoBehaviour
{
    private const string DebugSessionId = "729dee";

    /// <summary>Agent file log (NDJSON). Off on device — file I/O here caused noisy WinRT traces and is not needed for shipping.</summary>
    // Toggle only when debugging file I/O (kept non-const so DebugWrite is not always-unreachable / CS0162).
    private static bool AgentDebugFileLog;

    private static string DebugLogPath =>
        Path.Combine(Application.persistentDataPath, "debug-729dee.log");
    [Header("API")]
    [Tooltip("If true, Awake sets the Hugging Face Space runtime URL for this platform. Turn off to use baseUrl from the inspector.")]
    [SerializeField] private bool usePlatformDefaultApiUrl = true;
    [Tooltip("Overridden at runtime when usePlatformDefaultApiUrl is true.")]
    [SerializeField] private string baseUrl = "https://mederbekaiana-sign-language.hf.space";
    [SerializeField] private string sessionId = "";
    [SerializeField] private float requestTimeoutSeconds = 15f;

    [Header("PV CPU image pipeline (Health server)")]
    [Tooltip("Use AR Foundation XRCpuImage for HoloLens PV — no WebCamTexture; POST optimized JPEG frames to /predict.")]
    [SerializeField] private bool useXrCpuImagePipeline = true;
    [SerializeField] private HololensPvCpuImageSource pvCpuImageSource;
    [Tooltip("Path on baseUrl for raw JPEG POST (default /predict).")]
    [SerializeField] private string inferEndpointPath = "/predict";
    [Tooltip("Minimum time between send attempts in ms (150–200 ≈ 5–6 FPS). Used when CPU pipeline is on.")]
    [SerializeField] private float minSendIntervalMs = 175f;
    [Tooltip("Max width after resize/crop (default 640). Synced to HololensPvCpuImageSource on Start.")]
    [SerializeField] private int maxSendFrameWidth = 640;
    [Tooltip("Crop center region before resize (see HololensPvCpuImageSource).")]
    [SerializeField] private bool cpuPipelineCropCenter = false;
    [Tooltip("Fraction of min(w,h) when cropping center (CPU pipeline).")]
    [SerializeField, Range(0.2f, 1f)] private float cpuPipelineCenterCropFraction = 0.92f;

    [Header("HoloLens PV source (device only)")]
#pragma warning disable 0414 // Read only inside UNITY_WSA && !UNITY_EDITOR; Editor compile omits those branches (CS0414).
    [Tooltip(
        "If true: PV via WebCamTexture (legacy, use when Mirage/XRCpuImage fails). If false: AR Foundation XRCpuImage via HololensPvCpuImageSource (preferred; avoids black frames from ReadPixels-in-Update). Editor ignores this.")]
    [SerializeField] private bool hololensPvUseWebCamTexture = false;
#pragma warning restore 0414

    [Header("Capture")]
    [Tooltip("If enabled and available, uses WebCamTexture (PV camera) as source.")]
    [SerializeField] private bool useWebCamTexture = false;
    [Tooltip("If true, only allow WebCamTexture capture on HoloLens device builds (never desktop editor/webcam).")]
    [SerializeField] private bool hololensCameraOnly = true;
    [Tooltip("Editor-only debug: allow desktop webcam in Unity Editor while keeping device builds HoloLens-focused.")]
#pragma warning disable 0414 // Only read in UNITY_EDITOR branch of IsCameraAllowedForCurrentRuntime()
    [SerializeField] private bool allowEditorDesktopCamera = true;
#pragma warning restore 0414
    [Header("HoloLens camera")]
    [Tooltip("If WebCamTexture.Play fails (e.g. HRESULT 0xC00D3EA3 — camera preempted), retry this many times.")]
    [SerializeField] private int webCamStartMaxAttempts = 2;
    [SerializeField] private float webCamRetryDelaySeconds = 1.5f;
    [Tooltip("Optional texture source if not using WebCamTexture (e.g. RenderTexture converted elsewhere).")]
    [SerializeField] private Texture overrideSource;
    [SerializeField] private int targetSize = 224;
    [Tooltip(
        "WebCam/override texture path only: if false (recommended when the API resizes internally), JPEG keeps the camera aspect ratio and only scales down when the long edge exceeds maxSendFrameWidth. If true, legacy behavior warps the full frame to targetSize × targetSize (extra resize + wrong aspect).")]
    [SerializeField] private bool warpFullFrameToSquareBeforeUpload = false;
    [SerializeField] private int jpegQuality = 88;
    [SerializeField, Range(0.25f, 1f)] private float centerCropScale = 1f;

    [Header("Hand ROI + PV (HoloLens 2)")]
    [Tooltip("Use OpenXR hand joints + AR Foundation locatable-camera projection (see SignLanguageHandRoiPipeline). When off, uses center crop.")]
    [SerializeField] private bool useHandRoiInference = false;
    [SerializeField] private SignLanguageHandRoiPipeline handRoiPipeline;
    [Tooltip("Multipart filename when hand ROI + HF multipart path is used (spec: hand.jpg).")]
    [SerializeField] private string handRoiMultipartFileName = "hand.jpg";

    [Header("Rate control")]
    [Tooltip("When enabled, force strict low-latency path: XRCpuImage + /predict + single in-flight + no legacy capture paths.")]
    [SerializeField] private bool strictLowLatencyMode = true;
    [Tooltip("Stage 1 test mode: never send network inference requests (ROI/debug only).")]
    [SerializeField] private bool stage1Only = false;
    [Tooltip("Convenience mode for testing Stage A+B together with recommended defaults.")]
    [SerializeField] private bool combinedABMode = true;
    [Tooltip("If false, sign capture stays idle until enabled from UI/code.")]
    [SerializeField] private bool signCaptureActive = false;
    [Tooltip("Inference requests per second, independent from camera FPS.")]
    [SerializeField] private float requestFps = 5f;
    [Tooltip("When enabled, sends the first inference request as soon as startup capture is ready.")]
    [SerializeField] private bool startCapturingOnLaunch = false;
    [Tooltip("Delay before first startup capture request to reduce scene-load contention on device.")]
    [SerializeField] private float startupCaptureDelaySeconds = 1.25f;
    [Tooltip("Skip frame while one request is running.")]
    [SerializeField] private bool dropIfRequestInFlight = true;
    [Tooltip("Only attempt inference every Nth update tick (1 = every tick).")]
    [SerializeField] private int sendEveryNthFrame = 1;
    [Tooltip("Write cadence/latency/drop logs every N send attempts.")]
    [SerializeField] private int logEveryNSendAttempts = 20;

    [Header("Optional change gating")]
    [Tooltip("If enabled, skips sending when ROI is very similar to last sent ROI.")]
    [SerializeField] private bool skipSimilarFrames = false;
    [Tooltip("Lower = stricter change required. 0.02-0.08 is a practical range.")]
    [SerializeField, Range(0.001f, 0.2f)] private float similarityThreshold = 0.04f;
    [Tooltip("How aggressively to downsample before similarity check.")]
    [SerializeField] private int similaritySampleSize = 16;

    [Header("Model tuning (optional fields)")]
#pragma warning disable 0414 // Serialized for inspector compatibility; reserved for upcoming backend tuning hooks.
    [SerializeField] private int stableFrames = 0;
    [SerializeField] private int pauseMs = 0;
    [SerializeField] private float minConf = 0f;
    [SerializeField] private int noiseFrames = 0;
    [SerializeField] private int wordPauseMs = 0;
#pragma warning restore 0414

    [Header("UI & Captions")]
    [SerializeField] private Text letterText;
    [SerializeField] private Text serverText;
    [SerializeField] private Text statusHintText;
    [Tooltip("If no legacy Text is assigned, write status to UI Toolkit label 'subtitle-text'.")]
    [SerializeField] private bool useSubtitleLabelFallback = true;
    [SerializeField] private float confidenceThreshold = 0.55f;
    [SerializeField] private float minTop2Margin = 0.12f;
    [SerializeField] private int commitStableFrames = 3;
    [Tooltip("Fast path stability when confidence is strong (e.g., 2/3) to reduce perceived latency.")]
    [SerializeField] private int fastCommitStableFrames = 2;
    [Tooltip("Use fast commit stability only when confidence is at least this value.")]
    [SerializeField] private float fastCommitConfidenceThreshold = 0.75f;
    [SerializeField] private float commitCooldownSeconds = 0.25f;
    [SerializeField] private float uiDebounceSeconds = 0.08f;
    [SerializeField] private bool useServerTextAsAuthoritative = true;

    [Header("Caption Logic")]
    [SerializeField] private int autoSpaceNoHandFrames = 6;
    [SerializeField] private int endSentenceNoHandFrames = 15;
    [SerializeField] private int autoClearNoHandFrames = 40;
    [SerializeField] private int maxCharsPerLine = 40;
    [SerializeField] private int maxHistoryChars = 96;
    [SerializeField] private int maxCompletedSentences = 2;
    [SerializeField] private string noHandFriendlyMessage = "Show your hand to start signing";
    [Header("Local Word Correction (fast, no network)")]
    [SerializeField] private bool enableLocalWordCorrection = true;
    [Tooltip("Max edit distance for local correction candidate search.")]
    [SerializeField] private int localCorrectionMaxEditDistance = 1;

    [Header("HUD Orientation")]
    [Tooltip("If true, the HUD UI will automatically rotate to face the camera (Billboarding).")]
    [SerializeField] private bool useBillboardHud = true;
    [Tooltip("Speed at which the HUD rotates to face you. 0 = instant.")]
    [SerializeField] private float billboardLerpSpeed = 12f;

    [Header("Debug")]
    [Tooltip("CPU pipeline: log frame bytes, round-trip ms, hand/no-hand, HTTP ok/fail, and send FPS (see logEveryNSendAttempts for summaries).")]
    [SerializeField] private bool logCpuPipelineDebug = false;
    [Tooltip("If true, logs every raw /predict request. If false, only periodic summaries + errors.")]
    [SerializeField] private bool logEveryCpuPipelineRequest = false;
    [Tooltip("If enabled, saves occasional captured ROI JPGs to persistentDataPath/sign_debug.")]
    [SerializeField] private bool saveDebugFrames = true;
    [Tooltip("Save one debug frame every N send attempts.")]
    [SerializeField] private int saveEveryNSends = 5;
    [Tooltip("If enabled, saves the first sent inference JPEGs exactly as posted to /predict.")]
    [SerializeField] private bool saveFirstSentFrames = true;
    [Tooltip("How many sent inference frames to save for visual inspection.")]
    [SerializeField] private int saveFirstSentFramesCount = 80;

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
    private string _historyText = "";
    private string _candidateLetter = "";
    private int _candidateStableCount;
    private int _noHandFrames;
    private float _lastCommitAt = -999f;

    // Captioning variables
    private string _currentWordBuffer = "";
    private List<string> _captionLines = new List<string> { "", "" };
    private Queue<string> _sentenceHistory = new Queue<string>();
    private readonly Dictionary<string, string> _localCorrectionCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _commonTypoMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "TEH", "THE" }, { "YUO", "YOU" }, { "YOUU", "YOU" }, { "THSI", "THIS" },
        { "WROD", "WORD" }, { "SINGN", "SIGN" }, { "HELO", "HELLO" }, { "PLESE", "PLEASE" }
    };
    private static readonly string[] LocalCorrectionVocabulary =
    {
        "A","AN","AND","ARE","AS","AT","BE","BY","CAN","DO","FOR","FROM","GO","GOOD","HAVE","HELLO",
        "HELP","HI","HOW","I","IN","IS","IT","ME","MY","NO","NOT","OF","OK","ON","PLEASE","SHE",
        "SIGN","SORRY","SPEAK","STOP","THANK","THANKS","THAT","THE","THEY","THIS","TO","TURN",
        "UP","USE","VERY","WE","WHAT","WHEN","WHERE","WHO","WHY","WITH","WORD","WORK","YOU","YOUR",
        "ITALIAN","ENGLISH","CAPTION","CAPTIONS","TRANSLATION","LANGUAGE","HAND"
    };
    private List<GameObject> _hudObjects = new List<GameObject>();
    private Color32[] _lastRoiSample;
    private int _frameTickCounter;
#pragma warning disable 0414 // Debug placeholders kept for planned telemetry expansion.
    private bool _loggedFirstRequest;
    private bool _loggedFirstSuccess;
#pragma warning restore 0414
    private int _captureFrameCount;
    private int _sendAttemptCount;
    private int _sendSuccessCount;
    private int _skippedHandRoiFrames;
    private int _droppedInFlightFrames;
    private int _droppedInvalidFrameCount;
    private int _handRoiLogCounter;
    private int _roiInvalidStatusCounter;
    private bool _warnedMissingHandPipeline;
    private string _lastMultipartFileName = "frame.jpg";
    private float _lastSendAt = -1f;
    // CPU pipeline (/predict) debug aggregates
    private int _cpuPipeHttpOk;
    private int _cpuPipeHttpFail;
    private int _cpuPipeHandDetected;
    private int _cpuPipeNoHand;
    private int _cpuPipeParseFail;
    private long _cpuPipeTotalJpegBytes;
    private int _cpuPipeRoundTripSamples;
    private double _cpuPipeTotalRoundTripMs;
    private double _cpuPipeTotalServerProcMs;
    private int _cpuPipeServerProcSamples;
    private float _cpuPipeFirstCompletedRt = -1f;
    private float _cpuPipeLastCompletedRt = -1f;
    private int _cpuPipeCompletedSinceLastSummary;
    private float _cpuPipeSummaryWindowStartRt = -1f;
    private string _debugFrameDir;
    private string _sentFramesDir;
    private int _savedSentFramesCount;
    private bool _loggedSentFramesTargetPath;
    private Label _subtitleLabel;
    private Label _mainHudCaptionLabel;
    private string _inferCaptionLine = "";
    private string _lastNetworkError = "";
    private bool _applicationIsQuitting;
    private int _requestSequence;
#if UNITY_WSA && !UNITY_EDITOR
    private static bool _requestedWebCamUserAuthorization;
    /// <summary>UWP: false until <see cref="CoRequestWebCamUserAuthorizationOnce"/> finishes (prompt or already granted).</summary>
    private bool _uwpWebCamPrivacyReady;
#else
    private const bool _uwpWebCamPrivacyReady = true;
#endif
    private bool _reportedPvStartupDiagnostics;
    private static bool _loggedInferenceRouting;

    /// <summary>
    /// Shown when PV / AR camera never starts (often Mirage 0x80070005). VS deploy uses DefaultAccount/DevelopmentFiles — camera often fails there.
    /// </summary>
    private static string PvBlockedUserHint()
    {
#if UNITY_WSA && !UNITY_EDITOR
        try
        {
            string blob = (Application.persistentDataPath ?? "") + "\n" + (Application.dataPath ?? "");
            if (blob.IndexOf("DefaultAccount", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("DevelopmentFiles", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return
                    "VS/DevelopmentFiles (DefaultAccount): Mirage often blocks PV here (80070005). " +
                    "Build an app package, install on HoloLens for your signed-in user, open from Start — do not rely on F5/Remote Debugger. " +
                    "Then Settings → Privacy → Camera → Bootstrap ON, reboot once.";
            }
        }
        catch
        {
            // ignore
        }
#endif
        return
            "OS blocked camera access. Settings → Privacy → Camera: enable this app. " +
            "Use a normal signed-in user (not DefaultAccount). Reboot. Close MRC / Device Portal live camera.";
    }

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
        // User request: disable all cropping paths and send full frame JPEGs.
        useHandRoiInference = false;
        cpuPipelineCropCenter = false;
        centerCropScale = 1f;

        // Keep debug frame saving configurable from Inspector; forcing this ON on device can cause I/O stalls.

        if (combinedABMode)
        {
            ApplyCombinedAbDefaults();
        }

        if (strictLowLatencyMode)
        {
            ApplyStrictLowLatencyMode();
        }

#if UNITY_WSA && !UNITY_EDITOR
        ApplyHoloLensWebCamPvOverride();

        // Exclusive XRCpuImage path: WebCamTexture off (unless hololensPvUseWebCamTexture forced WebCam above).
        if (!hololensPvUseWebCamTexture)
        {
            if (useWebCamTexture || useXrCpuImagePipeline)
            {
                useWebCamTexture = false;
                Debug.Log("[SignInferenceClient] WebCamTexture disabled on UWP (use XRCpuImage pipeline or intrinsics).");
            }
        }
#endif

        if (useXrCpuImagePipeline)
        {
            useWebCamTexture = false;
        }

        minSendIntervalMs = Mathf.Clamp(minSendIntervalMs, 50f, 2000f);
        maxSendFrameWidth = Mathf.Clamp(maxSendFrameWidth, 160, 1920);

        if (usePlatformDefaultApiUrl)
        {
            ApplyPlatformDefaultBaseUrl();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N");
        }

        baseUrl = NormalizeInferenceBaseUrl(baseUrl);
        LogInferenceRoutingOnce();

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
        logEveryNSendAttempts = Mathf.Max(1, logEveryNSendAttempts);
        startupCaptureDelaySeconds = Mathf.Clamp(startupCaptureDelaySeconds, 0f, 8f);
        commitStableFrames = Mathf.Clamp(commitStableFrames, 1, 8);
        fastCommitStableFrames = Mathf.Clamp(fastCommitStableFrames, 1, commitStableFrames);
        fastCommitConfidenceThreshold = Mathf.Clamp01(fastCommitConfidenceThreshold);
        uiDebounceSeconds = Mathf.Clamp(uiDebounceSeconds, 0f, 0.5f);

        _roiTexture = new Texture2D(targetSize, targetSize, TextureFormat.RGB24, false);
        _debugFrameDir = Path.Combine(Application.persistentDataPath, "sign_debug");
        _sentFramesDir = ResolveSentFramesDirectory();
    }

    private void ApplyCombinedAbDefaults()
    {
        // Low-latency Python path: PV CPU image + raw JPEG + throttling (no XR hand ROI).
        stage1Only = false;
        useXrCpuImagePipeline = true;
        useHandRoiInference = false;
        useWebCamTexture = false;
        minSendIntervalMs = 175f;
        requestFps = 5f;
        dropIfRequestInFlight = true;
        jpegQuality = 88;
        maxSendFrameWidth = 640;
        inferEndpointPath = "/predict";
        confidenceThreshold = 0.55f;
        minTop2Margin = 0.12f;
        commitStableFrames = 3;
        fastCommitStableFrames = 2;
        fastCommitConfidenceThreshold = 0.75f;
        uiDebounceSeconds = 0.08f;
    }

    private void ApplyStrictLowLatencyMode()
    {
        useXrCpuImagePipeline = true;
        useWebCamTexture = false;
        useHandRoiInference = false;
        dropIfRequestInFlight = true;
        stage1Only = false;
        inferEndpointPath = "/predict";
        minSendIntervalMs = Mathf.Clamp(minSendIntervalMs, 150f, 200f);
        maxSendFrameWidth = Mathf.Clamp(maxSendFrameWidth, 160, 640);
        jpegQuality = Mathf.Clamp(jpegQuality, 85, 90);
        requestFps = Mathf.Clamp(requestFps, 4f, 6f);
        uiDebounceSeconds = Mathf.Clamp(uiDebounceSeconds, 0.05f, 0.2f);
    }

#if UNITY_WSA && !UNITY_EDITOR
    /// <summary>
    /// Older working path on device: PV through <see cref="WebCamTexture"/> instead of AR Foundation <c>XRCpuImage</c> / Mirage.
    /// Keeps current <c>/predict</c> queue, JSON, and UI behavior.
    /// </summary>
    private void ApplyHoloLensWebCamPvOverride()
    {
        if (!hololensPvUseWebCamTexture)
        {
            return;
        }

        useXrCpuImagePipeline = false;
        useWebCamTexture = true;
        Debug.Log("[SignInferenceClient] HoloLens: PV via WebCamTexture (inspector: hololensPvUseWebCamTexture). /predict pipeline unchanged.");
    }
#endif

    private void ApplyPlatformDefaultBaseUrl()
    {
        if (useXrCpuImagePipeline)
        {
#if UNITY_WSA && !UNITY_EDITOR
            // Device: local Python at 127.0.0.1 is the headset, not your PC — default to hosted Space API.
            baseUrl = "https://mederbekaiana-sign-language.hf.space";
#else
            // Editor / other platforms: hand pipeline on this machine — HoloLens override: SetInferenceBaseUrl("http://<PC_IP>:8010").
            baseUrl = "http://127.0.0.1:8010";
#endif
            return;
        }

#if UNITY_EDITOR
        baseUrl = "https://mederbekaiana-sign-language.hf.space";
#elif UNITY_WSA && !UNITY_EDITOR
        baseUrl = "https://mederbekaiana-sign-language.hf.space";
#else
        baseUrl = "https://mederbekaiana-sign-language.hf.space";
#endif
    }

    /// <summary>Sets the server root for <c>/predict</c> (no trailing slash). Stops platform presets from overriding this in the same session.</summary>
    public void SetInferenceBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        usePlatformDefaultApiUrl = false;
        baseUrl = NormalizeInferenceBaseUrl(url.Trim());
        _loggedInferenceRouting = false;
        LogInferenceRoutingOnce();
    }

    private void LogInferenceRoutingOnce()
    {
        if (_loggedInferenceRouting)
        {
            return;
        }

        _loggedInferenceRouting = true;
        if (UsesHuggingFaceSpaceApi())
        {
            Debug.Log(
                "[SignInferenceClient] Inference traffic goes to Hugging Face Space (baseUrl). A local api_server.py on your PC will not see these requests.");
            return;
        }

#if UNITY_WSA && !UNITY_EDITOR
        if (!string.IsNullOrEmpty(baseUrl)
            && (baseUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                || baseUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            Debug.LogWarning(
                "[SignInferenceClient] baseUrl is localhost/127.0.0.1 — on HoloLens that is the device itself, not your PC. Use http://<PC_LAN_IP>:8010 for local Python.");
        }
#endif
    }

    private static void SetLiveCaptionForHud(string line)
    {
        LiveCaptionForHud = line ?? "";
    }

    private static void DebugWrite(string runId, string hypothesisId, string location, string message, string dataJson)
    {
        if (!AgentDebugFileLog)
        {
            return;
        }

        try
        {
            string line =
                "{\"sessionId\":\"" + DebugSessionId + "\"," +
                "\"runId\":\"" + runId + "\"," +
                "\"hypothesisId\":\"" + hypothesisId + "\"," +
                "\"location\":\"" + location.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"," +
                "\"message\":\"" + message.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"," +
                "\"data\":" + (string.IsNullOrEmpty(dataJson) ? "{}" : dataJson) + "," +
                "\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
                "}\n";
            File.AppendAllText(DebugLogPath, line);
        }
        catch
        {
            // Debug logging must never affect runtime behavior.
        }
    }

    private void Start()
    {
        Debug.Log(
            "[SignInferenceClient] startup config " +
            $"mode={App.CurrentInputMode} useXrCpuImagePipeline={useXrCpuImagePipeline} useWebCamTexture={useWebCamTexture} " +
#if UNITY_WSA && !UNITY_EDITOR
            $"hololensPvUseWebCamTexture={hololensPvUseWebCamTexture} " +
#endif
            $"strictLowLatencyMode={strictLowLatencyMode} baseUrl={baseUrl} endpointPath={inferEndpointPath} requestTimeoutSeconds={requestTimeoutSeconds:0.0} requestFps={requestFps:0.0} minSendIntervalMs={minSendIntervalMs:0}");

        if (saveDebugFrames || saveFirstSentFrames)
        {
            Debug.Log(
                "[SignInferenceClient] Frame save dirs: persistentDataPath=" + Application.persistentDataPath +
                " debug=" + _debugFrameDir + " sent=" + _sentFramesDir +
                $" saveDebugFrames={saveDebugFrames} saveFirstSentFrames={saveFirstSentFrames} saveEveryNSends={saveEveryNSends}");
        }

        if (useSubtitleLabelFallback)
        {
            StartCoroutine(BindToolkitCaptionLabelsWhenReady());
        }

        if (useXrCpuImagePipeline)
        {
            if (pvCpuImageSource == null)
            {
                pvCpuImageSource = FindObjectOfType<HololensPvCpuImageSource>();
            }

            if (pvCpuImageSource != null)
            {
                pvCpuImageSource.SetEncodingOptions(
                    maxSendFrameWidth,
                    cpuPipelineCropCenter,
                    cpuPipelineCenterCropFraction,
                    jpegQuality,
                    true);
            }
            else
            {
                Debug.LogWarning(
                    "[SignInferenceClient] useXrCpuImagePipeline is on but no HololensPvCpuImageSource found. Add it to the PV/AR camera object.");
            }
        }

        if (signCaptureActive && useWebCamTexture && !useXrCpuImagePipeline)
        {
#if UNITY_WSA && !UNITY_EDITOR
            StartCoroutine(CoRequestWebCamUserAuthorizationOnce());
#endif
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

        if (signCaptureActive && useWebCamTexture && !useXrCpuImagePipeline)
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

        // Only drive sign caption/rendering when Sign mode is active.
        // Prevents stale sign status when another input mode is selected.
        if (App.CurrentInputMode != App.InputMode.Sign)
        {
            if (_mainHudCaptionLabel != null)
            {
                _mainHudCaptionLabel.text = "";
            }
            SetLiveCaptionForHud("");
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

        float intervalSec = useXrCpuImagePipeline && minSendIntervalMs > 0f
            ? minSendIntervalMs * 0.001f
            : (1f / requestFps);

        if (useXrCpuImagePipeline)
        {
            _nextRequestAt = Time.time + intervalSec;

            if (_requestInFlight && dropIfRequestInFlight)
            {
                _droppedInFlightFrames++;
                return;
            }

            _frameTickCounter++;
            if ((_frameTickCounter % sendEveryNthFrame) != 0)
            {
                return;
            }

            RunCpuImagePipelineLoopTick();
            return;
        }

        // WebCam / overrideSource: timer + GPU readback run in LateUpdate so WebCamTexture has updated this frame.
    }

    private void LateUpdate()
    {
        if (_applicationIsQuitting)
        {
            return;
        }

        if (App.CurrentInputMode != App.InputMode.Sign)
        {
            return;
        }

        if (!signCaptureActive)
        {
            return;
        }

        if (useXrCpuImagePipeline)
        {
            return;
        }

        UpdateHudOrientation();

        if (Time.time < _nextRequestAt)
        {
            return;
        }

        float intervalSec = (1f / requestFps);
        _nextRequestAt = Time.time + intervalSec;

        if (_requestInFlight && dropIfRequestInFlight)
        {
            _droppedInFlightFrames++;
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
            if (_webCamTexture != null && _webCamTexture.isPlaying)
            {
                _inferCaptionLine = "Sign: waiting for first frame…";
            }
            else if (_webCamTexture != null)
            {
                _inferCaptionLine = "Sign: starting camera…";
            }
            else
            {
                _inferCaptionLine = "Sign: waiting for camera frame…";
            }

            return;
        }

        if (_webCamTexture != null && !_webCamTexture.didUpdateThisFrame)
        {
            return;
        }

        if (_inferCaptionLine.StartsWith("Sign: waiting", StringComparison.Ordinal)
            || _inferCaptionLine.StartsWith("Sign: starting", StringComparison.Ordinal))
        {
            _inferCaptionLine = "";
        }

        _captureFrameCount++;

        if (!TryBuildJpegForInference(src, out byte[] jpegBytes))
        {
            _droppedInvalidFrameCount++;
            return;
        }

        QueueInference(jpegBytes, "loop");
    }

    private static string FormatCpuPipelineErrorCaption(string err)
    {
        if (string.IsNullOrEmpty(err))
        {
            return "Sign: waiting for camera frame...";
        }

        if (err.IndexOf("AR camera subsystem not running", StringComparison.OrdinalIgnoreCase) >= 0
            || err.IndexOf("ARCameraManager missing or disabled", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Sign: " + PvBlockedUserHint();
        }

        return "Sign: " + err;
    }

    private void RunCpuImagePipelineLoopTick()
    {
#if UNITY_WSA && !UNITY_EDITOR
        if (!_uwpWebCamPrivacyReady)
        {
            _inferCaptionLine = "Sign: waiting for camera permission (accept the system prompt if shown)...";
            return;
        }
#endif
        if (pvCpuImageSource == null)
        {
            pvCpuImageSource = FindObjectOfType<HololensPvCpuImageSource>();
        }

        if (pvCpuImageSource == null)
        {
            _inferCaptionLine = "Sign: add HololensPvCpuImageSource + ARCameraManager";
            return;
        }

        if (!pvCpuImageSource.TryGetJpegFrame(out byte[] jpegBytes, out string err))
        {
            _inferCaptionLine = FormatCpuPipelineErrorCaption(err);
            if (!_reportedPvStartupDiagnostics
                && !string.IsNullOrEmpty(err)
                && (err.IndexOf("AR camera subsystem not running", StringComparison.OrdinalIgnoreCase) >= 0
                    || err.IndexOf("ARCameraManager missing or disabled", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                _reportedPvStartupDiagnostics = true;
                string diag = pvCpuImageSource.GetRuntimeDiagnosticsSummary();
                _cameraUserMessage = diag + " " + PvBlockedUserHint();
                Debug.LogWarning("[SignInferenceClient] " + diag);
                ApplyCaptionToSubtitle();
            }
            return;
        }

        _reportedPvStartupDiagnostics = false;

        _captureFrameCount++;
        QueueInference(jpegBytes, "loop");
    }

    public void SetSignCaptureActive(bool active)
    {
        if (signCaptureActive == active) return;
        signCaptureActive = active;
        #region agent log
        DebugWrite(
            "pre-fix",
            "H1",
            "SignInferenceClient.cs:SetSignCaptureActive",
            "Sign capture toggled",
            "{\"active\":" + (active ? "true" : "false") + ",\"mode\":\"" + App.CurrentInputMode.ToString() + "\"}");
        #endregion

        if (signCaptureActive)
        {
            if (useXrCpuImagePipeline)
            {
                if (pvCpuImageSource == null)
                {
                    pvCpuImageSource = FindObjectOfType<HololensPvCpuImageSource>();
                }

                if (pvCpuImageSource != null)
                {
                    pvCpuImageSource.SetEncodingOptions(
                        maxSendFrameWidth,
                        cpuPipelineCropCenter,
                        cpuPipelineCenterCropFraction,
                        jpegQuality,
                        true);
                    pvCpuImageSource.RequestStartupNudge();
                }

#if UNITY_WSA && !UNITY_EDITOR
                StartCoroutine(CoRequestWebCamUserAuthorizationOnce());
#endif
            }

            if (useWebCamTexture && !useXrCpuImagePipeline)
            {
#if UNITY_WSA && !UNITY_EDITOR
                StartCoroutine(CoRequestWebCamUserAuthorizationOnce());
#endif
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
            _inferCaptionLine = "";
            _cameraUserMessage = "";
            SetLiveCaptionForHud("");
            UpdateIdleStatusHint();
        }
    }

#if UNITY_WSA && !UNITY_EDITOR
    /// <summary>
    /// UWP/HoloLens: capability in manifest is not always enough; request runtime consent once per process (XRCpuImage or WebCamTexture).
    /// </summary>
    private IEnumerator CoRequestWebCamUserAuthorizationOnce()
    {
        if (_requestedWebCamUserAuthorization)
        {
            _uwpWebCamPrivacyReady = true;
            yield break;
        }

        if (Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            _requestedWebCamUserAuthorization = true;
            _uwpWebCamPrivacyReady = true;
            yield break;
        }

        _requestedWebCamUserAuthorization = true;
        Debug.Log("[SignInferenceClient] Requesting WebCam user authorization (runtime)...");
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        _uwpWebCamPrivacyReady = true;
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogWarning("[SignInferenceClient] WebCam user authorization not granted.");
        }
        else
        {
            Debug.Log("[SignInferenceClient] WebCam user authorization granted.");
        }
    }
#endif

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

                if (WebCamTextureHasReadyFrame(_webCamTexture))
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
        if (startupCaptureDelaySeconds > 0f)
        {
            yield return new WaitForSecondsRealtime(startupCaptureDelaySeconds);
        }

        if (useXrCpuImagePipeline)
        {
            yield return null;
            _nextRequestAt = 0f;
            _frameTickCounter = Mathf.Max(0, sendEveryNthFrame - 1);
            TryQueueInferenceNow();
            yield break;
        }

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
            else
            {
                // WebCam pixel data is only valid after the frame renders; avoid black JPEGs from ReadPixels too early.
                yield return new WaitForEndOfFrame();
                for (int guard = 0; _webCamTexture != null && !_webCamTexture.didUpdateThisFrame && guard < 90; guard++)
                {
                    yield return null;
                }
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
            _droppedInFlightFrames++;
            return;
        }

        if (useXrCpuImagePipeline)
        {
            if (pvCpuImageSource == null)
            {
                pvCpuImageSource = FindObjectOfType<HololensPvCpuImageSource>();
            }

            if (pvCpuImageSource == null || !pvCpuImageSource.TryGetJpegFrame(out byte[] jCpu, out _))
            {
                return;
            }

            QueueInference(jCpu, "launch");
            return;
        }

        Texture src = GetActiveSourceTexture();
        if (src == null)
        {
            return;
        }

        if (_webCamTexture != null && !_webCamTexture.didUpdateThisFrame)
        {
            return;
        }

        if (!TryBuildJpegForInference(src, out byte[] jpegBytes))
        {
            _droppedInvalidFrameCount++;
            return;
        }

        QueueInference(jpegBytes, "launch");
    }

    private void QueueInference(byte[] jpegBytes, string tag)
    {
        _sendAttemptCount++;
        _lastSendAt = Time.time;
        _lastMultipartFileName = useHandRoiInference && handRoiPipeline != null
            ? (string.IsNullOrEmpty(handRoiMultipartFileName) ? "hand.jpg" : handRoiMultipartFileName)
            : "frame.jpg";
        MaybeSaveDebugFrame(jpegBytes, tag);
        MaybeSaveFirstSentFrame(jpegBytes, tag);
        MaybeLogHandRoiStats(jpegBytes);
        if (stage1Only)
        {
            if ((_sendAttemptCount % 30) == 0)
            {
                Debug.Log("[SignInferenceClient] Stage1Only enabled: inference API call skipped.");
            }
            return;
        }

        if ((_sendAttemptCount % logEveryNSendAttempts) == 0)
        {
            Debug.Log(
                $"[SignInferenceClient] cadence sendAttempts={_sendAttemptCount} success={_sendSuccessCount} droppedInFlight={_droppedInFlightFrames} droppedInvalid={_droppedInvalidFrameCount} skippedRoi={_skippedHandRoiFrames}");
        }

        if (UsesHuggingFaceSpaceApi())
        {
            StartCoroutine(PostInfer(jpegBytes));
            return;
        }

        if (strictLowLatencyMode || useXrCpuImagePipeline)
        {
            StartCoroutine(PostInferRawJpeg(jpegBytes));
            return;
        }

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
        if (!strictLowLatencyMode && useHandRoiInference)
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
            _roiInvalidStatusCounter++;
            if ((_roiInvalidStatusCounter % 10) == 0)
            {
                string reason = string.IsNullOrEmpty(handRoiPipeline.LastInvalidReason)
                    ? "unknown"
                    : handRoiPipeline.LastInvalidReason;
                // When intrinsics are not ready, also show the camera subsystem state for diagnosis.
                if (reason.StartsWith("intrinsics_not_ready") && handRoiPipeline.LocatableCamera != null)
                {
                    string camStatus = handRoiPipeline.LocatableCamera.CameraStatusLine;
                    _inferCaptionLine = $"Sign: {camStatus}";
                }
                else
                {
                    _inferCaptionLine = $"Sign: hand ROI invalid ({reason})";
                }
            }
            return false;
        }

        _roiInvalidStatusCounter = 0;

        int rw = roi.width;
        int rh = roi.height;
        if (rw <= 0 || rh <= 0)
        {
            _inferCaptionLine = "Sign: hand ROI invalid (empty bbox)";
            return false;
        }

        var scale = new Vector2(rw / (float)source.width, rh / (float)source.height);
        var offset = new Vector2(roi.x / (float)source.width, roi.y / (float)source.height);

        RenderTexture rt = RenderTexture.GetTemporary(rw, rh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
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

    /// <summary>HL PV can report small non-zero sizes; <c>width &gt; 16</c> wrongly rejected valid 16px-wide reports.</summary>
    private static bool WebCamTextureHasReadyFrame(WebCamTexture w)
    {
        return w != null && w.isPlaying && w.width > 0 && w.height > 0;
    }

    private Texture GetActiveSourceTexture()
    {
        if (overrideSource != null)
        {
            return overrideSource;
        }

        if (WebCamTextureHasReadyFrame(_webCamTexture))
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

        int w = readable.width;
        int h = readable.height;

        if (warpFullFrameToSquareBeforeUpload)
        {
            // Legacy: warp entire frame to targetSize² (duplicates server resize and distorts aspect).
            Color[] framePixels = readable.GetPixels(0, 0, w, h);
            _roiTexture.Reinitialize(targetSize, targetSize, TextureFormat.RGB24, false);
            _roiTexture.SetPixels(ScalePixelsBilinear(framePixels, w, h, targetSize, targetSize));
            _roiTexture.Apply(false, false);
            jpegBytes = _roiTexture.EncodeToJPG(jpegQuality);
        }
        else
        {
            // Native aspect: optional downscale so long edge ≤ maxSendFrameWidth (bandwidth); server does model resize.
            int maxEdge = Mathf.Clamp(maxSendFrameWidth, 160, 4096);
            int longEdge = Mathf.Max(w, h);
            if (longEdge <= maxEdge)
            {
                jpegBytes = readable.EncodeToJPG(jpegQuality);
            }
            else
            {
                float s = maxEdge / (float)longEdge;
                int outW = Mathf.Max(1, Mathf.RoundToInt(w * s));
                int outH = Mathf.Max(1, Mathf.RoundToInt(h * s));
                Color[] framePixels = readable.GetPixels(0, 0, w, h);
                _roiTexture.Reinitialize(outW, outH, TextureFormat.RGB24, false);
                _roiTexture.SetPixels(ScalePixelsBilinear(framePixels, w, h, outW, outH));
                _roiTexture.Apply(false, false);
                jpegBytes = _roiTexture.EncodeToJPG(jpegQuality);
            }
        }

        if (skipSimilarFrames)
        {
            Texture2D forSimilarity = warpFullFrameToSquareBeforeUpload || Mathf.Max(w, h) > Mathf.Clamp(maxSendFrameWidth, 160, 4096)
                ? _roiTexture
                : readable;
            if (IsRoiTooSimilar(forSimilarity))
            {
                return false;
            }
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

        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
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

    /// <summary>
    /// Hand pipeline: raw JPEG body, <c>Content-Type: image/jpeg</c>, JSON with predicted_letter / confidence / hand_detected.
    /// </summary>
    private IEnumerator PostInferRawJpeg(byte[] jpegBytes)
    {
        _requestInFlight = true;
        float startedAt = Time.realtimeSinceStartup;
        string requestId = "sign-" + System.Threading.Interlocked.Increment(ref _requestSequence).ToString("D5");
        int jpegLen = jpegBytes?.Length ?? 0;
        string path = string.IsNullOrEmpty(inferEndpointPath) ? "/predict" : inferEndpointPath;
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = "/" + path;
        }

        string url = TrimTrailingSlash(baseUrl) + path;

        if (!_loggedFirstRequest)
        {
            _loggedFirstRequest = true;
            Debug.Log($"[SignInferenceClient] {requestId} POST {url} (image/jpeg, {jpegLen} bytes)");
        }

        using (UnityWebRequest req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(jpegBytes ?? Array.Empty<byte>());
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "image/jpeg");
            req.timeout = Mathf.RoundToInt(requestTimeoutSeconds);
            yield return req.SendWebRequest();

            float roundTripMs = (Time.realtimeSinceStartup - startedAt) * 1000f;

            if (_applicationIsQuitting)
            {
                _requestInFlight = false;
                yield break;
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                string bodyPreview = req.downloadHandler != null ? (req.downloadHandler.text ?? "") : "";
                if (bodyPreview.Length > 400)
                {
                    bodyPreview = bodyPreview.Substring(0, 400) + "...";
                }

                string err = requestId + " infer failed: " + req.error;
                Debug.LogWarning("[SignInferenceClient] " + err);
                Debug.LogWarning(
                    $"[SignInferenceClient] infer http={req.responseCode} url={url} body={bodyPreview}");
                _lastNetworkError = err;
                Debug.LogWarning(
                    $"[SignInferenceClient] latencyMs={roundTripMs:0} status=raw_infer_failed code={req.responseCode}");
                OnNetworkError?.Invoke(err);
                RecordCpuPipelineHttpFailure(jpegLen, roundTripMs);
                if (logCpuPipelineDebug && logEveryCpuPipelineRequest)
                {
                    Debug.Log(
                        $"[SignInferenceClient][cpu-pipe] req={requestId} jpegBytes={jpegLen} roundTripMs={roundTripMs:0.0} http=FAIL detection=n/a");
                }
                MaybeLogCpuPipelineSummary();
            }
            else
            {
                _lastNetworkError = "";
                _sendSuccessCount++;
                string body = req.downloadHandler.text ?? "";
                string procHdr = req.GetResponseHeader("X-Process-Time-Ms");
                float? serverProcMs = null;
                if (!string.IsNullOrEmpty(procHdr)
                    && float.TryParse(procHdr, NumberStyles.Float, CultureInfo.InvariantCulture, out float sp))
                {
                    serverProcMs = sp;
                }

                if (!TryParseInferResponse(body, out InferResponse response, out string parseErr))
                {
                    if (!string.IsNullOrEmpty(parseErr))
                    {
                        _lastNetworkError = parseErr;
                        OnNetworkError?.Invoke(parseErr);
                    }

                    _cpuPipeParseFail++;
                    RecordCpuPipelineRoundTripOnly(jpegLen, roundTripMs, serverProcMs);
                    if (logCpuPipelineDebug && logEveryCpuPipelineRequest)
                    {
                        Debug.LogWarning(
                            $"[SignInferenceClient][cpu-pipe] req={requestId} jpegBytes={jpegLen} roundTripMs={roundTripMs:0.0} serverProcMs={(serverProcMs.HasValue ? serverProcMs.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a")} http=OK parse=FAIL");
                    }
                    MaybeLogCpuPipelineSummary();
                }
                else
                {
                    if (logCpuPipelineDebug && logEveryCpuPipelineRequest)
                    {
                        string det = response.no_hand ? "no_hand" : "hand";
                        string spStr = serverProcMs.HasValue
                            ? serverProcMs.Value.ToString("0.0", CultureInfo.InvariantCulture)
                            : "n/a";
                        Debug.Log(
                            $"[SignInferenceClient][cpu-pipe] req={requestId} jpegBytes={jpegLen} roundTripMs={roundTripMs:0.0} serverProcMs={spStr} http=OK parse=OK detection={det} letter={response.letter} conf={response.confidence:0.000}");
                    }

                    if (!_loggedFirstSuccess)
                    {
                        _loggedFirstSuccess = true;
                        Debug.Log("[SignInferenceClient] First raw inference response received.");
                    }

                    RecordCpuPipelineSuccess(jpegLen, roundTripMs, response, serverProcMs);
                    if (!logEveryCpuPipelineRequest)
                    {
                        float latencyMs = roundTripMs;
                        Debug.Log(
                            $"[SignInferenceClient] latencyMs={latencyMs:0} raw_infer letter={response.letter} conf={response.confidence:0.000} no_hand={response.no_hand}");
                    }

                    HandleInferResponse(response);
                    OnInferResponse?.Invoke(response);
                    MaybeLogCpuPipelineSummary();
                }
            }
        }

        _requestInFlight = false;
    }

    private void RecordCpuPipelineHttpFailure(int jpegBytes, double roundTripMs)
    {
        _cpuPipeHttpFail++;
        _cpuPipeTotalJpegBytes += jpegBytes;
        _cpuPipeTotalRoundTripMs += roundTripMs;
        _cpuPipeRoundTripSamples++;
        CpuPipelineBumpCompleted(roundTripMs);
    }

    private void RecordCpuPipelineRoundTripOnly(int jpegBytes, double roundTripMs, float? serverProcMs = null)
    {
        _cpuPipeHttpOk++;
        _cpuPipeTotalJpegBytes += jpegBytes;
        _cpuPipeTotalRoundTripMs += roundTripMs;
        _cpuPipeRoundTripSamples++;
        if (serverProcMs.HasValue)
        {
            _cpuPipeTotalServerProcMs += serverProcMs.Value;
            _cpuPipeServerProcSamples++;
        }

        CpuPipelineBumpCompleted(roundTripMs);
    }

    private void RecordCpuPipelineSuccess(int jpegBytes, double roundTripMs, InferResponse r, float? serverProcMs)
    {
        _cpuPipeHttpOk++;
        _cpuPipeTotalJpegBytes += jpegBytes;
        _cpuPipeTotalRoundTripMs += roundTripMs;
        _cpuPipeRoundTripSamples++;
        if (serverProcMs.HasValue)
        {
            _cpuPipeTotalServerProcMs += serverProcMs.Value;
            _cpuPipeServerProcSamples++;
        }

        if (r.no_hand)
        {
            _cpuPipeNoHand++;
        }
        else
        {
            _cpuPipeHandDetected++;
        }

        CpuPipelineBumpCompleted(roundTripMs);
    }

    private void CpuPipelineBumpCompleted(double roundTripMs)
    {
        float rt = Time.realtimeSinceStartup;
        if (_cpuPipeFirstCompletedRt < 0f)
        {
            _cpuPipeFirstCompletedRt = rt;
        }

        _cpuPipeLastCompletedRt = rt;
        if (_cpuPipeSummaryWindowStartRt < 0f)
        {
            _cpuPipeSummaryWindowStartRt = rt;
        }

        _cpuPipeCompletedSinceLastSummary++;
    }

    private void MaybeLogCpuPipelineSummary()
    {
        if (!logCpuPipelineDebug)
        {
            return;
        }

        if (_cpuPipeRoundTripSamples <= 0 || _cpuPipeRoundTripSamples % logEveryNSendAttempts != 0)
        {
            return;
        }

        int httpTotal = _cpuPipeHttpOk + _cpuPipeHttpFail;
        float httpOkRate = httpTotal > 0 ? _cpuPipeHttpOk / (float)httpTotal : 0f;
        int detTotal = _cpuPipeHandDetected + _cpuPipeNoHand;
        float handRate = detTotal > 0 ? _cpuPipeHandDetected / (float)detTotal : 0f;
        float avgBytes = _cpuPipeRoundTripSamples > 0 ? (float)(_cpuPipeTotalJpegBytes / (double)_cpuPipeRoundTripSamples) : 0f;
        float avgRtt = _cpuPipeRoundTripSamples > 0 ? (float)(_cpuPipeTotalRoundTripMs / _cpuPipeRoundTripSamples) : 0f;
        float avgSrv = _cpuPipeServerProcSamples > 0 ? (float)(_cpuPipeTotalServerProcMs / _cpuPipeServerProcSamples) : 0f;
        string avgSrvStr = _cpuPipeServerProcSamples > 0 ? avgSrv.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
        float nowRt = Time.realtimeSinceStartup;
        float overallFps = _cpuPipeRoundTripSamples > 0 && _cpuPipeFirstCompletedRt > 0f
            ? _cpuPipeRoundTripSamples / Mathf.Max(1e-4f, nowRt - _cpuPipeFirstCompletedRt)
            : 0f;
        float windowDur = nowRt - _cpuPipeSummaryWindowStartRt;
        float windowFps = windowDur > 1e-4f && _cpuPipeCompletedSinceLastSummary > 0
            ? _cpuPipeCompletedSinceLastSummary / windowDur
            : 0f;

        Debug.Log(
            "[SignInferenceClient][cpu-pipe:summary] " +
            $"httpOk={_cpuPipeHttpOk} httpFail={_cpuPipeHttpFail} httpOkRate={httpOkRate:P0} " +
            $"hand={_cpuPipeHandDetected} noHand={_cpuPipeNoHand} handDetectionRate={handRate:P0} parseFail={_cpuPipeParseFail} " +
            $"avgJpegBytes={avgBytes:0} avgRoundTripMs={avgRtt:0.0} avgServerProcMs={avgSrvStr} " +
            $"sendFpsOverall={overallFps:0.00} sendFpsWindow={windowFps:0.00}");

        _cpuPipeCompletedSinceLastSummary = 0;
        _cpuPipeSummaryWindowStartRt = nowRt;
    }

    private IEnumerator PostInfer(byte[] jpegBytes)
    {
        _requestInFlight = true;
        float startedAt = Time.realtimeSinceStartup;
        string requestId = "sign-" + System.Threading.Interlocked.Increment(ref _requestSequence).ToString("D5");

        string callUrl = TrimTrailingSlash(baseUrl) + "/gradio_api/call/predict";
        #region agent log
        DebugWrite(
            "pre-fix",
            "H2",
            "SignInferenceClient.cs:PostInfer",
            "Sending infer request",
            "{\"url\":\"" + callUrl.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"bytes\":" + (jpegBytes != null ? jpegBytes.Length : 0).ToString(CultureInfo.InvariantCulture) + "}");
        #endregion
        if (!_loggedFirstRequest)
        {
            _loggedFirstRequest = true;
            Debug.Log("[SignInferenceClient] " + requestId + " sending first inference request to " + callUrl);
        }

        string imageDataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes ?? Array.Empty<byte>());
        string payload = "{\"data\":[{\"url\":\"" + imageDataUrl + "\"}]}";

        using (UnityWebRequest req = new UnityWebRequest(callUrl, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = Mathf.RoundToInt(requestTimeoutSeconds);
            UnityWebRequestAsyncOperation op = null;
            try
            {
                op = req.SendWebRequest();
            }
            catch (InvalidOperationException ex)
            {
                string err =
                    "HTTP blocked by Unity Player setting. Set Player > Other Settings > Allow downloads over HTTP = Always allowed. " +
                    ex.Message;
                Debug.LogError("[SignInferenceClient] " + requestId + " " + err);
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
                string err = $"{requestId} predict failed: {req.error}";
                Debug.LogWarning("[SignInferenceClient] " + err);
                _lastNetworkError = err;
                float latencyMs = (Time.realtimeSinceStartup - startedAt) * 1000f;
                Debug.LogWarning($"[SignInferenceClient] latencyMs={latencyMs:0} status=call_failed");
                #region agent log
                DebugWrite(
                    "pre-fix",
                    "H3",
                    "SignInferenceClient.cs:PostInfer",
                    "Infer request failed",
                    "{\"result\":\"" + req.result.ToString() + "\",\"error\":\"" + (req.error ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"responseCode\":" + req.responseCode.ToString(CultureInfo.InvariantCulture) + "}");
                #endregion
                OnNetworkError?.Invoke(err);
            }
            else
            {
                string callResponse = req.downloadHandler.text ?? "";
                string eventId = ReadJsonStringField(callResponse, "event_id");
                if (string.IsNullOrEmpty(eventId))
                {
                    string err = "predict failed: missing event_id in /gradio_api/call/predict response";
                    Debug.LogWarning("[SignInferenceClient] " + err);
                    _lastNetworkError = err;
                    float latencyMs = (Time.realtimeSinceStartup - startedAt) * 1000f;
                    Debug.LogWarning($"[SignInferenceClient] latencyMs={latencyMs:0} status=missing_event_id");
                    OnNetworkError?.Invoke(err);
                }
                else
                {
                    string streamUrl = TrimTrailingSlash(baseUrl) + "/gradio_api/call/predict/" + eventId;
                    using (UnityWebRequest streamReq = UnityWebRequest.Get(streamUrl))
                    {
                        streamReq.timeout = Mathf.RoundToInt(requestTimeoutSeconds);
                        yield return streamReq.SendWebRequest();

                        if (streamReq.result != UnityWebRequest.Result.Success)
                        {
                            string err = $"predict stream failed: {streamReq.error}";
                            err = requestId + " " + err;
                            Debug.LogWarning("[SignInferenceClient] " + err);
                            _lastNetworkError = err;
                            float latencyMs = (Time.realtimeSinceStartup - startedAt) * 1000f;
                            Debug.LogWarning($"[SignInferenceClient] latencyMs={latencyMs:0} status=stream_failed");
                            OnNetworkError?.Invoke(err);
                        }
                        else
                        {
                            _lastNetworkError = "";
                            _sendSuccessCount++;
                            string json = ExtractJsonObjectFromSse(streamReq.downloadHandler.text);
                            #region agent log
                            DebugWrite(
                                "pre-fix",
                                "H4",
                                "SignInferenceClient.cs:PostInfer",
                                "Infer request succeeded",
                                "{\"responseCode\":" + streamReq.responseCode.ToString(CultureInfo.InvariantCulture) + ",\"jsonLength\":" + (json != null ? json.Length : 0).ToString(CultureInfo.InvariantCulture) + "}");
                            #endregion
                            if (!TryParseInferResponse(json, out InferResponse response, out string parseErr))
                            {
                                if (!string.IsNullOrEmpty(parseErr))
                                {
                                    _lastNetworkError = parseErr;
                                    OnNetworkError?.Invoke(parseErr);
                                }
                            }
                            else
                            {
                                float latencyMs = (Time.realtimeSinceStartup - startedAt) * 1000f;
                                Debug.Log(
                                    $"[SignInferenceClient] latencyMs={latencyMs:0} status=ok letter={response.letter} conf={response.confidence:0.000}");
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
                }
            }
        }

        _requestInFlight = false;
    }

    private void HandleInferResponse(InferResponse response)
    {
        bool noHand = IsNoHand(response);
        string normalizedLetter = (response.letter ?? "").Trim().ToUpperInvariant();
        bool isLetter = normalizedLetter.Length == 1 && normalizedLetter[0] >= 'A' && normalizedLetter[0] <= 'Z';
        int requiredStableFrames = response.confidence >= fastCommitConfidenceThreshold
            ? Mathf.Max(1, fastCommitStableFrames)
            : Mathf.Max(1, commitStableFrames);
        bool accepted = !noHand
            && isLetter
            && response.confidence >= confidenceThreshold
            && response.top2_margin >= Mathf.Max(0f, minTop2Margin);

        if (noHand)
        {
            _noHandFrames++;
            _pendingLetter = null;
            _candidateLetter = "";
            _candidateStableCount = 0;

            if (_noHandFrames == Mathf.Max(1, autoSpaceNoHandFrames))
            {
                if (_currentWordBuffer.Length > 0)
                {
                    string finalizedWord = CorrectWordLocal(_currentWordBuffer);
                    AppendWordToCaption(finalizedWord);
                    _historyText += " ";
                    _currentWordBuffer = "";
                }
            }
            else if (_noHandFrames == Mathf.Max(1, endSentenceNoHandFrames))
            {
                if (_historyText.Length > 0)
                {
                    _sentenceHistory.Enqueue(_historyText);
                    while (_sentenceHistory.Count > Mathf.Max(1, maxCompletedSentences))
                    {
                        _sentenceHistory.Dequeue();
                    }
                    _historyText = "";
                }
                _captionLines[0] = "";
                _captionLines[1] = "";
            }
            else if (_noHandFrames == Mathf.Max(1, autoClearNoHandFrames))
            {
                // Auto-clear or fade out
                _captionLines[0] = "";
                _captionLines[1] = "";
            }

            if (letterText != null)
                letterText.text = noHandFriendlyMessage;
        }
        else
        {
            _noHandFrames = 0;
            if (accepted)
            {
                if (string.Equals(_candidateLetter, normalizedLetter, StringComparison.Ordinal))
                    _candidateStableCount++;
                else
                {
                    _candidateLetter = normalizedLetter;
                    _candidateStableCount = 1;
                }

                _pendingLetter = normalizedLetter;
                _nextUiApplyAt = Time.time + Mathf.Max(0f, uiDebounceSeconds);
                if (_candidateStableCount >= requiredStableFrames
                    && Time.time - _lastCommitAt >= Mathf.Max(0.01f, commitCooldownSeconds))
                {
                    _currentWordBuffer += _candidateLetter;
                    _historyText += _candidateLetter;
                    if (_historyText.Length > Mathf.Max(24, maxHistoryChars))
                    {
                        _historyText = _historyText.Substring(_historyText.Length - Mathf.Max(24, maxHistoryChars));
                    }
                    _lastCommitAt = Time.time;
                }

                if (letterText != null)
                    letterText.text = normalizedLetter;
            }
            else
            {
                _pendingLetter = null;
                _candidateLetter = "";
                _candidateStableCount = 0;
                if (letterText != null)
                    letterText.text = "…";
            }
        }

        string displayCaption = UpdateCaptionLinesFromBuffer();

        if (string.IsNullOrWhiteSpace(response.text))
            response.text = _historyText;

        _inferCaptionLine = FormatInferCaption(
            response,
            displayCaption,
            _historyText,
            _candidateLetter,
            _candidateStableCount,
            requiredStableFrames,
            noHand);
        ApplyCaptionToSubtitle();

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

    private void AppendWordToCaption(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        // Add word boundary space if line lacks one and is not empty
        string lastLine = _captionLines[1];
        if (lastLine.Length > 0 && !lastLine.EndsWith(" "))
        {
            lastLine += " ";
        }

        if (lastLine.Length + word.Length > maxCharsPerLine)
        {
            // Wrap to new line
            _captionLines[0] = _captionLines[1];
            _captionLines[1] = word;
        }
        else
        {
            _captionLines[1] = lastLine + word;
        }
    }

    private string UpdateCaptionLinesFromBuffer()
    {
        string displayStr = _captionLines[0];
        if (!string.IsNullOrEmpty(displayStr))
        {
            displayStr += "\n";
        }
        displayStr += _captionLines[1];

        // Append grey preview text of the current uncommitted word
        if (_currentWordBuffer.Length > 0)
        {
            if (displayStr.Length > 0 && !displayStr.EndsWith(" ") && !displayStr.EndsWith("\n"))
            {
                displayStr += " ";
            }
            displayStr += $"<color=#888888>{_currentWordBuffer}</color>";
        }

        // Output to main HUD caption
        if (_mainHudCaptionLabel != null)
        {
            _mainHudCaptionLabel.text = "";
            _mainHudCaptionLabel.style.display = DisplayStyle.None;
        }

        return displayStr;
    }

    private void UpdateHudOrientation()
    {
        if (!useBillboardHud || _hudObjects == null || _hudObjects.Count == 0) 
            return;
        
        Transform camTrans = Camera.main != null ? Camera.main.transform : null;
        if (camTrans == null) return;

        foreach (var go in _hudObjects)
        {
            if (go == null) continue;
            
            if (billboardLerpSpeed <= 0f)
            {
                go.transform.rotation = camTrans.rotation;
            }
            else
            {
                go.transform.rotation = Quaternion.Slerp(
                    go.transform.rotation, 
                    camTrans.rotation, 
                    Time.deltaTime * billboardLerpSpeed);
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
        // Keep idle state silent so it does not overwrite other UI text channels.
    }

    private void ApplyCaptionToSubtitle()
    {
        string caption = "";
        if (!string.IsNullOrEmpty(_inferCaptionLine))
        {
            caption = _inferCaptionLine;
        }
        else if (!string.IsNullOrEmpty(_cameraUserMessage))
        {
            caption = _cameraUserMessage;
        }
        else if (!string.IsNullOrEmpty(_lastNetworkError))
        {
            caption = _lastNetworkError;
        }
        #region agent log
        DebugWrite(
            "pre-fix",
            "H5",
            "SignInferenceClient.cs:ApplyCaptionToSubtitle",
            "Caption resolved",
            "{\"mode\":\"" + App.CurrentInputMode.ToString() + "\",\"signCaptureActive\":" + (signCaptureActive ? "true" : "false") + ",\"captionLen\":" + caption.Length.ToString(CultureInfo.InvariantCulture) + "}");
        #endregion

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
            _mainHudCaptionLabel.text = "";
            _mainHudCaptionLabel.style.display = DisplayStyle.None;
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
            error = "empty /predict body";
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

        bool? rawHandDetected = ReadJsonBoolField(json, "hand_detected");
        bool? rawNoHand = ReadJsonBoolField(json, "no_hand");
        if (r != null)
        {
            if (rawHandDetected.HasValue)
            {
                r.hand_detected = rawHandDetected.Value;
            }
            if (rawNoHand.HasValue)
            {
                r.no_hand = rawNoHand.Value;
            }
            else if (rawHandDetected.HasValue)
            {
                r.no_hand = !rawHandDetected.Value;
            }
        }

        if (r != null && InferResponseHasContent(r))
        {
            response = r;
            return true;
        }

        InferResponse manual = new InferResponse();
        manual.letter = ReadJsonStringField(json, "letter");
        if (string.IsNullOrEmpty(manual.letter))
        {
            manual.letter = ReadJsonStringField(json, "predicted_letter");
        }
        manual.text = ReadJsonStringField(json, "text");
        manual.status_hint = ReadJsonStringField(json, "status_hint");
        manual.model = ReadJsonStringField(json, "model");
        string confStr = ReadJsonNumberField(json, "confidence");
        if (!string.IsNullOrEmpty(confStr)
            && float.TryParse(confStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float cf))
        {
            manual.confidence = cf;
        }
        string marginStr = ReadJsonNumberField(json, "top2_margin");
        if (!string.IsNullOrEmpty(marginStr)
            && float.TryParse(marginStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float mg))
        {
            manual.top2_margin = mg;
        }
        manual.detail = ReadJsonStringField(json, "detail");

        bool? noHand = ReadJsonBoolField(json, "no_hand");
        if (noHand.HasValue)
        {
            manual.no_hand = noHand.Value;
        }
        bool? handDetected = ReadJsonBoolField(json, "hand_detected");
        if (handDetected.HasValue)
        {
            manual.hand_detected = handDetected.Value;
            if (!noHand.HasValue)
            {
                manual.no_hand = !handDetected.Value;
            }
        }

        // Gradio call API may wrap output in data[0] object (letter + hand_detected often only appear there).
        string wrapped = ReadFirstDataObject(json);
        if (!string.IsNullOrEmpty(wrapped))
        {
            if (string.IsNullOrEmpty(manual.letter))
            {
                manual.letter = ReadJsonStringField(wrapped, "predicted_letter");
                if (string.IsNullOrEmpty(manual.letter))
                {
                    manual.letter = ReadJsonStringField(wrapped, "letter");
                }
            }

            if (manual.confidence <= 0f)
            {
                string nestedConf = ReadJsonNumberField(wrapped, "confidence");
                if (!string.IsNullOrEmpty(nestedConf)
                    && float.TryParse(nestedConf, NumberStyles.Float, CultureInfo.InvariantCulture, out float nestedCf))
                {
                    manual.confidence = nestedCf;
                }
            }
            if (manual.top2_margin <= 0f)
            {
                string nestedMargin = ReadJsonNumberField(wrapped, "top2_margin");
                if (!string.IsNullOrEmpty(nestedMargin)
                    && float.TryParse(nestedMargin, NumberStyles.Float, CultureInfo.InvariantCulture, out float nestedMg))
                {
                    manual.top2_margin = nestedMg;
                }
            }
            if (string.IsNullOrEmpty(manual.detail))
            {
                manual.detail = ReadJsonStringField(wrapped, "detail");
            }

            if (!handDetected.HasValue)
            {
                bool? nestedHd = ReadJsonBoolField(wrapped, "hand_detected");
                if (nestedHd.HasValue)
                {
                    manual.hand_detected = nestedHd.Value;
                    if (!noHand.HasValue)
                    {
                        manual.no_hand = !nestedHd.Value;
                    }
                }
            }

            bool? nestedNoHand = ReadJsonBoolField(wrapped, "no_hand");
            if (nestedNoHand.HasValue)
            {
                manual.no_hand = nestedNoHand.Value;
            }
        }

        if (InferResponseHasContent(manual))
        {
            response = manual;
            return true;
        }

        error = error ?? "could not read predicted_letter/letter/text/status_hint from /predict JSON";
        return false;
    }

    private static bool InferResponseHasContent(InferResponse r)
    {
        if (r == null) return false;
        return r.no_hand
            || !r.hand_detected
            || !string.IsNullOrEmpty(r.letter)
            || !string.IsNullOrEmpty(r.text)
            || !string.IsNullOrEmpty(r.status_hint)
            || !string.IsNullOrEmpty(r.detail);
    }

    /// <summary>
    /// <see cref="InferResponse.hand_detected"/> defaults to <c>false</c> when JSON omits it (JsonUtility + Gradio).
    /// Do not treat that as "no hand" until we have ruled out <c>predicted_letter</c>/<c>letter</c>.
    /// </summary>
    private static bool IsNoHand(InferResponse r)
    {
        if (r == null)
        {
            return true;
        }

        if (r.no_hand)
        {
            return true;
        }

        if (string.Equals(r.letter, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (r.hand_detected)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(r.letter))
        {
            return false;
        }

        return true;
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

    private static string ExtractJsonObjectFromSse(string sse)
    {
        if (string.IsNullOrWhiteSpace(sse))
        {
            return sse;
        }

        string[] lines = sse.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line.Substring(5).Trim();
            string json = ExtractJsonObject(payload);
            if (!string.IsNullOrEmpty(json) && json.StartsWith("{", StringComparison.Ordinal))
            {
                return json;
            }
        }

        return ExtractJsonObject(sse);
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

    private static bool? ReadJsonBoolField(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        i = json.IndexOf(':', i);
        if (i < 0)
        {
            return null;
        }

        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i]))
        {
            i++;
        }

        if (i + 4 <= json.Length && json.Substring(i, 4).Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (i + 5 <= json.Length && json.Substring(i, 5).Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
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

    private static string ReadFirstDataObject(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        int dataIdx = json.IndexOf("\"data\"", StringComparison.Ordinal);
        if (dataIdx < 0)
        {
            return null;
        }

        int objStart = json.IndexOf('{', dataIdx);
        if (objStart < 0)
        {
            return null;
        }

        int depth = 0;
        for (int i = objStart; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '{') depth++;
            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return json.Substring(objStart, i - objStart + 1);
                }
            }
        }

        return null;
    }

    private string FormatInferCaption(
        InferResponse r,
        string displayCaption,
        string historyText,
        string candidateLetter,
        int candidateStableCount,
        int commitStableFrames,
        bool noHand)
    {
        if (r == null)
        {
            return "";
        }

        var parts = new List<string>();
        if (noHand)
        {
            parts.Add(noHandFriendlyMessage);
        }

        if (!string.IsNullOrWhiteSpace(displayCaption))
        {
            parts.Add(displayCaption.Trim());
        }
        else if (!string.IsNullOrEmpty(historyText))
        {
            parts.Add(historyText.TrimEnd());
        }
        else if (!string.IsNullOrEmpty(r.text))
        {
            parts.Add(r.text.Trim());
        }

        if (!string.IsNullOrEmpty(r.detail))
        {
            parts.Add("status: " + r.detail);
        }

        if (!string.IsNullOrEmpty(r.status_hint))
        {
            parts.Add(r.status_hint);
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "";
    }

    private string CorrectWordLocal(string rawWord)
    {
        if (!enableLocalWordCorrection || string.IsNullOrWhiteSpace(rawWord))
            return rawWord;

        string normalized = NormalizeWordToken(rawWord);
        if (string.IsNullOrEmpty(normalized))
            return rawWord;

        if (_commonTypoMap.TryGetValue(normalized, out string mapped))
            return mapped;

        if (_localCorrectionCache.TryGetValue(normalized, out string cached))
            return cached;

        int bestDistance = int.MaxValue;
        string best = normalized;
        int maxDist = Mathf.Clamp(localCorrectionMaxEditDistance, 1, 2);

        for (int i = 0; i < LocalCorrectionVocabulary.Length; i++)
        {
            string candidate = LocalCorrectionVocabulary[i];
            if (Mathf.Abs(candidate.Length - normalized.Length) > maxDist)
                continue;

            int d = BoundedLevenshteinDistance(normalized, candidate, maxDist);
            if (d >= 0 && d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
                if (d == 0) break;
            }
        }

        string corrected = bestDistance <= maxDist ? best : normalized;
        _localCorrectionCache[normalized] = corrected;
        return corrected;
    }

    private static string NormalizeWordToken(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string up = input.Trim().ToUpperInvariant();
        var sb = new StringBuilder(up.Length);
        char prev = '\0';
        int repeat = 0;
        for (int i = 0; i < up.Length; i++)
        {
            char c = up[i];
            bool keep = (c >= 'A' && c <= 'Z') || c == '\'';
            if (!keep) continue;

            if (c == prev)
            {
                repeat++;
                if (repeat > 2) continue;
            }
            else
            {
                prev = c;
                repeat = 1;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static int BoundedLevenshteinDistance(string a, string b, int maxDistance)
    {
        int n = a.Length;
        int m = b.Length;
        if (Mathf.Abs(n - m) > maxDistance) return -1;
        int[] prev = new int[m + 1];
        int[] curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            int rowMin = curr[0];
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int del = prev[j] + 1;
                int ins = curr[j - 1] + 1;
                int sub = prev[j - 1] + cost;
                int val = del < ins ? del : ins;
                if (sub < val) val = sub;
                curr[j] = val;
                if (val < rowMin) rowMin = val;
            }
            if (rowMin > maxDistance) return -1;
            var tmp = prev; prev = curr; curr = tmp;
        }

        return prev[m] <= maxDistance ? prev[m] : -1;
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
                        if (!_hudObjects.Contains(docs[i].gameObject))
                        {
                            _hudObjects.Add(docs[i].gameObject);
                        }
                        Debug.Log("[SignInferenceClient] Bound subtitle-text for sign captions (Billboarding enabled).");
                    }
                }

                if (_mainHudCaptionLabel == null)
                {
                    Label mainCap = root.Q<Label>("sign-inference-caption");
                    if (mainCap != null)
                    {
                        _mainHudCaptionLabel = mainCap;
                        if (!_hudObjects.Contains(docs[i].gameObject))
                        {
                            _hudObjects.Add(docs[i].gameObject);
                        }
                        Debug.Log("[SignInferenceClient] Bound sign-inference-caption on MainLayout (Billboarding enabled).");
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
        // After QueueInference increments _sendAttemptCount: save on 1st, (N+1)th, (2N+1)th, … so the first send is not skipped.
        if (((_sendAttemptCount - 1) % saveEveryNSends) != 0) return;

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
        if (UsesHuggingFaceSpaceApi())
        {
            string msg = $"Spell action not supported by this Space API: {path}";
            Debug.LogWarning("[SignInferenceClient] " + msg);
            _lastNetworkError = msg;
            OnNetworkError?.Invoke(msg);
            yield break;
        }

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

    /// <summary>
    /// If the inspector has the full endpoint (e.g. <c>http://PC:8010/predict</c>) while <see cref="inferEndpointPath"/>
    /// is also <c>/predict</c>, the client would POST to <c>.../predict/predict</c> (404). Strip a lone trailing route only.
    /// </summary>
    private static string NormalizeInferenceBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url ?? "";
        }

        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            return TrimTrailingSlash(url);
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.Equals("/predict", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/infer", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{uri.Authority}";
        }

        return TrimTrailingSlash(url);
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

    private bool UsesHuggingFaceSpaceApi()
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        return baseUrl.IndexOf("hf.space", StringComparison.OrdinalIgnoreCase) >= 0
            || baseUrl.IndexOf("huggingface.co", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
