using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit;

public class App : MonoBehaviour
{
    private GameObject _mainUI;
    private Camera _mainCam;
    
    // --- SETTINGS ---
    private float _distance = 1.5f;      // How far away (Meters)
    private float _smoothSpeed = 4.0f;    // How fast it catches up to you
    
    // UI DIMENSIONS (Must match your USS/CSS)
    // We need these to center the window correctly.
    private float _uiWidth = 550f; 
    private float _uiHeight = 560f;
    private float _scale = 0.001f;        // The scale we apply to the object

    // Horizontal offset in front of the user so the person
    // they are looking at can remain roughly centered.
    // Negative = to the left of the camera view.
    [SerializeField] private float _horizontalOffset = 0.0f;

    // Simple state flags other systems can read if needed
    public bool IsAsrActive { get; private set; }
    public bool IsTranslationActive { get; private set; }

    private VisualElement _cardAsr;
    private VisualElement _cardSign;
    private VisualElement _cardTranslation;
    private Button _btnAsrStart;
    private Button _btnSignStart;
    private Button _btnTranslationStart;
    private Button _btnModeSwitch;
    private VisualElement _translationToggleSwitch;
    private Label _asrDescription;

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

        // 1. Target Position: In front of camera, slightly offset horizontally
        Vector3 forward = _mainCam.transform.forward;
        Vector3 right = _mainCam.transform.right;
        Vector3 targetPos = _mainCam.transform.position
                            + (forward * _distance)
                            + (right * _horizontalOffset);

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
        int webWidth = 550;
        int webHeight = 560;
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
        var runtimeStyles = Resources.Load<StyleSheet>("UI/Styles");
        if (runtimeStyles != null && !root.styleSheets.Contains(runtimeStyles))
        {
            root.styleSheets.Add(runtimeStyles);
        }

        SetIcon(root, "brand-logo", "UI/HoloAssistLogo");

        // Icons still loaded so existing styles work
        SetBottomBarIcon(root, "icon-volume", "UI/volume");
        SetBottomBarIcon(root, "icon-microphone", "UI/ear-listen");
        SetBottomBarIcon(root, "icon-refresh", "UI/refresh");

        SetIcon(root, "icon-search", "UI/search");
        SetIcon(root, "nav-icon-asr", "UI/ASR");
        SetIcon(root, "nav-icon-translation", "UI/language");
        SetIcon(root, "nav-icon-sign-language", "UI/sign-language");
        SetIcon(root, "nav-icon-notifications", "UI/notification");
        SetIcon(root, "nav-icon-privacy", "UI/privacy");
        SetIcon(root, "nav-icon-help", "UI/help");
        SetIcon(root, "nav-icon-about", "UI/about");

        // Grab cards and buttons for mode switching / ASR control
        _cardAsr = root.Q<VisualElement>("card-asr");
        _cardSign = root.Q<VisualElement>("card-sign");
        _cardTranslation = root.Q<VisualElement>("card-translation");

        _btnAsrStart = root.Q<Button>("btn-asr-start");
        _btnSignStart = root.Q<Button>("btn-sign-start");
        _btnTranslationStart = root.Q<Button>("btn-translation-start");
        _btnModeSwitch = root.Q<Button>("btn-mode-switch");

        _translationToggleSwitch = root.Q<VisualElement>("translation-toggle-switch");
        _asrDescription = root.Q<Label>("asr-description");

        // Optional accent icon for sign language card (bottom-right)
        SetIcon(root, "icon-signlanguage-accent", "UI/blue-signlanguage");
        // Optional accent icon for language translation card (bottom-right)
        SetIcon(root, "icon-translation-accent", "UI/blue-translate");

        WireUiLogic();

