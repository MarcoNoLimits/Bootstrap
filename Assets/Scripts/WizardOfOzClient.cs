using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.Windows.Speech;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Unified controller for the Wizard of Oz Machine Translation Demo.
/// Handles UI Creation, Voice Recognition, and Network Communication.
/// </summary>
[DefaultExecutionOrder(-50)]
public class WizardOfOzClient : MonoBehaviour
{
    [Header("Settings")]
    public string serverIP = "localhost";
    public int serverPort = 8081;

    // UI Master Components (from legacy App.cs)
    private GameObject _mainUIRoot;
    private Camera _mainCam;
    private UIDocument _uiDoc;
    private RenderTexture _uiRT;

    // Sub-Managers
    private NetworkManager _network;
    private VoiceManager _voice;
    private UIManager _uiManager;

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
        Debug.Log("[WizardOfOz] Unified Client Awake.");
        _mainCam = Camera.main;
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
                _uiManager = new UIManager(_uiDoc);
                _network = new NetworkManager(serverIP, serverPort);
                _voice = new VoiceManager();

                WireEvents();
                _voice.Start();
                
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
        _uiRT = new RenderTexture(1000, 200, 24);
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
        quad.transform.localScale = new Vector3(1.0f, 0.2f, 1f);

        Material mat = new Material(Shader.Find("Unlit/Transparent"));
        mat.mainTexture = _uiRT;
        quad.GetComponent<Renderer>().material = mat;

        // 5. Interaction
        Destroy(quad.GetComponent<Collider>());
        BoxCollider box = quad.AddComponent<BoxCollider>();
        box.size = new Vector3(1, 1, 0.05f);

        // WorldUIInputBridge — translates XR ray hits into UI Toolkit pointer events
        var bridge = _mainUIRoot.AddComponent<WorldUIInputBridge>();
        bridge.uiDoc = _uiDoc;
        bridge.renderTexture = _uiRT;
        bridge.targetCollider = box;

        // XRSimpleInteractable — makes the quad visible to XR ray interactors
        var interactable = _mainUIRoot.AddComponent<XRSimpleInteractable>();
        interactable.colliders.Clear();
        interactable.colliders.Add(box);
        interactable.selectEntered.AddListener(bridge.OnSelectEntered);
        interactable.hoverEntered.AddListener(bridge.OnHoverEntered);
        interactable.hoverExited.AddListener(bridge.OnHoverExited);

        // Position it in front of camera
        if (_mainCam != null)
        {
            Vector3 target = _mainCam.transform.position + (_mainCam.transform.forward * 1.5f);
            target += Vector3.down * 0.4f;
            _mainUIRoot.transform.position = target;
            _mainUIRoot.transform.LookAt(_mainCam.transform);
            _mainUIRoot.transform.Rotate(0, 180, 0);
        }
    }

    private void WireEvents()
    {
        if (_voice == null || _uiManager == null) return;

        _voice.OnListeningStarted += () => MainThreadDispatcher.RunOnMainThread(() => _uiManager.UpdateText("Listening..."));
        
        _voice.OnSentenceCompleted += (text) => {
            MainThreadDispatcher.RunOnMainThread(() => _uiManager.UpdateText($"Recognized: {text}"));
            _network.SendTranslationRequest(text, (resp) => {
                MainThreadDispatcher.RunOnMainThread(() => _uiManager.UpdateText(resp));
            });
        };

        _voice.OnError += (err) => MainThreadDispatcher.RunOnMainThread(() => _uiManager.UpdateText($"Error: {err}"));
    }

    private void Update()
    {
        if (_mainUIRoot != null && _mainCam != null)
        {
            Vector3 target = _mainCam.transform.position + (_mainCam.transform.forward * 1.5f);
            target += Vector3.down * 0.4f;
            _mainUIRoot.transform.position = Vector3.Lerp(_mainUIRoot.transform.position, target, Time.deltaTime * 4.0f);
            _mainUIRoot.transform.LookAt(_mainCam.transform);
            _mainUIRoot.transform.Rotate(0, 180, 0);
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

    private void OnDestroy()
    {
        _voice?.Dispose();
        _uiRT?.Release();
    }
}

// Support Classes (Internal for maximal reliability)

public class UIManager
{
    private Label _label;
    private Button _startBtn;
    private bool _isRunning = false;

    public System.Action OnStartPressed;
    public System.Action OnStopPressed;

    public UIManager(UIDocument doc) {
        _label = doc.rootVisualElement.Q<Label>("subtitle-text");
        if (_label != null) {
            _label.text = "READY: Speak Now";
        }
    }
    public void UpdateText(string t) { if (_label != null) _label.text = t; }
}

public class NetworkManager
{
    private string _ip; private int _p;
    public NetworkManager(string ip, int p) { _ip = ip; _p = p; }
    public async void SendTranslationRequest(string text, Action<string> cb) {
        try {
            using (TcpClient c = new TcpClient()) {
                await c.ConnectAsync(_ip, _p);
                byte[] d = Encoding.UTF8.GetBytes(text);
                await c.GetStream().WriteAsync(d, 0, d.Length);
                byte[] b = new byte[1024];
                int r = await c.GetStream().ReadAsync(b, 0, b.Length);
                cb?.Invoke(Encoding.UTF8.GetString(b, 0, r));
            }
        } catch (Exception e) { Debug.LogError(e.Message); cb?.Invoke("Net Error"); }
    }
}

public class VoiceManager : IDisposable
{
    private DictationRecognizer _r;
    public Action OnListeningStarted;
    public Action<string> OnSentenceCompleted;
    public Action<string> OnError;

    public VoiceManager() {
        _r = new DictationRecognizer();
        _r.DictationResult += (t, c) => OnSentenceCompleted?.Invoke(t);
        _r.DictationHypothesis += (t) => OnListeningStarted?.Invoke();
        _r.DictationError += (e, h) => {
            OnError?.Invoke(e.ToString());
            Restart();
        };
        _r.DictationComplete += (cause) => {
            Debug.Log($"[VoiceManager] Dictation completed because: {cause}. Restarting...");
            Restart();
        };
    }
    public void Start() { 
        if (_r.Status != SpeechSystemStatus.Running) {
            Debug.Log("[VoiceManager] Stopping PhraseRecognitionSystem to prevent conflict...");
            if (PhraseRecognitionSystem.Status == SpeechSystemStatus.Running) {
                PhraseRecognitionSystem.Shutdown();
            }
            _r.Start(); 
        }
    }

    private void Restart() {
        if (_r.Status == SpeechSystemStatus.Running) {
            _r.Stop();
        }
        _r.Start();
    }

    public void Dispose() { _r?.Stop(); _r?.Dispose(); }
}

public class MainThreadDispatcher : MonoBehaviour
{
    private static readonly Queue<Action> _q = new Queue<Action>();
    public static void RunOnMainThread(Action a) { lock (_q) { _q.Enqueue(a); } }
    private void Update() { lock (_q) { while (_q.Count > 0) _q.Dequeue().Invoke(); } }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init() {
        var go = new GameObject("Dispatcher");
        go.AddComponent<MainThreadDispatcher>();
        DontDestroyOnLoad(go);
    }
}
