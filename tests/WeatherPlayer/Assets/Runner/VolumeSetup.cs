using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The volume pass in the weather player: the real VolumeRenderer, VolumeWindowResampler and VolumeLook (linked from
    // src/Atmospheres/Render/Volume), fed by a test window in place of the field. The world is flat and axis-aligned: east is +x, north
    // is +z, up is +y, the window stands at the origin. Scenarios (the forced field states of the verification): "convective" is one
    // deep convective cell with an anvil, "front" a line of cells across the wind, "deck" a stratus deck, "none" clear air.
    internal sealed class VolumeSetup
    {
        internal readonly VolumeRenderer Renderer = new VolumeRenderer();
        internal VolumeWindowResampler Resampler;
        internal readonly VolumeFrame Frame = new VolumeFrame();
        internal readonly WeatherWindow Coarse = new WeatherWindow();
        internal VolumeTier Tier;
        internal VolumeLook Look;
        internal string Scenario = "convective";
        internal string Problem;
        // Overrides of the tuning keys (the command line's -set volume.x=..), merged over the compiled defaults.
        internal readonly Dictionary<string, float> Overrides = new Dictionary<string, float>();
        internal int Rebuilds;
        internal double ResampleMs;
        private float groundAlt;
        private Texture3D noise;

        // The window's frame in the world and the wind of the scenario, east and north, m/s.
        internal Vector2 Wind = new Vector2(9f, 4f);
        // The time of the snapshot, s; the pattern drifts with the wind after it.
        internal double SnapshotSeconds;

        // The tier of the run: -tier on the command line, else 2 (Medium, the profile of the A4500 runs).
        internal static int TierArgument()
        {
            string t = WeatherPass.Argument("-tier");
            int v;
            return t != null && int.TryParse(t, out v) ? v : 2;
        }

        internal float Get(string key, float fallback)
        {
            float v;
            return Overrides.TryGetValue(key, out v) ? v : fallback;
        }

        // Makes the renderer and the first window. tierIndex is the numeric QualityTier (3 = High: the 64 x 32 x 64 window).
        internal bool Init(string scenario, int tierIndex, int windowAcrossOverride = 0)
        {
            Scenario = scenario;
            Tier = VolumeMath.ForTier(tierIndex);
            string vset = WeatherPass.Argument("-vset");
            if (!string.IsNullOrEmpty(vset))
            {
                foreach (string pair in vset.Split(';'))
                {
                    string[] kv = pair.Split('=');
                    float v;
                    if (kv.Length == 2 && float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) Overrides[kv[0]] = v;
                }
            }
            Shader shader = Shader.Find("ReDefinition/AtmospheresVolume");
            if (shader == null)
            {
                Problem = "shader missing: ReDefinition/AtmospheresVolume";
                return false;
            }
            if (!Renderer.Init(shader, Tier.WindowXz, Tier.WindowY, null))
            {
                Problem = Renderer.Problem;
                return false;
            }
            Resampler = new VolumeWindowResampler(Tier.WindowXz, Tier.WindowY);
            Look = VolumeLook.Read(Get);
            Rebuild(0.0, 1f);
            return true;
        }

        // Fills the coarse window for the scenario and resamples it completely (the player builds the window before the first frame).
        internal void Rebuild(double seconds, float smoothing)
        {
            MakeWindow(Scenario, Coarse);
            Look = VolumeLook.Read(Get);
            var watch = Stopwatch.StartNew();
            Resampler.Begin(Coarse, seconds, 7UL, Tier.HalfWidthM, WindowHeight(Coarse), 0f, 0f, groundAlt, smoothing, Look);
            Resampler.Complete();
            watch.Stop();
            ResampleMs = watch.Elapsed.TotalMilliseconds;
            if (Scenario == "deck") Resampler.FillDeckMapUniform(0.85f, 0f);
            else Resampler.FillDeckMapUniform(0f, 0f);
            Renderer.Upload(Resampler);
            SnapshotSeconds = seconds;
            Rebuilds++;
        }

        internal static float WindowHeight(WeatherWindow w)
        {
            return Math.Max(4000f, (w.Nz + 0.5f) * Math.Max(w.SpacingVerticalM, 1f) + w.OriginAltM);
        }

        // The forced field states, as a coarse window of 29.5 km cells (the simulation's cell size) around the origin.
        internal static void MakeWindow(string scenario, WeatherWindow w)
        {
            const int n = 5, levels = 14;
            w.Resize(n, n, levels);
            w.SpacingHorizontalM = 29500f;
            w.SpacingVerticalM = 750f;
            w.OriginAltM = 375f;
            w.CentreLatDeg = 0.0;
            w.CentreLonDeg = 0.0;
            w.RadiusKm = n * 29.5 * 0.5;
            Array.Clear(w.CloudWater, 0, w.CloudWater.Length);
            Array.Clear(w.Updraft, 0, w.Updraft.Length);
            for (int i = 0; i < w.Temperature.Length; i++) w.Temperature[i] = 288f;
            if (scenario == "convective")
            {
                // One deep cell in the middle column (base 1 km, top 9 km, an anvil in the highest levels that reaches the neighbours).
                Column(w, 2, 2, 1100f, 9000f, 1.3f, 22f);
                Anvil(w, 2, 2, 7800f, 9000f, 0.45f);
                Anvil(w, 2, 1, 8300f, 9000f, 0.3f);
                Anvil(w, 1, 2, 8300f, 9000f, 0.3f);
                Anvil(w, 3, 2, 8300f, 9000f, 0.3f);
                Anvil(w, 2, 3, 8300f, 9000f, 0.3f);
            }
            else if (scenario == "front")
            {
                // A line across the wind (east-west) through the middle row: lower cells with a base at 700 m and a top at 5 km.
                for (int ix = 0; ix < n; ix++) Column(w, ix, 2, 700f, 5000f, 1.0f, 9f);
            }
            // "deck" and "none" have no 3D cloud water; the deck comes from the coverage and type map.
        }

        private static void Column(WeatherWindow w, int ix, int iy, float baseAlt, float topAlt, float peak, float updraft)
        {
            for (int iz = 0; iz < w.Nz; iz++)
            {
                float alt = w.OriginAltM + iz * w.SpacingVerticalM;
                if (alt < baseAlt || alt > topAlt) continue;
                float s = (alt - baseAlt) / (topAlt - baseAlt);
                int i = w.Index(ix, iy, iz);
                w.CloudWater[i] = peak * Mathf.Sin(Mathf.Clamp01(s) * Mathf.PI) * 0.85f + 0.15f * peak;
                w.Updraft[i] = updraft * Mathf.Sin(Mathf.Clamp01(s) * Mathf.PI);
                w.Temperature[i] = 288f - 6.5f * alt / 1000f;
            }
        }

        private static void Anvil(WeatherWindow w, int ix, int iy, float baseAlt, float topAlt, float cw)
        {
            for (int iz = 0; iz < w.Nz; iz++)
            {
                float alt = w.OriginAltM + iz * w.SpacingVerticalM;
                if (alt < baseAlt || alt > topAlt) continue;
                int i = w.Index(ix, iy, iz);
                w.CloudWater[i] = Mathf.Max(w.CloudWater[i], cw);
                w.Temperature[i] = 288f - 6.5f * alt / 1000f;
            }
        }

        // Writes the frame's numbers (time, wind, light, the jitter) and records the passes on the camera.
        internal void Prepare(Camera cam, CellSettings c, int frameNumber, double seconds, Vector2 jitterPixels, int jitterIndex,
                              Action<CommandBuffer> markStart, Action<CommandBuffer> markMarched, Action<CommandBuffer> markEnd, bool analytic)
        {
            VolumeFrame f = Frame;
            f.Centre = Vector3.zero;
            f.Up = Vector3.up;
            f.East = Vector3.right;
            f.North = Vector3.forward;
            f.Curvature = 0f;
            f.HalfWidthM = Tier.HalfWidthM;
            f.HeightM = WindowHeight(Coarse);
            float age = (float)(seconds - SnapshotSeconds);
            f.Drift = new Vector2(Wind.x * age, Wind.y * age);
            f.WindOffset = new Vector2(StormVolumeMath.Wrap(Wind.x * seconds, Look.ErosionTileM * 16.0), StormVolumeMath.Wrap(Wind.y * seconds, Look.ErosionTileM * 16.0));
            f.AdvectVelocity = new Vector3(Wind.x, 0f, Wind.y);
            f.TowardsSun = c.TowardsSun;
            f.SunColour = c.SunColour;
            f.AmbientColour = c.Ambient;
            f.Steps = (int)Get("volume.steps", Tier.Steps);
            f.Resolution = Get("volume.resolution", 1f);
            f.VerticalHalf = Get("volume.vertHalf", 0f) > 0.5f;
            f.FrameNumber = frameNumber;
            f.JitterIndex = jitterIndex;
            f.JitterPixels = jitterPixels;
            f.SunTaps = Tier.SunTaps;
            f.Curl = Get("volume.kwCurl", Tier.Curl ? 1f : 0f) > 0.5f;
            f.Powder = Get("volume.kwPowder", 1f) > 0.5f;
            f.Deck = Scenario == "deck" || Get("volume.kwDeck", 0f) > 0.5f;
            f.KeyedDither = Get("volume.kwJitter", 1f) > 0.5f;
            Renderer.Prepare(cam, f, Look, markStart, markMarched, markEnd, analytic);
        }

        internal void SetOverride(string key, float value)
        {
            Overrides[key] = value;
            Look = VolumeLook.Read(Get);
        }

        // Parses -set text "volume.a=1;volume.b=2" (names that start with volume. go here; the rest are the cell's).
        internal bool TrySet(string name, float value)
        {
            if (!name.StartsWith("volume.", StringComparison.Ordinal)) return false;
            SetOverride(name, value);
            return true;
        }

        internal static string Describe(float v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
