using UnityEditor;
using UnityEngine;
using System.IO;

public static class BuildScript
{
    public static void BuildAndroidAPK()
    {
        var scenes = EditorBuildSettings.scenes;
        var outDir = Path.Combine(Application.dataPath, "..", "Builds");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "OpenQuestCapture-1.2.1.apk");

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        PlayerSettings.Android.bundleVersionCode = 7;
        PlayerSettings.bundleVersion = "1.2.1";

        var report = BuildPipeline.BuildPlayer(scenes, outPath, BuildTarget.Android, BuildOptions.None);
        Debug.Log($"[BuildScript] Result: {report.summary.result}, Size: {report.summary.totalSize}, Output: {outPath}");
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
        }
    }
}
