using System;
using System.IO;

namespace AtmoSim.Core
{
    // The grid and numerical choices of one simulation.
    public sealed class SimConfig
    {
        public int NLon = 128, NLat = 64, Levels = 16;
        // Truncation of the dynamical core (the baroclinic scales of a small planet need few waves); its Gaussian grid is
        // the smallest one that holds the truncation.
        public int DynM = 21;
        public double StepSeconds = 3600.0;
        public ulong Seed = 1;
        // The condensation heating of the last step feeds the dynamics.
        public bool LatentFeedback = true;
        // Switches for the A/B pairs.
        public bool CoriolisOn = true;
        public bool OceanOn = true;

        public SimConfig Clone() { return (SimConfig)MemberwiseClone(); }
    }

    // Technique references: the two-level quasi-geostrophic model (Phillips 1956), the spectral
    // transform method on the sphere (Hoskins and Simmons 1975) with normalised associated Legendre functions, semi-Lagrangian
    // advection (Stam, "Stable Fluids", 1999, as the transport scheme), a moist-static-energy convection trigger (Betts-Miller style
    // adjustment) [VERIFY bibliographic details].
    //
    // A coarse, deterministic weather simulation of one body: a two-layer quasi-geostrophic core on the sphere (spectral)
    // supplies winds and the temperature structure; moisture, cloud water and a surface layer live on a lat-lon x sigma grid
    // and are advected semi-Lagrangian. The state at a time is a pure function of (body, config, seed, steps taken).
    public sealed partial class Atmosphere
    {
        public readonly BodyParams Body;
        public readonly SimConfig Cfg;
        public readonly int NLon, NLat, K, N2;
        public readonly double Dt;

        // Grids and transforms.
        private readonly Spectral dyn, phy;
        private readonly int dynN2;

        // Derived constants.
        private readonly double a, omega, f0, deformF, ekmanR, radTau, tCoef, stabS, hScale, gamma, sigmaMid1 = 0.25, sigmaMid2 = 0.75;
        private readonly double[] sigma, sigmaEdge, zLevel, dpFrac;
        private readonly double[] latRad, cosLat, sinLat; // physics rows
        private double[] esTable; private const double TabT0 = 150.0, TabDt = 0.25; private const int TabN = 1000;
        private readonly double eps;

        // Prognostic state.
        public double Ut;
        public long StepIndex;
        private double[] q1r, q1i, q2r, q2i; // spectral PV of the two layers
        public double[] Qv, Qc;              // [K][N2] vapour and cloud water (kg/kg)
        public double[] Ts, SnowW, IceH, SoilMm, DustAir, DustDep; // 2D surface layer
        public double[] OceanFrac, TerrainM;

        // Diagnosed fields of the last step.
        public double[] T3;                  // [K][N2] temperature
        public double[] U3, V3;              // [K][N2] wind, m/s east/north
        public double[] Omega3;              // [K][N2] vertical pressure velocity, Pa/s (positive down)
        public double[] ConvW;               // [N2] convective updraft maximum, m/s
        public double[] ConvTopM;            // [N2] height of the convective cloud top, m (0 where there is no convection)
        public double[] PrecipMmH, PrecipConvMmH, SnowFall, CloudPath, PsAnom, Wind10, T2m, HeatK;
        public double[] Psi1Grid, Psi2Grid, DeltaGrid;
        public double MaxWind;
        public double WaterLedgerResidual;

        private double[] ucosD, vcosD, qgD, aD, bD;                // dynamics-grid scratch
        private double[] t1r, t1i, t2r, t2i, k1r, k1i, k2r, k2i;   // spectral scratch
        private double[] psi1r, psi1i, psi2r, psi2i;
        private double[] deqR, deqI, latR, latI;                    // forcing in spectral space
        private double[] diffDamp;                                  // exp(-hyperdiffusion dt) per coefficient
        private double[] nn, invLapl;                               // n(n+1)/a^2 and its helpers per coefficient
        private double[] scratchA, scratchB;
        private double[] heatGrid;                                  // condensation heating of the last step, K/s on the physics grid
        private double[] kQv, kQc;

