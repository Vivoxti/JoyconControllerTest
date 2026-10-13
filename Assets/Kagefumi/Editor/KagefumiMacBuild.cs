using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kagefumi.EditorTools
{
    /// <summary>
    /// Development macOS build of KAGEFUMI for the Joy-Con 2 CoreBluetooth tests (Plugins/macOS/Joycon2Ble.bundle).
    /// First launch: macOS asks for Bluetooth access; if it was denied — System Settings → Privacy &amp; Security → Bluetooth.
    /// </summary>
    public static class KagefumiMacBuild
    {
        const string Scene = "Assets/Kagefumi/Scenes/Kagefumi.unity";
        const string Output = "Builds/macOS/Kagefumi.app";
        const string BluetoothUsage = "Joy-Con 2 controllers connect over Bluetooth.";

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

            EnsureBluetoothUsageDescription();

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

        /// <summary>
        /// Without NSBluetoothAlwaysUsageDescription in Info.plist macOS kills the player on its first CoreBluetooth call
        /// (the plugin refuses to start instead). Player Settings → macOS → Bluetooth Usage Description has no public API.
        /// </summary>
        static void EnsureBluetoothUsageDescription()
        {
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var description = settings.FindProperty("bluetoothUsageDescription");
            if (description == null || !string.IsNullOrEmpty(description.stringValue)) return;
            description.stringValue = BluetoothUsage;
            settings.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log($"[Kagefumi] Bluetooth Usage Description was empty, set to \"{BluetoothUsage}\"");
        }
    }
}
