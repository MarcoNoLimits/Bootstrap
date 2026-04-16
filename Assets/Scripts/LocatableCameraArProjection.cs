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
    }

    /// <summary>
    /// Projects a world-space point to pixel coordinates in the same orientation as the Unity texture used for PV (typically WebCamTexture).
    /// </summary>
    public bool TryWorldToTexturePixel(
        Vector3 worldPosition,
        int textureWidth,
        int textureHeight,
        out Vector2 pixel)
    {
        pixel = default;
        if (_arCameraManager == null || _camera == null || textureWidth <= 0 || textureHeight <= 0)
        {
            return false;
        }

        if (!_arCameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
        {
            return false;
        }

        Vector3 view = _camera.worldToCameraMatrix.MultiplyPoint(worldPosition);
        // Unity camera view space: in front of the camera, Z is negative.
        if (view.z >= -1e-5f)
        {
            return false;
        }

        float invZ = -1f / view.z;
        float u = intrinsics.focalLength.x * view.x * invZ + intrinsics.principalPoint.x;
        float v = intrinsics.focalLength.y * view.y * invZ + intrinsics.principalPoint.y;

        Vector2Int res = intrinsics.resolution;
        if (res.x <= 0 || res.y <= 0)
        {
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
