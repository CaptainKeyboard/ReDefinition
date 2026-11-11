using System;

namespace AtmoSim.Core
{
    // The switches of the layered cloud fields (package E43). Everything is a pure function of (body, seed, position, UT); nothing is stored.
    public sealed class LayerOptions
    {
        // Layers of clouds at different heights turn against each other by the wind of their height (HeightWind.LayerRate); off = all layers move with the generator's patterns.
        public bool Shear = true;
        // Towers grow in the first GrowFraction of the life of a cell and sink in the last part; off = the tower has its full height for the whole life.
        public bool Vertical = true;
        // The top of a tower and its anvil are shifted downwind by the wind shear aloft (TiltTimeS x the difference of the large-scale wind at the top and at 1500 m).
        public bool Tilt = true;
        // Consistency rule: where a tall cell stands the decks beneath it are thinned (ThinMax), its anvil lies above them.
        public bool Couple = true;
        // The stratiform deck in the window columns (off = towers only, for the measurement of the towers).
        public bool Deck = true;
        // Add the broad lift of the column to the updraft output (the window of the game wants it); off = the towers' vertical speed only.
        public bool AmbientUpdraft = false;
        public double TiltTimeS = 1800.0, ThinMax = 0.75, GrowFraction = 0.25, SinkShare = 0.45, TopMinM = 2200.0, AnvilShare = 0.6;
        // Height of the stratiform deck the window follows (share of the tropopause height).
        public double DeckHeightFraction = 0.2;
    }

    // Layers of clouds with their own wind, towers that grow and sink, and the consistency between them. Built on WeatherGenerator.Sample and MeanWind; the generator is not changed
    // by any of it (no feedback), so the field stays a pure function.
    public static class LayerCoupling
    {
        // Share (0..1) of the full tower height at the life phase u: rises over the first GrowFraction of the life, sinks by SinkShare in the last 45 %.
        public static double GrowthShare(double u, LayerOptions o)
        {
            return DMath.SmoothStep(0.0, o.GrowFraction, u) * (1.0 - o.SinkShare * DMath.SmoothStep(0.55, 1.0, u));
        }

        // d(GrowthShare)/du.
        public static double GrowthSlope(double u, LayerOptions o)
        {
            double g = o.GrowFraction;
            double s1 = DMath.Clamp(u / g, 0.0, 1.0), s2 = DMath.Clamp((u - 0.55) / 0.45, 0.0, 1.0);
            double rise = (u > 0.0 && u < g) ? 6.0 * s1 * (1.0 - s1) / g : 0.0;
            double fall = (u > 0.55 && u < 1.0) ? 6.0 * s2 * (1.0 - s2) / 0.45 : 0.0;
            return rise * (1.0 - o.SinkShare * DMath.SmoothStep(0.55, 1.0, u)) - DMath.SmoothStep(0.0, g, u) * o.SinkShare * fall;
        }

        // The top of the tower of the cell at the point now (metres above the ground).
        public static double TopNow(ref Weather w, LayerOptions o)
        {
            if (!o.Vertical || w.CellLifeS <= 0.0) return w.CellTopM;
            double lo = o.TopMinM < w.CellTopM ? o.TopMinM : w.CellTopM;
            return lo + (w.CellTopM - lo) * GrowthShare(w.CellPhase, o);
        }

        // The rise of the top (m/s, negative while the tower sinks): the vertical speed of the updraft at the top.
        public static double TopRiseMs(ref Weather w, LayerOptions o)
        {
            if (!o.Vertical || w.CellLifeS <= 0.0) return 0.0;
            double lo = o.TopMinM < w.CellTopM ? o.TopMinM : w.CellTopM;
            return (w.CellTopM - lo) * GrowthSlope(w.CellPhase, o) / w.CellLifeS;
        }

