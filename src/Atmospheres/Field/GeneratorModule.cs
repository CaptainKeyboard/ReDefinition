using System;
using System.Collections.Generic;
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
        }

        private GeneratorField field;
        private readonly Dictionary<int, GeneratorRaster> rasters = new Dictionary<int, GeneratorRaster>();
        private readonly Dictionary<int, Built> built = new Dictionary<int, Built>();
        private int lastSeed = int.MinValue;
        private bool installed;

        // The field of the running module, or null before Init and after Shutdown.
        internal static GeneratorField Current { get; private set; }

        public void Init()
        {
            field = new GeneratorField();
            field.Maps.Start();
            Current = field;
            lastSeed = AtmospheresSettings.GenSeed;
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
            }
            field.SetKnobs(AtmospheresSettings.LightWeatherFrequency, AtmospheresSettings.HeavyShare);
            Planetarium p = Planetarium.fetch;
            if (p != null) field.Observe(Planetarium.GetUniversalTime());
            BuildNext();
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
                return false;
            }
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
            BodyFacts facts = BodyProbe.Read(body, null, notes);
            string thermalNote;
            ThermalFit fit = ThermalFit.From(ThermalProbe.Read(body, out thermalNote));
            if (fit == null && ++b.ThermalTries < ThermalTries) return true;
            if (fit == null) notes.Add("temperature fit: " + (thermalNote.Length > 0 ? thermalNote : "the table could not be fitted") + "; the target curve of the body is used");

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
    }
}
