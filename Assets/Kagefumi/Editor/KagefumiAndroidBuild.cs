using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kagefumi.EditorTools
{
    /// <summary>ARM64 IL2CPP development APK for testing the Android Joy-Con 2 BLE transport.</summary>
    public static class KagefumiAndroidBuild
    {
        const string Scene = "Assets/Kagefumi/Scenes/Kagefumi.unity";
        const string Output = "Builds/Android/Kagefumi-Joycon2-dev.apk";

        [MenuItem("Kagefumi/Configure Android (Joy-Con test)")]
        public static void Configure()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            var id = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            if (string.IsNullOrEmpty(id) || id.StartsWith("com.UnityTechnologies."))
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.vivoderin.kagefumi");
            EditorUserBuildSettings.buildAppBundle = false;
            AssetDatabase.SaveAssets();
            Debug.Log("[Kagefumi] Android configured: ARM64 / IL2CPP / landscape");
        }

        [MenuItem("Kagefumi/Build Android APK (Joy-Con dev)")]
        public static void Build() => BuildAndroid();

        public static void BuildFromCommandLine()
        {
            if (!BuildAndroid()) EditorApplication.Exit(1);
        }

        static bool BuildAndroid()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Debug.LogError("[Kagefumi] Android Build Support is not installed.");
                return false;
            }
            Configure();
            Directory.CreateDirectory(Path.GetDirectoryName(Output));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene }, locationPathName = Output, target = BuildTarget.Android,
                options = BuildOptions.Development | BuildOptions.DetailedBuildReport,
            });
            bool success = report.summary.result == BuildResult.Succeeded;
            if (success) Debug.Log($"[Kagefumi] Android APK: {Path.GetFullPath(Output)} " +
                $"({report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalTime.TotalSeconds:0} s)");
            else Debug.LogError($"[Kagefumi] Android build {report.summary.result}: {report.summary.totalErrors} errors");
            return success;
        }
    }
}
