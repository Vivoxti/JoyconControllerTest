using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kagefumi.EditorTools
{
    /// <summary>
    /// Development iOS build of KAGEFUMI for the Joy-Con 2 CoreBluetooth tests (Plugins/iOS/libJoycon2Ble.a).
    /// Produces the Xcode project in Builds/iOS/Xcode; Native/ios/deploy.sh signs it, installs it on the connected
    /// iPhone and launches it. First launch: iOS asks for Bluetooth access; if it was denied — Settings → Kagefumi.
    /// </summary>
    public static class KagefumiIOSBuild
    {
        const string Scene = "Assets/Kagefumi/Scenes/Kagefumi.unity";
        public const string Output = "Builds/iOS/Xcode";
        const string Plugin = "Assets/Joycon2/Plugins/iOS/libJoycon2Ble.a";
        const string BundleId = "com.vivoderin.kagefumi";
        const string TeamId = "59P2VDSY5D";
        const string BluetoothUsage = "Joy-Con 2 controllers connect over Bluetooth.";

        [MenuItem("Kagefumi/Configure iOS (Joy-Con test)")]
        public static void Configure()
        {
            if (!File.Exists(Plugin)) throw new FileNotFoundException("Run Native/ios/build.sh before the iOS build", Plugin);
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
            {
                AddInfoPlistKeys(Path.Combine(Output, "Info.plist"));
                Debug.Log($"[Kagefumi] iOS Xcode project: {Path.GetFullPath(Output)} ({report.summary.totalTime.TotalSeconds:0} s). " +
                          "Install on the iPhone: Native/ios/deploy.sh");
            }
            else Debug.LogError($"[Kagefumi] iOS build {report.summary.result}: {report.summary.totalErrors} errors");
            return success;
        }

        /// <summary>
        /// Without NSBluetoothAlwaysUsageDescription iOS kills the app on its first CoreBluetooth call (the plugin refuses
        /// to start instead), and iOS Player Settings have no field for it. File sharing exposes the F10/Record dumps
        /// (Documents) in the Files app. Plain XML: no dependency on the iOS module's Xcode API.
        /// </summary>
        static void AddInfoPlistKeys(string path)
        {
            XDocument doc;
            // Skip the plist DOCTYPE (never fetch the DTD); Xcode reads the file fine without it.
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                doc = XDocument.Load(reader);
            var dict = doc.Root.Element("dict");
            void Set(string key, XElement value)
            {
                var existing = dict.Elements("key").FirstOrDefault(k => k.Value == key);
                if (existing != null)
                {
                    (existing.NextNode as XElement)?.Remove();
                    existing.AddAfterSelf(value);
                }
                else dict.Add(new XElement("key", key), value);
            }
            Set("NSBluetoothAlwaysUsageDescription", new XElement("string", BluetoothUsage));
            Set("UIFileSharingEnabled", new XElement("true"));
            Set("LSSupportsOpeningDocumentsInPlace", new XElement("true"));
            using (var writer = XmlWriter.Create(path, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true }))
                doc.Save(writer);
        }
    }
}
