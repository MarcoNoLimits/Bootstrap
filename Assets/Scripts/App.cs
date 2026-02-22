using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit;

public class App : MonoBehaviour
{
    private GameObject _mainUI;
    private Camera _mainCam;
    
    // --- SETTINGS ---
    private float _distance = 2.0f;      // How far away (Meters)
    private float _smoothSpeed = 4.0f;    // How fast it catches up to you
    
    // UI DIMENSIONS (Must match your USS/CSS)
    // We need these to center the window correctly.
    private float _uiWidth = 800f; 
    private float _uiHeight = 500f;
    private float _scale = 0.001f;        // The scale we apply to the object

    private RenderTexture _uiRenderTexture;

    private void Awake()
    {
        if (GameObject.FindObjectOfType<WizardOfOz.AppBootstrap>() != null)
        {
            Debug.LogWarning("[App] AppBootstrap detected. KILLING legacy UI to clear the view.");
            this.gameObject.SetActive(false);
            return;
        }
    }

    private void Start()
    {
        _mainCam = Camera.main;
        InitializeUI();
        
        // Initial jump to front
        if (_mainCam != null)
        {
            UpdatePosition(true);
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

        // 1. Target Position: In front of camera
        Vector3 targetPos = _mainCam.transform.position + (_mainCam.transform.forward * _distance);

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
        int webWidth = 800;
        int webHeight = 500;
        _uiRenderTexture = new RenderTexture(webWidth, webHeight, 24);
        _uiRenderTexture.name = "UIRenderTexture";

        // 2. Setup Panel Settings
        var originalSettings = Resources.Load<PanelSettings>("UI/DefaultPanelSettings");
        PanelSettings runtimeSettings = Instantiate(originalSettings);
        runtimeSettings.targetTexture = _uiRenderTexture;
        runtimeSettings.scaleMode = PanelScaleMode.ConstantPixelSize; // Maps 1:1 to texture
        runtimeSettings.scale = 1.0f;
        runtimeSettings.clearColor = true;

        // 3. Setup UIDocument
        GameObject uiLogicObject = new GameObject("UILogic");
        uiLogicObject.transform.SetParent(_mainUI.transform, false);
        var uiDoc = uiLogicObject.AddComponent<UIDocument>();
        uiDoc.visualTreeAsset = Resources.Load<VisualTreeAsset>("UI/MainLayout");
        uiDoc.panelSettings = runtimeSettings;

        // 4. Create the Quad to display the texture
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(_mainUI.transform, false);
        quad.name = "UIQuad";

        // Replace MeshCollider with BoxCollider for better XRI detection
        Collider meshCollider = quad.GetComponent<Collider>();
        if (meshCollider != null) Destroy(meshCollider);
        
        BoxCollider boxCol = quad.AddComponent<BoxCollider>();
        boxCol.size = new Vector3(1, 1, 0.05f); // Thin box
        
        // Add rudimentary interaction support? 
        // For now, just getting visuals back is priority #1.
        // We will add the bridge script in the next step.

        // 5. Material Setup
        // Unlit/Transparent is standard. 
        Material mat = new Material(Shader.Find("Unlit/Transparent"));
        mat.mainTexture = _uiRenderTexture;
        quad.GetComponent<Renderer>().material = mat;

        // 6. Scale the Quad to match physical size
        // _uiWidth = 800, _scale = 0.001 => 0.8m width
        // _uiHeight = 500 => 0.5m height
        float physicalWidth = _uiWidth * _scale;
        float physicalHeight = _uiHeight * _scale;
        
        quad.transform.localScale = new Vector3(physicalWidth, physicalHeight, 1f);

        // 7. Interaction Bridge
        var bridge = _mainUI.AddComponent<WorldUIInputBridge>();
        bridge.uiDoc = uiDoc;
        bridge.renderTexture = _uiRenderTexture;
        bridge.targetCollider = boxCol;
        
        // 8. XR Interaction Setup
        // Add XRSimpleInteractable so hand rays can "Select" (Click) the Quad
        var interactable = _mainUI.AddComponent<XRSimpleInteractable>();
        interactable.colliders.Clear();
        interactable.colliders.Add(boxCol);
        
        // Wire up the event
        interactable.selectEntered.AddListener(bridge.OnSelectEntered);
        interactable.hoverEntered.AddListener(bridge.OnHoverEntered);
        interactable.hoverExited.AddListener(bridge.OnHoverExited);

        // 9. Test Button Action
        // Find the "Settings" button and add a debug action
        var root = uiDoc.rootVisualElement;
        var btnSettings = root.Q<Button>("btn-settings");
        if (btnSettings != null)
        {
            btnSettings.clicked += () => 
            {
                Debug.Log("Settings Button Clicked via Hand Interaction!");
                // Text will be overridden by logger
            };

            // 10. Debug Hand Tracking
            var logger = _mainUI.AddComponent<XRDebugLogger>();
            logger.debugButton = btnSettings;
        }
    }

    private void OnDestroy()
    {
        if (_uiRenderTexture != null)
        {
            _uiRenderTexture.Release();
            Destroy(_uiRenderTexture);
        }
    }
}