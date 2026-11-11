using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // One weather system (a cell or a front's rain area) at one time.
    internal struct SystemPoint
    {
        public double LatDeg;
        public double LonDeg;
        public float RadiusM;
        public float Strength01;
        // Non-zero where the source knows the system's identity; the tracker keeps it as the track's key.
        public ulong Key;
    }

    // Optional on a field: it lists the weather systems of a body at a time directly, so that the forecast need not search the field for
    // them. A field that does not implement it is searched by GridScan. The list is all systems of the body.
    internal interface IWeatherSystemSource
    {
        // Appends the systems of the body at the time to the list; false where this field cannot list them.
        bool TryGetSystems(int bodyIndex, double ut, List<SystemPoint> into);
    }

    // Finds the systems in a field that cannot list them: local maxima of the precipitation level on a grid, with a radius from the
    // half-maximum along the grid axes and a position refined by a parabola through the maximum and its two neighbours per axis. The grid
    // is filled one row at a time so that a caller can spread a scan over frames (a row is at most a few dozen samples). A local grid is a
    // square around a place; the global grid covers the body in steps of GlobalDegrees. Pure; any thread; one scan object per thread.
    internal sealed class GridScan
    {
        // The level from which a maximum counts as a system.
        internal const float Threshold = PrecipScale.FlurriesFrom;
        // The spacing of the global grid in degrees.
        internal const double GlobalDegrees = 5.0;

        private readonly bool global;
        private readonly IWeatherField field;
        private readonly int bodyIndex;
        private readonly double radiusM;
        private readonly double altM;
        private readonly double centreLat, centreLon, spacingM;
        private readonly int half;
        private readonly double dLat, dLon;
        private readonly float[] values;
        private double ut;

        private GridScan(bool global, IWeatherField field, int bodyIndex, double radiusM, double altM, double centreLat, double centreLon, double rangeM, int half)
        {
            this.global = global;
            this.field = field;
            this.bodyIndex = bodyIndex;
            this.radiusM = radiusM;
            this.altM = altM;
            this.centreLat = centreLat;
            this.centreLon = centreLon;
            this.half = half;
            if (global)
            {
                Height = (int)Math.Round(180.0 / GlobalDegrees);
                Width = (int)Math.Round(360.0 / GlobalDegrees);
                dLat = 180.0 / Height;
                dLon = 360.0 / Width;
                spacingM = radiusM * GlobalDegrees * ForecastMath.DegToRad;
            }
            else
            {
                Width = Height = 2 * half + 1;
                spacingM = rangeM / half;
            }
            values = new float[Width * Height];
        }

        // A square of (2 half + 1)^2 samples over +-rangeM around a place.
        internal static GridScan Local(IWeatherField field, int bodyIndex, double bodyRadiusM, double centreLatDeg, double centreLonDeg, double altM,
            double rangeM, int half)
        {
            return new GridScan(false, field, bodyIndex, bodyRadiusM, altM, centreLatDeg, centreLonDeg, rangeM, half);
        }

        // The whole body in cells of GlobalDegrees.
        internal static GridScan Global(IWeatherField field, int bodyIndex, double bodyRadiusM, double altM)
        {
            return new GridScan(true, field, bodyIndex, bodyRadiusM, altM, 0.0, 0.0, 0.0, 0);
        }

        // The columns and the rows of the grid; a row is the unit of work.
        internal int Width { get; }
        internal int Height { get; }
        // The distance two samples are apart; a system may move this much more than its size between two times and still be the same.
        internal double SpacingM { get { return spacingM; } }

        // Starts a scan at a time.
        internal void Begin(double ut)
        {
            this.ut = ut;
        }

        // Samples one row of the grid.
        internal void FillRow(int iy)
        {
            for (int ix = 0; ix < Width; ix++)
            {
                double lat, lon;
                Position(ix, iy, 0.0, 0.0, out lat, out lon);
                WeatherSample s;
                field.Sample(bodyIndex, lat, lon, altM, ut, out s);
                float level = s.Level01;
                values[iy * Width + ix] = float.IsNaN(level) ? 0f : level;
            }
        }

        // Scans every row at a time and appends the maxima.
        internal void ScanAll(double ut, List<SystemPoint> into)
        {
            Begin(ut);
            for (int iy = 0; iy < Height; iy++) FillRow(iy);
            Collect(into);
        }

        // Appends the local maxima of the filled grid.
        internal void Collect(List<SystemPoint> into)
        {
            for (int iy = 1; iy < Height - 1; iy++)
            {
                int x0 = global ? 0 : 1, x1 = global ? Width : Width - 1;
                for (int ix = x0; ix < x1; ix++)
                {
                    float peak = values[iy * Width + ix];
                    if (peak < Threshold || !IsMaximum(ix, iy)) continue;
                    double ox = ParabolaOffset(At(ix - 1, iy), peak, At(ix + 1, iy));
                    double oy = ParabolaOffset(At(ix, iy - 1), peak, At(ix, iy + 1));
                    double cellX, cellY;
                    CellSize(iy, out cellX, out cellY);
                    float rx = HalfWidth(ix, iy, true, peak, cellX);
                    float ry = HalfWidth(ix, iy, false, peak, cellY);
                    double lat, lon;
                    Position(ix, iy, ox, oy, out lat, out lon);
                    var p = new SystemPoint();
                    p.LatDeg = lat;
                    p.LonDeg = lon;
                    p.RadiusM = 0.5f * (rx + ry);
                    p.Strength01 = peak;
                    into.Add(p);
                }
            }
        }

        private float At(int ix, int iy)
        {
            if (global) ix = (ix % Width + Width) % Width;
            return values[iy * Width + ix];
        }

        // The place of a grid cell plus an offset in cells.
        private void Position(int ix, int iy, double ox, double oy, out double latDeg, out double lonDeg)
        {
            if (global)
            {
                latDeg = -90.0 + (iy + 0.5 + oy) * dLat;
                lonDeg = ForecastMath.WrapLon(-180.0 + (ix + 0.5 + ox) * dLon);
            }
            else
            {
                ForecastMath.Offset(centreLat, centreLon, (ix - half + ox) * spacingM, (iy - half + oy) * spacingM, radiusM, out latDeg, out lonDeg);
            }
        }

        // The size of a cell in metres, east-west and north-south, in a row.
        private void CellSize(int iy, out double x, out double y)
        {
            if (!global)
            {
                x = y = spacingM;
                return;
            }
            double lat = -90.0 + (iy + 0.5) * dLat;
            y = radiusM * dLat * ForecastMath.DegToRad;
            x = Math.Max(radiusM * Math.Cos(lat * ForecastMath.DegToRad) * dLon * ForecastMath.DegToRad, 0.2 * y);
        }

        // Whether a cell is a local maximum of its eight neighbours; of equal neighbours only the first in reading order counts.
        private bool IsMaximum(int ix, int iy)
        {
            float c = values[iy * Width + ix];
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    float n = At(ix + dx, iy + dy);
                    if (n > c) return false;
                    if (n == c && (dy < 0 || (dy == 0 && dx < 0))) return false;
                }
            }
            return true;
        }

        private static double ParabolaOffset(float l, float c, float r)
        {
            float denom = l - 2f * c + r;
            if (!(denom < 0f)) return 0.0;
            double off = 0.5 * (l - r) / denom;
            return off < -0.5 ? -0.5 : off > 0.5 ? 0.5 : off;
        }

        // The distance in metres from the maximum to where the level falls under half of it, along an axis, averaged over both ways.
        private float HalfWidth(int ix, int iy, bool alongX, float peak, double unit)
        {
            double sum = 0.0;
            int guard = alongX ? Width : Height;
            for (int dir = -1; dir <= 1; dir += 2)
            {
                double dist = 0.0;
                int x = ix, y = iy;
                float prev = peak;
                for (int k = 0; k < guard; k++)
                {
                    int nx = alongX ? x + dir : x, ny = alongX ? y : y + dir;
                    if (ny < 0 || ny >= Height) break;
                    if (!global && (nx < 0 || nx >= Width)) break;
                    float cur = At(nx, ny);
                    if (cur < 0.5f * peak)
                    {
                        float span = prev - cur;
                        dist += (span > 0f ? (prev - 0.5f * peak) / span : 1.0) * unit;
                        break;
                    }
                    dist += unit;
                    prev = cur;
                    x = global ? (nx % Width + Width) % Width : nx;
                    y = ny;
                }
                sum += dist;
            }
            double r = 0.5 * sum;
            double floor = 0.5 * unit;
            return (float)(r < floor ? floor : r);
        }
    }
}
