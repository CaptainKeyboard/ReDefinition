using System;

namespace AtmoSim.Core
{
    public enum CirculationRegime
    {
        // Zonal bands: one to three overturning cells per hemisphere, a jet, a storm track.
        Zonal = 0,
        // Sub-solar to anti-solar flow instead of bands (slow or tidally locked rotators).
        SubSolar = 1
    }

    // The closed-form general circulation of a body: everything derived once from the BodyParams by Rossby-number and Rhines-scale
    // rules. No time stepping; the generator reads these numbers and the season only.
    public sealed class BodyDerived
    {
        public const double UReferenceMs = 25.0;       // characteristic eddy velocity of the Rhines rule
        public const double RhinesCells3 = 1.5;        // jet count from which three cells per hemisphere form
        public const double RhinesCells2 = 0.9;        // ... two cells
        public const double SubSolarLo = 0.25, SubSolarHi = 0.7; // jet counts between which the flow turns to the sub-solar pattern

        public readonly BodyParams Body;
        public readonly double A, Omega, F45;
        // Free-fall scales
        public readonly double Brunt, DeformationRadiusM, SystemRadiusM;
        // Rhines jet count between equator and pole; cells per hemisphere; regime weights.
        public readonly double JetCount;
        public readonly int Cells;
        public readonly double SubSolarWeight;       // 0 zonal ... 1 sub-solar
        public readonly double WaveGate;             // 0..1: how well synoptic waves form (Rossby and Rhines gate)
        // Hadley and Ferrel cell edges (rad, annual mean), jet latitude, jet speed
        public readonly double Edge1, Edge2, JetLat0, JetSpeed0, SurfaceWind;
        public readonly double ThermalWindMs;
        // Temperature climatology
        public readonly double HeatTransport, TempNorm;
        public readonly double EquatorPoleK;
        // Systems
        public readonly int SlotsPerHemisphere;
        public readonly double PressureAmpPa, FrontAmpK, FrontSharpness, FrontRefKPer100Km;
        // Lattice of convective cells
        public readonly double CellLatticeM;
        // Absolute precipitation bands, mm/h of water equivalent: flurries start, light, moderate, heavy, blizzard threshold
        public readonly double[] BandsMmH;
        // Steering wind of convective cells by |latitude|, every 0.5 degree from 0 to 90
        public readonly double[] SteerMs;

        public BodyDerived(BodyParams b)
        {
            Body = b; A = b.RadiusM; Omega = b.Omega;
            F45 = 2.0 * Omega * 0.70710678118654752;
            double gammaD = b.GravityMs2 / b.Cp;
            double n2 = b.GravityMs2 / b.SurfaceMeanK * DMath.Max(0.3 * gammaD, gammaD - b.LapseKPerM);
            Brunt = DMath.Sqrt(n2);
            double fSafe = DMath.Max(F45, 1e-9);
            DeformationRadiusM = Brunt * b.TropopauseM / fSafe;
            SystemRadiusM = DMath.Clamp(0.75 * DeformationRadiusM, 0.12 * A, 0.45 * A);

            JetCount = 0.5 * DMath.Sqrt(2.0 * Omega * A / UReferenceMs);
            Cells = JetCount >= RhinesCells3 ? 3 : (JetCount >= RhinesCells2 ? 2 : 1);
            SubSolarWeight = b.TidallyLocked ? 1.0 : 1.0 - DMath.SmoothStep(SubSolarLo, SubSolarHi, JetCount);
            WaveGate = DMath.SmoothStep(0.35, 1.2, JetCount) * (1.0 - SubSolarWeight);

            double d2r = DMath.Pi / 180.0;
            Edge1 = Cells == 3 ? 30.0 * d2r : (Cells == 2 ? 40.0 * d2r : 90.0 * d2r);
            Edge2 = Cells == 3 ? 60.0 * d2r : 90.0 * d2r;
            JetLat0 = (Cells == 3 ? 48.0 : (Cells == 2 ? 52.0 : 55.0)) * d2r;

            // Temperature climatology: normalise so that the area mean of the declination-zero profile is the body's mean temperature.
            HeatTransport = DMath.Clamp(0.3 * DMath.Sqrt(b.SurfacePressurePa / 101325.0), 0.2, 0.95);
            double s = 0; int n = 400;
            for (int i = 0; i < n; i++)
            {
                double x = -1.0 + (i + 0.5) * 2.0 / n;
                s += DMath.Sqrt(DMath.Sqrt(HeatTransport + (1.0 - HeatTransport) * Insolation(x, 0.0)));
            }
            TempNorm = b.SurfaceMeanK / (s / n);
            EquatorPoleK = AnnualK(0.0, 0.0) - AnnualK(DMath.Pi / 3.0, 0.8660254037844386);

            // Thermal wind across the jet zone, saturated by the Rossby bound 0.1 Omega a.
            double dT = DMath.Max(EquatorPoleK, 0.0);
            double uT = b.GravityMs2 * b.TropopauseM * (dT / b.SurfaceMeanK) / (fSafe * 0.4 * A);
            ThermalWindMs = uT;
            double usat = 0.1 * Omega * A;
            JetSpeed0 = usat > 1e-9 ? usat * DMath.Tanh(0.4 * uT / usat) : 0.0;
            SurfaceWind = DMath.Max(0.35 * JetSpeed0, b.TidallyLocked || SubSolarWeight > 0.5 ? 0.0 : 0.5);

            SlotsPerHemisphere = (int)DMath.Clamp(DMath.Floor(DMath.TwoPi * A * DMath.Cos(JetLat0) / (1.7 * SystemRadiusM) + 0.5), 3, 8);
            PressureAmpPa = b.SystemAmpFraction * b.SurfacePressurePa;
            FrontAmpK = 4.5 * (b.SurfaceMeanK / 288.0);
            FrontSharpness = 1.6;
            FrontRefKPer100Km = 5.0 * (b.SurfaceMeanK / 288.0);

            CellLatticeM = DMath.Clamp(50e3 * DMath.Sqrt(A / 6.371e6), 12e3, 32e3);

            double k = b.PrecipBodyFactor;
            BandsMmH = new[] { 0.05 * k, 0.25 * k, 1.0 * k, 3.0 * k, 8.0 * k };

            SteerMs = new double[181];
            for (int i = 0; i <= 180; i++)
            {
                double lat = i * 0.5 * d2r;
                double uz, vz;
                SurfaceCirculation(lat, out uz, out vz);
                double jet = DMath.Exp(-((lat - JetLat0) / 0.3) * ((lat - JetLat0) / 0.3));
                SteerMs[i] = 1.5 * uz + 0.5 * JetSpeed0 * jet * (1.0 - SubSolarWeight);
            }
        }

