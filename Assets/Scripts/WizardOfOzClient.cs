using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.Windows.Speech;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Unified controller for the Wizard of Oz Machine Translation Demo.
/// Handles UI Creation, Voice Recognition, and Network Communication.
/// </summary>
[DefaultExecutionOrder(-50)]
public class WizardOfOzClient : MonoBehaviour
{
    public static WizardOfOzClient Instance { get; private set; }
    /// <summary>Fired when Italian ASR mode (separate API URL) is enabled or disabled (sync MainLayout button).</summary>
    public static event Action<bool> OnItalianLocalAsrStateChanged;
    [Header("Settings")]
    public string serverIP = "localhost";
    public int serverPort = 18080;

    [Header("Translation API (Hugging Face NMT)")]
    [Tooltip("Base URL only; /translate is appended automatically.")]
    [SerializeField] private string translationBaseUrl = "https://marconolimits-nmt.hf.space";
    [Tooltip("Optional X-API-Key for the hosted NMT space. Leave empty when key enforcement is off.")]
    [SerializeField] private string translationApiKey = "";

    [Header("ASR — English")]
    [Tooltip("English ASR: POST float32 LE mono, X-Sample-Rate; JSON { \"text\" }. Used when Italian ASR mode is off.")]
    [SerializeField] private string asrApiUrl = "https://thedeezat-asr-hearing-impaired-api.hf.space/audio";

    [Header("ASR — Italian")]
    [Tooltip("Italian ASR: same wire format as English. Use your POST /audio proxy to the Italian Space (Gradio alone does not expose float32 /audio). JSON may include text + text_en / italian + english.")]
    [SerializeField] private string italianAsrApiUrl = "";

    [Tooltip("After this many consecutive failed HTTP requests, switch to built-in dictation.")]
    [SerializeField] private int asrFallbackAfterConsecutiveFailures = 3;
    [Tooltip("If true, repeated API HTTP failures switch to local dictation. Keep false to stay on API and retry.")]
    [SerializeField] private bool allowApiFailureFallback = false;
    [Tooltip("If true, repeated empty API responses switch to local dictation. Keep false to avoid disconnects.")]
    [SerializeField] private bool allowApiEmptyResponseFallback = false;

    [Tooltip("API-only: silence after last partial before phrase end (~850 ms matches realtime proxy VAD). Dictation fallback ignores this.")]
    [SerializeField] private float asrPhraseEndSilenceSeconds = 0.85f;
    [Tooltip("If true, API mode can auto-fallback when no transcript is received for too long while speaking. Keep false to avoid silence-triggered mode switches.")]
    [SerializeField] private bool allowApiSilenceAutoFallback = false;
    [Header("ASR Runtime Tuning")]
    [Tooltip("English API tuning: chunk length in seconds.")]
    [SerializeField] private float englishAsrChunkSeconds = 1.8f;
    [Tooltip("English API tuning: max send window in seconds.")]
    [SerializeField] private float englishAsrSendWindowSeconds = 2.2f;
    [Tooltip("English API tuning: skip sending chunks quieter than this RMS.")]
    [SerializeField] private float englishAsrMinChunkRms = 0.0065f;
    [Tooltip("English API tuning: adaptive gain target RMS.")]
    [SerializeField] private float englishAsrAdaptiveGainTargetRms = 0.038f;
    [Tooltip("English API tuning: adaptive gain cap.")]
    [SerializeField] private float englishAsrAdaptiveGainMax = 8f;
    [Tooltip("English mode: start with on-device dictation, and switch to API only if local dictation errors.")]
    [SerializeField] private bool preferLocalEnglishAsrWithApiFallback = false;
    [Tooltip("Italian API tuning: slightly longer chunk reduces empty-string responses on softer speech.")]
    [SerializeField] private float italianAsrChunkSeconds = 1.8f;
    [Tooltip("Italian API tuning: window length in seconds.")]
    [SerializeField] private float italianAsrSendWindowSeconds = 2.2f;
    [Tooltip("Italian API tuning: lower silence gate to avoid dropping soft speech.")]
    [SerializeField] private float italianAsrMinChunkRms = 0.0065f;
    [Tooltip("Italian API tuning: higher target RMS for clearer backend input.")]
    [SerializeField] private float italianAsrAdaptiveGainTargetRms = 0.040f;
    [Tooltip("Italian API tuning: adaptive gain cap.")]
    [SerializeField] private float italianAsrAdaptiveGainMax = 8f;

    // UI Master Components (from legacy App.cs)
    private GameObject _mainUIRoot;
    private Camera _mainCam;
    private UIDocument _uiDoc;
    private RenderTexture _uiRT;

    // Sub-Managers
    private NetworkManager _network;
    private HybridVoiceManager _voice;
    private UIManager _uiManager;

    [Header("Voice UI")]
    [Tooltip("How long to wait (after last listening/hypothesis activity or ASR HTTP completion) before showing a stall hint. Remote ASR first request can be slow; see also skip while request in flight.")]
    [SerializeField] private float listeningStallSeconds = 90f;