        // Subscribe to ASR text updates if the manager exists in the scene.
        if (HololensAsrManager.Instance != null)
        {
            HololensAsrManager.Instance.OnTextUpdated += OnAsrTextUpdated;
        }
    }

    private enum Mode
    {
        Asr,
        Sign,
        Translation
    }

    private Mode _currentMode = Mode.Asr;

    private void WireUiLogic()
    {
        // Mode button – cycle between cards
        if (_btnModeSwitch != null)
        {
            _btnModeSwitch.clicked += () =>
            {
                switch (_currentMode)
                {
                    case Mode.Asr:
                        SetMode(Mode.Sign);
                        break;
                    case Mode.Sign:
                        SetMode(Mode.Translation);
                        break;
                    default:
                        SetMode(Mode.Asr);
                        break;
                }
            };
        }

        // ASR start / stop
        if (_btnAsrStart != null)
        {
            _btnAsrStart.clicked += () =>
            {
                IsAsrActive = !IsAsrActive;
                _btnAsrStart.text = IsAsrActive ? "Stop ASR" : "Start ASR";
                Debug.Log(IsAsrActive ? "[ASR] Started" : "[ASR] Stopped");

                if (HololensAsrManager.Instance != null)
                {
                    if (IsAsrActive)
                    {
                        HololensAsrManager.Instance.StartAsr();
                    }
                    else
                    {
                        HololensAsrManager.Instance.StopAsr();
                    }
                }
            };
        }

        // Sign language start / stop
        if (_btnSignStart != null)
        {
            _btnSignStart.clicked += () =>
            {
                bool active = _btnSignStart.text.StartsWith("Start");
                _btnSignStart.text = active ? "Stop Sign Language" : "Start Sign Language";
                Debug.Log(active ? "[Sign] Started" : "[Sign] Stopped");
            };
        }

        // Translation card start / stop
        if (_btnTranslationStart != null)
        {
            _btnTranslationStart.clicked += () =>
            {
                bool active = _btnTranslationStart.text.StartsWith("Start");
                _btnTranslationStart.text = active ? "Stop Translation" : "Start Translation";
                Debug.Log(active ? "[Translation] Started" : "[Translation] Stopped");
            };
        }

        // Translation toggle inside ASR card (visual on/off)
        if (_translationToggleSwitch != null && _asrDescription != null)
        {
            string en = "Use Automatic Speech Recognition to capture live speech from the headset and render it as readable text, with optional translation to Italian.";
            string it = "Usa il riconoscimento vocale automatico per catturare il parlato dal visore e mostrarlo come testo leggibile, con traduzione opzionale in italiano.";

            _translationToggleSwitch.RegisterCallback<ClickEvent>(_ =>
            {
                IsTranslationActive = !IsTranslationActive;
                _translationToggleSwitch.ToggleInClassList("on");
                // When toggling, keep whatever live text we currently have, just
                // switch between English and Italian variants where possible.
                Debug.Log(IsTranslationActive ? "[ASR] Italian translation ON" : "[ASR] Italian translation OFF");
            });

            // Ensure initial state is off
            IsTranslationActive = false;
            _translationToggleSwitch.RemoveFromClassList("on");
            _asrDescription.text = en;
        }

        // Initial mode
        SetMode(_currentMode);
    }

    private void SetMode(Mode mode)
    {
        _currentMode = mode;

        if (_cardAsr != null)
            _cardAsr.style.display = mode == Mode.Asr ? DisplayStyle.Flex : DisplayStyle.None;
        if (_cardSign != null)
            _cardSign.style.display = mode == Mode.Sign ? DisplayStyle.Flex : DisplayStyle.None;
        if (_cardTranslation != null)
            _cardTranslation.style.display = mode == Mode.Translation ? DisplayStyle.Flex : DisplayStyle.None;

        if (_btnModeSwitch != null)
        {
            switch (mode)
            {
                case Mode.Asr:
                    _btnModeSwitch.text = "Sign Language";
                    break;
                case Mode.Sign:
                    _btnModeSwitch.text = "Language Translation";
                    break;
                case Mode.Translation:
                    _btnModeSwitch.text = "Back to ASR";
                    break;
            }
        }
    }

    private void OnAsrTextUpdated(string text)
    {
        if (_asrDescription == null) return;

        // In a real app you would pass 'text' to a translation service when
        // IsTranslationActive is true and then display the translated result.
        // For now we simply prefix to show the flow is working.
        if (IsTranslationActive)
        {
            _asrDescription.text = "[IT] " + text;
        }
        else
        {
            _asrDescription.text = text;
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