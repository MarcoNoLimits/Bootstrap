using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR;
using System.Collections.Generic;

public class XRDebugLogger : MonoBehaviour
{
    [Tooltip("Small label under the rail — keeps Settings button free for real feedback.")]
    public Label statusLabel;

    private List<InputDevice> _devices = new List<InputDevice>();

    void Update()
    {
        if (statusLabel == null) return;

        // Bottom HUD must show sign caption (letter, text, hint) — not send/capture telemetry.
        string infer = SignInferenceClient.LiveCaptionForHud;
        if (!string.IsNullOrEmpty(infer))
        {
            statusLabel.text = infer;
            return;
        }

        string status = "Hands: ";
        GetDeviceStatus(XRNode.LeftHand, ref status);
        GetDeviceStatus(XRNode.RightHand, ref status);
        statusLabel.text = status;
    }

    void GetDeviceStatus(XRNode node, ref string status)
    {
        _devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, _devices);
        
        string side = node == XRNode.LeftHand ? "L" : "R";
        
        if (_devices.Count == 0)
        {
            status += $"{side}:NoDev ";
        }
        else
        {
            bool tracked = false;
            if (_devices[0].TryGetFeatureValue(CommonUsages.isTracked, out tracked) && tracked)
            {
                // Check position to see if it's stuck at 0,0,0 (common failure)
                if (_devices[0].TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos))
                {
                    status += $"{side}:OK "; //({pos.x:F1},{pos.y:F1}) ";
                }
                else
                {
                    status += $"{side}:NoPos ";
                }
            }
            else
            {
                status += $"{side}:Lost ";
            }
        }
    }
}
