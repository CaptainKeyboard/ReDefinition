using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.B12;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // D30: the delivery of alerts, as a state machine against fakes: one message per event, no repeat, the summary of a jump of the time
    // warp, the warp stop for the active vessel only, the suppression while the forecast is not usable, and each switch on its own.
    [TestClass]
    public class AlertDeliveryTests
    {
        private const double H = D30Fixtures.H;

        private static ForecastAlert Alert(AlertKind kind, double ut, string name = "Pad", PrecipBand band = PrecipBand.Heavy, double eta = 0.0)
        {
            ForecastSite site = D30Fixtures.Site(name);
            return new ForecastAlert { Kind = kind, Ut = ut, EtaSeconds = eta, Band = band, Site = site, Text = "Heavy snow " + kind + " " + name };
        }

        private static AlertDelivery Make(FakeSink sink, AlertDeliverySettings settings = null, Func<bool> ready = null)
        {
            settings = settings ?? new AlertDeliverySettings();
            return new AlertDelivery(sink, () => settings, ready ?? (() => true));
        }

        [TestMethod]
        public void OneMessageForOneEventAndNoRepeatOfTheSameEvent()
        {
            var sink = new FakeSink();
            AlertDelivery delivery = Make(sink);
            delivery.Receive(Alert(AlertKind.Arrived, 10 * H));
            Assert.AreEqual(1, delivery.Flush(10 * H).Count);
            Assert.AreEqual(1, sink.Screen.Count);
            Assert.AreEqual(1, sink.App.Count);
            delivery.Receive(Alert(AlertKind.Arrived, 10 * H));
            Assert.AreEqual(0, delivery.Flush(10 * H + 5).Count, "The same event is not delivered twice.");
            Assert.AreEqual(1, sink.Screen.Count);
            Assert.AreEqual(0, delivery.Flush(11 * H).Count, "Nothing queued, nothing delivered.");
        }

        [TestMethod]
        public void AJumpOverASystemsEtaIsOneMessageThatSaysItWasPassedDuringWarp()
        {
            const double lead = 3.0;
            var field = new ScriptedField { Level = ut => ut >= 10 * H && ut < 16 * H ? 0.7f : 0f };
            var rules = new AlertRules(lead, PrecipBand.Heavy, 0f);
            var evaluator = new AlertEvaluator(rules);
            var ctx = new ForecastContext(new ForecastRules(H, 24.0), 0.0, alerts: rules);
            ForecastReport report = ForecastEngine.Compute(D30Fixtures.Site(), 0.0, 24.0, ctx, field);
            var sink = new FakeSink();
            AlertDelivery delivery = Make(sink);
            foreach (double now in new[] { 0.0, 1 * H })
            {
                foreach (ForecastAlert a in evaluator.Tick(D30Fixtures.Site(), false, report, now).Alerts) delivery.Receive(a);
                Assert.AreEqual(0, delivery.Flush(now).Count);
            }
            AlertTick jump = evaluator.Tick(D30Fixtures.Site(), false, report, 13 * H);
            Assert.AreEqual(2, jump.Alerts.Count, "The engine's evaluator gives an Approaching and an Arrived for the jump.");
            foreach (ForecastAlert a in jump.Alerts) delivery.Receive(a);
            List<DeliveredMessage> made = delivery.Flush(13 * H);
            Console.WriteLine("D30 jump: " + made.Count + " message: " + made[0].Text);
            Assert.AreEqual(1, made.Count);
            Assert.IsTrue(made[0].Summary);
            StringAssert.Contains(made[0].Text, "passed during time warp");
            StringAssert.Contains(made[0].Text, "approaching");
            StringAssert.Contains(made[0].Text, "arrived");
            Assert.AreEqual(1, sink.Screen.Count);
            Assert.AreEqual(1, sink.App.Count);
            // The evaluator remembers the events and the delivery the messages: nothing more after the jump or a repeated tick.
            foreach (ForecastAlert a in evaluator.Tick(D30Fixtures.Site(), false, report, 13 * H).Alerts) delivery.Receive(a);
            Assert.AreEqual(0, delivery.Flush(13 * H).Count);
            Assert.AreEqual(1, delivery.Log.Count);
        }

        [TestMethod]
        public void AReloadForgetsTheMessagesOfEventsThatLieAheadAgain()
        {
            var sink = new FakeSink();
            AlertDelivery delivery = Make(sink);
            delivery.Flush(100 * H);
            delivery.Receive(Alert(AlertKind.Arrived, 101 * H));
            Assert.AreEqual(1, delivery.Flush(101 * H).Count);
            // Loading an earlier save: the event is in the future again, and when it comes it is delivered again.
            Assert.AreEqual(0, delivery.Flush(90 * H).Count);
            delivery.Receive(Alert(AlertKind.Arrived, 101 * H));
            Assert.AreEqual(1, delivery.Flush(101 * H).Count);
            Assert.AreEqual(2, sink.Screen.Count);
        }

        [TestMethod]
        public void TwoSitesAreTwoMessagesAndEachEventKindHasItsOwnKey()
        {
            var sink = new FakeSink();
            AlertDelivery delivery = Make(sink);
            delivery.Receive(Alert(AlertKind.Arrived, 10 * H, "Pad"));
            delivery.Receive(Alert(AlertKind.Arrived, 10 * H, "Other"));
            // The two sites differ in name only here; the key is the place, so give the second a place of its own.
            Assert.AreEqual(1, delivery.Flush(10 * H).Count);
            var other = Alert(AlertKind.Arrived, 10 * H, "Other");
            other.Site.LatDeg = -10.0;
            delivery.Receive(other);
            Assert.AreEqual(1, delivery.Flush(10 * H).Count);
            delivery.Receive(Alert(AlertKind.Clearing, 10 * H + 600, "Pad"));
            Assert.AreEqual(1, delivery.Flush(10 * H + 600).Count);
            Assert.AreEqual(3, sink.Screen.Count);
        }

        [TestMethod]
        public void TheWarpStopIsForAnApproachingAlertOfTheActiveVesselsSiteAndOnlyWhenOnAndRunning()
        {
            var sink = new FakeSink { Running = true, ActiveSite = s => s.Name == "Pad" };
            var settings = new AlertDeliverySettings { WarpStop = true };
            AlertDelivery delivery = Make(sink, settings);
            delivery.Receive(Alert(AlertKind.Arrived, 10 * H));
            delivery.Flush(10 * H);
            Assert.AreEqual(0, sink.WarpStops, "Only Approaching stops the warp.");
            delivery.Receive(Alert(AlertKind.Approaching, 11 * H, "Pad", PrecipBand.Heavy, 3 * H));
            List<DeliveredMessage> made = delivery.Flush(11 * H);
            Assert.AreEqual(1, sink.WarpStops);
            Assert.IsTrue(made[0].WarpStopped);
            // The warp is stopped now: no second stop.
            var distant = Alert(AlertKind.Approaching, 12 * H, "Distant", PrecipBand.Heavy, 3 * H);
            distant.Site.LatDeg = 5.0;
            sink.Running = true;
            delivery.Receive(distant);
            delivery.Flush(12 * H);
            Assert.AreEqual(1, sink.WarpStops, "An alert of another site does not stop the vessel's warp.");
            settings.WarpStop = false;
            var third = Alert(AlertKind.Approaching, 13 * H, "Pad", PrecipBand.Light, 3 * H);
            delivery.Receive(third);
            delivery.Flush(13 * H);
            Assert.AreEqual(1, sink.WarpStops, "The switch is off.");
            settings.WarpStop = true;
            sink.Running = false;
            var fourth = Alert(AlertKind.Approaching, 14 * H, "Pad", PrecipBand.Moderate, 3 * H);
            delivery.Receive(fourth);
            delivery.Flush(14 * H);
            Assert.AreEqual(1, sink.WarpStops, "No warp runs.");
        }

        // The evaluator ticks at most every 2 s real, so at a high warp rate its Approaching alert comes long after the time it is stamped
        // at; the guard in front of it stops the warp before that time (review R3).
        [TestMethod]
        public void TheGuardStopsAHighWarpBeforeTheApproachTimeWhereTheEvaluatorsTickComesLate()
        {
            const double lead = 3.0, rate = 20000.0, frame = 1.0 / 60.0, tickRealSeconds = 2.0;
            var field = new ScriptedField { Level = t => t >= 10 * H && t < 16 * H ? 0.7f : 0f };
            var rules = new AlertRules(lead, PrecipBand.Heavy, 0f);
            var evaluator = new AlertEvaluator(rules);
            var ctx = new ForecastContext(new ForecastRules(H, 24.0), 0.0, alerts: rules);
            ForecastReport report = ForecastEngine.Compute(D30Fixtures.Site(), 0.0, 24.0, ctx, field);
            ForecastSite site = D30Fixtures.Site();

            // Without the guard: the warp runs, the evaluator ticks every 2 s real, and the alert comes after the time it is stamped at.
            double ut = 0.0, sinceTick = tickRealSeconds;
            ForecastAlert late = default(ForecastAlert);
            bool found = false;
            while (!found && ut < 24 * H)
            {
                ut += rate * frame;
                sinceTick += frame;
                if (sinceTick < tickRealSeconds) continue;
                sinceTick = 0.0;
                foreach (ForecastAlert a in evaluator.Tick(site, false, report, ut).Alerts)
                {
                    if (a.Kind != AlertKind.Approaching) continue;
                    late = a;
                    found = true;
                }
            }
            Assert.IsTrue(found);
            double arrival = late.Ut + late.EtaSeconds;
            double stamp = arrival - lead * H;
            Console.WriteLine("D30 warp guard: arrival " + (arrival / H).ToString("0.00") + " h, stamp " + (stamp / H).ToString("0.00") + " h, evaluator's alert at "
                              + (ut / H).ToString("0.00") + " h at " + rate + "x");
            Assert.IsTrue(ut > stamp + 3600.0, "The evaluator's tick comes more than an hour after the stamp at this rate: " + ut / H);

            // With the guard on every frame: the warp stops before the stamp, within one second of rate of it, and only once.
            var sink = new FakeSink { Running = true, Rate = rate, ActiveSite = s => true };
            AlertDelivery delivery = Make(sink, new AlertDeliverySettings { WarpStop = true });
            ut = 0.0;
            while (sink.Running && ut < 24 * H)
            {
                ut += rate * frame;
                delivery.GuardWarp(ut, site, arrival, lead);
            }
            Assert.AreEqual(1, sink.WarpStops);
            Assert.IsTrue(ut <= stamp, "The warp stopped before the stamp: " + ut / H + " h against " + stamp / H);
            Assert.IsTrue(stamp - ut <= rate * AlertDelivery.GuardRealSeconds, "and within one second of warp of it: " + (stamp - ut));

            // The player warps on: no second stop for the same storm, and none after the stamp.
            sink.Running = true;
            Assert.IsFalse(delivery.GuardWarp(ut + 10, site, arrival, lead));
            Assert.IsFalse(delivery.GuardWarp(stamp + 10, site, arrival, lead));
            Assert.AreEqual(1, sink.WarpStops);
            // The Approaching alert that follows for the same storm does not stop it again either.
            delivery.Receive(late);
            delivery.Flush(late.Ut);
            Assert.AreEqual(1, sink.WarpStops);
        }

        [TestMethod]
        public void TheGuardStopsNothingForADistantSiteASwitchedOffStopOrAStormInsideTheLeadAndRearmsAfterAReload()
        {
            const double lead = 3.0, arrival = 20 * H, stamp = arrival - lead * H;
            var sink = new FakeSink { Running = true, Rate = 100000.0, ActiveSite = s => false };
            var settings = new AlertDeliverySettings { WarpStop = true };
            AlertDelivery delivery = Make(sink, settings);
            ForecastSite site = D30Fixtures.Site();
            Assert.IsFalse(delivery.GuardWarp(stamp - 10, site, arrival, lead), "Not the active vessel's site.");
            sink.ActiveSite = s => true;
            settings.WarpStop = false;
            Assert.IsFalse(delivery.GuardWarp(stamp - 10, site, arrival, lead), "The switch is off.");
            settings.WarpStop = true;
            sink.Rate = 1000.0;
            Assert.IsFalse(delivery.GuardWarp(stamp - 10 * H, site, arrival, lead), "Ten hours from the stamp at 1000x is far away.");
            Assert.AreEqual(0, sink.WarpStops);
            Assert.IsTrue(delivery.GuardWarp(stamp - 500.0, site, arrival, lead));
            Assert.AreEqual(1, sink.WarpStops);
        }

        [TestMethod]
        public void TheGuardDoesNotStopWhenTheStormIsAlreadyInsideTheLeadOrWhenNoWarpRuns()
        {
            const double lead = 3.0, arrival = 20 * H, stamp = arrival - lead * H;
            var sink = new FakeSink { Running = true, Rate = 1000.0, ActiveSite = s => true };
            AlertDelivery delivery = Make(sink, new AlertDeliverySettings { WarpStop = true });
            ForecastSite site = D30Fixtures.Site();
            Assert.IsFalse(delivery.GuardWarp(stamp - 5000.0, site, arrival, lead), "5000 s from the stamp at 1000x is five seconds: not yet.");
            Assert.IsFalse(delivery.GuardWarp(stamp + 60.0, site, arrival, lead), "A storm already inside the lead stops no warp.");
            Assert.IsFalse(delivery.GuardWarp(stamp - 100.0, site, double.NaN, lead), "No storm known.");
            sink.Running = false;
            Assert.IsFalse(delivery.GuardWarp(stamp - 100.0, site, arrival, lead), "No warp runs.");
            sink.Running = true;
            Assert.IsTrue(delivery.GuardWarp(stamp - 900.0, site, arrival, lead), "900 s from the stamp at 1000x is under a second: stop.");
            // A reload to before the storm re-arms the guard.
            delivery.Flush(30 * H);
            delivery.Flush(1 * H);
            sink.Running = true;
            Assert.IsTrue(delivery.GuardWarp(stamp - 900.0, site, arrival, lead));
            Assert.AreEqual(2, sink.WarpStops);
        }

        [TestMethod]
        public void OnlyAGroupTheEvaluatorFoundAsAJumpSaysItPassedDuringTimeWarp()
        {
            var sink = new FakeSink();
            AlertDelivery delivery = Make(sink);
            // Two ordinary events of one frame (no suffix in their texts): one message, the events named, no claim about a warp.
            delivery.Receive(Alert(AlertKind.Worsening, 10 * H));
            delivery.Receive(Alert(AlertKind.Arrived, 10 * H + 60.0));
            List<DeliveredMessage> made = delivery.Flush(10 * H + 60.0);
            Assert.AreEqual(1, made.Count);
            Assert.IsTrue(made[0].Summary);
            StringAssert.Contains(made[0].Text, "[worsening, arrived]");
            Assert.IsFalse(made[0].Text.Contains("passed during time warp"), made[0].Text);
            // The same pair with the evaluator's jump text: the suffix stays.
            var a = Alert(AlertKind.Approaching, 20 * H);
            var b = Alert(AlertKind.Arrived, 20 * H);
            a.Text += ForecastStrings.WarpSummarySuffix;
            b.Text += ForecastStrings.WarpSummarySuffix;
            delivery.Receive(a);
            delivery.Receive(b);
            made = delivery.Flush(20 * H);
            StringAssert.Contains(made[0].Text, "passed during time warp");
        }

        [TestMethod]
        public void NothingIsDeliveredWhileTheForecastIsNotUsableOrTheDeliveryIsOffAndWhatArrivedThenIsDropped()
        {
            var sink = new FakeSink();
            bool ready = false;
            var settings = new AlertDeliverySettings();
            AlertDelivery delivery = Make(sink, settings, () => ready);
            delivery.Receive(Alert(AlertKind.Arrived, 10 * H));
            Assert.AreEqual(0, delivery.Flush(10 * H).Count);
            Assert.AreEqual(0, delivery.QueuedCount, "What arrived while the field was not Ready is dropped, not held.");
            ready = true;
            Assert.AreEqual(0, delivery.Flush(10 * H + 10).Count);
            settings.Enabled = false;
            delivery.Receive(Alert(AlertKind.Arrived, 11 * H));
            Assert.AreEqual(0, delivery.Flush(11 * H).Count);
            Assert.AreEqual(0, sink.Screen.Count + sink.App.Count);
        }

        [TestMethod]
        public void EachDeliveryCanBeSwitchedOnItsOwn()
        {
            var sink = new FakeSink();
            var settings = new AlertDeliverySettings { ScreenMessage = false, MessageApp = true };
            AlertDelivery delivery = Make(sink, settings);
            delivery.Receive(Alert(AlertKind.Arrived, 10 * H));
            delivery.Flush(10 * H);
            Assert.AreEqual(0, sink.Screen.Count);
            Assert.AreEqual(1, sink.App.Count);
            StringAssert.StartsWith(sink.App[0], "Arrived:");
            settings.ScreenMessage = true;
            settings.MessageApp = false;
            delivery.Receive(Alert(AlertKind.Clearing, 12 * H));
            delivery.Flush(12 * H);
            Assert.AreEqual(1, sink.Screen.Count);
            Assert.AreEqual(1, sink.App.Count);
        }
    }
}
