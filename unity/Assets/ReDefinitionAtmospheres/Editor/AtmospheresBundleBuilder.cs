using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.EditorTools
{
    // Builds the shaders of ReDefinition Atmospheres into an AssetBundle of their own, atmospheres.shaders, and puts it into the Atmospheres folder
    // of the game. The atmospheres work without the rest of ReDefinition, so their bundle is not the main one (BundleBuilder), and it holds
    // no compute shader: BundleBuilder refuses a bundle without one.
    //
    // The same Unity version applies as for BundleBuilder: KSP 1.12.5 is Unity 2019.4.18f1, and the player rejects a bundle from
    // another version without a word.
    //
    // The KSP folder is -kspRoot, else the KspRoot environment variable, else the Steam location. The option -output writes the bundle to
    // that file instead of into the game's GameData.
    //
    //   * In the editor: menu "ReDefinition / Build Atmospheres AssetBundle"
    //   * Headless:      Unity.exe -batchmode -quit -projectPath <unity>
    //                      -executeMethod ReDefinition.EditorTools.AtmospheresBundleBuilder.BuildFromCommandLine
    //                      [-kspRoot "<path to KSP>"] [-output "<bundle file>"]
    public static class AtmospheresBundleBuilder
    {
        // The bundle's file name, which the mod's StormShaders loads; the shader sources; the Steam install.
        public const string BundleName = "atmospheres.shaders";
        private const string ShaderFolder = "Assets/ReDefinitionAtmospheres/Shaders";
        private const string DefaultKspRoot =
            @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program";

        [MenuItem("ReDefinition/Build Atmospheres AssetBundle")]
        public static void BuildFromMenu()
        {
            string kspRoot = EditorPrefs.GetString("ReDefinition.KspRoot", DefaultKspRoot);
            string message;
            bool ok = Build(kspRoot, null, out message);
            EditorUtility.DisplayDialog("ReDefinition Atmospheres", ok ? message : "Failed:\n\n" + message, ok ? "Good" : "Damn");
        }

        // Entry point for batch mode; the process exit code tells a script whether it worked.
        public static void BuildFromCommandLine()
        {
            string kspRoot = ArgumentValue("-kspRoot") ?? Environment.GetEnvironmentVariable("KspRoot") ?? DefaultKspRoot;
            string message;
            bool ok = Build(kspRoot, ArgumentValue("-output"), out message);
            if (ok)
            {
                Debug.Log("ReDefinition Atmospheres: " + message);
                EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError("ReDefinition Atmospheres: " + message);
                EditorApplication.Exit(1);
            }
        }

        // output: the bundle file to write; null for the Atmospheres folder of kspRoot's GameData.
        public static bool Build(string kspRoot, string output, out string message)
        {
            var shaders = new List<string>();
            foreach (string path in Directory.GetFiles(ShaderFolder, "*.shader", SearchOption.TopDirectoryOnly))
                shaders.Add(path.Replace('\\', '/'));
            if (shaders.Count == 0)
            {
                message = "There is not a single shader in " + ShaderFolder + ".";
                return false;
            }

            string stagingDir = Path.Combine("Temp", "ReDefinitionAtmospheresBundle");
            Directory.CreateDirectory(stagingDir);

            var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = shaders.ToArray() };

            EnsureGraphicsApis();

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                stagingDir,
                new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
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

            string target;
            if (output != null)
                target = Path.GetFullPath(output);
            else
            {
                if (!Directory.Exists(Path.Combine(kspRoot, "GameData")))
                {
                    message = "No GameData at '" + kspRoot + "'. Pass -kspRoot or -output.";
                    return false;
                }
                target = Path.Combine(Path.Combine(kspRoot, Path.Combine("GameData",
                    Path.Combine("ReDefinition", Path.Combine("Atmospheres", "Shaders")))), BundleName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(built, target, true);
            long kb = new FileInfo(target).Length / 1024;
            message = shaders.Count + " shaders bundled (" + kb + " KB):\n" + target;
            return true;
        }

        // The bundle carries the kernels of Direct3D 11 and Direct3D 12, as the main bundle does: the game may run on either.
        private static void EnsureGraphicsApis()
        {
            GraphicsDeviceType[] wanted = { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 };
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
        }

        private static string ArgumentValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
