using System;

namespace AtmoSim.Core
{
    public enum PrecipKind { None = 0, Rain = 1, Sleet = 2, Snow = 3, Dust = 4 }

    public enum PrecipLevel { None = 0, Flurries = 1, Light = 2, Moderate = 3, Heavy = 4, Blizzard = 5 }

    // One evaluation of the generator. Plain data; filled by WeatherGenerator.Sample.
    public struct Weather
    {
        public double TempK, TempMeanK, SurfaceTempK, PressureAnomPa, Humidity, Uplift, FrontKPer100Km, Instability;
        public double CloudWater, Coverage, PrecipMmH, ConvectiveShare, Density3D;
        public double WindEast, WindNorth, WindSpeed, GustFactor;
        public double CellStrength, Dust, SnowFrac, SleetFrac, RainFrac, OceanFraction;
        public PrecipKind Type;
        // Vertical profile parameters of the column (for the local 3D window): stratiform layer base/thickness/amount, cell top.
        public double LayerBaseM, LayerThicknessM, LayerAmount, CellTopM;
        public double WindSpeedMs { get { return WindSpeed; } }

        // Cloud density (0..1.5) at an altitude of this column: a stratiform layer with a sin^2 profile plus towering cells.
        public double DensityAt(double altM)
        {
            double strat = 0.0;
            if (altM > LayerBaseM && altM < LayerBaseM + LayerThicknessM)
            {
                double sv = DMath.Sin(DMath.Pi * (altM - LayerBaseM) / LayerThicknessM);
                strat = LayerAmount * sv * sv;
            }
            double cellD = CellStrength * DMath.SmoothStep(300.0, 1500.0, altM) * (1.0 - DMath.SmoothStep(CellTopM - 1500.0, CellTopM, altM));
            double d = strat + 0.9 * cellD;
            return d > 1.5 ? 1.5 : d;
        }
    }

    // The deterministic synoptic weather generator: a pure function of (body, seed, position, altitude, UT). No state, no
    // checkpoints. The only caches are per-thread and hold values that are themselves pure functions of the UT.
    // Technique references: Held-Hou and Rhines scalings for the circulation numbers, the Stefan law for ice (SurfaceLayer.cs),
    // degree-day melt, the wet-bulb snow fraction of plan 09 section 3.5.
    public sealed class WeatherGenerator
    {
        // Coefficients of the cloud and rain proxies (CW = bg + X + cell part; P = kP max(0, X - c1) + kC cell).
        public const double CloudBgGain = 0.55, CloudBgOffset = 0.14, UpliftGain = 1.1, FrontGain = 0.7;
        public const double StratThreshold = 0.12, StratRateMmH = 4.0, ConvRateMmH = 12.0;
        public const double CellHourBucketS = 10800.0;
        // A cell rains only where its strength exceeds this (the tails of the Gaussian profile are cloud, not rain).
        public const double CellRainThreshold = 0.15;
        public const int MaxSlots = 8;
        public const double FrontSharp = 1.6;
        private static readonly double[] LevelAnchors = { 0.04, 0.2, 0.4, 0.6, 0.8 };
        public const double FrontTilt = 0.45;
        public const double FrontEnvelopeAlong = 0.45, FrontEnvelopeAcross = 1.28;
        private const double TiltNorm = 1.0966090; // sqrt(1 + FrontTilt^2)

        public readonly BodyParams Body;
        public readonly BodyDerived D;
        public readonly ulong Seed;
        public readonly HeightGrid Grid;
        private readonly double a, fcrit;
        private readonly double fx0;

        // The body is read at construction and during every Sample (the per-thread frame cache is keyed on the generator and the UT):
        // treat a BodyParams instance as immutable once it has been handed to a generator; make a Clone to vary a switch.
        public WeatherGenerator(BodyParams body, ulong seed)
        {
            Body = body; Seed = seed; D = new BodyDerived(body);
            a = body.RadiusM;
            Grid = body.Grid ?? MapGen.Continents(180, 90, body.OceanFractionTarget, seed);
            fcrit = 2.0 * body.Omega * 0.20791169081775934; // f at 12 degrees: the Coriolis cap near the equator
            fx0 = fcrit * fcrit;
        }

