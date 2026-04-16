using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Projects world-space points into pixel coordinates on the HoloLens photo/video (PV) image using the
/// official AR Foundation locatable-camera path: <see cref="ARCameraManager"/> intrinsics
/// (<see cref="XRCameraIntrinsics"/>) plus the <see cref="Camera.worldToCameraMatrix"/> from the PV camera.
/// <para>
/// On HoloLens with Mixed Reality OpenXR + AR Foundation, the AR session camera is the PV/locatable camera.
/// Add this to the same GameObject as <see cref="ARCameraManager"/> and the PV <see cref="Camera"/>.
/// </para>
/// <para>
/// Resolution: match <see cref="UnityEngine.WebCamTexture"/> to the same profile as intrinsics (this project uses 896×504 for PV).
/// If the active texture size differs from <see cref="XRCameraIntrinsics.resolution"/>, pixels are scaled proportionally.
/// </para>
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ARCameraManager))]
public class LocatableCameraArProjection : MonoBehaviour
{
    [SerializeField] private ARCameraManager _arCameraManager;
    [SerializeField] private Camera _camera;

    [Header("Image layout vs Unity texture")]
    [Tooltip("If true, flips V after projection (camera image top-left vs texture bottom-left). Usually enable for PV.")]
    [SerializeField] private bool flipImageY = true;

    [Tooltip("If true, mirrors U (some runtimes / preview paths flip horizontally).")]
    [SerializeField] private bool mirrorImageX;

    private void Reset()
    {
        _arCameraManager = GetComponent<ARCameraManager>();
        _camera = GetComponent<Camera>();
    }

    private void Awake()
    {
        EnsureArSessionExists();

        if (_arCameraManager == null)
        {
            _arCameraManager = GetComponent<ARCameraManager>();
            if (_arCameraManager == null)
            {
                _arCameraManager = gameObject.AddComponent<ARCameraManager>();
                Debug.Log("[LocatableCameraArProjection] Added missing ARCameraManager on camera.");
            }
        }

        if (_camera == null)
        {
            _camera = GetComponent<Camera>();
        }

        // On HoloLens, AR camera provider startup is more reliable when ARCameraBackground is present.
        if (GetComponent<ARCameraBackground>() == null)
        {
            gameObject.AddComponent<ARCameraBackground>();
            Debug.Log("[LocatableCameraArProjection] Added ARCameraBackground to camera.");
        }
    }

    private static void EnsureArSessionExists()
    {
        ARSession existing = FindObjectOfType<ARSession>();
        if (existing != null)
        {
            return;
        }

        GameObject sessionGo = new GameObject("AR Session (Auto)");
        sessionGo.AddComponent<ARSession>();
        Debug.Log("[LocatableCameraArProjection] Added missing ARSession to scene.");
    }

    /// <summary>
    /// Projects a world-space point to pixel coordinates in the same orientation as the Unity texture used for PV (typically WebCamTexture).
    /// </summary>
    /// <summary>True once ARCameraManager has provided intrinsics at least once.</summary>
    public bool IntrinsicsReady { get; private set; }

    /// <summary>Human-readable camera subsystem status for on-screen debug display.</summary>
    public string CameraStatusLine { get; private set; } = "cam:init";

    private int _camDiagFrame;

    private void Update()
    {
        if (!IntrinsicsReady && _arCameraManager != null)
        {
            if (_arCameraManager.TryGetIntrinsics(out _))
            {
                IntrinsicsReady = true;
                CameraStatusLine = "cam:intrinsics_ok";
                Debug.Log("[LocatableCameraArProjection] Intrinsics ready — projection enabled.");
            }
        }

        // Diagnostics: update status line every 90 frames (~3 s at 30 fps)
        _camDiagFrame++;
        if (_camDiagFrame % 90 == 0)
        {
            bool camEnabled = _arCameraManager != null && _arCameraManager.enabled;
            bool subsysExists = _arCameraManager?.subsystem != null;
            bool subsysRunning = subsysExists && _arCameraManager.subsystem.running;
            XRCameraIntrinsics intr = default;
            bool gotIntrinsics = _arCameraManager != null && _arCameraManager.TryGetIntrinsics(out intr);
            string intrRes = gotIntrinsics ? $"{intr.resolution.x}x{intr.resolution.y}" : "none";
            CameraStatusLine = $"cam enabled={camEnabled} sub={subsysExists} run={subsysRunning} intr={intrRes}";
            Debug.Log($"[LocatableCameraArProjection] {CameraStatusLine}");
        }
    }

    /// <summary>Last sub-reason why TryWorldToTexturePixel returned false. Empty when successful.</summary>
    public string LastProjectionFailReason { get; private set; } = "";

    public bool TryWorldToTexturePixel(
        Vector3 worldPosition,
        int textureWidth,
        int textureHeight,
        out Vector2 pixel)
    {
        pixel = default;
        if (_arCameraManager == null || _camera == null || textureWidth <= 0 || textureHeight <= 0)
        {
            LastProjectionFailReason = "null_refs(arCam=" + (_arCameraManager != null) + ",cam=" + (_camera != null) + ")";
            return false;
        }

        if (!_arCameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
        {
            LastProjectionFailReason = "intrinsics_not_ready";
            return false;
        }

        Vector3 view = _camera.worldToCameraMatrix.MultiplyPoint(worldPosition);
        // Unity camera view space: in front of the camera, Z is negative.
        if (view.z >= -1e-5f)
        {
            LastProjectionFailReason = "joint_behind_camera(z=" + view.z.ToString("0.00") + ")";
            return false;
        }

        float invZ = -1f / view.z;
        float u = intrinsics.focalLength.x * view.x * invZ + intrinsics.principalPoint.x;
        float v = intrinsics.focalLength.y * view.y * invZ + intrinsics.principalPoint.y;

        Vector2Int res = intrinsics.resolution;
        if (res.x <= 0 || res.y <= 0)
        {
            LastProjectionFailReason = "intrinsics_res_zero";
            return false;
        }

        // Scale from intrinsics resolution to the active texture (WebCamTexture size).
        float sx = textureWidth / (float)res.x;
        float sy = textureHeight / (float)res.y;
        u *= sx;
        v *= sy;

        if (mirrorImageX)
        {
            u = textureWidth - 1f - u;
        }

        if (flipImageY)
        {
            v = textureHeight - 1f - v;
        }

        pixel = new Vector2(u, v);
        return true;
    }
}
