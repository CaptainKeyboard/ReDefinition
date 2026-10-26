using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using ReDefinition.Core;

namespace ReDefinition.RaytracingPlayer
{
    // Builds the raytracing test player (docs/development/raytracing.md, "Testing
    // without the game"): a Windows player set up as KSP's is, Direct3D 11 with
    // native graphics jobs and gamma colour space, with one scene that holds the
    // runner. tools/build_rt_player.py copies ReDefinition's raytracing sources and
    // shaders in first and calls this in batch mode:
    //
    //   Unity.exe -batchmode -quit -projectPath tests/RaytracingPlayer
    //     -executeMethod ReDefinition.RaytracingPlayer.BuildPlayer.Build -output <exe>
    public static class BuildPlayer
    {
        // Sets the player up, makes the materials and the runner's scene, builds to -output and exits Unity with 0
        // on success, 1 on failure.
        public static void Build()
        {
            string output = Argument("-output") ?? Path.GetFullPath("../../build/RaytracingPlayer/RaytracingPlayer.exe");
            try
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
                PlayerSettings.graphicsJobs = true;
                PlayerSettings.graphicsJobMode = GraphicsJobMode.Native;
                PlayerSettings.colorSpace = ColorSpace.Gamma;
                PlayerSettings.defaultIsNativeResolution = false;
                PlayerSettings.defaultScreenWidth = 1280;
                PlayerSettings.defaultScreenHeight = 720;
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                PlayerSettings.runInBackground = true;
                PlayerSettings.displayResolutionDialog = ResolutionDialogSetting.Disabled;
                PlayerSettings.productName = "RaytracingPlayer";

                // The materials the runner loads, so that the player carries the standard shaders.
                Directory.CreateDirectory("Assets/Linked/Resources");
                AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), "Assets/Linked/Resources/RtStandard.mat");
                AssetDatabase.CreateAsset(new Material(Shader.Find("Skybox/Procedural")), "Assets/Linked/Resources/RtSky.mat");
                AssetDatabase.SaveAssets();

                Directory.CreateDirectory("Assets/Scenes");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Runner").AddComponent<RtPlayerRunner>();
                const string scenePath = "Assets/Scenes/Main.unity";
                EditorSceneManager.SaveScene(scene, scenePath);

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = output,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                };
                var report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                {
                    Debug.LogError("RaytracingPlayer: build " + report.summary.result);
                    EditorApplication.Exit(1);
                    return;
                }
                Debug.Log("RaytracingPlayer: built " + output);
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("RaytracingPlayer: " + e);
                EditorApplication.Exit(1);
            }
        }

        // The value after the named command-line argument, or null without one.
        private static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
