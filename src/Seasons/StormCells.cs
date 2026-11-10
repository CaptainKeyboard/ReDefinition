using System;
using System.Globalization;

namespace ReDefinition.Seasons
{
    // The snow storms as cells that stand on the planet. A cell is a place (latitude, longitude) with a radius of 5 to 25 km and a life
    // (ramp up, peak, decay: the envelope of StormModel), and it drifts with the wind. Everything is a pure function of the seed and the
    // universal time, so the game and the tests get the same cells.
    //
    // The spawn rule is the one of StormModel (Draw): slots of 24 Kerbin hours per hemisphere and per 10-degree band of latitude, and a
    // chance from the winter gate and the frequency setting. Each band is also cut into sectors of about SectorKm along the parallel,
    // and each sector has its own draw in each slot. So a band holds many cells at once. A cell lies wholly inside its slot in time.
    // Its place starts at a point of its sector and moves along a straight line, passing that point in the middle of its life.
    internal static class StormCells
    {
        // The width of a spawn sector along the parallel, in km. The cell sits anywhere inside its sector at the middle of its life.
        internal const double SectorKm = 100;

        // The radius of a cell is the setting times a factor from this to that, kept between the setting's limits (SeasonsSettings).
        internal const double RadiusFactorMin = 0.6, RadiusFactorMax = 1.4;

        // The wind speed that moves a cell, in m/s. A cell moves 5 to 15 km an hour, so it is a place that travels, slowly.
        internal const double SpeedMinMs = 1.5, SpeedMaxMs = 4;

        // The cell drifts east, as the prevailing wind of the middle latitudes does, turned by up to this much to either side (radians).
        internal const double HeadingSpread = 0.9;

        // The cloud base above the ground at the cell's centre, in m.
        internal const double BaseAglMinM = 1500, BaseAglMaxM = 2500;

        // The soft edge as a share of the radius: full intensity inside 80 %, nothing at the radius.
        internal const double EdgeShare = 0.2;

        // The number of bands of latitude (0 to 10 degrees, ..., 80 to 90) and the highest latitude a cell starts at.
        internal const int Bands = 9;
        internal const double HighestStartLatitude = 85;

        // The most that a cell drifts from its starting point, in m: half of the longest life at the highest speed. A cell drifts at 1.5
        // to 4 m/s, which is 5 to 15 km an hour, toward the east turned by up to 0.9 rad. That is at most 122 km.
        internal static readonly double MaxDriftM = SpeedMaxMs * 0.5 * (StormModel.RampUpMaxHours + StormModel.PeakMaxHours + StormModel.DecayMaxHours) * StormModel.HourSeconds;

        // The room of the array that Collect fills. At the default frequency about 18 cells stand on the planet in a northern midwinter
        // (counted from the model, not seen in the game).
        internal const int Capacity = 64;

        // One cell at a time: where it is, how big, how strong.
        internal struct Cell
        {
            internal ulong Key;
            // The cell of the diagnostics setting (forceStormCell).
            internal bool Forced;
            internal double Latitude, Longitude;
            internal double RadiusM;
            // The peak intensity of its storm, 0 to 1, and the envelope now, 0 to 1.
            internal double Height, Envelope;
            // The drift: the heading in radians clockwise from north, and the speed in m/s.
            internal double Heading, SpeedMs;
            // The height of the cloud base above the ground at the centre, in m.
            internal double BaseAglM;
            // The seconds since the storm began.
            internal double AgeSeconds;
            // The great-circle distance from the point asked about to the centre, in m (set by Collect).
            internal double DistanceM;

            // The intensity of the cell's storm at its centre now.
            internal double Strength
            {
                get { return Height * Envelope; }
            }
        }

        // What the cells depend on besides the time.
        internal struct Rules
        {
            internal double BodyRadius, YearSeconds, ForcedPhase, WinterLine, Frequency, RadiusKm;
            internal int Seed;
            // Whether the schedule makes cells (the storms switch); the forced cell does not depend on it.
            internal bool Scheduled;
            // The cell of the diagnostics setting: at its place with the radius, at its peak.
            internal bool HasForced;
            internal double ForcedLatitude, ForcedLongitude, ForcedRadiusKm;
        }

        // The number of sectors in a band: the parallel through its middle, in sectors of SectorKm.
        internal static int Sectors(int band, double bodyRadius)
        {
            double middle = Geo.ToRad((band + 0.5) * StormModel.BandDegrees);
            double length = 2 * Math.PI * bodyRadius * Math.Cos(middle);
            return Math.Max(1, (int)Math.Round(length / (SectorKm * 1000)));
        }

