using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR;
using System.Collections.Generic;

public class XRDebugLogger : MonoBehaviour
{
    public Button debugButton; // We'll re-use the settings button to show text
    private List<InputDevice> _devices = new List<InputDevice>();

    void Update()
    {
        if (debugButton == null) return;

        string status = "Hands: ";
        GetDeviceStatus(XRNode.LeftHand, ref status);
        GetDeviceStatus(XRNode.RightHand, ref status);
        
        // Also check Head
        // GetDeviceStatus(XRNode.Head, ref status);

        // Update UI (throttled?) - performing every frame is fine for simple text
        debugButton.text = status;
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
