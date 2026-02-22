#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public class HoloLensCapabilitySetup : EditorWindow
{
    [MenuItem("Tools/HoloLens/Apply UWP Capabilities")]
    public static void ApplyCapabilities()
    {
        // 1. Network Capabilities for the TCP Socket
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.InternetClient, true);
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.InternetClientServer, true);
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.PrivateNetworkClientServer, true);

        // 2. Voice Capability for the DictationRecognizer
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.Microphone, true);

        // 3. Spatial Perception for HoloLens environment tracking and UI placement
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.SpatialPerception, true);

        Debug.Log("Success: UWP Capabilities for the NMT Wizard of Oz demo have been applied!");
    }
}
#endif