        public Atmosphere(BodyParams body, SimConfig cfg)
        {
            Body = body; Cfg = cfg;
            NLon = cfg.NLon; NLat = cfg.NLat; K = cfg.Levels; N2 = NLon * NLat; Dt = cfg.StepSeconds;
            a = body.RadiusM; omega = cfg.CoriolisOn ? body.Omega : 1e-12;
            f0 = cfg.CoriolisOn ? body.MidLatCoriolis : 1e-9;
            hScale = body.GasConstant * body.SurfaceMeanK / body.GravityMs2;
            gamma = body.LapseKPerM;
            double gd = body.GravityMs2 / body.Cp;
            double n2 = body.GravityMs2 / body.SurfaceMeanK * DMath.Max(gd - gamma, 0.15 * gd);
            double nb = DMath.Sqrt(n2);
            double ld = nb * body.TropopauseM / f0 * body.EddyScale;
            deformF = 1.0 / (ld * ld);
            // Ekman spin-down scales with 1/sqrt(rotation rate): two Earth days at four times the rotation of the Earth.
            ekmanR = 1.0 / (4.0 * 86400.0 * DMath.Sqrt(7.2921e-5 / DMath.Max(omega, 1e-7)));
            if (ekmanR > 1.0 / 43200.0) ekmanR = 1.0 / 43200.0;
            double tEff = 255.0;
            double tauRad = body.Cp * body.SurfacePressurePa / (body.GravityMs2 * 4.0 * 5.670374e-8 * tEff * tEff * tEff) * 0.7;
            // thin atmospheres cool and warm faster, thick ones slower; keep the time scale within a sane range
            radTau = DMath.Clamp(tauRad, 4.0 * 86400.0 * 0.25, 300.0 * 86400.0);
            tCoef = f0 / (body.GasConstant * DMath.Log(sigmaMid2 / sigmaMid1));
            eps = 0.622;
            stabS = body.GasConstant * 250.0 / (body.SurfacePressurePa * 0.5 * body.Cp) * DMath.Max(1.0 - gamma / gd, 0.15);

            int dm = cfg.DynM;
            int dlon = 3 * dm + 1; while ((dlon & 3) != 0) dlon++;
            int dlat = (3 * dm + 2) / 2; if ((dlat & 1) != 0) dlat++;
            dyn = new Spectral(dlon, dlat, dm);
            phy = new Spectral(NLon, NLat, dm);
            dynN2 = dlon * dlat;

            sigma = new double[K]; sigmaEdge = new double[K + 1]; zLevel = new double[K]; dpFrac = new double[K];
            const double sTop = 0.03;
            for (int k = 0; k < K; k++)
            {
                double s = (k + 0.5) / K;
                sigma[k] = 1.0 - (1.0 - sTop) * (0.6 * s + 0.4 * s * s);
                zLevel[k] = -hScale * DMath.Log(sigma[k]);
            }
            sigmaEdge[0] = 1.0; sigmaEdge[K] = 0.0;
            for (int k = 1; k < K; k++) sigmaEdge[k] = 0.5 * (sigma[k - 1] + sigma[k]);
            for (int k = 0; k < K; k++) dpFrac[k] = sigmaEdge[k] - sigmaEdge[k + 1];

            latRad = phy.LatRad; cosLat = phy.CosLat; sinLat = phy.Mu;

            esTable = new double[TabN + 1];
            for (int i = 0; i <= TabN; i++)
            {
                double t = TabT0 + i * TabDt;
                double l = t < body.TripleK ? body.LatentSublimation : body.LatentVapour;
                esTable[i] = body.TripleVapourPa * DMath.Exp(l / body.Rv * (1.0 / body.TripleK - 1.0 / t));
            }

            Allocate();
            BuildSpectralTables();
            BuildMaps();
            Reset();
        }

