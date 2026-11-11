using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // D30: the Kerbal Alarm Clock bridge against a fake of its API: an alarm at the storm's ETA minus the lead time, named for the site,
    // deleted when the forecast generation changes or the ETA moves, and left alone once it has fired. Absent KAC is silent.
    [TestClass]
    public class KacBridgeTests
    {
        private const double H = D30Fixtures.H;

        private static bool Sync(KacBridge bridge, FakeKac kac, long generation, double now, double eta, double lead = 3.0, KacAction action = KacAction.MessageOnly,
            bool enabled = true, ForecastSite? at = null)
        {
            ForecastSite site = at ?? D30Fixtures.Site();
            return bridge.Sync(enabled, generation, now, lead, action, new List<ForecastSite> { site }, new List<ulong> { KacBridge.PlaceKey(3, site.BodyIndex) },
                new List<double> { eta });
        }

        [TestMethod]
        public void AVesselThatMovesKeepsItsOneAlarm()
        {
            var kac = new FakeKac();
            var bridge = new KacBridge(kac);
            ForecastSite site = D30Fixtures.Site("Vessel");
            Sync(bridge, kac, 1, 0.0, 10 * H, 3.0, KacAction.MessageOnly, true, site);
            for (int i = 1; i <= 5; i++)
            {
                site.LatDeg += 0.3;
                Assert.IsFalse(Sync(bridge, kac, 1, i * 60.0, 10 * H, 3.0, KacAction.MessageOnly, true, site), "A tenth of a degree on is the same alarm.");
            }
            Assert.AreEqual(1, kac.Alarms.Count);
            Assert.AreEqual(0, kac.Deleted.Count);
        }

        [TestMethod]
        public void TheAlarmIsAtTheEtaMinusTheLeadTimeAndNamedForTheSite()
        {
            var kac = new FakeKac();
            var bridge = new KacBridge(kac);
            Assert.IsTrue(Sync(bridge, kac, 1, 0.0, 10 * H));
            Assert.AreEqual(1, kac.Alarms.Count);
            KeyValuePair<string, double> alarm = kac.Alarms["A1"];
            Assert.AreEqual("Storm approaching: Pad", alarm.Key);
            Assert.AreEqual(7 * H, alarm.Value, 1e-6);
            Assert.AreEqual(KacAction.MessageOnly, kac.Actions["A1"]);
            Assert.IsFalse(Sync(bridge, kac, 1, 60.0, 10 * H), "Nothing changes while the forecast does not.");
            Assert.AreEqual(1, kac.Alarms.Count);
            Assert.AreEqual(0, kac.Deleted.Count);
        }

        [TestMethod]
        public void AChangedForecastGenerationDeletesTheAlarmsItMadeAndMakesThemAgain()
        {
            var kac = new FakeKac();
            var bridge = new KacBridge(kac);
            Sync(bridge, kac, 1, 0.0, 10 * H);
            Assert.IsTrue(Sync(bridge, kac, 2, 100.0, 12 * H));
            CollectionAssert.AreEqual(new[] { "A1" }, kac.Deleted);
            Assert.AreEqual(1, kac.Alarms.Count);
            Assert.AreEqual(9 * H, kac.Alarms["A2"].Value, 1e-6, "Made again from the new ETA.");
        }

        [TestMethod]
        public void AMovedEtaAnActionChangeAndALostStormReplaceOrRemoveTheAlarm()
        {
            var kac = new FakeKac();
            var bridge = new KacBridge(kac);
            Sync(bridge, kac, 1, 0.0, 10 * H);
            Assert.IsFalse(Sync(bridge, kac, 1, 10.0, 10 * H + 30.0), "Half a minute is inside the tolerance.");
            Assert.IsTrue(Sync(bridge, kac, 1, 20.0, 11 * H));
            Assert.AreEqual(8 * H, kac.Alarms["A2"].Value, 1e-6);
            Assert.IsTrue(Sync(bridge, kac, 1, 30.0, 11 * H, 3.0, KacAction.KillWarp));
            Assert.AreEqual(KacAction.KillWarp, kac.Actions["A3"]);
            Assert.IsTrue(Sync(bridge, kac, 1, 40.0, double.NaN, 3.0, KacAction.KillWarp));
            Assert.AreEqual(0, kac.Alarms.Count);
            Assert.AreEqual(0, bridge.Count);
        }

        [TestMethod]
        public void NoAlarmIsMadeInsideTheLeadTimeOrWithKacAbsentOrSwitchedOff()
        {
            var kac = new FakeKac();
            var bridge = new KacBridge(kac);
            Assert.IsFalse(Sync(bridge, kac, 1, 0.0, 2 * H), "The storm is nearer than the lead time: the alarm time is past.");
            Assert.AreEqual(0, kac.Alarms.Count);
            kac.IsReady = false;
            Assert.IsFalse(Sync(bridge, kac, 1, 0.0, 10 * H));
            Assert.AreEqual(0, kac.Alarms.Count, "KAC is not ready: nothing is made.");
            kac.IsReady = true;
            Sync(bridge, kac, 1, 0.0, 10 * H);
            Assert.AreEqual(1, kac.Alarms.Count);
            Assert.IsTrue(Sync(bridge, kac, 1, 10.0, 10 * H, 3.0, KacAction.MessageOnly, false), "Switched off: what was made is deleted.");
            Assert.AreEqual(0, kac.Alarms.Count);
        }

        [TestMethod]
        public void AnAlarmThatHasFiredIsLeftToKacAndOnlyItsIdIsDropped()
        {
            var kac = new FakeKac();
            var bridge = new KacBridge(kac);
            Sync(bridge, kac, 1, 0.0, 10 * H);
            Sync(bridge, kac, 1, 8 * H, 10 * H);
            Assert.AreEqual(0, kac.Deleted.Count, "The alarm at 7 h has fired; it is KAC's now.");
            Assert.AreEqual(1, kac.Alarms.Count);
            Assert.AreEqual(0, bridge.Count);
        }

        [TestMethod]
        public void KacThatIsNotInstalledGivesNoReadyApiAndNoLogLine()
        {
            var lines = new List<string>();
            var reflection = new KacReflection(lines.Add);
            Assert.IsFalse(reflection.Ready);
            Assert.IsNull(reflection.CreateAlarm("Storm approaching: Pad", 100.0, KacAction.MessageOnly));
            Assert.IsFalse(reflection.DeleteAlarm("x"));
            Assert.AreEqual(0, lines.Count, "An absent KAC makes no log noise.");
        }
    }
}