        // ---- per-UT frame (cached per thread) -----------------------------------------------------------------------------

        private struct Slot
        {
            public bool Valid;
            public double LatC, LonC, CosLatC, K, InvLx2, InvLy2, InvLxT2, InvLyT2, Lx, Ly, Tilt, Amp, Bk;
        }

        private sealed class Frame
        {
            public WeatherGenerator Owner;
            public double Ut = double.NaN;
            public double DLand, DSea, DLag, SinDLand, CosDLand, SunLon, ItczLat;
            public bool Ready0, Ready1;
            public readonly Slot[] Slots = new Slot[2 * MaxSlots];
        }

        [ThreadStatic] private static Frame tl;

        private Frame GetFrame(double ut)
        {
            Frame f = tl;
            if (f == null) { f = new Frame(); tl = f; }
            if (f.Owner == this && f.Ut == ut) return f;
            f.Owner = this; f.Ut = ut; f.Ready0 = false; f.Ready1 = false;
            double eps = Body.ObliquityRad, y = Body.YearS;
            double x = DMath.TwoPi * DMath.Frac(ut / y);
            f.DLand = -eps * DMath.Cos(x);
            f.DSea = -eps * DMath.Cos(x - DMath.TwoPi * Body.SeaSeasonLagYearFraction);
            f.DLag = -eps * DMath.Cos(x - DMath.TwoPi * Body.CirculationLagYearFraction);
            f.SinDLand = DMath.Sin(f.DLand); f.CosDLand = DMath.Cos(f.DLand);
            double rate = Body.Omega - DMath.TwoPi / y;
            f.SunLon = DMath.WrapPi(-rate * ut);
            f.ItczLat = 0.55 * f.DLag;
            return f;
        }

        public double DeclinationLag(double ut)
        {
            double x = DMath.TwoPi * DMath.Frac(ut / Body.YearS);
            return -Body.ObliquityRad * DMath.Cos(x - DMath.TwoPi * Body.CirculationLagYearFraction);
        }

        // Jet latitude (rad, positive) and speed of a hemisphere (+1 north, -1 south) at a time: poleward and weaker in summer.
        public double JetLatitude(int hemiSign, double ut) { return D.JetLat0 + 0.22 * hemiSign * DeclinationLag(ut); }

        public double JetSpeed(int hemiSign, double ut)
        {
            double s = DMath.Clamp(hemiSign * DeclinationLag(ut) / 0.4084, -1.0, 1.0);
            return D.JetSpeed0 * (1.0 - 0.3 * s);
        }

