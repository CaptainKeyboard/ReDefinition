using System;

namespace AtmoSim.Core
{
    // The dynamical core: a two-layer quasi-geostrophic model on the sphere (Phillips type), potential vorticity of the upper
    // and the lower layer, spectral inversion, strong-stability Runge-Kutta in time. The thickness Delta = psi1 - psi2 is the
    // temperature of the column through the thermal wind; it relaxes to an equilibrium that follows the insolation.
    public sealed partial class Atmosphere
    {
        private double[] teqsCur;       // equilibrium surface temperature of the physics rows
        private double tmRef;           // global-mean equilibrium temperature of the mid layer
        private const double HLat0 = 0.20791169081775934; // sin(12 degrees): the width of the regularised sign near the equator
        private const double HEps = 0.25;

        // The smooth sign of the Coriolis parameter, zero at the equator.
        private static double SignH(double sinLat)
        {
            return sinLat / DMath.Sqrt(sinLat * sinLat + HLat0 * HLat0);
        }

        // Delta_eq (spectral) from the meridional gradient of the equilibrium mid-layer temperature (thermal wind, regularised at the equator).
        private void BuildDeltaEqThermalWind(double[] teqs, double[] re, double[] im)
        {
            int nl = dyn.NLat, nx = dyn.NLon;
            double[] tm = new double[nl], dd = new double[nl], de = new double[nl];
            double wsum = 0, tmGm = 0;
            for (int j = 0; j < nl; j++)
            {
                tm[j] = MidTemp(TeqSurfaceAtMu(teqs, dyn.Mu[j]));
                tmGm += tm[j] * dyn.Weight[j]; wsum += dyn.Weight[j];
            }
            tmRef = tmGm / wsum;
            double rl = Body.GasConstant * DMath.Log(sigmaMid2 / sigmaMid1) / f0;
            for (int j = 0; j < nl; j++)
            {
                int jn = j > 0 ? j - 1 : j, js = j < nl - 1 ? j + 1 : j;
                double dt = (tm[jn] - tm[js]) / (dyn.LatRad[jn] - dyn.LatRad[js]);
                double h = SignH(dyn.Mu[j]);
                dd[j] = rl * dt * h / (h * h + HEps * HEps);
            }
            de[nl - 1] = 0.0;
            for (int j = nl - 1; j > 0; j--) de[j - 1] = de[j] + 0.5 * (dd[j] + dd[j - 1]) * (dyn.LatRad[j - 1] - dyn.LatRad[j]);
            double mean = 0;
            for (int j = 0; j < nl; j++) mean += de[j] * dyn.Weight[j];
            mean /= wsum;
            double[] g = scratchA;
            for (int j = 0; j < nl; j++)
                for (int i = 0; i < nx; i++) g[j * nx + i] = de[j] - mean;
            dyn.Analyze(g, re, im);
            re[0] = 0.0; im[0] = 0.0;
        }

        private void Invert(double[] a1r, double[] a1i, double[] a2r, double[] a2i, double[] p1r, double[] p1i, double[] p2r, double[] p2i)
        {
            int nc = dyn.NCoef;
            p1r[0] = p1i[0] = p2r[0] = p2i[0] = 0.0;
            double f2 = 2.0 * deformF;
            for (int i = 1; i < nc; i++)
            {
                double btr = 0.5 * (a1r[i] + a2r[i]), bti = 0.5 * (a1i[i] + a2i[i]);
                double bcr = 0.5 * (a1r[i] - a2r[i]), bci = 0.5 * (a1i[i] - a2i[i]);
                double pbr = nn[i] > 0 ? -btr / nn[i] : 0.0, pbi = nn[i] > 0 ? -bti / nn[i] : 0.0;
                double pcr = -bcr / (nn[i] + f2), pci = -bci / (nn[i] + f2);
                p1r[i] = pbr + pcr; p1i[i] = pbi + pci; p2r[i] = pbr - pcr; p2i[i] = pbi - pci;
            }
        }

