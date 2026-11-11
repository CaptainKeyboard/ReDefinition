using System;

namespace AtmoSim.Core
{
    // The horizontal wind as a function of the height above the ground (package E43). Textbook scalings, own deterministic maths (DMath only):
    //   friction layer   the wind near the ground is weak and crosses the isobars (Ekman turning); with height it grows to the free-troposphere value and turns back
    //                    (veering in the northern hemisphere, backing in the southern one) by EkmanTurnDeg;
    //   thermal wind     the system wind (the part that comes from the pressure of the waves) grows by a further 0.8 of itself up to the jet level and veers in a warm sector
    //                    (warm advection) and backs in a cold sector by up to VeerDeg: the direction turns with height where the temperature field has a gradient;
    //   zonal mean       the surface circulation of the cells decays with height; over the Hadley cell the trades (westward at the ground) are replaced by weak westerlies aloft,
    //                    over the jet latitude the jet grows to the jet level (JetHeightFactor x tropopause) and decays above it (Gaussian of half a jet height);
    //   return flow      the meridional surface flow of the cells reverses in the middle troposphere;
    //   sub-solar flow   on slow rotators and tidally locked bodies the low-level flow toward the sub-solar point is replaced by a return flow aloft.
    // The ground value (altitude 0) equals the surface wind of the generator exactly; every term is zero or one at the ground.
    public static class HeightWind
    {
        private const double Deg = DMath.Pi / 180.0;

        // Wind (east, north, m/s) at a height. sysE/sysN: wind of the pressure systems at the ground (turned and damped geostrophic wind); zonE/zonN: the zonal-mean surface
        // circulation of the cells at the shifted latitude (north component with the hemisphere sign applied); warmth: -1 (cold sector) .. 1 (warm sector) from the
        // temperature anomaly; ssE/ssN: the low-level flow toward the sub-solar point (already multiplied by 6 m/s x the sub-solar weight).
        public static void Wind(BodyDerived D, BodyParams b, double altM, int hs, double la, double dLag, double sysE, double sysN, double zonE, double zonN, double warmth, double ssE, double ssN, out double east, out double north)
        {
            if (altM <= 0.0)
            {
                east = sysE + zonE + ssE; north = sysN + zonN + ssN;
                return;
            }
            double htrop = b.TropopauseM;
            double zj = b.JetHeightFactor * htrop;
            double hb = b.BoundaryLayerFraction * htrop;
            double ssw = D.SubSolarWeight;
            double sBL = DMath.SmoothStep(0.0, hb, altM);
            double sFree = DMath.SmoothStep(hb, zj, altM);
            double x = altM / zj;
            double fall = 1.0;
            if (x > 1.0) { double d = (x - 1.0) * 2.0; fall = DMath.Exp(-d * d); }
            double grow = 1.0 + 0.54 * sBL;

            // system wind: grows out of the friction layer, turns back toward the geostrophic direction, veers or backs with the thermal advection
            double amp = grow * (1.0 + 0.8 * sFree) * fall;
            double rot = -hs * (b.EkmanTurnDeg * sBL + b.VeerDeg * warmth * sFree) * Deg;
            double cr = DMath.Cos(rot), sr = DMath.Sin(rot);
            double e = amp * (cr * sysE - sr * sysN), n = amp * (sr * sysE + cr * sysN);

            // zonal mean of the cells: trades die out over the Hadley cell, the meridional flow reverses aloft
            double hadW = 1.0 - DMath.SmoothStep(D.Edge1 - 0.1, D.Edge1 + 0.1, la);
            double tradeLoss = hadW * DMath.SmoothStep(hb, 0.45 * zj, altM);
            e += zonE * grow * (1.0 - tradeLoss) * fall;
            double retn = DMath.SmoothStep(2.0 * hb, 0.55 * zj, altM);
            n += zonN * grow * (1.0 - 2.0 * retn) * fall;

            // thermal wind of the jet latitude and the westerlies aloft over the Hadley cell
            double jl = D.JetLat0 + 0.22 * hs * dLag;
            double jp = DMath.Exp(-((la - jl) * (la - jl)) / 0.09);
            double jetAmp = D.JetSpeed0 * ((1.0 - 0.3 * DMath.Clamp(hs * dLag / 0.4084, -1.0, 1.0)) * jp + b.TradeAloftFraction * hadW);
            double rise = x < 1.0 ? DMath.SmoothStep(0.1, 1.0, x) : 1.0;
            e += (1.0 - ssw) * jetAmp * rise * fall;

            // sub-solar regime: flow toward the sub-solar point near the ground, return flow aloft
            if (ssw > 0.001)
            {
                double fss = (1.0 - DMath.SmoothStep(0.15, 0.45, x)) - 0.8 * DMath.SmoothStep(0.15, 0.5, x) * (1.0 - DMath.SmoothStep(1.0, 1.6, x));
                e += ssE * fss; n += ssN * fss;
            }
            east = e; north = n;
        }

        // The speed at which the patterns of the generator move by themselves (m/s, east positive) at an absolute latitude: the systems turn rigidly at the angular speed of
        // their steering (SystemSpeedFactor x the jet speed at the jet latitude, times 0.94 as measured on the maps, test PatternDriftMatchesTheModel), the cells and the
        // tropical patterns follow the steering table. This is the motion a layer of clouds already has; the drift of a layer is its wind minus this.
        public static double PatternDriftMs(BodyDerived D, BodyParams b, double la)
        {
            double a = b.RadiusM;
            double omegaS = D.JetSpeed0 * b.SystemSpeedFactor / (a * DMath.Cos(D.JetLat0));
            double rigid = PatternDriftScale * omegaS * a * DMath.Cos(la) * (1.0 - D.SubSolarWeight);
            double w = DMath.SmoothStep(0.26, 0.61, la);
            double idx = la * (180.0 / DMath.Pi) * 2.0;
            int i0 = (int)idx; if (i0 > 179) i0 = 179; if (i0 < 0) i0 = 0;
            double t = idx - i0;
            double steer = D.SteerMs[i0] * (1.0 - t) + D.SteerMs[i0 + 1] * t;
            return rigid * w + steer * (1.0 - w);
        }

        public const double PatternDriftScale = 0.94;

        // The angular rate (rad/s, positive = eastward) at which a layer of clouds at a height above the ground turns against the generator's own patterns: the large-scale wind
        // of the height at the storm-track latitude (annual mean, no systems) minus the pattern speed there, divided by the radius of that parallel. One rate per body and
        // height: the layer turns rigidly, so it does not shear into stripes however long the clock runs. Zero when the body has the round-3 profile.
        public static double LayerRate(BodyDerived D, BodyParams b, double heightM)
        {
            if (!b.HeightWind) return 0.0;
            double la = D.JetLat0;
            double uz, vz;
            D.SurfaceCirculation(la, out uz, out vz);
            double e, n;
            Wind(D, b, heightM, 1, la, 0.0, 0.0, 0.0, uz, vz, 0.0, 0.0, 0.0, out e, out n);
            double r = b.RadiusM * DMath.Cos(la);
            return (e - PatternDriftMs(D, b, la)) / r;
        }

        // The jet level of a body in metres above the ground.
        public static double JetHeightM(BodyParams b) { return b.JetHeightFactor * b.TropopauseM; }
    }
}