    [Tooltip("Minimum time between stall messages so they do not spam every stall interval.")]
    [SerializeField] private float stallMessageCooldownSeconds = 120f;
    [Tooltip("Throttle ASR partial-caption refreshes to avoid UI hitches while clicking buttons.")]
    [SerializeField] private float hypothesisUiUpdateMinIntervalSeconds = 0.14f;

    [Tooltip("If false, never show the stall hint. When true, deadline refreshes on each ASR HTTP round-trip so empty transcripts do not trigger a false stall.")]
    [SerializeField] private bool showListeningStallHint = true;
    [Header("Subtitle panel placement")]
    [SerializeField] private int subtitleRenderTextureWidth = 1400;
    [SerializeField] private int subtitleRenderTextureHeight = 520;
    [SerializeField] private float subtitlePanelWidthMeters = 1.12f;
    [SerializeField] private float subtitlePanelHeightMeters = 0.5f;
    [SerializeField] private float subtitleDistanceMeters = 1.35f;
    [SerializeField] private float subtitleVerticalOffsetMeters = -0.30f;
    [SerializeField] private bool autoResizeSubtitlePanel = false;
    [SerializeField] private float subtitleAutoHeightPerLineMeters = 0.06f;
    [SerializeField] private float subtitlePanelMinHeightMeters = 0.28f;
    [SerializeField] private float subtitlePanelMaxHeightMeters = 1.1f;

    private float _listeningStallDeadline = -1f;
    private float _nextStallMessageAllowedTime;
    private string _lastModeBanner = "";
    private bool _italianAsrModeEnabled;
    private bool _asrActive;
    private Transform _subtitleQuadTransform;
    private BoxCollider _subtitleQuadCollider;
    private int _subtitleEstimatedLines = 1;
    private float _lastHypothesisUiUpdateAt = -999f;
    private string _lastHypothesisUiText = "";
    public bool IsItalianLocalAsrEnabled => _italianAsrModeEnabled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoStart()
    {
        if (FindObjectOfType<WizardOfOzClient>() == null)
        {
            Debug.Log("[WizardOfOz] Auto-starting unified client...");
            GameObject go = new GameObject("WIZARD_OF_OZ_CLIENT");
            go.AddComponent<WizardOfOzClient>();
            DontDestroyOnLoad(go);
        }
    }

    private void Awake()
    {
        Instance = this;
        Debug.Log("[WizardOfOz] Unified Client Awake.");
        _mainCam = ResolveMainCamera();
    }

    private static Camera ResolveMainCamera()
    {
        if (Camera.main != null)
        {
            Camera.main.stereoTargetEye = StereoTargetEyeMask.Both;
            return Camera.main;
        }

        Camera[] cameras = FindObjectsOfType<Camera>();
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera c = cameras[i];
            if (c != null && c.enabled && c.gameObject.activeInHierarchy)
            {
                c.stereoTargetEye = StereoTargetEyeMask.Both;
                return c;
            }
        }

