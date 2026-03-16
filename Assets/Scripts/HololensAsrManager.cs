using System.Text;
using UnityEngine;
using UnityEngine.Windows.Speech;

/// <summary>
/// Simple wrapper around HoloLens / Windows DictationRecognizer so the rest of
/// the app can start/stop ASR and receive text updates.
/// </summary>
public class HololensAsrManager : MonoBehaviour
{
    public static HololensAsrManager Instance { get; private set; }

    public bool IsRunning
    {
        get { return _dictationRecognizer != null && _dictationRecognizer.Status == SpeechSystemStatus.Running; }
    }

    public delegate void TextUpdatedHandler(string text);
    public event TextUpdatedHandler OnTextUpdated;

    private DictationRecognizer _dictationRecognizer;
    private readonly StringBuilder _buffer = new StringBuilder();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        TryCreateRecognizer();
    }

    private void TryCreateRecognizer()
    {
        if (_dictationRecognizer != null) return;

        try
        {
            _dictationRecognizer = new DictationRecognizer(ConfidenceLevel.Medium);

            _dictationRecognizer.DictationHypothesis += (text) =>
            {
                // Live hypothesis (partial) – we keep last hypothesis visible.
                _buffer.Length = 0;
                _buffer.Append(text);
                OnTextUpdated?.Invoke(_buffer.ToString());
            };

            _dictationRecognizer.DictationResult += (text, confidence) =>
            {
                _buffer.Length = 0;
                _buffer.Append(text);
                OnTextUpdated?.Invoke(_buffer.ToString());
            };

            _dictationRecognizer.DictationComplete += (cause) =>
            {
                Debug.Log("[ASR] Dictation completed: " + cause);
            };

            _dictationRecognizer.DictationError += (error, hresult) =>
            {
                Debug.LogError("[ASR] Dictation error: " + error + " (0x" + hresult.ToString("X") + ")");
            };
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[ASR] Failed to create DictationRecognizer: " + ex.Message);
        }
    }

    public void StartAsr()
    {
        TryCreateRecognizer();
        if (_dictationRecognizer == null) return;
        if (_dictationRecognizer.Status == SpeechSystemStatus.Running) return;

        _buffer.Length = 0;
        _dictationRecognizer.Start();
        Debug.Log("[ASR] DictationRecognizer started.");
    }

    public void StopAsr()
    {
        if (_dictationRecognizer == null) return;
        if (_dictationRecognizer.Status != SpeechSystemStatus.Running) return;

        _dictationRecognizer.Stop();
        Debug.Log("[ASR] DictationRecognizer stopped.");
    }

    private void OnDestroy()
    {
        if (_dictationRecognizer != null)
        {
            if (_dictationRecognizer.Status == SpeechSystemStatus.Running)
            {
                _dictationRecognizer.Stop();
            }
            _dictationRecognizer.Dispose();
            _dictationRecognizer = null;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }
}

