using UnityEngine;

/// <summary>
/// Stage 1 only: reads PV camera frame, computes hand ROI, and logs ROI validity metrics.
/// No API/inference requests are made by this component.
/// </summary>
[DefaultExecutionOrder(-34)]
public class SignStage1RoiDriver : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private SignLanguageHandRoiPipeline pipeline;
    [SerializeField] private Texture overrideSource;

    [Header("Camera Source")]
    [SerializeField] private bool useWebCamTexture = true;
    [SerializeField] private int requestWidth = 896;
    [SerializeField] private int requestHeight = 504;
    [SerializeField] private int requestFps = 30;

    [Header("Debug Config (Stage 1)")]
    [SerializeField] private bool logRoiMetrics = true;
    [SerializeField] private int logEveryNFrames = 15;

    private WebCamTexture _webCamTexture;
    private int _frameCounter;

    private void Start()
    {
        if (useWebCamTexture)
        {
            StartWebCam();
        }
    }

    private void Update()
    {
        if (pipeline == null)
        {
            return;
        }

        Texture src = GetActiveSourceTexture();
        if (src == null)
        {
            return;
        }

        pipeline.SetPvTextureDimensions(src.width, src.height);
        bool valid = pipeline.TryGetHandRoiInPvPixels(out RectInt roi, out bool handTracked);
        _frameCounter++;

        if (!logRoiMetrics || (_frameCounter % Mathf.Max(1, logEveryNFrames)) != 0)
        {
            return;
        }

        string reason = valid ? "ok" : pipeline.LastInvalidReason;
        Debug.Log(
            $"[SignStage1RoiDriver] valid={valid} tracked={handTracked} bbox={roi.width}x{roi.height} areaFrac={pipeline.LastAreaFraction:0.000} reason={reason}");
    }

    private void OnDisable()
    {
        StopWebCam();
    }

    private void OnDestroy()
    {
        StopWebCam();
    }

    private Texture GetActiveSourceTexture()
    {
        if (overrideSource != null)
        {
            return overrideSource;
        }

        if (_webCamTexture != null && _webCamTexture.isPlaying && _webCamTexture.width > 16)
        {
            return _webCamTexture;
        }

        return null;
    }

    private void StartWebCam()
    {
        if (_webCamTexture != null)
        {
            return;
        }

        if (WebCamTexture.devices == null || WebCamTexture.devices.Length == 0)
        {
            Debug.LogWarning("[SignStage1RoiDriver] No camera devices found.");
            return;
        }

        string deviceName = WebCamTexture.devices[0].name;
        _webCamTexture = new WebCamTexture(deviceName, requestWidth, requestHeight, requestFps);
        _webCamTexture.Play();
        Debug.Log($"[SignStage1RoiDriver] Camera started: {deviceName} ({requestWidth}x{requestHeight}@{requestFps})");
    }

    private void StopWebCam()
    {
        if (_webCamTexture == null)
        {
            return;
        }

        try
        {
            if (_webCamTexture.isPlaying)
            {
                _webCamTexture.Stop();
            }
        }
        finally
        {
            Destroy(_webCamTexture);
            _webCamTexture = null;
        }
    }
}
