#if UNITY_WSA && !UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEngine;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

/// <summary>
/// Italian speech using Windows.Media.SpeechRecognition with explicit it-IT, independent of OS UI language order.
/// English ASR stays on API + default dictation; this path is only used when Italian mode is on.
/// </summary>
public sealed class ItalianWinRtSpeechRecognizer : IDisposable
{
    private SpeechRecognizer _recognizer;
    private bool _disposed;

    public Action OnListeningStarted;
    public Action<string> OnHypothesis;
    public Action<string> OnSentenceCompleted;
    public Action<string> OnError;

    public static bool IsItalianSpeechEngineAvailable()
    {
        try
        {
            // Unity's UWP metadata often omits SpeechRecognizer.SystemSpeechLanguages; probe it-IT instead.
            var lang = new Language("it-IT");
            var probe = new SpeechRecognizer(lang);
            probe.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ItalianWinRt] IsItalianSpeechEngineAvailable: " + ex.Message);
            return false;
        }
    }

    public async Task StartAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!IsItalianSpeechEngineAvailable())
            {
                MainThreadDispatcher.RunOnMainThread(() =>
                    OnError?.Invoke("Italian unavailable"));
                return;
            }

            var lang = new Language("it-IT");
            _recognizer = new SpeechRecognizer(lang);
            _recognizer.Constraints.Add(
                new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            await WinRtAsync.AsTask(_recognizer.CompileConstraintsAsync());

            _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;

            await WinRtAsync.AsTask(_recognizer.ContinuousRecognitionSession.StartAsync());
            MainThreadDispatcher.RunOnMainThread(() => OnListeningStarted?.Invoke());
            Debug.Log("[ItalianWinRt] Continuous recognition started (it-IT).");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ItalianWinRt] StartAsync failed: " + ex);
            MainThreadDispatcher.RunOnMainThread(() =>
                OnError?.Invoke("Italian unavailable"));
        }
    }

    private void OnResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (_disposed || args?.Result == null)
        {
            return;
        }

        string text = args.Result.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        MainThreadDispatcher.RunOnMainThread(() =>
        {
            OnHypothesis?.Invoke(text);
            OnSentenceCompleted?.Invoke(text);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_recognizer != null)
            {
                try
                {
                    _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ItalianWinRt] Unsubscribe: " + ex.Message);
                }

                try
                {
                    IAsyncAction stop = _recognizer.ContinuousRecognitionSession.StopAsync();
                    if (stop != null)
                    {
                        WinRtAsync.AsTask(stop).Wait(TimeSpan.FromSeconds(3));
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ItalianWinRt] StopAsync: " + ex.Message);
                }

                _recognizer.Dispose();
                _recognizer = null;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ItalianWinRt] Dispose: " + ex.Message);
        }
    }
}

internal static class WinRtAsync
{
    public static Task AsTask(IAsyncAction op)
    {
        if (op == null)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<object>();
        op.Completed = (info, status) =>
        {
            if (status == AsyncStatus.Completed)
            {
                tcs.SetResult(null);
            }
            else if (status == AsyncStatus.Error)
            {
                tcs.SetException(new System.Exception(info.ErrorCode.ToString()));
            }
            else
            {
                tcs.SetCanceled();
            }
        };
        return tcs.Task;
    }

    public static Task<T> AsTask<T>(IAsyncOperation<T> op)
    {
        if (op == null)
        {
            return Task.FromResult(default(T));
        }

        var tcs = new TaskCompletionSource<T>();
        op.Completed = (info, status) =>
        {
            if (status == AsyncStatus.Completed)
            {
                tcs.SetResult(info.GetResults());
            }
            else if (status == AsyncStatus.Error)
            {
                tcs.SetException(new System.Exception(info.ErrorCode.ToString()));
            }
            else
            {
                tcs.SetCanceled();
            }
        };
        return tcs.Task;
    }
}
#endif
