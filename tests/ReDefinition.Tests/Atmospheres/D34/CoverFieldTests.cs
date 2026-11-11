using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.D34
{
    // Package D34 against the real weather generator (C25): the cover is what the field's surface layer says, with nothing of its own.
    [TestClass]
    public class CoverFieldTests
    {
        private const int Body = FieldFixture.BodyIndex;

        // The map of the field at a time, resampled as the game does it, for a layer with the given full depth.
        private static CoverMap.Frame Frame(GeneratorField field, double ut, float maxDepthM, out WeatherMap source)
        {
            source = new WeatherMap();
            // The map worker keeps the last snapshot; the test asks for one at this time (the game's worker does it by itself).
            field.Maps.RenderCoverNow(Body, ut);
            Assert.IsTrue(field.GetMap(Body, WeatherMapKind.SnowCover, ut, source));
            var map = new CoverMap();
            CoverMap.Frame frame = map.Resample(source.Values, source.Width, source.Height, maxDepthM, source.Version, ut);
            map.Publish(frame);
            return frame;
        }

        [TestMethod]
        public void TheCoverDepthAtAPointIsTheFieldsSurfaceValueWithinQuantisation()
        {
            GeneratorField field = FieldFixture.Field();
            double ut = FieldFixture.HomeYear + 3.0 * 86400.0;
            WeatherMap source;
            CoverMap.Frame frame = Frame(field, ut, 0.5f, out source);
            CoverDef def = CoverDepthTests.Snow(maxDepth: 0.5f);
            int checkedTexels = 0, covered = 0;
            for (int j = 2; j < source.Height - 2; j += 3)
            {
                for (int i = 0; i < source.Width; i += 5)
                {
                    double lat = 90.0 - (j + 0.5) * 180.0 / source.Height, lon = -180.0 + (i + 0.5) * 360.0 / source.Width;
                    float m = source.Values[j * source.Width + i];
                    double depth = CoverDepth.Depth01(def, frame.Sample01(lat, lon) * def.MaxDepthM, CoverDepth.Mask(def, lat, 0, 0, false), 1.0, 0.0, 1.0);
                    // The 1024 x 512 map is bilinear from the 256 x 128 one: at a source texel centre it equals the source up to the
                    // curvature of the field between texel centres (a few thousandths of the layer's depth) and the 16-bit step.
                    Assert.AreEqual(Math.Min(1.0, m / 0.5), depth, 0.01, "lat " + lat + " lon " + lon + " field " + m);
                    checkedTexels++;
                    if (m > 0.01f) covered++;
                }
            }
            Console.WriteLine("D34 L0: " + checkedTexels + " texels compared, " + covered + " with cover");
            Assert.IsTrue(covered > 20, "the generator has snow somewhere at this time: " + covered);
        }

        [TestMethod]
        public void TheCoverIsBareWhereTheFieldIsWarmAndColdPlacesHaveSome()
        {
            GeneratorField field = FieldFixture.Field();
            double ut = FieldFixture.HomeYear + 3.0 * 86400.0;
            WeatherMap source;
            CoverMap.Frame frame = Frame(field, ut, 0.5f, out source);
            WeatherSample s;
            Assert.IsTrue(field.Sample(Body, 0, 40, 0, ut, out s));
            Assert.IsTrue(s.TemperatureK > 273.15f + 5f, "the equator is above freezing: " + s.TemperatureK);
            Assert.AreEqual(0.0, frame.Sample01(0, 40), 1e-9, "no cover where the temperature stays above freezing");
            Assert.AreEqual(0.0, frame.Sample01(5, -100), 1e-9);
            Assert.IsTrue(frame.Sample01(80, 20) > 0.0 || frame.Sample01(85, -60) > 0.0 || frame.Sample01(75, 120) > 0.0, "the cold pole has cover");
        }

        [TestMethod]
        public void TheCoverGrowsWhileASnowSystemFallsAndOnlyUnderIt()
        {
            // A scripted field (the generator's maps ignore the forced discs, see the note in the report): snow falls from a disc where it is
            // below freezing, 8 times the water equivalent, and nothing falls into the warm.
            var field = new ScriptedField();
            double ut = 1.0e6;
            // The centre of a texel, so that the disc covers whole texels; 75 N is cold, 20 N is warm.
            double lat = 90.0 - (12 + 0.5) * 180.0 / 128, lon = -180.0 + (140 + 0.5) * 360.0 / 256;
            double warmLat = 90.0 - (91 + 0.5) * 180.0 / 128;
            // 40 km are 3.8 degrees of this body (radius 600 km).
            field.AddPrecipitationDisc(Body, lat, lon, 40, 4f, PrecipType.Snow, ut - 6 * 3600.0, ut + 6 * 3600.0);
            field.AddPrecipitationDisc(Body, warmLat, lon, 40, 4f, PrecipType.Snow, ut - 6 * 3600.0, ut + 6 * 3600.0);
            double before = field.Frame(ut - 6 * 3600.0, 0.5f).Sample01(lat, lon);
            double mid = field.Frame(ut - 3 * 3600.0, 0.5f).Sample01(lat, lon);
            double end = field.Frame(ut, 0.5f).Sample01(lat, lon);
            double after = field.Frame(ut + 12 * 3600.0, 0.5f).Sample01(lat, lon);
            // 4 mm an hour for 3 hours, 8 times the water equivalent: 0.096 m, of a layer 0.5 m deep.
            Assert.AreEqual(0.0, before, 1e-9);
            Assert.AreEqual(0.096 / 0.5, mid - before, 0.005, "after three hours");
            Assert.AreEqual(0.192 / 0.5, end - before, 0.005, "after six hours");
            Assert.IsTrue(mid > before && end > mid, "the cover grows while it falls: " + before + ", " + mid + ", " + end);
            Assert.AreEqual(field.Frame(ut + 6 * 3600.0, 0.5f).Sample01(lat, lon), after, 0.005, "the scripted fall does not melt after it ends");
            Assert.AreEqual(0.0, field.Frame(ut, 0.5f).Sample01(warmLat, lon), 1e-9, "no cover where the field stays above freezing");
            Assert.AreEqual(0.0, field.Frame(ut, 0.5f).Sample01(-60, -100), 1e-9, "nothing outside the disc");
        }

        // The weather field of a test: a cold north and a warm south; a forced snow disc deposits depth in the surface map where it is cold.
        private sealed class ScriptedField : IWeatherField, ISurfaceForcing
        {
            private sealed class Disc
            {
                internal double Lat, Lon, RadiusKm, From, To;
                internal float Rate;
            }

            private readonly List<Disc> discs = new List<Disc>();

            public void AddPrecipitationDisc(int bodyIndex, double latDeg, double lonDeg, double radiusKm, float rateMmPerH, PrecipType type, double fromUt, double toUt)
            {
                if (type == PrecipType.Snow) discs.Add(new Disc { Lat = latDeg, Lon = lonDeg, RadiusKm = radiusKm, From = fromUt, To = toUt, Rate = rateMmPerH });
            }

            private static double TemperatureAt(double lat)
            {
                return lat > 60 ? 255.0 : 290.0;
            }

            public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
            {
                sample = WeatherSample.Clear(bodyIndex);
                sample.TemperatureK = (float)TemperatureAt(latDeg);
                return true;
            }

            public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
            {
                if (kind != WeatherMapKind.SnowCover) return false;
                target.Resize(256, 128);
                for (int j = 0; j < 128; j++)
                {
                    double lat = 90.0 - (j + 0.5) * 180.0 / 128;
                    for (int i = 0; i < 256; i++)
                    {
                        double lon = -180.0 + (i + 0.5) * 360.0 / 256;
                        double depth = 0;
                        if (TemperatureAt(lat) < 273.15)
                        {
                            foreach (Disc d in discs)
                            {
                                if (ut <= d.From) continue;
                                double dLat = (lat - d.Lat) * Math.PI / 180.0, dLon = (lon - d.Lon) * Math.PI / 180.0 * Math.Cos((lat + d.Lat) * Math.PI / 360.0);
                                if (600000.0 * Math.Sqrt(dLat * dLat + dLon * dLon) > d.RadiusKm * 1000.0) continue;
                                depth += d.Rate * (Math.Min(ut, d.To) - d.From) / 3600.0 * 8.0 / 1000.0;
                            }
                        }
                        target.Values[j * 256 + i] = (float)depth;
                    }
                }
                target.Kind = kind;
                target.BodyIndex = bodyIndex;
                target.Ut = ut;
                target.Version = (long)(ut / 60.0) + discs.Count;
                return true;
            }

            internal CoverMap.Frame Frame(double ut, float maxDepthM)
            {
                var map = new CoverMap();
                var source = new WeatherMap();
                GetMap(Body, WeatherMapKind.SnowCover, ut, source);
                CoverMap.Frame frame = map.Resample(source.Values, source.Width, source.Height, maxDepthM, source.Version, ut);
                map.Publish(frame);
                return frame;
            }

            public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target) { return false; }
            public void Advance(double budgetMs) { }
            public long Version { get { return discs.Count; } }
            public long DefinitionId { get { return 34; } }
            public bool ThreadSafe { get { return false; } }
            public double StepSeconds { get { return 60; } }
            public FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
        }

        [TestMethod]
        public void AForcedSystemLeavesAPatchTheTilesOfWhichCarryANewVersion()
        {
            var field = new ScriptedField();
            double ut = 1.0e6;
            double lat = 90.0 - (12 + 0.5) * 180.0 / 128, lon = -180.0 + (140 + 0.5) * 360.0 / 256;
            var map = new CoverMap();
            var src = new WeatherMap();
            field.GetMap(Body, WeatherMapKind.SnowCover, ut - 6 * 3600.0, src);
            CoverMap.Frame f1 = map.Resample(src.Values, src.Width, src.Height, 0.5f, src.Version, 0.0);
            map.Publish(f1);
            field.AddPrecipitationDisc(Body, lat, lon, 40, 4f, PrecipType.Snow, ut - 6 * 3600.0, ut + 6 * 3600.0);
            field.GetMap(Body, WeatherMapKind.SnowCover, ut, src);
            CoverMap.Frame f2 = map.Resample(src.Values, src.Width, src.Height, 0.5f, src.Version, 1.0);
            int tx, ty;
            CoverMap.TileOf(lat, lon, out tx, out ty);
            Assert.AreEqual(f2.Number, f2.TileVersions[ty * CoverMap.TilesX + tx], "the tile under the system is new");
            Assert.IsTrue(CoverMap.MaxVersion(f2.TileVersions, lat - 1, lat + 1, lon - 1, lon + 1) == f2.Number);
            int changed = 0;
            foreach (int v in f2.TileVersions) if (v == f2.Number) changed++;
            var where = new System.Text.StringBuilder();
            for (int k = 0; k < f2.TileVersions.Length; k++) if (f2.TileVersions[k] == f2.Number) where.Append(k % CoverMap.TilesX).Append(',').Append(k / CoverMap.TilesX).Append(' ');
            Assert.IsTrue(changed >= 1 && changed <= 8, "only the tiles around the 40 km disc: " + changed + " at " + where + " (disc tile " + tx + "," + ty + ", f1 " + f1.Number + " f2 " + f2.Number + ")");
        }

        [TestMethod]
        public void TheLayerThatTheFieldsMapHoldsIsTheDustLayerOnADustyBodyAndSnowOtherwise()
        {
            var snow = new CoverRules("snow", CoverKind.Snow);
            var sand = new CoverRules("sand", CoverKind.Sand);
            var dust = new CoverRules("dust", CoverKind.Dust);
            var wet = new BodyClimate("Wet", 3, 6e5, 9e6, 2e4, BodyStatus.Full, 1, covers: new[] { snow });
            Assert.AreEqual("snow", CoverBodies.Primary(wet).Id);
            var desert = new BodyClimate("Dry", 4, 3e5, 1e7, 5e4, BodyStatus.Full, 1, covers: new[] { snow, sand });
            Assert.AreEqual("sand", CoverBodies.Primary(desert).Id, "a body with sand is dusty: its map is sand");
            var dusty = new BodyClimate("Duna", 6, 3e5, 1e7, 5e4, BodyStatus.Full, 1, covers: new[] { dust });
            Assert.AreEqual("dust", CoverBodies.Primary(dusty).Id);
            var none = new BodyClimate("Rock", 7, 3e5, 1e7, 5e4, BodyStatus.Full, 1);
            Assert.IsNull(CoverBodies.Primary(none));
            Assert.IsNull(CoverBodies.Primary(null));
            var noneKind = new BodyClimate("Odd", 8, 3e5, 1e7, 5e4, BodyStatus.Full, 1, covers: new[] { CoverRules.None });
            Assert.IsNull(CoverBodies.Primary(noneKind), "a layer of kind None carries nothing");
        }

        [TestMethod]
        public void ADustLayerTakesTheFieldsFallOfItsOwnTypeOnly()
        {
            CoverDef snow = CoverDepthTests.Snow();
            var s = WeatherSample.Clear(1);
            s.Level01 = 0.8f;
            s.Type = PrecipType.Snow;
            s.SnowFraction = 1f;
            Assert.AreEqual(0.8, VehicleCover.FallOf(snow, s), 1e-6);
            s.Type = PrecipType.Rain;
            s.SnowFraction = 0f;
            s.RainFraction = 1f;
            Assert.AreEqual(0.0, VehicleCover.FallOf(snow, s), 1e-6, "rain is no snow");
            s.SnowFraction = 0.25f;
            Assert.AreEqual(0.2, VehicleCover.FallOf(snow, s), 1e-6, "sleet counts by its snow share");
            s.Type = PrecipType.Dust;
            Assert.AreEqual(0.0, VehicleCover.FallOf(CoverDepthTests.Sand(), s), 1e-6, "dust does not fall on sand");
            s.Type = PrecipType.Sand;
            Assert.AreEqual(0.8, VehicleCover.FallOf(CoverDepthTests.Sand(), s), 1e-6);
        }
    }
}