        private void SetupHemisphere(Frame f, int h)
        {
            int hs = h == 0 ? 1 : -1;
            int n = Body.WavesEnabled ? D.SlotsPerHemisphere : 0;
            double ut = f.Ut, y = Body.YearS;
            double lifeMin = Body.SystemLifeMinDays * 86400.0, lifeMax = Body.SystemLifeMaxDays * 86400.0;
            double r = D.SystemRadiusM;
            for (int j = 0; j < MaxSlots; j++)
            {
                int idx = h * MaxSlots + j;
                f.Slots[idx].Valid = false;
                if (j >= n || D.WaveGate <= 0.0) continue;
                ulong hj = Hash.Of(Seed, h * 64 + j, 0, 11);
                double life = lifeMin + (lifeMax - lifeMin) * Hash.Draw(hj, 0);
                double off = Hash.Draw(hj, 1) * life;
                double tt = ut - off;
                double gen = DMath.Floor(tt / life);
                double u = tt / life - gen;
                double sw = DMath.Sin(DMath.Pi * u);
                double w = sw * sw;
                if (w < 1e-3) continue;
                ulong hg = Hash.Of(Seed, h * 64 + j, (long)gen, 12);
                double tm = off + (gen + 0.5) * life;
                double dLag = -Body.ObliquityRad * DMath.Cos(DMath.TwoPi * (DMath.Frac(tm / y) - Body.CirculationLagYearFraction));
                double s = DMath.Clamp(hs * dLag / 0.4084, -1.0, 1.0);
                double uj = D.JetSpeed0 * (1.0 - 0.3 * s);
                double jetLat = D.JetLat0 + 0.22 * hs * dLag;
                double r2 = 0.8 + 0.4 * Hash.Draw(hg, 4);
                double ly = 1.25 * r * r2;
                double latAbs = DMath.Clamp(jetLat + (Hash.Draw(hg, 1) * 2.0 - 1.0) * 0.5 * ly / a, 0.35, 1.35);
                double cosC = DMath.Cos(latAbs);
                double lon0 = (Hash.Draw(hg, 0) * 2.0 - 1.0) * DMath.Pi;
                double r1 = 0.85 + 0.4 * Hash.Draw(hg, 3);
                double lambda = 3.0 * r * r1;
                double lx = 0.9 * lambda;
                double gate = w * D.WaveGate;
                var sl = new Slot();
                sl.Valid = true;
                sl.LatC = hs * latAbs; sl.CosLatC = cosC;
                sl.LonC = DMath.WrapPi(lon0 + uj * (ut - tm) / (a * cosC));
                sl.K = DMath.TwoPi / lambda; sl.Lx = lx; sl.Ly = ly;
                sl.InvLx2 = 1.0 / (lx * lx); sl.InvLy2 = 1.0 / (ly * ly);
                // the temperature anomaly (fronts) has a narrower envelope along the track and a longer one across: one dominant front per system
                double lxT = FrontEnvelopeAlong * lambda, lyT = FrontEnvelopeAcross * ly;
                sl.InvLxT2 = 1.0 / (lxT * lxT); sl.InvLyT2 = 1.0 / (lyT * lyT);
                sl.Tilt = FrontTilt * hs;
                sl.Amp = D.PressureAmpPa * (0.6 + 0.6 * Hash.Draw(hg, 2)) * gate;
                sl.Bk = D.FrontAmpK * (0.7 + 0.5 * Hash.Draw(hg, 5)) * gate;
                f.Slots[idx] = sl;
            }
            if (h == 0) f.Ready0 = true; else f.Ready1 = true;
        }

        // Positions of the active wave systems of one hemisphere at a time (for the tracker tests and the sheets).
        public int ActiveSystems(int hemi, double ut, double[] latLonAmp)
        {
            Frame f = GetFrame(ut);
            if (hemi == 0 ? !f.Ready0 : !f.Ready1) SetupHemisphere(f, hemi);
            int c = 0;
            for (int j = 0; j < MaxSlots; j++)
            {
                Slot s = f.Slots[hemi * MaxSlots + j];
                if (!s.Valid) continue;
                if (latLonAmp != null && 3 * c + 2 < latLonAmp.Length) { latLonAmp[3 * c] = s.LatC; latLonAmp[3 * c + 1] = s.LonC; latLonAmp[3 * c + 2] = s.Amp; }
                c++;
            }
            return c;
        }

        // The pressure amplitude (Pa) of one slot at a time, 0 where the slot has no active generation. For the hand-over (popping) measurement.
        public double SlotAmplitude(int hemi, int slot, double ut)
        {
            Frame f = GetFrame(ut);
            if (hemi == 0 ? !f.Ready0 : !f.Ready1) SetupHemisphere(f, hemi);
            Slot s = f.Slots[hemi * MaxSlots + slot];
            return s.Valid ? s.Amp : 0.0;
        }