        return null;
    }

    private IEnumerator Start()
    {
        Debug.Log("[WizardOfOz] Starting initialization sequence...");
        
        // Wait one frame to avoid Unity UI Toolkit "SendMessage" warnings during scene settle
        yield return null;

        InitializeUI();
        Debug.Log("[WizardOfOz] UI Created. Waiting 1 frame for UIDocument to bind...");
        
        // CRITICAL: Wait one more frame so the UIDocument can internalize the UXML
        yield return null;

        // Setup Managers
        if (_uiDoc != null && _uiDoc.rootVisualElement != null)
        {
            try {
                EnsureManagersForVoice();
                _asrActive = App.CurrentInputMode == App.InputMode.Asr;
                if (_asrActive)
                {
                    CreateAndStartVoiceManager();
                }
                
                Debug.Log("[WizardOfOz] System READY.");
            } catch (Exception e) {
                Debug.LogError($"[WizardOfOz] Manager Setup Failed: {e.Message}");
            }
        }
        else
        {
            Debug.LogError("[WizardOfOz] Critical Failure: UIDocument or rootVisualElement is null.");
        }
    }

    private void InitializeUI()
    {
        _mainUIRoot = new GameObject("TranslationPanel");
        
        // 1. Render Texture
        _uiRT = new RenderTexture(
            Mathf.Clamp(subtitleRenderTextureWidth, 800, 3000),
            Mathf.Clamp(subtitleRenderTextureHeight, 220, 1400),
            24);
        _uiRT.name = "WizRT";

        // 2. Panel Settings
        var originalSettings = Resources.Load<PanelSettings>("UI/DefaultPanelSettings");
        if (originalSettings == null) {
            Debug.LogError("[WizardOfOz] Could not load DefaultPanelSettings from Resources/UI/");
            return;
        }

        PanelSettings settings = Instantiate(originalSettings);
        settings.targetTexture = _uiRT;
        settings.scaleMode = PanelScaleMode.ConstantPixelSize;
        settings.clearColor = true;

        // 3. UIDocument
        GameObject uiObj = new GameObject("UIDoc");
        uiObj.transform.SetParent(_mainUIRoot.transform);
        _uiDoc = uiObj.AddComponent<UIDocument>();
        _uiDoc.visualTreeAsset = Resources.Load<VisualTreeAsset>("UI/SubtitleLayout");
        _uiDoc.panelSettings = settings;

        // 4. Visual Quad
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "PanelQuad";
        quad.transform.SetParent(_mainUIRoot.transform);
        quad.transform.localScale = new Vector3(
            Mathf.Clamp(subtitlePanelWidthMeters, 0.8f, 2.5f),
            Mathf.Clamp(subtitlePanelHeightMeters, 0.2f, 1.5f),
            1f);
        _subtitleQuadTransform = quad.transform;

        quad.GetComponent<Renderer>().material = WorldUiQuadMaterial.Create(_uiRT);

        // 5. Keep subtitle panel display-only (non-interactive) so it never steals XR ray hits from action buttons.
        Destroy(quad.GetComponent<Collider>());
        _subtitleQuadCollider = null;

        // Position it in front of camera
        if (_mainCam != null)
        {
            AttachSubtitleToCamera();
        }
    }

    private void AttachSubtitleToCamera()
    {
        if (_mainUIRoot == null || _mainCam == null)
        {
            return;
        }

        // World-anchored subtitle: do not parent to camera, do not use local transforms.
        _mainUIRoot.transform.SetParent(null, true);
        Vector3 target = _mainCam.transform.position + (_mainCam.transform.forward * Mathf.Clamp(subtitleDistanceMeters, 0.6f, 2.5f));
        target += _mainCam.transform.up * Mathf.Clamp(subtitleVerticalOffsetMeters, -0.6f, 0.6f);
        _mainUIRoot.transform.position = target;
        FaceSubtitleLikeSidebar();
    }

    private void FaceSubtitleLikeSidebar()
    {
        if (_mainUIRoot == null || _mainCam == null)
        {
            return;
        }

        _mainUIRoot.transform.LookAt(_mainCam.transform.position, Vector3.up);
        _mainUIRoot.transform.Rotate(0f, 180f, 0f);
    }

    private static string StallHintText()
    {
#if UNITY_WSA && !UNITY_EDITOR
        return "Still waiting for speech. Check HoloLens microphone permissions and speak clearly. Listening continues.";
#else
        return "Still waiting for speech. Check the microphone and speech privacy settings. Listening continues.";
#endif
    }

    /// <summary>Subtitle while waiting for speech (mode-specific label).</summary>
    private string ListeningIdleCaption()
    {
        return _italianAsrModeEnabled ? "Listening for ITA ..." : "Listening for ENG ...";
    }

    private string ListeningPartialCaption(string partial)
    {
        string prefix = _italianAsrModeEnabled ? "Listening for ITA ..." : "Listening for ENG ...";
        return string.IsNullOrEmpty(partial)
            ? prefix
            : $"{prefix} {partial}";
    }

    private void WireEvents()
    {
        if (_voice == null || _uiManager == null) return;

        _voice.OnListeningStarted += () => MainThreadDispatcher.RunOnMainThread(() => {
            if (App.CurrentInputMode != App.InputMode.Asr) return;
            _listeningStallDeadline = Time.time + listeningStallSeconds;
            _uiManager.UpdateText(ListeningIdleCaption());
        });

        _voice.OnHypothesis += (partial) => MainThreadDispatcher.RunOnMainThread(() => {
            if (App.CurrentInputMode != App.InputMode.Asr) return;
            _listeningStallDeadline = Time.time + listeningStallSeconds;
            if (!string.IsNullOrEmpty(partial)) {
                string caption = ListeningPartialCaption(partial);
                float minInterval = Mathf.Clamp(hypothesisUiUpdateMinIntervalSeconds, 0.05f, 0.5f);
                if (caption != _lastHypothesisUiText || Time.time - _lastHypothesisUiUpdateAt >= minInterval)
                {
                    _lastHypothesisUiText = caption;
                    _lastHypothesisUiUpdateAt = Time.time;
                    _uiManager.UpdateText(caption);
                }
            }
        });

        _voice.OnSpeechBargeIn += () => MainThreadDispatcher.RunOnMainThread(() => {
            if (App.CurrentInputMode != App.InputMode.Asr) return;
            _listeningStallDeadline = Time.time + listeningStallSeconds;
            _uiManager.UpdateText(ListeningIdleCaption());
        });

        _voice.OnSentenceCompleted += (text) => {
            MainThreadDispatcher.RunOnMainThread(() => {
                if (App.CurrentInputMode != App.InputMode.Asr) return;
                _listeningStallDeadline = -1f;
                _nextStallMessageAllowedTime = 0f;
                if (_italianAsrModeEnabled || App.IsTranslationEnabled)
                {
                    return;
                }

                _uiManager.UpdateText(text);
            });
            if (App.CurrentInputMode != App.InputMode.Asr)
                return;

            if (_italianAsrModeEnabled)
            {
                string it = string.IsNullOrWhiteSpace(text) ? "..." : text.Trim();
                string en = HololensAsrManager.Instance != null ? HololensAsrManager.Instance.LastEnglishFromApi : null;
                if (!string.IsNullOrWhiteSpace(en))
                {
                    MainThreadDispatcher.RunOnMainThread(() => _uiManager.UpdateText($"ITA: {it}\nENG: {en.Trim()}"));
                    return;
                }

                _network.SendTranslationRequest(text, (resp) => {
                    MainThreadDispatcher.RunOnMainThread(() =>
                        _uiManager.UpdateText($"ITA: {it}\nENG: {(string.IsNullOrWhiteSpace(resp) ? it : resp.Trim())}"));
                });
                return;
            }

            if (!App.IsTranslationEnabled)
                return;

            _network.SendTranslationRequest(text, (resp) => {
                MainThreadDispatcher.RunOnMainThread(() => _uiManager.UpdateText(resp));
            });
        };

        _voice.OnError += (err) => MainThreadDispatcher.RunOnMainThread(() => {
            if (App.CurrentInputMode != App.InputMode.Asr) return;
            _listeningStallDeadline = -1f;
            if (string.IsNullOrEmpty(err)) return;
            if (err.StartsWith(HybridVoiceManager.AsrFallbackUserMessage, StringComparison.Ordinal))
            {
                // Fallback already starts local dictation; do not show a system message in the caption.
                _uiManager.UpdateText(ListeningIdleCaption());
                return;
            }

            // Keep subtitles focused on speech content; non-fatal ASR/network diagnostics stay in logs.
            _uiManager.UpdateText(ListeningIdleCaption());
        });

    }

    private string ResolveEffectiveAsrApiUrl()
    {
        if (!_italianAsrModeEnabled)
            return NormalizeAsrAudioPostUrl(asrApiUrl != null ? asrApiUrl.Trim() : "");

        string it = italianAsrApiUrl != null ? italianAsrApiUrl.Trim() : "";
        if (!string.IsNullOrEmpty(it))
            return NormalizeAsrAudioPostUrl(it);

        Debug.LogWarning(
            "[WizardOfOz] Italian ASR URL is empty — set Wizard Italian ASR URL to your POST /audio proxy (same float32 + X-Sample-Rate contract). Using English ASR URL until configured.");
        return NormalizeAsrAudioPostUrl(asrApiUrl != null ? asrApiUrl.Trim() : "");
    }

    private static string NormalizeAsrAudioPostUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        string u = raw.Trim();
        if (!Uri.TryCreate(u, UriKind.Absolute, out Uri uri))
        {
            return u;
        }

        string path = (uri.AbsolutePath ?? "").Trim();
        bool hasAudioPath = path.EndsWith("/audio", StringComparison.OrdinalIgnoreCase);
        bool hasTranscribePath = path.EndsWith("/transcribe", StringComparison.OrdinalIgnoreCase);

        if (!hasAudioPath)
        {
            if (hasTranscribePath)
            {
                string basePart = u.Substring(0, u.Length - "/transcribe".Length);
                return basePart.TrimEnd('/') + "/audio";
            }

            if (path == "/" || string.IsNullOrEmpty(path))
            {
                return u.TrimEnd('/') + "/audio";
            }
        }

        return u;
    }

    private void CreateAndStartVoiceManager()
    {
        try
        {
            _voice?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[WizardOfOz] Dispose old voice failed: " + ex.Message);
        }

        EnsureAsrManager();
        ApplyAsrRuntimeTuningForCurrentMode();
        HololensAsrManager.Instance.SetForcedLanguage(_italianAsrModeEnabled ? "italian" : "english");

        _voice = new HybridVoiceManager(
            this,
            ResolveEffectiveAsrApiUrl(),
            asrFallbackAfterConsecutiveFailures,
            asrPhraseEndSilenceSeconds,
            false,
            allowApiSilenceAutoFallback,
            allowApiFailureFallback,
            allowApiEmptyResponseFallback,
            preferLocalEnglishAsrWithApiFallback && !_italianAsrModeEnabled,
            false);
        WireEvents();
        _voice.Start();
    }

    private static void EnsureAsrManager()
    {
        if (HololensAsrManager.Instance != null)
        {
            return;
        }

        var go = new GameObject("HololensAsrManager");
        DontDestroyOnLoad(go);
        go.AddComponent<HololensAsrManager>();
    }

    private void ApplyAsrRuntimeTuningForCurrentMode()
    {
        if (HololensAsrManager.Instance == null)
        {
            return;
        }

        if (_italianAsrModeEnabled)
        {
            HololensAsrManager.Instance.SetRuntimeTuning(
                italianAsrChunkSeconds,
                italianAsrSendWindowSeconds,
                italianAsrMinChunkRms,
                italianAsrAdaptiveGainTargetRms,
                italianAsrAdaptiveGainMax);
            return;
        }

        HololensAsrManager.Instance.SetRuntimeTuning(
            englishAsrChunkSeconds,
            englishAsrSendWindowSeconds,
            englishAsrMinChunkRms,
            englishAsrAdaptiveGainTargetRms,
            englishAsrAdaptiveGainMax);
    }

    private void ToggleItalianLocalAsr()
    {
        SetItalianLocalAsrEnabled(!_italianAsrModeEnabled);
    }

    public void SetItalianLocalAsrEnabled(bool enabled)
    {
        if (_italianAsrModeEnabled == enabled)
        {
            return;
        }

        _italianAsrModeEnabled = enabled;
        _listeningStallDeadline = -1f;
        _nextStallMessageAllowedTime = 0f;
        OnItalianLocalAsrStateChanged?.Invoke(_italianAsrModeEnabled);

        if (!_italianAsrModeEnabled)
        {
            _uiManager?.UpdateText("");
        }

        // Ensure ITA ON immediately starts capture even when ASR was idle.
        if (_italianAsrModeEnabled && !_asrActive)
        {
            SetAsrActive(true);
        }
        else if (_asrActive)
        {
            CreateAndStartVoiceManager();
        }
    }

    /// <summary>Clears subtitle caption (e.g. when switching to Sign or English ASR).</summary>
    public void ClearSubtitleCaption()
    {
        _uiManager?.UpdateText("");
    }

    /// <summary>Subtitle panel + network must exist before <see cref="HybridVoiceManager"/> wires UI callbacks.</summary>
    private void EnsureManagersForVoice()
    {
        if (_uiDoc == null || _uiDoc.rootVisualElement == null)
        {
            return;
        }

        if (_uiManager == null)
        {
            _uiManager = new UIManager(_uiDoc);
            _uiManager.OnEstimatedLineCountChanged += OnSubtitleEstimatedLineCountChanged;
        }

        if (_network == null)
        {
            _network = new NetworkManager(translationBaseUrl, translationApiKey);
        }

        SubscribeAsrStallReset();
    }

    private IEnumerator CoSetAsrActiveWhenManagersReady()
    {
        float deadline = Time.realtimeSinceStartup + 10f;
        while (_uiManager == null && Time.realtimeSinceStartup < deadline)
        {
            EnsureManagersForVoice();
            yield return null;
        }

        if (_uiManager == null)
        {
            Debug.LogError("[WizardOfOz] ASR cannot start: subtitle UIManager not ready after wait.");
            _asrActive = false;
            yield break;
        }

        CreateAndStartVoiceManager();
    }

    public void SetAsrActive(bool active)
    {
        if (_asrActive == active) return;
        _asrActive = active;

        if (_asrActive)
        {
            EnsureManagersForVoice();
            if (_uiManager == null)
            {
                StartCoroutine(CoSetAsrActiveWhenManagersReady());
                return;
            }

            CreateAndStartVoiceManager();
            return;
        }

        _listeningStallDeadline = -1f;
        _nextStallMessageAllowedTime = 0f;
        try
        {
            _voice?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[WizardOfOz] Dispose voice when ASR off: " + ex.Message);
        }

        _voice = null;
        if (App.CurrentInputMode == App.InputMode.Sign)
        {
            var signClient = FindObjectOfType<SignInferenceClient>();
            if (signClient != null)
            {
                signClient.SetSignCaptureActive(true);
            }
        }
    }

    private void Update()
    {
        if (_mainCam == null)
            _mainCam = ResolveMainCamera();

        if (_mainUIRoot != null && _mainCam != null)
        {
            ApplyDynamicSubtitlePanelHeight();
            Vector3 target = _mainCam.transform.position + (_mainCam.transform.forward * Mathf.Clamp(subtitleDistanceMeters, 0.6f, 2.5f));
            target += _mainCam.transform.up * Mathf.Clamp(subtitleVerticalOffsetMeters, -0.6f, 0.6f);
            _mainUIRoot.transform.position = Vector3.Lerp(_mainUIRoot.transform.position, target, Time.deltaTime * 6.0f);
            FaceSubtitleLikeSidebar();
        }

        if (_uiManager != null)
        {
            if (App.CurrentInputMode == App.InputMode.Sign)
            {
                if (_lastModeBanner != "sign")
                {
                    _lastModeBanner = "sign";
                    // Subtitle is driven by SignInferenceClient (API caption); do not clear or overwrite here.
                }
            }
            else if (App.CurrentInputMode == App.InputMode.None)
            {
                if (_lastModeBanner != "none")
                {
                    _lastModeBanner = "none";
                    _uiManager.UpdateText("");
                }
            }
            else
            {
                _lastModeBanner = "";
            }
        }

        // Do not show stall while an ASR HTTP request is still running (cold remote can take >60s; deadline only refreshes on response).
        if (App.CurrentInputMode == App.InputMode.Asr
            && showListeningStallHint
            && _listeningStallDeadline > 0f
            && Time.time >= _listeningStallDeadline
            && _uiManager != null
            && !(HololensAsrManager.Instance != null && HololensAsrManager.Instance.IsApiRequestInFlight))
        {
            if (Time.time < _nextStallMessageAllowedTime)
            {
                _listeningStallDeadline = Time.time + listeningStallSeconds;
            }
            else
            {
                _nextStallMessageAllowedTime = Time.time + stallMessageCooldownSeconds;
                _listeningStallDeadline = -1f;
                _uiManager.UpdateText(StallHintText());
            }
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            Debug.Log("[WizardOfOz] DIAGNOSTIC: Simulation triggered.");
            if (_voice != null) {
                _voice.OnSentenceCompleted?.Invoke("The robot is learning to translate.");
            } else {
                Debug.LogWarning("[WizardOfOz] Simulation skipped: VoiceManager is not initialized yet.");
            }
        }
    }

    /// <summary>Empty JSON text responses do not fire OnHypothesis; we still extend the stall deadline on each HTTP round-trip.</summary>
    private void SubscribeAsrStallReset()
    {
        if (HololensAsrManager.Instance == null) return;
        HololensAsrManager.Instance.OnApiRequestFinished -= OnAsrHttpRoundTrip;
        HololensAsrManager.Instance.OnApiRequestFinished += OnAsrHttpRoundTrip;
    }

    private void OnAsrHttpRoundTrip(bool success)
    {
        MainThreadDispatcher.RunOnMainThread(() =>
        {
            if (_listeningStallDeadline > 0f)
                _listeningStallDeadline = Time.time + listeningStallSeconds;
        });
    }

    private void OnSubtitleEstimatedLineCountChanged(int lines)
    {
        _subtitleEstimatedLines = Mathf.Max(1, lines);
    }

    private void ApplyDynamicSubtitlePanelHeight()
    {
        if (!autoResizeSubtitlePanel || _subtitleQuadTransform == null)
        {
            return;
        }

        float baseHeight = Mathf.Clamp(subtitlePanelHeightMeters, 0.2f, 1.5f);
        float autoHeight = baseHeight + Mathf.Max(0, _subtitleEstimatedLines - 1) * Mathf.Clamp(subtitleAutoHeightPerLineMeters, 0.02f, 0.2f);
        float targetHeight = Mathf.Clamp(
            autoHeight,
            Mathf.Clamp(subtitlePanelMinHeightMeters, 0.2f, 2f),
            Mathf.Clamp(subtitlePanelMaxHeightMeters, 0.2f, 2f));

        Vector3 s = _subtitleQuadTransform.localScale;
        float lerped = Mathf.Lerp(s.y, targetHeight, Time.deltaTime * 7f);
        _subtitleQuadTransform.localScale = new Vector3(s.x, lerped, s.z);

        if (_subtitleQuadCollider != null)
        {
            _subtitleQuadCollider.size = new Vector3(1f, 1f, 0.05f);
        }
    }

    private void OnApplicationQuit()
    {
        try
        {
            _voice?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[WizardOfOz] Dispose voice on quit: " + ex.Message);
        }
    }

    private void OnDestroy()
    {
        if (HololensAsrManager.Instance != null)
            HololensAsrManager.Instance.OnApiRequestFinished -= OnAsrHttpRoundTrip;
        if (_uiManager != null)
        {
            _uiManager.OnEstimatedLineCountChanged -= OnSubtitleEstimatedLineCountChanged;
        }
        try
        {
            _voice?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[WizardOfOz] Dispose voice on destroy: " + ex.Message);
        }

        _uiRT?.Release();
        if (Instance == this) Instance = null;
    }
}

