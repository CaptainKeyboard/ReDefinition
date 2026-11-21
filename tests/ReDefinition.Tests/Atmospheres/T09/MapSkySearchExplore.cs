using System;
using System.Globalization;
using System.IO;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The sky from the weather map (master plan section 34): the probe places the craft only at KSC, so a second measuring place is a TIME at which KSC lies under a deck, and a third
    // one with a storm cell near it. This searches the generator (the game's seed, SkyMapsExplore's field) at the M0 time of day (ut 1486800 + k days, the same sun) and prints per
    // candidate the column at KSC and the means over a disc around it; with MAPSKY_MAPS also the field's maps of the given times (u8 / f32 for the map patches) and the humidity map
    // byte for byte against a game capture's humidity.u8 (MAPSKY_GAME_HUMIDITY), so the test field can be checked against the game's. Runs only when MAPSKY_SEARCH_OUT names a file.
    [TestClass]
    public class MapSkySearchExplore
    {
        private const double KscLat = -0.0972, KscLon = -74.5577, DiscKm = 30.0;

        [TestMethod, TestCategory("Explore")]
        public void SearchADeckAndAStormOverKsc()
        {
            string outFile = Environment.GetEnvironmentVariable("MAPSKY_SEARCH_OUT");
            if (string.IsNullOrEmpty(outFile)) return;
            BodyClimate climate;
            GeneratorField field = SkyMapsExplore.KerbinField(664216370, out climate);
            int body = FieldFixture.BodyIndex;
            WeatherGenerator g = field.StateOf(body).Generator;
            double day = 21600.0;
            string fromText = Environment.GetEnvironmentVariable("MAPSKY_FROM_DAYS") ?? "-60", toText = Environment.GetEnvironmentVariable("MAPSKY_TO_DAYS") ?? "240";
            int from = int.Parse(fromText, CultureInfo.InvariantCulture), to = int.Parse(toText, CultureInfo.InvariantCulture);
            using (var w = new StreamWriter(outFile))
            {
                w.WriteLine("ut,cover,heap,sheet,deck,base,top,stable,rhLow,rhMid,rhHigh,reach,precip,discCover,discHeap,discSheet,discReachMax,discReachKm");
                for (int k = from; k <= to; k++)
                {
                    foreach (double hour in new[] { 0.0, -2.0 * 3600.0, 2.0 * 3600.0 })
                    {
                        double ut = 1486800.0 + k * day + hour;
                        if (ut <= 0.0) continue;
                        Weather wx;
                        SkyColumn c;
                        g.Sample(KscLat * Math.PI / 180.0, KscLon * Math.PI / 180.0, 0.0, ut, out wx, true);
                        SkyColumn.Derive(g.Body, ref wx, out c);
                        double sc = 0, sh = 0, ss = 0, rmax = 0, rKm = -1;
                        int n = 0;
                        for (double dy = -DiscKm; dy <= DiscKm; dy += 7.5)
                        for (double dx = -DiscKm; dx <= DiscKm; dx += 7.5)
                        {
                            if (dx * dx + dy * dy > DiscKm * DiscKm) continue;
                            double lat = KscLat + dy / 600.0 / Math.PI * 180.0, lon = KscLon + dx / 600.0 / Math.PI * 180.0;
                            Weather v;
                            SkyColumn s;
                            g.Sample(lat * Math.PI / 180.0, lon * Math.PI / 180.0, 0.0, ut, out v, true);
                            SkyColumn.Derive(g.Body, ref v, out s);
                            sc += v.Coverage;
                            sh += s.HeapAmount;
                            ss += s.SheetAmount;
                            if (v.CellReach01 > rmax)
                            {
                                rmax = v.CellReach01;
                                rKm = Math.Sqrt(dx * dx + dy * dy);
                            }
                            n++;
                        }
                        w.WriteLine(string.Join(",", new[]
                        {
                            ut.ToString("R", CultureInfo.InvariantCulture), F(wx.Coverage), F(c.HeapAmount), F(c.SheetAmount), F(c.DeckAmount), F(c.BaseM), F(c.TopM), F(c.StableM),
                            F(c.RhLow), F(c.RhMid), F(c.RhHigh), F(wx.CellReach01), F(wx.PrecipMmH), F(sc / n), F(sh / n), F(ss / n), F(rmax), F(rKm)
                        }));
                    }
                }
            }
            string mapsDir = Environment.GetEnvironmentVariable("MAPSKY_MAPS");
            string uts = Environment.GetEnvironmentVariable("MAPSKY_MAP_UTS");
            if (string.IsNullOrEmpty(mapsDir) || string.IsNullOrEmpty(uts)) return;
            Directory.CreateDirectory(mapsDir);
            var kinds = new[]
            {
                WeatherMapKind.Coverage, WeatherMapKind.ConvectiveAmount, WeatherMapKind.StratiformAmount, WeatherMapKind.DeckAmount, WeatherMapKind.ConvectiveBase,
                WeatherMapKind.ConvectiveTop, WeatherMapKind.StableShare, WeatherMapKind.HumidityLow, WeatherMapKind.HumidityMid, WeatherMapKind.HumidityHigh, WeatherMapKind.CellReach,
                WeatherMapKind.Humidity
            };
            foreach (string part in uts.Split(','))
            {
                double ut = double.Parse(part, CultureInfo.InvariantCulture);
                var map = new WeatherMap();
                // the first read asks for the sky maps; the render then makes them for this time
                field.GetMap(body, WeatherMapKind.ConvectiveAmount, ut, map);
                Assert.IsTrue(field.Maps.RenderNow(body, ut));
                foreach (WeatherMapKind kind in kinds)
                {
                    Assert.IsTrue(field.GetMap(body, kind, ut, map), kind.ToString());
                    var bytes = new byte[map.Values.Length * 4];
                    Buffer.BlockCopy(map.Values, 0, bytes, 0, bytes.Length);
                    File.WriteAllBytes(Path.Combine(mapsDir, "u" + ((long)ut).ToString(CultureInfo.InvariantCulture) + "_" + kind + ".f32"), bytes);
                    if (kind == WeatherMapKind.Humidity)
                    {
                        string game = Environment.GetEnvironmentVariable("MAPSKY_GAME_HUMIDITY");
                        if (!string.IsNullOrEmpty(game) && File.Exists(game) && Math.Abs(ut - 1486800.0) < 1.0)
                        {
                            map.PackBytes(0f, 1f);
                            byte[] g2 = File.ReadAllBytes(game);
                            int same = 0, close = 0;
                            for (int i = 0; i < g2.Length && i < map.Bytes.Length; i++)
                            {
                                if (g2[i] == map.Bytes[i]) same++;
                                if (Math.Abs(g2[i] - map.Bytes[i]) <= 2) close++;
                            }
                            Console.WriteLine("MAPSKY humidity against the game: equal " + same + ", within 2 " + close + " of " + g2.Length);
                        }
                    }
                }
            }
        }

        private static string F(double v)
        {
            return v.ToString("0.#####", CultureInfo.InvariantCulture);
        }
    }
}
