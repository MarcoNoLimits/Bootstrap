using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;
using Unity.XR.CoreUtils;

/// <summary>
/// Dominant-hand policy for sign-language ROI (OpenXR hand tracking via <see cref="XRHandSubsystem"/>).
/// </summary>
public enum SignDominantHandPolicy
{
    PreferRight = 0,
    PreferLeft = 1,
    RightOnly = 2,
    LeftOnly = 3
}

/// <summary>
/// HoloLens 2 sign pipeline helper: reads hand joints (wrist + finger tips), projects them into PV texture pixels
/// using <see cref="LocatableCameraArProjection"/> (AR Foundation locatable camera), builds a padded, clamped <see cref="RectInt"/> ROI.
/// Exposes <see cref="TryGetHandRoiInPvPixels"/> for inference gating and debug overlays.
/// <para>
/// Setup: enable OpenXR <em>Hand Tracking</em> in XR Plug-in Management; add <see cref="XROrigin"/>; assign PV
/// <see cref="UnityEngine.XR.ARFoundation.ARCameraManager"/> + <see cref="LocatableCameraArProjection"/> on the PV camera object.
/// PV resolution documented for this app: 896×504 (see <see cref="SignInferenceClient"/> WebCamTexture request).
/// </para>
/// </summary>
[DefaultExecutionOrder(-35)]
public class SignLanguageHandRoiPipeline : MonoBehaviour
{
    private static readonly XRHandJointID[] s_RoiJoints =
    {
        XRHandJointID.Wrist,
        XRHandJointID.ThumbTip,
        XRHandJointID.IndexTip,
        XRHandJointID.MiddleTip,
        XRHandJointID.RingTip,
        XRHandJointID.LittleTip
    };

    [Header("References")]
    [SerializeField] private XROrigin xrOrigin;
    [SerializeField] private LocatableCameraArProjection locatableCamera;

    /// <summary>Exposed for diagnostics from SignInferenceClient.</summary>
    public LocatableCameraArProjection LocatableCamera => locatableCamera;

    [Header("Hand selection")]
    [SerializeField] private SignDominantHandPolicy dominantPolicy = SignDominantHandPolicy.PreferRight;

    [Header("ROI Config (Stage 1)")]
    [Tooltip("Padding added to all bbox sides. Matches contract baseline behavior (pad=40).")]
    [SerializeField] private int paddingPixels = 40;
    [Tooltip("Minimum width/height in pixels. Smaller boxes are invalid.")]
    [SerializeField] private int minRoiDimensionPixels = 32;
    [Tooltip("Minimum hand area as a fraction of frame area.")]
    [SerializeField, Range(0f, 1f)] private float minHandAreaFraction = 0.01f;
    [Tooltip("When enabled, smooths ROI corners frame-to-frame to reduce jitter.")]
    [SerializeField] private bool smoothRoi = true;
    [SerializeField, Range(0.01f, 1f)] private float roiSmoothing = 0.35f;
    [Tooltip("Minimum number of projected joints required to build ROI. Allows tolerance when one or two joints fail projection.")]
    [SerializeField, Range(3, 6)] private int minProjectedJoints = 4;

    private XRHandSubsystem _handSubsystem;

    /// <summary>Last successful ROI in PV/WebCam texture pixel space (bottom-left origin).</summary>
    public RectInt LastRoi { get; private set; }

    /// <summary>True when the last <see cref="TryGetHandRoiInPvPixels"/> had a tracked hand and valid ROI.</summary>
    public bool LastHadValidRoi { get; private set; }
    public bool LastRoiValid { get; private set; }
    public float LastAreaFraction { get; private set; }
    public string LastInvalidReason { get; private set; } = "";
    private Rect _smoothedRoi;
    private bool _hasSmoothedRoi;

    private void Awake()
    {
        if (xrOrigin == null)
        {
            xrOrigin = FindObjectOfType<XROrigin>();
        }
    }

    private void OnEnable()
    {
        TryAcquireHandSubsystem();
    }

    private void TryAcquireHandSubsystem()
    {
        if (_handSubsystem != null)
        {
            return;
        }

        var subs = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subs);
        if (subs.Count > 0)
        {
            _handSubsystem = subs[0];
            return;
        }

