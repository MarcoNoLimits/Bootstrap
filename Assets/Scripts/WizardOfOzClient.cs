using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.Windows.Speech;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Unified controller for the Wizard of Oz Machine Translation Demo.
/// Handles UI Creation, Voice Recognition, and Network Communication.
/// </summary>
[DefaultExecutionOrder(-50)]
public class WizardOfOzClient : MonoBehaviour
{
    public static WizardOfOzClient Instance { get; private set; }
    [Header("Settings")]
    public string serverIP = "localhost";
    public int serverPort = 18080;

    [Header("Translation API (Hugging Face NMT)")]
    [Tooltip("Base URL only; /translate is appended automatically.")]
    [SerializeField] private string translationBaseUrl = "https://marconolimits-nmt.hf.space";
    [Tooltip("Optional X-API-Key for the hosted NMT space. Leave empty when key enforcement is off.")]
    [SerializeField] private string translationApiKey = "";

    [Header("ASR")]
    [Tooltip("Primary transcribe URL (POST float32 LE mono, application/octet-stream, X-Sample-Rate; JSON { \"text\" }). Default: HF API. Clear to use only HoloLens / Windows dictation.")]
    [SerializeField] private string asrApiUrl = "https://thedeezat-asr-hearing-impaired-api.hf.space/audio";

    [Tooltip("After this many consecutive failed HTTP requests, switch to built-in dictation.")]
    [SerializeField] private int asrFallbackAfterConsecutiveFailures = 5;

    [Tooltip("API-only: seconds of silence after the last transcript before sending a finalized phrase (translation). Dictation fallback ignores this.")]
    [SerializeField] private float asrPhraseEndSilenceSeconds = 0.65f;
    [Tooltip("If true, API mode can auto-fallback when no transcript is received for too long while speaking. Keep false to avoid silence-triggered mode switches.")]
    [SerializeField] private bool allowApiSilenceAutoFallback = false;

    // UI Master Components (from legacy App.cs)
    private GameObject _mainUIRoot;
    private Camera _mainCam;
    private UIDocument _uiDoc;
    private RenderTexture _uiRT;

    // Sub-Managers
    private NetworkManager _network;
    private HybridVoiceManager _voice;
    private UIManager _uiManager;

    [Header("Voice UI")]
    [Tooltip("How long to wait (after last listening/hypothesis activity or ASR HTTP completion) before showing a stall hint. Remote ASR first request can be slow; see also skip while request in flight.")]
    [SerializeField] private float listeningStallSeconds = 90f;

    [Tooltip("Minimum time between stall messages so they do not spam every stall interval.")]
    [SerializeField] private float stallMessageCooldownSeconds = 120f;

    [Tooltip("If false, never show the stall hint. When true, deadline refreshes on each ASR HTTP round-trip so empty transcripts do not trigger a false stall.")]
    [SerializeField] private bool showListeningStallHint = true;

