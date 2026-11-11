using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Planner;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // D30: the planner's storm-on-route information: a table of legs against moving systems, with the intersection found, the length of
    // the stretch inside, the line the route panel shows, and nothing at all where the seam is empty.
    [TestClass]
    public class StormOnRouteTests
    {
        private const double H = D30Fixtures.H;
        private const double R = D30Fixtures.RadiusM;
        private const double KmPerDegree = R * Math.PI / 180.0 / 1000.0;

        private static ForecastTrack[] Tracks(params ForecastTrack[] tracks)
        {
            return tracks;
        }

        private static RouteLeg Leg(int number, double startH, double endH, double lat1, double lon1, double lat2, double lon2)
        {
            return new RouteLeg { Number = number, BodyIndex = 1, StartUt = startH * H, EndUt = endH * H, StartLatDeg = lat1, StartLonDeg = lon1, EndLatDeg = lat2, EndLonDeg = lon2 };
        }

        private static StormOnRouteInfo Find(IList<RouteLeg> legs, ForecastTrack[] tracks, PrecipType type = PrecipType.Snow)
        {
            return StormOnRoute.Find(legs, body => body == 1 ? tracks : null, body => R, (body, lat, lon, ut) => type);
        }

        [TestMethod]
        public void ALegThroughAStationarySystemHasOneEntryWithTheLengthInsideIt()
        {
            // A system 0.5 degrees wide centred on (0, 5); the leg runs east along the equator from 0 to 10 degrees in 10 hours.
            ForecastTrack[] tracks = Tracks(D30Fixtures.Track(9, 0.0, 0.0, 5.0, 0.0, 0.0, 40, (float)(KmPerDegree * 1000.0 * 1.0), 0.7f));
            var legs = new List<RouteLeg> { Leg(3, 0, 10, 0, 0, 0, 10) };
            StormOnRouteInfo info = Find(legs, tracks);
            Assert.AreEqual(1, info.Entries.Count);
            StormOnRouteEntry e = info.Entries[0];
            Assert.AreEqual(3, e.LegNumber);
            Assert.AreEqual(PrecipBand.Heavy, e.Band);
            Assert.AreEqual(9UL, e.CellKey);
            Assert.AreEqual(PrecipType.Snow, e.Type);
            // The footprint is 1 degree in radius: about 2 degrees of the leg lie inside it, sampled at a spacing of 0.1 degrees or finer.
            Assert.AreEqual(2.0 * KmPerDegree * 1000.0, e.InsideMetres, 0.25 * KmPerDegree * 1000.0);
            Assert.AreEqual(4.0 * H, e.Ut, 0.2 * H, "The leg enters the system around 4 degrees, 4 hours in.");
            string line = info.Lines()[0];
            Console.WriteLine("D30 route: " + line);
            StringAssert.StartsWith(line, "Leg 3 (UT ");
            StringAssert.Contains(line, "): Heavy snow, ");
            StringAssert.EndsWith(line, " km");
            Assert.IsTrue(info.HasMarker);
            Assert.AreEqual(9UL, info.Worst.CellKey);
        }

        [TestMethod]
        public void ASystemThatMovesAwayIsMissedAndALaterLegMeetsIt()
        {
            // The system leaves the leg's corridor to the north at 5 degrees an hour; leg 1 is flown before it, leg 2 after it came back south.
            var t = D30Fixtures.Track(4, 0.0, 0.0, 5.0, 0.0, 0.0, 60, 100000f, 0.5f);
            for (int i = 0; i < t.Lat.Length; i++) t.Lat[i] = i * 0.5 < 2.0 ? 30.0 : 0.0;
            var legs = new List<RouteLeg> { Leg(1, 0, 1, 0, 4, 0, 6), Leg(2, 10, 11, 0, 4, 0, 6) };
            StormOnRouteInfo info = Find(legs, Tracks(t));
            Assert.AreEqual(1, info.Entries.Count);
            Assert.AreEqual(2, info.Entries[0].LegNumber);
            Assert.AreEqual(PrecipBand.Moderate, info.Entries[0].Band);
        }

        [TestMethod]
        public void TwoSystemsOnOneLegAreTwoEntriesAndAWeakOneIsNoStorm()
        {
            ForecastTrack[] tracks = Tracks(
                D30Fixtures.Track(1, 0.0, 0.0, 3.0, 0.0, 0.0, 40, 150000f, 0.7f),
                D30Fixtures.Track(2, 0.0, 0.0, 7.0, 0.0, 0.0, 40, 150000f, 0.3f),
                D30Fixtures.Track(3, 0.0, 0.0, 9.0, 0.0, 0.0, 40, 150000f, 0.02f));
            StormOnRouteInfo info = Find(new List<RouteLeg> { Leg(1, 0, 10, 0, 0, 0, 10) }, tracks);
            Assert.AreEqual(2, info.Entries.Count);
            Assert.AreEqual(1UL, info.Entries[0].CellKey);
            Assert.AreEqual(2UL, info.Entries[1].CellKey);
            Assert.AreEqual(PrecipBand.Light, info.Entries[1].Band);
            Assert.AreEqual(PrecipBand.Heavy, info.Worst.Band);
        }

        [TestMethod]
        public void WithoutTracksOrWithTheSeamEmptyThereIsNothing()
        {
            var legs = new List<RouteLeg> { Leg(1, 0, 10, 0, 0, 0, 10) };
            Assert.AreEqual(0, Find(legs, null).Entries.Count);
            Assert.AreEqual(0, StormOnRoute.Find(null, b => null, b => R, null).Entries.Count);
            Func<int, ForecastTrack[]> tracks = WeatherHooks.BodyTracks;
            try
            {
                WeatherHooks.BodyTracks = null;
                Assert.IsNull(StormOnRoute.FindWithSeam(legs, b => R), "The planner shows no weather row without the atmospheres.");
            }
            finally
            {
                WeatherHooks.BodyTracks = tracks;
            }
        }

        [TestMethod]
        public void TheSeamPathReadsTheTypeFromTheForecastAndTheGeometryIsRight()
        {
            Func<int, ForecastTrack[]> tracks = WeatherHooks.BodyTracks;
            Func<ForecastSite, ForecastReport> forecast = WeatherHooks.Forecast;
            try
            {
                WeatherHooks.BodyTracks = b => new[] { D30Fixtures.Track(5, 0.0, 0.0, 5.0, 0.0, 0.0, 40, 150000f, 0.9f) };
                WeatherHooks.Forecast = site =>
                {
                    ForecastReport report = D30Fixtures.Report(0.0, PrecipBand.Heavy, PrecipBand.Heavy, PrecipBand.Heavy, PrecipBand.Heavy, PrecipBand.Heavy);
                    for (int i = 0; i < report.Steps.Length; i++) report.Steps[i].Type = PrecipType.Rain;
                    return report;
                };
                StormOnRouteInfo info = StormOnRoute.FindWithSeam(new List<RouteLeg> { Leg(1, 0, 4, 0, 0, 0, 10) }, b => R);
                Assert.AreEqual(1, info.Entries.Count);
                Assert.AreEqual(PrecipType.Rain, info.Entries[0].Type);
                StringAssert.Contains(info.Lines(ut => "day " + (int)(ut / 21600))[0], "day 0");
            }
            finally
            {
                WeatherHooks.BodyTracks = tracks;
                WeatherHooks.Forecast = forecast;
            }
            double lat, lon;
            Geo.Along(0.0, 0.0, 0.0, 10.0, 0.5, out lat, out lon);
            Assert.AreEqual(0.0, lat, 1e-9);
            Assert.AreEqual(5.0, lon, 1e-9);
            Geo.Along(0.0, 0.0, 90.0, 0.0, 0.5, out lat, out lon);
            Assert.AreEqual(45.0, lat, 1e-6, "Along a meridian the latitude is halved.");
            Assert.AreEqual(Math.PI / 2.0 * R, Geo.DistanceM(0.0, 0.0, 90.0, 0.0, R), 1e-3);
        }
    }
}
