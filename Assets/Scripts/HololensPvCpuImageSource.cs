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

    private Texture2D _rgbaTexture;

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
            errorMessage = "AR camera subsystem not running";
            return false;
        }

        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
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

                var conversionParams = new XRCpuImage.ConversionParams
                {
                    inputRect = inputRect,
                    outputDimensions = outDims,
                    outputFormat = TextureFormat.RGBA32,
                    transformation = mirrorY ? XRCpuImage.Transformation.MirrorY : XRCpuImage.Transformation.None
                };

                int dataSize = image.GetConvertedDataSize(conversionParams);
                if (dataSize <= 0)
                {
                    errorMessage = "GetConvertedDataSize failed";
                    return false;
                }

                NativeArray<byte> raw = new NativeArray<byte>(dataSize, Allocator.Temp);
                try
                {
                    image.Convert(conversionParams, new NativeSlice<byte>(raw));
                }
                catch (Exception ex)
                {
                    errorMessage = "Convert: " + ex.Message;
                    return false;
                }

                try
                {
                    EnsureRgbaTexture(outDims.x, outDims.y);
                    _rgbaTexture.LoadRawTextureData(raw);
                    _rgbaTexture.Apply(false, false);
                }
                finally
                {
                    raw.Dispose();
                }

                jpegBytes = _rgbaTexture.EncodeToJPG(jpegQuality);
                if (jpegBytes == null || jpegBytes.Length == 0)
                {
                    errorMessage = "JPEG encode failed";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
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

    private void EnsureRgbaTexture(int w, int h)
    {
        if (_rgbaTexture != null && _rgbaTexture.width == w && _rgbaTexture.height == h)
        {
            return;
        }

        if (_rgbaTexture != null)
        {
            Destroy(_rgbaTexture);
        }

        _rgbaTexture = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
    }

    private void OnDestroy()
    {
        if (_rgbaTexture != null)
        {
            Destroy(_rgbaTexture);
            _rgbaTexture = null;
        }
    }
}
