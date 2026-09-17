using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ReDefinition.EditorTools
{
    // Compiles Include/ReDefinitionIncludeCheck.shader, which calls every function of
    // ReDefinition.cginc, for the player KSP runs, in a bundle of its own that is thrown
    // away. Fails on any shader error or warning Unity logs while it does.
    //
    //   Unity.exe -batchmode -quit -projectPath <unity>
    //     -executeMethod ReDefinition.EditorTools.IncludeCheck.RunFromCommandLine
    //     -logFile build/include-check.log
    public static class IncludeCheck
    {
        private const string CheckShader = "Assets/ReDefinition/Include/ReDefinitionIncludeCheck.shader";

        public static void RunFromCommandLine()
        {
            List<string> problems = new List<string>();
            Application.LogCallback collect = (condition, stackTrace, type) =>
            {
                if (condition.Contains("Shader error") || condition.Contains("Shader warning")
                    || (condition.Contains("IncludeCheck") && type != LogType.Log))
                    problems.Add(condition);
            };
            Application.logMessageReceived += collect;

            AssetDatabase.ImportAsset(CheckShader, ImportAssetOptions.ForceUpdate);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(CheckShader);
            if (shader == null)
                problems.Add("IncludeCheck: " + CheckShader + " did not import as a shader.");
            else if (ShaderUtil.ShaderHasError(shader))
                problems.Add("IncludeCheck: Unity reports errors in " + CheckShader + ".");

            string stagingDir = Path.Combine("Temp", "ReDefinitionIncludeCheck");
            Directory.CreateDirectory(stagingDir);
            AssetBundleBuild build = new AssetBundleBuild
            {
                assetBundleName = "includecheck",
                assetNames = new[] { CheckShader },
            };
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(stagingDir, new[] { build },
                BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64);
            if (manifest == null)
                problems.Add("IncludeCheck: Unity built no bundle from " + CheckShader + ".");

            Application.logMessageReceived -= collect;
            Directory.Delete(stagingDir, true);

            if (problems.Count > 0)
            {
                foreach (string problem in problems)
                    Debug.LogError("ReDefinition include check: " + problem);
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("ReDefinition include check: ReDefinition.cginc compiles, every function called.");
            EditorApplication.Exit(0);
        }
    }
}
