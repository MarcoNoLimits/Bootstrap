using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Subsystems;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// HoloLens **photo/video (PV)** via AR Foundation — the only supported capture path here is
/// <see cref="ARCameraManager.TryAcquireLatestCpuImage"/> (<see cref="XRCpuImage"/>).
/// Do not use <c>WebCamTexture</c> or raw WinRT camera APIs for this pipeline; they fight PV/locatable camera on UWP.
/// <para><b>Scene:</b> on the **AR Main Camera** (under XR Origin): <c>Camera</c>, <c>ARCameraManager</c>,
/// <c>HololensPvCpuImageSource</c>; add <c>ARCameraBackground</c> if you render the passthrough camera feed (recommended).</para>
/// <para><b>Orientation:</b> use inspector <see cref="mirrorY"/>; try <c>Transformation.None</c> if the model sees a flipped image.</para>
/// <para><b>OpenXR:</b> AR Foundation <b>5.1+</b> is required for HoloLens so Unity registers <see cref="XRCameraSubsystem"/> with OpenXR.
/// AR Foundation 4.x assumed the legacy <i>Windows XR Plugin</i> for HL camera; OpenXR-only projects will see “AR camera subsystem not running” until you upgrade.</para>
/// </summary>
[DefaultExecutionOrder(-35)]
public sealed class HololensPvCpuImageSource : MonoBehaviour
{
    [SerializeField] private ARCameraManager arCameraManager;

    [Tooltip("Maximum output width in pixels (aspect preserved). Default 640.")]
    [SerializeField] private int maxOutputWidth = 640;

    [Tooltip("If true, crop a centered square (or min-axis) region before resize.")]
    [SerializeField] private bool cropCenterRegion;

    [Tooltip("Fraction of min(fullWidth, fullHeight) used when cropCenterRegion is true.")]
    [SerializeField, Range(0.2f, 1f)] private float centerCropFraction = 0.92f;

    [Tooltip("JPEG quality 1–100.")]
    [SerializeField, Range(1, 100)] private int jpegQuality = 88;

    [Tooltip("Mirror Y when converting (typical PV preview alignment).")]
    [SerializeField] private bool mirrorY = true;

    private Texture2D _jpegSourceTexture;
    private TextureFormat _jpegSourceFormat;
    private float _nextSubsystemLogAt;
    private float _nextStartupNudgeAt;
    private float _nextBlackFrameLogAt;

    private void Awake()
    {
        if (arCameraManager == null)
        {
            arCameraManager = FindObjectOfType<ARCameraManager>();
        }

        maxOutputWidth = Mathf.Clamp(maxOutputWidth, 64, 4096);
        jpegQuality = Mathf.Clamp(jpegQuality, 1, 100);
        centerCropFraction = Mathf.Clamp(centerCropFraction, 0.2f, 1f);
    }

    private void Start()
    {
        // After XR loaders run, an empty list means no plugin registered a camera provider (common: AR Foundation 4.x + OpenXR-only).
        var descs = new List<XRCameraSubsystemDescriptor>();
        SubsystemManager.GetSubsystemDescriptors(descs);
        if (descs.Count == 0)
        {
            Debug.LogWarning(
                "[HololensPvCpuImageSource] No XRCameraSubsystem provider is registered. " +
                "With OpenXR on HoloLens, upgrade com.unity.xr.arfoundation to 5.1+ (Unity docs: OpenXR as HoloLens AR Foundation provider). " +
                "AR Foundation 4.x targeted the legacy Windows XR Plugin for PV.");
        }
        else
        {
            Debug.Log("[HololensPvCpuImageSource] XRCameraSubsystem providers: " + descs.Count);
        }

        // One early nudge so ARSession / ARCameraManager are active before first Sign pipeline frame (reduces "subsystem not running" races).
        RequestStartupNudge();
    }

    /// <summary>
    /// Assign or refresh the camera manager (e.g. from scene load).
    /// </summary>
    public void SetCameraManager(ARCameraManager manager)
    {
        arCameraManager = manager;
    }

    public void SetEncodingOptions(int maxWidth, bool cropCenter, float cropFraction, int jpgQ, bool useMirrorY)
    {
        maxOutputWidth = Mathf.Clamp(maxWidth, 64, 4096);
        cropCenterRegion = cropCenter;
        centerCropFraction = Mathf.Clamp(cropFraction, 0.2f, 1f);
        jpegQuality = Mathf.Clamp(jpgQ, 1, 100);
        mirrorY = useMirrorY;
    }

    /// <summary>
    /// Best-effort startup nudge for cases where AR camera components are present but not yet active/running.
    /// Safe to call repeatedly; internally throttled.
    /// </summary>
    public void RequestStartupNudge()
    {
        TryNudgeCameraStartup(forceLog: true);
    }

