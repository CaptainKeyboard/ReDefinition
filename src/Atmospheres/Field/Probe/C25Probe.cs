using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The probe of package C25 inside the integrated series K-W3a (the section "Simulation inside KSP"): what only the running game can say
    // about the weather generator. The generator is a pure function, so the proof of its numbers is offline (the unit tests, the sync tool);
    // here the game's own pieces are measured: that the height raster of the focused body is read from the PQS, that the game's thermal code gives
    // a temperature table the fit accepts, that the field stands in the hub, that the answer is the same on the main thread and on a worker, and
    // what a sample, a map and a window cost inside the game's process (Mono, the game's JIT) with the rest of the game running.
    [AtmospheresModule(986)]
    internal sealed class C25Probe : SeriesProbe
    {
        internal const string Series = "K-W3a";

        protected override string SeriesName
        {
            get { return Series; }
        }

        protected override double MaxSeconds
        {
            get { return 6 * 60; }
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("C25: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.GenEnabled, 1.0);
            CelestialBody body = FlightGlobals.currentMainBody;
            int index = body.flightGlobalsIndex;
            GeneratorField field = null;
            // The module reads the raster a few milliseconds a frame: give it a minute.
            yield return ProbeWait.Until(() =>
            {
                field = GeneratorModule.Current;
                return field != null && field.StateOf(index) != null;
            }, 90, "the generator state of the focused body");
            Emit("C25.state_ready", field != null && field.StateOf(index) != null);
            Emit("C25.field_in_hub", field != null && ReferenceEquals(WeatherHub.Field, field));
            if (field == null || field.StateOf(index) == null) yield break;
            GeneratorState state = field.StateOf(index);

            // The axial tilt: the angle between the body's rotation axis and the normal of its planet's orbit, as the game has it (stock bodies
            // have none), against the tilt the generator uses (the default Earth axis where the body is untilted but has an orbit season clock).
            BodyFacts facts = BodyProbe.Read(body, null, new List<string>());
            Emit("C25.body_obliquity_deg", facts.ObliquityDeg);
            Emit("C25.axis_from_reference_deg", facts.AxisFromReferenceDeg);
            Emit("C25.tilt_used_deg", state.Parameters.ObliquityRad * 180.0 / Math.PI);

            // The temperature table of the game's thermal code and the fit of it.
            string note;
            ThermalFit fit = ThermalFit.From(ThermalProbe.Read(body, out note));
            Emit("C25.thermal_fit_ok", fit != null);
            if (fit != null)
            {
                Emit("C25.thermal_mean_k", fit.MeanK);
                Emit("C25.thermal_equator_minus_pole_k", fit.EquatorK - fit.PoleK);
                Emit("C25.thermal_lapse_k_per_km", fit.LapseKPerM * 1000.0);
                Emit("C25.thermal_diurnal_k", fit.DiurnalAmpK);
            }
            else EmitText("C25.thermal_note", note);
            Emit("C25.profile_used", state.Parameters.ProfileK != null);

            // Answers: all finite, equal on a worker, the cost inside the game's process.
            double ut = Planetarium.GetUniversalTime();
            int bad = 0;
            double sumMain = 0.0;
            WeatherSample s;
            for (int i = 0; i < 2000; i++)
            {
                field.Sample(index, -80.0 + 160.0 * (i % 97) / 97.0, -180.0 + 360.0 * (i % 89) / 89.0, 50.0 * (i % 13), ut + 3600.0 * (i / 200), out s);
                if (float.IsNaN(s.TemperatureK) || float.IsNaN(s.Level01) || float.IsNaN(s.WindWorld.x)) bad++;
                sumMain += s.TemperatureK + 1000.0 * s.Level01 + s.WindWorld.x;
            }
            Emit("C25.sample_nan_count", bad);
            double sumWorker = 0.0;
            var worker = new Thread(() =>
            {
                WeatherSample w;
                for (int i = 0; i < 2000; i++)
                {
                    field.Sample(index, -80.0 + 160.0 * (i % 97) / 97.0, -180.0 + 360.0 * (i % 89) / 89.0, 50.0 * (i % 13), ut + 3600.0 * (i / 200), out w);
                    sumWorker += w.TemperatureK + 1000.0 * w.Level01 + w.WindWorld.x;
                }
            });
            worker.Start();
            worker.Join();
            Emit("C25.worker_equals_main", sumMain == sumWorker);

            // The cost rows measure the steady state of a Release build: every path has been through the JIT (the 4000 answers above, a first
            // map and a first window below, each timed apart as the cold row), the collector has run, and a row is the MEDIAN of many short
            // batches (a batch is 256 answers, so the timer's own cost is spread over them; one stall of the game's other threads or of the
            // collector moves one batch, not the row). The 99th percentile of the batches is reported beside it. A Debug build is named in the
            // log: its numbers are not the gate's.
#if DEBUG
            EmitText("C25.timing_build", "Debug");
#else
            EmitText("C25.timing_build", "Release");
#endif
            GC.Collect();
            const int Batch = 256, Batches = 48;
            var batchUs = new double[Batches];
            for (int b = 0; b < Batches; b++)
            {
                long t0 = Stopwatch.GetTimestamp();
                for (int j = 0; j < Batch; j++)
                {
                    int i = b * Batch + j;
                    field.Sample(index, -80.0 + 160.0 * (i % 97) / 97.0, -180.0 + 360.0 * (i % 89) / 89.0, 50.0 * (i % 13), ut + 3600.0 * (i / 256), out s);
                }
                batchUs[b] = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency / Batch;
            }
            Emit("C25.sample_median_us", Percentile(batchUs, 0.5));
            Emit("C25.sample_p99_us", Percentile(batchUs, 0.99));

            var weather = new GeneratorMaps.Weather2D();
            var watch = Stopwatch.StartNew();
            bool rendered = GeneratorMaps.RenderWeather(state, ut, weather);
            Emit("C25.map_render_cold_ms", watch.Elapsed.TotalMilliseconds);
            Emit("C25.map_rendered", rendered);
            GC.Collect();
            var mapMs = new double[5];
            for (int r = 0; r < mapMs.Length; r++)
            {
                watch.Restart();
                GeneratorMaps.RenderWeather(state, ut + 60.0 * (r + 1), weather);
                mapMs[r] = watch.Elapsed.TotalMilliseconds;
            }
            Emit("C25.map_render_ms", Percentile(mapMs, 0.5));

            // A read of a finished map costs the main thread a copy only.
            field.Maps.RenderNow(index, ut);
            var map = new WeatherMap();
            field.GetMap(index, WeatherMapKind.Coverage, ut, map);
            watch.Restart();
            for (int i = 0; i < 20; i++) field.GetMap(index, WeatherMapKind.Coverage, ut, map);
            Emit("C25.map_read_ms", watch.Elapsed.TotalMilliseconds / 20.0);

            var window = new WeatherWindow();
            watch.Restart();
            field.FillWindow(index, FlightGlobals.ActiveVessel.latitude, FlightGlobals.ActiveVessel.longitude, 30.0, ut, window);
            Emit("C25.window_cold_ms", watch.Elapsed.TotalMilliseconds);
            GC.Collect();
            var windowMs = new double[25];
            bool filled = true;
            for (int r = 0; r < windowMs.Length; r++)
            {
                watch.Restart();
                filled &= field.FillWindow(index, FlightGlobals.ActiveVessel.latitude, FlightGlobals.ActiveVessel.longitude, 30.0, ut + 600.0 * r, window);
                windowMs[r] = watch.Elapsed.TotalMilliseconds;
            }
            // The synchronous fill: what the worker spends (it was the main thread's cost before the fill went to a worker).
            Emit("C25.window_fill_ms", Percentile(windowMs, 0.5));
            Emit("C25.window_fill_p99_ms", Percentile(windowMs, 0.99));
            Emit("C25.window_filled", filled);

            // The worker fill, as the volume pass uses it: the main thread asks and takes, the worker fills a double buffer. The window must
            // be the one the synchronous fill makes at the same place and time (hash of every number), and the main thread's share must be small.
            double lat = FlightGlobals.ActiveVessel.latitude, lon = FlightGlobals.ActiveVessel.longitude;
            var taken = new WeatherWindow();
            var reference = new WeatherWindow();
            var mainMs = new double[25];
            var readyMs = new double[25];
            bool same = true, jobsOk = true;
            for (int r = 0; r < mainMs.Length; r++)
            {
                double at = ut + 600.0 * r;
                field.FillWindow(index, lat, lon, 30.0, at, reference);
                Drain(field);
                watch.Restart();
                jobsOk &= field.BeginWindow(index, lat, lon, 30.0, at);
                double ask = watch.Elapsed.TotalMilliseconds;
                var wall = Stopwatch.StartNew();
                while (field.WindowState == WindowJobState.Running && wall.ElapsedMilliseconds < 5000) Thread.Sleep(0);
                readyMs[r] = ask + wall.Elapsed.TotalMilliseconds;
                watch.Restart();
                jobsOk &= field.TakeWindow(ref taken) == WindowJobState.Ready;
                mainMs[r] = ask + watch.Elapsed.TotalMilliseconds;
                same &= WindowHash(taken) == WindowHash(reference);
            }
            Emit("C25.window_job_ok", jobsOk);
            Emit("C25.window_job_matches_sync", same);
            Emit("C25.window_ms", Percentile(mainMs, 0.5));
            Emit("C25.window_p99_ms", Percentile(mainMs, 0.99));
            Emit("C25.window_job_ready_ms", Percentile(readyMs, 0.5));
        }

        // Waits for a fill the volume pass may have in the air and drops its result, so that the probe's own fill can start.
        private static void Drain(GeneratorField field)
        {
            var wall = Stopwatch.StartNew();
            while (field.WindowState == WindowJobState.Running && wall.ElapsedMilliseconds < 5000) Thread.Sleep(0);
            var scrap = new WeatherWindow();
            field.TakeWindow(ref scrap);
        }

        // A hash over the bits of every number of a window.
        private static ulong WindowHash(WeatherWindow w)
        {
            ulong h = 0xCBF29CE484222325UL;
            h = (h ^ (ulong)w.Nx) * 0x100000001B3UL;
            h = (h ^ (ulong)w.Ny) * 0x100000001B3UL;
            h = (h ^ (ulong)w.Nz) * 0x100000001B3UL;
            h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(w.CentreLatDeg)) * 0x100000001B3UL;
            h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(w.CentreLonDeg)) * 0x100000001B3UL;
            h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(w.Ut)) * 0x100000001B3UL;
            for (int i = 0; i < w.Count; i++)
            {
                h = (h ^ (uint)BitConverter.ToInt32(BitConverter.GetBytes(w.CloudWater[i]), 0)) * 0x100000001B3UL;
                h = (h ^ (uint)BitConverter.ToInt32(BitConverter.GetBytes(w.Updraft[i]), 0)) * 0x100000001B3UL;
                h = (h ^ (uint)BitConverter.ToInt32(BitConverter.GetBytes(w.Temperature[i]), 0)) * 0x100000001B3UL;
            }
            return h;
        }

        // The value at the fraction p of the sorted values (nearest rank); the input is sorted in place.
        private static double Percentile(double[] values, double p)
        {
            Array.Sort(values);
            int rank = (int)Math.Ceiling(p * values.Length) - 1;
            return values[Math.Max(0, Math.Min(values.Length - 1, rank))];
        }
    }
}
