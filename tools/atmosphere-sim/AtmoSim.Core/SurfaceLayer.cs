using System;

namespace AtmoSim.Core
{
    public struct SurfaceState
    {
        public double SnowSweMm, SnowDepthM, IceThicknessM, IceFraction, MixedLayerK, Wet01, DustDepositMm;
        public bool DustLifting, IsSea;
    }

    // The ice model of the surface layer: a mixed-layer heat store, ice formation at the freezing point of the body's liquid,
    // Stefan growth, melt by positive degree-days and a solar term, snow cover as insulation and albedo. One daily step.
    // Technique: Stefan's law h^2 = h0^2 + 2 k_i / (rho_i L_f) x FDD; degree-day melt factors.
    public static class IceModel
    {
        public const double KIce = 2.2, RhoIce = 917.0, LatentFusion = 3.34e5, KSnow = 0.3;
        public const double SeaExchangeWm2K = 15.0;
        public const double MeltFactorMPerKDay = 0.006;
        public const double AlbedoIce = 0.5, AlbedoSnowCovered = 0.8;
        public const double NewIceM = 0.01;

        // 2 k_i / (rho_i L_f) in m^2 per (K day)
        public static readonly double StefanCPerKDay = 2.0 * KIce / (RhoIce * LatentFusion) * 86400.0;

        public static double StefanThickness(double h0, double freezingDegreeDays)
        {
            return DMath.Sqrt(h0 * h0 + StefanCPerKDay * freezingDegreeDays);
        }

        // One step of dtDays. tml: mixed-layer temperature (K); h: ice thickness (m); tAir in K; solarWm2: daily mean absorbed-before-albedo flux;
        // snowM: snow depth on the ice; capJPerM2K: heat capacity of the mixed layer per area.
        public static void Step(ref double tml, ref double h, double tAir, double solarWm2, double snowM, double tFreeze, double capJPerM2K, double dtDays)
        {
            double dtS = dtDays * 86400.0;
            if (h <= 0.0)
            {
                double tau = capJPerM2K / SeaExchangeWm2K;
                tml += (tAir - tml) * (1.0 - DMath.Exp(-dtS / tau));
                if (tml <= tFreeze) { tml = tFreeze; h = NewIceM; }
                return;
            }
            tml = tFreeze;
            if (tAir < tFreeze)
            {
                double eff = 1.0 / (1.0 + KIce * snowM / (KSnow * DMath.Max(h, 0.05)));
                h = DMath.Sqrt(h * h + StefanCPerKDay * eff * (tFreeze - tAir) * dtDays);
            }
            else
            {
                double alb = snowM > 0.02 ? AlbedoSnowCovered : AlbedoIce;
                double melt = MeltFactorMPerKDay * (tAir - tFreeze) * dtDays
                    + (1.0 - alb) * solarWm2 * dtS / (RhoIce * LatentFusion) * DMath.SmoothStep(-3.0, 0.0, tAir - tFreeze);
                h -= melt;
                if (h <= 0.0) { h = 0.0; tml = tFreeze + 0.05; }
            }
        }
    }

    // The surface state of one site (a map texel or a tile), a deterministic function of the UT. Snow, wet/dry and dust are read from a
    // window of 6-hour weather samples cached per site; ice is integrated with daily steps from a climatological anchor at the last
    // quarter-year boundary, two overlapping integrations (anchor n and n-1) cross-faded by smoothstep.
    public sealed class SurfaceSite
    {
        public const double StepS = 21600.0;
        public const int SnowWindowSteps = 160;       // 40 days of 6 h
        public const int WetWindowSteps = 16;
        public const int DustWindowSteps = 40;

        private readonly WeatherGenerator g;
        private readonly BodyParams b;
        private readonly double lat, lon, terrain, oc, quarterS, tFreeze, cap;
        private readonly bool sea, dusty;
        private readonly double[] anchorTml = new double[4], anchorH = new double[4];
        private readonly int cap6;
        private readonly float[] tSer, pSer, wSer, hSer;
        // Per-step values that need an Exp, computed once with the sample: snow added by the step (water equivalent, mm) and the decay factor of the wet recurrence.
        private readonly double[] snowAdd, wetDecay;
        private readonly long[] stamp;
        private long cacheM0 = long.MinValue, cacheM1 = long.MinValue;
        private double cacheV0, cacheV1;
        public long SamplesTaken;