    private float _listeningStallDeadline = -1f;
    private float _nextStallMessageAllowedTime;
    private string _lastModeBanner = "";
    private bool _italianLocalAsrEnabled;
    private bool _asrActive;

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
        Instance = this;
        Debug.Log("[WizardOfOz] Unified Client Awake.");
        _mainCam = ResolveMainCamera();
    }

    private static Camera ResolveMainCamera()
    {
        if (Camera.main != null)
        {
            Camera.main.stereoTargetEye = StereoTargetEyeMask.Both;
            return Camera.main;
        }

        Camera[] cameras = FindObjectsOfType<Camera>();
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera c = cameras[i];
            if (c != null && c.enabled && c.gameObject.activeInHierarchy)
            {
                c.stereoTargetEye = StereoTargetEyeMask.Both;
                return c;
            }
        }

        return null;
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
                _network = new NetworkManager(translationBaseUrl, translationApiKey);
                SubscribeAsrStallReset();
                _asrActive = App.CurrentInputMode == App.InputMode.Asr;
                if (_asrActive)
                {
                    CreateAndStartVoiceManager();
                }
                
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

        quad.GetComponent<Renderer>().material = WorldUiQuadMaterial.Create(_uiRT);

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
            Vector3 target = _mainCam.transform.position + (_mainCam.transform.forward * 1.2f);
            target += Vector3.down * 0.30f;
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
            if (App.CurrentInputMode != App.InputMode.Asr) return;
            _listeningStallDeadline = Time.time + listeningStallSeconds;
            _uiManager.UpdateText("Listening...");
        });

        _voice.OnHypothesis += (partial) => MainThreadDispatcher.RunOnMainThread(() => {
            if (App.CurrentInputMode != App.InputMode.Asr) return;
            _listeningStallDeadline = Time.time + listeningStallSeconds;
            if (!string.IsNullOrEmpty(partial)) {
                _uiManager.UpdateText($"Listening… {partial}");
            }
        });

        _voice.OnSpeechBargeIn += () => MainThreadDispatcher.RunOnMainThread(() => {
            if (App.CurrentInputMode != App.InputMode.Asr) return;
            _listeningStallDeadline = Time.time + listeningStallSeconds;
            _uiManager.UpdateText("Listening…");
        });

        _voice.OnSentenceCompleted += (text) => {
            MainThreadDispatcher.RunOnMainThread(() => {
                if (App.CurrentInputMode != App.InputMode.Asr) return;
                _listeningStallDeadline = -1f;
                _nextStallMessageAllowedTime = 0f;
                _uiManager.UpdateText($"Recognized: {text}");
            });
            if (App.CurrentInputMode == App.InputMode.Asr && App.IsTranslationEnabled && !_italianLocalAsrEnabled)
            {
                _network.SendTranslationRequest(text, (resp) => {
                    MainThreadDispatcher.RunOnMainThread(() => _uiManager.UpdateText(resp));
                });
            }
        };

        _voice.OnError += (err) => MainThreadDispatcher.RunOnMainThread(() => {
            if (App.CurrentInputMode != App.InputMode.Asr) return;
            _listeningStallDeadline = -1f;
            if (string.IsNullOrEmpty(err)) return;
            if (err.StartsWith(HybridVoiceManager.AsrFallbackUserMessage, StringComparison.Ordinal))
                _uiManager.UpdateText(err);
            else
                _uiManager.UpdateText($"Error: {err}");
        });

        _uiManager.OnItalianTogglePressed -= ToggleItalianLocalAsr;
        _uiManager.OnItalianTogglePressed += ToggleItalianLocalAsr;
        _uiManager.SetItalianToggleState(_italianLocalAsrEnabled);
    }

    private void CreateAndStartVoiceManager()
    {
        try
        {
            _voice?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[WizardOfOz] Dispose old voice failed: " + ex.Message);
        }

        _voice = new HybridVoiceManager(
            this,
            asrApiUrl,
            asrFallbackAfterConsecutiveFailures,
            asrPhraseEndSilenceSeconds,
            _italianLocalAsrEnabled,
            allowApiSilenceAutoFallback);
        WireEvents();
        _voice.Start();
    }

    private void ToggleItalianLocalAsr()
    {
        SetItalianLocalAsrEnabled(!_italianLocalAsrEnabled);
    }

    public void SetItalianLocalAsrEnabled(bool enabled)
    {
        if (_italianLocalAsrEnabled == enabled)
        {
            return;
        }

        _italianLocalAsrEnabled = enabled;
        _uiManager?.SetItalianToggleState(_italianLocalAsrEnabled);
        _listeningStallDeadline = -1f;
        _nextStallMessageAllowedTime = 0f;
        if (_asrActive)
        {
            CreateAndStartVoiceManager();
        }

        if (_uiManager != null)
        {
            _uiManager.UpdateText(
                _italianLocalAsrEnabled
                    ? "Italian local ASR ON. Speak Italian and captions will show recognized text."
                    : "Italian local ASR OFF. Switched back to English/API ASR.");
        }
    }

    public void SetAsrActive(bool active)
    {
        if (_asrActive == active) return;
        _asrActive = active;

        if (_asrActive)
        {
            CreateAndStartVoiceManager();
            return;
        }

        _listeningStallDeadline = -1f;
        _nextStallMessageAllowedTime = 0f;
        try
        {
            _voice?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[WizardOfOz] Dispose voice when ASR off: " + ex.Message);
        }

        _voice = null;
    }

    private void Update()
    {
        if (_mainCam == null)
            _mainCam = ResolveMainCamera();

        if (_mainUIRoot != null && _mainCam != null)
        {
            Vector3 target = _mainCam.transform.position + (_mainCam.transform.forward * 1.2f);
            target += Vector3.down * 0.30f;
            _mainUIRoot.transform.position = Vector3.Lerp(_mainUIRoot.transform.position, target, Time.deltaTime * 4.0f);
            _mainUIRoot.transform.LookAt(_mainCam.transform);
            _mainUIRoot.transform.Rotate(0, 180, 0);
        }

        if (_uiManager != null)
        {
            if (App.CurrentInputMode == App.InputMode.Sign)
            {
                if (_lastModeBanner != "sign")
                {
                    _lastModeBanner = "sign";
                    // Subtitle is driven by SignInferenceClient (API caption); do not clear or overwrite here.
                }
            }
            else if (App.CurrentInputMode == App.InputMode.None)
            {
                if (_lastModeBanner != "none")
                {
                    _lastModeBanner = "none";
                    _uiManager.UpdateText("");
                }
            }
            else
            {
                _lastModeBanner = "";
            }
        }

        // Do not show stall while an ASR HTTP request is still running (cold remote can take >60s; deadline only refreshes on response).
        if (App.CurrentInputMode == App.InputMode.Asr
            && showListeningStallHint
            && _listeningStallDeadline > 0f
            && Time.time >= _listeningStallDeadline
            && _uiManager != null
            && !(HololensAsrManager.Instance != null && HololensAsrManager.Instance.IsApiRequestInFlight))
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

    /// <summary>Empty JSON text responses do not fire OnHypothesis; we still extend the stall deadline on each HTTP round-trip.</summary>
    private void SubscribeAsrStallReset()
    {
        if (HololensAsrManager.Instance == null) return;
        HololensAsrManager.Instance.OnApiRequestFinished -= OnAsrHttpRoundTrip;
        HololensAsrManager.Instance.OnApiRequestFinished += OnAsrHttpRoundTrip;
    }

    private void OnAsrHttpRoundTrip(bool success)
    {
        MainThreadDispatcher.RunOnMainThread(() =>
        {
            if (_listeningStallDeadline > 0f)
                _listeningStallDeadline = Time.time + listeningStallSeconds;
        });
    }

    private void OnApplicationQuit()
    {
        try
        {
            _voice?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[WizardOfOz] Dispose voice on quit: " + ex.Message);
        }
    }

    private void OnDestroy()
    {
        if (HololensAsrManager.Instance != null)
            HololensAsrManager.Instance.OnApiRequestFinished -= OnAsrHttpRoundTrip;
        if (_uiManager != null)
            _uiManager.OnItalianTogglePressed -= ToggleItalianLocalAsr;
        try
        {
            _voice?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[WizardOfOz] Dispose voice on destroy: " + ex.Message);
        }

        _uiRT?.Release();
        if (Instance == this) Instance = null;
    }
}