        private void Allocate()
        {
            int nc = dyn.NCoef, n3 = K * N2;
            q1r = new double[nc]; q1i = new double[nc]; q2r = new double[nc]; q2i = new double[nc];
            t1r = new double[nc]; t1i = new double[nc]; t2r = new double[nc]; t2i = new double[nc];
            k1r = new double[nc]; k1i = new double[nc]; k2r = new double[nc]; k2i = new double[nc];
            psi1r = new double[nc]; psi1i = new double[nc]; psi2r = new double[nc]; psi2i = new double[nc];
            deqR = new double[nc]; deqI = new double[nc]; latR = new double[nc]; latI = new double[nc];
            diffDamp = new double[nc]; nn = new double[nc]; invLapl = new double[nc];
            ucosD = new double[dynN2]; vcosD = new double[dynN2]; qgD = new double[dynN2]; aD = new double[dynN2]; bD = new double[dynN2];
            scratchA = new double[dynN2]; scratchB = new double[dynN2];
            Qv = new double[n3]; Qc = new double[n3]; T3 = new double[n3]; U3 = new double[n3]; V3 = new double[n3]; Omega3 = new double[n3];
            kQv = new double[n3]; kQc = new double[n3];
            Ts = new double[N2]; SnowW = new double[N2]; IceH = new double[N2]; SoilMm = new double[N2]; DustAir = new double[N2]; DustDep = new double[N2];
            ConvW = new double[N2]; ConvTopM = new double[N2]; PrecipMmH = new double[N2]; PrecipConvMmH = new double[N2]; SnowFall = new double[N2]; CloudPath = new double[N2];
            PsAnom = new double[N2]; Wind10 = new double[N2]; T2m = new double[N2]; HeatK = new double[N2]; heatGrid = new double[N2];
            Psi1Grid = new double[N2]; Psi2Grid = new double[N2]; DeltaGrid = new double[N2];
            OceanFrac = new double[N2]; TerrainM = new double[N2];
        }

        private void BuildSpectralTables()
        {
            int M = dyn.M;
            double nmax = M;
            // hyperdiffusion: the smallest wave decays with a time of six hours of game time; applied per full step through the substeps
            for (int m = 0; m <= M; m++)
                for (int n = m; n <= M; n++)
                {
                    int i = dyn.Index(m, n);
                    nn[i] = n * (n + 1.0) / (a * a);
                    double r = n * (n + 1.0) / (nmax * (nmax + 1.0));
                    diffDamp[i] = r * r * r * r; // rate factor, scaled by 1/tau below
                }
        }

        // --- moist thermodynamics ---------------------------------------------------------------------------------

        private double Es(double t)
        {
            double x = (t - TabT0) / TabDt;
            if (x < 0) x = 0; if (x > TabN - 1) x = TabN - 1;
            int i = (int)x; double f = x - i;
            return esTable[i] * (1.0 - f) + esTable[i + 1] * f;
        }

        public double Qsat(double t, double p)
        {
            double e = Es(t);
            double lim = 0.5 * p; if (e > lim) e = lim;
            return eps * e / (p - e);
        }

        // --- orbital geometry ---------------------------------------------------------------------------------------

        private void Geometry(double ut, out double declSin, out double declCos, out double distFactor, out double subLon)
        {
            double phase = ut / Body.YearS; phase -= DMath.Floor(phase);
            double decl = -Body.ObliquityRad * DMath.Cos(DMath.TwoPi * phase);
            declSin = DMath.Sin(decl); declCos = DMath.Cos(decl);
            double d = 1.0 + Body.Eccentricity * DMath.Cos(DMath.TwoPi * (phase - Body.PerihelionPhase));
            distFactor = d * d;
            double day = ut / Body.RotationPeriodS; day -= DMath.Floor(day);
            subLon = DMath.Pi - DMath.TwoPi * day; // the Sun moves west: the sub-solar longitude decreases
        }

        // Daily-mean insolation fraction (0..1 of the solar constant) at a latitude for a declination, by quadrature over the day.
        private static double DailyMean(double sinLat, double cosLat, double declSin, double declCos)
        {
            const int Nh = 24;
            double sum = 0;
            for (int h = 0; h < Nh; h++)
            {
                double ha = DMath.TwoPi * (h + 0.5) / Nh;
                double mu0 = sinLat * declSin + cosLat * declCos * DMath.Cos(ha);
                if (mu0 > 0) sum += mu0;
            }
            return sum / Nh;
        }

