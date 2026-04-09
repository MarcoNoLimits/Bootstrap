using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit;
using System.Collections.Generic;

public class App : MonoBehaviour
{
    private const string DefaultAsrText = "Live speech appears here. Toggle translation to view Italian text.";
    public enum InputMode { None, Asr, Sign }
    public static InputMode CurrentInputMode { get; private set; } = InputMode.None;
    public static bool IsTranslationEnabled { get; private set; }
    private GameObject _mainUI;
    private Camera _mainCam;
    
    [Header("World placement")]
    [SerializeField] private float _distance = 1.5f;
    [SerializeField] private float _smoothSpeed = 4f;
    [Tooltip("Positive = right side of the view (camera +X).")]
    [SerializeField] private float _rightOffsetMeters = 0.34f;
    [Tooltip("Optional vertical nudge (camera +Y).")]
    [SerializeField] private float _verticalOffsetMeters = -0.05f;
    [Header("Scene Background")]
    [SerializeField] private Color _sceneBackgroundColor = new Color(0.06f, 0.09f, 0.14f, 1f);

    // UI DIMENSIONS (must match USS .glass-panel-minimal)
    private float _uiWidth = 260f;
    private float _uiHeight = 440f;
    private float _scale = 0.001f;

    private bool _audioOn;
    private bool _translationOn;
    private bool _signOn;
    private Coroutine _settingsFlashRoutine;
    private Button _translationToggleBtn;
    private Button _asrToggleBtn;
    private Button _signToggleBtn;

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
        _mainCam = Camera.main;
        CurrentInputMode = InputMode.None;
        IsTranslationEnabled = false;
        
