using UnityEngine;
using System;

namespace WizardOfOz
{
    public class AppBootstrap : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private string _serverIP = "localhost"; // Placeholder, update in Inspector or here

        private NetworkManager _networkManager;
        private VoiceManager _voiceManager;
        private UIManager _uiManager;

        static AppBootstrap()
        {
            Debug.Log("[AppBootstrap] Static Constructor: Class loaded in Assembly.");
        }

        private void Awake()
        {
            Debug.Log("[AppBootstrap] Awake triggered.");
        }

        private void Start()
        {
            Debug.Log("[AppBootstrap] Start triggered. Initializing managers...");
            InitializeManagers();
            WireEvents();
            
            _voiceManager.Start();
            Debug.Log("[AppBootstrap] Wizard of Oz Client Initialized and VoiceManager started.");
        }

        private void InitializeManagers()
        {
            _uiManager = new UIManager();
            _networkManager = new NetworkManager(_serverIP);
            _voiceManager = new VoiceManager();
            
            _uiManager.UpdateText("READY: Say something in English...");
        }

        private void WireEvents()
        {
            _voiceManager.OnListeningStarted += () => 
            {
                Debug.Log("[AppBootstrap] Event: Listening Started");
                MainThreadDispatcher.RunOnMainThread(() => 
                {
                    _uiManager.UpdateText("Listening...");
                });
            };

            _voiceManager.OnSentenceCompleted += (text) => 
            {
                Debug.Log($"[AppBootstrap] Event: Sentence Completed -> {text}");
                MainThreadDispatcher.RunOnMainThread(() => 
                {
                    _uiManager.UpdateText($"Recognized: {text}");
                });

                _networkManager.SendTranslationRequest(text, (response) => 
                {
                    Debug.Log($"[AppBootstrap] Event: Translation Received -> {response}");
                    MainThreadDispatcher.RunOnMainThread(() => 
                    {
                        _uiManager.UpdateText(response);
                    });
                });
            };

            _voiceManager.OnError += (error) => 
            {
                Debug.LogError($"[AppBootstrap] Event: Voice Error -> {error}");
                MainThreadDispatcher.RunOnMainThread(() => 
                {
                    _uiManager.UpdateText($"Voice Error: {error}");
                });
            };
        }

        private void Update()
        {
            // Update UI position
            if (_uiManager != null)
            {
                _uiManager.UpdatePosition();
            }

            // DIAGNOSTIC: Press SPACE to simulate a recognition event
            if (Input.GetKeyDown(KeyCode.Space))
            {
                Debug.Log("[AppBootstrap] DIAGNOSTIC: Simulating voice recognition result...");
                _voiceManager.OnSentenceCompleted?.Invoke("The robot is learning to translate.");
            }
        }

        private void OnDestroy()
        {
            Debug.Log("[AppBootstrap] Shutting down...");
            if (_voiceManager != null)
            {
                _voiceManager.Dispose();
            }
        }
    }

    /// <summary>
    /// Simple utility to run actions on Unity's main thread (since networking/callbacks might be async)
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static readonly System.Collections.Generic.Queue<Action> _executionQueue = new System.Collections.Generic.Queue<Action>();

        public static void RunOnMainThread(Action action)
        {
            lock (_executionQueue)
            {
                _executionQueue.Enqueue(action);
            }
        }

        private void Update()
        {
            lock (_executionQueue)
            {
                while (_executionQueue.Count > 0)
                {
                    _executionQueue.Dequeue().Invoke();
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            GameObject dispatcher = new GameObject("MainThreadDispatcher");
            dispatcher.AddComponent<MainThreadDispatcher>();
            DontDestroyOnLoad(dispatcher);
        }
    }
}