// Support Classes (Internal for maximal reliability)

public class UIManager
{
    private Label _label;
    private Button _startBtn;
    public Action<int> OnEstimatedLineCountChanged;

    public System.Action OnStartPressed;
    public System.Action OnStopPressed;

    public UIManager(UIDocument doc) {
        _label = doc.rootVisualElement.Q<Label>("subtitle-text");
        if (_label != null) {
            _label.text = "";
            _label.style.display = DisplayStyle.None;
        }
    }
    public void UpdateText(string t)
    {
        if (_label == null) return;
        bool hasText = !string.IsNullOrWhiteSpace(t);
        _label.style.display = hasText ? DisplayStyle.Flex : DisplayStyle.None;
        string content = hasText ? t : "";
        _label.text = content;
        OnEstimatedLineCountChanged?.Invoke(EstimateWrappedLineCount(content));
    }

    private static int EstimateWrappedLineCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 1;
        }

        const int approxCharsPerLine = 34;
        int lines = 0;
        string[] hardLines = text.Split('\n');
        for (int i = 0; i < hardLines.Length; i++)
        {
            string ln = hardLines[i] ?? string.Empty;
            int len = ln.Trim().Length;
            lines += Mathf.Max(1, Mathf.CeilToInt(len / (float)approxCharsPerLine));
        }

        return Mathf.Clamp(lines, 1, 12);
    }
}

