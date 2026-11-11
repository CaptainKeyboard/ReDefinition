using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The reconnaissance probe of the Atmospheres plan (series K-pre, package B09): answers the premises P1 to P12 of master plan 9 with
    // measurements in one scripted flight, before the packages that rest on them are built. It is a series probe: active only with
    // -atmoSeries Kpre on the game's command line, started by tools/atmo_series.py, and it ends the game with Probe: PASSED or FAILED.
    //
    // Two starts. run1 has the full mod set (EVE, Scatterer, Deferred, TUFX, Parallax) and measures every premise; run2 has Scatterer
    // absent and measures the premises that depend on the ocean and the camera chain (P6 to P8 and P10). A premise that does not hold is
    // a result, not a failure of the probe: the metrics carry the numbers, tests/series/rows/B09.json the thresholds, and
    // tests/data/recon/RECON.md the table with the consequence taken.
    //
    // The flight: the save ReDefinitionProbe, the space centre, then a Mk1 command pod over the sea off the nearest coast to the launch
    // pad. In run1 the pod hangs under its parachute first (P4); then it floats and the camera measurements follow. Every step runs in its
    // own guard: an exception ends that premise with a line and the scenario goes on.
    //
    // Members of the game's API that the plan named differently are listed in RECON.md. Anything this file could not verify against the
    // installed assemblies carries [VERIFY].
    [AtmospheresModule(990)]
    internal sealed partial class AtmoRecon : SeriesProbe
    {
        internal const string Series = "K-pre";

        // The place of the dumps, relative to the game folder; the series runner collects this folder into tests/data/recon/dumps.
        internal const string DumpFolder = "GameData/ReDefinition/Atmospheres/PluginData/recon";

        // The save the flight is made from.
        private const string SaveFolderName = "ReDefinitionProbe";
        private const string VesselName = "ReDefinition recon pod";

        // The launch pad of the space centre, in degrees (the same as the scene probes use).
        private const double PadLatitude = -0.0972, PadLongitude = -74.5577;

        private bool full = true;
        private bool scattererPresent;
        private ReconHost host;
        private ReconFrame lastFrame;
        private string dumpPrefix = "run1";

        protected override string SeriesName
        {
            get { return Series; }
        }

        // 17 of the 20 minutes: the game needs about two to come up, and the runner ends a start at 20.
        protected override double MaxSeconds
        {
            get { return 17 * 60; }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            dumpPrefix = StartId ?? "run1";
            full = dumpPrefix != "run2";
            scattererPresent = AssemblyLoaded("Scatterer");
            host = ReconHost.Get();
            Say("start " + dumpPrefix + ", " + (full ? "full" : "reduced") + " mod set, Scatterer " + (scattererPresent ? "present" : "absent"));
            try
            {
                ReportEnvironment();
            }
            catch (Exception e)
            {
                Say("the environment could not be reported (" + e.GetType().Name + " " + e.Message + ")");
            }

            // The wind hook registers before the first flight starts: the integrator reads its overrides when a vessel is built.
            if (full) ReconWindHook.Register(Say);

            foreach (ProbeWait w in Guard("flight", ToFlight())) yield return w;
            if (FlightGlobals.ActiveVessel == null || HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("the flight did not start");
                yield break;
            }

            KeepRenderSettings();
            try
            {
                DumpBodies();
            }
            catch (Exception e)
            {
                Say("the body list could not be written (" + e.GetType().Name + " " + e.Message + ")");
            }

            // The blocking premises first, so that a start that runs out of time loses informative ones; the steps that can disturb the
            // scene go last: the clock jumps (P1), the forced Apply of EVE (P9), the other bodies' terrain (P2more).
            if (!spawnOk)
            {
                Say("the pod is not over deep water near the planned place: P4 and the ocean discs are left out");
                EmitText("P4.error", "the pod is not over the sea at the planned place (Env.pod_offset_m, Env.pod_ground_m)");
                EmitText((scattererPresent ? "P11" : "P10") + ".error", "the pod is not over the sea at the planned place");
            }
            if (full && spawnOk) foreach (ProbeWait w in Guard("P4", WindDrop())) yield return w;
            if (full) foreach (ProbeWait w in Guard("P2", HeightGrid())) yield return w;
            foreach (ProbeWait w in Guard("P6", CameraChain())) yield return w;
            if (spawnOk) foreach (ProbeWait w in Guard("P10", DiscOverOcean())) yield return w;
            foreach (ProbeWait w in Guard("P8", ForeignShader())) yield return w;
            foreach (ProbeWait w in Guard("P7", FogFixture())) yield return w;
            if (full)
            {
                foreach (ProbeWait w in Guard("P5", AudioPaths())) yield return w;
                foreach (ProbeWait w in Guard("P12", AmbientFactors())) yield return w;
                foreach (ProbeWait w in Guard("P3", VertexSpacing())) yield return w;
                foreach (ProbeWait w in Guard("P1", TemperatureSamples())) yield return w;
                Vessel after = FlightGlobals.ActiveVessel;
                Emit("P1.vessel_intact", after != null && after.state != Vessel.State.DEAD);
                foreach (ProbeWait w in Guard("P9", EveLayers())) yield return w;
                foreach (ProbeWait w in Guard("P2more", HeightGridsOthers())) yield return w;
            }
            Say("the scenario ended");
            host.PerFrame.Clear();
            host.EndOfFrame.Clear();
        }

        // Runs a sub-scenario so that an exception ends only that step: the iterator is walked by hand, since a yield cannot sit inside a
        // try block with a catch.
        private IEnumerable<ProbeWait> Guard(string id, IEnumerable<ProbeWait> body)
        {
            // The start has 20 minutes and the game needs about two to come up: a step that would begin after 14 minutes of the scenario is
            // left out, so that the later steps and the verdict are not lost to the clock.
            if (id != "flight" && Elapsed > StepBudgetSeconds)
            {
                Say(id + " left out: " + Num(Elapsed) + " s of the scenario are gone");
                EmitText(id + ".error", "left out: the start ran out of time");
                yield break;
            }
            IEnumerator<ProbeWait> e = body.GetEnumerator();
            Say(id + " begins");
            bool stopped = false;
            while (true)
            {
                bool more;
                try
                {
                    more = e.MoveNext();
                }
                catch (Exception ex)
                {
                    Say(id + " stopped by " + ex.GetType().Name + ": " + ex.Message);
                    EmitText(id + ".error", ex.GetType().Name + ": " + ex.Message);
                    more = false;
                    stopped = true;
                }
                if (!more) break;
                yield return e.Current;
            }
            CleanUp();
            if (!stopped) Say(id + " ended");
        }

        private const double StepBudgetSeconds = 14 * 60;

        // What the fixtures leave: per-frame hooks, discs, balls, audio objects, the render settings the tests change, a wind, a pause.
        private void CleanUp()
        {
            try
            {
                host.PerFrame.Clear();
                host.EndOfFrame.Clear();
                ReconMesh.DestroyTracked();
                UndressPod();
                ReconWindHook.Wind = Vector3.zero;
                if (renderSettingsKept)
                {
                    RenderSettings.fog = keptFog;
                    RenderSettings.fogMode = keptFogMode;
                    RenderSettings.fogDensity = keptFogDensity;
                    RenderSettings.fogColor = keptFogColour;
                    RenderSettings.ambientIntensity = keptAmbientIntensity;
                }
                if (pausedByProbe)
                {
                    pausedByProbe = false;
                    FlightDriver.SetPause(false, false);
                }
            }
            catch (Exception e)
            {
                Say("the clean-up failed (" + e.GetType().Name + " " + e.Message + ")");
            }
        }

        private bool renderSettingsKept, pausedByProbe, keptFog;
        private UnityEngine.FogMode keptFogMode;
        private float keptFogDensity, keptAmbientIntensity;
        private Color keptFogColour;

        private void KeepRenderSettings()
        {
            keptFog = RenderSettings.fog;
            keptFogMode = RenderSettings.fogMode;
            keptFogDensity = RenderSettings.fogDensity;
            keptFogColour = RenderSettings.fogColor;
            keptAmbientIntensity = RenderSettings.ambientIntensity;
            renderSettingsKept = true;
        }

        private void Say(string message)
        {
            LogLine("Probe: " + message);
        }

        // The place of a dump file of this start.
        private string DumpPath(string name)
        {
            return Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, DumpFolder.Replace('/', Path.DirectorySeparatorChar)), dumpPrefix + "-" + name);
        }

        private static bool AssemblyLoaded(string name)
        {
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // The mods of the stack that are loaded, as rows and as a dump; the plan names these as the interaction partners.
        private void ReportEnvironment()
        {
            string[] mods = { "EnvironmentalVisualEnhancements", "Scatterer", "Deferred", "TUFX", "Parallax", "ModularFlightIntegrator",
                              "KSPCommunityFixes", "0Harmony", "FreeIva", "ThroughTheEyes", "Firefly", "RocketSoundEnhancement", "Waterfall" };
            var json = new ReconJson().BeginObject();
            json.Text("start", dumpPrefix).Text("unity", Application.unityVersion).Text("ksp", Versioning.GetVersionString());
            json.BeginObject("mods");
            foreach (string mod in mods)
            {
                bool present = AssemblyLoaded(mod);
                json.Flag(mod, present);
                Emit("Env." + mod, present);
            }
            json.EndObject();
            json.BeginArray("assemblies");
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n.StartsWith("System") || n.StartsWith("Unity") || n.StartsWith("mscorlib") || n == "Mono.Security") continue;
                json.Text(null, n + " " + a.GetName().Version);
            }
            json.EndArray().EndObject();
            json.Save(DumpPath("env.json"));
            EmitText("Env.unity", Application.unityVersion);
        }

        // Reads the flight camera's frame at the end of this frame; the frame is in lastFrame afterwards (null if the read failed).
        private IEnumerable<ProbeWait> Capture()
        {
            host.Read();
            foreach (ProbeWait w in WaitFor(() => !host.Reading, 10)) yield return w;
            lastFrame = waited ? host.Frame : null;
        }

        // Whether the last WaitFor saw its condition.
        private bool waited;

        // Waits for a condition for up to that many real seconds; waited says whether it came true. A step that times out goes on with
        // that knowledge instead of ending the whole scenario, as ProbeWait.Until does.
        private IEnumerable<ProbeWait> WaitFor(Func<bool> condition, double seconds)
        {
            double end = Time.realtimeSinceStartup + seconds;
            waited = false;
            while (Time.realtimeSinceStartup < end)
            {
                if (condition())
                {
                    waited = true;
                    yield break;
                }
                yield return ProbeWait.Frames(1);
            }
            waited = condition();
        }

        private static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
