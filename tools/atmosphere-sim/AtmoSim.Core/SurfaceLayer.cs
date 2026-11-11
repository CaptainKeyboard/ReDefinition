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
        // fluxK: the warming of the ice-free mixed layer's equilibrium by the ocean heat convergence (flux / SeaExchangeWm2K, L2); 0 = signed.
        public static void Step(ref double tml, ref double h, double tAir, double solarWm2, double snowM, double tFreeze, double capJPerM2K, double dtDays, double fluxK = 0.0, bool smoothAlbedo = false)
        {
            double dtS = dtDays * 86400.0;
            if (h <= 0.0)
            {
                double tau = capJPerM2K / SeaExchangeWm2K;
                tml += (tAir + fluxK - tml) * (1.0 - DMath.Exp(-dtS / tau));
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
                // smoothAlbedo: the albedo follows the snow depth continuously (the signed step at 0.02 m changed the melt of the whole integration history when the snow at the
                // evaluation time crossed it, a jump of 0.4 m of ice at one site in the continuity check); false = the signed step
                double alb = smoothAlbedo ? AlbedoIce + (AlbedoSnowCovered - AlbedoIce) * DMath.SmoothStep(0.01, 0.04, snowM) : (snowM > 0.02 ? AlbedoSnowCovered : AlbedoIce);
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
        public const int SignedSnowWindowSteps = 160; // 40 days of 6 h (the signed window)
        public const int WetWindowSteps = 16;
        public const int DustWindowSteps = 40;

        private readonly WeatherGenerator g;
        private readonly BodyParams b;
        private readonly double lat, lon, terrain, oc, quarterS, tFreeze, cap, fluxK, tSnow;
        private readonly bool sea, dusty;
        private readonly double[] anchorTml = new double[4], anchorH = new double[4];
        private readonly int cap6;
        // The snow window in 6-hour steps: a fixed fraction of the body's year (L3), 160 steps (40 days) where the lever is off.
        private readonly int snowSteps;
        private readonly bool smoothAlb, r2snow;
        // The memory of the snow on the ice in seconds (BodyParams.IceSnowMemoryDays), 0 = none.
        private readonly double memorySteps, memoryGrowth, sinLat;
        // The wet state of the last two evaluated steps (the next evaluation of the map reuses one of them).
        private long wetM0 = long.MinValue, wetM1 = long.MinValue;
        private double wetV0, wetV1;
        private readonly float[] tSer, pSer, wSer, hSer;
        // Per-step values that need an Exp, computed once with the sample: snow added by the step (water equivalent, mm) and the decay factor of the wet recurrence.
        private readonly double[] snowAdd, wetDecay, meltDrop;
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
            fluxK = b.SeaHeatFluxWm2 / IceModel.SeaExchangeWm2K;
            tSnow = b.SnowFreezeK;
            smoothAlb = b.SmoothSnowAlbedo;
            sinLat = DMath.Sin(lat);
            memorySteps = b.IceSnowMemoryDays * 86400.0;
            memoryGrowth = memorySteps > 0.0 ? DMath.Exp(86400.0 / memorySteps) : 1.0;
            // the degree-day melt with a spread acts on land; the snow on sea ice keeps the round-1 rule (a fast change of the snow depth over ice would move the albedo, and with it the
            // whole integrated ice history, in a few hours: the continuity check of the ice at the anchor boundaries)
            r2snow = b.Round2Snow && !sea;
            int need = (int)Math.Ceiling(2.2 * quarterS / StepS) + 8;
            snowSteps = b.SnowWindowYearFraction > 0.0
                ? (int)Math.Max(24, Math.Min(SignedSnowWindowSteps, Math.Round(b.SnowWindowYearFraction * b.YearS / StepS)))
                : SignedSnowWindowSteps;
            // the ring of cached steps has a power-of-two size so that the slot of a step is a mask, not a long division (the cache only memoises pure values: the results do not depend on its size)
            int ring = Math.Max(need, snowSteps + 8);
            if (ring > 1024) ring = 1024;
            int pow2 = 1; while (pow2 < ring) pow2 <<= 1;
            cap6 = pow2;
            tSer = new float[cap6]; pSer = new float[cap6]; wSer = new float[cap6]; hSer = new float[cap6];
            snowAdd = new double[cap6]; wetDecay = new double[cap6]; meltDrop = new double[cap6];
            stamp = new long[cap6];
            for (int i = 0; i < cap6; i++) stamp[i] = long.MinValue;
            if (sea) BuildAnchors();
        }

        public bool IsSea { get { return sea; } }

        private double ClimT(double ut) { return g.ClimateTemperature(lat, lon, ut) - b.LapseKPerM * Math.Min(terrain, b.TropopauseM); }

        // Daily-mean insolation of the site in units of a quarter of the solar constant.
        private double SolarUnit(double ut)
        {
            return BodyDerived.Insolation(sinLat, SinDeclination(ut));
        }

        private double ClimSolar(double ut)
        {
            return b.SolarFluxWm2 * 0.25 * BodyDerived.Insolation(sinLat, SinDeclination(ut));
        }

        // The sine of the declination at a time: the same for every site, so the last 256 values per thread are memoised (a pure function of the body and the time; the map update of
        // thousands of sites asks for the same times).
        [ThreadStatic] private static double[] declKey, declVal;
        [ThreadStatic] private static BodyParams[] declBody;

        private double SinDeclination(double ut)
        {
            if (declKey == null) { declKey = new double[256]; declVal = new double[256]; declBody = new BodyParams[256]; }
            long bits = BitConverter.DoubleToInt64Bits(ut);
            int idx = (int)((bits ^ (bits >> 23) ^ (bits >> 41)) & 255);
            if (declKey[idx] == ut && ReferenceEquals(declBody[idx], b)) return declVal[idx];
            double decl = -b.ObliquityRad * DMath.Cos(DMath.TwoPi * DMath.Frac(ut / b.YearS));
            double v = DMath.Sin(decl);
            declKey[idx] = ut; declVal[idx] = v; declBody[idx] = b;
            return v;
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
                IceModel.Step(ref tml, ref h, ClimT(t + 0.5 * dt), ClimSolar(t + 0.5 * dt), 0.0, tFreeze, cap, dt / dayS, fluxK);
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
                    IceModel.Step(ref tml, ref h, ClimT(t + 0.5 * dt), ClimSolar(t + 0.5 * dt), 0.0, tFreeze, cap, dt / dayS, fluxK);
                    t += dt;
                }
            }
        }

        // ---- cached 6-hour series ---------------------------------------------------------------------------------------

        private int Slot(long step) { return (int)(step & (cap6 - 1)); }

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
            snowAdd[i] = p > 0.0 ? p * 6.0 * (1.0 / (1.0 + DMath.Exp((t - tSnow - 1.2 - 1.0) / 0.9))) : 0.0;
            double tau = 18.0 * DMath.Exp(-(t - 288.0) / 25.0);
            wetDecay[i] = DMath.Exp(-6.0 / DMath.Max(2.0, tau));
            if (r2snow)
            {
                // melt of the step (mm of water equivalent) by the degree-day rule of Round2Snow: only where the air is within reach of the freezing point (a spread of 6 K: 5 sigma = 30 K below it
                // the expectation is below 1e-6 K), the insolation factor only then
                // Round 3 (SnowRadiativeHeatK): the sun heats the snow surface above the air temperature (the radiation term of an enhanced degree-day model), so snow melts
                // on high ground in summer where the air stays below freezing, and not in winter (small insolation); 0 = round 2
                double sg = b.SnowTempSpreadK;
                // the insolation (three transcendental functions) is only needed where the air is within reach of the freezing point: the mean insolation unit is at most 3 for every obliquity
                double su = t + b.SnowRadiativeHeatK * 3.0 - tSnow < -5.0 * sg ? 0.0 : SolarUnit(step * StepS);
                double xx = (t + b.SnowRadiativeHeatK * su - tSnow) / sg;
                if (xx < -5.0) meltDrop[i] = 0.0;
                else
                {
                    double pdd = sg * (0.3989422804 * DMath.Exp(-0.5 * xx * xx) + xx / (1.0 + DMath.Exp(-1.702 * xx)));
                    meltDrop[i] = (3.0 + b.SnowMeltRadPerUnit * su) * pdd * 0.25;
                }
            }
            stamp[i] = step; SamplesTaken++;
        }

        private double T(long step) { Ensure(step); return tSer[Slot(step)]; }

        private double SnowBaseMm(double ut)
        {
            double t = ClimT(ut);
            return 120.0 * DMath.SmoothStep(tSnow - 1.0, tSnow - 12.0, t);
        }

        // Snow water equivalent (mm) of the window of snowSteps ending at step m.
        private double SweAt(long m)
        {
            long m0 = m - snowSteps;
            double swe = SnowBaseMm(m0 * StepS);
            int i = Slot(m0 + 1);
            for (long s = m0 + 1; s <= m; s++, i = i + 1 == cap6 ? 0 : i + 1)
            {
                if (stamp[i] != s) Fill(s, i);
                double t = tSer[i], wnd = wSer[i];
                swe += snowAdd[i];
                // Round2Snow: positive degree-days of a temperature distribution: the 6 h mean t has a spread (diurnal cycle, synoptic variability), so melt starts below the mean
                // freezing point; E[max(0, T - Tf)] = s (phi(x) + x Phi(x)), x = (t - Tf) / s, Phi by the logistic approximation (computed in Fill). The factor grows with the
                // insolation (radiation-aided degree-day factor); the lapse rate enters through t, which is sampled at the terrain height.
                if (r2snow) swe -= meltDrop[i];
                else if (t > tSnow) swe -= 3.0 * (t - tSnow) * 0.25;
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

        private double WetCached(long m)
        {
            if (m == wetM1) return wetV1;
            if (m == wetM0) return wetV0;
            double v = WetAt(m);
            wetM0 = wetM1; wetV0 = wetV1;
            wetM1 = m; wetV1 = v;
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
            double wgt = 0.0, prevDt = 0.0; bool wValid = false;
            while (t < ut)
            {
                double dt = DMath.Min(86400.0, ut - t);
                double tm = 0.5 * (t + t + dt);
                double iEnd = Interp(t + dt);
                double meanT = 0.5 * (0.5 * (iStart + iEnd) + Interp(0.5 * (t + (t + dt))));
                // Round 3 (IceSnowMemoryDays): the snow of the evaluation time counts for the steps long before it with a decaying weight, so a quick change of the snow does not move
                // the whole integrated history (the continuity check of the ice, synthetic seed A); 0 = the same snow depth for every step
                // (the weight of consecutive whole days differs by the constant factor memoryGrowth, so one Exp per call and no snow means no Exp)
                double snowStep = snowM;
                if (memorySteps > 0.0 && snowM > 0.0)
                {
                    if (wValid && dt == 86400.0 && prevDt == 86400.0) wgt *= memoryGrowth; else wgt = DMath.Exp(-(ut - tm) / memorySteps);
                    wValid = true; snowStep = snowM * wgt;
                }
                prevDt = dt;
                IceModel.Step(ref tml, ref h, meanT, ClimSolar(tm), snowStep, tFreeze, cap, dt / 86400.0, fluxK, smoothAlb);
                t += dt;
                iStart = iEnd;
            }
        }

        // Diagnostic: the two integrations of the ice (from the anchor of the last quarter boundary and from the one before) and the cross-fade weight of the first.
        public void DebugIce(double ut, out double hA, out double hB, out double wA)
        {
            long aIdx = (long)DMath.Floor(ut / quarterS);
            double aUt = aIdx * quarterS, tml;
            double snow = SweCached((long)DMath.Floor(ut / StepS)) * 0.008;
            wA = DMath.SmoothStep(0.0, 0.5 * quarterS, ut - aUt);
            IceFrom(aUt, aIdx, ut, snow, out tml, out hA);
            IceFrom(aUt - quarterS, aIdx - 1, ut, snow, out tml, out hB);
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
            double w0 = WetCached(n), w1 = WetCached(n + 1);
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
