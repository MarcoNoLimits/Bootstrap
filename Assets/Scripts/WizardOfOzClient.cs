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
    public int serverPort = 18080;

    // UI Master Components (from legacy App.cs)
    private GameObject _mainUIRoot;
    private Camera _mainCam;
    private UIDocument _uiDoc;
    private RenderTexture _uiRT;

    // Sub-Managers
    private NetworkManager _network;
    private VoiceManager _voice;
    private UIManager _uiManager;

    [Header("Voice UI")]
    [Tooltip("How long to wait (after last listening/hypothesis activity) before showing a stall hint. HoloLens often needs 45s+ before a final result.")]
    [SerializeField] private float listeningStallSeconds = 55f;

    [Tooltip("Minimum time between stall messages so they do not spam every stall interval.")]
    [SerializeField] private float stallMessageCooldownSeconds = 120f;

    [Tooltip("If false, never show the stall hint (subtitle can stay on Listening indefinitely).")]
    [SerializeField] private bool showListeningStallHint = true;

    private float _listeningStallDeadline = -1f;
    private float _nextStallMessageAllowedTime;

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

    private static string StallHintText()
    {
#if UNITY_WSA && !UNITY_EDITOR
        return "Still waiting for speech. Check HoloLens microphone permissions and speak clearly. Listening continues.";
#else
        return "Still waiting for speech. Check the microphone and speech privacy settings. Listening continues.";
#endif
    }

    private void WireEvents()
    {
        if (_voice == null || _uiManager == null) return;

        _voice.OnListeningStarted += () => MainThreadDispatcher.RunOnMainThread(() => {
            _listeningStallDeadline = Time.time + listeningStallSeconds;
            _uiManager.UpdateText("Listening...");
        });

        _voice.OnHypothesis += (partial) => MainThreadDispatcher.RunOnMainThread(() => {
            _listeningStallDeadline = Time.time + listeningStallSeconds;
            if (!string.IsNullOrEmpty(partial)) {
                _uiManager.UpdateText($"Listening… {partial}");
            }
        });

        _voice.OnSentenceCompleted += (text) => {
            MainThreadDispatcher.RunOnMainThread(() => {
                _listeningStallDeadline = -1f;
                _nextStallMessageAllowedTime = 0f;
                _uiManager.UpdateText($"Recognized: {text}");
            });
            _network.SendTranslationRequest(text, (resp) => {
                MainThreadDispatcher.RunOnMainThread(() => _uiManager.UpdateText(resp));
            });
        };

        _voice.OnError += (err) => MainThreadDispatcher.RunOnMainThread(() => {
            _listeningStallDeadline = -1f;
            _uiManager.UpdateText($"Error: {err}");
        });
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

        if (showListeningStallHint
            && _listeningStallDeadline > 0f
            && Time.time >= _listeningStallDeadline
            && _uiManager != null)
        {
            if (Time.time < _nextStallMessageAllowedTime)
            {
                _listeningStallDeadline = Time.time + listeningStallSeconds;
            }
            else
            {
                _nextStallMessageAllowedTime = Time.time + stallMessageCooldownSeconds;
                _listeningStallDeadline = -1f;
                _uiManager.UpdateText(StallHintText());
            }
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
    private const float RestartSettleSeconds = 0.22f;

    private DictationRecognizer _r;
    private bool _disposed;

    private bool _restartPipelineRunning;
    private bool _restartPending;
    private DictationCompletionCause _pendingRestartCause = DictationCompletionCause.Complete;

    public Action OnListeningStarted;
    /// <summary>Partial recognition text — use for live subtitle, not the same as session start.</summary>
    public Action<string> OnHypothesis;
    public Action<string> OnSentenceCompleted;
    public Action<string> OnError;

    public VoiceManager() {
        _r = CreateRecognizer();
    }

    private DictationRecognizer CreateRecognizer() {
        var r = new DictationRecognizer();
        r.DictationResult += OnDictationResult;
        r.DictationHypothesis += OnDictationHypothesis;
        r.DictationError += OnDictationError;
        r.DictationComplete += OnDictationComplete;
        return r;
    }

    private void OnDictationResult(string text, ConfidenceLevel confidence) {
        MainThreadDispatcher.RunOnMainThread(() => OnSentenceCompleted?.Invoke(text));
    }

    private void OnDictationHypothesis(string text) {
        MainThreadDispatcher.RunOnMainThread(() => OnHypothesis?.Invoke(text));
    }

    private void OnDictationError(string error, int hresult) {
        MainThreadDispatcher.RunOnMainThread(() =>
            OnError?.Invoke(string.IsNullOrEmpty(error) ? $"HRESULT: {hresult}" : $"{error} (HRESULT: {hresult})"));
        ScheduleRestart(DictationCompletionCause.UnknownError);
    }

    private void OnDictationComplete(DictationCompletionCause cause) {
        Debug.Log($"[VoiceManager] Dictation completed because: {cause}. Scheduling restart...");
        ScheduleRestart(cause);
    }

    private static bool ShouldRecreateRecognizer(DictationCompletionCause cause) {
        switch (cause) {
            case DictationCompletionCause.UnknownError:
            case DictationCompletionCause.AudioQualityFailure:
            case DictationCompletionCause.MicrophoneUnavailable:
            case DictationCompletionCause.NetworkFailure:
                return true;
            default:
                return false;
        }
    }

    public void Start() {
        if (_disposed || _r == null) return;
        if (_r.Status != SpeechSystemStatus.Running) {
            EnsurePhraseSystemForDictation();
            try {
                _r.Start();
                MainThreadDispatcher.RunOnMainThread(() => OnListeningStarted?.Invoke());
            } catch (Exception ex) {
                Debug.LogError($"[VoiceManager] Start failed: {ex}");
                MainThreadDispatcher.RunOnMainThread(() => OnError?.Invoke(ex.Message));
            }
        }
    }

    private static void EnsurePhraseSystemForDictation() {
        if (PhraseRecognitionSystem.Status == SpeechSystemStatus.Running) {
            Debug.Log("[VoiceManager] Stopping PhraseRecognitionSystem to prevent conflict...");
            PhraseRecognitionSystem.Shutdown();
        }
    }

    /// <summary>
    /// Dictation callbacks can run off the main thread; Stop/Start must run on the main thread.
    /// Restarts are serialized so rapid DictationComplete events do not overlap Stop/Start (a common source of flakiness).
    /// A short delay after Stop lets the Windows / HoloLens speech stack settle.
    /// </summary>
    private void ScheduleRestart(DictationCompletionCause cause) {
        if (_disposed) return;
        _pendingRestartCause = cause;
        _restartPending = true;
        if (_restartPipelineRunning) {
            return;
        }
        MainThreadDispatcher.RunCoroutine(RestartPipeline());
    }

    private IEnumerator RestartPipeline() {
        _restartPipelineRunning = true;
        try {
            while (_restartPending && !_disposed) {
                _restartPending = false;
                yield return RestartOneSession(_pendingRestartCause);
            }
        } finally {
            _restartPipelineRunning = false;
        }
    }

    private IEnumerator RestartOneSession(DictationCompletionCause cause) {
        if (_disposed) yield break;

        if (ShouldRecreateRecognizer(cause)) {
            Debug.Log($"[VoiceManager] Recreating DictationRecognizer after {cause}.");
            DisposeRecognizerOnly();
            _r = CreateRecognizer();
        } else {
            try {
                if (_r != null && _r.Status == SpeechSystemStatus.Running) {
                    _r.Stop();
                }
            } catch (Exception ex) {
                Debug.LogWarning($"[VoiceManager] Stop during restart: {ex.Message}");
            }
        }

        yield return null;
        yield return new WaitForSecondsRealtime(RestartSettleSeconds);

        if (_disposed || _r == null) yield break;

        EnsurePhraseSystemForDictation();

        bool needSecondChance = false;
        try {
            _r.Start();
            MainThreadDispatcher.RunOnMainThread(() => OnListeningStarted?.Invoke());
        } catch (Exception ex) {
            Debug.LogError($"[VoiceManager] Restart Start failed: {ex}");
            MainThreadDispatcher.RunOnMainThread(() => OnError?.Invoke(ex.Message));
            DisposeRecognizerOnly();
            _r = CreateRecognizer();
            needSecondChance = true;
        }

        if (needSecondChance) {
            yield return null;
            yield return new WaitForSecondsRealtime(RestartSettleSeconds);
            if (_disposed || _r == null) yield break;
            try {
                EnsurePhraseSystemForDictation();
                _r.Start();
                MainThreadDispatcher.RunOnMainThread(() => OnListeningStarted?.Invoke());
            } catch (Exception ex2) {
                Debug.LogError($"[VoiceManager] Second-chance Start failed: {ex2}");
                MainThreadDispatcher.RunOnMainThread(() => OnError?.Invoke(ex2.Message));
            }
        }
    }

    private void DisposeRecognizerOnly() {
        if (_r == null) return;
        try {
            if (_r.Status == SpeechSystemStatus.Running) {
                _r.Stop();
            }
        } catch (Exception ex) {
            Debug.LogWarning($"[VoiceManager] Stop before dispose: {ex.Message}");
        }
        _r.DictationResult -= OnDictationResult;
        _r.DictationHypothesis -= OnDictationHypothesis;
        _r.DictationError -= OnDictationError;
        _r.DictationComplete -= OnDictationComplete;
        _r.Dispose();
        _r = null;
    }

    public void Dispose() {
        _disposed = true;
        _restartPending = false;
        DisposeRecognizerOnly();
    }
}

public class MainThreadDispatcher : MonoBehaviour
{
    private static readonly Queue<Action> _q = new Queue<Action>();
    private static MainThreadDispatcher _instance;

    public static void RunOnMainThread(Action a) { lock (_q) { _q.Enqueue(a); } }

    /// <summary>Queues a coroutine to run on the Unity main thread (for non-MonoBehaviour callers).</summary>
    public static void RunCoroutine(IEnumerator routine) {
        RunOnMainThread(() => {
            if (_instance != null) {
                _instance.StartCoroutine(routine);
            }
        });
    }

    private void Awake() { _instance = this; }

    private void Update() { lock (_q) { while (_q.Count > 0) _q.Dequeue().Invoke(); } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init() {
        var go = new GameObject("Dispatcher");
        go.AddComponent<MainThreadDispatcher>();
        DontDestroyOnLoad(go);
    }
}
