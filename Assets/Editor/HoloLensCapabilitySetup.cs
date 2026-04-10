#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public class HoloLensCapabilitySetup : EditorWindow
{
    [InitializeOnLoadMethod]
    private static void EnsureCapabilitiesOnLoad()
    {
        ApplyCapabilitiesInternal(logResult: false);
    }

    [MenuItem("Tools/HoloLens/Apply UWP Capabilities")]
    public static void ApplyCapabilities()
    {
        ApplyCapabilitiesInternal(logResult: true);
    }

    private static void ApplyCapabilitiesInternal(bool logResult)
    {
        // Network capabilities for API/socket access.
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.InternetClient, true);
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.InternetClientServer, true);
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.PrivateNetworkClientServer, true);

        // Voice + camera for ASR and sign capture.
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.Microphone, true);
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.WebCam, true);

        // HoloLens environment tracking and UI placement.
        PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.SpatialPerception, true);

        AssetDatabase.SaveAssets();

        if (logResult)
        {
            Debug.Log("Success: UWP capabilities have been applied for HoloLens build.");
        }
    }
}
#endif