// Support Classes (Internal for maximal reliability)

public class UIManager
{
    private Label _label;
    private Button _startBtn;
    private Button _italianToggleBtn;

    public System.Action OnStartPressed;
    public System.Action OnStopPressed;
    public System.Action OnItalianTogglePressed;

    public UIManager(UIDocument doc) {
        _label = doc.rootVisualElement.Q<Label>("subtitle-text");
        if (_label != null) {
            _label.text = "";
            _label.style.display = DisplayStyle.None;
        }
        _italianToggleBtn = doc.rootVisualElement.Q<Button>("btn-ita-asr-toggle");
        if (_italianToggleBtn != null)
        {
            _italianToggleBtn.clicked += () => OnItalianTogglePressed?.Invoke();
        }
    }
    public void UpdateText(string t)
    {
        if (_label == null) return;
        bool hasText = !string.IsNullOrWhiteSpace(t);
        _label.style.display = hasText ? DisplayStyle.Flex : DisplayStyle.None;
        _label.text = hasText ? t : "";
    }

    public void SetItalianToggleState(bool enabled)
    {
        if (_italianToggleBtn == null) return;
        _italianToggleBtn.text = enabled ? "Switch to ITA · On" : "Switch to ITA · Off";
        _italianToggleBtn.style.backgroundColor = enabled
            ? new StyleColor(new Color(0.40f, 0.20f, 0.72f, 0.95f))
            : new StyleColor(new Color(0.26f, 0.13f, 0.44f, 0.92f));
    }
}