        private double HeatTransport()
        {
            if (Body.HeatTransportFraction >= 0) return Body.HeatTransportFraction;
            double r = Body.SurfacePressurePa / 101325.0;
            return DMath.Min(0.95, 0.5 * DMath.Sqrt(r));
        }

        // Radiative-equilibrium surface temperature of a latitude row at a time (stand-in for the target profile).
        private void EquilibriumRows(double ut, double[] teqs, out double declS, out double declC, out double dist, out double subLon)
        {
            Geometry(ut, out declS, out declC, out dist, out subLon);
            double w = HeatTransport();
            double qsum = 0, wsum = 0;
            double[] q = scratchRows ?? (scratchRows = new double[NLat]);
            for (int j = 0; j < NLat; j++)
            {
                q[j] = DailyMean(sinLat[j], cosLat[j], declS, declC) * dist;
                qsum += q[j] * phy.Weight[j]; wsum += phy.Weight[j];
            }
            double gm = qsum / wsum;
            if (gm < 1e-6) gm = 1e-6;
            for (int j = 0; j < NLat; j++)
            {
                double qn = w + (1.0 - w) * q[j] / gm;
                teqs[j] = Body.SurfaceMeanK * tNorm * DMath.Sqrt(DMath.Sqrt(qn));
            }
        }
        private double[] scratchRows;
        private double tNorm = 1.0;

        // --- maps -------------------------------------------------------------------------------------------------

        private void BuildMaps()
        {
            if (Body.OceanFraction != null && Body.OceanFraction.Length == N2) Array.Copy(Body.OceanFraction, OceanFrac, N2);
            else MapGen.Continents(phy, Body.OceanFractionTarget, Cfg.Seed ^ 0x9E3779B97F4A7C15UL, OceanFrac, TerrainM);
            if (Body.TerrainM != null && Body.TerrainM.Length == N2) Array.Copy(Body.TerrainM, TerrainM, N2);
            if (!Cfg.OceanOn) for (int i = 0; i < N2; i++) OceanFrac[i] = 0.0;
            // normalise the equilibrium temperature so that its annual global mean is the configured surface mean
            tNorm = 1.0;
            double[] teq = new double[NLat];
            double acc = 0, wacc = 0;
            for (int s = 0; s < 24; s++)
            {
                double declS, declC, dist, sl;
                EquilibriumRows(Body.YearS * (s + 0.5) / 24.0, teq, out declS, out declC, out dist, out sl);
                for (int j = 0; j < NLat; j++) { acc += teq[j] * phy.Weight[j]; wacc += phy.Weight[j]; }
            }
            tNorm = Body.SurfaceMeanK / (acc / wacc);
        }

        // --- initial state ------------------------------------------------------------------------------------------

        // Re-initialises the state from climatology at time ut; the small seeded perturbation is the only random input.
        public void Reset() { Init(0.0, Cfg.Seed); }