        // The period (seconds) and phase offset of a slot: generation n spans [off + n life, off + (n + 1) life).
        public void SlotTiming(int hemi, int slot, out double lifeS, out double offsetS)
        {
            ulong hj = Hash.Of(Seed, hemi * 64 + slot, 0, 11);
            lifeS = Body.SystemLifeMinDays * 86400.0 + (Body.SystemLifeMaxDays - Body.SystemLifeMinDays) * 86400.0 * Hash.Draw(hj, 0);
            offsetS = Hash.Draw(hj, 1) * lifeS;
        }

        // ---- climatology --------------------------------------------------------------------------------------------------

        // Air temperature at the surface from latitude, season, land/sea and time of day only (no weather systems).
        public double ClimateTemperature(double latRad, double lonRad, double ut)
        {
            Frame f = GetFrame(ut);
            return ClimateInner(f, latRad, DMath.Sin(latRad), DMath.Cos(latRad), lonRad, Grid.MoistAt(latRad, lonRad));
        }

        // Annual-mean profile (measured where the body has one) plus the seasonal anomaly of the insolation model, amplified over land.
        private double ClimateInner(Frame f, double latRad, double sinLat, double cosLat, double lon, double oc)
        {
            double dT = f.DLand + oc * (f.DSea - f.DLand);
            double sinD = DMath.Sin(dT);
            double annModel = D.ClimateK(sinLat, 0.0);
            double t = D.ClimateK(sinLat, sinD);
            double ann = D.HasProfile ? D.AnnualK(latRad, sinLat) : annModel;
            double landW = 1.0 - oc;
            return ann + (t - annModel) * (1.0 + 0.15 * landW);
        }

        // ---- the point evaluation ---------------------------------------------------------------------------------------

