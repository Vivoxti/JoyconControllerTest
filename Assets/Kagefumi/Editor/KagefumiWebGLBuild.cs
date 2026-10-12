using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kagefumi.EditorTools
{
    /// <summary>
    /// Development WebGL build of KAGEFUMI for the Joy-Con 2 Web Bluetooth tests (Chrome/Edge).
    /// Serve it from localhost (Web Bluetooth needs HTTPS or localhost), e.g.
    /// <c>python -m http.server 8000 -d Builds/WebGL</c> → http://localhost:8000
    /// </summary>
    public static class KagefumiWebGLBuild
    {
        const string Scene = "Assets/Kagefumi/Scenes/Kagefumi.unity";
        const string Output = "Builds/WebGL";
        /// <summary>Windows rate helper (Native/windows/build.ps1), served next to index.html for the in-game download.</summary>
        const string Booster = "Native/windows/build/Joycon2Boost.exe";

        [MenuItem("Kagefumi/Build WebGL (dev, localhost)")]
        public static void Build() => BuildWebGL();

        /// <summary>Unity.exe -batchmode -quit -projectPath . -executeMethod Kagefumi.EditorTools.KagefumiWebGLBuild.BuildFromCommandLine</summary>
        public static void BuildFromCommandLine()
        {
            if (!BuildWebGL()) EditorApplication.Exit(1);
        }

        static bool BuildWebGL()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
            {
                const string message = "Web Build Support is not installed for this Unity version.\n" +
                                       "Unity Hub → Installs → 6000.3.18f1 → Add modules → Web Build Support.";
                if (Application.isBatchMode) Debug.LogError("[Kagefumi] " + message);
                else EditorUtility.DisplayDialog("KAGEFUMI WebGL", message, "OK");
                return false;
            }

            // The loader decompresses by itself, so any static server works (no Content-Encoding headers needed).
            PlayerSettings.WebGL.decompressionFallback = true;

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = Output,
                target = BuildTarget.WebGL,
                options = BuildOptions.Development, // keeps the Joy-Con overlay (F9) on
            });

            var summary = report.summary;
            bool ok = summary.result == BuildResult.Succeeded;
            if (ok)
            {
                if (System.IO.File.Exists(Booster))
                    System.IO.File.Copy(Booster, System.IO.Path.Combine(Output, "Joycon2Boost.exe"), true);
                else
                    Debug.LogWarning($"[Kagefumi] {Booster} not found (Native/windows/build.ps1): the in-game helper download will 404.");
            }
            if (ok)
                Debug.Log($"[Kagefumi] WebGL build: {System.IO.Path.GetFullPath(Output)} ({summary.totalSize / (1024 * 1024)} MB, " +
                          $"{summary.totalTime.TotalSeconds:0} s). Serve: python -m http.server 8000 -d {Output}");
            else
                Debug.LogError($"[Kagefumi] WebGL build {summary.result}: {summary.totalErrors} errors");
            return ok;
        }
    }
}