        public void Init(double ut, ulong seed)
        {
            Ut = ut; StepIndex = 0;
            double[] teqs = teqsCur ?? (teqsCur = new double[NLat]);
            double declS, declC, dist, sl;
            EquilibriumRows(ut, teqs, out declS, out declC, out dist, out sl);
            // balanced start: psi2 = 0 at the surface layer, psi1 = Delta_eq (thermal wind), plus a seeded perturbation
            BuildDeltaEqThermalWind(teqs, deqR, deqI);
            for (int i = 0; i < dyn.NCoef; i++) { psi1r[i] = deqR[i]; psi1i[i] = deqI[i]; psi2r[i] = 0.0; psi2i[i] = 0.0; }
            var rng = new Rng(seed ^ 0xD1B54A32D192ED03UL);
            double amp = 1.0e5 * (a / 6.0e5) * (a / 6.0e5);
            for (int m = 1; m <= dyn.M; m++)
                for (int n = m; n <= dyn.M && n <= 14; n++)
                {
                    int i = dyn.Index(m, n);
                    double sc = amp / (1.0 + 0.3 * n);
                    double r1 = rng.Gauss(), r2 = rng.Gauss(), r3 = rng.Gauss(), r4 = rng.Gauss();
                    psi1r[i] += sc * r1; psi1i[i] += sc * r2; psi2r[i] += 0.5 * sc * r3; psi2i[i] += 0.5 * sc * r4;
                }
            // PV from the streamfunctions
            for (int i = 0; i < dyn.NCoef; i++)
            {
                double d1r = psi1r[i] - psi2r[i], d1i = psi1i[i] - psi2i[i];
                q1r[i] = -nn[i] * psi1r[i] - deformF * d1r; q1i[i] = -nn[i] * psi1i[i] - deformF * d1i;
                q2r[i] = -nn[i] * psi2r[i] + deformF * d1r; q2i[i] = -nn[i] * psi2i[i] + deformF * d1i;
            }
            q1r[0] = q1i[0] = q2r[0] = q2i[0] = 0.0;
            // surface and moisture climatology
            for (int j = 0; j < NLat; j++)
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    double tsurf = teqs[j] - gamma * DMath.Max(TerrainM[c], 0.0) * (1.0 - OceanFrac[c]);
                    Ts[c] = tsurf;
                    SnowW[c] = 0.0; IceH[c] = 0.0; DustAir[c] = 0.0; DustDep[c] = 0.0;
                    SoilMm[c] = Body.OceanFractionTarget > 0.0 && Cfg.OceanOn ? 0.5 * Body.SoilCapacityMm : 0.0;
                    if (OceanFrac[c] > 0.5 && tsurf < Body.SeaFreezeK) IceH[c] = DMath.Min(0.5 + (Body.SeaFreezeK - tsurf) * 0.3, 2.5);
                }
            Array.Clear(heatGrid, 0, heatGrid.Length);
            Diagnose(false);
            for (int k = 0; k < K; k++)
                for (int c = 0; c < N2; c++)
                {
                    double p = Body.SurfacePressurePa * sigma[k];
                    Qv[k * N2 + c] = 0.7 * Qsat(T3[k * N2 + c], p) * (Cfg.OceanOn || Body.OceanFractionTarget > 0 ? 1.0 : 0.05);
                    Qc[k * N2 + c] = 0.0;
                }
            Diagnose(false);
        }

        private double TeqSurfaceAtMu(double[] teqs, double mu)
        {
            // linear interpolation in mu between the physics rows (descending mu)
            int n = NLat;
            if (mu >= sinLat[0]) return teqs[0];
            if (mu <= sinLat[n - 1]) return teqs[n - 1];
            int j = 0;
            // physics rows are almost uniform in latitude: start from an estimate
            j = (int)((sinLat[0] - mu) / (sinLat[0] - sinLat[n - 1]) * (n - 1));
            if (j < 0) j = 0; if (j > n - 2) j = n - 2;
            while (j > 0 && sinLat[j] < mu) j--;
            while (j < n - 2 && sinLat[j + 1] > mu) j++;
            double f = (sinLat[j] - mu) / (sinLat[j] - sinLat[j + 1]);
            return teqs[j] * (1.0 - f) + teqs[j + 1] * f;
        }

        private double MidTemp(double tsurf)
        {
            double z = -hScale * DMath.Log(0.5);
            double strat = Body.SurfaceMeanK - gamma * Body.TropopauseM;
            double t = tsurf - gamma * z;
            return t > strat ? t : strat;
        }

        // --- fixed-point sampling used by the runner and the hash ---------------------------------------------------

        public int Index(int j, int i) { return j * NLon + i; }
        public double LatDeg(int j) { return latRad[j] * 180.0 / DMath.Pi; }
        public double LonDeg(int i) { return phy.LonRad[i] * 180.0 / DMath.Pi; }
        public double[] PhysLatRad { get { return phy.LatRad; } }
        public double[] PhysWeight { get { return phy.Weight; } }
        public double[] SigmaLevels { get { return sigma; } }
        public double[] ZLevels { get { return zLevel; } }
        public int DynGridLon { get { return dyn.NLon; } }
        public int DynGridLat { get { return dyn.NLat; } }
        public double DeformationRadiusM { get { return DMath.Sqrt(1.0 / deformF); } }
        public double RadiativeTauS { get { return radTau; } }

