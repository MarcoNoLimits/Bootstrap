using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Captures the main camera (what the HoloLens displays) and serves it to a local webpage
/// so you can view the headset feed in a browser. Add this to a GameObject in your scene
/// (e.g. on the same object as App) and open http://localhost:PORT when Unity is running.
/// </summary>
public class HololensViewStreamer : MonoBehaviour
{
    [Tooltip("Port for the viewer webpage (ensure it's not in use).")]
    [SerializeField] private int _port = 8080;

    [Tooltip("Stream width; height is derived from main camera aspect.")]
    [SerializeField] private int _streamWidth = 960;

    [Tooltip("JPEG quality 1-100; lower = smaller frames, faster.")]
    [SerializeField, Range(50, 95)] private int _jpegQuality = 75;

    [Tooltip("How often to capture a new frame (seconds).")]
    [SerializeField] private float _captureInterval = 1f / 30f;

    private RenderTexture _captureRT;
    private Texture2D _readbackTex;
    private Camera _mainCam;
    private Camera _captureCam;
    private HttpListener _listener;
    private Thread _serverThread;
    private bool _running;
    private byte[] _latestFrame;
    private readonly object _frameLock = new object();
    private float _nextCaptureTime;

    private const string Html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>HoloLens Live View</title>
  <link rel=""preconnect"" href=""https://fonts.googleapis.com"">
  <link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
  <link href=""https://fonts.googleapis.com/css2?family=Montserrat:wght@400;500;600;700&display=swap"" rel=""stylesheet"">
  <style>
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      font-family: 'Montserrat', -apple-system, BlinkMacSystemFont, sans-serif;
      background: #0d1117;
      color: rgba(255,255,255,0.98);
      min-height: 100vh;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      padding: 24px;
      overflow-x: hidden;
    }
    .container {
      width: 100%;
      max-width: 1100px;
      background: rgba(14, 42, 78, 0.58);
      border: 1px solid rgba(255, 255, 255, 0.25);
      border-radius: 24px;
      padding: 28px;
      box-shadow: 0 16px 48px rgba(0,0,0,0.2);
    }
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 20px;
      flex-wrap: wrap;
      gap: 12px;
    }
    h1 {
      font-size: 1.5rem;
      font-weight: 700;
      letter-spacing: 0.02em;
      color: rgba(255,255,255,0.98);
    }
    .badge {
      background: rgba(50, 120, 200, 0.6);
      color: #fff;
      padding: 6px 14px;
      border-radius: 10px;
      font-size: 0.8rem;
      font-weight: 600;
    }
    .viewer {
      position: relative;
      width: 100%;
      aspect-ratio: 16/9;
      background: rgba(0,0,0,0.3);
      border-radius: 14px;
      overflow: hidden;
      border: 1px solid rgba(255, 255, 255, 0.2);
    }
    .viewer img {
      width: 100%;
      height: 100%;
      object-fit: contain;
      display: block;
    }
    .viewer .placeholder {
      position: absolute;
      inset: 0;
      display: flex;
      align-items: center;
      justify-content: center;
      flex-direction: column;
      gap: 12px;
      color: rgba(235, 242, 255, 0.7);
      font-size: 0.95rem;
      font-weight: 500;
    }
    .viewer .placeholder.hidden { display: none; }
    .status {
      margin-top: 16px;
      font-size: 0.85rem;
      font-weight: 500;
      color: rgba(220, 235, 255, 0.88);
    }
    .status.live { color: rgba(100, 220, 150, 0.95); }
    .status.error { color: rgba(248, 81, 73, 0.9); }
  </style>
</head>
<body>
  <div class=""container"">
    <header>
      <h1>HoloLens Live View</h1>
      <span class=""badge"">ASR</span>
    </header>
    <div class=""viewer"">
      <img id=""feed"" alt=""Live feed"" crossorigin=""anonymous"">
      <div class=""placeholder"" id=""placeholder"">Waiting for stream…</div>
    </div>
    <p class=""status"" id=""status"">Connecting…</p>
  </div>
  <script>
    (function() {
      var img = document.getElementById('feed');
      var placeholder = document.getElementById('placeholder');
      var status = document.getElementById('status');
      var interval = 1000 / 30;
      var failed = 0;
      function tick() {
        img.src = '/frame?t=' + Date.now();
      }
      img.onload = function() {
        placeholder.classList.add('hidden');
        status.textContent = 'Live';
        status.classList.add('live');
        status.classList.remove('error');
        failed = 0;
      };
      img.onerror = function() {
        failed++;
        if (failed === 1) status.classList.add('error');
        status.textContent = failed > 10 ? 'No stream. Is Unity running?' : 'Connecting…';
      };
      setInterval(tick, interval);
      tick();
    })();
  </script>