        // Writes the cells whose reach touches the circle of rangeM around the point into cells, with their state at the time, and
        // returns how many. A cell with no storm in this slot, or before or after its storm, is not there.
        internal static int Collect(Cell[] cells, double universalTime, double latitude, double longitude, double rangeM, Rules rules)
        {
            int count = 0;
            if (cells == null || double.IsNaN(universalTime) || double.IsInfinity(universalTime) || double.IsNaN(latitude) || double.IsNaN(longitude)) return 0;
            double radius = rules.BodyRadius > 0 ? rules.BodyRadius : 600000;
            double latQuery = Mathx.ClampOrMin(latitude, -90, 90);
            if (rules.HasForced && count < cells.Length)
            {
                Cell forced = Forced(rules);
                forced.DistanceM = Distance(latQuery, longitude, forced.Latitude, forced.Longitude, radius);
                if (forced.DistanceM <= rangeM + forced.RadiusM) cells[count++] = forced;
            }
            if (!rules.Scheduled) return count;

            double slotSeconds = StormModel.SlotHours * StormModel.HourSeconds;
            double slot = Math.Floor(universalTime / slotSeconds);
            double phase = rules.ForcedPhase >= 0 && rules.ForcedPhase < 1 ? rules.ForcedPhase : SeasonClock.Phase((slot + 0.5) * slotSeconds, rules.YearSeconds);
            double maxRadiusM = SeasonsSettings.StormCellRadiusMaxKm * 1000;
            double reach = rangeM + maxRadiusM + MaxDriftM;
            double degreesPerM = 180 / (Math.PI * radius);
            for (int h = 0; h < 2; h++)
            {
                bool north = h == 0;
                double sign = north ? 1 : -1;
                for (int band = 0; band < Bands; band++)
                {
                    double gate = StormModel.Gate((band + 0.5) * StormModel.BandDegrees * sign, phase, rules.WinterLine);
                    if (!(gate > 0)) continue;
                    // The starting latitudes of the band; skip it when the circle cannot reach it.
                    double low = band * StormModel.BandDegrees * sign, high = (band + 1) * StormModel.BandDegrees * sign;
                    double lo = Math.Min(low, high), hi = Math.Max(low, high);
                    double gap = latQuery < lo ? lo - latQuery : latQuery > hi ? latQuery - hi : 0;
                    if (gap > reach * degreesPerM) continue;
                    ulong slotKey = StormModel.SlotKey(rules.Seed, north, band, slot);
                    int sectors = Sectors(band, radius);
                    for (int s = 0; s < sectors; s++)
                    {
                        ulong key = StormModel.Mix(slotKey, (ulong)s + 4001UL);
                        StormModel.Storm storm = StormModel.Draw(key, slot, gate, rules.Frequency);
                        if (!storm.Present) continue;
                        Cell cell;
                        if (!At(out cell, storm, north, band, s, sectors, universalTime, rules, radius)) continue;
                        cell.DistanceM = Distance(latQuery, longitude, cell.Latitude, cell.Longitude, radius);
                        if (cell.DistanceM > rangeM + cell.RadiusM) continue;
                        if (count >= cells.Length) return count;
                        cells[count++] = cell;
                    }
                }
            }
            return count;
        }

        // The cell of a storm of a sector at a time; false before and after its storm.
        private static bool At(out Cell cell, StormModel.Storm storm, bool north, int band, int sector, int sectors, double universalTime, Rules rules, double bodyRadius)
        {
            cell = default(Cell);
            double age = universalTime - storm.Start;
            double envelope = StormModel.Envelope(age, storm.Up, storm.Peak, storm.Down);
            if (!(envelope > 0)) return false;
            ulong key = storm.Key;
            double sign = north ? 1 : -1;
            double startLatitude = sign * Math.Min(HighestStartLatitude, band * StormModel.BandDegrees + StormModel.Unit(key, 6) * StormModel.BandDegrees);
            double startLongitude = -180 + (sector + StormModel.Unit(key, 7)) * 360.0 / sectors;
            double heading = Math.PI / 2 + (StormModel.Unit(key, 8) - 0.5) * 2 * HeadingSpread;
            double speed = Mathx.Lerp(SpeedMinMs, SpeedMaxMs, StormModel.Unit(key, 9));
            double sinceMiddle = universalTime - (storm.Start + (storm.Up + storm.Peak + storm.Down) * 0.5);
            double northM = Math.Cos(heading) * speed * sinceMiddle;
            double eastM = Math.Sin(heading) * speed * sinceMiddle;
            double latitude = startLatitude + Geo.ToDeg(northM / bodyRadius);
            double cosStart = Math.Max(0.2, Math.Cos(Geo.ToRad(startLatitude)));
            double longitude = WrapLongitude(startLongitude + Geo.ToDeg(eastM / (bodyRadius * cosStart)));
            cell.Key = key;
            cell.Latitude = Mathx.ClampOrMin(latitude, -89.5, 89.5);
            cell.Longitude = longitude;
            cell.RadiusM = RadiusM(rules.RadiusKm, StormModel.Unit(key, 10));
            cell.Height = storm.Height;
            cell.Envelope = envelope;
            cell.Heading = heading;
            cell.SpeedMs = speed;
            cell.BaseAglM = Mathx.Lerp(BaseAglMinM, BaseAglMaxM, StormModel.Unit(key, 11));
            cell.AgeSeconds = age;
            return true;
        }