public class NetworkManager
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private DateTime _nextConnectionLogAllowedAt = DateTime.MinValue;
    private int _translationRequestSeq;

    public NetworkManager(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl != null ? baseUrl.Trim().TrimEnd('/') : "";
        _apiKey = apiKey != null ? apiKey.Trim() : "";
    }

    public void SendTranslationRequest(string text, Action<string> cb) {
        _ = SendTranslationRequestAsync(text, cb);
    }

    private async System.Threading.Tasks.Task SendTranslationRequestAsync(string text, Action<string> cb) {
        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(text)) {
            cb?.Invoke(text);
            return;
        }

        string requestId = "nmt-" + System.Threading.Interlocked.Increment(ref _translationRequestSeq).ToString("D4");
        try {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(70);
                string url = _baseUrl + "/translate";
                string translated = await TranslateOnce(client, url, text, requestId);
                if (string.IsNullOrWhiteSpace(translated) || string.Equals(translated, text, StringComparison.Ordinal))
                {
                    // HF Spaces may cold-start; retry once.
                    translated = await TranslateOnce(client, url, text, requestId);
                }

                Debug.Log("[NetworkManager] " + requestId + " complete.");
                cb?.Invoke(string.IsNullOrWhiteSpace(translated) ? text : translated);
            }
        } catch (Exception e) {
            if (DateTime.UtcNow >= _nextConnectionLogAllowedAt) {
                Debug.LogWarning("[NetworkManager] " + requestId + " translation server unreachable at " + _baseUrl + "/translate. Keeping ASR text. " + e.Message);
                _nextConnectionLogAllowedAt = DateTime.UtcNow.AddSeconds(8);
            }
            cb?.Invoke(text);
        }
    }

    private static string EscapeJsonString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string ExtractTranslation(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        Match m = Regex.Match(json, "\"translation\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        return Regex.Unescape(m.Groups["v"].Value).Trim();
    }

    private async System.Threading.Tasks.Task<string> TranslateOnce(HttpClient client, string url, string text, string requestId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent("{\"text\":\"" + EscapeJsonString(text) + "\"}", Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
        }

        var resp = await client.SendAsync(request);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if (DateTime.UtcNow >= _nextConnectionLogAllowedAt)
            {
                Debug.LogWarning("[NetworkManager] " + requestId + " NMT translate failed HTTP " + (int)resp.StatusCode + " at " + url + ". " + body);
                _nextConnectionLogAllowedAt = DateTime.UtcNow.AddSeconds(8);
            }
            return text;
        }

        string translated = ExtractTranslation(body);
        return string.IsNullOrWhiteSpace(translated) ? text : translated;
    }
}

