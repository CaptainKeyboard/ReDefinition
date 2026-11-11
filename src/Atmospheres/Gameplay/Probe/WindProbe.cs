using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package D31 inside the integrated series K-W3a, start "wind" (the series flies the launch-pad vessel of the probe save;
    // the start runs this probe alone: -atmoProbe WindProbe).
    //
    // Reference expectations (written before the code):
    //  - Mode table: with a forced field (10 m/s east, level 0.2) the wind applied to the landed vessel is, against the Ours case without a
    //    foreign mod (= 10 m/s times the body's ground factor): Auto without a foreign wind mod 1.0, Auto with a stub that applies a base
    //    wind StormShare(0.2) = 0.198 (the storm delta only), Ours 1.0 with or without the stub, Foreign 0, Off 0. Base wind once in every mode.
    //  - Routes: windInjection Auto picks H1 where ModularFlightIntegrator took the slot, Force picks H3.
    //  - Solar: with the sun at 40 degrees or more and the optical depth forced to 0, 1.5 and 5, the panels' _flowRate follows
    //    SolarMath.Factor(SunTransmission.From(tau, elevation)) within 0.03 of the clear value; a save written in the dimmed state holds the
    //    efficiencyMult the panels held before the dimming.
    //  - Chute: a canopy over the sea in a 15 m/s wind (level 0.8 lifts the speed cap to the violent wind) drifts downwind by the distance
    //    of the drag model of ReconMath.PredictChuteDriftBetween over the second half of the 30 s window, within 15 %, with H1; with H3
    //    (windInjection Force) the same drift within 30 %; during each window only its own route acted. A forced absurd wind makes the
    //    clamp fire (smallest share below 1) and the applied wind stays under the violent cap. Ten minutes of game time at physics warp 3x
    //    leave every position finite and every speed under 100 m/s.
    // The views are numbers: the series table is the evidence (a trajectory log line per second goes to the game log).
    [AtmospheresModule(987)]
    internal sealed partial class WindProbe : SeriesProbe
    {
        internal const string Series = "K-W3a";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 17 * 60; }
        }

        private ForcedWindField forced;
        private IWeatherField original;

        private static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static double Ut()
        {
            return Planetarium.GetUniversalTime();
        }

        // Frames until that many seconds of game time have passed (real time caps it).
        private IEnumerable<ProbeWait> GameSeconds(double seconds)
        {
            double end = Ut() + seconds;
            double realEnd = Time.realtimeSinceStartup + 4.0 * seconds + 20.0;
            while (Ut() < end && Time.realtimeSinceStartup < realEnd) yield return ProbeWait.Frames(1);
        }

        private void InstallField()
        {
            original = WeatherHub.Field;
            forced = new ForcedWindField(original);
            WeatherHub.Field = forced;
        }

        private void RemoveField()
        {
            if (forced != null && ReferenceEquals(WeatherHub.Field, forced)) WeatherHub.Field = original;
            forced = null;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && GameGlobals.ActiveVessel != null, 330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("D31: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.WindForces, 1.0);
            SetSetting(AtmospheresSettings.Keys.WindStrength, 1.0);
            SetSetting(AtmospheresSettings.Keys.GustStrength, 0.0);
            SetSetting(AtmospheresSettings.Keys.WindOnLanded, 1.0);
            SetSetting(AtmospheresSettings.Keys.WindSource, (double)(int)WindSourceMode.Auto);
            SetSetting(AtmospheresSettings.Keys.WindInjection, (double)(int)WindInjection.Auto);
            SetSetting(AtmospheresSettings.Keys.SolarDimming, 1.0);
            SetSetting(AtmospheresSettings.Keys.SolarDimmingStrength, 1.0);
            SetSetting(AtmospheresSettings.Keys.VisibilityExport, 1.0);
            InstallField();
            yield return ProbeWait.Seconds(6);

            foreach (ProbeWait w in PadScenario()) yield return w;
            foreach (ProbeWait w in ChuteScenario()) yield return w;
            RemoveField();
            WindModule.ForeignOverride = null;
        }

        // --- Flight 1: the pad ---

        private IEnumerable<ProbeWait> PadScenario()
        {
            Vessel v = GameGlobals.ActiveVessel;
            Emit("D31.pad_landed", v.LandedOrSplashed);
            Emit("D31.mfi_registered", WindHook.Registered);
            EmitText("D31.mfi_status", WindHook.Status);
            Emit("D31.fields_dragvector", typeof(Part).GetField("dragVector") != null);

            // A. The mode table, numerically, with a stub that applies a base wind.
            forced.EastMs = 10f;
            forced.NorthMs = 0f;
            forced.Level01 = 0.2f;
            ForeignWinds stub = new ForeignWinds(new List<ForeignWindMod> { ForeignWindDetector.Catalogue[3] });
            double reference = 0.0;
            var cells = new[]
            {
                new { Name = "ours_absent", Mode = WindSourceMode.Ours, Stub = false, Expect = 1.0 },
                new { Name = "auto_absent", Mode = WindSourceMode.Auto, Stub = false, Expect = 1.0 },
                new { Name = "auto_present", Mode = WindSourceMode.Auto, Stub = true, Expect = WindCompose.StormShare(0.2) },
                new { Name = "ours_present", Mode = WindSourceMode.Ours, Stub = true, Expect = 1.0 },
                new { Name = "foreign_absent", Mode = WindSourceMode.Foreign, Stub = false, Expect = 0.0 },
                new { Name = "foreign_present", Mode = WindSourceMode.Foreign, Stub = true, Expect = 0.0 },
                new { Name = "off_absent", Mode = WindSourceMode.Off, Stub = false, Expect = 0.0 }
            };
            foreach (var cell in cells)
            {
                WindModule.ForeignOverride = cell.Stub ? stub : ForeignWinds.None;
                SetSetting(AtmospheresSettings.Keys.WindSource, (double)(int)cell.Mode);
                yield return ProbeWait.Seconds(3);
                double applied = WindService.Plan.AppliesAnything ? WindService.WindFor(v).Length : 0.0;
                if (cell.Name == "ours_absent")
                {
                    reference = applied;
                    Emit("D31.mode_reference_ms", applied);
                    EmitText("D31.mode_plan_ours", WindService.Plan.OwnerLine());
                }
                double ratio = reference > 1e-6 ? applied / reference : double.NaN;
                Emit("D31.mode_" + cell.Name + "_ratio", ratio);
                Check("mode " + cell.Name, !double.IsNaN(ratio) && Math.Abs(ratio - cell.Expect) < 0.04, "ratio " + Num(ratio) + ", expected " + Num(cell.Expect));
            }
            WindModule.ForeignOverride = null;
            SetSetting(AtmospheresSettings.Keys.WindSource, (double)(int)WindSourceMode.Auto);

            // B. The routes.
            yield return ProbeWait.Seconds(2);
            Emit("D31.route_auto", (double)(int)WindService.Plan.Route);
            SetSetting(AtmospheresSettings.Keys.WindInjection, (double)(int)WindInjection.Force);
            yield return ProbeWait.Seconds(2);
            Emit("D31.route_force", (double)(int)WindService.Plan.Route);
            SetSetting(AtmospheresSettings.Keys.WindInjection, (double)(int)WindInjection.Auto);
            yield return ProbeWait.Seconds(2);

            // C. The visibility export.
            forced.EastMs = 0f;
            forced.Level01 = 0f;
            forced.VisibilityM = 400f;
            yield return ProbeWait.Seconds(3);
            float vis = VisibilityExport.VisibilityM;
            Emit("D31.visibility_m", float.IsNaN(vis) ? -1.0 : vis);
            Emit("D31.visibility_line_ok", VisibilityExport.Line.StartsWith("visibility ", StringComparison.Ordinal));
            EmitText("D31.visibility_line", VisibilityExport.Line);
            SetSetting(AtmospheresSettings.Keys.VisibilityExport, 0.0);
            yield return ProbeWait.Seconds(1);
            Emit("D31.visibility_off_is_nan", float.IsNaN(VisibilityExport.VisibilityM));
            SetSetting(AtmospheresSettings.Keys.VisibilityExport, 1.0);
            forced.VisibilityM = WeatherSample.ClearVisibilityM;

            // D. The solar panels at three levels, and the save.
            foreach (ProbeWait w in SolarScenario(v)) yield return w;
        }

        private static List<ModuleDeployableSolarPanel> Panels(Vessel v)
        {
            var list = new List<ModuleDeployableSolarPanel>();
            foreach (Part p in v.parts)
            {
                foreach (PartModule m in p.Modules)
                {
                    var panel = m as ModuleDeployableSolarPanel;
                    if (panel != null) list.Add(panel);
                }
            }
            return list;
        }

        private static double Flow(List<ModuleDeployableSolarPanel> panels)
        {
            double sum = 0.0;
            foreach (ModuleDeployableSolarPanel p in panels) sum += p._flowRate;
            return sum;
        }

        private static double SunElevationRad(Vessel v)
        {
            CelestialBody sun = GameGlobals.Sun;
            if (sun == null) return Math.PI / 2.0;
            Vector3d toSun = (sun.position - v.GetWorldPos3D()).normalized;
            Vector3d up = (v.GetWorldPos3D() - v.mainBody.position).normalized;
            return Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, up))));
        }

        private IEnumerable<ProbeWait> SolarScenario(Vessel v)
        {
            List<ModuleDeployableSolarPanel> panels = Panels(v);
            Emit("D31.panels", panels.Count);
            if (panels.Count == 0)
            {
                Fail("D31: the pad vessel has no solar panel");
                yield break;
            }
            // The sun: the clock goes on in steps of two minutes until it is 40 degrees over the pad (at most a day).
            for (int i = 0; i < 400 && SunElevationRad(v) < 40.0 * Math.PI / 180.0; i++)
            {
                Planetarium.SetUniversalTime(Ut() + 120.0);
                yield return ProbeWait.Frames(2);
            }
            double elevation = SunElevationRad(v);
            Emit("D31.sun_elevation_deg", elevation * 180.0 / Math.PI);
            yield return ProbeWait.Seconds(4);

            double[] taus = { 0.0, 1.5, 5.0 };
            double clear = 0.0;
            var before = new List<float>();
            foreach (ModuleDeployableSolarPanel p in panels) before.Add(p.efficiencyMult);
            BodyClimate climate = Bodies.Get(v.mainBody);
            double solarLoss = climate != null ? climate.Gameplay.SolarLoss : 1.0;
            for (int level = 0; level < taus.Length; level++)
            {
                forced.OpticalDepth = (float)taus[level];
                yield return ProbeWait.Seconds(10);
                double flow = Flow(panels);
                if (level == 0)
                {
                    clear = flow;
                    Emit("D31.panel_flow_clear", flow);
                    if (flow <= 1e-6)
                    {
                        Fail("D31: the panels make no power in the clear sky (sun on them? elevation " + Num(elevation * 180.0 / Math.PI) + ")");
                        yield break;
                    }
                    continue;
                }
                double expected = SolarMath.Factor(SolarMath.Transmission(taus[level], elevation), solarLoss, 1.0);
                double ratio = flow / clear;
                Emit("D31.panel_ratio_level" + level, ratio);
                Emit("D31.panel_expected_level" + level, expected);
                Emit("D31.panel_error_level" + level, Math.Abs(ratio - expected));
            }

            // The save in the dimmed state (level 2): the file holds the efficiencyMult of before.
            string path = null;
            try
            {
                path = GamePersistence.SaveGame("d31wind", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            }
            catch (Exception e)
            {
                EmitText("D31.save_error", e.Message);
            }
            bool same = false;
            int found = 0;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                string text = File.ReadAllText(path);
                var values = new List<float>();
                foreach (Match m in Regex.Matches(text, @"efficiencyMult\s*=\s*([0-9eE+\-.]+)"))
                {
                    float f;
                    if (float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) values.Add(f);
                }
                found = values.Count;
                same = found >= before.Count;
                foreach (float b in before)
                {
                    bool has = false;
                    foreach (float s in values) if (Math.Abs(s - b) < 1e-5f) has = true;
                    same &= has;
                }
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    // A file left behind is in the probe's save folder; the runner restores the tracked files.
                }
            }
            Emit("D31.save_efficiency_values", found);
            Emit("D31.save_efficiency_unchanged", same);
            // The dimming goes on after the save.
            yield return ProbeWait.Seconds(6);
            double again = Flow(panels) / clear;
            double expectedAgain = SolarMath.Factor(SolarMath.Transmission(taus[2], elevation), solarLoss, 1.0);
            Emit("D31.panel_resumes_after_save_error", Math.Abs(again - expectedAgain));
            // And the live efficiencyMult is the one before the dimming.
            bool live = true;
            for (int i = 0; i < panels.Count; i++) live &= Math.Abs(panels[i].efficiencyMult - before[i]) < 1e-5f;
            Emit("D31.live_efficiency_unchanged", live);

            forced.OpticalDepth = 0f;
            yield return ProbeWait.Seconds(8);
            Emit("D31.panel_restored_ratio", Flow(panels) / clear);
        }
    }
}
