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
        // The scene made for the player and deleted after the run, the shaders the probe is given, where the
        // player is built, and how long it may run.
        private const string ScenePath = "Assets/ReDefinition/MotionVectorCheck/MotionVectorCheck.unity";
        private const string ShaderPath = "Assets/ReDefinition/Shaders/ReDefinitionCloudMotion.shader";
        private const string AuditShaderPath = "Assets/ReDefinition/Shaders/ReDefinitionMotionAudit.shader";
        private const string ShadowShaderPath = "Assets/ReDefinition/Shaders/ReDefinitionVesselShadow.shader";
        private const string PlayerDirectory = "../build/motion-vector-check";
        private const int TimeoutMilliseconds = 120000;

        // The sphere is a mesh, the pass hits a true sphere: its rim differs by a pixel.
        private const float MinimumCoverage = 0.95f;
        private const float RelativeTolerance = 0.03f;
        private const float AbsoluteTolerance = 2e-4f;

        // Entry point for batch mode: builds and runs the player, judges its result file, exits 1 on any problem.
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

        // Saves a scene holding only the probe with its shaders and builds the player from it; null when it fails.
        private static string BuildPlayer(List<string> problems)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            MotionVectorProbe probe = new GameObject("MotionVectorProbe").AddComponent<MotionVectorProbe>();
            probe.cloudMotion = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            // Standard writes depth, which Unity's camera motion reads.
            probe.surface = Shader.Find("Standard");
            probe.motionAudit = AssetDatabase.LoadAssetAtPath<Shader>(AuditShaderPath);
            probe.forwardOnly = Shader.Find("Unlit/Color");
            probe.vesselShadow = AssetDatabase.LoadAssetAtPath<Shader>(ShadowShaderPath);
            if (probe.cloudMotion == null || probe.surface == null || probe.motionAudit == null || probe.vesselShadow == null)
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

        // Runs the player windowed at 640 x 360 and waits for it to write its result file, killing it after the timeout.
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

        // Logs each line of the result and checks it against the limits for its case; also that every case is there.
        private static void Judge(string resultPath, List<string> problems)
        {
            string[] lines = File.ReadAllLines(resultPath);
            int cases = 0;
            bool readback = false;
            int audits = 0;
            float depthTarget = -1f, depthWritten = -1f;
            int shadowCases = 0;
            bool farCase = false;
            bool repairCase = false;
            int unwrittenCases = 0;
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
                if (f.Length == 8 && (f[0] == "shadowChase" || f[0] == "shadowParked" || f[0] == "shadowKrakensbane"))
                {
                    shadowCases++;
                    JudgeShadow(f, problems);
                    continue;
                }
                if (f.Length == 10 && f[0] == "far")
                {
                    farCase = true;
                    JudgeFar(f, problems);
                    continue;
                }
                if (f.Length == 5 && f[0] == "unwritten")
                {
                    unwrittenCases++;
                    if (f[3] != f[4])
                        problems.Add("unwritten: the mark at the end of the frame reached " + f[3] + " of " + f[4]
                                     + " pixels of the motion vector buffer in the " + f[1] + " case.");
                    if (f[2] != "0")
                        problems.Add("unwritten: " + f[2] + " pixels of Unity's motion vectors in the " + f[1]
                                     + " case still hold the value they were cleared to a frame before.");
                    continue;
                }
                if (f.Length == 5 && f[0] == "repair")
                {
                    repairCase = true;
                    int repaired = int.Parse(f[1], CultureInfo.InvariantCulture);
                    if (repaired < 100)
                        problems.Add("repair: the sphere covers only " + repaired + " pixels.");
                    else if (f[2] != f[3])
                        problems.Add("repair: the copy holds the cleared value on " + f[2] + " of " + f[3] + " pixels after they were cleared; the case shows nothing.");
                    else if (Float(f[4]) > 0.01f)
                        problems.Add("repair: where Unity wrote no motion vectors, pass 3 lies " + f[4]
                                     + " px from the camera's move on average.");
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
            if (shadowCases != 3) problems.Add("the result has " + shadowCases + " of 3 shadow cases.");
            if (!farCase) problems.Add("the result has no far case.");
            if (unwrittenCases != 2) problems.Add("the result has " + unwrittenCases + " of 2 lines on unwritten pixels.");
            if (!repairCase) problems.Add("the result has no case for motion vectors Unity did not write.");
            if (depthTarget <= 0f)
                problems.Add("depthTarget: the camera's own depth buffer shows no forward-only cube.");
            else if (Mathf.Abs(depthWritten - depthTarget) > 0.01f * depthTarget)
                problems.Add("depthWrite: the depth pass wrote " + depthWritten + " where the camera's depth buffer holds "
                             + depthTarget + ".");
        }

        // SceneMotion: the ground 150 km away, seen from 150 km from the origin after a move of
        // 10 m sideways and 6 m up, moves by its parallax, the same over neighbouring pixels, and
        // on average as Unity's vectors in this player show it, `y` up. Unity's own spread is
        // logged for comparison.
        private static void JudgeFar(string[] f, List<string> problems)
        {
            int pixels = int.Parse(f[1], CultureInfo.InvariantCulture);
            float unityJump = Float(f[2]), oursJump = Float(f[3]), oursError = Float(f[4]), expected = Float(f[5]);
            Vector2 unity = new Vector2(Float(f[6]), Float(f[7]));
            Vector2 ours = new Vector2(Float(f[8]), Float(f[9]));
            Debug.Log("ReDefinition motion vector check: far: Unity's vectors differ by " + unityJump
                      + " px between neighbouring pixels, pass 2's by " + oursJump + " px; parallax " + expected + " px.");
            if (pixels < 500)
            {
                problems.Add("far: the ground covers only " + pixels + " pixels.");
                return;
            }
            if (Mathf.Abs(expected) < 1e-3f)
                problems.Add("far: the move gives no parallax (" + expected + " px).");
            if (oursError > 0.01f)
                problems.Add("far: pass 2 lies " + oursError + " px from the parallax on average.");
            if (oursJump > 0.01f)
                problems.Add("far: pass 2's vectors differ by " + oursJump + " px between neighbouring pixels.");
            if (Mathf.Abs(unity.y) < 1e-3f)
                problems.Add("far: Unity's motion vectors show no vertical motion (" + Show(unity) + ").");
            if ((ours - unity).magnitude > RelativeTolerance * unity.magnitude)
                problems.Add("far: computed " + Show(ours) + ", Unity " + Show(unity) + ".");
        }

        // VesselShadowLayer: the block's shadow found and nothing else; left in place where the
        // camera follows the block, to the motion vectors where it stands; the shadowed ground
        // brought to its brightness in the same frame without the shadow, the lit ground
        // unchanged.
        private static void JudgeShadow(string[] f, List<string> problems)
        {
            int inShadow = int.Parse(f[1], CultureInfo.InvariantCulture);
            int found = int.Parse(f[2], CultureInfo.InvariantCulture);
            int outside = int.Parse(f[3], CultureInfo.InvariantCulture);
            float choice = Float(f[4]), before = Float(f[5]), after = Float(f[6]), litChange = Float(f[7]);
            if (inShadow < 200)
            {
                problems.Add(f[0] + ": the block's shadow covers only " + inShadow + " ground pixels.");
                return;
            }
            if (found < 0.9f * inShadow)
                problems.Add(f[0] + ": the pass found " + found + " of " + inShadow + " shadowed pixels.");
            if (outside > 0.02f * inShadow)
                problems.Add(f[0] + ": the pass took " + outside + " lit pixels for the block's shadow.");
            if (f[0] == "shadowChase" || f[0] == "shadowKrakensbane")
            {
                if (choice < 0.8f)
                    problems.Add(f[0] + ": mean choice " + choice + "; a shadow standing on the screen must stay in place.");
                if (before > 0.85f)
                    problems.Add(f[0] + ": the shadowed ground is " + before + " of its brightness without the"
                                 + " shadow; there is hardly a shadow to take out.");
                if (after < 0.93f || after > 1.07f)
                    problems.Add(f[0] + ": after the composition the shadowed ground is " + after + " of its"
                                 + " brightness without the shadow; it must come out near 1.");
                if (litChange > 0.01f)
                    problems.Add(f[0] + ": the lit ground changed by " + litChange + " on average.");
            }
            else if (choice > 0.2f)
                problems.Add("shadowParked: mean choice " + choice + "; a shadow moving with the ground must be left to it.");
        }

        // Parses a number of the result file, written in the invariant culture.
        private static float Float(string text)
        {
            return float.Parse(text, CultureInfo.InvariantCulture);
        }

        // A motion vector with five decimals, for the problem messages.
        private static string Show(Vector2 value)
        {
            return value.ToString("F5");
        }
    }
}
