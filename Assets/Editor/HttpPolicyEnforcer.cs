using UnityEditor;
using UnityEngine;

/// <summary>
/// Keeps project HTTP download policy on "Always allowed" for local LAN inference endpoints.
/// </summary>
public static class HttpPolicyEnforcer
{
    [InitializeOnLoadMethod]
    private static void EnsureHttpAllowed()
    {
        if (PlayerSettings.insecureHttpOption == InsecureHttpOption.AlwaysAllowed)
        {
            return;
        }

        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        AssetDatabase.SaveAssets();
        Debug.Log("[HTTP Policy] Set PlayerSettings.insecureHttpOption = AlwaysAllowed.");
    }
}
