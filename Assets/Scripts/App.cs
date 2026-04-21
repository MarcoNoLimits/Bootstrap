using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit;

public class App : MonoBehaviour
{
    public enum InputMode { None, Asr, Sign }
    public static InputMode CurrentInputMode { get; private set; } = InputMode.None;
    public static bool IsTranslationEnabled { get; private set; }
    private GameObject _mainUI;
    private Camera _mainCam;
    
    [Header("World placement")]
    [SerializeField] private float _distance = 1.1f;
    [SerializeField] private float _smoothSpeed = 4f;
    [Tooltip("Positive = right side of the view (camera +X).")]
    [SerializeField] private float _rightOffsetMeters = 0.18f;
    [Tooltip("Optional vertical nudge (camera +Y). Higher moves the panel up.")]
    [SerializeField] private float _verticalOffsetMeters = 0.58f;
    [Header("Scene Background")]
    [SerializeField] private Color _sceneBackgroundColor = new Color(0f, 0f, 0f, 0f);

    // UI DIMENSIONS (must match USS .glass-panel-minimal)
    private float _uiWidth = 260f;
    private float _uiHeight = 420f;
    private float _scale = 0.001f;

    private bool _audioOn;
    private bool _translationOn;
    private bool _signOn;
    private Button _translationToggleBtn;
    private Button _asrToggleBtn;
    private Button _signToggleBtn;
    private Button _itaToggleBtn;
    private bool _italianAsrOn;
    private float _nextSignCaptureEnsureAt;

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
        InitializeUI();
    }

    private void Start()
    {
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

        // 1. Target position: in front of camera; optional small +X via _rightOffsetMeters
        Transform cam = _mainCam.transform;
        Vector3 targetPos = cam.position
            + cam.forward * _distance
            + cam.right * _rightOffsetMeters
            + cam.up * _verticalOffsetMeters;

        // 2. Move
        if (instant)
        {
            _mainUI.transform.position = targetPos;
        }
        else
        {
            _mainUI.transform.position = Vector3.Lerp(_mainUI.transform.position, targetPos, Time.deltaTime * _smoothSpeed);
        }

        // 3. Rotate to face camera (simple LookAt for Quad)
        _mainUI.transform.LookAt(_mainCam.transform.position, Vector3.up);
        _mainUI.transform.Rotate(0, 180, 0); // Quads face backwards effectively
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
            Debug.LogError("[App] MainLayout.uxml not found at Resources/UI/MainLayout — UI will be blank.");
        }
        GameObject uiLogicObject = new GameObject("UILogic");
        uiLogicObject.transform.SetParent(_mainUI.transform, false);
        var uiDoc = uiLogicObject.AddComponent<UIDocument>();
        uiDoc.visualTreeAsset = mainLayout;
        uiDoc.panelSettings = runtimeSettings;

        // 4. Quad + MeshCollider (must match rendered UI): BoxCollider hits do not give mesh UVs; manual
        // local→panel mapping was wrong for top/bottom rows after LookAt, so only the middle (SLR) worked.
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(_mainUI.transform, false);
        quad.name = "UIQuad";

        var uiMeshCollider = quad.GetComponent<MeshCollider>();
        uiMeshCollider.sharedMesh = quad.GetComponent<MeshFilter>().sharedMesh;
        uiMeshCollider.convex = false;

        // 5. Material (project shader — built-in Unlit/* is often stripped on UWP/IL2CPP → magenta quad)
        quad.GetComponent<Renderer>().material = WorldUiQuadMaterial.Create(rt);

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

        // 10. Pinch / click feedback (placeholder until wired to real audio / SLR / settings)
        var btnAudio = root.Q<Button>("btn-audio-toggle");
        _asrToggleBtn = btnAudio;
        if (btnAudio != null)
        {
            btnAudio.text = "Automatic Speech Recognition";
            btnAudio.clicked += () =>
            {
                _audioOn = !_audioOn;
                btnAudio.text = _audioOn ? "Automatic Speech Recognition · On" : "Automatic Speech Recognition";
                btnAudio.EnableInClassList("action-rail-btn-on", _audioOn);

                var wizard = WizardOfOzClient.Instance;
                if (_audioOn && _italianAsrOn)
                {
                    _italianAsrOn = false;
                    if (_itaToggleBtn != null)
                    {
                        _itaToggleBtn.text = "Switch to ASR Italian";
                        _itaToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }

                    wizard?.SetItalianLocalAsrEnabled(false);
                }

                if (_audioOn)
                {
                    wizard?.ClearSubtitleCaption();
                    _translationOn = true;
                }

                CurrentInputMode = (_audioOn || _italianAsrOn) ? InputMode.Asr : (_signOn ? InputMode.Sign : InputMode.None);
                IsTranslationEnabled = _audioOn && _translationOn;
                SetAsrCaptureActive(_audioOn || _italianAsrOn);

                var signClient = FindObjectOfType<SignInferenceClient>();
                if (_audioOn && signClient != null)
                {
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
                    if (signClient != null) signClient.SetSignCaptureActive(false);
                }

                if (_translationToggleBtn != null)
                {
                    _translationToggleBtn.style.display = _audioOn ? DisplayStyle.Flex : DisplayStyle.None;
                    if (_audioOn)
                    {
                        _translationToggleBtn.text = _translationOn ? "Translation · On" : "Translation · Off";
                        _translationToggleBtn.EnableInClassList("action-rail-btn-on", _translationOn);
                    }
                    if (!_audioOn)
                    {
                        _translationOn = false;
                        IsTranslationEnabled = false;
                        _translationToggleBtn.text = "Translation · Off";
                        _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }
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
                CurrentInputMode = _signOn ? InputMode.Sign : ((_audioOn || _italianAsrOn) ? InputMode.Asr : InputMode.None);
                if (_signOn) IsTranslationEnabled = false;

                if (_signOn && (_audioOn || _italianAsrOn))
                {
                    _audioOn = false;
                    btnAudio.text = "Automatic Speech Recognition";
                    btnAudio.EnableInClassList("action-rail-btn-on", false);
                    if (_italianAsrOn)
                    {
                        _italianAsrOn = false;
                        if (_itaToggleBtn != null)
                        {
                            _itaToggleBtn.text = "Switch to ASR Italian";
                            _itaToggleBtn.EnableInClassList("action-rail-btn-on", false);
                        }

                        WizardOfOzClient.Instance?.SetItalianLocalAsrEnabled(false);
                    }

                    if (_translationToggleBtn != null)
                    {
                        _translationOn = false;
                        IsTranslationEnabled = false;
                        _translationToggleBtn.text = "Translation · Off";
                        _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                        _translationToggleBtn.style.display = DisplayStyle.None;
                    }

                    SetAsrCaptureActive(false);
                }

                var signClient = FindObjectOfType<SignInferenceClient>();
                if (signClient != null)
                {
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
                _translationToggleBtn.text = _translationOn ? "Translation · On" : "Translation · Off";
                _translationToggleBtn.EnableInClassList("action-rail-btn-on", _translationOn);
            };
        }

        _itaToggleBtn = root.Q<Button>("btn-ita-asr-toggle");
        if (_itaToggleBtn != null)
        {
            _italianAsrOn = false;
            _itaToggleBtn.text = "Switch to ASR Italian";
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
                _itaToggleBtn.text = _italianAsrOn ? "Italian ASR Active . ON" : "Switch to ASR Italian";
                _itaToggleBtn.EnableInClassList("action-rail-btn-on", _italianAsrOn);

                if (_italianAsrOn && _signOn)
                {
                    _signOn = false;
                    if (_signToggleBtn != null)
                    {
                        _signToggleBtn.text = "Sign Language";
                        _signToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }

                    FindObjectOfType<SignInferenceClient>()?.SetSignCaptureActive(false);
                }

                if (_italianAsrOn && _audioOn)
                {
                    _audioOn = false;
                    if (_asrToggleBtn != null)
                    {
                        _asrToggleBtn.text = "Automatic Speech Recognition";
                        _asrToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }

                    if (_translationToggleBtn != null)
                    {
                        _translationOn = false;
                        _translationToggleBtn.style.display = DisplayStyle.None;
                        _translationToggleBtn.text = "Translation · Off";
                        _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                    }
                }

                CurrentInputMode = _italianAsrOn
                    ? InputMode.Asr
                    : (_audioOn ? InputMode.Asr : (_signOn ? InputMode.Sign : InputMode.None));
                IsTranslationEnabled = _audioOn && _translationOn;
                SetAsrCaptureActive(_italianAsrOn || _audioOn);
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
    }

    private void OnDestroy()
    {
        WizardOfOzClient.OnItalianLocalAsrStateChanged -= SyncItalianToggleFromWizard;
    }

    private void SyncItalianToggleFromWizard(bool enabled)
    {
        _italianAsrOn = enabled;
        if (_itaToggleBtn == null)
        {
            return;
        }

        _itaToggleBtn.text = enabled ? "Italian ASR Active . ON" : "Switch to ASR Italian";
        _itaToggleBtn.EnableInClassList("action-rail-btn-on", enabled);
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
        _signOn = false;
        _italianAsrOn = false;
        CurrentInputMode = InputMode.None;
        IsTranslationEnabled = false;

        if (_asrToggleBtn != null)
        {
            _asrToggleBtn.text = "Automatic Speech Recognition";
            _asrToggleBtn.EnableInClassList("action-rail-btn-on", false);
        }

        if (_signToggleBtn != null)
        {
            _signToggleBtn.text = "Sign Language";
            _signToggleBtn.EnableInClassList("action-rail-btn-on", false);
        }

        if (_translationToggleBtn != null)
        {
            _translationToggleBtn.text = "Translation · Off";
            _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
            _translationToggleBtn.style.display = DisplayStyle.None;
        }

        if (_itaToggleBtn != null)
        {
            _itaToggleBtn.text = "Switch to ASR Italian";
            _itaToggleBtn.EnableInClassList("action-rail-btn-on", false);
        }

        WizardOfOzClient.Instance?.ClearSubtitleCaption();

        var signClient = FindObjectOfType<SignInferenceClient>();
        if (signClient != null)
        {
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
}