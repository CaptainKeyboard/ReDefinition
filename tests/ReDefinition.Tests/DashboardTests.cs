using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Dashboard;
using ReDefinition.Modules;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // The dashboard's parts free of the game: its settings and rows, the JSON it speaks and the QR
    // code a phone opens it with.
    [TestClass]
    public class DashboardTests
    {
        // A phone's address as the QR code carries it: host, port and the key, 40 bytes.
        private const string PhoneAddress = "http://192.168.178.20:8765/?key=abcdefgh";

        // As shipped the dashboard is open on the network and has no key yet: the server makes one at its first
        // start.
        [TestMethod]
        public void TheDashboardOpensOnTheNetworkAsItShipsAndItsKeyIsMadeAtTheFirstStart()
        {
            OwnSettings settings = new OwnSettings();
            Assert.IsTrue(settings.DashboardNetwork);
            Assert.AreEqual("", settings.DashboardKey);
        }

        // A reset keeps the key the paired phones hold, so they stay connected, and turns the network back on.
        [TestMethod]
        public void ResetKeepsTheKeyThePhonesHold()
        {
            OwnSettings now = new OwnSettings { DashboardKey = "abcdefgh", DashboardNetwork = false };
            OwnSettings reset = OwnSettingsReset.From(now, true);
            Assert.AreEqual("abcdefgh", reset.DashboardKey);
            Assert.IsTrue(reset.DashboardNetwork);
        }

        // The window's Apply copies what it changed: a key the server made meanwhile is not taken back.
        [TestMethod]
        public void ApplyLeavesAKeyMadeWhileTheWindowWasOpen()
        {
            OwnSettings live = new OwnSettings();
            OwnSettings before = live.Clone();
            OwnSettings after = live.Clone();
            live.DashboardKey = "abcdefgh";
            after.DashboardNetwork = false;

            live.ChangeLive(before, after);

            Assert.AreEqual("abcdefgh", live.DashboardKey);
            Assert.IsFalse(live.DashboardNetwork);
        }

        // The Devices tab has the network switch and the Connect button in its Dashboard section, the switch
        // needing no profile; the General tab has no dialog rows.
        [TestMethod]
        public void TheSwitchAndTheConnectButtonStandInDevicesUnderDashboard()
        {
            ModuleSetting network = OurModules.Rows(SettingCategory.Devices).Single();
            Assert.AreEqual("dashboardNetwork", network.Key);
            Assert.AreEqual("Dashboard", network.Section);
            Assert.IsFalse(network.NeedsProfile);
            Assert.AreEqual("False", network.Read(new OwnSettings { DashboardNetwork = false }));

            ModuleDialog connect = OurModules.DialogRows(SettingCategory.Devices).Single();
            Assert.AreEqual("Dashboard", connect.Section);
            Assert.AreEqual("Connect", connect.Button);
            Assert.IsNotNull(connect.Content);
            Assert.AreEqual(0, OurModules.DialogRows(SettingCategory.General).Count);
        }

        // The JSON reader takes a dashboard's message: arrays of strings, numbers as doubles, and empty arrays.
        [TestMethod]
        public void JsonReadsWhatADashboardSends()
        {
            Dictionary<string, object> message = Json.DecodeObject("{\"+\": [\"v.altitude\", \"r.resource[LiquidFuel]\"], \"rate\": 200, \"run\": []}");

            CollectionAssert.AreEqual(new object[] { "v.altitude", "r.resource[LiquidFuel]" }, (List<object>)message["+"]);
            Assert.AreEqual(200.0, message["rate"]);
            Assert.AreEqual(0, ((List<object>)message["run"]).Count);
        }

        // The JSON sent to the page writes numbers with a point under a German culture too, and escapes quotes in
        // text.
        [TestMethod]
        public void JsonWritesNumbersInvariantlyAndEscapesText()
        {
            System.Globalization.CultureInfo culture = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string text = Json.Encode(new Dictionary<string, object>
                {
                    { "v.altitude", 1234.5 }, { "v.name", "Kerbal \"X\"" }, { "f.sasEnabled", true },
                });
                Assert.AreEqual("{\"v.altitude\":1234.5,\"v.name\":\"Kerbal \\\"X\\\"\",\"f.sasEnabled\":true}", text);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = culture;
            }
        }

        // The address's 40 bytes need version 3 at level M: 29 modules a side.
        [TestMethod]
        public void TheQrCodeIsTheSmallestVersionThatHoldsTheAddress()
        {
            Assert.AreEqual(29, new QrCode(PhoneAddress).Size);
            Assert.AreEqual(21, new QrCode("http://a").Size);
        }

        // The code's three finder patterns stand in their corners as rings of seven modules, and the timing
        // row and column alternate between them.
        [TestMethod]
        public void TheQrCodeHasItsFinderPatternsAndTiming()
        {
            QrCode code = new QrCode(PhoneAddress);
            foreach (int[] corner in new[] { new[] { 0, 0 }, new[] { code.Size - 7, 0 }, new[] { 0, code.Size - 7 } })
            {
                for (int dy = 0; dy < 7; dy++)
                    for (int dx = 0; dx < 7; dx++)
                    {
                        int ring = System.Math.Max(System.Math.Abs(dx - 3), System.Math.Abs(dy - 3));
                        Assert.AreEqual(ring != 2, code[corner[0] + dx, corner[1] + dy], "finder at " + corner[0] + "," + corner[1]);
                    }
            }
            for (int i = 8; i < code.Size - 8; i++)
            {
                Assert.AreEqual(i % 2 == 0, code[i, 6], "timing row at " + i);
                Assert.AreEqual(i % 2 == 0, code[6, i], "timing column at " + i);
            }
        }

        // Level M with mask 0: the format's 15 bits are 101010000010010 (ISO/IEC 18004, table C.1). Next
        // to the top left finder they run from the least significant bit down column 8 -- rows 0 to 5,
        // 7 and 8 -- then along row 8 from column 7 to 0.
        [TestMethod]
        public void TheQrCodeSaysLevelMAndMaskZero()
        {
            QrCode code = new QrCode(PhoneAddress);
            const string Format = "101010000010010";
            int[][] cells =
            {
                new[] { 8, 0 }, new[] { 8, 1 }, new[] { 8, 2 }, new[] { 8, 3 }, new[] { 8, 4 }, new[] { 8, 5 }, new[] { 8, 7 },
                new[] { 8, 8 }, new[] { 7, 8 }, new[] { 5, 8 }, new[] { 4, 8 }, new[] { 3, 8 }, new[] { 2, 8 }, new[] { 1, 8 },
                new[] { 0, 8 },
            };
            for (int i = 0; i < 15; i++)
                Assert.AreEqual(Format[14 - i] == '1', code[cells[i][0], cells[i][1]], "format bit " + i);
        }
    }
}
