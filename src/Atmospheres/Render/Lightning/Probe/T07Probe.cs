using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package T07 inside the integrated series K-T1 (rows tests/series/rows/T07.json, series K-T1): lightning against the running game.
    // It forces a storm around the vessel (a cell source that covers a disc 6 km to 18 km away) and runs the module for a minute at a time:
    //   level 0.4 then 1.0 for the same seed and time span: the number of flashes (the second may not be fewer);
    //   the strokes that begin in any second with the limit at 3 (at most 3), the thunder delay planned against distance / 343.2 (at most 5 %);
    //   a warp rate of 5 (high mode, landed): no flash spawns (rows T07.gate_violations).
    // Not measured here and left to the screenshots of the series: the look of the bolt, the light on the vessel, the thunder's sound
    // [VERIFY: not run in the game yet].
    [AtmospheresModule(985)]
    internal sealed class T07Probe : SeriesProbe
    {
        internal const string Series = "K-T1";

        private bool warpedByProbe;

        // Cells in a ring segment east of the camera: a disc of 12 km radius centred 12 km east, uniform.
        private sealed class StormEast : ICellSource
        {
            internal float Level = 1f;
            internal double CentreLatRad, CentreLonRad, BodyRadiusM;

            public bool Sample(double latRad, double lonRad, double ut, out CellSample sample)
            {
                sample = new CellSample { Strength = 0.9f, Level01 = Level, TopM = 9000f, Coverage = 0.95f };
                double dn = (latRad - CentreLatRad) * BodyRadiusM;
                double de = (lonRad - CentreLonRad) * BodyRadiusM * Math.Cos(latRad);
                return dn * dn + de * de < 12000.0 * 12000.0;
            }
        }

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 8 * 60; }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            LightningModule module = null;
            yield return ProbeWait.Until(() => { module = LightningModule.Current; return module != null && FlightGlobals.ActiveVessel != null && FlightGlobals.currentMainBody != null; }, 120, "the flight scene and the lightning module");
            CelestialBody body = FlightGlobals.currentMainBody;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!body.atmosphere)
            {
                Emit("T07.skipped_no_atmosphere", true);
                yield break;
            }
            LightningSwitches.Enabled = true;
            LightningSwitches.MaxBumpsPerSecond = 3;
            LightningSwitches.ThunderSettings.RealisticDelay = true;
            LightningSwitches.Thunder = true;
            var storm = new StormEast { BodyRadiusM = body.Radius, CentreLatRad = vessel.latitude * Math.PI / 180.0, CentreLonRad = (vessel.longitude + 12000.0 / body.Radius * 180.0 / Math.PI) * Math.PI / 180.0 };
            module.ForcedSource = storm;

            int[] counts = new int[2];
            float[] levels = { 0.4f, 1f };
            for (int i = 0; i < 2; i++)
            {
                storm.Level = levels[i];
                int before = module.Spawned;
                yield return ProbeWait.Seconds(70);
                counts[i] = module.Spawned - before;
                Emit("T07.flashes.level" + (int)(levels[i] * 100), counts[i]);
            }
            Emit("T07.event_rate_monotone", counts[1] >= counts[0]);

            // The strokes of the minute and the thunder delays.
            var strokes = new List<double>(module.StrokeTimes);
            Emit("T07.max_strokes_per_second", MostInAnyWindow(strokes));
            double worst = 0.0;
            foreach (LightningModule.ThunderRecord t in module.ThunderLog)
            {
                double expected = t.DistanceM / 343.2;
                if (expected < 0.5) continue;
                worst = Math.Max(worst, Math.Abs(t.PlannedDelayS - expected) / expected);
            }
            Emit("T07.thunder_delay_error", worst);
            Emit("T07.thunders", module.ThunderLog.Count);

            // A warp rate of 5: no flash.
            bool landed = vessel != null && (vessel.Landed || vessel.Splashed) && TimeWarp.fetch != null;
            Emit("T07.warp_landed", landed);
            if (landed)
            {
                int spawned = module.Spawned;
                TimeWarp.fetch.Mode = TimeWarp.Modes.HIGH;
                TimeWarp.SetRate(1, true, false);
                warpedByProbe = true;
                yield return ProbeWait.Frames(10);
                Emit("T07.warp_rate", TimeWarp.CurrentRate);
                yield return ProbeWait.Seconds(15);
                Emit("T07.warp_spawned", module.Spawned - spawned);
                TimeWarp.SetRate(0, true, false);
                warpedByProbe = false;
            }
            Emit("T07.gate_violations", module.GateViolations);
            Check("no flash spawned at a warp rate of 5 or above", module.GateViolations == 0);
            Check("a higher level gave no fewer flashes", counts[1] >= counts[0], counts[0] + " then " + counts[1]);
            Check("the thunder came after distance / 343.2", worst <= 0.05, "worst " + worst);
            LightningSwitches.Reset();
            module.ForcedSource = null;
        }

        private static int MostInAnyWindow(List<double> times)
        {
            times.Sort();
            int most = 0;
            for (int i = 0; i < times.Count; i++)
            {
                int n = 0;
                for (int j = i; j < times.Count && times[j] < times[i] + 1.0; j++) n++;
                most = Math.Max(most, n);
            }
            return most;
        }
    }
}
