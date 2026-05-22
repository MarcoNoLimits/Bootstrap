using UnityEditor;
using System;
using UnityEngine;

public static class BuildCommand
{
    [MenuItem("Build/UWP Build Player")]
    public static void BuildUWP()
    {
        Debug.Log("[BuildCommand] Starting UWP Build Player...");
        
        // Retrieve scenes enabled in Build Settings
        var buildScenes = EditorBuildSettings.scenes;
        if (buildScenes == null || buildScenes.Length == 0)
        {
            Debug.LogError("[BuildCommand] No scenes found in EditorBuildSettings!");
            if (Application.isBatchMode) EditorApplication.Exit(1);
            return;
        }

        string[] scenePaths = new string[buildScenes.Length];
        for (int i = 0; i < buildScenes.Length; i++)
        {
            scenePaths[i] = buildScenes[i].path;
            Debug.Log($"[BuildCommand] Included Scene: {scenePaths[i]}");
        }

        BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
        buildPlayerOptions.scenes = scenePaths;
        buildPlayerOptions.locationPathName = "UWP"; // Root relative path
        buildPlayerOptions.target = BuildTarget.WSAPlayer;
        buildPlayerOptions.options = BuildOptions.None;

        var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
        var summary = report.summary;

        if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.Log($"[BuildCommand] Build Succeeded! Total time: {summary.totalTime.TotalSeconds:F2} seconds. Output path: {summary.outputPath}");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else if (summary.result == UnityEditor.Build.Reporting.BuildResult.Failed)
        {
            Debug.LogError($"[BuildCommand] Build Failed! Total errors: {summary.totalErrors}");
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