    /// <summary>
    /// Returns a compact runtime diagnostics line for on-device troubleshooting.
    /// </summary>
    public string GetRuntimeDiagnosticsSummary()
    {
        if (arCameraManager == null)
        {
            arCameraManager = FindObjectOfType<ARCameraManager>();
        }

        ARSession arSession = FindObjectOfType<ARSession>();
        bool camObjActive = arCameraManager != null && arCameraManager.gameObject.activeInHierarchy;
        bool camEnabled = arCameraManager != null && arCameraManager.enabled;
        bool subsystemPresent = arCameraManager != null && arCameraManager.subsystem != null;
        bool subsystemRunning = subsystemPresent && arCameraManager.subsystem.running;
        bool sessionPresent = arSession != null;
        bool sessionEnabled = arSession != null && arSession.enabled;

        return
            "PV diagnostics: " +
            $"ARCameraManager={(arCameraManager != null ? "found" : "missing")}, " +
            $"camObjActive={camObjActive}, " +
            $"camEnabled={camEnabled}, " +
            $"subsystemPresent={subsystemPresent}, " +
            $"subsystemRunning={subsystemRunning}, " +
            $"ARSession={(sessionPresent ? "found" : "missing")}, " +
            $"sessionEnabled={sessionEnabled}, " +
            $"sessionState={ARSession.state}";
    }

    /// <summary>
    /// Acquire latest PV frame, convert, resize, JPEG-encode.
    /// </summary>
    public bool TryGetJpegFrame(out byte[] jpegBytes, out string errorMessage)
    {
        jpegBytes = null;
        errorMessage = null;

        if (arCameraManager == null)
        {
            arCameraManager = FindObjectOfType<ARCameraManager>();
        }

        if (arCameraManager == null || !arCameraManager.enabled)
        {
            errorMessage = "ARCameraManager missing or disabled";
            return false;
        }

        if (arCameraManager.subsystem == null || !arCameraManager.subsystem.running)
        {
            TryNudgeCameraStartup(forceLog: false);
            errorMessage = "AR camera subsystem not running";
            if (Time.realtimeSinceStartup >= _nextSubsystemLogAt)
            {
                _nextSubsystemLogAt = Time.realtimeSinceStartup + 5f;
                Debug.LogWarning("[HololensPvCpuImageSource] " + errorMessage);
            }
            return false;
        }

        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            TryNudgeCameraStartup(forceLog: false);
            errorMessage = "no CPU image (subsystem starting?)";
            return false;
        }

        using (image)
        {
            try
            {
                int iw = image.width;
                int ih = image.height;
                if (iw <= 0 || ih <= 0)
                {
                    errorMessage = "invalid image size";
                    return false;
                }

                RectInt inputRect = ComputeInputRect(iw, ih);
                Vector2Int outDims = ComputeOutputDimensions(inputRect.width, inputRect.height);

                var transformation = mirrorY ? XRCpuImage.Transformation.MirrorY : XRCpuImage.Transformation.None;
                TextureFormat[] tryFormats = { TextureFormat.RGB24, TextureFormat.RGBA32 };

                foreach (TextureFormat fmt in tryFormats)
                {
                    var conversionParams = new XRCpuImage.ConversionParams
                    {
                        inputRect = inputRect,
                        outputDimensions = outDims,
                        outputFormat = fmt,
                        transformation = transformation
                    };

                    int dataSize = image.GetConvertedDataSize(conversionParams);
                    if (dataSize <= 0)
                    {
                        continue;
                    }

                    NativeArray<byte> raw = new NativeArray<byte>(dataSize, Allocator.Temp);
                    try
                    {
                        bool convertedOk = false;
                        try
                        {
                            image.Convert(conversionParams, new NativeSlice<byte>(raw));
                            convertedOk = true;
                        }
                        catch (Exception ex)
                        {
                            errorMessage = "Convert: " + ex.Message;
                        }

                        if (!convertedOk)
                        {
                            continue;
                        }

                        if (IsRawBufferMostlyBlack(raw, fmt))
                        {
                            if (Time.realtimeSinceStartup >= _nextBlackFrameLogAt)
                            {
                                _nextBlackFrameLogAt = Time.realtimeSinceStartup + 4f;
                                Debug.LogWarning(
                                    "[HololensPvCpuImageSource] Dropping black PV frame (" + fmt +
                                    "). If this repeats: close MRC/Device Portal camera, confirm ARCameraBackground, " +
                                    "and try Player Settings → Multithreaded Rendering on.");
                            }

                            errorMessage = "PV frame is black (subsystem warming up or camera blocked)";
                            continue;
                        }

                        EnsureJpegSourceTexture(outDims.x, outDims.y, fmt);
                        _jpegSourceTexture.LoadRawTextureData(raw);
                        _jpegSourceTexture.Apply(false, false);
                    }
                    finally
                    {
                        raw.Dispose();
                    }

                    jpegBytes = _jpegSourceTexture.EncodeToJPG(jpegQuality);
                    if (jpegBytes == null || jpegBytes.Length == 0)
                    {
                        errorMessage = "JPEG encode failed";
                        continue;
                    }

                    errorMessage = null;
                    return true;
                }

                if (string.IsNullOrEmpty(errorMessage))
                {
                    errorMessage = "PV conversion failed (no compatible format)";
                }

                return false;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }

    private void TryNudgeCameraStartup(bool forceLog)
    {
        if (Time.realtimeSinceStartup < _nextStartupNudgeAt)
        {
            return;
        }

        _nextStartupNudgeAt = Time.realtimeSinceStartup + 1.5f;
        bool changed = false;

        if (arCameraManager == null)
        {
            arCameraManager = FindObjectOfType<ARCameraManager>();
        }

        if (arCameraManager == null)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                cam = FindObjectOfType<Camera>();
            }

            if (cam != null)
            {
                arCameraManager = cam.GetComponent<ARCameraManager>();
                if (arCameraManager == null)
                {
                    arCameraManager = cam.gameObject.AddComponent<ARCameraManager>();
                    changed = true;
                }

                ARCameraBackground bg = cam.GetComponent<ARCameraBackground>();
                if (bg == null)
                {
                    cam.gameObject.AddComponent<ARCameraBackground>();
                    changed = true;
                }
            }
        }