        // The 64-bit hash (FNV-1a over the bit patterns) of the prognostic state.
        public ulong StateHash()
        {
            ulong h = 14695981039346656037UL;
            h = HashArr(h, q1r); h = HashArr(h, q1i); h = HashArr(h, q2r); h = HashArr(h, q2i);
            h = HashArr(h, Qv); h = HashArr(h, Qc); h = HashArr(h, Ts); h = HashArr(h, SnowW); h = HashArr(h, IceH);
            h = HashArr(h, SoilMm); h = HashArr(h, DustAir); h = HashArr(h, DustDep); h = HashArr(h, heatGrid);
            h = HashOne(h, (ulong)StepIndex); h = HashOne(h, (ulong)BitConverter.DoubleToInt64Bits(Ut));
            return h;
        }

        private static ulong HashOne(ulong h, ulong v)
        {
            for (int b = 0; b < 8; b++) { h ^= (v >> (8 * b)) & 0xFFUL; h *= 1099511628211UL; }
            return h;
        }

        private static ulong HashArr(ulong h, double[] x)
        {
            for (int i = 0; i < x.Length; i++) h = HashOne(h, (ulong)BitConverter.DoubleToInt64Bits(x[i]));
            return h;
        }

        // --- checkpoints ----------------------------------------------------------------------------------------------

        private double[][] StateArrays()
        {
            return new[] { q1r, q1i, q2r, q2i, Qv, Qc, Ts, SnowW, IceH, SoilMm, DustAir, DustDep, heatGrid };
        }

        public int StateBytes()
        {
            int n = 0; foreach (var x in StateArrays()) n += x.Length * 8;
            return n + 16;
        }

        // Resident memory of this simulation in bytes: the prognostic state, the diagnosed 3D fields and the scratch arrays.
        public long ResidentBytes()
        {
            long n3 = (long)K * N2, b = 0;
            b += StateBytes();
            b += 7 * n3 * 8;           // T3 U3 V3 Omega3 kQv kQc plus a work array
            b += 16L * N2 * 8;         // 2D diagnosed fields and scratch
            b += 40L * dyn.NCoef * 8 + 16L * dynN2 * 8;
            return b;
        }

        public void SaveCheckpoint(Stream s)
        {
            var w = new BinaryWriter(s);
            w.Write(Ut); w.Write(StepIndex);
            foreach (var x in StateArrays()) for (int i = 0; i < x.Length; i++) w.Write(x[i]);
            w.Flush();
        }

        public void LoadCheckpoint(Stream s)
        {
            var r = new BinaryReader(s);
            Ut = r.ReadDouble(); StepIndex = r.ReadInt64();
            foreach (var x in StateArrays()) for (int i = 0; i < x.Length; i++) x[i] = r.ReadDouble();
            Diagnose(false);
        }

        public Atmosphere Clone()
        {
            var c = new Atmosphere(Body, Cfg);
            var ms = new MemoryStream();
            SaveCheckpoint(ms); ms.Position = 0;
            c.LoadCheckpoint(ms);
            return c;
        }
    }

    // A small deterministic generator (xorshift64*), used only for the seeded initial perturbation and the synthetic maps.
    public sealed class Rng
    {
        private ulong s;
        public Rng(ulong seed) { s = seed == 0 ? 0x2545F4914F6CDD1DUL : seed; for (int i = 0; i < 4; i++) Next(); }
        public ulong Next()
        {
            s ^= s >> 12; s ^= s << 25; s ^= s >> 27;
            return s * 0x2545F4914F6CDD1DUL;
        }
        public double Uniform() { return (Next() >> 11) * (1.0 / 9007199254740992.0); }
        // Sum of twelve uniforms minus six: approximately standard normal, without log or sqrt.
        public double Gauss()
        {
            double t = 0; for (int i = 0; i < 12; i++) t += Uniform();
            return t - 6.0;
        }
    }
}