        public void Sample(double latRad, double lonRad, double altM, double ut, out Weather w)
        {
            Frame f = GetFrame(ut);
            int h = latRad >= 0.0 ? 0 : 1;
            int hs = h == 0 ? 1 : -1;
            if (h == 0 ? !f.Ready0 : !f.Ready1) SetupHemisphere(f, h);

            double sinLat = DMath.Sin(latRad), cosLat = DMath.Cos(latRad);
            double la = hs * latRad;
            double oc, moist;
            Grid.Lookup(latRad, lonRad, out oc, out moist);
            // The land/sea contrast of the temperature uses the smoothed ocean share (the regional scale of the contrast, 30 degrees of arc):
            // a coast line is not a front.
            double tClim = ClimateInner(f, latRad, sinLat, cosLat, lonRad, moist);

            // wave systems of this hemisphere
            double p = 0, tAnom = 0, fr = 0, dpdx = 0, dpdy = 0;
            int baseIdx = h * MaxSlots;
            for (int j = 0; j < MaxSlots; j++)
            {
                Slot s = f.Slots[baseIdx + j];
                if (!s.Valid) continue;
                double y = a * (latRad - s.LatC);
                if (y > 3.0 * s.Ly || y < -3.0 * s.Ly) continue;
                double x = a * s.CosLatC * DMath.WrapPi(lonRad - s.LonC);
                if (x > 3.0 * s.Lx || x < -3.0 * s.Lx) continue;
                double ex = DMath.Exp(-(x * x * s.InvLx2 + y * y * s.InvLy2));
                double q = s.K * (x + s.Tilt * y);
                double sq = DMath.Sin(q), cq = DMath.Cos(q);
                double ae = s.Amp * ex;
                p -= ae * cq;
                double th = DMath.TanhFast(FrontSharp * sq);
                double exT = DMath.Exp(-(x * x * s.InvLxT2 + y * y * s.InvLyT2));
                tAnom += s.Bk * exT * th;
                fr += s.Bk * exT * FrontSharp * s.K * (1.0 - th * th) * DMath.Abs(cq) * TiltNorm;
                dpdx += ae * (2.0 * x * s.InvLx2 * cq + s.K * sq);
                dpdy += ae * (2.0 * y * s.InvLy2 * cq + s.K * s.Tilt * sq);
            }
            // the systems of a hemisphere fade to zero at the equator (they are evaluated only for their own hemisphere, so no jump at latitude 0)
            double eqw = DMath.SmoothStep(0.0, 0.2, la);
            p *= eqw; tAnom *= eqw; fr *= eqw; dpdx *= eqw; dpdy *= eqw;
            double frontK100 = fr * 1e5;
            double frontN = DMath.Min(1.0, frontK100 / D.FrontRefKPer100Km);

            // sub-solar geometry (needed by the day-night regime and the sun elevation)
            double sinDs = f.SinDLand, cosDs = f.CosDLand;
            double dLon = f.SunLon - lonRad;
            double cosDl = DMath.Cos(dLon);
            double sunSin = sinLat * sinDs + cosLat * cosDs * cosDl;
            double ssw = D.SubSolarWeight;

            double tSurf = tClim + tAnom;
            // humidity proxy
            double e = DMath.Exp(0.07 * (tSurf - Body.SurfaceMeanK));
            double hT = e / (1.0 + e);
            double belt = DMath.Exp(-((latRad - f.ItczLat) * (latRad - f.ItczLat)) / 0.04);
            double sub;
            {
                double e1 = D.Cells >= 2 ? D.Edge1 : 0.55;
                sub = DMath.Exp(-((la - e1) * (la - e1)) / 0.0256);
            }
            if (ssw > 0.001)
            {
                double cosAng = DMath.Clamp(sinLat * sinDs + cosLat * cosDs * cosDl, -1.0, 1.0);
                double ang = DMath.Acos(cosAng);
                double beltSs = DMath.Exp(-(ang * ang) / 0.92);
                belt = belt * (1.0 - ssw) + beltSs * ssw;
                sub *= (1.0 - ssw);
            }
            double humidity = hT * (0.04 + 0.96 * moist) * (1.0 + 1.2 * belt) * (1.0 - 0.55 * sub);
            humidity = DMath.Clamp(humidity, 0.0, 1.0);

            // uplift, cloud water, rain
            double aref = 0.8 * D.PressureAmpPa;
            double lowP = p < 0.0 ? DMath.Min(1.0, -p / aref) : 0.0;
            double uplift = DMath.Min(1.4, 0.85 * lowP + 0.5 * frontN);
            double bg = DMath.Clamp(humidity * CloudBgGain - CloudBgOffset, 0.0, 1.0);
            double xdyn = humidity * (UpliftGain * uplift + FrontGain * frontN);

            // convective cells
            double warm = tAnom > 0.0 ? DMath.Min(1.0, tAnom / D.FrontAmpK) : 0.0;
            double solar = sunSin > 0.0 ? sunSin * (1.0 - 0.6 * moist) : 0.0;
            double inst = DMath.Clamp(1.3 * humidity + 0.3 * warm + 0.25 * solar - 0.3, 0.0, 1.0);
            double pcell = Body.CellsEnabled ? DMath.Clamp(0.8 * frontN + 0.9 * DMath.Max(0.0, inst - 0.35) + 0.3 * uplift, 0.0, 0.95) : 0.0;
            double cs = 0.0, tower = 0.0;
            if (pcell > 0.02) cs = CellStrength(latRad, lonRad, ut, pcell, out tower);

            double cw = bg + xdyn + 0.7 * cs;
            if (cw > 1.5) cw = 1.5;
            double pStrat = xdyn > StratThreshold ? StratRateMmH * (xdyn - StratThreshold) : 0.0;
            double pConv = cs > CellRainThreshold ? ConvRateMmH * (cs - CellRainThreshold) / (1.0 - CellRainThreshold) : 0.0;
            double precip = (pStrat + pConv) * Body.PrecipBodyFactor;

            // wind
            double rho = Body.SurfacePressurePa / (Body.GasConstant * tSurf);
            double f0 = 2.0 * Body.Omega * sinLat;
            double fe = f0 / (f0 * f0 + fx0);
            double ug = -dpdy / rho * fe, vg = dpdx / rho * fe;
            {
                // soft saturation of the geostrophic wind (gradient-wind balance bounds it): vmax = twice the jet speed, at least 15 m/s
                double vgm = DMath.Sqrt(ug * ug + vg * vg);
                if (vgm > 1e-9)
                {
                    double vsat = DMath.Max(15.0, 2.0 * D.JetSpeed0);
                    double sc = vsat * DMath.Tanh(vgm / vsat) / vgm;
                    ug *= sc; vg *= sc;
                }
            }
            const double cr = 0.9063, sr = 0.4226; // 25 degrees of surface turning toward low pressure
            double us = 0.65 * (cr * ug - hs * sr * vg), vs = 0.65 * (hs * sr * ug + cr * vg);
            double laShift = la - 0.22 * hs * f.DLag;
            if (laShift < 0.0) laShift = 0.0;
            double uz, vz;
            D.SurfaceCirculation(laShift > DMath.HalfPi ? DMath.HalfPi : laShift, out uz, out vz);
            double windE = us + uz, windN = vs + hs * vz;
            if (ssw > 0.001)
            {
                double eastC = cosDs * DMath.Sin(dLon);
                double northC = cosLat * sinDs - sinLat * cosDs * cosDl;
                double vss = 6.0 * ssw;
                windE += vss * eastC; windN += vss * northC;
            }
            double alt = altM < 0.0 ? 0.0 : altM;
            double htrop = Body.TropopauseM;
            double zr = DMath.Min(1.0, alt / (0.5 * htrop));
            double windScale = 1.0 + 1.2 * zr;
            windE *= windScale; windN *= windScale;
            if (alt > 0.4 * htrop && ssw < 0.5)
            {
                double jl = D.JetLat0 + 0.22 * hs * f.DLag;
                double jp = DMath.Exp(-((la - jl) * (la - jl)) / 0.09);
                windE += D.JetSpeed0 * (1.0 - 0.3 * DMath.Clamp(hs * f.DLag / 0.4084, -1.0, 1.0)) * jp * DMath.SmoothStep(0.4 * htrop, htrop, alt);
            }
            double wind = DMath.Sqrt(windE * windE + windN * windN);
            double gust = 1.0 + 0.5 * cs + 0.3 * frontN;

            // temperature: diurnal term, then altitude
            double diurnalAmp = Body.DiurnalLandAmpK * (1.0 - 0.7 * moist) * (1.0 - 0.5 * DMath.Min(1.0, 1.0 - DMath.Exp(-3.0 * cw)));
            double hour = cosLat * cosDs * DMath.Cos(dLon - 0.5);
            double tSurfFinal = tSurf + diurnalAmp * hour;
            double drop = Body.LapseKPerM * (alt < htrop ? alt : htrop);
            if (drop > 0.4 * tSurfFinal) drop = 0.4 * tSurfFinal;
            double tAir = tSurfFinal - drop;

            // precipitation type by wet-bulb temperature
            double fs = 0, fl = 0, fRain = 0;
            PrecipKind kind = PrecipKind.None;
            if (precip > 0.0)
            {
                double twC = tAir - Body.FreezePointK - 1.2;
                fs = 1.0 / (1.0 + DMath.Exp((twC - 1.0) / 0.9));
                fRain = 1.0 - fs;
                fl = 4.0 * fs * fRain;
                kind = (fs >= fRain && fs >= fl) ? PrecipKind.Snow : (fl >= fRain ? PrecipKind.Sleet : PrecipKind.Rain);
                if (fl > fs && fl > fRain) kind = PrecipKind.Sleet;
            }
            double dust = 0.0;
            if (Body.Dusty)
            {
                double dry = 1.0 - DMath.SmoothStep(0.1, 0.4, humidity);
                dust = DMath.SmoothStep(0.8 * Body.DustWindThresholdMs, 1.3 * Body.DustWindThresholdMs, wind) * dry * (1.0 - oc);
                if (dust > 0.3 && precip <= 0.0) kind = PrecipKind.Dust;
            }

            // vertical profile for the 3D window
            double zb = 600.0 + 1200.0 * (1.0 - humidity);
            double th2 = 1200.0 + 3500.0 * DMath.Min(1.2, uplift);
            double top = 0.35 * htrop + 0.65 * htrop * tower;

            w.TempK = tAir; w.TempMeanK = tSurf - DMath.Min(Body.LapseKPerM * (alt < htrop ? alt : htrop), 0.4 * tSurf); w.SurfaceTempK = tSurfFinal; w.PressureAnomPa = p; w.Humidity = humidity; w.Uplift = uplift;
            w.FrontKPer100Km = frontK100; w.Instability = inst; w.CloudWater = cw;
            w.Coverage = 1.0 - DMath.Exp(-3.0 * cw);
            w.PrecipMmH = precip; w.ConvectiveShare = precip > 0.0 ? pConv * Body.PrecipBodyFactor / precip : 0.0;
            w.LayerBaseM = zb; w.LayerThicknessM = th2; w.LayerAmount = bg + xdyn; w.CellTopM = top;
            w.Density3D = 0.0;
            w.WindEast = windE; w.WindNorth = windN; w.WindSpeed = wind; w.GustFactor = gust;
            w.CellStrength = cs; w.Dust = dust; w.SnowFrac = fs; w.SleetFrac = fl; w.RainFrac = fRain; w.OceanFraction = oc;
            w.Type = kind;
            w.Density3D = w.DensityAt(alt);
        }