        // The tower density of a column at an altitude for the given top.
        public static double CellDensity(double strength, double top, double altM)
        {
            return strength * DMath.SmoothStep(300.0, 1500.0, altM) * (1.0 - DMath.SmoothStep(top - 1500.0, top, altM));
        }

        // The longitude a layer of the given height is looked up at (the layer turns rigidly by LayerRate x UT).
        public static double LayerLon(WeatherGenerator g, double lonRad, double ut, double heightM, LayerOptions o)
        {
            if (!o.Shear) return lonRad;
            double rate = HeightWind.LayerRate(g.D, g.Body, heightM);
            return rate == 0.0 ? lonRad : DMath.WrapPi(lonRad - rate * ut);
        }

        // The downwind shift (east, north, metres) of the top of a tower of height topM at a point.
        public static void TiltShift(WeatherGenerator g, double lat, double lon, double ut, double topM, LayerOptions o, out double east, out double north)
        {
            east = 0.0; north = 0.0;
            if (!o.Tilt || !o.Shear || !g.Body.HeightWind) return;
            double eU, nU, eL, nL;
            g.MeanWind(lat, lon, 0.9 * topM, ut, out eU, out nU);
            g.MeanWind(lat, lon, 1500.0, ut, out eL, out nL);
            east = (eU - eL) * o.TiltTimeS; north = (nU - nL) * o.TiltTimeS;
        }

        // The coverage (0..1) of one layer of clouds at a point: the stratiform pattern looked up where the layer's own wind has carried it, thinned beneath a tall cell, plus the tower
        // where the layer lies inside it and the anvil where the layer lies at its top.
        public static double Coverage(WeatherGenerator g, double lat, double lon, double ut, double layerMidM, LayerOptions o)
        {
            Weather w0, wd;
            g.Sample(lat, lon, 0.0, ut, out w0);
            wd = w0;
            double lonL = LayerLon(g, lon, ut, layerMidM, o);
            if (lonL != lon) g.Sample(lat, lonL, 0.0, ut, out wd);
            double top = TopNow(ref w0, o);
            double thin = 1.0;
            if (o.Couple && w0.CellStrength > 0.0)
                thin = 1.0 - o.ThinMax * DMath.SmoothStep(0.15, 0.6, w0.CellStrength) * DMath.SmoothStep(1500.0, 3000.0, top) * (1.0 - DMath.SmoothStep(top - 1000.0, top + 500.0, layerMidM));
            double deck = 1.0 - DMath.Exp(-3.0 * wd.LayerAmount * thin);
            double tower = 0.0, anvil = 0.0;
            if (w0.CellStrength > 0.0)
            {
                double cellCov = 1.0 - DMath.Exp(-3.0 * 0.7 * w0.CellStrength);
                tower = cellCov * (1.0 - DMath.SmoothStep(top - 1500.0, top, layerMidM)) * DMath.SmoothStep(300.0, 1500.0, layerMidM);
                double dTop = layerMidM - top;
                if (o.Couple && dTop > -1800.0 && dTop < 2500.0)
                {
                    double east, north;
                    TiltShift(g, lat, lon, ut, top, o, out east, out north);
                    Weather wt = w0;
                    if (east != 0.0 || north != 0.0)
                    {
                        double cosLat = DMath.Max(0.02, DMath.Cos(lat));
                        g.Sample(DMath.Clamp(lat - north / g.Body.RadiusM, -1.5707, 1.5707), DMath.WrapPi(lon - east / (g.Body.RadiusM * cosLat)), 0.0, ut, out wt);
                    }
                    double bell = DMath.SmoothStep(-1800.0, -300.0, dTop) * (1.0 - DMath.SmoothStep(300.0, 2500.0, dTop));
                    anvil = o.AnvilShare * (1.0 - DMath.Exp(-3.0 * 0.7 * wt.CellStrength)) * bell * DMath.SmoothStep(0.5, 1.0, GrowthShare(wt.CellPhase, o));
                }
            }
            return 1.0 - (1.0 - deck) * (1.0 - tower) * (1.0 - anvil);
        }