</body>
</html>";

    private void Awake()
    {
        _mainCam = Camera.main;
        if (_mainCam == null)
        {
            Debug.LogWarning("[HololensViewStreamer] No main camera found. Streamer will retry in Update.");
            return;
        }
        InitCapture();
    }

    private void InitCapture()
    {
        if (_mainCam == null) return;
        int w = _streamWidth;
        int h = Mathf.RoundToInt(w / _mainCam.aspect);
        if (h < 1) h = 1;
        _captureRT = new RenderTexture(w, h, 24);
        _captureRT.name = "HololensViewStream";
        _readbackTex = new Texture2D(w, h, TextureFormat.RGB24, false);

        GameObject captureGo = new GameObject("HololensViewStreamerCapture");
        captureGo.hideFlags = HideFlags.HideAndDontSave;
        _captureCam = captureGo.AddComponent<Camera>();
        _captureCam.CopyFrom(_mainCam);
        _captureCam.targetTexture = _captureRT;
        _captureCam.enabled = false;
    }

    private void Start()
    {
        StartServer();
    }

    private void LateUpdate()
    {
        if (_mainCam == null)
        {
            _mainCam = Camera.main;
            if (_mainCam != null) InitCapture();
            return;
        }
        if (_captureCam == null || _captureRT == null || _readbackTex == null || !_running) return;
        if (Time.time < _nextCaptureTime) return;
        _nextCaptureTime = Time.time + _captureInterval;

        _captureCam.CopyFrom(_mainCam);
        _captureCam.targetTexture = _captureRT;
        _captureCam.Render();

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = _captureRT;
        _readbackTex.ReadPixels(new Rect(0, 0, _captureRT.width, _captureRT.height), 0, 0);
        _readbackTex.Apply();
        RenderTexture.active = prev;

        byte[] jpeg = _readbackTex.EncodeToJPG(Mathf.Clamp(_jpegQuality, 1, 100));
        lock (_frameLock)
        {
            _latestFrame = jpeg;
        }
    }

    private void StartServer()
    {
        if (_listener != null) return;
        string prefix = "http://localhost:" + _port + "/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        try
        {
            _listener.Start();
            _running = true;
            _serverThread = new Thread(ServerLoop);
            _serverThread.IsBackground = true;
            _serverThread.Start();
            Debug.Log("[HololensViewStreamer] Viewer at: " + prefix);
        }
        catch (Exception e)
        {
            Debug.LogError("[HololensViewStreamer] Could not start server on port " + _port + ": " + e.Message);
        }
    }

    private void ServerLoop()
    {
        while (_running && _listener != null)
        {
            try
            {
                var context = _listener.GetContext();
                var request = context.Request;
                var response = context.Response;
                string path = request.Url.AbsolutePath.TrimStart('/').Split('?')[0];

                if (path == "frame")
                {
                    byte[] frame;
                    lock (_frameLock)
                    {
                        frame = _latestFrame;
                    }
                    if (frame != null && frame.Length > 0)
                    {
                        response.ContentType = "image/jpeg";
                        response.ContentLength64 = frame.Length;
                        response.OutputStream.Write(frame, 0, frame.Length);
                    }
                    else
                    {
                        response.StatusCode = 204;
                    }
                }
                else
                {
                    byte[] html = Encoding.UTF8.GetBytes(Html);
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = html.Length;
                    response.OutputStream.Write(html, 0, html.Length);
                }
                response.OutputStream.Close();
            }
            catch (HttpListenerException)
            {
                if (!_running) break;
            }
            catch (Exception e)
            {
                if (_running) Debug.LogException(e);
            }
        }
    }

    private void OnDestroy()
    {
        _running = false;
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { /* ignore */ }
        _listener = null;
        if (_captureCam != null)
        {
            Destroy(_captureCam.gameObject);
            _captureCam = null;
        }
        if (_captureRT != null)
        {
            _captureRT.Release();
            _captureRT = null;
        }
        if (_readbackTex != null)
        {
            Destroy(_readbackTex);
            _readbackTex = null;
        }
    }
}