        // The radius of a cell from the setting (km) and a unit random value, in m, between the setting's limits.
        internal static double RadiusM(double settingKm, double unit)
        {
            double km = (double.IsNaN(settingKm) ? SeasonsSettings.StormCellRadiusDefaultKm : settingKm) * Mathx.Lerp(RadiusFactorMin, RadiusFactorMax, unit);
            return Mathx.ClampOrMin(km, SeasonsSettings.StormCellRadiusMinKm, SeasonsSettings.StormCellRadiusMaxKm) * 1000;
        }

        // The cell of the diagnostics setting: standing at its place at its peak.
        internal static Cell Forced(Rules rules)
        {
            return new Cell
            {
                Key = 1UL,
                Forced = true,
                Latitude = Mathx.ClampOrMin(rules.ForcedLatitude, -89.5, 89.5),
                Longitude = WrapLongitude(rules.ForcedLongitude),
                RadiusM = Mathx.ClampOrMin(rules.ForcedRadiusKm, SeasonsSettings.StormCellRadiusMinKm, SeasonsSettings.StormCellRadiusMaxKm) * 1000,
                Height = 1,
                Envelope = 1,
                BaseAglM = 2000,
                AgeSeconds = 0
            };
        }

        // The intensity of a cell at a place, 0 to 1: its strength inside 80 % of the radius, falling smoothly to 0 at the radius.
        internal static double IntensityAt(ref Cell cell, double latitude, double longitude, double bodyRadius)
        {
            return cell.Strength * Shape(Distance(latitude, longitude, cell.Latitude, cell.Longitude, bodyRadius), cell.RadiusM);
        }

        // The largest intensity of the cells at a place; 0 for no cells.
        internal static double IntensityAt(Cell[] cells, int count, double latitude, double longitude, double bodyRadius)
        {
            double best = 0;
            for (int i = 0; i < count; i++)
            {
                double v = IntensityAt(ref cells[i], latitude, longitude, bodyRadius);
                if (v > best) best = v;
            }
            return best;
        }

        // 1 inside (1 - EdgeShare) of the radius, 0 from the radius, smooth in between.
        internal static double Shape(double distance, double radius)
        {
            if (!(radius > 0) || double.IsNaN(distance)) return 0;
            double inner = radius * (1 - EdgeShare);
            if (distance <= inner) return 1;
            if (distance >= radius) return 0;
            return 1 - Mathx.SmoothStep(inner, radius, distance);
        }

        // Keeps the nearest max cells (by DistanceM minus the radius, the gap to the cell's edge), sorted nearest first, and returns
        // their number. No allocation.
        internal static int Nearest(Cell[] cells, int count, int max)
        {
            for (int i = 1; i < count; i++)
            {
                Cell x = cells[i];
                double gap = x.DistanceM - x.RadiusM;
                int j = i - 1;
                while (j >= 0 && cells[j].DistanceM - cells[j].RadiusM > gap)
                {
                    cells[j + 1] = cells[j];
                    j--;
                }
                cells[j + 1] = x;
            }
            return Math.Max(0, Math.Min(count, max));
        }

        // The great-circle distance between two places in degrees, on a sphere of that radius, in m.
        internal static double Distance(double latitudeA, double longitudeA, double latitudeB, double longitudeB, double bodyRadius)
        {
            double p1 = Geo.ToRad(latitudeA), p2 = Geo.ToRad(latitudeB);
            double dp = p2 - p1, dl = Geo.ToRad(longitudeB - longitudeA);
            double sp = Math.Sin(dp * 0.5), sl = Math.Sin(dl * 0.5);
            double a = sp * sp + Math.Cos(p1) * Math.Cos(p2) * sl * sl;
            return 2 * bodyRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        // A longitude in degrees brought into -180 to 180.
        internal static double WrapLongitude(double degrees)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return 0;
            double d = (degrees + 180) % 360;
            if (d < 0) d += 360;
            return d - 180;
        }

        // "latitude,longitude,radiusKm" (the forceStormCell setting) into numbers. "off", an empty text and anything that is not three
        // numbers give false. The latitude is held between -90 and 90, the longitude wrapped, the radius between the limits.
        internal static bool TryParseForced(string text, out double latitude, out double longitude, out double radiusKm)
        {
            latitude = longitude = radiusKm = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 3) return false;
            var culture = CultureInfo.InvariantCulture;
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, culture, out latitude)
                || !double.TryParse(parts[1].Trim(), NumberStyles.Float, culture, out longitude)
                || !double.TryParse(parts[2].Trim(), NumberStyles.Float, culture, out radiusKm)) return false;
            if (double.IsNaN(latitude) || double.IsInfinity(latitude) || double.IsNaN(longitude) || double.IsInfinity(longitude)
                || double.IsNaN(radiusKm) || double.IsInfinity(radiusKm)) return false;
            latitude = Mathx.ClampOrMin(latitude, -90, 90);
            longitude = WrapLongitude(longitude);
            radiusKm = Mathx.ClampOrMin(radiusKm, SeasonsSettings.StormCellRadiusMinKm, SeasonsSettings.StormCellRadiusMaxKm);
            return true;
        }

    }
}