public class NetworkManager
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private DateTime _nextConnectionLogAllowedAt = DateTime.MinValue;
    private int _translationRequestSeq;

    public NetworkManager(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl != null ? baseUrl.Trim().TrimEnd('/') : "";
        _apiKey = apiKey != null ? apiKey.Trim() : "";
    }

    public void SendTranslationRequest(string text, Action<string> cb) {
        _ = SendTranslationRequestAsync(text, cb);
    }

    private async System.Threading.Tasks.Task SendTranslationRequestAsync(string text, Action<string> cb) {
        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(text)) {
            cb?.Invoke(text);
            return;
        }

        string requestId = "nmt-" + System.Threading.Interlocked.Increment(ref _translationRequestSeq).ToString("D4");
        try {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(70);
                string url = _baseUrl + "/translate";
                string translated = await TranslateOnce(client, url, text, requestId);
                if (string.IsNullOrWhiteSpace(translated) || string.Equals(translated, text, StringComparison.Ordinal))
                {
                    // HF Spaces may cold-start; retry once.
                    translated = await TranslateOnce(client, url, text, requestId);
                }

                Debug.Log("[NetworkManager] " + requestId + " complete.");
                cb?.Invoke(string.IsNullOrWhiteSpace(translated) ? text : translated);
            }
        } catch (Exception e) {
            if (DateTime.UtcNow >= _nextConnectionLogAllowedAt) {
                Debug.LogWarning("[NetworkManager] " + requestId + " translation server unreachable at " + _baseUrl + "/translate. Keeping ASR text. " + e.Message);
                _nextConnectionLogAllowedAt = DateTime.UtcNow.AddSeconds(8);
            }
            cb?.Invoke(text);
        }
    }

    private static string EscapeJsonString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string ExtractTranslation(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        Match m = Regex.Match(json, "\"translation\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        return Regex.Unescape(m.Groups["v"].Value).Trim();
    }

    private async System.Threading.Tasks.Task<string> TranslateOnce(HttpClient client, string url, string text, string requestId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent("{\"text\":\"" + EscapeJsonString(text) + "\"}", Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
        }

        var resp = await client.SendAsync(request);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if (DateTime.UtcNow >= _nextConnectionLogAllowedAt)
            {
                Debug.LogWarning("[NetworkManager] " + requestId + " NMT translate failed HTTP " + (int)resp.StatusCode + " at " + url + ". " + body);
                _nextConnectionLogAllowedAt = DateTime.UtcNow.AddSeconds(8);
            }
            return text;
        }

        string translated = ExtractTranslation(body);
        return string.IsNullOrWhiteSpace(translated) ? text : translated;
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
            try {
                PhraseRecognitionSystem.Shutdown();
            } catch (Exception ex) {
                Debug.LogWarning($"[VoiceManager] PhraseRecognitionSystem.Shutdown: {ex.Message}");
            }
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
        try {
            _r.DictationResult -= OnDictationResult;
            _r.DictationHypothesis -= OnDictationHypothesis;
            _r.DictationError -= OnDictationError;
            _r.DictationComplete -= OnDictationComplete;
            _r.Dispose();
        } catch (Exception ex) {
            Debug.LogWarning($"[VoiceManager] DictationRecognizer dispose: {ex.Message}");
        }
        _r = null;
    }

    public void Dispose() {
        if (_disposed) return;
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

    private void Update()
    {
        lock (_q)
        {
            while (_q.Count > 0)
            {
                Action action = _q.Dequeue();
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[MainThreadDispatcher] callback failed: " + ex);
                }
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init() {
        var go = new GameObject("Dispatcher");
        go.AddComponent<MainThreadDispatcher>();
        DontDestroyOnLoad(go);
    }
}
