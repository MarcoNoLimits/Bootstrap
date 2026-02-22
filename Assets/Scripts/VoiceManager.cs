using System;
using UnityEngine;
using UnityEngine.Windows.Speech;

namespace WizardOfOz
{
    public class VoiceManager : IDisposable
    {
        private DictationRecognizer _recognizer;
        
        public Action OnListeningStarted;
        public Action<string> OnSentenceCompleted;
        public Action<string> OnError;

        public VoiceManager()
        {
            _recognizer = new DictationRecognizer();
            
            _recognizer.DictationResult += (text, confidence) =>
            {
                Debug.Log($"[VoiceManager] Result: {text}");
                OnSentenceCompleted?.Invoke(text);
            };

            _recognizer.DictationHypothesis += (text) =>
            {
                // Useful for showing partial recognition
                OnListeningStarted?.Invoke();
            };

            _recognizer.DictationError += (error, hresult) =>
            {
                Debug.LogError($"[VoiceManager] Error: {error} (HResult: {hresult})");
                OnError?.Invoke(error);
            };
        }

        public void Start()
        {
            Debug.Log($"[VoiceManager] Attempting to start recognizer. Current status: {_recognizer.Status}");
            if (_recognizer.Status != SpeechSystemStatus.Running)
            {
                try 
                {
                    _recognizer.Start();
                    Debug.Log("[VoiceManager] Start() called on recognizer.");
                    OnListeningStarted?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[VoiceManager] START FAILED: {e.Message}");
                }
            }
        }

        public void Stop()
        {
            if (_recognizer.Status == SpeechSystemStatus.Running)
            {
                _recognizer.Stop();
            }
        }

        public void Dispose()
        {
            if (_recognizer != null)
            {
                if (_recognizer.Status == SpeechSystemStatus.Running)
                {
                    _recognizer.Stop();
                }
                _recognizer.Dispose();
                _recognizer = null;
            }
        }
    }
}
