using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit;
using System.Collections.Generic;

public class App : MonoBehaviour
{
    private const string DefaultAsrText = "Live speech appears here. Toggle translation to view Italian text.";
    private GameObject _mainUI;
    private Camera _mainCam;
    
    [Header("World placement")]
    [SerializeField] private float _distance = 1.5f;
    [SerializeField] private float _smoothSpeed = 4f;
    [Tooltip("Positive = right side of the view (camera +X).")]
    [SerializeField] private float _rightOffsetMeters = 0.22f;
    [Tooltip("Optional vertical nudge (camera +Y).")]
    [SerializeField] private float _verticalOffsetMeters = 0f;

    // UI DIMENSIONS (must match USS .glass-panel-minimal)
    private float _uiWidth = 180f;
    private float _uiHeight = 400f;
    private float _scale = 0.001f;

    private bool _audioOn;
    private Coroutine _slrFlashRoutine;
    private Coroutine _settingsFlashRoutine;

    private void Awake()
    {
        InitializeUI();
    }

    private void Start()
    {
        _mainCam = Camera.main;
        
        // Initial jump to front (so you don't have to wait for it to fly in)
        if (_mainCam != null)
        {
            UpdatePosition(true); // true = instant snap
        }
    }

    private void Update()
    {
        if (_mainCam == null)
        {
            _mainCam = Camera.main;
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
        if (btnAudio != null)
        {
            btnAudio.clicked += () =>
            {
                _audioOn = !_audioOn;
                btnAudio.text = _audioOn ? "Audio · On" : "Audio · Off";
                btnAudio.EnableInClassList("action-rail-btn-on", _audioOn);
            };
        }

        var btnSlr = root.Q<Button>("btn-slr-capture");
        if (btnSlr != null)
        {
            btnSlr.clicked += () =>
            {
                if (_slrFlashRoutine != null) StopCoroutine(_slrFlashRoutine);
                _slrFlashRoutine = StartCoroutine(FlashButtonLabel(btnSlr, "SLR", "Captured!", "action-rail-btn-flash", 0.75f));
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

        var debugHudLabel = root.Q<Label>("xr-debug-hud");
        if (debugHudLabel != null)
        {
            debugHudLabel.pickingMode = PickingMode.Ignore;
            var logger = _mainUI.AddComponent<XRDebugLogger>();
            logger.statusLabel = debugHudLabel;
        }
    }

    private IEnumerator FlashButtonLabel(Button btn, string defaultText, string flashText, string flashClass, float seconds)
    {
        btn.EnableInClassList(flashClass, true);
        btn.text = flashText;
        yield return new WaitForSeconds(seconds);
        btn.EnableInClassList(flashClass, false);
        btn.text = defaultText;
    }
}