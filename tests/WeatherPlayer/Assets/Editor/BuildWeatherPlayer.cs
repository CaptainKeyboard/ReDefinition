using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // Builds the weather test player, which tools/build_weather_player.py then runs without the game. It is set up as the raytracing
    // player is (tests/RaytracingPlayer/Assets/Editor/BuildPlayer.cs) and as KSP's player is: Direct3D 11, native graphics jobs
    // and gamma colour space, one scene that holds the runner.
    //
    //   Unity.exe -batchmode -quit -projectPath tests/WeatherPlayer
    //     -executeMethod ReDefinition.WeatherPlayer.BuildWeatherPlayer.Build -output <exe>
    public static class BuildWeatherPlayer
    {
        public static void Build()
        {
            string output = Argument("-output") ?? Path.GetFullPath("../../build/WeatherPlayer/WeatherPlayer.exe");
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
                PlayerSettings.productName = "WeatherPlayer";

                // The material the runner loads, so that the player carries the standard shader; the shaders in Resources are
                // carried as they are.
                Directory.CreateDirectory("Assets/Linked/Resources");
                AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), "Assets/Linked/Resources/WeatherStandard.mat");
                AssetDatabase.SaveAssets();

                Directory.CreateDirectory("Assets/Scenes");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Runner").AddComponent<WeatherRunner>();
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
                    Debug.LogError("WeatherPlayer: build " + report.summary.result);
                    EditorApplication.Exit(1);
                    return;
                }
                Debug.Log("WeatherPlayer: built " + output);
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("WeatherPlayer: " + e);
                EditorApplication.Exit(1);
            }
        }

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