public class VoiceManager : IDisposable
{
    private const float RestartSettleSeconds = 0.22f;

    private DictationRecognizer _r;
    private bool _disposed;

    private bool _restartPipelineRunning;
    private bool _restartPending;
    private DictationCompletionCause _pendingRestartCause = DictationCompletionCause.Complete;

    public Action OnListeningStarted;
    /// <summary>Partial recognition text — use for live subtitle, not the same as session start.</summary>
    public Action<string> OnHypothesis;
    public Action<string> OnSentenceCompleted;
    public Action<string> OnError;

    public VoiceManager() {
        _r = CreateRecognizer();
    }

    private DictationRecognizer CreateRecognizer() {
        var r = new DictationRecognizer();
        // Defaults are short: long pauses fire DictationComplete and feel like ASR "turned off".
        // Keep sessions alive across normal silence between words/phrases.
        r.InitialSilenceTimeoutSeconds = 120f;
        r.AutoSilenceTimeoutSeconds = 60f;
        r.DictationResult += OnDictationResult;
        r.DictationHypothesis += OnDictationHypothesis;
        r.DictationError += OnDictationError;
        r.DictationComplete += OnDictationComplete;
        return r;
    }

    private void OnDictationResult(string text, ConfidenceLevel confidence) {
        MainThreadDispatcher.RunOnMainThread(() => OnSentenceCompleted?.Invoke(text));
    }

