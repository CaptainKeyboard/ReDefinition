using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // What the field says about the convection at a place (the generator's cell strength, level and tower height).
    internal struct CellSample
    {
        public float Strength, Level01, TopM, Coverage;
    }

    // Where the scan gets its numbers: the generator at a latitude and longitude and a time. Implemented over the generator state by
    // GeneratorCellSource; a test supplies its own.
    internal interface ICellSource
    {
        bool Sample(double latRad, double lonRad, double ut, out CellSample sample);
    }

    // The scan of the lattice around a point: the squares within a range that have a cell strong enough to flash. A scan of a few
    // hundred squares costs a few generator samples each, so it runs in steps of a few rows (Step) and the caller swaps the result in when
    // it is complete. The squares are rows of equal height with as many columns as fit the circle of latitude.
    internal sealed class LightningScanner
    {
        private readonly LightningParams p;
        private ICellSource source;
        private double bodyRadiusM, centreLatRad, centreLonRad, rangeM, ut;
        private long row, lastRow;
        private bool running;
        private List<LightningCell> found = new List<LightningCell>();

        internal LightningScanner(LightningParams p)
        {
            this.p = p;
        }

        internal bool Running { get { return running; } }
        // The result of the last complete scan, in the order of the rows.
        internal List<LightningCell> Result { get; private set; } = new List<LightningCell>();
        // The squares sampled in the scan that is running or was last complete.
        internal int Sampled { get; private set; }

        internal void Begin(ICellSource source, double bodyRadiusM, double latDeg, double lonDeg, double rangeM, double ut)
        {
            this.source = source;
            this.bodyRadiusM = bodyRadiusM;
            centreLatRad = latDeg * (Math.PI / 180.0);
            centreLonRad = lonDeg * (Math.PI / 180.0);
            this.rangeM = rangeM;
            this.ut = ut;
            double rowM = p.SquareM;
            double dLat = (rangeM + rowM) / bodyRadiusM;
            row = (long)Math.Floor((centreLatRad - dLat) * bodyRadiusM / rowM);
            lastRow = (long)Math.Floor((centreLatRad + dLat) * bodyRadiusM / rowM);
            found = new List<LightningCell>();
            Sampled = 0;
            running = true;
        }

        // Scans up to maxRows rows; true when the scan is complete (Result is then the new list).
        internal bool Step(int maxRows)
        {
            if (!running) return true;
            for (int n = 0; n < maxRows && row <= lastRow; n++, row++) ScanRow(row);
            if (row > lastRow)
            {
                Result = found;
                running = false;
                return true;
            }
            return false;
        }

        private void ScanRow(long j)
        {
            double rowM = p.SquareM;
            double latC = (j + 0.5) * rowM / bodyRadiusM;
            if (latC > Math.PI / 2.0 - 0.02 || latC < -Math.PI / 2.0 + 0.02) return;
            double cosC = Math.Cos(latC);
            double ring = 2.0 * Math.PI * bodyRadiusM * cosC;
            long cols = Math.Max(1L, (long)Math.Floor(ring / rowM));
            double dLon = 2.0 * Math.PI / cols;
            double northM = rowM;
            double eastM = bodyRadiusM * cosC * dLon;
            // The columns within the range: the longitude span of the range at this latitude, the whole ring where it reaches the pole.
            double dy = Math.Abs(latC - centreLatRad) * bodyRadiusM;
            if (dy > rangeM + rowM) return;
            double reach = Math.Sqrt(Math.Max(0.0, (rangeM + rowM) * (rangeM + rowM) - dy * dy));
            double lonSpan = cosC > 1e-6 ? reach / (bodyRadiusM * cosC) : Math.PI;
            long first, count;
            if (lonSpan >= Math.PI)
            {
                first = 0;
                count = cols;
            }
            else
            {
                first = (long)Math.Floor((centreLonRad - lonSpan + Math.PI) / dLon);
                count = (long)Math.Ceiling(2.0 * lonSpan / dLon) + 1;
                if (count > cols) count = cols;
            }
            for (long k = 0; k < count; k++)
            {
                long col = (((first + k) % cols) + cols) % cols;
                double lonC = -Math.PI + (col + 0.5) * dLon;
                double dist = GreatCircleM(centreLatRad, centreLonRad, latC, lonC);
                if (dist > rangeM + rowM) continue;
                CellSample s;
                Sampled++;
                if (!source.Sample(latC, lonC, ut, out s)) continue;
                if (!(s.Strength >= p.MinStrength) || !(s.Coverage > p.MinCoverage)) continue;
                var c = new LightningCell();
                c.Row = (int)j;
                c.Col = (int)col;
                c.LatDeg = latC * (180.0 / Math.PI);
                c.LonDeg = lonC * (180.0 / Math.PI);
                c.NorthM = northM;
                c.EastM = eastM;
                c.NorthDeg = northM / bodyRadiusM * (180.0 / Math.PI);
                c.EastDeg = dLon * (180.0 / Math.PI);
                c.Strength = s.Strength;
                c.Level01 = s.Level01;
                c.TopM = s.TopM;
                c.Coverage = s.Coverage;
                found.Add(c);
            }
        }

        // The great-circle distance in m on the sphere of the body's radius (haversine).
        internal double GreatCircleM(double lat1, double lon1, double lat2, double lon2)
        {
            double dp = lat2 - lat1, dl = lon2 - lon1;
            double a = Math.Sin(dp * 0.5) * Math.Sin(dp * 0.5) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dl * 0.5) * Math.Sin(dl * 0.5);
            a = a < 0.0 ? 0.0 : a > 1.0 ? 1.0 : a;
            return 2.0 * bodyRadiusM * Math.Asin(Math.Sqrt(a));
        }
    }
}
