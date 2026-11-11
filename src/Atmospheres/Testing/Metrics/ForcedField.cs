using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Precomputed checkpoints for the phases of the year: what the K-series ships so that a forced phase does not run a year of
    // simulation. Phase 0 to 1 within one body year; each entry holds the state of the field at that phase.
    internal interface ICheckpointStore
    {
        // The checkpoints of the store as (phase 0..1, bytes), ordered by phase.
        IList<KeyValuePair<double, byte[]>> Entries { get; }
    }

    // A store held in memory.
    internal sealed class MemoryCheckpointStore : ICheckpointStore
    {
        private readonly List<KeyValuePair<double, byte[]>> entries = new List<KeyValuePair<double, byte[]>>();

        public IList<KeyValuePair<double, byte[]>> Entries { get { return entries; } }

        internal void Add(double phase, byte[] bytes)
        {
            entries.Add(new KeyValuePair<double, byte[]>(phase, bytes));
            entries.Sort((a, b) => a.Key.CompareTo(b.Key));
        }
    }

    // What ForcePhase did: the game time it set, the phase of the checkpoint it loaded, the milliseconds the load and the catch-up to
    // Ready took (the K-series reports them per phase), and whether the field is Ready.
    internal struct PhaseResult
    {
        internal double Ut;
        internal double LoadedPhase;
        internal double CatchUpMs;
        internal int AdvanceCalls;
        internal bool Loaded;
        internal bool Ready;
    }

    // A test decorator around a weather field. It forwards everything to the real field, and a test can force what the field says:
    // the precipitation level and type, the game time, the place, and the phase of the year (through a checkpoint restore). It
    // forwards ISurfaceForcing to the real field where the real field has it and does nothing where it has not. It does not expose
    // the checkpoint writer of the field, so a forced run never writes side files; ForcedField.Active is true while any decorator
    // exists and the writers of side files ask AllowSideFileWrite before they write.
    internal sealed class ForcedField : IWeatherField, ISurfaceForcing
    {
        private static int attached;
        private readonly IWeatherField inner;

        internal ForcedField(IWeatherField inner, int seed = 0)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            this.inner = inner;
            Seed = seed;
            attached++;
        }

        // True while a decorator is attached (until Detach).
        internal static bool Active { get { return attached > 0; } }

        // Side files are written only when no forced run is going.
        internal static bool AllowSideFileWrite()
        {
            return attached == 0;
        }

        internal void Detach()
        {
            if (attached > 0) attached--;
        }

        // Forgets every attached decorator (a test's clean-up).
        internal static void DetachAll()
        {
            attached = 0;
        }

        internal IWeatherField Inner { get { return inner; } }

        // The forced values; null / NaN where the field's own value stands.
        internal float? Level01;
        internal PrecipType? Type;
        internal double? Ut;
        internal double? LatDeg, LonDeg, AltM;
        // The seed the run states in its sidecar (tools/atmo_ab.py compares it between A and B); the field is built with it.
        internal int Seed { get; private set; }
        // How many disc calls were forwarded and how many were dropped because the real field cannot take them.
        internal int ForwardedForcing { get; private set; }
        internal int DroppedForcing { get; private set; }

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            bool covered = inner.Sample(bodyIndex, LatDeg ?? latDeg, LonDeg ?? lonDeg, AltM ?? altM, Ut ?? ut, out sample);
            if (Level01.HasValue)
            {
                sample.Level01 = Level01.Value;
                if (Level01.Value <= 0f)
                {
                    sample.Type = PrecipType.None;
                    sample.SnowFraction = sample.SleetFraction = sample.RainFraction = 0f;
                }
            }
            if (Type.HasValue && sample.Level01 > 0f)
            {
                sample.Type = Type.Value;
                sample.SnowFraction = Type.Value == PrecipType.Snow ? 1f : 0f;
                sample.SleetFraction = Type.Value == PrecipType.Sleet ? 1f : 0f;
                sample.RainFraction = Type.Value == PrecipType.Rain ? 1f : 0f;
            }
            return covered;
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
        {
            return inner.GetMap(bodyIndex, kind, Ut ?? ut, target);
        }

        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target)
        {
            return inner.FillWindow(bodyIndex, LatDeg ?? centreLatDeg, LonDeg ?? centreLonDeg, radiusKm, Ut ?? ut, target);
        }

        public void Advance(double budgetMs)
        {
            inner.Advance(budgetMs);
        }

        public long Version { get { return inner.Version; } }
        public long DefinitionId { get { return inner.DefinitionId; } }
        public bool ThreadSafe { get { return inner.ThreadSafe; } }
        public double StepSeconds { get { return inner.StepSeconds; } }
        public FieldReadiness Readiness { get { return inner.Readiness; } }

        // Forwards to the real field if it takes a forcing; otherwise does nothing (and counts it).
        public void AddPrecipitationDisc(int bodyIndex, double latDeg, double lonDeg, double radiusKm, float rateMmPerH, PrecipType type, double fromUt, double toUt)
        {
            var forcing = inner as ISurfaceForcing;
            if (forcing == null)
            {
                DroppedForcing++;
                return;
            }
            forcing.AddPrecipitationDisc(bodyIndex, latDeg, lonDeg, radiusKm, rateMmPerH, type, fromUt, toUt);
            ForwardedForcing++;
        }

        // The game time of a phase of the current year: the start of the year that holds currentUt plus phase times the year.
        internal static double UtOfPhase(double phase01, double yearSeconds, double currentUt)
        {
            if (yearSeconds <= 0) throw new ArgumentOutOfRangeException("yearSeconds");
            double yearStart = Math.Floor(currentUt / yearSeconds) * yearSeconds;
            return yearStart + phase01 * yearSeconds;
        }

        // Sets the game time to the phase of the current year through a checkpoint restore: loads the latest checkpoint of the store at
        // or before the phase into the field (the field must be an ICheckpointedField), then lets the field advance until it is Ready
        // or maxCatchUpMs have passed. The forced time becomes the target UT. A field that cannot be restored leaves Loaded false.
        internal PhaseResult ForcePhase(double phase01, double yearSeconds, double currentUt, ICheckpointStore store, double maxCatchUpMs = 20000, double sliceMs = 5)
        {
            var result = new PhaseResult { Ut = UtOfPhase(phase01, yearSeconds, currentUt) };
            var checkpointed = inner as ICheckpointedField;
            if (checkpointed == null || store == null) return result;
            KeyValuePair<double, byte[]>? best = null;
            foreach (KeyValuePair<double, byte[]> entry in store.Entries)
                if (entry.Key <= phase01 + 1e-9) best = entry;
            if (!best.HasValue) return result;
            long start = Stopwatch.GetTimestamp();
            double yearStart = Math.Floor(currentUt / yearSeconds) * yearSeconds;
            using (var stream = new MemoryStream(best.Value.Value, false))
                result.Loaded = checkpointed.LoadCheckpoint(stream, yearStart + best.Value.Key * yearSeconds);
            result.LoadedPhase = best.Value.Key;
            while (result.Loaded && checkpointed.Readiness != FieldReadiness.Ready)
            {
                double spent = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (spent > maxCatchUpMs) break;
                checkpointed.Advance(sliceMs);
                result.AdvanceCalls++;
            }
            result.CatchUpMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            result.Ready = checkpointed.Readiness == FieldReadiness.Ready;
            if (result.Loaded) Ut = result.Ut;
            return result;
        }
    }
}
