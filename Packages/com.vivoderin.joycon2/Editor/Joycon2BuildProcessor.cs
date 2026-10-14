using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace Joycon2.Editor
{
    /// <summary>
    /// Everything a player build needs for Joy-Con 2, so a game only adds the package:
    /// <list type="bullet">
    /// <item>iOS: NSBluetoothAlwaysUsageDescription in Info.plist (iOS kills the app on its first Bluetooth call without it,
    /// and Player Settings have no field for it); development builds also expose the F10 recordings in the Files app.</item>
    /// <item>macOS: Player Settings → Bluetooth Usage Description, if empty (same reason).</item>
    /// <item>WebGL: the browser report-rate helpers next to index.html (<see cref="Joycon2Settings.CopyRateHelpersToWebBuild"/>).</item>
    /// <item>IL2CPP: keeps the runtime assembly — the Input System finds the layout and its state struct by reflection.</item>
    /// </list>
    /// </summary>
    sealed class Joycon2BuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport, IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.StandaloneOSX) EnsureMacBluetoothUsageDescription();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            var settings = Joycon2SettingsProvider.Current;
            string output = report.summary.outputPath;
            switch (report.summary.platform)
            {
                case BuildTarget.iOS:
                    bool development = (report.summary.options & BuildOptions.Development) != 0;
                    AddIOSInfoPlistKeys(Path.Combine(output, "Info.plist"), settings.BluetoothUsageDescription, development);
                    break;
                case BuildTarget.WebGL:
                    if (settings.CopyRateHelpersToWebBuild) CopyRateHelpers(output);
                    break;
            }
        }

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            string path = Path.Combine(PackageRoot, "Runtime", "link.xml");
            if (File.Exists(path)) return Path.GetFullPath(path);
            Debug.LogWarning($"[Joycon2] {path} not found: IL2CPP may strip the Joycon2Gamepad layout.");
            return null;
        }

        /// <summary>The package folder; also works if the package was copied into Assets as a plain folder.</summary>
        static string PackageRoot
        {
            get
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Joycon2BuildProcessor).Assembly);
                if (package != null) return package.resolvedPath;
                foreach (string guid in AssetDatabase.FindAssets(nameof(Joycon2BuildProcessor) + " t:MonoScript"))
                {
                    string editorFolder = Path.GetDirectoryName(AssetDatabase.GUIDToAssetPath(guid)); // <package>/Editor
                    return Path.GetFullPath(Path.GetDirectoryName(editorFolder));
                }
                return Path.GetFullPath("Packages/com.vivoderin.joycon2");
            }
        }

        static string UsageDescription
        {
            get
            {
                string text = Joycon2SettingsProvider.Current.BluetoothUsageDescription;
                return string.IsNullOrWhiteSpace(text) ? "Joy-Con 2 controllers connect over Bluetooth." : text;
            }
        }

        /// <summary>No public API for this Player Settings field.</summary>
        static void EnsureMacBluetoothUsageDescription()
        {
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var description = settings.FindProperty("bluetoothUsageDescription");
            if (description == null)
            {
                Debug.LogWarning("[Joycon2] Player Settings have no bluetoothUsageDescription in this Unity version: " +
                                 "make sure the app's Info.plist has NSBluetoothAlwaysUsageDescription.");
                return;
            }
            if (!string.IsNullOrEmpty(description.stringValue)) return;
            description.stringValue = UsageDescription;
            settings.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log($"[Joycon2] Player Settings → macOS → Bluetooth Usage Description was empty, set to \"{UsageDescription}\".");
        }

        /// <summary>Plain XML: no dependency on the iOS module's Xcode API.</summary>
        internal static void AddIOSInfoPlistKeys(string path, string usageDescription, bool development)
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"[Joycon2] {path} not found: add NSBluetoothAlwaysUsageDescription to Info.plist by hand.");
                return;
            }
            if (string.IsNullOrWhiteSpace(usageDescription)) usageDescription = UsageDescription;
            XDocument doc;
            // Never fetch the DTD; the DOCTYPE is dropped on save, which Xcode accepts.
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                doc = XDocument.Load(reader);
            var dict = doc.Root?.Element("dict");
            if (dict == null)
            {
                Debug.LogError($"[Joycon2] {path} is not a plist dictionary: add NSBluetoothAlwaysUsageDescription by hand.");
                return;
            }
            void Set(string key, XElement value, bool overwrite)
            {
                var existing = dict.Elements("key").FirstOrDefault(k => k.Value == key);
                if (existing == null) dict.Add(new XElement("key", key), value);
                else if (overwrite)
                {
                    (existing.NextNode as XElement)?.Remove();
                    existing.AddAfterSelf(value);
                }
            }
            // A description the game already put there wins.
            Set("NSBluetoothAlwaysUsageDescription", new XElement("string", usageDescription), overwrite: false);
            if (development)
            {
                // Recordings (F10 / Record) go to Documents: visible in the Files app.
                Set("UIFileSharingEnabled", new XElement("true"), overwrite: true);
                Set("LSSupportsOpeningDocumentsInPlace", new XElement("true"), overwrite: true);
            }
            using (var writer = XmlWriter.Create(path, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true }))
                doc.Save(writer);
        }

        static void CopyRateHelpers(string output)
        {
            string source = Path.Combine(PackageRoot, "WebHelpers~");
            var missing = new System.Collections.Generic.List<string>();
            foreach (string file in new[] { Joycon2RateBooster.WindowsHelperFile, Joycon2RateBooster.MacHelperFile, Joycon2RateBooster.AndroidHelperFile })
            {
                string from = Path.Combine(source, file);
                if (File.Exists(from)) File.Copy(from, Path.Combine(output, file), true);
                else missing.Add(file);
            }
            if (missing.Count > 0)
                Debug.LogWarning($"[Joycon2] Web rate helpers not built yet, their in-game download will 404: {string.Join(", ", missing)}. " +
                                 "Build them with the scripts in the package's Native~ folder (README → Rate helpers), or turn " +
                                 "Project Settings → Joy-Con 2 → Copy Rate Helpers To Web Build off.");
        }
    }
}