        // The pressure of the zonal-mean structure (subtropical highs, subpolar lows) is added by callers that want the full
        // pressure; Sample returns the wave part only. Zonal-mean pressure anomaly in Pa at a latitude and season.
        public double ZonalPressureAnomaly(double latRad, double ut)
        {
            Frame f = GetFrame(ut);
            int hs = latRad >= 0.0 ? 1 : -1;
            double la = hs * latRad - 0.22 * hs * f.DLag;
            double ps = Body.SurfacePressurePa;
            double z = -0.004 * ps * DMath.Exp(-(latRad - f.ItczLat) * (latRad - f.ItczLat) / 0.04);
            if (D.Cells >= 2)
            {
                z += 0.006 * ps * DMath.Exp(-(la - D.Edge1) * (la - D.Edge1) / 0.0256);
                double e2 = D.Cells == 3 ? D.Edge2 : 1.1;
                z -= 0.008 * ps * DMath.Exp(-(la - e2) * (la - e2) / 0.0256);
            }
            return z * (1.0 - D.SubSolarWeight);
        }

        // ---- convective cells ---------------------------------------------------------------------------------------------

        // Strongest cell influence at a point from the lattice cells around it (3 rows x 3 columns in the frame drifting with
        // the steering wind of the row). Cells live 1-3 hours inside one 3-hour bucket and have a sin^2 life envelope.
        private double CellStrength(double lat, double lon, double ut, double pcell, out double tower)
        {
            double lc = D.CellLatticeM;
            double rowH = lc / a;
            double bucketF = DMath.Floor(ut / CellHourBucketS);
            long bucket = (long)bucketF;
            long j0 = (long)DMath.Floor(lat / rowH);
            double best = 0.0, bestTower = 0.0;
            for (int dj = -1; dj <= 1; dj++)
            {
                long j = j0 + dj;
                double latC = (j + 0.5) * rowH;
                double latAbs = latC < 0 ? -latC : latC;
                if (latAbs > 1.40) continue;
                double cosC = DMath.Cos(latC);
                long nj = (long)DMath.Floor(DMath.TwoPi * cosC / rowH);
                if (nj < 1) nj = 1;
                double dlon = DMath.TwoPi / nj;
                // steering wind of the row from the table
                double idx = latAbs * (180.0 / DMath.Pi) * 2.0;
                int i0t = (int)idx; if (i0t > 179) i0t = 179;
                double tt = idx - i0t;
                double steer = D.SteerMs[i0t] * (1.0 - tt) + D.SteerMs[i0t + 1] * tt;
                double shift = DMath.WrapPi(steer * ut / (a * cosC));
                double lonL = DMath.WrapPi(lon - shift);
                double fi = DMath.Floor((lonL + DMath.Pi) / dlon);
                for (int di = -1; di <= 1; di++)
                {
                    long i = (long)fi + di;
                    long ci = ((i % nj) + nj) % nj;
                    ulong hc = Hash.Of(Seed ^ 0xC311C311UL, j, ci, bucket);
                    double u1 = Hash.Draw(hc, 0);
                    double margin = pcell - u1;
                    if (margin <= 0.0) continue;
                    double dur = 3600.0 + 7200.0 * Hash.Draw(hc, 1);
                    double start = (CellHourBucketS - dur) * Hash.Draw(hc, 2);
                    double age = ut - (bucketF * CellHourBucketS + start);
                    if (age <= 0.0 || age >= dur) continue;
                    double sa = DMath.Sin(DMath.Pi * age / dur);
                    double env = sa * sa;
                    double jx = (Hash.Draw(hc, 3) * 2.0 - 1.0) * 0.3, jy = (Hash.Draw(hc, 4) * 2.0 - 1.0) * 0.3;
                    double rad = 0.3 * lc * (0.75 + 0.5 * Hash.Draw(hc, 5));
                    double cLon = -DMath.Pi + (ci + 0.5 + jx) * dlon;
                    double cLat = latC + jy * rowH;
                    double dx = a * cosC * DMath.WrapPi(lonL - cLon);
                    double dy = a * (lat - cLat);
                    double r2 = (dx * dx + dy * dy) / (rad * rad);
                    if (r2 > 9.0) continue;
                    double c = env * DMath.Exp(-r2) * DMath.SmoothStep(0.0, 0.1, margin);
                    if (c > best) { best = c; bestTower = Hash.Draw(hc, 6); }
                }
            }
            tower = bestTower;
            return best;
        }