        // Tendency of the PV of both layers at a state (advection of absolute PV, thermal relaxation, latent heating, drag).
        private void Rhs(double[] a1r, double[] a1i, double[] a2r, double[] a2i, double[] o1r, double[] o1i, double[] o2r, double[] o2i)
        {
            Invert(a1r, a1i, a2r, a2i, psi1r, psi1i, psi2r, psi2i);
            int nl = dyn.NLat, nx = dyn.NLon, nc = dyn.NCoef;
            for (int layer = 0; layer < 2; layer++)
            {
                double[] pr = layer == 0 ? psi1r : psi2r, pi = layer == 0 ? psi1i : psi2i;
                double[] qr = layer == 0 ? a1r : a2r, qi = layer == 0 ? a1i : a2i;
                double[] orr = layer == 0 ? o1r : o2r, oi = layer == 0 ? o1i : o2i;
                dyn.SynthesizeWindCos(pr, pi, a, ucosD, vcosD);
                dyn.Synthesize(qr, qi, qgD);
                for (int j = 0; j < nl; j++)
                {
                    double f = 2.0 * omega * dyn.Mu[j];
                    for (int i = 0; i < nx; i++)
                    {
                        int c = j * nx + i;
                        double qa = qgD[c] + f;
                        aD[c] = ucosD[c] * qa; bD[c] = vcosD[c] * qa;
                    }
                }
                dyn.FluxDivergence(aD, bD, a, k1r_tmp, k1i_tmp);
                for (int i = 0; i < nc; i++) { orr[i] = -k1r_tmp[i]; oi[i] = -k1i_tmp[i]; }
            }
            // forcing: thermal relaxation toward Delta_eq, latent heating, in terms of the thickness tendency
            double invTau = 1.0 / radTau;
            double rl = 1.0 / tCoef;
            for (int i = 1; i < nc; i++)
            {
                double dr = psi1r[i] - psi2r[i], di = psi1i[i] - psi2i[i];
                double tr = -(dr - deqR[i]) * invTau + latR[i];
                double ti = -(di - deqI[i]) * invTau + latI[i];
                o1r[i] -= deformF * tr; o1i[i] -= deformF * ti;
                o2r[i] += deformF * tr; o2i[i] += deformF * ti;
                // Ekman drag on the lower layer, weak drag on the upper one: -r * vorticity = + r * n(n+1)/a^2 * psi
                o2r[i] += ekmanR * nn[i] * psi2r[i]; o2i[i] += ekmanR * nn[i] * psi2i[i];
                o1r[i] += 0.1 * ekmanR * nn[i] * psi1r[i]; o1i[i] += 0.1 * ekmanR * nn[i] * psi1i[i];
            }
            o1r[0] = o1i[0] = o2r[0] = o2i[0] = 0.0;
        }
        private double[] k1r_tmp, k1i_tmp;

        private void EnsureTmp()
        {
            if (k1r_tmp == null) { k1r_tmp = new double[dyn.NCoef]; k1i_tmp = new double[dyn.NCoef]; }
        }

