using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kagefumi.EditorTools
{
    /// <summary>
    /// Development macOS build of KAGEFUMI for the Joy-Con 2 tests (the package fills the Bluetooth usage description).
    /// First launch: macOS asks for Bluetooth access; if it was denied — System Settings → Privacy &amp; Security → Bluetooth.
    /// </summary>
    public static class KagefumiMacBuild
    {
        const string Scene = "Assets/Kagefumi/Scenes/Kagefumi.unity";
        const string Output = "Builds/macOS/Kagefumi.app";

        [MenuItem("Kagefumi/Build macOS (dev)")]
        public static void Build() => BuildMac();

        /// <summary>Unity -batchmode -quit -projectPath . -executeMethod Kagefumi.EditorTools.KagefumiMacBuild.BuildFromCommandLine</summary>
        public static void BuildFromCommandLine()
        {
            if (!BuildMac()) EditorApplication.Exit(1);
        }

        static bool BuildMac()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX))
            {
                const string message = "Mac Build Support is not installed for this Unity version.";
                if (Application.isBatchMode) Debug.LogError("[Kagefumi] " + message);
                else EditorUtility.DisplayDialog("KAGEFUMI macOS", message, "OK");
                return false;
            }

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = Output,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.Development, // keeps the Joy-Con overlay (F9) on
            });

            var summary = report.summary;
            bool ok = summary.result == BuildResult.Succeeded;
            if (ok)
                Debug.Log($"[Kagefumi] macOS build: {System.IO.Path.GetFullPath(Output)} ({summary.totalSize / (1024 * 1024)} MB, " +
                          $"{summary.totalTime.TotalSeconds:0} s)");
            else
                Debug.LogError($"[Kagefumi] macOS build {summary.result}: {summary.totalErrors} errors");
            return ok;
        }
    }
}