        // Initial jump to front (so you don't have to wait for it to fly in)
        if (_mainCam != null)
        {
            _mainCam.clearFlags = CameraClearFlags.SolidColor;
            _mainCam.backgroundColor = _sceneBackgroundColor;
            UpdatePosition(true); // true = instant snap
        }
    }

    private void Update()
    {
        if (_mainCam == null)
        {
            _mainCam = Camera.main;
            if (_mainCam != null)
            {
                _mainCam.clearFlags = CameraClearFlags.SolidColor;
                _mainCam.backgroundColor = _sceneBackgroundColor;
            }
            return;
        }

        // Run the "Tag-Along" logic every frame
        UpdatePosition(false); // false = smooth movement
    }

    private void UpdatePosition(bool instant)
    {
        if (_mainUI == null) return;

        // 1. Target position: in front of camera, shifted to the right (not centered)
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
        _mainUI.transform.LookAt(_mainCam.transform);
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
        // We load the default, INSTANTIATE it to avoid changing the asset, and assign texture
        var originalSettings = Resources.Load<PanelSettings>("UI/DefaultPanelSettings");
        PanelSettings runtimeSettings = Instantiate(originalSettings);
        runtimeSettings.targetTexture = rt;
        runtimeSettings.scaleMode = PanelScaleMode.ConstantPixelSize; // Maps 1:1 to texture
        runtimeSettings.scale = 1.0f;
        runtimeSettings.clearColor = true;
        runtimeSettings.colorClearValue = Color.clear;

        // 3. Setup UIDocument
        GameObject uiLogicObject = new GameObject("UILogic");
        uiLogicObject.transform.SetParent(_mainUI.transform, false);
        var uiDoc = uiLogicObject.AddComponent<UIDocument>();
        uiDoc.visualTreeAsset = Resources.Load<VisualTreeAsset>("UI/MainLayout");
        uiDoc.panelSettings = runtimeSettings;

        // 4. Quad + MeshCollider (must match rendered UI): BoxCollider hits do not give mesh UVs; manual
        // local→panel mapping was wrong for top/bottom rows after LookAt, so only the middle (SLR) worked.
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(_mainUI.transform, false);
        quad.name = "UIQuad";

        var uiMeshCollider = quad.GetComponent<MeshCollider>();
        uiMeshCollider.sharedMesh = quad.GetComponent<MeshFilter>().sharedMesh;
        uiMeshCollider.convex = false;

        // 5. Material Setup
        // Unlit/Transparent is standard. 
        Material mat = new Material(Shader.Find("Unlit/Transparent"));
        mat.mainTexture = rt;
        quad.GetComponent<Renderer>().material = mat;

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
                CurrentInputMode = _audioOn ? InputMode.Asr : (_signOn ? InputMode.Sign : InputMode.None);
                IsTranslationEnabled = _audioOn && _translationOn;

                if (_audioOn && _signOn)
                {
                    _signOn = false;
                    var btnSign = root.Q<Button>("btn-slr-capture");
                    if (btnSign != null)
                    {
                        btnSign.text = "Sign Language";
                        btnSign.EnableInClassList("action-rail-btn-on", false);
                    }
                    var signClient = FindObjectOfType<SignInferenceClient>();
                    if (signClient != null) signClient.SetSignCaptureActive(false);
                }

                if (_translationToggleBtn != null)
                {
                    _translationToggleBtn.style.display = _audioOn ? DisplayStyle.Flex : DisplayStyle.None;
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
                CurrentInputMode = _signOn ? InputMode.Sign : (_audioOn ? InputMode.Asr : InputMode.None);
                if (_signOn) IsTranslationEnabled = false;

                if (_signOn && _audioOn)
                {
                    _audioOn = false;
                    btnAudio.text = "Automatic Speech Recognition";
                    btnAudio.EnableInClassList("action-rail-btn-on", false);
                    if (_translationToggleBtn != null)
                    {
                        _translationOn = false;
                        IsTranslationEnabled = false;
                        _translationToggleBtn.text = "Translation · Off";
                        _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
                        _translationToggleBtn.style.display = DisplayStyle.None;
                    }
                }

                var signClient = FindObjectOfType<SignInferenceClient>();
                if (signClient != null)
                {
                    signClient.SetSignCaptureActive(_signOn);
                }
            };
        }

        var btnSettingsRail = root.Q<Button>("btn-settings-rail");
        if (btnSettingsRail != null)
        {
            btnSettingsRail.clicked += () =>
            {
                if (_settingsFlashRoutine != null) StopCoroutine(_settingsFlashRoutine);
                _settingsFlashRoutine = StartCoroutine(FlashButtonLabel(btnSettingsRail, "Settings", "Saved", "action-rail-btn-flash", 0.65f));
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

        var debugHudLabel = root.Q<Label>("xr-debug-hud");
        if (debugHudLabel != null)
        {
            debugHudLabel.pickingMode = PickingMode.Ignore;
            var logger = _mainUI.AddComponent<XRDebugLogger>();
            logger.statusLabel = debugHudLabel;
        }

        ActivateSignByDefaultForTesting();
    }

    private IEnumerator FlashButtonLabel(Button btn, string defaultText, string flashText, string flashClass, float seconds)
    {
        btn.EnableInClassList(flashClass, true);
        btn.text = flashText;
        yield return new WaitForSeconds(seconds);
        btn.EnableInClassList(flashClass, false);
        btn.text = defaultText;
    }

    private void ActivateSignByDefaultForTesting()
    {
        _audioOn = false;
        _translationOn = false;
        _signOn = true;
        CurrentInputMode = InputMode.Sign;
        IsTranslationEnabled = false;

        if (_asrToggleBtn != null)
        {
            _asrToggleBtn.text = "Automatic Speech Recognition";
            _asrToggleBtn.EnableInClassList("action-rail-btn-on", false);
        }

        if (_signToggleBtn != null)
        {
            _signToggleBtn.text = "Sign Language · On";
            _signToggleBtn.EnableInClassList("action-rail-btn-on", true);
        }

        if (_translationToggleBtn != null)
        {
            _translationToggleBtn.text = "Translation · Off";
            _translationToggleBtn.EnableInClassList("action-rail-btn-on", false);
            _translationToggleBtn.style.display = DisplayStyle.None;
        }

        var signClient = FindObjectOfType<SignInferenceClient>();
        if (signClient != null)
        {
            signClient.SetSignCaptureActive(true);
        }
    }
}