using System.IO;
using UnityEditor;
using UnityEngine;

namespace Joycon2.Editor
{
    /// <summary>Edit → Project Settings → Joy-Con 2.</summary>
    static class Joycon2SettingsProvider
    {
        public const string SettingsPath = "Project/Joy-Con 2";
        const string DefaultAssetPath = "Assets/Settings/Resources/" + Joycon2Settings.ResourceName + ".asset";

        /// <summary>The settings asset of this project, if one exists (in any Resources folder).</summary>
        public static Joycon2Settings FindAsset()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(Joycon2Settings)))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == Joycon2Settings.ResourceName && path.Contains("/Resources/"))
                    return AssetDatabase.LoadAssetAtPath<Joycon2Settings>(path);
            }
            return null;
        }

        /// <summary>Settings as builds see them: the asset, or the defaults.</summary>
        public static Joycon2Settings Current => FindAsset() ?? ScriptableObject.CreateInstance<Joycon2Settings>();

        [MenuItem("Tools/Joy-Con 2/Project Settings", priority = 0)]
        static void Open() => SettingsService.OpenProjectSettings(SettingsPath);

        [SettingsProvider]
        static SettingsProvider Create()
        {
            UnityEditor.Editor editor = null;
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Joy-Con 2",
                keywords = new[] { "Joy-Con", "Joycon", "Switch", "Bluetooth", "BLE", "gamepad", "gyro" },
                guiHandler = _ =>
                {
                    var asset = FindAsset();
                    if (asset == null)
                    {
                        EditorGUILayout.HelpBox("No settings asset: the defaults apply. Create one to change them " +
                                                $"(it goes to {DefaultAssetPath}; any Resources folder works).", MessageType.Info);
                        if (GUILayout.Button("Create Joy-Con 2 settings", GUILayout.Width(220))) CreateAsset();
                        return;
                    }
                    UnityEditor.Editor.CreateCachedEditor(asset, null, ref editor);
                    EditorGUILayout.LabelField("Asset", AssetDatabase.GetAssetPath(asset), EditorStyles.miniLabel);
                    EditorGUILayout.Space();
                    editor.OnInspectorGUI();
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(
                        "Joy-Con 2 appear as Joycon2Gamepad (a Gamepad): <Gamepad>/… bindings work unchanged. Players hold SYNC " +
                        "to connect. WebGL: the browser lists them only after a click — keep the web panel on or call " +
                        "Joycon2Manager.RequestController() from your own button.", MessageType.None);
                },
            };
        }

        static void CreateAsset()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultAssetPath));
            var asset = ScriptableObject.CreateInstance<Joycon2Settings>();
            AssetDatabase.CreateAsset(asset, DefaultAssetPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
        }
    }
}