        public SurfaceSite(WeatherGenerator gen, double latRad, double lonRad)
        {
            g = gen; b = gen.Body; lat = latRad; lon = lonRad;
            terrain = gen.Grid.TerrainAt(lat, lon);
            oc = gen.Grid.OceanAt(lat, lon);
            sea = oc >= 0.5;
            dusty = b.Dusty;
            quarterS = b.YearS / 4.0;
            tFreeze = b.FreezePointK;
            cap = b.SeaHeatCapacityPerM3 * b.MixedLayerDepthM;
            int need = (int)Math.Ceiling(2.2 * quarterS / StepS) + 8;
            cap6 = Math.Max(need, SnowWindowSteps + 8);
            if (cap6 > 1400) cap6 = 1400;
            tSer = new float[cap6]; pSer = new float[cap6]; wSer = new float[cap6]; hSer = new float[cap6];
            snowAdd = new double[cap6]; wetDecay = new double[cap6];
            stamp = new long[cap6];
            for (int i = 0; i < cap6; i++) stamp[i] = long.MinValue;
            if (sea) BuildAnchors();
        }

        public bool IsSea { get { return sea; } }

        private double ClimT(double ut) { return g.ClimateTemperature(lat, lon, ut) - b.LapseKPerM * Math.Min(terrain, b.TropopauseM); }

        private double ClimSolar(double ut)
        {
            double decl = -b.ObliquityRad * DMath.Cos(DMath.TwoPi * DMath.Frac(ut / b.YearS));
            return b.SolarFluxWm2 * 0.25 * BodyDerived.Insolation(DMath.Sin(lat), DMath.Sin(decl));
        }

        // The climatological annual cycle of the mixed-layer temperature and ice thickness at the four quarter boundaries of the year.
        private void BuildAnchors()
        {
            double tml = ClimT(0.0), h = 0.0;
            const double dayS = 86400.0;
            double year = b.YearS;
            double t = 0.0;
            // four spin-up years in whole steps of one day (the last step of the spin-up is shortened to land on the year boundary)
            while (t < 4.0 * year)
            {
                double dt = Math.Min(dayS, 4.0 * year - t);
                IceModel.Step(ref tml, ref h, ClimT(t + 0.5 * dt), ClimSolar(t + 0.5 * dt), 0.0, tFreeze, cap, dt / dayS);
                t += dt;
            }
            // the fifth year: steps end exactly on the quarter-year boundaries, whose states are recorded
            for (int q = 0; q < 4; q++)
            {
                anchorTml[q] = tml; anchorH[q] = h;
                double qEnd = 4.0 * year + (q + 1) * quarterS;
                while (t < qEnd - 1e-6)
                {
                    double dt = Math.Min(dayS, qEnd - t);
                    IceModel.Step(ref tml, ref h, ClimT(t + 0.5 * dt), ClimSolar(t + 0.5 * dt), 0.0, tFreeze, cap, dt / dayS);
                    t += dt;
                }
            }
        }

        // ---- cached 6-hour series ---------------------------------------------------------------------------------------

        private int Slot(long step) { int r = (int)(step % cap6); return r < 0 ? r + cap6 : r; }

        private void Ensure(long step)
        {
            int i = Slot(step);
            if (stamp[i] == step) return;
            Fill(step, i);
        }

        private void Fill(long step, int i)
        {
            Weather w;
            g.Sample(lat, lon, terrain, step * StepS, out w);
            tSer[i] = (float)w.TempMeanK; pSer[i] = (float)w.PrecipMmH; wSer[i] = (float)w.WindSpeed; hSer[i] = (float)w.Humidity;
            double t = tSer[i], p = pSer[i];
            snowAdd[i] = p > 0.0 ? p * 6.0 * (1.0 / (1.0 + DMath.Exp((t - tFreeze - 1.2 - 1.0) / 0.9))) : 0.0;
            double tau = 18.0 * DMath.Exp(-(t - 288.0) / 25.0);
            wetDecay[i] = DMath.Exp(-6.0 / DMath.Max(2.0, tau));
            stamp[i] = step; SamplesTaken++;
        }

        private double T(long step) { Ensure(step); return tSer[Slot(step)]; }

        private double SnowBaseMm(double ut)
        {
            double t = ClimT(ut);
            return 120.0 * DMath.SmoothStep(tFreeze - 1.0, tFreeze - 12.0, t);
        }

        // Snow water equivalent (mm) of the window of SnowWindowSteps ending at step m.
        private double SweAt(long m)
        {
            long m0 = m - SnowWindowSteps;
            double swe = SnowBaseMm(m0 * StepS);
            int i = Slot(m0 + 1);
            for (long s = m0 + 1; s <= m; s++, i = i + 1 == cap6 ? 0 : i + 1)
            {
                if (stamp[i] != s) Fill(s, i);
                double t = tSer[i], wnd = wSer[i];
                swe += snowAdd[i];
                if (t > tFreeze) swe -= 3.0 * (t - tFreeze) * 0.25;
                swe -= 0.15 * 0.25 * (1.0 + wnd / 8.0);
                if (wnd > 12.0) swe -= swe * DMath.Min(0.5, 0.02 * (wnd - 12.0) * 0.25);
                if (swe < 0.0) swe = 0.0;
                if (swe > 2000.0) swe = 2000.0;
            }
            return swe;
        }

