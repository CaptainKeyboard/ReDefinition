// Scene of the weather player for the resolution levels of the cloud pass (package T15), linked through the manifests of T01, T05 and T12 (the layer core, the
// temporal rig and EdgeKit):
//   -scene recon-levels -in <T01 inputs>   the resolution levels 1 to 4 through two turns: ghosting, trail and shimmer against the full march of the same pose
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The resolution levels through two turns: ghosting while the camera turns, the trail (pixels more than 4 percent off), and the shimmer of the settled picture.
    [WeatherScene("recon-levels")]
    public sealed class ReconLevelsScene : WeatherSceneBase
    {
        private const int W = 640, H = 360, RefFrames = 3;
        private Texture2D readback;

        private sealed class Scenario
        {
            public string Name;
            public Func<int, float> Yaw = i => 0f;
            public int Frames = 40, LastMove;
        }

        // A level as the game's switches make it: none is level 1 (the full march), lv2 and lv3 the block modes, u4none level 4.
        private sealed class Level
        {
            public string Name;
            public ReconMode Mode = ReconMode.Full;
            public int Block = 4;

            internal ReconSettings Settings()
            {
                return new ReconSettings { Mode = Mode, Block = Block };
            }
        }

        public override IEnumerator Run()
        {
            string input = WeatherPass.Argument("-in");
            TemporalRig rig = input != null && File.Exists(Path.Combine(input, "jobs.json")) ? EdgeKit.Rig(input) : null;
            Sink.Flag("levels_available", rig != null);
            if (rig == null) yield break;
            LayersJobFile file = JsonUtility.FromJson<LayersJobFile>(File.ReadAllText(Path.Combine(input, "jobs.json")));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR);
            rt.Create();
            Cam.targetTexture = rt;
            int pixels = W * H;
            var img = new float[pixels];
            var tmpAlpha = new float[pixels];
            var scenarios = new List<Scenario>
            {
                new Scenario { Name = "turn2", Frames = 50, Yaw = i => Mathf.Clamp(i - 6, 0, 22) * 2f, LastMove = 28 },
                new Scenario { Name = "turn12", Frames = 40, Yaw = i => Mathf.Clamp(i - 6, 0, 10) * 12f, LastMove = 16 },
            };
            var variants = new List<Level>
            {
                new Level { Name = "none" },
                new Level { Name = "lv2", Mode = ReconMode.Block, Block = 2 },
                new Level { Name = "lv3", Mode = ReconMode.Block, Block = 3 },
                new Level { Name = "u4none", Mode = ReconMode.Block, Block = 4 },
            };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (string jobId in new[] { "V-ground", "V-stack-below" })
            {
                LayersJob job = null;
                foreach (LayersJob j in file.jobs) if (j.id == jobId) job = j;
                if (job == null) continue;
                rig.Load(job, input);
                rig.Frame.UseProductCost();
                var full = new ReconSettings { Mode = ReconMode.Full };
                foreach (Scenario sc in scenarios)
                {
                    var refDisplay = new float[sc.Frames][];
                    var refAlpha = new float[sc.Frames][];
                    for (int i = 0; i < sc.Frames; i++)
                    {
                        rig.Pose(Cam, sc.Yaw(i), Vector3.zero);
                        var sum = new float[pixels];
                        var al = new float[pixels];
                        for (int k = 0; k < RefFrames; k++)
                        {
                            rig.Recon.Reset();
                            EdgeKit.Render(Cam, rig, full, 9000 + i * 7 + k);
                            EdgeKit.Read(rig.Recon.Output, ref readback, img, tmpAlpha);
                            for (int p = 0; p < pixels; p++) { sum[p] += img[p] / RefFrames; al[p] += tmpAlpha[p] / RefFrames; }
                        }
                        refDisplay[i] = sum;
                        refAlpha[i] = al;
                        if (i % 6 == 5) yield return null;
                    }
                    foreach (Level v in variants)
                    {
                        if (clock.Elapsed.TotalMinutes > 16.0) { Sink.Flag("levels_completed", false); yield break; }
                        ReconSettings s = v.Settings();
                        rig.Recon.Reset();
                        var moving = new List<double>();
                        var trail = new List<double>();
                        var shimmer = new List<double>();
                        float[] previous = null;
                        var outAlpha = new float[pixels];
                        string key = "levels." + jobId + "." + sc.Name + "." + v.Name;
                        for (int i = 0; i < sc.Frames; i++)
                        {
                            rig.Pose(Cam, sc.Yaw(i), Vector3.zero);
                            EdgeKit.Render(Cam, rig, s, 100 + i);
                            var d = new float[pixels];
                            EdgeKit.Read(rig.Recon.Output, ref readback, d, outAlpha);
                            double err = 0, trailCount = 0, region = 0, flicker = 0;
                            for (int p = 0; p < pixels; p++)
                            {
                                if (refAlpha[i][p] < 0.02f && outAlpha[p] < 0.02f) continue;
                                region += 1;
                                double e = Math.Abs(d[p] - refDisplay[i][p]);
                                err += e;
                                if (e > 0.04) trailCount += 1;
                                if (previous != null) flicker += Math.Abs(d[p] - previous[p]);
                            }
                            if (region > 0)
                            {
                                err /= region;
                                bool isMoving = i > 6 && i <= sc.LastMove;
                                bool settled = i > sc.LastMove + 10;
                                if (isMoving) { moving.Add(err); trail.Add(trailCount / region); }
                                else if (settled && previous != null) shimmer.Add(flicker / region);
                            }
                            previous = d;
                        }
                        Sink.Info(key + ".ghost_move", EdgeKit.Mean(moving));
                        Sink.Info(key + ".trail_px", EdgeKit.Mean(trail));
                        Sink.Info(key + ".shimmer", EdgeKit.Mean(shimmer));
                        yield return null;
                    }
                }
            }
            rig.Dispose();
            Cam.targetTexture = Target;
            rt.Release();
            Sink.Flag("levels_completed", true);
        }
    }
}
