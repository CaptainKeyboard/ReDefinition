using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ReDefinition.MotionVectorCheck;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ReDefinition.EditorTools
{
    // Checks the motion vectors ReDefinition computes against Unity's own, in a Windows
    // player: in the editor the frames do not advance between renders, and Unity keeps the
    // previous frame's matrices. Builds the player with MotionVectorProbe in a scene made
    // for it, runs it, and compares, for a moving camera and a moving object, Unity's motion
    // vectors over a sphere with those pass 2 of Hidden/ReDefinition/CloudMotion computes
    // for it (ScaledSpaceMotion in the plugin, through ReDefinitionMotionVector). Fails when
    // the pass misses more than a few of the sphere's pixels or its mean differs. A third
    // case reads the sphere's centre with AsyncGPUReadback at the row MotionVectorAudit in
    // the plugin reads, the viewport y times the height.
    //
    //   Unity.exe -batchmode -quit -projectPath <unity>
    //     -executeMethod ReDefinition.EditorTools.MotionVectorCheck.RunFromCommandLine
    //     -logFile build/motion-vector-check.log
    public static class MotionVectorCheck
    {
        private const string ScenePath = "Assets/ReDefinition/MotionVectorCheck/MotionVectorCheck.unity";
        private const string ShaderPath = "Assets/ReDefinition/Shaders/ReDefinitionCloudMotion.shader";
        private const string AuditShaderPath = "Assets/ReDefinition/Shaders/ReDefinitionMotionAudit.shader";
        private const string PlayerDirectory = "../build/motion-vector-check";
        private const int TimeoutMilliseconds = 120000;

        // The sphere is a mesh, the pass hits a true sphere: its rim differs by a pixel.
        private const float MinimumCoverage = 0.95f;
        private const float RelativeTolerance = 0.03f;
        private const float AbsoluteTolerance = 2e-4f;

        public static void RunFromCommandLine()
        {
            List<string> problems = new List<string>();
            string resultPath = Path.GetFullPath(Path.Combine(PlayerDirectory, "result.txt"));
            try
            {
                string player = BuildPlayer(problems);
                if (player != null) RunPlayer(player, resultPath, problems);
                if (problems.Count == 0) Judge(resultPath, problems);
            }
            finally
            {
                AssetDatabase.DeleteAsset(ScenePath);
            }

            if (problems.Count > 0)
            {
                foreach (string problem in problems) Debug.LogError("ReDefinition motion vector check: " + problem);
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("ReDefinition motion vector check: the computed motion vectors match Unity's.");
            EditorApplication.Exit(0);
        }

        private static string BuildPlayer(List<string> problems)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            MotionVectorProbe probe = new GameObject("MotionVectorProbe").AddComponent<MotionVectorProbe>();
            probe.cloudMotion = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            // Standard writes depth, which Unity's camera motion reads.
            probe.surface = Shader.Find("Standard");
            probe.motionAudit = AssetDatabase.LoadAssetAtPath<Shader>(AuditShaderPath);
            probe.forwardOnly = Shader.Find("Unlit/Color");
            if (probe.cloudMotion == null || probe.surface == null || probe.motionAudit == null)
            {
                problems.Add("the shaders for the probe did not load.");
                return null;
            }
            EditorSceneManager.SaveScene(scene, ScenePath);

            string player = Path.GetFullPath(Path.Combine(PlayerDirectory, "MotionVectorCheck.exe"));
            // Without focus a player stops its frames unless it runs in the background.
            bool runInBackground = PlayerSettings.runInBackground;
            PlayerSettings.runInBackground = true;
            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(new[] { ScenePath }, player,
                    BuildTarget.StandaloneWindows64, BuildOptions.None);
            }
            finally
            {
                PlayerSettings.runInBackground = runInBackground;
            }
            if (report.summary.result != BuildResult.Succeeded)
            {
                problems.Add("the player did not build (" + report.summary.result + ").");
                return null;
            }
            return player;
        }

        private static void RunPlayer(string player, string resultPath, List<string> problems)
        {
            if (File.Exists(resultPath)) File.Delete(resultPath);
            ProcessStartInfo start = new ProcessStartInfo(player,
                "-screen-fullscreen 0 -screen-width 640 -screen-height 360 -logFile \""
                + Path.Combine(Path.GetDirectoryName(player), "player.log") + "\" -result \"" + resultPath + "\"")
            {
                UseShellExecute = false,
            };
            using (Process process = Process.Start(start))
            {
                if (!process.WaitForExit(TimeoutMilliseconds))
                {
                    process.Kill();
                    problems.Add("the player did not finish within " + TimeoutMilliseconds / 1000 + " s.");
                    return;
                }
            }
            if (!File.Exists(resultPath)) problems.Add("the player wrote no result; see its player.log.");
        }

        private static void Judge(string resultPath, List<string> problems)
        {
            string[] lines = File.ReadAllLines(resultPath);
            int cases = 0;
            bool readback = false;
            int audits = 0;
            float depthTarget = -1f, depthWritten = -1f;
            foreach (string line in lines)
            {
                Debug.Log("ReDefinition motion vector check: " + line);
                string[] f = line.Split(' ');
                if (f.Length == 4 && (f[0] == "audit" || f[0] == "auditWrong" || f[0] == "repair"))
                {
                    audits++;
                    long audited = long.Parse(f[1], CultureInfo.InvariantCulture);
                    long bad = long.Parse(f[2], CultureInfo.InvariantCulture);
                    if (audited < 100)
                        problems.Add(f[0] + ": the audit found only " + audited + " of the sphere's pixels.");
                    else if (f[0] == "repair" && bad > 0.02 * audited)
                        problems.Add("repair: " + bad + " of " + audited + " pixels off after pass 1 wrote them;"
                                     + " the vessel's motion vectors would be written wrong.");
                    else if (f[0] == "audit" && bad > 0.02 * audited)
                        problems.Add("audit: " + bad + " of " + audited + " pixels off, where Unity's motion vectors are"
                                     + " right; the vessel check would report errors that are not there.");
                    else if (f[0] == "auditWrong" && bad < 0.9 * audited)
                        problems.Add("auditWrong: only " + bad + " of " + audited + " pixels off, where the motion"
                                     + " vectors were compared with none; the vessel check would miss errors.");
                    continue;
                }
                if (f.Length == 4 && f[0] == "depthTarget")
                {
                    depthTarget = Float(f[1]);
                    continue;
                }
                if (f.Length == 4 && f[0] == "depthWrite")
                {
                    depthWritten = Float(f[1]);
                    continue;
                }
                if (f.Length > 4 && f[0] == "depthSources")
                {
                    // Unity's depth sources in the deferred path lack what the forward
                    // pass draws; the vessel's depth is written for that reason.
                    if (Float(f[1]) != 0f)
                        problems.Add("depthSources: ResolvedDepth holds the forward-only cube (" + f[1] + "); the"
                                     + " vessel's depth pass may no longer be needed.");
                    continue;
                }
                if (f.Length == 3 && f[0] == "readback")
                {
                    readback = true;
                    float atRow = Float(f[1]), mirrored = Float(f[2]);
                    if (atRow < 0.1f || mirrored > 0.05f)
                        problems.Add("readback: at the sphere's viewport row " + atRow.ToString("0.00")
                                     + ", mirrored " + mirrored.ToString("0.00") + "; MotionVectorAudit reads the"
                                     + " wrong row.");
                    continue;
                }
                if (f.Length != 7 || (f[0] != "camera" && f[0] != "object")) continue;
                cases++;
                int pixels = int.Parse(f[1], CultureInfo.InvariantCulture);
                int covered = int.Parse(f[2], CultureInfo.InvariantCulture);
                Vector2 unity = new Vector2(Float(f[3]), Float(f[4]));
                Vector2 ours = new Vector2(Float(f[5]), Float(f[6]));
                if (pixels < 100)
                {
                    problems.Add(f[0] + ": the sphere covers only " + pixels + " pixels.");
                    continue;
                }
                if (unity.magnitude < 1e-3f)
                    problems.Add(f[0] + ": Unity's motion vectors show no motion (" + Show(unity) + ").");
                if (covered < MinimumCoverage * pixels)
                    problems.Add(f[0] + ": the pass covers " + covered + " of the sphere's " + pixels + " pixels.");
                if ((ours - unity).magnitude > AbsoluteTolerance + RelativeTolerance * unity.magnitude)
                    problems.Add(f[0] + ": computed " + Show(ours) + ", Unity " + Show(unity) + ".");
            }
            if (cases != 2) problems.Add("the result has " + cases + " of 2 cases.");
            if (!readback) problems.Add("the result has no readback line.");
            if (audits != 3) problems.Add("the result has " + audits + " of 3 audit lines.");
            if (depthTarget <= 0f)
                problems.Add("depthTarget: the camera's own depth buffer shows no forward-only cube.");
            else if (Mathf.Abs(depthWritten - depthTarget) > 0.01f * depthTarget)
                problems.Add("depthWrite: the depth pass wrote " + depthWritten + " where the camera's depth buffer holds "
                             + depthTarget + ".");
        }

        private static float Float(string text)
        {
            return float.Parse(text, CultureInfo.InvariantCulture);
        }

        private static string Show(Vector2 value)
        {
            return value.ToString("F5");
        }
    }
}
