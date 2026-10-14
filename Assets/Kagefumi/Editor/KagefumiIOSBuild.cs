using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kagefumi.EditorTools
{
    /// <summary>
    /// Development iOS build of KAGEFUMI for the Joy-Con 2 tests. Produces the Xcode project in Builds/iOS/Xcode;
    /// Tools/ios-deploy.sh signs it, installs it on the connected iPhone and launches it. The Joy-Con 2 package adds the
    /// Bluetooth usage description to Info.plist. First launch: iOS asks for Bluetooth access; if it was denied —
    /// Settings → Kagefumi.
    /// </summary>
    public static class KagefumiIOSBuild
    {
        const string Scene = "Assets/Kagefumi/Scenes/Kagefumi.unity";
        public const string Output = "Builds/iOS/Xcode";
        const string BundleId = "com.vivoderin.kagefumi";
        const string TeamId = "59P2VDSY5D";

        [MenuItem("Kagefumi/Configure iOS (Joy-Con test)")]
        public static void Configure()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;
            if (string.IsNullOrEmpty(PlayerSettings.iOS.appleDeveloperTeamID)) PlayerSettings.iOS.appleDeveloperTeamID = TeamId;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            var id = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.iOS);
            if (string.IsNullOrEmpty(id) || id.StartsWith("com.Unity-Technologies.") || id.StartsWith("com.UnityTechnologies."))
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, BundleId);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Kagefumi] iOS configured: IL2CPP / device SDK / landscape / {PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.iOS)}, " +
                      $"team {PlayerSettings.iOS.appleDeveloperTeamID}");
        }

        [MenuItem("Kagefumi/Build iOS Xcode project (Joy-Con dev)")]
        public static void Build() => BuildIOS();

        /// <summary>Unity -batchmode -quit -projectPath . -executeMethod Kagefumi.EditorTools.KagefumiIOSBuild.BuildFromCommandLine</summary>
        public static void BuildFromCommandLine()
        {
            if (!BuildIOS()) EditorApplication.Exit(1);
        }

        public static bool BuildIOS()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS, BuildTarget.iOS))
            {
                Debug.LogError("[Kagefumi] iOS Build Support is not installed.");
                return false;
            }
            Configure();
            Directory.CreateDirectory(Output);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene }, locationPathName = Output, target = BuildTarget.iOS,
                options = BuildOptions.Development, // keeps the Joy-Con overlay (Details) and the logs
            });
            bool success = report.summary.result == BuildResult.Succeeded;
            if (success)
                Debug.Log($"[Kagefumi] iOS Xcode project: {Path.GetFullPath(Output)} ({report.summary.totalTime.TotalSeconds:0} s). " +
                          "Install on the iPhone: Tools/ios-deploy.sh");
            else Debug.LogError($"[Kagefumi] iOS build {report.summary.result}: {report.summary.totalErrors} errors");
            return success;
        }
    }
}
