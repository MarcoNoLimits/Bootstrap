using UnityEngine;
using UnityEngine.UIElements;

public class App : MonoBehaviour
{
    // We keep this variable to reference the UI Root
    private VisualElement _root;

    private void Awake()
    {
        // 1. Initialize the UI in World Space
        InitializeUI();

        // 2. Bind Logic (The C++ way: explicit binding)
        BindUIEvents();
    }

    private void InitializeUI()
    {
        // Create the GameObject that holds the UI
        var uiGO = new GameObject("MainUI");
        var uiDoc = uiGO.AddComponent<UIDocument>();

        // --- STEP 1: Load the Layout (.uxml) ---
        // Make sure "MainLayout" is in Assets/Resources/UI/
        uiDoc.visualTreeAsset = Resources.Load<VisualTreeAsset>("UI/MainLayout");

        // --- STEP 2: Load the Settings (The Fix for text) ---
        // Make sure "DefaultPanelSettings" is in Assets/Resources/UI/
        var settings = Resources.Load<PanelSettings>("UI/DefaultPanelSettings");
        
        if (settings == null)
        {
            Debug.LogError("CRITICAL: Could not find 'DefaultPanelSettings' in Resources/UI/. Text will be invisible!");
        }
        else
        {
            uiDoc.panelSettings = settings;
        }

        // --- STEP 3: Position in World Space ---
        // 1.5 meters in front of the camera
        uiGO.transform.position = new Vector3(0, 0, 1.5f);
        // Scale down heavily because pixels are huge in World Space
        uiGO.transform.localScale = Vector3.one * 0.002f; 

        // Cache the root element so we can query buttons later
        _root = uiDoc.rootVisualElement;
    }

    private void BindUIEvents()
    {
        if (_root == null) return;

        // Query elements by name (must match your UXML names exactly)
        var startBtn = _root.Q<Button>("btn-start");
        var settingsBtn = _root.Q<Button>("btn-settings");

        // Safety check to prevent crashes if UXML names don't match
        if (startBtn != null)
        {
            startBtn.clicked += () => Debug.Log("✅ ASR Started");
            
            // Visual feedback for Hover
            startBtn.RegisterCallback<MouseEnterEvent>(e => startBtn.style.opacity = 0.8f);
            startBtn.RegisterCallback<MouseLeaveEvent>(e => startBtn.style.opacity = 1.0f);
        }

        if (settingsBtn != null)
        {
            settingsBtn.clicked += () => Debug.Log("⚙️ Settings Opened");
        }
    }
}