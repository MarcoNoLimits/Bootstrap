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
    private float _uiWidth = 920f; 
    private float _uiHeight = 500f;
    private float _scale = 0.001f;        // The scale we apply to the object

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
        // Match the UI dimensions exactly so it fills the quad
        int webWidth = 920;
        int webHeight = 500;
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

        // 9. Brand logo and bottom-bar icons: add images to Assets/Resources/UI/
        var root = uiDoc.rootVisualElement;
        var brandLogo = root.Q<VisualElement>("brand-logo");
        if (brandLogo != null)
        {
            // Try Texture2D (e.g. PNG) first
            var logoTexture = Resources.Load<Texture2D>("UI/HoloAssistLogo");
            if (logoTexture != null)
            {
                brandLogo.style.backgroundImage = Background.FromTexture2D(logoTexture);
            }
            else
            {
                // Fallback to Sprite (e.g. imported SVG as Sprite)
                var logoSprite = Resources.Load<Sprite>("UI/HoloAssistLogo");
                if (logoSprite != null)
                {
                    brandLogo.style.backgroundImage = Background.FromSprite(logoSprite);
                }
            }
        }

        SetBottomBarIcon(root, "icon-volume", "UI/volume");
        SetBottomBarIcon(root, "icon-microphone", "UI/microphone");
        SetBottomBarIcon(root, "icon-refresh", "UI/refresh");

        SetIcon(root, "icon-search", "UI/search");
        SetIcon(root, "nav-icon-asr", "UI/ASR");
        SetIcon(root, "nav-icon-translation", "UI/language");
        SetIcon(root, "nav-icon-sign-language", "UI/sign-language");
        SetIcon(root, "nav-icon-notifications", "UI/notification");
        SetIcon(root, "nav-icon-privacy", "UI/privacy");
        SetIcon(root, "nav-icon-help", "UI/help");
        SetIcon(root, "nav-icon-about", "UI/about");

        // 10. Test Button Action
        // Find the "Settings" button and add a debug action
        var btnSettings = root.Q<Button>("btn-settings");
        if (btnSettings != null)
        {
            btnSettings.clicked += () => 
            {
                Debug.Log("Settings Button Clicked via Hand Interaction!");
                // Text will be overridden by logger
            };

            // 11. Debug Hand Tracking
            var logger = _mainUI.AddComponent<XRDebugLogger>();
            logger.debugButton = btnSettings;
        }
    }

    private static void SetBottomBarIcon(VisualElement root, string elementName, string resourcePath)
    {
        SetIcon(root, elementName, resourcePath);
    }

    private static void SetIcon(VisualElement root, string elementName, string resourcePath)
    {
        var el = root.Q<VisualElement>(elementName);
        if (el == null)
        {
            Debug.LogWarning($"[UI] Element '{elementName}' not found in UXML.");
            return;
        }

        // --- BULLETPROOF LOADING ---
        // 1) Try to find a VectorImage first (UI Toolkit preference)
        var vectorImages = Resources.LoadAll<VectorImage>(resourcePath);
        if (vectorImages.Length > 0)
        {
            el.style.backgroundImage = new StyleBackground(Background.FromVectorImage(vectorImages[0]));
            return;
        }

        // 2) Try to find a Sprite
        var sprites = Resources.LoadAll<Sprite>(resourcePath);
        if (sprites.Length > 0)
        {
            // Prefer sprites with textures
            foreach (var s in sprites)
            {
                if (s.texture != null)
                {
                    el.style.backgroundImage = Background.FromSprite(s);
                    return;
                }
            }
            // Fallback to textureless sprite (will likely warn but we've tried)
            el.style.backgroundImage = Background.FromSprite(sprites[0]);
            return;
        }

        // 3) Try to find a Texture2D (PNG/Logo)
        var textures = Resources.LoadAll<Texture2D>(resourcePath);
        if (textures.Length > 0)
        {
            el.style.backgroundImage = Background.FromTexture2D(textures[0]);
            return;
        }

        // 4) Diagnostics (if everything failed)
        Object[] all = Resources.LoadAll(resourcePath);
        if (all.Length == 0)
        {
            Debug.LogError($"[UI] Failed to find ANY resource at path: '{resourcePath}' for element '{elementName}'.");
            return;
        }

        Debug.LogError($"[UI] Found {all.Length} assets at '{resourcePath}', but none are suitable (1st is {all[0].GetType().Name}). Check Import Settings.");
    }
}