        // One simulation step: dynamics (sub-stepped), diagnosis of the 3D fields, moist physics and the surface layer.
        public void Step()
        {
            EnsureTmp();
            double ut0 = Ut, utMid = ut0 + 0.5 * Dt;
            double[] teqs = teqsCur ?? (teqsCur = new double[NLat]);
            double declS, declC, dist, subLon;
            EquilibriumRows(utMid, teqs, out declS, out declC, out dist, out subLon);
            BuildDeltaEqThermalWind(teqs, deqR, deqI);
            // latent heating of the last step as a thickness tendency: dDelta/dt = Q / c(lat), regularised at the equator
            {
                phy.Analyze(heatGridDelta(), latR, latI);
                latR[0] = 0.0; latI[0] = 0.0;
            }
            // number of sub-steps from the Courant number of the last winds
            double umax = DMath.Max(MaxWind, 10.0);
            int nsub = (int)(Dt * umax * dyn.M / (a * 1.1)) + 1;
            if (nsub < 2) nsub = 2; if (nsub > 24) nsub = 24;
            double h = Dt / nsub;
            int nc = dyn.NCoef;
            const double tauH = 10800.0; // hyper-diffusion time of the smallest wave: 3 h
            for (int s = 0; s < nsub; s++)
            {
                // SSP RK3
                Rhs(q1r, q1i, q2r, q2i, k1r, k1i, k2r, k2i);
                for (int i = 0; i < nc; i++) { t1r[i] = q1r[i] + h * k1r[i]; t1i[i] = q1i[i] + h * k1i[i]; t2r[i] = q2r[i] + h * k2r[i]; t2i[i] = q2i[i] + h * k2i[i]; }
                Rhs(t1r, t1i, t2r, t2i, k1r, k1i, k2r, k2i);
                for (int i = 0; i < nc; i++)
                {
                    t1r[i] = 0.75 * q1r[i] + 0.25 * (t1r[i] + h * k1r[i]); t1i[i] = 0.75 * q1i[i] + 0.25 * (t1i[i] + h * k1i[i]);
                    t2r[i] = 0.75 * q2r[i] + 0.25 * (t2r[i] + h * k2r[i]); t2i[i] = 0.75 * q2i[i] + 0.25 * (t2i[i] + h * k2i[i]);
                }
                Rhs(t1r, t1i, t2r, t2i, k1r, k1i, k2r, k2i);
                for (int i = 0; i < nc; i++)
                {
                    q1r[i] = (q1r[i] + 2.0 * (t1r[i] + h * k1r[i])) / 3.0; q1i[i] = (q1i[i] + 2.0 * (t1i[i] + h * k1i[i])) / 3.0;
                    q2r[i] = (q2r[i] + 2.0 * (t2r[i] + h * k2r[i])) / 3.0; q2i[i] = (q2i[i] + 2.0 * (t2i[i] + h * k2i[i])) / 3.0;
                }
                // scale-selective damping of the smallest waves
                for (int i = 1; i < nc; i++)
                {
                    double d = DMath.Exp(-h * diffDamp[i] / tauH);
                    q1r[i] *= d; q1i[i] *= d; q2r[i] *= d; q2i[i] *= d;
                }
            }
            Ut = ut0 + Dt; StepIndex++;
            // omega needs the tendency at the final state
            Rhs(q1r, q1i, q2r, q2i, k1r, k1i, k2r, k2i);
            Diagnose(true);
            Physics(utMid, declS, declC, dist, subLon);
        }

        private double[] heatDeltaGrid;
        private double[] heatGridDelta()
        {
            if (heatDeltaGrid == null) heatDeltaGrid = new double[N2];
            double rl = Body.GasConstant * DMath.Log(sigmaMid2 / sigmaMid1) / f0;
            for (int j = 0; j < NLat; j++)
            {
                double h = SignH(sinLat[j]);
                double inv = rl * h / (h * h + HEps * HEps);
                for (int i = 0; i < NLon; i++) heatDeltaGrid[j * NLon + i] = Cfg.LatentFeedback ? heatGrid[j * NLon + i] * inv : 0.0;
            }
            return heatDeltaGrid;
        }

