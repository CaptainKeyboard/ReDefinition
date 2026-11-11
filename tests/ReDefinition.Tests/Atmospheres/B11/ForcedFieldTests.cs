using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.B11
{
    // The test decorator of the weather field: forwarding of the surface forcing (and doing nothing where the field cannot take it),
    // forced level, type, time and place, no side files while it is attached, and the phase of the year through a checkpoint restore.
    [TestClass]
    public class ForcedFieldTests
    {
        // A field that records what it was asked and can take a surface forcing.
        private class FakeField : IWeatherField
        {
            internal double LastUt, LastLat, LastLon, LastAlt;
            internal float BaseLevel = 0.5f;
            internal long Ver = 1;

            public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
            {
                LastLat = latDeg;
                LastLon = lonDeg;
                LastAlt = altM;
                LastUt = ut;
                sample = WeatherSample.Clear(bodyIndex);
                sample.Level01 = BaseLevel;
                sample.Type = PrecipType.Rain;
                sample.RainFraction = 1f;
                return true;
            }

            public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target) { LastUt = ut; return false; }
            public bool FillWindow(int bodyIndex, double a, double b, double radiusKm, double ut, WeatherWindow target) { LastUt = ut; return false; }
            public virtual void Advance(double budgetMs) { }
            public long Version { get { return Ver; } }
            public long DefinitionId { get { return 77; } }
            public bool ThreadSafe { get { return true; } }
            public double StepSeconds { get { return 3600.0; } }
            public virtual FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
        }

        private sealed class ForcingField : FakeField, ISurfaceForcing
        {
            internal readonly List<string> Discs = new List<string>();

            public void AddPrecipitationDisc(int bodyIndex, double latDeg, double lonDeg, double radiusKm, float rateMmPerH, PrecipType type, double fromUt, double toUt)
            {
                Discs.Add(bodyIndex + ":" + radiusKm + ":" + rateMmPerH + ":" + type);
            }
        }

        // A field with a state (a number), checkpoints of it, and a catch-up of a few advances after a restore.
        private sealed class CheckpointedField : FakeField, ICheckpointedField
        {
            internal int State;
            internal double LoadedUt = double.NaN;
            internal int PendingAdvances;
            internal int WriteCalls;
            internal int AdvancesAfterLoad = 3;

            public override FieldReadiness Readiness { get { return PendingAdvances > 0 ? FieldReadiness.CatchingUp : FieldReadiness.Ready; } }

            public void SaveCheckpoint(Stream stream)
            {
                WriteCalls++;
                stream.Write(BitConverter.GetBytes(State), 0, 4);
            }

            public bool LoadCheckpoint(Stream stream, double ut)
            {
                var bytes = new byte[4];
                if (stream.Read(bytes, 0, 4) != 4) return false;
                State = BitConverter.ToInt32(bytes, 0);
                LoadedUt = ut;
                PendingAdvances = AdvancesAfterLoad;
                return true;
            }

            public double CheckpointIntervalSeconds { get { return 86400.0; } }

            public override void Advance(double budgetMs)
            {
                if (PendingAdvances > 0) PendingAdvances--;
            }
        }

        [TestCleanup]
        public void Detach()
        {
            ForcedField.DetachAll();
        }

        [TestMethod]
        public void The_surface_forcing_is_forwarded_to_a_field_that_takes_it()
        {
            var inner = new ForcingField();
            var forced = new ForcedField(inner);
            forced.AddPrecipitationDisc(1, 10.0, 20.0, 5.0, 3f, PrecipType.Snow, 0.0, 100.0);
            Assert.AreEqual(1, inner.Discs.Count);
            Assert.AreEqual("1:5:3:Snow", inner.Discs[0]);
            Assert.AreEqual(1, forced.ForwardedForcing);
            forced.Detach();
        }

        [TestMethod]
        public void The_surface_forcing_does_nothing_where_the_field_cannot_take_it()
        {
            var forced = new ForcedField(new FakeField());
            forced.AddPrecipitationDisc(1, 10.0, 20.0, 5.0, 3f, PrecipType.Snow, 0.0, 100.0);
            Assert.AreEqual(0, forced.ForwardedForcing);
            Assert.AreEqual(1, forced.DroppedForcing);
            forced.Detach();
        }

        [TestMethod]
        public void Reads_forward_unchanged_and_forced_values_replace_what_the_field_says()
        {
            var inner = new FakeField();
            var forced = new ForcedField(inner, 4242);
            WeatherSample s;
            Assert.IsTrue(forced.Sample(2, 10.0, 20.0, 30.0, 1000.0, out s));
            Assert.AreEqual(0.5f, s.Level01);
            Assert.AreEqual(PrecipType.Rain, s.Type);
            Assert.AreEqual(1000.0, inner.LastUt);
            Assert.AreEqual(77L, forced.DefinitionId);
            Assert.AreEqual(3600.0, forced.StepSeconds);
            Assert.AreEqual(4242, forced.Seed);

            forced.Level01 = 0.9f;
            forced.Type = PrecipType.Snow;
            forced.Ut = 5000.0;
            forced.LatDeg = 45.0;
            forced.LonDeg = -10.0;
            forced.AltM = 120.0;
            forced.Sample(2, 10.0, 20.0, 30.0, 1000.0, out s);
            Assert.AreEqual(0.9f, s.Level01);
            Assert.AreEqual(PrecipType.Snow, s.Type);
            Assert.AreEqual(1f, s.SnowFraction);
            Assert.AreEqual(0f, s.RainFraction);
            Assert.AreEqual(5000.0, inner.LastUt);
            Assert.AreEqual(45.0, inner.LastLat);
            Assert.AreEqual(-10.0, inner.LastLon);
            Assert.AreEqual(120.0, inner.LastAlt);

            forced.Level01 = 0f;
            forced.Sample(2, 10.0, 20.0, 30.0, 1000.0, out s);
            Assert.AreEqual(PrecipType.None, s.Type);
            forced.Detach();
        }

        [TestMethod]
        public void A_forced_run_never_writes_side_files()
        {
            Assert.IsTrue(ForcedField.AllowSideFileWrite());
            var inner = new CheckpointedField();
            var forced = new ForcedField(inner);
            Assert.IsFalse(ForcedField.AllowSideFileWrite());
            Assert.IsFalse(typeof(ICheckpointedField).IsAssignableFrom(typeof(ForcedField)), "the decorator does not expose the checkpoint writer");
            forced.Detach();
            Assert.IsTrue(ForcedField.AllowSideFileWrite());
            Assert.AreEqual(0, inner.WriteCalls);
        }

        private static MemoryCheckpointStore Store(params double[] phases)
        {
            var store = new MemoryCheckpointStore();
            foreach (double p in phases) store.Add(p, BitConverter.GetBytes((int)Math.Round(p * 1000)));
            return store;
        }

        [TestMethod]
        public void Force_phase_restores_the_checkpoint_of_the_phase_and_catches_up_to_ready()
        {
            var inner = new CheckpointedField();
            var forced = new ForcedField(inner);
            double year = 426.0 * 21600.0;
            double now = 3.4 * year;
            PhaseResult r = forced.ForcePhase(0.5, year, now, Store(0.0, 0.25, 0.5, 0.75));
            Assert.IsTrue(r.Loaded && r.Ready);
            Assert.AreEqual(500, inner.State);
            Assert.AreEqual(0.5, r.LoadedPhase);
            Assert.AreEqual(3 * year + 0.5 * year, r.Ut, 1e-6);
            Assert.AreEqual(r.Ut, inner.LoadedUt, 1e-6);
            Assert.AreEqual(3, r.AdvanceCalls);
            Assert.IsTrue(r.CatchUpMs >= 0.0);
            Assert.AreEqual(r.Ut, forced.Ut.Value, 1e-6);
            // The restored state answers at the forced time.
            WeatherSample s;
            forced.Sample(1, 0, 0, 0, now, out s);
            Assert.AreEqual(r.Ut, inner.LastUt, 1e-6);
            Assert.AreEqual(0, inner.WriteCalls);
            forced.Detach();
        }

        [TestMethod]
        public void Force_phase_takes_the_latest_checkpoint_at_or_before_the_phase()
        {
            var inner = new CheckpointedField();
            var forced = new ForcedField(inner);
            PhaseResult r = forced.ForcePhase(0.6, 1000.0, 2500.0, Store(0.0, 0.25, 0.5, 0.75));
            Assert.IsTrue(r.Loaded);
            Assert.AreEqual(0.5, r.LoadedPhase);
            Assert.AreEqual(2600.0, r.Ut, 1e-9);
            Assert.AreEqual(2500.0, inner.LoadedUt, 1e-9);
            forced.Detach();
        }

        [TestMethod]
        public void Force_phase_leaves_the_field_alone_where_it_cannot_be_restored()
        {
            var forced = new ForcedField(new CheckpointedField());
            PhaseResult early = forced.ForcePhase(0.1, 1000.0, 0.0, Store(0.25, 0.5));
            Assert.IsFalse(early.Loaded);
            Assert.IsFalse(forced.Ut.HasValue);
            PhaseResult plain = new ForcedField(new FakeField()).ForcePhase(0.5, 1000.0, 0.0, Store(0.5));
            Assert.IsFalse(plain.Loaded);
            Assert.AreEqual(500.0, plain.Ut, 1e-9);
        }

        [TestMethod]
        public void Force_phase_stops_at_the_catch_up_limit()
        {
            var inner = new CheckpointedField { AdvancesAfterLoad = int.MaxValue };
            var forced = new ForcedField(inner);
            PhaseResult r = forced.ForcePhase(0.5, 1000.0, 0.0, Store(0.5), 20.0, 1.0);
            Assert.IsTrue(r.Loaded);
            Assert.IsFalse(r.Ready);
            Assert.IsTrue(r.CatchUpMs >= 20.0);
            forced.Detach();
        }

        [TestMethod]
        public void The_phase_ut_is_in_the_current_year()
        {
            Assert.AreEqual(250.0, ForcedField.UtOfPhase(0.25, 1000.0, 10.0), 1e-9);
            Assert.AreEqual(2250.0, ForcedField.UtOfPhase(0.25, 1000.0, 2999.0), 1e-9);
        }
    }
}
