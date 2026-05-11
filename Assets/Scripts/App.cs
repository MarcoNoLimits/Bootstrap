using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit;

public class App : MonoBehaviour
{
    private static App _instance;
    public enum InputMode { None, Asr, Sign }
    public static InputMode CurrentInputMode { get; private set; } = InputMode.None;
    public static bool IsTranslationEnabled { get; private set; }
    public static bool IsItalianTranslationEnabled { get; private set; }
    private GameObject _mainUI;
    private Camera _mainCam;
    
    [Header("World placement")]
    [SerializeField] private float _distance = 1.1f;
    [SerializeField] private float _smoothSpeed = 4f;
    [Tooltip("Positive = right side of the view (camera +X).")]
    [SerializeField] private float _rightOffsetMeters = 0.12f;
    [Tooltip("Optional vertical nudge (camera +Y). Higher moves the panel up.")]
    [SerializeField] private float _verticalOffsetMeters = 0.04f;
    [Header("Scene Background")]
    [SerializeField] private Color _sceneBackgroundColor = new Color(0f, 0f, 0f, 0f);

    // UI DIMENSIONS (must match USS .glass-panel-minimal)
    private float _uiWidth = 260f;
    private float _uiHeight = 420f;
    private float _scale = 0.001f;

    private bool _audioOn;
    private bool _translationOn;
    private bool _italianTranslationOn;
    private bool _signOn;
    private Button _translationToggleBtn;
    private Button _itaTranslationToggleBtn;
    private Button _asrToggleBtn;
    private Button _signToggleBtn;
    private Button _itaToggleBtn;
    private Label _asrTopInstructionLabel;
    private Coroutine _asrTopInstructionCo;
    private bool _italianAsrOn;
    private float _nextSignCaptureEnsureAt;
    private UIDocument _uiDocInstance;
    private Renderer _uiRenderer;
    private RenderTexture _uiRenderTexture;
    private float _nextUiHealthLogAt;
    private bool _uiRebuildAttempted;
    private float _nextFacingLogAt;
    private float _uiInitTime;
    [Header("ASR Instruction")]
    [SerializeField] private string _asrTopInstructionText = "Tip: A longer pause means end of sentence.";
    [SerializeField] private float _asrTopInstructionVisibleSeconds = 150f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoStart()
    {
        if (FindObjectOfType<App>() != null) return;
        var go = new GameObject("APP_UI_CLIENT");
        go.AddComponent<App>();
        DontDestroyOnLoad(go);
        Debug.Log("[App] Auto-started MainLayout UI.");
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("[App] Duplicate App detected, destroying this instance.");
            Destroy(gameObject);
            return;
        }
        _instance = this;
    }

    private void Start()
    {
        if (_mainUI == null)
        {
            InitializeUI();
        }

        _mainCam = ResolveMainCamera();
        CurrentInputMode = InputMode.None;
        IsTranslationEnabled = false;

        // Initial jump to front (so you don't have to wait for it to fly in)
        if (_mainCam != null)
        {
            EnsureStereoBothEyes(_mainCam);
            _mainCam.clearFlags = CameraClearFlags.SolidColor;
            _mainCam.backgroundColor = _sceneBackgroundColor;
            UpdatePosition(true); // true = instant snap
        }
    }

    private void Update()
    {
        if (_mainCam == null)
        {
            _mainCam = ResolveMainCamera();
            if (_mainCam != null)
            {
                EnsureStereoBothEyes(_mainCam);
                _mainCam.clearFlags = CameraClearFlags.SolidColor;
                _mainCam.backgroundColor = _sceneBackgroundColor;
            }
            return;
        }

        // Run the "Tag-Along" logic every frame
        UpdatePosition(false); // false = smooth movement
        ValidateUiHealth();

        // Safety: keep sign capture active whenever Sign mode is active.
        // Other systems can occasionally toggle it off; re-assert at a low rate.
        if (CurrentInputMode == InputMode.Sign && Time.time >= _nextSignCaptureEnsureAt)
        {
            _nextSignCaptureEnsureAt = Time.time + 0.6f;
            FindObjectOfType<SignInferenceClient>()?.SetSignCaptureActive(true);
        }
    }

    private void UpdatePosition(bool instant)
    {
        if (_mainUI == null) return;
        if (!_mainUI.activeSelf) _mainUI.SetActive(true);

        // 1. Target position: in front of camera; optional small +X via _rightOffsetMeters
        Transform cam = _mainCam.transform;
        float halfFovRad = 0.5f * _mainCam.fieldOfView * Mathf.Deg2Rad;
        float visibleHalfHeightAtDistance = Mathf.Tan(halfFovRad) * _distance;
        // Keep the panel safely inside the upper/lower visible region even if serialized values are extreme.
        float clampedVerticalOffset = Mathf.Clamp(_verticalOffsetMeters, -visibleHalfHeightAtDistance * 0.8f, visibleHalfHeightAtDistance * 0.8f);

        Vector3 targetPos = cam.position
            + cam.forward * _distance
            + cam.right * _rightOffsetMeters
            + cam.up * clampedVerticalOffset;

        // 2. Move
        if (instant)
        {
            _mainUI.transform.position = targetPos;
        }
        else
        {
            _mainUI.transform.position = Vector3.Lerp(_mainUI.transform.position, targetPos, Time.deltaTime * _smoothSpeed);
        }

        // 3. Rotate to face camera with yaw only (keeps panel upright; avoids pitch/roll skew).
        Vector3 toCamera = _mainCam.transform.position - _mainUI.transform.position;
        Vector3 flatToCamera = Vector3.ProjectOnPlane(toCamera, Vector3.up);
        if (flatToCamera.sqrMagnitude > 0.0001f)
        {
            // Quad front faces opposite in this setup; flip yaw so visible face and hit mapping align.
            _mainUI.transform.rotation = Quaternion.LookRotation(-flatToCamera.normalized, Vector3.up);
        }
        if (Time.time >= _nextFacingLogAt)
        {
            _nextFacingLogAt = Time.time + 3f;
            Vector3 toCameraNorm = (_mainCam.transform.position - _mainUI.transform.position).normalized;
            float facingDot = Vector3.Dot(_mainUI.transform.forward, toCameraNorm);
            if (facingDot < 0.15f)
            {
                Debug.LogWarning("[App] UI quad appears back-facing (dot=" + facingDot.ToString("F2") + ").");
            }
        }

        // Safety: if any script disabled the quad renderer, force it back on.
        var renderer = _mainUI.GetComponentInChildren<Renderer>(true);
        if (renderer != null && !renderer.enabled) renderer.enabled = true;
    }

    private void InitializeUI()
    {
        _mainUI = new GameObject("MainUI");
        
        // 1. Setup Render Texture
        // Match the UI dimensions exactly so it fills the quad
        int webWidth = Mathf.RoundToInt(_uiWidth);
        int webHeight = Mathf.RoundToInt(_uiHeight);
        RenderTexture rt = new RenderTexture(webWidth, webHeight, 24);
        rt.name = "UIRenderTexture";
        rt.Create();
        _uiRenderTexture = rt;

        // 2. Setup Panel Settings
        var originalSettings = Resources.Load<PanelSettings>("UI/DefaultPanelSettings");
        PanelSettings runtimeSettings;
        if (originalSettings != null)
        {
            runtimeSettings = Instantiate(originalSettings);
        }
        else
        {
            Debug.LogError("[App] DefaultPanelSettings not found at Resources/UI/DefaultPanelSettings — creating fallback.");
            runtimeSettings = ScriptableObject.CreateInstance<PanelSettings>();
        }
        runtimeSettings.targetTexture = rt;
        runtimeSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
        runtimeSettings.scale = 1.0f;
        runtimeSettings.clearColor = true;
        runtimeSettings.colorClearValue = Color.clear;

        // 3. Setup UIDocument
        var mainLayout = Resources.Load<VisualTreeAsset>("UI/MainLayout");
        if (mainLayout == null)
        {
            Debug.LogError("[App] MainLayout.uxml not found at Resources/UI/MainLayout — UI cannot render.");
            throw new System.InvalidOperationException("Missing UI/MainLayout resource.");
        }
        GameObject uiLogicObject = new GameObject("UILogic");
        uiLogicObject.transform.SetParent(_mainUI.transform, false);
        var uiDoc = uiLogicObject.AddComponent<UIDocument>();
        uiDoc.visualTreeAsset = mainLayout;
        uiDoc.panelSettings = runtimeSettings;
        _uiDocInstance = uiDoc;

        // 4. Quad + MeshCollider (must match rendered UI): BoxCollider hits do not give mesh UVs; manual
        // local→panel mapping was wrong for top/bottom rows after LookAt, so only the middle (SLR) worked.
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(_mainUI.transform, false);
        quad.name = "UIQuad";

        var uiMeshCollider = quad.GetComponent<MeshCollider>();
        uiMeshCollider.sharedMesh = quad.GetComponent<MeshFilter>().sharedMesh;
        uiMeshCollider.convex = false;

        // 5. Material (project shader — built-in Unlit/* is often stripped on UWP/IL2CPP → magenta quad)
        _uiRenderer = quad.GetComponent<Renderer>();
        _uiRenderer.material = WorldUiQuadMaterial.Create(rt);
        if (_uiRenderer.material == null)
        {
            Debug.LogError("[App] WorldUiQuad material is null. UI texture will not render on quad.");
        }
        else
        {
            // Force a visible unlit surface on device (prevents hidden/transparent material states).
            Material mat = _uiRenderer.material;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            mat.renderQueue = 3000;
        }

        // 6. Scale the Quad to match physical size
        // _uiWidth = 800, _scale = 0.001 => 0.8m width
        // _uiHeight = 500 => 0.5m height
        float physicalWidth = _uiWidth * _scale;
        float physicalHeight = _uiHeight * _scale;
        
        quad.transform.localScale = new Vector3(physicalWidth, physicalHeight, 1f);

        // 7. Interaction Bridge
        // Automatically attach the input bridge so we don't depend on manual setup
        var bridge = _mainUI.AddComponent<WorldUIInputBridge>();
        bridge.uiDoc = uiDoc;
        bridge.renderTexture = rt;
        bridge.targetCollider = uiMeshCollider;
        
        // 8. XR Interaction Setup
        // Add XRSimpleInteractable so hand rays can "Select" (Click) the Quad
        var interactable = _mainUI.AddComponent<XRSimpleInteractable>();
        interactable.colliders.Clear();
        interactable.colliders.Add(uiMeshCollider);
        
        // Wire up the event
        interactable.selectEntered.AddListener(bridge.OnSelectEntered);
        interactable.hoverEntered.AddListener(bridge.OnHoverEntered);
        interactable.hoverExited.AddListener(bridge.OnHoverExited);

        // 9. Root UI + debug strip (ignore picks so hits go to buttons)
        var root = uiDoc.rootVisualElement;
        _asrTopInstructionLabel = root.Q<Label>("asr-top-instruction");
        if (_asrTopInstructionLabel != null)
        {
            _asrTopInstructionLabel.text = "";
            _asrTopInstructionLabel.style.display = DisplayStyle.None;
            _asrTopInstructionLabel.pickingMode = PickingMode.Ignore;
        }

        // Labels in the action rail (sign caption, instruction, debug) must not participate in panel.Pick
        // or they steal XR ray-mapped clicks from the buttons in Italian / Sign modes.
        var actionRail = root.Q<VisualElement>("action-rail");
        if (actionRail != null)
        {
            actionRail.Query<Label>().ForEach(l => l.pickingMode = PickingMode.Ignore);
        }

        // 10. Pinch / click feedback (placeholder until wired to real audio / SLR / settings)
        var btnAudio = root.Q<Button>("btn-audio-toggle");
        _asrToggleBtn = btnAudio;
        if (btnAudio != null)
        {
            btnAudio.text = "English Captions";
            btnAudio.clicked += () =>
            {
                _audioOn = !_audioOn;
                btnAudio.text = _audioOn ? "English Captions · On" : "English Captions";
                btnAudio.EnableInClassList("action-rail-btn-on", _audioOn);

                var wizard = WizardOfOzClient.Instance;
                if (_audioOn && _italianAsrOn)
                {
                    _italianAsrOn = false;
                    if (_itaToggleBtn != null)
                    {
                        _itaToggleBtn.text = "Switch to Italian Captions";
                        _itaToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }

                    wizard?.SetItalianLocalAsrEnabled(false);
                }

                if (_audioOn)
                {
                    wizard?.ClearSubtitleCaption();
                    _translationOn = true;
                    IsTranslationEnabled = true;
                    _italianTranslationOn = false;
                    IsItalianTranslationEnabled = false;
                }

                CurrentInputMode = (_audioOn || _italianAsrOn) ? InputMode.Asr : (_signOn ? InputMode.Sign : InputMode.None);
                IsTranslationEnabled = _audioOn && _translationOn;
                HololensAsrManager.Instance?.SetForcedLanguage(_audioOn ? "english" : (_italianAsrOn ? "italian" : ""));
                SetAsrCaptureActive(_audioOn || _italianAsrOn);
                if (_audioOn)
                {
                    ShowAsrInstructionTemporarily();
                }
                else if (!_italianAsrOn)
                {
                    HideAsrInstruction();
                }

                var signClient = FindObjectOfType<SignInferenceClient>();
                if (_audioOn && signClient != null)
                {
                    signClient.ClearSignHistory("switch_to_asr");
                    signClient.SetSignCaptureActive(false);
                }

                if (_audioOn && _signOn)
                {
                    _signOn = false;
                    var btnSign = root.Q<Button>("btn-slr-capture");
                    if (btnSign != null)
                    {
                        btnSign.text = "Sign Language";
                        btnSign.EnableInClassList("action-rail-btn-on", false);
                    }
                    if (signClient != null)
                    {
                        signClient.ClearSignHistory("switch_to_asr");
                        signClient.SetSignCaptureActive(false);
                    }
                }

                if (_translationToggleBtn != null)
                {
                    _translationToggleBtn.style.display = _audioOn ? DisplayStyle.Flex : DisplayStyle.None;
                    if (_audioOn)
                    {
                        _translationToggleBtn.text = _translationOn ? "Italian Translation · On" : "Italian Translation · Off";
                        _translationToggleBtn.EnableInClassList("action-rail-btn-on", _translationOn);
                    }
                    if (!_audioOn)
                    {
                        _translationOn = false;
                        IsTranslationEnabled = false;
                        _translationToggleBtn.text = "Italian Translation · Off";
                        _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }
                }

                if (_itaTranslationToggleBtn != null)
                {
                    _itaTranslationToggleBtn.style.display = DisplayStyle.None;
                    _italianTranslationOn = false;
                    IsItalianTranslationEnabled = false;
                    _itaTranslationToggleBtn.text = "English Translation · Off";
                    _itaTranslationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                }

            };
        }

        var btnSlr = root.Q<Button>("btn-slr-capture");
        _signToggleBtn = btnSlr;
        if (btnSlr != null)
        {
            btnSlr.text = "Sign Language";
            btnSlr.clicked += () =>
            {
                _signOn = !_signOn;
                btnSlr.text = _signOn ? "Sign Language · On" : "Sign Language";
                btnSlr.EnableInClassList("action-rail-btn-on", _signOn);
                if (_signOn)
                {
                    WizardOfOzClient.Instance?.ClearSubtitleCaption();
                }
                else
                {
                    // When exiting sign mode, clear lingering sign caption immediately.
                    WizardOfOzClient.Instance?.ClearSubtitleCaption();
                }
                CurrentInputMode = _signOn ? InputMode.Sign : ((_audioOn || _italianAsrOn) ? InputMode.Asr : InputMode.None);
                if (_signOn) IsTranslationEnabled = false;
                if (_signOn) IsItalianTranslationEnabled = false;

                if (_signOn && (_audioOn || _italianAsrOn))
                {
                    _audioOn = false;
                    btnAudio.text = "English Captions";
                    btnAudio.EnableInClassList("action-rail-btn-on", false);
                    if (_italianAsrOn)
                    {
                        _italianAsrOn = false;
                        if (_itaToggleBtn != null)
                        {
                            _itaToggleBtn.text = "Switch to Italian Captions";
                            _itaToggleBtn.EnableInClassList("action-rail-btn-on", false);
                        }

                        WizardOfOzClient.Instance?.SetItalianLocalAsrEnabled(false);
                    }

                    if (_translationToggleBtn != null)
                    {
                        _translationOn = false;
                        IsTranslationEnabled = false;
                        _translationToggleBtn.text = "Italian Translation · Off";
                        _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                        _translationToggleBtn.style.display = DisplayStyle.None;
                    }
                    if (_itaTranslationToggleBtn != null)
                    {
                        _italianTranslationOn = false;
                        IsItalianTranslationEnabled = false;
                        _itaTranslationToggleBtn.text = "English Translation · Off";
                        _itaTranslationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                        _itaTranslationToggleBtn.style.display = DisplayStyle.None;
                    }

                    SetAsrCaptureActive(false);
                }

                var signClient = FindObjectOfType<SignInferenceClient>();
                if (signClient != null)
                {
                    if (_signOn)
                    {
                        signClient.ClearSignHistory("switch_to_slr");
                    }
                    else
                    {
                        signClient.ClearSignHistory("switch_off_slr");
                    }
                    signClient.SetSignCaptureActive(_signOn);
                }
            };
        }

        _translationToggleBtn = root.Q<Button>("btn-translation-toggle");
        if (_translationToggleBtn != null)
        {
            _translationToggleBtn.style.display = DisplayStyle.None;
            _translationToggleBtn.clicked += () =>
            {
                if (!_audioOn) return;
                _translationOn = !_translationOn;
                IsTranslationEnabled = _audioOn && _translationOn;
                _translationToggleBtn.text = _translationOn ? "Italian Translation · On" : "Italian Translation · Off";
                _translationToggleBtn.EnableInClassList("action-rail-btn-on", _translationOn);
                // Translation toggle must never stop ASR capture.
                SetAsrCaptureActive(_audioOn || _italianAsrOn);
                WizardOfOzClient.Instance?.NotifyTranslationDisplayModeChanged();
            };
        }

        _itaTranslationToggleBtn = root.Q<Button>("btn-ita-translation-toggle");
        if (_itaTranslationToggleBtn != null)
        {
            _itaTranslationToggleBtn.style.display = DisplayStyle.None;
            _itaTranslationToggleBtn.clicked += () =>
            {
                if (!_italianAsrOn) return;
                _italianTranslationOn = !_italianTranslationOn;
                IsItalianTranslationEnabled = _italianAsrOn && _italianTranslationOn;
                _itaTranslationToggleBtn.text = _italianTranslationOn ? "English Translation · On" : "English Translation · Off";
                _itaTranslationToggleBtn.EnableInClassList("action-rail-btn-on", _italianTranslationOn);
                // Translation toggle must never stop ASR capture.
                SetAsrCaptureActive(_audioOn || _italianAsrOn);
                WizardOfOzClient.Instance?.NotifyTranslationDisplayModeChanged();
            };
        }

        _itaToggleBtn = root.Q<Button>("btn-ita-asr-toggle");
        if (_itaToggleBtn != null)
        {
            _italianAsrOn = false;
            _itaToggleBtn.text = "Switch to Italian Captions";
            _itaToggleBtn.EnableInClassList("action-rail-btn-on", false);
            _itaToggleBtn.clicked += () =>
            {
                var wizard = WizardOfOzClient.Instance;
                if (wizard == null)
                {
                    return;
                }

                wizard.SetItalianLocalAsrEnabled(!_italianAsrOn);
                _italianAsrOn = wizard.IsItalianLocalAsrEnabled;
                _itaToggleBtn.text = _italianAsrOn ? "Italian Captions · On" : "Switch to Italian Captions";
                _itaToggleBtn.EnableInClassList("action-rail-btn-on", _italianAsrOn);
                if (_italianAsrOn)
                {
                    _italianTranslationOn = true;
                    IsItalianTranslationEnabled = true;
                }

                if (_italianAsrOn && _signOn)
                {
                    _signOn = false;
                    if (_signToggleBtn != null)
                    {
                        _signToggleBtn.text = "Sign Language";
                        _signToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }

                    var signClient = FindObjectOfType<SignInferenceClient>();
                    if (signClient != null)
                    {
                        signClient.ClearSignHistory("switch_to_ita_asr");
                        signClient.SetSignCaptureActive(false);
                    }
                }

                if (_italianAsrOn && _audioOn)
                {
                    _audioOn = false;
                    if (_asrToggleBtn != null)
                    {
                        _asrToggleBtn.text = "English Captions";
                        _asrToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }

                    if (_translationToggleBtn != null)
                    {
                        _translationOn = false;
                        _translationToggleBtn.style.display = DisplayStyle.None;
                        _translationToggleBtn.text = "Italian Translation · Off";
                        _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }
                }

                if (_itaTranslationToggleBtn != null)
                {
                    _itaTranslationToggleBtn.style.display = _italianAsrOn ? DisplayStyle.Flex : DisplayStyle.None;
                    if (_italianAsrOn)
                    {
                        _itaTranslationToggleBtn.text = _italianTranslationOn ? "English Translation · On" : "English Translation · Off";
                        _itaTranslationToggleBtn.EnableInClassList("action-rail-btn-on", _italianTranslationOn);
                    }
                    if (!_italianAsrOn)
                    {
                        _italianTranslationOn = false;
                        IsItalianTranslationEnabled = false;
                        _itaTranslationToggleBtn.text = "English Translation · Off";
                        _itaTranslationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }
                }

                CurrentInputMode = _italianAsrOn
                    ? InputMode.Asr
                    : (_audioOn ? InputMode.Asr : (_signOn ? InputMode.Sign : InputMode.None));
                IsTranslationEnabled = _audioOn && _translationOn;
                HololensAsrManager.Instance?.SetForcedLanguage(_italianAsrOn ? "italian" : (_audioOn ? "english" : ""));
                SetAsrCaptureActive(_italianAsrOn || _audioOn);
                if (_italianAsrOn)
                {
                    ShowAsrInstructionTemporarily();
                }
                else if (!_audioOn)
                {
                    HideAsrInstruction();
                }
            };
        }

        WizardOfOzClient.OnItalianLocalAsrStateChanged += SyncItalianToggleFromWizard;

        var debugHudLabel = root.Q<Label>("xr-debug-hud");
        if (debugHudLabel != null)
        {
            debugHudLabel.pickingMode = PickingMode.Ignore;
            debugHudLabel.style.display = DisplayStyle.None;
        }

        InitializeDefaultMode();
        _uiInitTime = Time.time;
        Debug.Log("[App] UI initialized: MainUI + UIDocument + RT quad are set.");
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
        WizardOfOzClient.OnItalianLocalAsrStateChanged -= SyncItalianToggleFromWizard;
        HideAsrInstruction();
    }

    private void SyncItalianToggleFromWizard(bool enabled)
    {
        _italianAsrOn = enabled;
        if (_itaToggleBtn == null)
        {
            return;
        }

        _itaToggleBtn.text = enabled ? "Italian Captions · On" : "Switch to Italian Captions";
        _itaToggleBtn.EnableInClassList("action-rail-btn-on", enabled);

        if (_itaTranslationToggleBtn != null)
        {
            _itaTranslationToggleBtn.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
            if (enabled)
            {
                _italianTranslationOn = true;
                IsItalianTranslationEnabled = true;
                _itaTranslationToggleBtn.text = "English Translation · On";
                _itaTranslationToggleBtn.EnableInClassList("action-rail-btn-on", true);
            }
            else
            {
                _italianTranslationOn = false;
                IsItalianTranslationEnabled = false;
                _itaTranslationToggleBtn.text = "English Translation · Off";
                _itaTranslationToggleBtn.EnableInClassList("action-rail-btn-on", false);
            }
        }

        if (_translationToggleBtn != null)
        {
            _translationToggleBtn.style.display = (_audioOn && !enabled) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        if (enabled)
        {
            ShowAsrInstructionTemporarily();
        }
        else if (!_audioOn)
        {
            HideAsrInstruction();
        }
    }

    private void ShowAsrInstructionTemporarily()
    {
        if (_asrTopInstructionLabel == null || string.IsNullOrWhiteSpace(_asrTopInstructionText))
        {
            return;
        }

        _asrTopInstructionLabel.text = _asrTopInstructionText.Trim();
        _asrTopInstructionLabel.style.display = DisplayStyle.Flex;

        if (_asrTopInstructionCo != null)
        {
            StopCoroutine(_asrTopInstructionCo);
            _asrTopInstructionCo = null;
        }

        _asrTopInstructionCo = StartCoroutine(CoHideAsrInstructionAfterDelay());
    }

    private System.Collections.IEnumerator CoHideAsrInstructionAfterDelay()
    {
        yield return new WaitForSecondsRealtime(Mathf.Clamp(_asrTopInstructionVisibleSeconds, 5f, 600f));
        HideAsrInstruction();
    }

    private void HideAsrInstruction()
    {
        if (_asrTopInstructionCo != null)
        {
            StopCoroutine(_asrTopInstructionCo);
            _asrTopInstructionCo = null;
        }

        if (_asrTopInstructionLabel != null)
        {
            _asrTopInstructionLabel.text = "";
            _asrTopInstructionLabel.style.display = DisplayStyle.None;
        }
    }

    /// <summary>HoloLens/XR: prefer MainCamera; fallback to any enabled camera so world UI still parents correctly.</summary>
    private static Camera ResolveMainCamera()
    {
        if (Camera.main != null)
            return Camera.main;

        Camera[] cameras = FindObjectsOfType<Camera>();
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera c = cameras[i];
            if (c != null && c.enabled && c.gameObject.activeInHierarchy)
                return c;
        }

        return null;
    }

    private static void EnsureStereoBothEyes(Camera cam)
    {
        if (cam == null) return;
        if (cam.stereoTargetEye != StereoTargetEyeMask.Both)
        {
            cam.stereoTargetEye = StereoTargetEyeMask.Both;
        }
    }

    private void InitializeDefaultMode()
    {
        _audioOn = false;
        _translationOn = false;
        _italianTranslationOn = false;
        _signOn = false;
        _italianAsrOn = false;
        CurrentInputMode = InputMode.None;
        IsTranslationEnabled = false;
        IsItalianTranslationEnabled = false;

        if (_asrToggleBtn != null)
        {
            _asrToggleBtn.text = "English Captions";
            _asrToggleBtn.EnableInClassList("action-rail-btn-on", false);
        }

        if (_signToggleBtn != null)
        {
            _signToggleBtn.text = "Sign Language";
            _signToggleBtn.EnableInClassList("action-rail-btn-on", false);
        }

        if (_translationToggleBtn != null)
        {
            _translationToggleBtn.text = "Italian Translation · Off";
            _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
            _translationToggleBtn.style.display = DisplayStyle.None;
        }

        if (_itaTranslationToggleBtn != null)
        {
            _itaTranslationToggleBtn.text = "English Translation · Off";
            _itaTranslationToggleBtn.EnableInClassList("action-rail-btn-on", false);
            _itaTranslationToggleBtn.style.display = DisplayStyle.None;
        }

        if (_itaToggleBtn != null)
        {
            _itaToggleBtn.text = "Switch to Italian Captions";
            _itaToggleBtn.EnableInClassList("action-rail-btn-on", false);
        }

        WizardOfOzClient.Instance?.ClearSubtitleCaption();

        var signClient = FindObjectOfType<SignInferenceClient>();
        if (signClient != null)
        {
            signClient.ClearSignHistory("initialize_default_mode");
            signClient.SetSignCaptureActive(false);
        }

        SetAsrCaptureActive(false);
    }

    private static void SetAsrCaptureActive(bool active)
    {
        var wizard = WizardOfOzClient.Instance;
        if (wizard != null)
        {
            wizard.SetAsrActive(active);
            return;
        }

        var asr = HololensAsrManager.Instance;
        if (asr == null)
        {
            return;
        }

        if (active)
        {
            asr.StartAsr();
        }
        else
        {
            asr.StopAsr();
        }
    }

    private void ValidateUiHealth()
    {
        if (Time.time < _nextUiHealthLogAt) return;
        _nextUiHealthLogAt = Time.time + 2.5f;

        bool ok = true;
        if (_mainUI == null)
        {
            ok = false;
            Debug.LogError("[App] UI health: MainUI root is null.");
        }
        else if (!_mainUI.activeInHierarchy)
        {
            ok = false;
            Debug.LogError("[App] UI health: MainUI exists but is inactive in hierarchy.");
        }

        if (_uiDocInstance == null)
        {
            ok = false;
            Debug.LogError("[App] UI health: UIDocument component missing.");
        }
        else if (_uiDocInstance.rootVisualElement == null)
        {
            ok = false;
            Debug.LogError("[App] UI health: UIDocument rootVisualElement is null.");
        }
        else if (_uiDocInstance.rootVisualElement.childCount == 0)
        {
            ok = false;
            Debug.LogError("[App] UI health: rootVisualElement has zero children (layout not bound).");
        }

        if (_uiRenderer == null)
        {
            ok = false;
            Debug.LogError("[App] UI health: Quad renderer missing.");
        }
        else if (!_uiRenderer.enabled)
        {
            ok = false;
            Debug.LogError("[App] UI health: Quad renderer disabled.");
        }
        else if (_uiRenderer.sharedMaterial == null)
        {
            ok = false;
            Debug.LogError("[App] UI health: Quad material missing.");
        }
        else if (_uiRenderer.sharedMaterial.mainTexture == null)
        {
            ok = false;
            Debug.LogError("[App] UI health: Quad material has no mainTexture.");
        }

        if (_uiRenderTexture == null)
        {
            ok = false;
            Debug.LogError("[App] UI health: RenderTexture is null.");
        }
        else if (!_uiRenderTexture.IsCreated())
        {
            // Editor startup can report false negatives in first moments after Play.
            bool warmupExpired = (Time.time - _uiInitTime) > 2.0f;
            if (warmupExpired)
            {
                ok = false;
                Debug.LogError("[App] UI health: RenderTexture not created.");
            }
            else
            {
                _uiRenderTexture.Create();
            }
        }

        if (ok)
        {
            Debug.Log("[App] UI health: OK.");
            return;
        }

        if (_uiRebuildAttempted) return;
        _uiRebuildAttempted = true;
        Debug.LogError("[App] UI health failed. Rebuilding UI once...");

        if (_mainUI != null)
        {
            Destroy(_mainUI);
            _mainUI = null;
        }

        _uiDocInstance = null;
        _uiRenderer = null;
        _uiRenderTexture = null;
        InitializeUI();
        UpdatePosition(true);
    }
}