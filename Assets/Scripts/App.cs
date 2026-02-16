using UnityEngine;
using UnityEngine.UIElements;

public class App : MonoBehaviour
{
    private GameObject _mainUI;
    private Camera _mainCam;
    
    // --- SETTINGS ---
    private float _distance = 0.85f;      // How far away (Meters)
    private float _smoothSpeed = 4.0f;    // How fast it catches up to you
    
    // UI DIMENSIONS (Must match your USS/CSS)
    // We need these to center the window correctly.
    private float _uiWidth = 800f; 
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
        // Higher resolution for crisp text
        int webWidth = 1280;
        int webHeight = 800;
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
        // We create a child object for the UIDocument so it doesn't conflict with the Quad's transform logic if needed
        // but actually, UIDocument with TargetTexture doesn't render to a GameObject, it renders to the Texture.
        // So the UIDocument component acts as the "Server" for the texture.
        GameObject uiLogicObject = new GameObject("UILogic");
        uiLogicObject.transform.SetParent(_mainUI.transform, false);
        var uiDoc = uiLogicObject.AddComponent<UIDocument>();
        uiDoc.visualTreeAsset = Resources.Load<VisualTreeAsset>("UI/MainLayout");
        uiDoc.panelSettings = runtimeSettings;

        // 4. Create the Quad to display the texture
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(_mainUI.transform, false);
        quad.name = "UIQuad";

        // Remove the default collider and add a BoxCollider for thickness if preferred, 
        // or keep MeshCollider. MeshCollider is fine for raycasts.
        // For HoloLens interaction, you usually want a BoxCollider backing.
        // Let's stick to the default MeshCollider for now, but ensure layer is correct if needed.

        // 5. Material Setup
        // Unlit/Transparent is standard. 
        Material mat = new Material(Shader.Find("Unlit/Transparent"));
        mat.mainTexture = rt;
        quad.GetComponent<Renderer>().material = mat;

        // 6. Scale the Quad to match Aspect Ratio and desired physical size
        // We want the width to be about 0.8 meters (match _scale * _uiWidth logic roughly)
        // _uiWidth = 800, _scale = 0.001 => 0.8m
        float physicalWidth = _uiWidth * _scale;
        float physicalHeight = _uiHeight * _scale;
        
        // However, we are now using 1280x800 texture. 
        // Aspect Ratio = 1.6
        // Let's keep physical width at 0.8m.
        // Height = 0.8m / 1.6 = 0.5m.
        
        quad.transform.localScale = new Vector3(physicalWidth, physicalHeight, 1f);

        // 7. Cleanup
        // No need for debug dot anymore as the Quad is visible
    }
}