    private void OnDictationHypothesis(string text) {
        MainThreadDispatcher.RunOnMainThread(() => OnHypothesis?.Invoke(text));
    }

    private void OnDictationError(string error, int hresult) {
        MainThreadDispatcher.RunOnMainThread(() =>
            OnError?.Invoke(string.IsNullOrEmpty(error) ? $"HRESULT: {hresult}" : $"{error} (HRESULT: {hresult})"));
        ScheduleRestart(DictationCompletionCause.UnknownError);
    }

    private void OnDictationComplete(DictationCompletionCause cause) {
        Debug.Log($"[VoiceManager] Dictation completed because: {cause}. Scheduling restart...");
        ScheduleRestart(cause);
    }

    private static bool ShouldRecreateRecognizer(DictationCompletionCause cause) {
        switch (cause) {
            case DictationCompletionCause.UnknownError:
            case DictationCompletionCause.AudioQualityFailure:
            case DictationCompletionCause.MicrophoneUnavailable:
            case DictationCompletionCause.NetworkFailure:
            case DictationCompletionCause.TimeoutExceeded:
                return true;
            default:
                return false;
        }
    }

    public void Start() {
        if (_disposed || _r == null) return;
        if (_r.Status != SpeechSystemStatus.Running) {
            EnsurePhraseSystemForDictation();
            try {
                _r.Start();
                MainThreadDispatcher.RunOnMainThread(() => OnListeningStarted?.Invoke());
            } catch (Exception ex) {
                Debug.LogError($"[VoiceManager] Start failed: {ex}");
                MainThreadDispatcher.RunOnMainThread(() => OnError?.Invoke(ex.Message));
            }
        }
    }

