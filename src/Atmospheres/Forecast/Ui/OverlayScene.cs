using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // A point on the screen in pixels, origin at the lower left as Unity's screen space has it.
    internal struct P2
    {
        internal float X, Y;

        internal P2(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    // Puts a place on a body onto the screen of the map camera. False where the place is behind the camera or on the far side of the body.
    internal interface IMapProjector
    {
        bool Project(int bodyIndex, double latDeg, double lonDeg, out float x, out float y);
    }

    // The shape of one thing the overlay draws.
    internal enum OverlayKind
    {
        // The outline of a system's footprint now.
        Ring = 0,
        // The path of a system over the horizon.
        Track = 1,
        // A short bar across the path at a full hour.
        Tick = 2,
        // A coarse dot of the precipitation map (a body without tracks).
        Dot = 3
    }

    // A polyline on the screen with the band it is drawn for.
    internal sealed class OverlayLine
    {
        internal OverlayKind Kind;
        internal PrecipBand Band;
        internal readonly List<P2> Points = new List<P2>();
    }

    // A system's footprint as the screen shows it: for the hover label and for the probe's check of its place.
    internal sealed class OverlayRing
    {
        internal ulong Key;
        internal P2 Centre;
        internal float RadiusPx;
        internal PrecipBand Band;
        internal float Strength01;
        internal double LatDeg, LonDeg;
        internal ForecastTrack Track;
    }

    // The limits of what the overlay draws.
    internal sealed class OverlayRules
    {
        // The most systems drawn (the capacity of the engine's tracks).
        internal int Capacity = 64;
        // Above this many systems, the rings that are smaller than MinRingPixels on the screen are left out (zoomed out, they only clutter).
        internal int ClutterSystems = 24;
        internal float MinRingPixels = 6f;
        // The spacing of the tick marks along a path.
        internal double TickSeconds = 3600.0;
        internal float TickPixels = 6f;
        internal int RingPoints = 28;
        // Systems below this strength are not drawn.
        internal float MinStrength01 = PrecipScale.FlurriesFrom;
    }

    // Everything the overlay draws in one frame.
    internal sealed class OverlayScene
    {
        internal readonly List<OverlayLine> Lines = new List<OverlayLine>();
        internal readonly List<OverlayRing> Rings = new List<OverlayRing>();
        // Systems left out for the capacity or for the clutter rule.
        internal int Hidden;
        internal int BodyIndex = -1;
        internal double Ut;

        internal void Clear()
        {
            Lines.Clear();
            Rings.Clear();
            Hidden = 0;
        }

        // The ring of a system, or null.
        internal OverlayRing RingOf(ulong key)
        {
            for (int i = 0; i < Rings.Count; i++)
            {
                if (Rings[i].Key == key) return Rings[i];
            }
            return null;
        }

        // The ring a pointer is on (inside it, or within a few pixels of a small one); the nearest centre wins.
        internal OverlayRing Pick(float x, float y, float slackPixels = 8f)
        {
            OverlayRing best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < Rings.Count; i++)
            {
                OverlayRing r = Rings[i];
                float dx = x - r.Centre.X, dy = y - r.Centre.Y;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                if (d > Math.Max(r.RadiusPx, slackPixels) || d >= bestD) continue;
                best = r;
                bestD = d;
            }
            return best;
        }
    }

    // The overlay's maths: from the tracks of a body, a projector and the time, the rings, the paths and the ticks on the screen. Pure.
    // The positions are projected one by one, so a point that does not project (behind the camera, the far side of the body) ends a line.
    internal static class OverlayBuilder
    {
        // The place a distance away on a bearing: the great-circle destination on a body of that radius.
        internal static void Destination(double latDeg, double lonDeg, double bearingDeg, double distanceM, double radiusM, out double lat2Deg,
            out double lon2Deg)
        {
            double d = distanceM / radiusM;
            double b = bearingDeg * ForecastMath.DegToRad;
            double p1 = latDeg * ForecastMath.DegToRad, l1 = lonDeg * ForecastMath.DegToRad;
            double sinP2 = Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(b);
            if (sinP2 > 1.0) sinP2 = 1.0;
            if (sinP2 < -1.0) sinP2 = -1.0;
            double p2 = Math.Asin(sinP2);
            double l2 = l1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * sinP2);
            lat2Deg = p2 * ForecastMath.RadToDeg;
            lon2Deg = ForecastMath.WrapLon(l2 * ForecastMath.RadToDeg);
        }

        // Fills the scene for the tracks of a body at the time now, drawn up to the horizon. siteLatDeg/siteLonDeg are not used for the
        // drawing; the hover label uses them. A null projector or no tracks leaves the scene empty.
        internal static void Build(OverlayScene scene, ForecastTrack[] tracks, IMapProjector projector, int bodyIndex, double bodyRadiusM, double nowUt,
            double horizonHours, OverlayRules rules)
        {
            scene.Clear();
            scene.BodyIndex = bodyIndex;
            scene.Ut = nowUt;
            if (tracks == null || projector == null || bodyRadiusM <= 0.0) return;
            rules = rules ?? new OverlayRules();
            var alive = new List<KeyValuePair<float, int>>();
            for (int t = 0; t < tracks.Length; t++)
            {
                double lat, lon;
                float radius, strength;
                if (!SystemTracker.PositionAt(tracks[t], nowUt, out lat, out lon, out radius, out strength)) continue;
                if (strength < rules.MinStrength01) continue;
                alive.Add(new KeyValuePair<float, int>(strength, t));
            }
            // The strongest first; the capacity cuts the rest.
            alive.Sort((a, b) => b.Key.CompareTo(a.Key));
            if (alive.Count > rules.Capacity)
            {
                scene.Hidden += alive.Count - rules.Capacity;
                alive.RemoveRange(rules.Capacity, alive.Count - rules.Capacity);
            }
            bool cluttered = alive.Count > rules.ClutterSystems;
            double endUt = nowUt + horizonHours * 3600.0;
            for (int i = 0; i < alive.Count; i++)
            {
                ForecastTrack track = tracks[alive[i].Value];
                double lat, lon;
                float radius, strength;
                SystemTracker.PositionAt(track, nowUt, out lat, out lon, out radius, out strength);
                float cx, cy;
                if (!projector.Project(bodyIndex, lat, lon, out cx, out cy)) continue;
                float rpx = ScreenRadius(projector, bodyIndex, lat, lon, radius, bodyRadiusM, cx, cy);
                if (cluttered && rpx < rules.MinRingPixels)
                {
                    scene.Hidden++;
                    continue;
                }
                PrecipBand band = PrecipScale.BandOf(strength, 0f);
                scene.Rings.Add(new OverlayRing
                {
                    Key = track.CellKey, Centre = new P2(cx, cy), RadiusPx = rpx, Band = band, Strength01 = strength, LatDeg = lat, LonDeg = lon, Track = track
                });
                AddRing(scene, projector, bodyIndex, lat, lon, radius, bodyRadiusM, band, rules.RingPoints);
                AddPath(scene, projector, bodyIndex, track, nowUt, endUt, band, rules);
            }
        }

        // The radius of a footprint on the screen: the distance from its centre to a point of its edge, east of it.
        internal static float ScreenRadius(IMapProjector projector, int bodyIndex, double lat, double lon, float radiusM, double bodyRadiusM, float cx, float cy)
        {
            double lat2, lon2;
            Destination(lat, lon, 90.0, radiusM, bodyRadiusM, out lat2, out lon2);
            float ex, ey;
            if (!projector.Project(bodyIndex, lat2, lon2, out ex, out ey))
            {
                Destination(lat, lon, 0.0, radiusM, bodyRadiusM, out lat2, out lon2);
                if (!projector.Project(bodyIndex, lat2, lon2, out ex, out ey)) return 0f;
            }
            float dx = ex - cx, dy = ey - cy;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static void AddRing(OverlayScene scene, IMapProjector projector, int bodyIndex, double lat, double lon, float radiusM, double bodyRadiusM,
            PrecipBand band, int points)
        {
            OverlayLine line = null;
            for (int k = 0; k <= points; k++)
            {
                double lat2, lon2;
                Destination(lat, lon, 360.0 * k / points, radiusM, bodyRadiusM, out lat2, out lon2);
                float x, y;
                if (projector.Project(bodyIndex, lat2, lon2, out x, out y))
                {
                    if (line == null)
                    {
                        line = new OverlayLine { Kind = OverlayKind.Ring, Band = band };
                        scene.Lines.Add(line);
                    }
                    line.Points.Add(new P2(x, y));
                }
                else
                {
                    line = null;
                }
            }
        }

        private static void AddPath(OverlayScene scene, IMapProjector projector, int bodyIndex, ForecastTrack track, double nowUt, double endUt, PrecipBand band,
            OverlayRules rules)
        {
            int n = track.Ut.Length;
            if (n < 2) return;
            double from = Math.Max(nowUt, track.Ut[0]);
            double to = Math.Min(endUt, track.Ut[n - 1]);
            if (to <= from) return;
            // The sample points between now and the end, plus the two ends.
            var times = new List<double> { from };
            for (int i = 0; i < n; i++)
            {
                if (track.Ut[i] > from + 1.0 && track.Ut[i] < to - 1.0) times.Add(track.Ut[i]);
            }
            times.Add(to);
            OverlayLine line = null;
            for (int i = 0; i < times.Count; i++)
            {
                double lat, lon;
                float radius, strength;
                float x, y;
                if (SystemTracker.PositionAt(track, times[i], out lat, out lon, out radius, out strength) && projector.Project(bodyIndex, lat, lon, out x, out y))
                {
                    if (line == null)
                    {
                        line = new OverlayLine { Kind = OverlayKind.Track, Band = band };
                        scene.Lines.Add(line);
                    }
                    line.Points.Add(new P2(x, y));
                }
                else
                {
                    line = null;
                }
            }
            // A bar across the path at every full tick after now.
            double first = Math.Ceiling(from / rules.TickSeconds) * rules.TickSeconds;
            for (double t = first; t <= to; t += rules.TickSeconds)
            {
                if (t <= from + 1.0) continue;
                AddTick(scene, projector, bodyIndex, track, t, band, rules.TickPixels);
            }
        }

        private static void AddTick(OverlayScene scene, IMapProjector projector, int bodyIndex, ForecastTrack track, double ut, PrecipBand band, float pixels)
        {
            double lat, lon, latB, lonB;
            float radius, strength, x, y, xb, yb;
            if (!SystemTracker.PositionAt(track, ut, out lat, out lon, out radius, out strength)) return;
            if (!SystemTracker.PositionAt(track, Math.Min(ut + 600.0, track.Ut[track.Ut.Length - 1]), out latB, out lonB, out radius, out strength)) return;
            if (!projector.Project(bodyIndex, lat, lon, out x, out y) || !projector.Project(bodyIndex, latB, lonB, out xb, out yb)) return;
            float dx = xb - x, dy = yb - y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-3f) return;
            // The bar stands across the direction of travel.
            float nx = -dy / len * pixels, ny = dx / len * pixels;
            var line = new OverlayLine { Kind = OverlayKind.Tick, Band = band };
            line.Points.Add(new P2(x - nx, y - ny));
            line.Points.Add(new P2(x + nx, y + ny));
            scene.Lines.Add(line);
        }

        // The label of a ring for the pointer: its level and the time it takes to reach the site, or that it does not.
        internal static string HoverLabel(OverlayRing ring, double siteLatDeg, double siteLonDeg, string siteName, double nowUt, double bodyRadiusM)
        {
            string head = ForecastFormat.LevelName(ring.Band);
            if (ring.Track == null || bodyRadiusM <= 0.0) return head;
            string where = string.IsNullOrEmpty(siteName) ? "the site" : siteName;
            double arrival = SystemTracker.ArrivalOf(ring.Track, siteLatDeg, siteLonDeg, nowUt, bodyRadiusM);
            if (double.IsNaN(arrival)) return head + ", does not reach " + where;
            return arrival <= nowUt + 1.0 ? head + ", over " + where : head + ", reaches " + where + " " + LaunchConditions.InText(arrival - nowUt);
        }

        // A coarse map of precipitation as dots, for a body whose systems are not tracked (the field's map, sampled on a grid). The dots go
        // to the scene as one-point lines; their band follows the level.
        internal static void AddMapDots(OverlayScene scene, WeatherMap map, IMapProjector projector, int bodyIndex, int columns, int rows, float minLevel)
        {
            scene.Clear();
            scene.BodyIndex = bodyIndex;
            if (map == null || projector == null || map.Width < 1 || map.Height < 1) return;
            for (int r = 0; r < rows; r++)
            {
                double lat = 90.0 - (r + 0.5) * 180.0 / rows;
                for (int c = 0; c < columns; c++)
                {
                    double lon = -180.0 + (c + 0.5) * 360.0 / columns;
                    float level = map.Sample(lat, lon);
                    if (!(level >= minLevel)) continue;
                    float x, y;
                    if (!projector.Project(bodyIndex, lat, lon, out x, out y)) continue;
                    var dot = new OverlayLine { Kind = OverlayKind.Dot, Band = PrecipScale.BandOf(level, 0f) };
                    dot.Points.Add(new P2(x, y));
                    scene.Lines.Add(dot);
                }
            }
        }

        // Whether a point on a sphere is on the side the camera sees: the point relative to the centre and the camera relative to the
        // centre, in any consistent unit; a point on the sphere of radius r is seen when its dot product with the camera exceeds r squared.
        internal static bool FacesCamera(double px, double py, double pz, double cx, double cy, double cz, double radius)
        {
            return px * cx + py * cy + pz * cz > radius * radius;
        }
    }
}