        // ---- levels -------------------------------------------------------------------------------------------------------

        // The level scale 0..1 from the rate by the signed absolute bands (anchors 0.04, 0.2, 0.4, 0.6, 0.8 at the band edges, 1.0 at twice the last).
        public double Level01(double precipMmH)
        {
            double[] b = D.BandsMmH;
            if (precipMmH < b[0]) return 0.0;
            double[] lv = LevelAnchors;
            for (int i = 0; i < 4; i++)
                if (precipMmH < b[i + 1]) return lv[i] + (lv[i + 1] - lv[i]) * (precipMmH - b[i]) / (b[i + 1] - b[i]);
            double top = 2.0 * b[4];
            return precipMmH >= top ? 1.0 : 0.8 + 0.2 * (precipMmH - b[4]) / (top - b[4]);
        }

        public PrecipLevel LevelOf(double precipMmH, PrecipKind kind, double windMs)
        {
            double l = Level01(precipMmH);
            if (l < 0.04) return PrecipLevel.None;
            if (l < 0.2) return PrecipLevel.Flurries;
            if (l < 0.4) return PrecipLevel.Light;
            if (l < 0.6) return PrecipLevel.Moderate;
            if (l < 0.8 || kind != PrecipKind.Snow || windMs < 15.6) return PrecipLevel.Heavy;
            return PrecipLevel.Blizzard;
        }

        // A hash of the numeric result for the determinism tests.
        public static ulong HashOf(ref Weather w, ulong h)
        {
            h = Hash.Fold(h, w.TempK); h = Hash.Fold(h, w.PressureAnomPa); h = Hash.Fold(h, w.Humidity); h = Hash.Fold(h, w.CloudWater);
            h = Hash.Fold(h, w.PrecipMmH); h = Hash.Fold(h, w.WindEast); h = Hash.Fold(h, w.WindNorth); h = Hash.Fold(h, w.CellStrength);
            h = Hash.Fold(h, w.FrontKPer100Km); h = Hash.Fold(h, (double)(int)w.Type);
            return h;
        }
    }
}