    private static void EnsurePhraseSystemForDictation() {
        if (PhraseRecognitionSystem.Status == SpeechSystemStatus.Running) {
            Debug.Log("[VoiceManager] Stopping PhraseRecognitionSystem to prevent conflict...");
            try {
                PhraseRecognitionSystem.Shutdown();
            } catch (Exception ex) {
                Debug.LogWarning($"[VoiceManager] PhraseRecognitionSystem.Shutdown: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Dictation callbacks can run off the main thread; Stop/Start must run on the main thread.
    /// Restarts are serialized so rapid DictationComplete events do not overlap Stop/Start (a common source of flakiness).
    /// A short delay after Stop lets the Windows / HoloLens speech stack settle.
    /// </summary>
    private void ScheduleRestart(DictationCompletionCause cause) {
        if (_disposed) return;
        _pendingRestartCause = cause;
        _restartPending = true;
        if (_restartPipelineRunning) {
            return;
        }
        MainThreadDispatcher.RunCoroutine(RestartPipeline());
    }

    private IEnumerator RestartPipeline() {
        _restartPipelineRunning = true;
        try {
            while (_restartPending && !_disposed) {
                _restartPending = false;
                yield return RestartOneSession(_pendingRestartCause);
            }
        } finally {
            _restartPipelineRunning = false;
        }
    }

    private IEnumerator RestartOneSession(DictationCompletionCause cause) {
        if (_disposed) yield break;

        if (ShouldRecreateRecognizer(cause)) {
            Debug.Log($"[VoiceManager] Recreating DictationRecognizer after {cause}.");
            DisposeRecognizerOnly();
            _r = CreateRecognizer();
        } else {
            try {
                if (_r != null && _r.Status == SpeechSystemStatus.Running) {
                    _r.Stop();
                }
            } catch (Exception ex) {
                Debug.LogWarning($"[VoiceManager] Stop during restart: {ex.Message}");
            }
        }

        yield return null;
        yield return new WaitForSecondsRealtime(RestartSettleSeconds);

        if (_disposed || _r == null) yield break;

        EnsurePhraseSystemForDictation();

        bool needSecondChance = false;
        try {
            _r.Start();
            MainThreadDispatcher.RunOnMainThread(() => OnListeningStarted?.Invoke());
        } catch (Exception ex) {
            Debug.LogError($"[VoiceManager] Restart Start failed: {ex}");
            MainThreadDispatcher.RunOnMainThread(() => OnError?.Invoke(ex.Message));
            DisposeRecognizerOnly();
            _r = CreateRecognizer();
            needSecondChance = true;
        }

        if (needSecondChance) {
            yield return null;
            yield return new WaitForSecondsRealtime(RestartSettleSeconds);
            if (_disposed || _r == null) yield break;
            try {
                EnsurePhraseSystemForDictation();
                _r.Start();
                MainThreadDispatcher.RunOnMainThread(() => OnListeningStarted?.Invoke());
            } catch (Exception ex2) {
                Debug.LogError($"[VoiceManager] Second-chance Start failed: {ex2}");
                MainThreadDispatcher.RunOnMainThread(() => OnError?.Invoke(ex2.Message));
            }
        }
    }

    private void DisposeRecognizerOnly() {
        if (_r == null) return;
        try {
            if (_r.Status == SpeechSystemStatus.Running) {
                _r.Stop();
            }
        } catch (Exception ex) {
            Debug.LogWarning($"[VoiceManager] Stop before dispose: {ex.Message}");
        }
        try {
            _r.DictationResult -= OnDictationResult;
            _r.DictationHypothesis -= OnDictationHypothesis;
            _r.DictationError -= OnDictationError;
            _r.DictationComplete -= OnDictationComplete;
            _r.Dispose();
        } catch (Exception ex) {
            Debug.LogWarning($"[VoiceManager] DictationRecognizer dispose: {ex.Message}");
        }
        _r = null;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _restartPending = false;
        DisposeRecognizerOnly();
    }
}

public class MainThreadDispatcher : MonoBehaviour
{
    private static readonly Queue<Action> _q = new Queue<Action>();
    private static MainThreadDispatcher _instance;

    public static void RunOnMainThread(Action a) { lock (_q) { _q.Enqueue(a); } }

    /// <summary>Queues a coroutine to run on the Unity main thread (for non-MonoBehaviour callers).</summary>
    public static void RunCoroutine(IEnumerator routine) {
        RunOnMainThread(() => {
            if (_instance != null) {
                _instance.StartCoroutine(routine);
            }
        });
    }

    private void Awake() { _instance = this; }

    private void Update()
    {
        lock (_q)
        {
            while (_q.Count > 0)
            {
                Action action = _q.Dequeue();
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[MainThreadDispatcher] callback failed: " + ex);
                }
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init() {
        var go = new GameObject("Dispatcher");
        go.AddComponent<MainThreadDispatcher>();
        DontDestroyOnLoad(go);
    }
}