        // The 3D density of one column of the local window. density[i0 + k * stride] and updraft[i0 + k * stride] (signed vertical speed of the tower in m/s, may be null) for the altitudes
        // zBaseM + k dzM. The stratiform deck is looked up with the rate of its height, the cells are not moved (they steer with the table), the top of a tower grows and sinks with
        // the life of the cell and its upper part is shifted downwind by the shear.
        public static void Column(WeatherGenerator g, double lat, double lon, double ut, double zBaseM, double dzM, int nz, float[] density, int i0, int stride, float[] updraft, LayerOptions o)
        {
            Weather w0, wd, wt;
            g.Sample(lat, lon, zBaseM, ut, out w0);
            wd = w0; wt = w0;
            double deckM = o.DeckHeightFraction * g.Body.TropopauseM;
            double lonL = LayerLon(g, lon, ut, deckM, o);
            if (lonL != lon) g.Sample(lat, lonL, zBaseM, ut, out wd);
            double top = TopNow(ref w0, o);
            double topT = top;
            bool hasCell = w0.CellStrength > 0.0;
            if (hasCell)
            {
                double east, north;
                TiltShift(g, lat, lon, ut, top, o, out east, out north);
                if (east != 0.0 || north != 0.0)
                {
                    double cosLat = DMath.Max(0.02, DMath.Cos(lat));
                    g.Sample(DMath.Clamp(lat - north / g.Body.RadiusM, -1.5707, 1.5707), DMath.WrapPi(lon - east / (g.Body.RadiusM * cosLat)), zBaseM, ut, out wt);
                    topT = TopNow(ref wt, o);
                }
            }
            double thinAmp = (o.Couple && hasCell) ? o.ThinMax * DMath.SmoothStep(0.15, 0.6, w0.CellStrength) * DMath.SmoothStep(1500.0, 3000.0, top) : 0.0;
            double rise = TopRiseMs(ref w0, o);
            for (int k = 0; k < nz; k++)
            {
                double alt = zBaseM + k * dzM;
                double strat = !o.Deck ? 0.0 : wd.StratDensityAt(alt) * (1.0 - thinAmp * (1.0 - DMath.SmoothStep(top - 1000.0, top + 500.0, alt)));
                double cell = 0.0, anvil = 0.0;
                if (hasCell)
                {
                    double c0 = CellDensity(w0.CellStrength, top, alt);
                    double s = (o.Tilt && o.Shear) ? DMath.SmoothStep(0.55 * top, top, alt) : 0.0;
                    double c1 = s > 0.0 ? CellDensity(wt.CellStrength, topT, alt) : 0.0;
                    cell = (1.0 - s) * c0 + s * c1;
                    if (o.Couple)
                    {
                        double dTop = alt - topT;
                        anvil = o.AnvilShare * wt.CellStrength * DMath.SmoothStep(-1200.0, -200.0, dTop) * (1.0 - DMath.SmoothStep(200.0, 1400.0, dTop));
                    }
                }
                double d = strat + 0.9 * (cell + anvil);
                density[i0 + k * stride] = (float)(d > 1.5 ? 1.5 : d);
                if (updraft != null)
                {
                    double up = hasCell ? rise * DMath.SmoothStep(0.0, 0.04, w0.CellStrength) * DMath.SmoothStep(300.0, 1500.0, alt) * (1.0 - DMath.SmoothStep(top - 300.0, top + 300.0, alt)) : 0.0;
                    // the broad lift of the column (the field's window has it as 0.3 x the uplift of the column above the first 1500 m)
                    if (o.AmbientUpdraft) up += 0.3 * w0.Uplift * DMath.SmoothStep(0.0, 1500.0, alt);
                    updraft[i0 + k * stride] = (float)up;
                }
            }
        }
    }
}