        if (XRGeneralSettings.Instance != null
            && XRGeneralSettings.Instance.Manager != null
            && XRGeneralSettings.Instance.Manager.activeLoader != null)
        {
            _handSubsystem = XRGeneralSettings.Instance.Manager.activeLoader.GetLoadedSubsystem<XRHandSubsystem>();
        }
    }

    /// <summary>
    /// Computes ROI in PV/WebCam texture pixels. Returns false when the hand is not tracked, joints are invalid, projection is unavailable, or ROI is too small.
    /// </summary>
    public bool TryGetHandRoiInPvPixels(out RectInt roi, out bool handTracked)
    {
        roi = default;
        handTracked = false;
        LastHadValidRoi = false;
        LastRoiValid = false;
        LastAreaFraction = 0f;
        LastInvalidReason = "";

        if (locatableCamera == null || xrOrigin == null)
        {
            LastInvalidReason = "missing_references";
            return false;
        }

        if (!locatableCamera.IntrinsicsReady)
        {
            LastInvalidReason = "intrinsics_not_ready";
            return false;
        }

        TryAcquireHandSubsystem();
        if (_handSubsystem == null)
        {
            LastInvalidReason = "no_hand_subsystem";
            return false;
        }

        if (!TryPickHand(out XRHand hand))
        {
            handTracked = false;
            LastInvalidReason = "hand_not_tracked";
            return false;
        }

        handTracked = true;

        Transform origin = xrOrigin.transform;
        var worldPoints = new List<Vector3>(s_RoiJoints.Length);
        for (int i = 0; i < s_RoiJoints.Length; i++)
        {
            XRHandJoint joint = hand.GetJoint(s_RoiJoints[i]);
            if (!IsJointUsable(joint))
            {
                LastInvalidReason = "joint_not_usable";
                return false;
            }

            if (!joint.TryGetPose(out Pose localPose))
            {
                LastInvalidReason = "joint_pose_missing";
                return false;
            }

            Vector3 world = JointPositionToWorld(localPose, origin);
            worldPoints.Add(world);
        }

        // Texture size must match the active PV/WebCamTexture used for readback (see SetPvTextureDimensions).
        int w = _lastPvWidth > 0 ? _lastPvWidth : 896;
        int h = _lastPvHeight > 0 ? _lastPvHeight : 504;

        float minU = float.PositiveInfinity, maxU = float.NegativeInfinity, minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
        int projectedCount = 0;
        for (int i = 0; i < worldPoints.Count; i++)
        {
            if (!locatableCamera.TryWorldToTexturePixel(worldPoints[i], w, h, out Vector2 uv))
            {
                continue;
            }

            minU = Mathf.Min(minU, uv.x);
            maxU = Mathf.Max(maxU, uv.x);
            minV = Mathf.Min(minV, uv.y);
            maxV = Mathf.Max(maxV, uv.y);
            projectedCount++;
        }

        if (projectedCount < Mathf.Clamp(minProjectedJoints, 3, s_RoiJoints.Length))
        {
            string subReason = locatableCamera.LastProjectionFailReason;
            LastInvalidReason = "projection_failed:" + (string.IsNullOrEmpty(subReason) ? "unknown" : subReason);
            _hasSmoothedRoi = false;
            return false;
        }

        int ix0 = Mathf.FloorToInt(minU);
        int iy0 = Mathf.FloorToInt(minV);
        int ix1 = Mathf.CeilToInt(maxU);
        int iy1 = Mathf.CeilToInt(maxV);

        int boxW = Mathf.Max(1, ix1 - ix0 + 1);
        int boxH = Mathf.Max(1, iy1 - iy0 + 1);
        int pad = Mathf.Max(0, paddingPixels);

        int rx0 = ix0 - pad;
        int ry0 = iy0 - pad;
        int rw = boxW + pad * 2;
        int rh = boxH + pad * 2;

        rx0 = Mathf.Clamp(rx0, 0, w - 1);
        ry0 = Mathf.Clamp(ry0, 0, h - 1);
        int rx1 = Mathf.Clamp(rx0 + rw - 1, 0, w - 1);
        int ry1 = Mathf.Clamp(ry0 + rh - 1, 0, h - 1);

        roi = new RectInt(rx0, ry0, Mathf.Max(1, rx1 - rx0 + 1), Mathf.Max(1, ry1 - ry0 + 1));
        if (smoothRoi)
        {
            roi = SmoothRoi(roi);
        }

        LastAreaFraction = (roi.width * roi.height) / Mathf.Max(1f, w * h);

        if (roi.width < minRoiDimensionPixels || roi.height < minRoiDimensionPixels)
        {
            LastInvalidReason = "roi_too_small";
            _hasSmoothedRoi = false;
            return false;
        }
        if (LastAreaFraction < minHandAreaFraction)
        {
            LastInvalidReason = "area_fraction_too_small";
            _hasSmoothedRoi = false;
            return false;
        }

        LastRoi = roi;
        LastHadValidRoi = true;
        LastRoiValid = true;
        return true;
    }

    private int _lastPvWidth;
    private int _lastPvHeight;

    /// <summary>Last dimensions passed to <see cref="SetPvTextureDimensions"/> (for debug overlay scaling).</summary>
    public int LastPvTextureWidth => _lastPvWidth > 0 ? _lastPvWidth : 896;

    /// <summary>Last dimensions passed to <see cref="SetPvTextureDimensions"/> (for debug overlay scaling).</summary>
    public int LastPvTextureHeight => _lastPvHeight > 0 ? _lastPvHeight : 504;

    /// <summary>
    /// Must be called each frame before <see cref="TryGetHandRoiInPvPixels"/> with the same dimensions as the active PV <see cref="UnityEngine.WebCamTexture"/> (or override texture).
    /// </summary>
    public void SetPvTextureDimensions(int width, int height)
    {
        _lastPvWidth = width;
        _lastPvHeight = height;
    }

    private static Vector3 JointPositionToWorld(Pose jointPose, Transform xrOriginTransform)
    {
        return xrOriginTransform.position + xrOriginTransform.rotation * jointPose.position;
    }

    private static bool IsJointUsable(XRHandJoint joint)
    {
        return joint.trackingState.HasFlag(XRHandJointTrackingState.Pose);
    }

    private RectInt SmoothRoi(RectInt raw)
    {
        Rect current = new Rect(raw.x, raw.y, raw.width, raw.height);
        if (!_hasSmoothedRoi)
        {
            _smoothedRoi = current;
            _hasSmoothedRoi = true;
        }
        else
        {
            float t = Mathf.Clamp01(roiSmoothing);
            _smoothedRoi.x = Mathf.Lerp(_smoothedRoi.x, current.x, t);
            _smoothedRoi.y = Mathf.Lerp(_smoothedRoi.y, current.y, t);
            _smoothedRoi.width = Mathf.Lerp(_smoothedRoi.width, current.width, t);
            _smoothedRoi.height = Mathf.Lerp(_smoothedRoi.height, current.height, t);
        }

        return new RectInt(
            Mathf.RoundToInt(_smoothedRoi.x),
            Mathf.RoundToInt(_smoothedRoi.y),
            Mathf.Max(1, Mathf.RoundToInt(_smoothedRoi.width)),
            Mathf.Max(1, Mathf.RoundToInt(_smoothedRoi.height)));
    }

    private bool TryPickHand(out XRHand hand)
    {
        XRHand left = _handSubsystem.leftHand;
        XRHand right = _handSubsystem.rightHand;
        bool leftOk = left.isTracked;
        bool rightOk = right.isTracked;

        switch (dominantPolicy)
        {
            case SignDominantHandPolicy.RightOnly:
                if (!rightOk)
                {
                    hand = default;
                    return false;
                }

                hand = right;
                return true;
            case SignDominantHandPolicy.LeftOnly:
                if (!leftOk)
                {
                    hand = default;
                    return false;
                }

                hand = left;
                return true;
            case SignDominantHandPolicy.PreferRight:
                if (rightOk)
                {
                    hand = right;
                    return true;
                }

                if (leftOk)
                {
                    hand = left;
                    return true;
                }

                hand = default;
                return false;
            case SignDominantHandPolicy.PreferLeft:
                if (leftOk)
                {
                    hand = left;
                    return true;
                }

                if (rightOk)
                {
                    hand = right;
                    return true;
                }

                hand = default;
                return false;
            default:
                if (rightOk)
                {
                    hand = right;
                    return true;
                }

                if (leftOk)
                {
                    hand = left;
                    return true;
                }

                hand = default;
                return false;
        }
    }
}
