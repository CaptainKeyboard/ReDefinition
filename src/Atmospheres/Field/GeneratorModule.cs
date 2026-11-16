using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using AtmoSim.Core;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Starts the weather generator with the other modules: makes the field, puts it into WeatherHub.Field while weatherEffects and genEnabled
    // are on (and takes it out again when either goes off, so that every consumer shows nothing and the game is vanilla), reads the height
    // raster of a body a few milliseconds a frame, probes the game's temperature curves, builds the state of the body and hands it to the
    // field. The field is a pure function; this module is the only part that touches the game's objects, on the main thread, a small step per
    // frame. A body whose sphere is not started has no raster and no weather yet: the module never starts a sphere (that loads its terrain).
    [AtmospheresModule(5)]
    internal sealed class GeneratorModule : IAtmospheresModule
    {
        // The ticks the thermal probe is tried again after it found no scene, before the body is built from the target curve of the probe.
        private const int ThermalTries = 4;

        private sealed class Built
        {
            internal BodyClimate Climate;
            internal int ThermalTries;
            // The sliced thermal probe of a rebuild in progress.
            internal ThermalProbe.Session Thermal;
        }

        // The main-thread time one frame of a rebuild spends asking the game's thermal code.
        private const double RebuildThermalSliceMs = 4.0;

        private GeneratorField field;
        private readonly Dictionary<int, GeneratorRaster> rasters = new Dictionary<int, GeneratorRaster>();
        private readonly Dictionary<int, Built> built = new Dictionary<int, Built>();
        private int lastSeed = int.MinValue;
        private bool installed;

        // A state being made on a worker for a rebuild (WeatherRebuild): the body's old state stays in the field until the new one is swapped in.
        private sealed class Creating
        {
            internal Task<GeneratorState> Task;
            internal string Error;
            internal BodyClimate Climate;
            internal double WorkerMs;
            internal List<string> Notes;
        }

        private readonly Dictionary<int, Creating> creating = new Dictionary<int, Creating>();
        private static string rebuildReason;
        private static bool rebuildAsked;

        // Asks for the state of the body the camera is over (or of every body that has one, where there is none) to be made again with the current inputs: the seed, the body's levers
        // and the viewer's test levers (GeneratorViewerOverrides). The old state serves until the new one is swapped in. The next tick of the module takes the request.
        internal static void RequestRebuild(string reason)
        {
            rebuildReason = reason ?? "rebuild";
            rebuildAsked = true;
        }

        // The field of the running module, or null before Init and after Shutdown.
        internal static GeneratorField Current { get; private set; }

        public void Init()
        {
            field = new GeneratorField();
            field.Maps.Start();
            Current = field;
            lastSeed = AtmospheresSettings.GenSeed;
            WeatherRebuild.Reset();
            creating.Clear();
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
            if (field == null) return;
            bool on = CloudGates.GeneratorInstalled(AtmospheresSettings.Number);
            if (!on)
            {
                Uninstall();
                return;
            }
            Install();
            if (!installed) return;
            int seed = AtmospheresSettings.GenSeed;
            if (seed != lastSeed)
            {
                lastSeed = seed;
                var provider = Bodies.Provider as BodyConfigProvider;
                if (provider != null) provider.Reset();
                built.Clear();
                // A seed change rebuilds every state; a rebuild asked for in the same tick is part of it.
                string why = rebuildAsked ? rebuildReason + " (seed " + seed + ")" : "seed " + seed;
                rebuildAsked = false;
                WeatherRebuild.Begin(why, StateIndexes());
            }
            else if (rebuildAsked)
            {
                rebuildAsked = false;
                BeginRebuild(rebuildReason);
            }
            field.SetKnobs(AtmospheresSettings.LightWeatherFrequency, AtmospheresSettings.HeavyShare);
            Planetarium p = Planetarium.fetch;
            if (p != null) field.Observe(Planetarium.GetUniversalTime());
            long start = Stopwatch.GetTimestamp();
            BuildNext();
            if (WeatherRebuild.Active) WeatherRebuild.AddFrame((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }

        private List<int> StateIndexes()
        {
            var list = new List<int>();
            foreach (GeneratorState s in field.AllStates()) list.Add(s.BodyIndex);
            return list;
        }

        // The rebuild of one body (the camera's) or of all that have a state: their entries are forgotten so that Step makes them again; the states stay in the field meanwhile.
        private void BeginRebuild(string reason)
        {
            var due = new List<int>();
            CelestialBody first = BodyWatcher.ActiveBody;
            if (first != null && field.StateOf(first.flightGlobalsIndex) != null) due.Add(first.flightGlobalsIndex);
            else due = StateIndexes();
            foreach (int index in due)
            {
                built.Remove(index);
                creating.Remove(index);
            }
            WeatherRebuild.Begin(reason, due);
            AtmospheresLog.Say("generator: rebuild asked (" + reason + "), " + due.Count + " body, test levers: " + GeneratorViewerOverrides.Describe());
        }

        public void Shutdown()
        {
            Uninstall();
            if (field != null) field.Maps.Stop();
            field = null;
            Current = null;
        }

        private void Install()
        {
            if (installed) return;
            if (!(WeatherHub.Field is NullField)) return;
            WeatherHub.Field = field;
            installed = true;
            AtmospheresLog.Say("weather generator installed (a pure function of body, seed, place and time)");
        }

        private void Uninstall()
        {
            if (!installed) return;
            if (ReferenceEquals(WeatherHub.Field, field)) WeatherHub.Field = null;
            installed = false;
        }

        // One body per frame: the one that is in the way of the weather first (the main body of the camera), then the rest.
        private void BuildNext()
        {
            List<CelestialBody> bodies = GameGlobals.Bodies;
            if (bodies == null) return;
            CelestialBody first = BodyWatcher.ActiveBody;
            if (first != null && Step(first)) return;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody b = bodies[i];
                if (b != null && b != first && Step(b)) return;
            }
        }

        // Advances one body by a step; true where it did work this frame (a raster read, a build), so that the frame is spent.
        private bool Step(CelestialBody body)
        {
            BodyClimate c = Bodies.Get(body);
            int index = body.flightGlobalsIndex;
            if (c == null || !c.Status.WeatherRuns || !c.Generator.Enabled)
            {
                if (field.StateOf(index) != null) field.RemoveState(index);
                built.Remove(index);
                creating.Remove(index);
                WeatherRebuild.Finished(index, 0.0, false);
                return false;
            }
            Creating pending;
            if (creating.TryGetValue(index, out pending)) return Finish(index, pending);
            Built b;
            if (built.TryGetValue(index, out b) && ReferenceEquals(b.Climate, c) && (field.StateOf(index) != null || field.IsBroken(index))) return false;
            if (b == null || !ReferenceEquals(b.Climate, c))
            {
                b = new Built { Climate = c };
                built[index] = b;
            }

            float[] heights = null;
            if (GeneratorBodies.NeedsHeights(c))
            {
                GeneratorRaster raster;
                if (!rasters.TryGetValue(index, out raster))
                {
                    raster = new GeneratorRaster();
                    rasters[index] = raster;
                }
                raster.Sample(body);
                if (!raster.Complete) return raster.Progress > 0.0;
                heights = raster.HeightsIfComplete;
            }

            var notes = new List<string>();
            bool rebuild = WeatherRebuild.IsDue(index);
            string thermalNote;
            ThermalFit fit;
            if (rebuild)
            {
                // A rebuild asks the game's thermal code in slices of RebuildThermalSliceMs, one slice per frame, so that no frame of it takes the main thread past the rebuild's budget.
                if (b.Thermal == null) b.Thermal = new ThermalProbe.Session(body);
                if (!b.Thermal.Run(RebuildThermalSliceMs)) return true;
                thermalNote = b.Thermal.Note;
                fit = ThermalFit.From(b.Thermal.Result);
                b.Thermal = null;
            }
            else
            {
                fit = ThermalFit.From(ThermalProbe.Read(body, out thermalNote));
            }
            long factsStart = Stopwatch.GetTimestamp();
            BodyFacts facts = BodyProbe.Read(body, null, notes);
            if (rebuild) AtmospheresLog.Say("generator: " + c.Name + " rebuild, main thread: facts " + ((Stopwatch.GetTimestamp() - factsStart) * 1000.0 / Stopwatch.Frequency).ToString("0.0") + " ms");
            if (fit == null && ++b.ThermalTries < ThermalTries) return true;
            if (fit == null) notes.Add("temperature fit: " + (thermalNote.Length > 0 ? thermalNote : "the table could not be fitted") + "; the target curve of the body is used");

            if (rebuild)
            {
                // A rebuild: the main thread has read the game (the facts, the thermal table, a copy of the viewer's test levers); the parameter set (with the height grid) and the state are made
                // on a worker (neither touches a game object) and swapped in by a later frame.
                var made = new Creating { Climate = c, Notes = notes };
                ulong seed = GeneratorBodies.SeedOf(c);
                float[] bands = c.Precip.BandRatesStated ? c.Precip.BandRatesMmPerH : null;
                PrecipType dust = GeneratorBodies.DustType(c);
                string name = c.Name;
                Dictionary<string, double> levers = GeneratorViewerOverrides.Snapshot();
                made.Task = Task.Run(() =>
                {
                    long t0 = Stopwatch.GetTimestamp();
                    string err;
                    BodyParams made2 = GeneratorBodies.Build(c, facts, heights, fit, notes, levers);
                    GeneratorState result = GeneratorState.Create(index, name, made2, seed, bands, dust, out err);
                    made.Error = err;
                    made.WorkerMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    return result;
                });
                creating[index] = made;
                return true;
            }
            BodyParams parameters = GeneratorBodies.Build(c, facts, heights, fit, notes);
            string error;
            GeneratorState state = GeneratorState.Create(index, c.Name, parameters, GeneratorBodies.SeedOf(c), c.Precip.BandRatesStated ? c.Precip.BandRatesMmPerH : null,
                GeneratorBodies.DustType(c), out error);
            foreach (string note in notes) AtmospheresLog.Warn("generator", c.Name + ": " + note);
            if (state == null)
            {
                field.MarkBroken(index, error);
                return true;
            }
            state.BodyFrequency = (float)c.PlayerFrequency;
            field.SetState(state);
            AtmospheresLog.Say("generator: " + c.Name + " ready (" + GeneratorBodies.Describe(parameters) + ", definition " + state.DefinitionId.ToString("X16") + ")");
            return true;
        }

        // The state a worker made, swapped in when it is done (the old one served until now); false while it is still being made.
        private bool Finish(int index, Creating made)
        {
            if (!made.Task.IsCompleted) return false;
            creating.Remove(index);
            GeneratorState state = made.Task.IsFaulted ? null : made.Task.Result;
            foreach (string note in made.Notes) AtmospheresLog.Warn("generator", made.Climate.Name + ": " + note);
            if (state == null)
            {
                field.MarkBroken(index, made.Error ?? "the worker failed");
                WeatherRebuild.Finished(index, made.WorkerMs, false);
                return true;
            }
            state.BodyFrequency = (float)made.Climate.PlayerFrequency;
            field.SetState(state);
            built[index] = new Built { Climate = made.Climate };
            WeatherRebuild.Finished(index, made.WorkerMs, true);
            AtmospheresLog.Say("generator: " + made.Climate.Name + " rebuilt (worker " + made.WorkerMs.ToString("0.0") + " ms, definition " + state.DefinitionId.ToString("X16") + ")");
            return true;
        }
    }
}
