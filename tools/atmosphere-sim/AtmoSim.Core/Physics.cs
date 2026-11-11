using System;

namespace AtmoSim.Core
{
    // Moisture, cloud water, convection, precipitation and the 2D surface layer on the physics grid.
    public sealed partial class Atmosphere
    {
        // Cumulative bookkeeping of the water stores (kg/m2, global mean) for the closure check of criterion 1.
        public double OceanSourceKgM2;
        public double StoresInitialKgM2 = double.NaN;

        private const double CapeMin = 30.0;
        private const double QcCrit = 2.0e-4;

        public double AtmosphereWaterKgM2()
        {
            double s = 0, w = 0;
            double g = Body.GravityMs2, ps = Body.SurfacePressurePa;
            for (int j = 0; j < NLat; j++)
            {
                double rs = 0;
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    for (int k = 0; k < K; k++) rs += (Qv[k * N2 + c] + Qc[k * N2 + c]) * dpFrac[k];
                }
                s += rs / NLon * ps / g * phy.Weight[j]; w += phy.Weight[j];
            }
            return s / w;
        }

        public double SurfaceWaterKgM2()
        {
            double s = 0, w = 0;
            for (int j = 0; j < NLat; j++)
            {
                double rs = 0;
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    double land = 1.0 - OceanFrac[c];
                    rs += land * (SoilMm[c] + SnowW[c] * 1000.0) + OceanFrac[c] * IceH[c] * 917.0;
                }
                s += rs / NLon * phy.Weight[j]; w += phy.Weight[j];
            }
            return s / w;
        }

        public double WaterStoresKgM2() { return AtmosphereWaterKgM2() + SurfaceWaterKgM2(); }

        // The global mass integral of a 3D mixing ratio (the semi-Lagrangian step does not conserve it; a global fixer restores it).
        private double Mass(double[] f)
        {
            double s = 0;
            for (int k = 0; k < K; k++)
                for (int j = 0; j < NLat; j++)
                {
                    double r = 0;
                    int b = k * N2 + j * NLon;
                    for (int i = 0; i < NLon; i++) r += f[b + i];
                    s += r * phy.Weight[j] * dpFrac[k];
                }
            return s;
        }

        private void Rescale(double[] f, double target)
        {
            double m = Mass(f);
            if (m <= 0 || target <= 0) return;
            double s = target / m;
            for (int i = 0; i < f.Length; i++) f[i] *= s;
        }

        private double Lat(double t) { return t < Body.TripleK ? Body.LatentSublimation : Body.LatentVapour; }

        private void Physics(double utMid, double declS, double declC, double dist, double subLon)
        {
            if (double.IsNaN(StoresInitialKgM2)) StoresInitialKgM2 = WaterStoresKgM2();
            double ps = Body.SurfacePressurePa, g = Body.GravityMs2, cp = Body.Cp, rv = Body.Rv;
            double massCol = ps / g;
            double dt = Dt;
            double wsum = 0;
            double oceanNet = 0;

            // --- 1. surface fluxes: evaporation into the lowest level, boundary-layer mixing -----------------------
            for (int j = 0; j < NLat; j++)
            {
                double rowNet = 0;
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    double oc = OceanFrac[c];
                    double u = U3[c], v = V3[c];
                    double ueff = DMath.Max(DMath.Sqrt(u * u + v * v), 3.0);
                    double qs = Qsat(Ts[c], ps);
                    double q0 = Qv[c];
                    double rho0 = ps / (Body.GasConstant * Ts[c]);
                    double icef = 1.0 - DMath.Exp(-IceH[c] / 0.15);
                    double eo = oc > 0 ? oc * (1.0 - icef) * rho0 * 1.3e-3 * ueff * DMath.Max(qs - q0, 0.0) : 0.0;
                    double snowCover = 1.0 - DMath.Exp(-SnowW[c] / 0.008);
                    double soilF = SoilMm[c] / Body.SoilCapacityMm;
                    double el = (1.0 - oc) > 0 ? (1.0 - oc) * (1.0 - snowCover) * soilF * rho0 * 1.3e-3 * ueff * DMath.Max(qs - q0, 0.0) : 0.0;
                    double cap = 0.8 * (qs - q0) * massCol * dpFrac[0] / dt;
                    if (cap < 0) cap = 0;
                    double etot = eo + el;
                    if (etot > cap && etot > 0) { double sc = cap / etot; eo *= sc; el *= sc; }
                    double landMm = el * dt; // kg/m2 = mm
                    if (landMm > SoilMm[c] * (1.0 - oc) && (1.0 - oc) > 0) { double sc = SoilMm[c] * (1.0 - oc) / landMm; landMm *= sc; el *= sc; }
                    SoilMm[c] -= (1.0 - oc) > 0 ? landMm / (1.0 - oc) : 0.0;
                    Qv[c] += (eo + el) * dt / (massCol * dpFrac[0]);
                    rowNet += eo * dt;
                    // boundary-layer mixing of vapour over the lowest three levels (mass weighted)
                    double mf = 1.0 - DMath.Exp(-dt / 10800.0);
                    double mean = 0, mw = 0;
                    for (int k = 0; k < 3 && k < K; k++) { mean += Qv[k * N2 + c] * dpFrac[k]; mw += dpFrac[k]; }
                    mean /= mw;
                    for (int k = 0; k < 3 && k < K; k++) Qv[k * N2 + c] += mf * (mean - Qv[k * N2 + c]);
                }
                oceanNet += rowNet / NLon * phy.Weight[j]; wsum += phy.Weight[j];
            }
            OceanSourceKgM2 += oceanNet / wsum;

            // --- 2. transport -----------------------------------------------------------------------------------
            double mv0 = Mass(Qv), mc0 = Mass(Qc);
            Advect(Qv, kQv); Advect(Qc, kQc);
            Array.Copy(kQv, Qv, Qv.Length); Array.Copy(kQc, Qc, Qc.Length);
            Rescale(Qv, mv0); Rescale(Qc, mc0);

            // --- 3. condensation, convection, precipitation per column --------------------------------------
            double precipOcean = 0;
            double[] hk = new double[K];
            double[] heat = heatGrid;
            double dtH = dt / 3600.0;
            for (int j = 0; j < NLat; j++)
            {
                double rowPOcean = 0;
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    double hsum = 0.0;              // sum of L * dq (J/kg of column mass)
                    // large-scale condensation, bottom to top
                    for (int k = 0; k < K; k++)
                    {
                        int idx = k * N2 + c;
                        double t = T3[idx], p = ps * sigma[k];
                        double qs = Qsat(t, p);
                        double l = Lat(t);
                        if (Qv[idx] > qs)
                        {
                            double dqs = qs * l * l / (cp * rv * t * t);
                            double dq = (Qv[idx] - qs) / (1.0 + dqs);
                            Qv[idx] -= dq; Qc[idx] += dq;
                            hsum += l * dq * dpFrac[k];
                        }
                        else if (Qc[idx] > 0)
                        {
                            double dq = DMath.Min(Qc[idx], (qs - Qv[idx]) * 0.5);
                            Qv[idx] += dq; Qc[idx] -= dq;
                            hsum -= l * dq * dpFrac[k];
                        }
                    }
                    // convection: parcel from the lowest levels with the moist static energy
                    double t0 = T3[c];
                    double h0 = cp * t0 + g * zLevel[0] + Lat(t0) * Qv[c];
                    {
                        double h1 = cp * T3[N2 + c] + g * zLevel[1] + Lat(T3[N2 + c]) * Qv[N2 + c];
                        h0 = 0.5 * (h0 + h1);
                    }
                    double cape = 0.0; int ktop = 0;
                    for (int k = 2; k < K; k++)
                    {
                        int idx = k * N2 + c;
                        double t = T3[idx], p = ps * sigma[k];
                        double hs = cp * t + g * zLevel[k] + Lat(t) * Qsat(t, p);
                        if (h0 > hs) { cape += g * (h0 - hs) / (cp * t) * (zLevel[k] - zLevel[k - 1]); ktop = k; }
                        else break;
                    }
                    double convP = 0.0, wc = 0.0;
                    if (ktop >= 3 && cape > CapeMin)
                    {
                        double f = DMath.Min(1.0, dt / 7200.0);
                        double removed = 0.0;
                        for (int k = 0; k < 3; k++)
                        {
                            int idx = k * N2 + c;
                            double qs = Qsat(T3[idx], ps * sigma[k]);
                            double dq = f * 0.3 * DMath.Max(0.0, Qv[idx] - 0.6 * qs);
                            Qv[idx] -= dq; removed += dq * dpFrac[k];
                            hsum += Lat(T3[idx]) * dq * dpFrac[k];
                        }
                        // part detrains as cloud water in the upper levels of the cloud, the rest rains out
                        double det = 0.35 * removed;
                        int k0 = ktop - 2 > 3 ? ktop - 2 : 3;
                        double wsumk = 0;
                        for (int k = k0; k <= ktop; k++) wsumk += dpFrac[k];
                        for (int k = k0; k <= ktop; k++) Qc[k * N2 + c] += det / wsumk;
                        convP = 0.65 * removed * massCol; // kg/m2 this step
                        wc = DMath.Min(30.0, DMath.Sqrt(DMath.Max(cape, 0.0)));
                    }
                    ConvW[c] = wc;
                    ConvTopM[c] = wc > 0.0 ? zLevel[ktop] : 0.0;
                    // autoconversion and fall-out from the top; evaporation into dry layers
                    double flux = 0.0;
                    for (int k = K - 1; k >= 0; k--)
                    {
                        int idx = k * N2 + c;
                        if (k == (ktop > 0 ? ktop : -1)) flux += convP;
                        double auto = DMath.Max(0.0, Qc[idx] - QcCrit) * (1.0 - DMath.Exp(-dt / 2400.0));
                        Qc[idx] -= auto;
                        double mass = massCol * dpFrac[k];
                        flux += auto * mass;
                        double t = T3[idx], qs = Qsat(t, ps * sigma[k]);
                        if (flux > 0 && Qv[idx] < qs)
                        {
                            double ev = DMath.Min(flux, (qs - Qv[idx]) * mass * 0.25);
                            Qv[idx] += ev / mass; flux -= ev;
                            hsum -= Lat(t) * ev / massCol;
                        }
                    }
                    if (ktop == 0) { /* no convective rain this step */ }
                    PrecipMmH[c] = flux / dtH;
                    PrecipConvMmH[c] = convP / dtH;
                    // heating of the column mean, K/step -> K/s
                    double dTcol = hsum / cp;
                    double hs_ = dTcol / dt;
                    if (hs_ > 3.0e-4) hs_ = 3.0e-4; if (hs_ < -3.0e-4) hs_ = -3.0e-4;
                    heat[c] = hs_;
                    HeatK[c] = dTcol;
                    // snow fraction by wet-bulb-ish temperature
                    double tw = T3[c] - 0.5;
                    double fs = 1.0 / (1.0 + DMath.Exp((tw - 274.15) / 0.9));
                    SnowFall[c] = PrecipMmH[c] * fs;
                    // column cloud water path (kg/m2)
                    double cwp = 0;
                    for (int k = 0; k < K; k++) cwp += Qc[k * N2 + c] * dpFrac[k];
                    CloudPath[c] = cwp * massCol;
                    // surface stores
                    double oc = OceanFrac[c];
                    double pm = flux; // kg/m2 = mm
                    rowPOcean += oc * pm;
                    double landIn = (1.0 - oc) * pm;
                    if (oc < 1.0)
                    {
                        double perLand = pm; // per land area
                        if (fs > 0.5) SnowW[c] += perLand * 0.001;
                        else SoilMm[c] += perLand;
                        if (fs > 0.5 && false) { }
                    }
                }
                precipOcean += rowPOcean / NLon * phy.Weight[j];
            }
            OceanSourceKgM2 -= precipOcean / wsum;

            // --- 4. surface layer ---------------------------------------------------------------------------------
            SurfaceLayer(utMid, declS, declC, dist, subLon, wsum);
            // water that the soil cannot hold runs to the sea (a body without sea keeps it)
            bool hasOcean = false;
            for (int c = 0; c < N2 && !hasOcean; c++) hasOcean = OceanFrac[c] > 0.0;
            double ro = 0;
            for (int j = 0; j < NLat; j++)
            {
                double rr = 0;
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    if (hasOcean && SoilMm[c] > Body.SoilCapacityMm) { rr += (1.0 - OceanFrac[c]) * (SoilMm[c] - Body.SoilCapacityMm); SoilMm[c] = Body.SoilCapacityMm; }
                }
                ro += rr / NLon * phy.Weight[j];
            }
            OceanSourceKgM2 -= ro / wsum;
            WaterLedgerResidual = WaterStoresKgM2() - StoresInitialKgM2 - OceanSourceKgM2;
        }

        private void SurfaceLayer(double ut, double declS, double declC, double dist, double subLon, double wsum)
        {
            double dt = Dt, dtH = dt / 3600.0;
            double cSea = Body.SeaHeatCapacityPerM * Body.SeaMixedDepthM;
            double tauSea = cSea / 15.0, tauLand = 21600.0;
            double gain = Body.DiurnalLandAmpK * 9.5;
            double tf = Body.SeaFreezeK;
            double iceNet = 0;
            double ps = Body.SurfacePressurePa;
            for (int j = 0; j < NLat; j++)
            {
                double rowIce = 0;
                double sbar = DailyMean(sinLat[j], cosLat[j], declS, declC) * dist;
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    double oc = OceanFrac[c];
                    double ha = phy.LonRad[i] - subLon;
                    double mu0 = sinLat[j] * declS + cosLat[j] * declC * DMath.Cos(ha);
                    double s = (mu0 > 0 ? mu0 : 0.0) * dist;
                    double tAir = T3[c];
                    double landTarget = tAir - gamma * 0.0 + gain * (s - sbar);
                    double seaTarget = tAir;
                    double rate = oc / tauSea + (1.0 - oc) / tauLand;
                    double target = oc > 0.5 ? seaTarget : landTarget;
                    double f = 1.0 - DMath.Exp(-dt * rate);
                    Ts[c] += (target - Ts[c]) * f;
                    // sea ice from the heat deficit of the mixed layer
                    if (oc > 0.3)
                    {
                        double before = IceH[c];
                        if (Ts[c] < tf)
                        {
                            double dh = cSea * (tf - Ts[c]) / Body.IceLatentDensity;
                            IceH[c] += dh; Ts[c] = tf;
                        }
                        else if (IceH[c] > 0 && Ts[c] > tf)
                        {
                            double melt = DMath.Min(IceH[c], cSea * (Ts[c] - tf) / Body.IceLatentDensity);
                            IceH[c] -= melt; Ts[c] -= melt * Body.IceLatentDensity / cSea;
                        }
                        if (IceH[c] > 0)
                        {
                            double grow = dt * 2.1 * DMath.Max(0.0, tf - tAir) / (Body.IceLatentDensity * (IceH[c] + 0.1));
                            double top = 0.01 * DMath.Max(0.0, tAir - tf) / 86400.0 * dt;
                            IceH[c] = DMath.Max(0.0, IceH[c] + grow - DMath.Min(top, IceH[c]));
                        }
                        rowIce += oc * (IceH[c] - before) * 917.0;
                    }
                    else if (IceH[c] > 0) { rowIce -= oc * IceH[c] * 917.0; IceH[c] = 0.0; }
                    // snow on land: melt by degree-hours, sublimation and blow-off
                    if (oc < 1.0 && SnowW[c] > 0)
                    {
                        double before = SnowW[c];
                        double melt = 0.18e-3 * DMath.Max(0.0, tAir - 273.15) * dtH;
                        double subl = 0.002e-3 * dtH;
                        double w10 = Wind10[c];
                        double blow = w10 > 12.0 ? 0.01e-3 * (w10 - 12.0) * dtH : 0.0;
                        double loss = DMath.Min(before, melt + subl + blow);
                        SnowW[c] = before - loss;
                        // the melt water wets the soil, sublimation and blow-off leave the stores
                        double toSoil = DMath.Min(loss, melt) * 1000.0;
                        SoilMm[c] += toSoil;
                        // sublimation and wind erosion return to the air
                        double toAir = loss * 1000.0 - toSoil;
                        Qv[c] += toAir * (1.0 - oc) / (ps / Body.GravityMs2 * dpFrac[0]);
                    }
                    // dust over dry land
                    if (Body.Dusty && oc < 0.5)
                    {
                        double w10 = Wind10[c];
                        double lift = w10 > Body.DustWindThresholdMs ? 2.0e-6 * (w10 - Body.DustWindThresholdMs) * (w10 - Body.DustWindThresholdMs) * dt : 0.0;
                        double settle = DustAir[c] * (1.0 - DMath.Exp(-dt / 172800.0));
                        DustAir[c] += lift - settle; DustDep[c] += settle;
                    }
                }
                iceNet += rowIce / NLon * phy.Weight[j];
            }
            // ice formation takes water from the sea, melting returns it; blow-off and sublimation leave the stores
            OceanSourceKgM2 += iceNet / wsum;
        }

        // --- semi-Lagrangian transport of a 3D field -----------------------------------------------------------

        private void Advect(double[] src, double[] dst)
        {
            double dt = Dt, ps = Body.SurfacePressurePa;
            double dlonCell = DMath.TwoPi / NLon;
            double top = latRad[0], bot = latRad[NLat - 1];
            for (int k = 0; k < K; k++)
            {
                for (int j = 0; j < NLat; j++)
                {
                    double cl = DMath.Max(cosLat[j], 0.12);
                    for (int i = 0; i < NLon; i++)
                    {
                        int c = j * NLon + i, idx = k * N2 + c;
                        double u = U3[idx], v = V3[idx];
                        double fi = i - u * dt / (a * cl * dlonCell);
                        double lat = latRad[j] - v * dt / a;
                        if (lat > top) lat = top; if (lat < bot) lat = bot;
                        // row index (rows run north to south)
                        int j0 = (int)((top - lat) / (top - bot) * (NLat - 1));
                        if (j0 < 0) j0 = 0; if (j0 > NLat - 2) j0 = NLat - 2;
                        while (j0 > 0 && latRad[j0] < lat) j0--;
                        while (j0 < NLat - 2 && latRad[j0 + 1] > lat) j0++;
                        double fj = (latRad[j0] - lat) / (latRad[j0] - latRad[j0 + 1]);
                        if (fj < 0) fj = 0; if (fj > 1) fj = 1;
                        double fl = fi - DMath.Floor(fi / NLon) * NLon;
                        int i0 = (int)fl; double fx = fl - i0; if (i0 >= NLon) { i0 -= NLon; }
                        int i1 = i0 + 1 == NLon ? 0 : i0 + 1;
                        // vertical
                        double sd = sigma[k] - Omega3[idx] / ps * dt;
                        int k0; double fk;
                        if (sd >= sigma[0]) { k0 = 0; fk = 0.0; }
                        else if (sd <= sigma[K - 1]) { k0 = K - 2 < 0 ? 0 : K - 2; fk = K > 1 ? 1.0 : 0.0; }
                        else
                        {
                            k0 = k; if (k0 > K - 2) k0 = K - 2;
                            while (k0 > 0 && sigma[k0] < sd) k0--;
                            while (k0 < K - 2 && sigma[k0 + 1] > sd) k0++;
                            fk = (sigma[k0] - sd) / (sigma[k0] - sigma[k0 + 1]);
                        }
                        int k1 = K > 1 ? k0 + 1 : k0;
                        double v00 = Bilin(src, k0, j0, i0, i1, fj, fx);
                        double v10 = k1 == k0 ? v00 : Bilin(src, k1, j0, i0, i1, fj, fx);
                        dst[idx] = v00 * (1.0 - fk) + v10 * fk;
                    }
                }
            }
        }

        private double Bilin(double[] s, int k, int j0, int i0, int i1, double fj, double fx)
        {
            int b = k * N2;
            double a00 = s[b + j0 * NLon + i0], a01 = s[b + j0 * NLon + i1];
            double a10 = s[b + (j0 + 1) * NLon + i0], a11 = s[b + (j0 + 1) * NLon + i1];
            double n = a00 * (1.0 - fx) + a01 * fx, m = a10 * (1.0 - fx) + a11 * fx;
            return n * (1.0 - fj) + m * fj;
        }
    }
}
