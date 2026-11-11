using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D34
{
    // Package D34, tracks: the tiles (stamps, fade, save and load), the revert, the memory, the strips and the draw cap.
    [TestClass]
    public class TrackTests
    {
        private const double Radius = 600000.0;
        private const double Tau = 3.0 * 3600.0;

        [TestMethod]
        public void APlaceIsFoundInItsTileAndComesBackWithinAHalfTexel()
        {
            for (double lat = -80; lat <= 80; lat += 17)
                for (double lon = -170; lon <= 170; lon += 37)
                {
                    int tx, ty;
                    double e, n;
                    TrackTiles.Locate(Radius, lat, lon, out tx, out ty, out e, out n);
                    Assert.IsTrue(e >= 0 && e <= TrackTiles.TileMetres && n >= 0 && n <= TrackTiles.TileMetres, "inside the tile: " + e + ", " + n);
                    double lat2, lon2;
                    TrackTiles.PlaceOf(Radius, tx, ty, e, n, out lat2, out lon2);
                    double dNorth = (lat2 - lat) * Math.PI / 180.0 * Radius;
                    double dEast = (lon2 - lon) * Math.PI / 180.0 * Radius * Math.Cos(lat * Math.PI / 180.0);
                    Assert.AreEqual(0.0, Math.Sqrt(dNorth * dNorth + dEast * dEast), 0.25, "lat " + lat + " lon " + lon);
                }
        }

        [TestMethod]
        public void ATileIsAnchoredInLatitudeAndLongitudeNotInTheWorld()
        {
            var tiles = new TrackTiles(Radius);
            Assert.IsTrue(tiles.Stamp(55.0, 20.0, 0.25, 1000.0, Tau, 1.0));
            // The same place gives the same tile whatever the floating origin does: only the latitude and longitude are asked.
            Assert.IsTrue(tiles.Shown(55.0, 20.0, 1000.0) > 0.99);
            Assert.AreEqual(0.0, tiles.Shown(55.0, 20.01, 1000.0), "1.1 km east is another place");
            Assert.AreEqual(1, tiles.LiveCount);
        }

        [TestMethod]
        public void AStampPressesAnAreaOfTheTyreWidthAndFadesWithTau()
        {
            var tiles = new TrackTiles(Radius);
            tiles.Stamp(55.0, 20.0, 0.25, 1000.0, Tau, 1.0);
            Assert.AreEqual(1.0, tiles.Shown(55.0, 20.0, 1000.0), 1e-9);
            double at = tiles.Shown(55.0, 20.0, 1000.0 + Tau);
            Assert.AreEqual(1.0 / Math.E, at, 0.02, "one tau later 1/e of it shows");
            Assert.AreEqual(0.0, tiles.Shown(55.0, 20.0, 1000.0 + 5.0 * Tau), "after 5 tau it is gone");
            Assert.AreEqual(0.0, tiles.Shown(55.0, 20.0, 999.0), "before the stamp nothing shows");
            // A disc of radius 0.25 m is a texel or four of 0.5 m, not a metre.
            int pressed = 0;
            TrackTile tile = new List<TrackTile>(tiles.Live)[0];
            foreach (byte b in tile.Pressed) if (b != 0) pressed++;
            Assert.IsTrue(pressed >= 1 && pressed <= 4, "texels pressed: " + pressed);
        }

        [TestMethod]
        public void ThinCoverTakesNoTrackAndABadNumberIsRefused()
        {
            var tiles = new TrackTiles(Radius);
            Assert.IsFalse(tiles.Stamp(55, 20, 0.25, 0, Tau, 0.19), "under 0.2 of cover");
            Assert.IsTrue(tiles.Stamp(55, 20, 0.25, 0, Tau, 0.2));
            Assert.IsFalse(tiles.Stamp(double.NaN, 20, 0.25, 0, Tau, 1));
            Assert.IsFalse(tiles.Stamp(55, 20, 0.25, 0, 0, 1), "a fade time of zero");
            Assert.IsFalse(tiles.Stamp(55, 20, 0.25, double.NaN, Tau, 1));
        }

        [TestMethod]
        public void ANewStampDoesNotFreshenOldTracksOfTheSameTile()
        {
            var tiles = new TrackTiles(Radius);
            tiles.Stamp(55.0, 20.0, 0.25, 0.0, Tau, 1.0);
            // A long time later another wheel marks 10 m away in the same tile: the old track has faded meanwhile.
            double lat2;
            double lon2;
            int tx, ty;
            double e, n;
            TrackTiles.Locate(Radius, 55.0, 20.0, out tx, out ty, out e, out n);
            TrackTiles.PlaceOf(Radius, tx, ty, e + 10.0 < 120.0 ? e + 10.0 : e - 10.0, n, out lat2, out lon2);
            tiles.Stamp(lat2, lon2, 0.25, Tau, Tau, 1.0);
            Assert.AreEqual(1.0 / Math.E, tiles.Shown(55.0, 20.0, Tau), 0.03, "the old track kept its fade");
            Assert.AreEqual(1.0, tiles.Shown(lat2, lon2, Tau), 0.01, "the new one is fresh");
        }

        [TestMethod]
        public void FallingSnowFillsTheTracksIn()
        {
            var tiles = new TrackTiles(Radius);
            tiles.Stamp(55.0, 20.0, 0.25, 0.0, Tau, 1.0);
            tiles.Attenuate(0.5);
            Assert.AreEqual(0.5, tiles.Shown(55.0, 20.0, 0.0), 0.01);
            tiles.Attenuate(1.0);
            Assert.AreEqual(0.0, tiles.Shown(55.0, 20.0, 0.0), 1e-9);
            tiles.Attenuate(-1);
            tiles.Attenuate(double.NaN);
        }

        [TestMethod]
        public void ATileSurvivesRunLengthAndDeflateAndKeepsItsStampAndFade()
        {
            var tile = new TrackTile(12, 34) { StampUt = 4321.5, TauSeconds = 7200.0 };
            var rng = new Random(5);
            for (int k = 0; k < 3000; k++) tile.Pressed[rng.Next(tile.Pressed.Length)] = (byte)rng.Next(1, 256);
            for (int k = 1000; k < 5000; k++) tile.Pressed[k] = 255;
            byte[] packed = TrackTiles.Encode(tile);
            Assert.IsTrue(packed.Length < tile.Pressed.Length / 4, "mostly empty: " + packed.Length + " bytes");
            TrackTile back = TrackTiles.Decode(packed, 12, 34);
            Assert.IsNotNull(back);
            CollectionAssert.AreEqual(tile.Pressed, back.Pressed);
            Assert.AreEqual(4321.5, back.StampUt);
            Assert.AreEqual(7200.0, back.TauSeconds);
            Assert.IsNull(TrackTiles.Decode(new byte[] { 1, 2, 3 }, 0, 0), "damaged bytes");
            byte[] cut = new byte[packed.Length / 2];
            Array.Copy(packed, cut, cut.Length);
            Assert.IsNull(TrackTiles.Decode(cut, 0, 0), "a cut file");
            var full = new TrackTile(0, 0);
            for (int k = 0; k < full.Pressed.Length; k++) full.Pressed[k] = (byte)(k % 251);
            CollectionAssert.AreEqual(full.Pressed, TrackTiles.Decode(TrackTiles.Encode(full), 0, 0).Pressed, "runs of one and the 255 limit of a run");
        }

        [TestMethod]
        public void TheTracksAreSavedAndLoadedWithTheirFadeApplied()
        {
            var tiles = new TrackTiles(Radius);
            for (int k = 0; k < 20; k++) tiles.Stamp(55.0 + k * 0.01, 20.0, 0.25, 1000.0 + k, Tau, 1.0);
            byte[] file = tiles.SaveAll(2000.0);
            var loaded = new TrackTiles(Radius);
            Assert.AreEqual(tiles.LiveCount, loaded.LoadAll(file, 3000.0));
            double before = tiles.Shown(55.05, 20.0, 1000.0 + Tau);
            double after = loaded.Shown(55.05, 20.0, 1000.0 + Tau);
            Assert.AreEqual(before, after, 1e-9, "the same fade after the load");
            Assert.IsTrue(after > 0.3, "the trail is there: " + after);
            Assert.AreEqual(-1, new TrackTiles(Radius * 2).LoadAll(file, 3000.0), "a file of another body is refused");
            Assert.AreEqual(-1, loaded.LoadAll(new byte[] { 1, 2, 3 }, 0), "a damaged file is refused");
            Assert.AreEqual(-1, loaded.LoadAll(new byte[0], 0));
        }

        [TestMethod]
        public void ARevertDropsEveryTileStampedAfterTheNewTimeAndNothingFutureIsShown()
        {
            var tiles = new TrackTiles(Radius);
            tiles.Stamp(55.0, 20.0, 0.25, 100000.0, Tau, 1.0);
            tiles.Stamp(10.0, 20.0, 0.25, 400000.0, Tau, 1.0);
            byte[] file = tiles.SaveAll(200000.0);
            double now = 100000.0 + 2 * 6 * 3600.0 - 3 * 86400.0;
            Assert.AreEqual(0.0, tiles.Shown(10.0, 20.0, now), "a stamp in the future shows nothing");
            Assert.AreEqual(1, tiles.DropAfter(300000.0), "the newer tile is dropped");
            Assert.AreEqual(1, tiles.LiveCount);
            var loaded = new TrackTiles(Radius);
            Assert.AreEqual(1, loaded.LoadAll(file, 300000.0), "on load the tile of the future is dropped");
            Assert.IsTrue(loaded.Shown(55.0, 20.0, 100001.0) > 0.9);
            Assert.AreEqual(0.0, loaded.Shown(10.0, 20.0, 400001.0));
            // The values stay inside 0..1 whatever the time asked.
            foreach (double ut in new[] { -1e9, 0.0, 100000.0, 1e12 })
            {
                double v = tiles.Shown(55.0, 20.0, ut);
                Assert.IsTrue(v >= 0.0 && v <= 1.0, "shown " + v + " at " + ut);
            }
        }

        [TestMethod]
        public void SixtyFourLiveTilesAreFourMegabytesAndOlderOnesGoToTheSideFile()
        {
            var tiles = new TrackTiles(Radius);
            // 0.02 degrees of latitude are 210 m: every mark is in a tile of its own (a tile is 128 m high).
            for (int k = 0; k < 100; k++) tiles.Stamp(10.0 + k * 0.02, 20.0, 0.25, 1000.0 + k, Tau, 1.0);
            Assert.AreEqual(TrackTiles.MaxLive, tiles.LiveCount);
            // A disc on a tile's edge makes two tiles, so there may be one or two more than the marks.
            Assert.IsTrue(tiles.ParkedCount >= 100 - TrackTiles.MaxLive && tiles.ParkedCount <= 100 - TrackTiles.MaxLive + 3, "live " + tiles.LiveCount + " parked " + tiles.ParkedCount);
            Assert.IsTrue(tiles.LiveBytes <= 4L * 1024 * 1024, "tile RAM " + tiles.LiveBytes);
            Console.WriteLine("D34 tracks: " + tiles.LiveCount + " live tiles = " + tiles.LiveBytes / 1024 + " KB, " + tiles.ParkedCount + " parked");
            // A parked tile comes back when it is touched, with its content.
            Assert.IsTrue(tiles.Shown(10.0, 20.0, 1000.0) > 0.99, "the oldest tile was parked and is read back");
            byte[] file = tiles.SaveAll(1100.0);
            Assert.AreEqual(tiles.LiveCount + tiles.ParkedCount, new TrackTiles(Radius).LoadAll(file, 1100.0));
        }

        [TestMethod]
        public void TheStripsOfATileAreRunsOfEquallyDarkTexelsAndFadeOutInLevels()
        {
            var tiles = new TrackTiles(Radius);
            // A wheel line: 40 marks 0.5 m apart along the east.
            int tx, ty;
            double e, n;
            TrackTiles.Locate(Radius, 55.0, 20.0, out tx, out ty, out e, out n);
            for (int k = 0; k < 40; k++)
            {
                double lat, lon;
                TrackTiles.PlaceOf(Radius, tx, ty, 20.0 + k * 0.5, 60.25, out lat, out lon);
                tiles.Stamp(lat, lon, 0.25, 0.0, Tau, 1.0);
            }
            var strips = new List<TrackRibbons.Strip>();
            TrackRibbons.AddTile(new List<TrackTile>(tiles.Live)[0], 0.0, strips, TrackRibbons.PoolSize);
            Assert.IsTrue(strips.Count >= 1 && strips.Count <= 4, "a line along a row is a few strips: " + strips.Count);
            float length = 0;
            foreach (TrackRibbons.Strip s in strips) length += s.Length;
            Assert.AreEqual(20.0, length, 2.0, "40 marks of half a metre");
            foreach (TrackRibbons.Strip s in strips)
            {
                Assert.AreEqual(TrackTiles.TexelMetres, s.Height, 1e-6);
                Assert.AreEqual(1.0f, s.Alpha, 1e-6f);
            }
            var later = new List<TrackRibbons.Strip>();
            TrackRibbons.AddTile(new List<TrackTile>(tiles.Live)[0], Tau, later, TrackRibbons.PoolSize);
            Assert.IsTrue(later.Count > 0 && later[0].Alpha < 1f && later[0].Alpha > 0f, "one tau later the strips are lighter");
            var gone = new List<TrackRibbons.Strip>();
            TrackRibbons.AddTile(new List<TrackTile>(tiles.Live)[0], 6.0 * Tau, gone, TrackRibbons.PoolSize);
            Assert.AreEqual(0, gone.Count, "after six tau no strip");
            Assert.AreEqual(0, TrackRibbons.LevelOf(0.01));
            Assert.AreEqual(TrackRibbons.Levels, TrackRibbons.LevelOf(1.0));
            Assert.AreEqual(1, TrackRibbons.LevelOf(TrackTiles.Gone + 0.001));
        }

        [TestMethod]
        public void TheStripPoolCapsAtTwoThousandAndTakesTheNearestTilesFirst()
        {
            var tiles = new TrackTiles(Radius);
            // Fill 3 tiles with dense marks: every row of 0.5 m, 256 rows, in each of a near and a far tile.
            int tx, ty;
            double e, n;
            TrackTiles.Locate(Radius, 55.0, 20.0, out tx, out ty, out e, out n);
            for (int tile = 0; tile < 3; tile++)
                for (int row = 0; row < 256; row++)
                {
                    double lat, lon;
                    TrackTiles.PlaceOf(Radius, tx + tile, ty, 10.0, row * 0.5 + 0.25, out lat, out lon);
                    tiles.Stamp(lat, lon, 0.25, 0.0, Tau, 1.0);
                }
            var into = new List<TrackRibbons.Strip>();
            int count = TrackRibbons.Collect(tiles, Radius, 55.0, 20.0, 400.0, 0.0, into, TrackRibbons.PoolSize);
            Assert.IsTrue(count <= TrackRibbons.PoolSize);
            Assert.AreEqual(count, into.Count);
            Assert.AreEqual(tx, into[0].Tx, "the tile of the centre first");
            var few = new List<TrackRibbons.Strip>();
            Assert.AreEqual(10, TrackRibbons.Collect(tiles, Radius, 55.0, 20.0, 400.0, 0.0, few, 10));
        }

        [TestMethod]
        public void TheTilesStayInsideTheMemoryOfTheBrief()
        {
            Assert.AreEqual(64 * 1024, TrackTiles.TexelsPerSide * TrackTiles.TexelsPerSide);
            Assert.AreEqual(128.0, TrackTiles.TileMetres);
            Assert.AreEqual(4L * 1024 * 1024, (long)TrackTiles.MaxLive * TrackTiles.TexelsPerSide * TrackTiles.TexelsPerSide);
        }
    }
}
