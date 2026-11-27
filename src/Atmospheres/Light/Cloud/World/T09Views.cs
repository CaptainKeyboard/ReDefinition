using System;

namespace ReDefinition.Atmospheres
{
    // One view of the K-T2 series and the cloud viewer: metres above the terrain at the site, pitch in degrees above the horizon, heading in degrees, the camera's
    // distance from the vessel.
    internal struct T09View
    {
        internal string Name;
        internal double AglM, PitchDeg, HeadingDeg, CameraDistM;
        // A view that looks at something kilometres away does not need the vessel within 20 m (the placement) or 100 m (the dwell) of its point: HeldToleranceM above 0 is the
        // distance from the held point the view accepts, in both checks (the type views of start 60: the vessel stood 120 to 370 m off after a far placement while altitude, heading
        // and elevation were right). PlaceAttempts above 0 bounds the placements of the view (a failed view must cost little). Both 0: the series' own values.
        internal double HeldToleranceM;
        internal int PlaceAttempts;
    }

    // Whether the flight scene is ready for placements (the world adapter and the series probes ask it; a test replaces it, through SeriesProbe.FlightReadyState).
    internal static class T09Flight
    {
        internal static Func<bool> ReadyState = () => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.fetch != null && FlightGlobals.ActiveVessel != null;
    }

    // What the cloud viewer (shipped) and the K-T2 probe (ReDefinition.Atmospheres.Probes.dll, cleanup 2 C) share: the series' views, the horizon's dip, the mask that
    // draws no layer, and a point along a bearing on a sphere.
    internal static class T09Views
    {
        // The views of the series. The orbit view's pitch is the horizon's dip at that height, computed when it is placed. The two ground views are
        // 200 m up with the camera 100 m from the vessel, so that the camera (at most 100 x sin 38 = 62 m below it) stays above the terrain.
        internal static readonly T09View[] All =
        {
            new T09View { Name = "ground", AglM = 200.0, PitchDeg = 18.0, HeadingDeg = 90.0, CameraDistM = 100.0 },
            new T09View { Name = "inside", AglM = 4550.0, PitchDeg = 8.0, HeadingDeg = 90.0, CameraDistM = 150.0 },
            new T09View { Name = "above", AglM = 8300.0, PitchDeg = -25.0, HeadingDeg = 90.0, CameraDistM = 150.0 },
            new T09View { Name = "orbit_edge", AglM = 300000.0, PitchDeg = double.NaN, HeadingDeg = 90.0, CameraDistM = 150.0 },
            new T09View { Name = "sky3", AglM = 200.0, PitchDeg = 38.0, HeadingDeg = 270.0, CameraDistM = 100.0 }
        };

        // The distance views (master plan 46.4, the probe's -t09FarViews): the 3D clouds seen from 300, 1,000, 3,000 and 10,000 km over the site, each looking at the limb
        // as the orbit view does (the planet's disc fills the lower half of the picture and shrinks with the height). The vessel may stand a kilometre off its point (the
        // thing looked at is hundreds of kilometres away) and a view that cannot be placed costs two attempts.
        internal static readonly T09View[] Far =
        {
            new T09View { Name = "far300", AglM = 300000.0, PitchDeg = double.NaN, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 },
            new T09View { Name = "far1000", AglM = 1000000.0, PitchDeg = double.NaN, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 },
            new T09View { Name = "far3000", AglM = 3000000.0, PitchDeg = double.NaN, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 },
            new T09View { Name = "far10000", AglM = 10000000.0, PitchDeg = double.NaN, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 }
        };

        // The approach views (master plan 46.4, the probe's -t09ApproachViews; the owner: nothing may pop on the approach): one place seen 60 degrees below the
        // horizontal from 400 km down to 130 km, the centre ray's distance from about 460 km to 150 km: through the band in which the field's far mean gives way
        // to the single clouds (312 to 156 km of distance at 1707 x 960).
        internal static readonly T09View[] Approach =
        {
            new T09View { Name = "app400", AglM = 400000.0, PitchDeg = -60.0, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 },
            new T09View { Name = "app320", AglM = 320000.0, PitchDeg = -60.0, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 },
            new T09View { Name = "app260", AglM = 260000.0, PitchDeg = -60.0, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 },
            new T09View { Name = "app210", AglM = 210000.0, PitchDeg = -60.0, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 },
            new T09View { Name = "app170", AglM = 170000.0, PitchDeg = -60.0, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 },
            new T09View { Name = "app130", AglM = 130000.0, PitchDeg = -60.0, HeadingDeg = 90.0, CameraDistM = 150.0, HeldToleranceM = 1000.0, PlaceAttempts = 2 }
        };

        // The layer mask that draws no layer (a bit above every layer index).
        internal const int NoLayersMask = 1 << 20;

        // The angle below the horizontal at which the horizon lies for an eye at the height over a body of the radius (degrees, positive).
        internal static double HorizonDipDeg(double radius, double heightM)
        {
            return Math.Acos(radius / (radius + Math.Max(0.0, heightM))) * 180.0 / Math.PI;
        }

        internal static double Rad(double deg) { return deg * Math.PI / 180.0; }

        internal static double WrapDeg(double d)
        {
            while (d > 180.0) d -= 360.0;
            while (d < -180.0) d += 360.0;
            return d;
        }

        // The point at km (negative: behind) along the bearing from a place, on a sphere of the body's radius.
        internal static void Destination(double latDeg, double lonDeg, double bearingDeg, double km, double radiusM, out double lat2, out double lon2)
        {
            double d = km * 1000.0 / radiusM, phi = Rad(latDeg), lam = Rad(lonDeg), th = Rad(bearingDeg);
            double sinPhi2 = Math.Sin(phi) * Math.Cos(d) + Math.Cos(phi) * Math.Sin(d) * Math.Cos(th);
            double phi2 = Math.Asin(Math.Max(-1.0, Math.Min(1.0, sinPhi2)));
            double lam2 = lam + Math.Atan2(Math.Sin(th) * Math.Sin(d) * Math.Cos(phi), Math.Cos(d) - Math.Sin(phi) * sinPhi2);
            lat2 = phi2 * 180.0 / Math.PI;
            lon2 = WrapDeg(lam2 * 180.0 / Math.PI);
        }
    }
}