        // Daily-mean insolation relative to the annual global mean as a function of x = sin(latitude) and the declination sine.
        public static double Insolation(double x, double sinDecl)
        {
            double p2 = 0.5 * (3.0 * x * x - 1.0);
            double i = 1.0 - 0.477 * p2 + 2.0 * x * sinDecl;
            return i < 0.03 ? 0.03 : i;
        }

        // The insolation model of the climatological surface temperature (K) at x = sin(latitude): the seasonal part and, without a
        // measured profile, the annual mean as well.
        public double ClimateK(double x, double sinDecl)
        {
            return TempNorm * DMath.Sqrt(DMath.Sqrt(HeatTransport + (1.0 - HeatTransport) * Insolation(x, sinDecl)));
        }

        public bool HasProfile { get { return Body.ProfileK != null; } }

        // The annual-mean surface temperature at a latitude: the measured sea-level zonal profile of KSP's temperature curves (B09 dump,
        // Catmull-Rom through the samples every 15 degrees, symmetric about the equator) where a body has one, else the insolation model.
        public double AnnualK(double latRad, double x)
        {
            double[] pk = Body.ProfileK;
            if (pk == null) return ClimateK(x, 0.0);
            double la = latRad < 0.0 ? -latRad : latRad;
            double f = la * (6.0 / DMath.HalfPi);
            int i = (int)f; if (i > 5) i = 5;
            double t = f - i;
            double p1 = pk[i], p2 = pk[i + 1];
            double p0 = i > 0 ? pk[i - 1] : pk[1];
            double p3 = i + 2 <= 6 ? pk[i + 2] : pk[12 - (i + 2)];
            return 0.5 * ((2.0 * p1) + (-p0 + p2) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t * t + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t * t * t);
        }

        private static double Hump(double lat, double lo, double hi)
        {
            if (lat <= lo || lat >= hi) return 0.0;
            double s = DMath.Sin(DMath.Pi * (lat - lo) / (hi - lo));
            return s * s;
        }

        // Zonal-mean surface wind of the cell structure at an absolute latitude (rad, northern hemisphere sign convention:
        // u east, v north; the southern hemisphere flips v). Amplitude SurfaceWind, weighted to zero for sub-solar bodies.
        public void SurfaceCirculation(double latAbs, out double u, out double v)
        {
            double us = SurfaceWind * (1.0 - SubSolarWeight);
            double hp = DMath.HalfPi;
            double pu, pv;
            if (Cells == 3)
            {
                double h = Hump(latAbs, 0.0, Edge1), f = Hump(latAbs, Edge1, Edge2), p = Hump(latAbs, Edge2, hp);
                pu = -h + f - 0.6 * p; pv = -h + f - 0.6 * p;
            }
            else if (Cells == 2)
            {
                double h = Hump(latAbs, 0.0, Edge1), p = Hump(latAbs, Edge1, hp);
                pu = -h + 0.8 * p; pv = -h + 0.5 * p;
            }
            else
            {
                double h = Hump(latAbs, 0.0, hp);
                pu = -0.5 * h; pv = -h;
            }
            u = us * pu; v = 0.35 * us * pv;
        }
    }
}
