using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.EditorTools
{
    // Builds the compute shaders into an AssetBundle and puts it next to the
    // plugin.
    //
    // A Unity player cannot compile shaders at run time: they come from the editor
    // of exactly the version the game was built with -- KSP 1.12.5 is Unity
    // 2019.4.18f1. The player rejects a bundle from another version without a
    // word.
    //
    // Two ways:
    //   * In the editor: menu "ReDefinition / Build AssetBundle"
    //   * Headless:      Unity.exe -batchmode -quit -projectPath <unity>
    //                      -executeMethod ReDefinition.EditorTools.BundleBuilder.BuildFromCommandLine
    //                      [-kspRoot "<path to KSP>"]
    public static class BundleBuilder
    {
        public const string BundleName = "redefinition.shaders";
        private const string ShaderFolder = "Assets/ReDefinition/Shaders";
        private const string DefaultKspRoot =
            @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program";

        [MenuItem("ReDefinition/Build AssetBundle")]
        public static void BuildFromMenu()
        {
            string kspRoot = EditorPrefs.GetString("ReDefinition.KspRoot", DefaultKspRoot);
            string message;
            bool ok = Build(kspRoot, out message);

            if (ok)
                EditorUtility.DisplayDialog("ReDefinition", message, "Good");
            else
                EditorUtility.DisplayDialog("ReDefinition", "Failed:\n\n" + message, "Damn");
        }

        [MenuItem("ReDefinition/Set KSP path ...")]
        public static void ChooseKspRoot()
        {
            string current = EditorPrefs.GetString("ReDefinition.KspRoot", DefaultKspRoot);
            string chosen = EditorUtility.OpenFolderPanel("Choose the KSP root folder", current, "");
            if (string.IsNullOrEmpty(chosen)) return;

            if (!Directory.Exists(Path.Combine(chosen, "GameData")))
            {
                EditorUtility.DisplayDialog("ReDefinition",
                    "There is no GameData folder there. That is not KSP's root folder.", "Retry");
                return;
            }

            EditorPrefs.SetString("ReDefinition.KspRoot", chosen);
            Debug.Log("ReDefinition: KSP path set to " + chosen);
        }

        // Entry point for batch mode. Sets the process exit code so a script can
        // tell whether it worked.
        public static void BuildFromCommandLine()
        {
            string kspRoot = ArgumentValue("-kspRoot") ?? DefaultKspRoot;
            string message;
            bool ok = Build(kspRoot, out message);

            if (ok)
            {
                Debug.Log("ReDefinition: " + message);
                EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError("ReDefinition: " + message);
                EditorApplication.Exit(1);
            }
        }

        public static bool Build(string kspRoot, out string message)
        {
            List<string> shaders = new List<string>();
            foreach (string path in Directory.GetFiles(ShaderFolder, "*.compute", SearchOption.TopDirectoryOnly))
                shaders.Add(path.Replace('\\', '/'));
            int computeShaders = shaders.Count;
            // The masks' own shader, drawn by command buffers on the camera.
            foreach (string path in Directory.GetFiles(ShaderFolder, "*.shader", SearchOption.TopDirectoryOnly))
                shaders.Add(path.Replace('\\', '/'));

            if (computeShaders == 0)
            {
                message = "There is not a single compute shader in " + ShaderFolder + ".";
                return false;
            }

            // A staging folder so Unity's companion files (one manifest per
            // build) do not end up in GameData.
            string stagingDir = Path.Combine("Temp", "ReDefinitionBundle");
            Directory.CreateDirectory(stagingDir);

            AssetBundleBuild build = new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = shaders.ToArray(),
            };

            EnsureGraphicsApis();

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                stagingDir,
                new[] { build },
                // ForceRebuild because the incremental build does not treat a
                // change of graphics APIs as a reason to recompile, and then
                // silently keeps a bundle without D3D12 kernels.
                BuildAssetBundleOptions.ChunkBasedCompression
                    | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                message = "Unity produced no bundle. The shader errors are further up in the log.";
                return false;
            }

            string built = Path.Combine(stagingDir, BundleName);
            if (!File.Exists(built))
            {
                message = "Expected file missing: " + built;
                return false;
            }

            string targetDir = Path.Combine(kspRoot, Path.Combine("GameData",
                Path.Combine("ReDefinition", "Shaders")));

            if (!Directory.Exists(Path.Combine(kspRoot, "GameData")))
            {
                message = "No GameData at '" + kspRoot
                          + "'. Correct the path via 'ReDefinition / Set KSP path'"
                          + " or pass -kspRoot.";
                return false;
            }

            Directory.CreateDirectory(targetDir);
            string target = Path.Combine(targetDir, BundleName);
            File.Copy(built, target, true);

            long kb = new FileInfo(target).Length / 1024;
            message = computeShaders + " compute shaders and " + (shaders.Count - computeShaders)
                      + " shaders bundled (" + kb + " KB):\n" + target;
            return true;
        }

        // A compute shader in an AssetBundle carries compiled kernels only for
        // the graphics APIs configured for the build target. Build with D3D11
        // alone and the bundle has no D3D12 kernels at all -- the player then
        // loads the shader, reports zero kernels, and every Dispatch fails with
        // "Invalid kernelIndex (0) passed, must be non-negative less than 0" --
        // as ParallaxContinued's do where KSP is started with -force-d3d12. The
        // bundle carries D3D11 and D3D12 kernels; the D3D12 ones add 51 KB to a
        // 128 KB bundle.
        private static void EnsureGraphicsApis()
        {
            GraphicsDeviceType[] wanted =
            {
                GraphicsDeviceType.Direct3D11,
                GraphicsDeviceType.Direct3D12,
            };

            GraphicsDeviceType[] current = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            if (current != null && current.Length == wanted.Length)
            {
                bool same = true;
                for (int i = 0; i < wanted.Length; i++)
                    if (current[i] != wanted[i]) { same = false; break; }
                if (same) return;
            }

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, wanted);
            Debug.Log("ReDefinition: graphics APIs for the bundle set to "
                      + string.Join(" + ", System.Array.ConvertAll(wanted, a => a.ToString())) + ".");
        }

        private static string ArgumentValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