        if (arCameraManager != null)
        {
            if (!arCameraManager.gameObject.activeSelf)
            {
                arCameraManager.gameObject.SetActive(true);
                changed = true;
            }

            if (!arCameraManager.enabled)
            {
                arCameraManager.enabled = true;
                changed = true;
            }
        }

        ARSession arSession = FindObjectOfType<ARSession>();
        if (arSession == null)
        {
            GameObject sessionGo = new GameObject("AR Session (Auto)");
            arSession = sessionGo.AddComponent<ARSession>();
            changed = true;
        }

        if (!arSession.enabled)
        {
            arSession.enabled = true;
            changed = true;
        }

        if (forceLog || changed)
        {
            Debug.Log(
                "[HololensPvCpuImageSource] Startup nudge applied " +
                $"(cameraManager={(arCameraManager != null ? "found" : "missing")}, " +
                $"cameraManagerEnabled={(arCameraManager != null && arCameraManager.enabled)}, " +
                $"cameraObjectActive={(arCameraManager != null && arCameraManager.gameObject.activeSelf)}, " +
                $"arSession={(arSession != null ? "found" : "missing")}, " +
                $"arSessionEnabled={(arSession != null && arSession.enabled)}).");
        }
    }

    private RectInt ComputeInputRect(int fullW, int fullH)
    {
        if (!cropCenterRegion)
        {
            return new RectInt(0, 0, fullW, fullH);
        }

        int side = Mathf.RoundToInt(Mathf.Min(fullW, fullH) * centerCropFraction);
        side = Mathf.Clamp(side, 16, Mathf.Min(fullW, fullH));
        int x = (fullW - side) / 2;
        int y = (fullH - side) / 2;
        return new RectInt(x, y, side, side);
    }

    private Vector2Int ComputeOutputDimensions(int cropW, int cropH)
    {
        if (cropW <= 0 || cropH <= 0)
        {
            return new Vector2Int(16, 16);
        }

        float aspect = cropW / (float)cropH;
        if (cropW <= maxOutputWidth)
        {
            return new Vector2Int(cropW, cropH);
        }

        int ow = maxOutputWidth;
        int oh = Mathf.Max(1, Mathf.RoundToInt(ow / aspect));
        return new Vector2Int(ow, oh);
    }

    private static bool IsRawBufferMostlyBlack(NativeArray<byte> raw, TextureFormat fmt)
    {
        int bpp = fmt == TextureFormat.RGB24 ? 3 : 4;
        if (raw.Length < bpp * 16)
        {
            return false;
        }

        int pixels = raw.Length / bpp;
        int step = Mathf.Max(1, pixels / 600);
        for (int p = 0; p < pixels; p += step)
        {
            int i = p * bpp;
            if (i + 2 >= raw.Length)
            {
                break;
            }

            if (raw[i] > 12 || raw[i + 1] > 12 || raw[i + 2] > 12)
            {
                return false;
            }
        }

        return true;
    }

    private void EnsureJpegSourceTexture(int w, int h, TextureFormat fmt)
    {
        if (_jpegSourceTexture != null && _jpegSourceTexture.width == w && _jpegSourceTexture.height == h
            && _jpegSourceFormat == fmt)
        {
            return;
        }

        if (_jpegSourceTexture != null)
        {
            Destroy(_jpegSourceTexture);
        }

        _jpegSourceFormat = fmt;
        _jpegSourceTexture = new Texture2D(w, h, fmt, false, false);
    }

    private void OnDestroy()
    {
        if (_jpegSourceTexture != null)
        {
            Destroy(_jpegSourceTexture);
            _jpegSourceTexture = null;
        }
    }
}