        private double SweCached(long m)
        {
            if (m == cacheM1) return cacheV1;
            if (m == cacheM0) return cacheV0;
            double v = SweAt(m);
            cacheM0 = cacheM1; cacheV0 = cacheV1;
            cacheM1 = m; cacheV1 = v;
            return v;
        }

        private double WetAt(long m)
        {
            double wsum = 0.0;
            for (long s = m - WetWindowSteps + 1; s <= m; s++)
            {
                Ensure(s);
                int i = Slot(s);
                wsum = wsum * wetDecay[i] + pSer[i] * 6.0 * 0.8;
            }
            return 1.0 - DMath.Exp(-wsum / 0.5);
        }

        private double DustAt(long m, out bool lifting)
        {
            double dep = 0.0; lifting = false;
            double thr = b.DustWindThresholdMs;
            for (long s = m - DustWindowSteps + 1; s <= m; s++)
            {
                Ensure(s);
                int i = Slot(s);
                bool dry = hSer[i] < 0.25 && pSer[i] <= 0.0;
                double wnd = wSer[i];
                bool lift = dry && wnd > thr;
                if (dry && wnd < 0.6 * thr) dep += 0.05;
                if (lift) dep -= 0.2 * (wnd - thr) + 0.05;
                if (dep < 0.0) dep = 0.0;
                if (dep > 5.0) dep = 5.0;
                if (s == m) lifting = lift;
            }
            return dep;
        }


        private double Interp(double ut)
        {
            double fs = ut / StepS;
            long n = (long)DMath.Floor(fs);
            double f = fs - n;
            return T(n) * (1.0 - f) + T(n + 1) * f;
        }

        private void IceFrom(double anchorUt, long anchorIdx, double ut, double snowM, out double tml, out double h)
        {
            int q = (int)(((anchorIdx % 4) + 4) % 4);
            tml = anchorTml[q]; h = anchorH[q];
            double t = anchorUt;
            // Daily mean temperature of the step [t, t + dt) from the cached series: linear interpolation at the segment ends and the middle. The end of a step is
            // the start of the next one, so its interpolation is carried over (the same argument, the same value).
            double iStart = Interp(t);
            while (t < ut)
            {
                double dt = DMath.Min(86400.0, ut - t);
                double tm = 0.5 * (t + t + dt);
                double iEnd = Interp(t + dt);
                double meanT = 0.5 * (0.5 * (iStart + iEnd) + Interp(0.5 * (t + (t + dt))));
                IceModel.Step(ref tml, ref h, meanT, ClimSolar(tm), snowM, tFreeze, cap, dt / 86400.0);
                t += dt;
                iStart = iEnd;
            }
        }

        public SurfaceState Evaluate(double ut)
        {
            var s = new SurfaceState();
            long n = (long)DMath.Floor(ut / StepS);
            double f = ut / StepS - n;
            // snow: window result interpolated between the steps either side of ut
            double swe0 = SweCached(n), swe1 = SweCached(n + 1);
            s.SnowSweMm = swe0 * (1.0 - f) + swe1 * f;
            s.SnowDepthM = s.SnowSweMm * 0.008;
            s.IsSea = sea;
            double w0 = WetAt(n), w1 = WetAt(n + 1);
            s.Wet01 = w0 * (1.0 - f) + w1 * f;
            if (dusty && !sea)
            {
                bool l0, l1;
                double d0 = DustAt(n, out l0), d1 = DustAt(n + 1, out l1);
                s.DustDepositMm = d0 * (1.0 - f) + d1 * f;
                s.DustLifting = f < 0.5 ? l0 : l1;
            }
            if (sea)
            {
                long aIdx = (long)DMath.Floor(ut / quarterS);
                double aUt = aIdx * quarterS;
                double tmlA, hA, tmlB, hB;
                double snowOnIce = s.SnowDepthM;
                double wA = DMath.SmoothStep(0.0, 0.5 * quarterS, ut - aUt);
                // the cross-fade weight is exactly 1 (or 0) over most of a quarter: the integration with weight 0 is not needed (0 * x = 0 for a finite x)
                if (wA == 1.0)
                {
                    IceFrom(aUt, aIdx, ut, snowOnIce, out tmlA, out hA);
                    s.IceThicknessM = hA; s.MixedLayerK = tmlA;
                }
                else
                {
                    IceFrom(aUt, aIdx, ut, snowOnIce, out tmlA, out hA);
                    IceFrom(aUt - quarterS, aIdx - 1, ut, snowOnIce, out tmlB, out hB);
                    s.IceThicknessM = wA * hA + (1.0 - wA) * hB;
                    s.MixedLayerK = wA * tmlA + (1.0 - wA) * tmlB;
                }
                s.IceFraction = DMath.SmoothStep(0.0, 0.15, s.IceThicknessM);
            }
            return s;
        }
    }
}
