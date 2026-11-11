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

            var ticks = new long[4000];
            for (int i = 0; i < ticks.Length; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                field.Sample(index, -80.0 + 160.0 * (i % 97) / 97.0, -180.0 + 360.0 * (i % 89) / 89.0, 50.0 * (i % 13), ut + 3600.0 * (i / 256), out s);
                ticks[i] = Stopwatch.GetTimestamp() - t0;
            }
            Array.Sort(ticks);
            Emit("C25.sample_median_us", ticks[ticks.Length / 2] * 1e6 / Stopwatch.Frequency);

            var weather = new GeneratorMaps.Weather2D();
            var watch = Stopwatch.StartNew();
            bool rendered = GeneratorMaps.RenderWeather(state, ut, weather);
            Emit("C25.map_render_ms", watch.Elapsed.TotalMilliseconds);
            Emit("C25.map_rendered", rendered);

            // A read of a finished map costs the main thread a copy only.
            field.Maps.RenderNow(index, ut);
            var map = new WeatherMap();
            field.GetMap(index, WeatherMapKind.Coverage, ut, map);
            watch.Restart();
            for (int i = 0; i < 20; i++) field.GetMap(index, WeatherMapKind.Coverage, ut, map);
            Emit("C25.map_read_ms", watch.Elapsed.TotalMilliseconds / 20.0);

            var window = new WeatherWindow();
            field.FillWindow(index, FlightGlobals.ActiveVessel.latitude, FlightGlobals.ActiveVessel.longitude, 30.0, ut, window);
            watch.Restart();
            bool filled = field.FillWindow(index, FlightGlobals.ActiveVessel.latitude, FlightGlobals.ActiveVessel.longitude, 30.0, ut, window);
            Emit("C25.window_ms", watch.Elapsed.TotalMilliseconds);
            Emit("C25.window_filled", filled);
        }
    }
}
