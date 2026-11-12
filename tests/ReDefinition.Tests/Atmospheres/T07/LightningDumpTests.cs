using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Audio;
using UnityEngine;

namespace ReDefinition.Tests
{
    // Writes the numbers behind the aid pictures of the lightning (package T07) to build/weather-series/T07-geometry/: bolts (their strands),
    // an hour of the event timeline of a storm, the envelope of a flash and the thunder clips. tools/weather_metrics/lightning.py draws them.
    [TestClass]
    public class LightningDumpTests
    {
        private static string Folder()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string dir = Path.Combine(root, "build", "weather-series", "T07-geometry");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string F(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private sealed class Blob : ICellSource
        {
            public bool Sample(double latRad, double lonRad, double ut, out CellSample s)
            {
                s = new CellSample { Strength = 0.9f, Level01 = 0.9f, TopM = 10000f, Coverage = 0.95f };
                return latRad * latRad + lonRad * lonRad < 0.03 * 0.03;
            }
        }

        [TestMethod]
        public void TheAidPicturesNumbersAreWritten()
        {
            string dir = Folder();
            var sb = new StringBuilder();
            sb.Append("{\"bolts\":[");
            var bp = new BoltParams();
            uint[] seeds = { 11, 23, 37, 51 };
            for (int b = 0; b < seeds.Length; b++)
            {
                var strands = BoltGeometry.Build(seeds[b], 5200f, new Vector2(350f, -250f), bp);
                if (b > 0) sb.Append(',');
                sb.Append("{\"seed\":" + seeds[b] + ",\"strands\":[");
                for (int s = 0; s < strands.Count; s++)
                {
                    if (s > 0) sb.Append(',');
                    sb.Append("{\"depth\":" + strands[s].Depth + ",\"width\":" + F(strands[s].WidthM) + ",\"brightness\":" + F(strands[s].Brightness) + ",\"points\":[");
                    for (int i = 0; i < strands[s].Points.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Vector3 p = strands[s].Points[i];
                        sb.Append("[" + F(p.x) + "," + F(p.y) + "," + F(p.z) + "]");
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
            }
            sb.Append("],\"timeline\":[");
            var p2 = new LightningParams();
            var scanner = new LightningScanner(p2);
            scanner.Begin(new Blob(), 600000.0, 0.0, 0.0, 30000.0, 0.0);
            while (!scanner.Step(8)) { }
            var events = new List<FlashEvent>();
            for (long slot = 0; slot < 600; slot++) LightningSchedule.EventsInSlot(p2, 4u, slot, scanner.Result, events);
            events.Sort((x, y) => x.Ut.CompareTo(y.Ut));
            var limiter = new FlashLimiter(3);
            bool first = true;
            foreach (var ev in events)
            {
                var e = ev;
                bool shown = limiter.TryAccept(ref e);
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"ut\":" + F(e.Ut) + ",\"bumps\":" + e.Bumps + ",\"ground\":" + (e.Kind == FlashKind.Ground ? 1 : 0) + ",\"shown\":" + (shown ? 1 : 0)
                    + ",\"north\":" + F(e.LatDeg * Math.PI / 180.0 * 600000.0) + ",\"east\":" + F(e.LonDeg * Math.PI / 180.0 * 600000.0) + "}");
            }
            sb.Append("],\"envelope\":[");
            var fe = new FlashEvent { Seed = 5, Bumps = 3, Kind = FlashKind.Ground, Ut = 0.0, TopM = 9000f };
            for (double t = 0.0; t < 0.8; t += 0.004)
            {
                if (t > 0.0) sb.Append(',');
                sb.Append("[" + F(t) + "," + F(FlashEnvelope.Level(ref fe, t)) + "]");
            }
            sb.Append("],\"thunder\":[");
            double[] distances = { 300.0, 1500.0, 5000.0, 15000.0 };
            for (int d = 0; d < distances.Length; d++)
            {
                var plan = ThunderPolicy.Plan(distances[d], default(ListenerState), true, 1f, 1f, 0, new ThunderSettings());
                float[] clip = ThunderSynth.Render(3u, plan);
                int step = ThunderSynth.SampleRate / 100;
                if (d > 0) sb.Append(',');
                sb.Append("{\"distance\":" + F(distances[d]) + ",\"delay\":" + F(plan.DelayS) + ",\"cutoff\":" + F(plan.CutoffHz) + ",\"gain\":" + F(plan.Gain) + ",\"env\":[");
                for (int i = 0; i + step < clip.Length; i += step)
                {
                    float peak = 0f;
                    for (int k = i; k < i + step; k++) peak = Math.Max(peak, Math.Abs(clip[k]));
                    if (i > 0) sb.Append(',');
                    sb.Append(F(peak));
                }
                sb.Append("]}");
                WriteWav(Path.Combine(dir, "thunder-" + (int)distances[d] + "m.wav"), clip, ThunderSynth.SampleRate, plan.Gain);
            }
            sb.Append("]}");
            File.WriteAllText(Path.Combine(dir, "geometry.json"), sb.ToString());
            Assert.IsTrue(File.Exists(Path.Combine(dir, "geometry.json")));
        }

        // A 16-bit mono WAV of the clip at the plan's loudness, for the listening pass (T5, joint with D33).
        private static void WriteWav(string path, float[] clip, int rate, float gain)
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                int bytes = clip.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + bytes);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(rate);
                w.Write(rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(bytes);
                for (int i = 0; i < clip.Length; i++) w.Write((short)(Mathf.Clamp(clip[i] * gain, -1f, 1f) * 32767f));
            }
        }
    }
}
