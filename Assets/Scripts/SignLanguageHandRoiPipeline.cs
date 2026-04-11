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

    [Header("Hand selection")]
    [SerializeField] private SignDominantHandPolicy dominantPolicy = SignDominantHandPolicy.PreferRight;

    [Header("ROI")]
    [Tooltip("Pad as a fraction of max(width, height), clamped by min padding.")]
    [SerializeField, Range(0.05f, 0.35f)] private float padFraction = 0.12f;

    [SerializeField] private int minPadPixels = 20;

    [SerializeField] private int minRoiDimensionPixels = 32;

    private XRHandSubsystem _handSubsystem;

    /// <summary>Last successful ROI in PV/WebCam texture pixel space (bottom-left origin).</summary>
    public RectInt LastRoi { get; private set; }

    /// <summary>True when the last <see cref="TryGetHandRoiInPvPixels"/> had a tracked hand and valid ROI.</summary>
    public bool LastHadValidRoi { get; private set; }

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

        if (locatableCamera == null || xrOrigin == null)
        {
            return false;
        }

        TryAcquireHandSubsystem();
        if (_handSubsystem == null)
        {
            return false;
        }

        if (!TryPickHand(out XRHand hand))
        {
            handTracked = false;
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
                return false;
            }

            if (!joint.TryGetPose(out Pose localPose))
            {
                return false;
            }

            Vector3 world = JointPositionToWorld(localPose, origin);
            worldPoints.Add(world);
        }

        // Texture size must match the active PV/WebCamTexture used for readback (see SetPvTextureDimensions).
        int w = _lastPvWidth > 0 ? _lastPvWidth : 896;
        int h = _lastPvHeight > 0 ? _lastPvHeight : 504;

        float minU = float.PositiveInfinity, maxU = float.NegativeInfinity, minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
        for (int i = 0; i < worldPoints.Count; i++)
        {
            if (!locatableCamera.TryWorldToTexturePixel(worldPoints[i], w, h, out Vector2 uv))
            {
                return false;
            }

            minU = Mathf.Min(minU, uv.x);
            maxU = Mathf.Max(maxU, uv.x);
            minV = Mathf.Min(minV, uv.y);
            maxV = Mathf.Max(maxV, uv.y);
        }

        int ix0 = Mathf.FloorToInt(minU);
        int iy0 = Mathf.FloorToInt(minV);
        int ix1 = Mathf.CeilToInt(maxU);
        int iy1 = Mathf.CeilToInt(maxV);

        int boxW = Mathf.Max(1, ix1 - ix0);
        int boxH = Mathf.Max(1, iy1 - iy0);
        int pad = Mathf.Max(minPadPixels, Mathf.RoundToInt(Mathf.Max(boxW, boxH) * padFraction));

        int rx0 = ix0 - pad;
        int ry0 = iy0 - pad;
        int rw = boxW + pad * 2;
        int rh = boxH + pad * 2;

        rx0 = Mathf.Clamp(rx0, 0, w - 1);
        ry0 = Mathf.Clamp(ry0, 0, h - 1);
        int rx1 = Mathf.Clamp(rx0 + rw - 1, 0, w - 1);
        int ry1 = Mathf.Clamp(ry0 + rh - 1, 0, h - 1);

        roi = new RectInt(rx0, ry0, Mathf.Max(1, rx1 - rx0 + 1), Mathf.Max(1, ry1 - ry0 + 1));
        if (roi.width < minRoiDimensionPixels || roi.height < minRoiDimensionPixels)
        {
            return false;
        }

        LastRoi = roi;
        LastHadValidRoi = true;
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
        return joint.trackingState.HasFlag(XRHandJointTrackingState.Position);
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