        // The 3D fields from the current state: streamfunctions on the physics grid, temperature, winds, vertical velocity.
        private void Diagnose(bool withOmega)
        {
            EnsureTmp();
            if (teqsCur == null) { teqsCur = new double[NLat]; double a1, a2, a3, a4; EquilibriumRows(Ut, teqsCur, out a1, out a2, out a3, out a4); BuildDeltaEqThermalWind(teqsCur, deqR, deqI); }
            int nc = dyn.NCoef;
            Invert(q1r, q1i, q2r, q2i, psi1r, psi1i, psi2r, psi2i);
            phy.Synthesize(psi1r, psi1i, Psi1Grid);
            phy.Synthesize(psi2r, psi2i, Psi2Grid);
            // Delta - Delta_eq on the physics grid
            for (int i = 0; i < nc; i++) { t1r[i] = psi1r[i] - psi2r[i] - deqR[i]; t1i[i] = psi1i[i] - psi2i[i] - deqI[i]; }
            phy.Synthesize(t1r, t1i, DeltaGrid);
            double[] u1 = wU1 ?? (wU1 = new double[N2]), v1 = wV1 ?? (wV1 = new double[N2]), u2 = wU2 ?? (wU2 = new double[N2]), v2 = wV2 ?? (wV2 = new double[N2]);
            phy.SynthesizeWindCos(psi1r, psi1i, a, u1, v1);
            phy.SynthesizeWindCos(psi2r, psi2i, a, u2, v2);
            double rl = Body.GasConstant * DMath.Log(sigmaMid2 / sigmaMid1) / f0;
            double ps = Body.SurfacePressurePa;
            double maxw = 0;
            double[] gamBase = new double[K];
            double bl = 0.08 * hScale;
            for (int j = 0; j < NLat; j++)
            {
                double cl = DMath.Max(cosLat[j], 0.05);
                double sgn = SignH(sinLat[j]);
                double cT = f0 * sgn / (Body.GasConstant * DMath.Log(sigmaMid2 / sigmaMid1));
                double tmEq = MidTemp(teqsCur[j]);
                double thermalRotation = sinLat[j] * 4.0; if (thermalRotation > 1) thermalRotation = 1; if (thermalRotation < -1) thermalRotation = -1;
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    double uu1 = u1[c] / cl, vv1 = v1[c] / cl, uu2 = u2[c] / cl, vv2 = v2[c] / cl;
                    double tAnom = cT * DeltaGrid[c];
                    if (tAnom > 60.0) tAnom = 60.0; if (tAnom < -60.0) tAnom = -60.0;
                    double tBase0 = 0.0;
                    for (int k = 0; k < K; k++)
                    {
                        double sg = sigma[k];
                        double tEq3 = teqsCur[j] - gamma * zLevel[k];
                        double strat = Body.SurfaceMeanK - gamma * Body.TropopauseM;
                        if (tEq3 < strat) tEq3 = strat;
                        double shape = 1.0 - 0.5 * (2.0 * sg - 1.0) * (2.0 * sg - 1.0);
                        double t = tEq3 + tAnom * shape;
                        T3[k * N2 + c] = t;
                        if (k == 0) tBase0 = t;
                        // wind between the layers, boundary layer turning below the lower layer
                        double s = (sg - sigmaMid1) / (sigmaMid2 - sigmaMid1);
                        double u, v;
                        if (sg <= sigmaMid2)
                        {
                            if (s < -0.4) s = -0.4;
                            u = uu1 + (uu2 - uu1) * s; v = vv1 + (vv2 - vv1) * s;
                        }
                        else
                        {
                            double sb = (sg - sigmaMid2) / (1.0 - sigmaMid2);
                            double th = 0.44 * sb * thermalRotation;
                            double ct = DMath.Cos(th), st = DMath.Sin(th);
                            double sp = 1.0 - 0.35 * sb;
                            u = sp * (uu2 * ct - vv2 * st); v = sp * (uu2 * st + vv2 * ct);
                        }
                        U3[k * N2 + c] = u; V3[k * N2 + c] = v;
                    }
                    // surface coupling: the skin temperature pulls the lowest levels
                    double ds = Ts[c] - tBase0;
                    if (ds > 40.0) ds = 40.0; if (ds < -40.0) ds = -40.0;
                    for (int k = 0; k < 6 && k < K; k++) T3[k * N2 + c] += ds * DMath.Exp(-zLevel[k] / bl);
                }
            }
            AddThermalLowWind();
            // surface pressure anomaly: rho0 f psi2 (geostrophic)
            double rho0 = ps / (Body.GasConstant * Body.SurfaceMeanK);
            for (int j = 0; j < NLat; j++)
            {
                double sgn = SignH(sinLat[j]);
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    PsAnom[c] = rho0 * f0 * sgn * Psi2Grid[c];
                    T2m[c] = T3[c];
                    double w = U3[c] * U3[c] + V3[c] * V3[c];
                    Wind10[c] = 0.8 * DMath.Sqrt(w);
                    if (Wind10[c] > maxw) maxw = Wind10[c];
                }
            }
            for (int c = 0; c < K * N2; c++) { double w = U3[c] * U3[c] + V3[c] * V3[c]; if (w > maxw * maxw) maxw = DMath.Sqrt(w); }
            MaxWind = maxw;
            if (withOmega) ComputeOmega(rl);
            else Array.Clear(Omega3, 0, Omega3.Length);
        }
        private double[] wU1, wV1, wU2, wV2;

        // Down-gradient flow toward warm surface air (thermal lows, sea and land breezes): important where rotation is slow.
        private void AddThermalLowWind()
        {
            double[] anom = wAnom ?? (wAnom = new double[N2]);
            double kth = 0.15 * Body.GasConstant;
            double rbl = 1.0 / 14400.0;
            for (int j = 0; j < NLat; j++)
            {
                double mean = 0;
                for (int i = 0; i < NLon; i++) mean += T3[j * NLon + i];
                mean /= NLon;
                for (int i = 0; i < NLon; i++) anom[j * NLon + i] = -kth * (T3[j * NLon + i] - mean); // geopotential anomaly of the boundary layer
            }
            for (int j = 0; j < NLat; j++)
            {
                double cl = DMath.Max(cosLat[j], 0.05);
                double f = 2.0 * omega * sinLat[j];
                double denom = rbl * rbl + f * f;
                int jN = j > 0 ? j - 1 : j, jS = j < NLat - 1 ? j + 1 : j;
                double dy = a * (latRad[jN] - latRad[jS]);
                double geo = rbl * rbl / denom; // fades the thermal flow where rotation dominates
                for (int i = 0; i < NLon; i++)
                {
                    int ie = i + 1 == NLon ? 0 : i + 1, iw = i == 0 ? NLon - 1 : i - 1;
                    double dx = a * cl * (2.0 * DMath.TwoPi / NLon);
                    double gx = (anom[j * NLon + ie] - anom[j * NLon + iw]) / dx;
                    double gy = dy != 0 ? (anom[jN * NLon + i] - anom[jS * NLon + i]) / dy : 0.0;
                    // (r + i f) W = -(gx + i gy)
                    double wr = -(gx * rbl + gy * f) / denom * geo, wi = -(gy * rbl - gx * f) / denom * geo;
                    for (int k = 0; k < K; k++)
                    {
                        double pr = (sigma[k] - 0.7) / 0.3; if (pr <= 0) continue; if (pr > 1) pr = 1;
                        int idx = k * N2 + j * NLon + i;
                        U3[idx] += pr * wr; V3[idx] += pr * wi;
                    }
                }
            }
        }
        private double[] wAnom;

        private void ComputeOmega(double rl)
        {
            int nc = dyn.NCoef, nx = NLon;
            // d(Delta)/dt from the PV tendencies: dpsi_bc/dt = -((k1 - k2)/2)/(n(n+1)/a^2 + 2F), dDelta/dt = 2 dpsi_bc/dt
            double f2 = 2.0 * deformF;
            double[] dr = t2r, di = t2i;
            dr[0] = di[0] = 0;
            for (int i = 1; i < nc; i++) { dr[i] = -(k1r[i] - k2r[i]) / (nn[i] + f2); di[i] = -(k1i[i] - k2i[i]) / (nn[i] + f2); }
            double[] dDelta = wD0 ?? (wD0 = new double[N2]);
            phy.Synthesize(dr, di, dDelta);
            // advection of Delta by the barotropic wind: J(psi_bt, Delta) = (Ubt Delta_lon + Vbt cos Delta_lat) / (a cos^2)
            for (int i = 0; i < nc; i++)
            {
                t1r[i] = 0.5 * (psi1r[i] + psi2r[i]); t1i[i] = 0.5 * (psi1i[i] + psi2i[i]);
                dr[i] = psi1r[i] - psi2r[i]; di[i] = psi1i[i] - psi2i[i];
            }
            double[] ub = wU1, vb = wV1; // reuse (overwritten below; the layer winds are no longer needed)
            double[] dlon = wDlon ?? (wDlon = new double[N2]), dlat = wDlat ?? (wDlat = new double[N2]);
            phy.SynthesizeWindCos(t1r, t1i, a, ub, vb);
            phy.SynthesizeGradient(dr, di, dlon, dlat);
            double ps = Body.SurfacePressurePa;
            double tauR = 1.0 / radTau;
            // heating in K/s (last step), relaxation of Delta toward its equilibrium as a heating of the mid layer
            for (int j = 0; j < NLat; j++)
            {
                double cl = DMath.Max(cosLat[j], 0.05);
                double sgn = SignH(sinLat[j]);
                double cT = f0 * sgn / (Body.GasConstant * DMath.Log(sigmaMid2 / sigmaMid1));
                for (int i = 0; i < NLon; i++)
                {
                    int c = j * NLon + i;
                    double jac = (ub[c] * dlon[c] + vb[c] * dlat[c]) / (a * cl * cl);
                    double qRadDelta = -DeltaGrid[c] * tauR;
                    double heat = Cfg.LatentFeedback ? heatGrid[c] : 0.0;
                    double om = (cT * (dDelta[c] + jac - qRadDelta) - heat) / stabS;
                    if (om > 3.0) om = 3.0; if (om < -3.0) om = -3.0;
                    for (int k = 0; k < K; k++)
                    {
                        double sg = sigma[k];
                        Omega3[k * N2 + c] = om * 4.0 * sg * (1.0 - sg);
                    }
                }
            }
        }
        private double[] wD0, wDlon, wDlat;
    }
}
