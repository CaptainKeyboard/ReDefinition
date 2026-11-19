namespace AtmoSim.Core
{
    // Moist-air relations shared by the generator and the renderer's height model (HeightModel uses the same Magnus constants).
    public static class MoistAir
    {
        // The Magnus constants for the saturation vapour pressure over water as the WMO CIMO Guide (2008) gives them, ew = 6.112 exp(17.62 t / (243.12 + t)); a dimensionless and b in C.
        public const double MagnusA = 17.62, MagnusB = 243.12;
        public const double ZeroCelsiusK = 273.15;

        // The dew point in kelvin of air at a temperature and a relative humidity from 0 to 1 (clamped to 0.01 to 1, where the formula holds). DMath, so the result is the same on every platform.
        public static double DewPointK(double temperatureK, double humidity01)
        {
            double rh = DMath.Clamp(humidity01, 0.01, 1.0);
            double tc = temperatureK - ZeroCelsiusK;
            double gamma = DMath.Log(rh) + MagnusA * tc / (MagnusB + tc);
            return MagnusB * gamma / (MagnusA - gamma) + ZeroCelsiusK;
        }

        // d ln(e_s) / dT of the Magnus formula at a temperature (1/K): a b / (b + t)^2.
        public static double MagnusSlope(double temperatureK)
        {
            double b = MagnusB + (temperatureK - ZeroCelsiusK);
            return MagnusA * MagnusB / (b * b);
        }

        // The fall of the dew point with height of air lifted without condensing (K/m). The mixing ratio is kept, so the vapour pressure falls with the pressure,
        // d ln e / dz = -g / (Rd T) (hydrostatic); at the dew point d ln e = (d ln e_s / dT) dTd, so dTd/dz = -g / (Rd T) / (d ln e_s / dT at Td). Derived [D]; with the dry-adiabatic
        // lapse g / cp it gives the lifting condensation level below.
        public static double DewPointLapseKPerM(double gravity, double gasConstant, double temperatureK, double dewPointK)
        {
            return gravity / (gasConstant * temperatureK) / MagnusSlope(dewPointK);
        }

        // The metres of lifting condensation level per kelvin of dew-point spread: 1 / (g / cp - dew-point lapse). For g 9.81, cp 1004, Rd 287 at 10 C it is 125.1 m/K, the
        // coefficient of Lawrence (2005, BAMS 86, eq. 22: z_LCL ~ 125 (t - td), the principle being the dry-adiabatic lapse rate). Infinity where the air is not lifted dry-adiabatically
        // faster than its dew point falls (no such body; guarded).
        public static double LclMetresPerKelvin(double gravity, double cp, double gasConstant, double temperatureK, double dewPointK)
        {
            double gap = gravity / cp - DewPointLapseKPerM(gravity, gasConstant, temperatureK, dewPointK);
            return gap > 0.0 ? 1.0 / gap : double.PositiveInfinity;
        }

        // The relative humidity (0 to 1) at height z of surface air lifted dry-adiabatically (a well-mixed layer): temperature and dew point fall at their lapse rates; 1 at and above the
        // condensation level. The Magnus formula at both, no other constant.
        public static double MixedLayerHumidity(double gravity, double cp, double gasConstant, double temperatureK, double dewPointK, double heightM)
        {
            double t = temperatureK - gravity / cp * heightM;
            double td = dewPointK - DewPointLapseKPerM(gravity, gasConstant, temperatureK, dewPointK) * heightM;
            if (td >= t) return 1.0;
            double tc = t - ZeroCelsiusK, tdc = td - ZeroCelsiusK;
            return DMath.Exp(MagnusA * tdc / (MagnusB + tdc) - MagnusA * tc / (MagnusB + tc));
        }
    }

    // What the new sky reads per column (package B of the cloud master plan). Heights in metres above the surface and as shares of the body's troposphere (BodyParams.TropopauseM).
    public struct SkyColumn
    {
        // The convective base (lifting condensation level), the convective top, the stable layer.
        public double BaseM, TopM, StableM;
        public double BaseShare, TopShare, StableShare;
        // The share of the column's cloud that is convective (0 to 1) and the convective amount (the field's Coverage times that share: the amount stays the map's).
        public double ConvectiveFraction, ConvectiveAmount;
        // The relative humidity at the low, middle and high level of the troposphere (BodyParams.SkyLevelLow/Mid/HighShare), 0 to 1.
        public double RhLow, RhMid, RhHigh;
        // The front: +1 warm, -1 cold, 0 none; the signed distance (m, positive ahead); its amplitude 0 to 1; the quarter wavelength across it (m). Copied from the Weather.
        public double FrontSign, FrontDistanceM, FrontAmp01, FrontQuarterM;

        // The life stage of a cell's tower, 0 to 1: the square root of the cell's own life envelope sin^2(pi phase), i.e. sin(pi phase): the tower rises faster than the cell's strength,
        // stands highest at mid-life and sinks to nothing at death, so the top map has no jump at a cell's birth or death. (A top that stays up while the cell decays, the anvil,
        // would jump at the death; the anvil is the renderer's.)
        public static double CellStage(double envelope)
        {
            return envelope > 0.0 ? DMath.Sqrt(envelope) : 0.0;
        }

        // The height of the stable layer as a share of the troposphere. Mixed-layer equilibrium z_i = w_e / D with w_e proportional to the forcing over the inversion jump
        // (Bretherton, Atm S 547 lecture 16, eqs. 16.33, 16.38a, 16.39: "The inversion height is increased by more radiative cooling, weaker subsidence or a weaker inversion
        // jump"). nu = descent - min(1, uplift) is the net large-scale descent; D grows as 1 + SkyDescentGain nu, the jump as 1 + SkyInversionGain max(nu, 0) (descent warms the air above
        // the layer). Forced ascent deepens the layer like heating (no equilibrium where D <= 0), so the forcing is max(heating, min(1, uplift)); the share is continuous everywhere.
        public static double StableShareOf(BodyParams b, double heating01, double descent01, double uplift)
        {
            double up = uplift > 1.0 ? 1.0 : (uplift < 0.0 ? 0.0 : uplift);
            double nu = descent01 - up;
            double dn = (1.0 + b.SkyDescentGain * nu) * (1.0 + b.SkyInversionGain * (nu > 0.0 ? nu : 0.0));
            double forcing = heating01 > up ? heating01 : up;
            double num = b.SkyStableShare * forcing;
            double s = dn <= num ? 1.0 : num / dn;
            return DMath.Clamp(s, b.SkyStableShareMin, 1.0);
        }

        // The convective base: the lifting condensation level of the surface air (m above the surface), at most the tropopause.
        public static double ConvectiveBaseM(BodyParams b, double temperatureK, double dewPointK)
        {
            double spread = temperatureK - dewPointK;
            if (spread < 0.0) spread = 0.0;
            double zb = spread * MoistAir.LclMetresPerKelvin(b.GravityMs2, b.Cp, b.GasConstant, temperatureK, dewPointK);
            return zb < b.TropopauseM ? zb : b.TropopauseM;
        }

        // The top of a storm cell's tower as a share of the troposphere: from the base share toward 1 by the cell's energy share and life stage. A full-energy cell at mid-life
        // reaches the tropopause (WMO: cumulonimbus tops reach the high etage; the anvil spreads where rising stops).
        public static double CellTopShare(double baseShare, double energy01, double stage)
        {
            return baseShare + (1.0 - baseShare) * energy01 * stage;
        }

        // The column of a sample of the generator: a pure function of the Weather made by WeatherGenerator.Sample (at any altitude: only column values are read) and the body.
        public static void Derive(BodyParams b, ref Weather w, out SkyColumn s)
        {
            s = new SkyColumn();
            double h = b.TropopauseM;
            double t = w.SurfaceTempK, td = MoistAir.DewPointK(t, w.RelHumidity);
            double zb = ConvectiveBaseM(b, t, td);

            double si = StableShareOf(b, w.Heating01, w.Descent01, w.Uplift);
            double zi = si * h;

            // shallow convection: from the base to the stable layer, as deep as the instability carries it (a full cell's energy reaches the stable layer; WMO: the depth of cumulus is
            // the distance from its base to the stable layer)
            double x = b.CellEnergyFull > 0.0 ? DMath.Clamp(w.Instability / b.CellEnergyFull, 0.0, 1.0) : (w.Instability > 0.0 ? 1.0 : 0.0);
            double depth = zi > zb ? zi - zb : 0.0;
            double zs = zb + depth * x;
            // a storm cell: from the base toward the tropopause by its energy, life stage and footprint
            double zc = zb + (h - zb) * w.CellReach01;
            double top = zs > zc ? zs : zc;

            // the convective share of the cloud: shallow cumulus where there is depth (ramped over the smallest stable share, so the map has no edge) and no saturated deck (a
            // deck's steady rain has no surface-driven cumulus under it, stack rule S7); a storm cell by its reach (embedded storms stay, S12)
            double ramp = DMath.SmoothStep(0.0, b.SkyStableShareMin * h, depth);
            double shallow = x * ramp * (1.0 - w.LiftSat01);
            double frac = shallow > w.CellReach01 ? shallow : w.CellReach01;

            s.BaseM = zb; s.TopM = top; s.StableM = zi;
            s.BaseShare = h > 0.0 ? zb / h : 0.0; s.TopShare = h > 0.0 ? top / h : 0.0; s.StableShare = si;
            s.ConvectiveFraction = frac; s.ConvectiveAmount = w.Coverage * frac;
            s.FrontSign = w.FrontSign; s.FrontDistanceM = w.FrontDistanceM; s.FrontAmp01 = w.FrontAmp01; s.FrontQuarterM = w.FrontQuarterM;

            s.RhLow = LevelHumidity(b, ref w, b.SkyLevelLowShare, h, zb, zi, top, frac, t, td);
            s.RhMid = LevelHumidity(b, ref w, b.SkyLevelMidShare, h, zb, zi, top, frac, t, td);
            s.RhHigh = LevelHumidity(b, ref w, b.SkyLevelHighShare, h, zb, zi, top, frac, t, td);
        }

        // The relative humidity at a level (share of the troposphere). In the mixed layer (below the stable layer) the surface air lifted dry-adiabatically. Above it the free
        // troposphere: the moisture source's humidity, dried by the large-scale descent, raised toward saturation by the strongest of: the dynamic lift up to the stratiform rain onset,
        // the overrunning warm air above the frontal surface (on the cold side of the front the surface rises from the front to the troposphere's top over the quarter wavelength of the
        // front's own phase), and convection that reaches the level.
        private static double LevelHumidity(BodyParams b, ref Weather w, double share, double h, double zb, double zi, double top, double frac, double t, double td)
        {
            double z = share * h;
            if (z < zi) return MoistAir.MixedLayerHumidity(b.GravityMs2, b.Cp, b.GasConstant, t, td, z);
            double r = DMath.Clamp(w.RelHumidity * (1.0 - w.Descent01), 0.0, 1.0);
            double boost = w.LiftSat01;
            if (w.FrontAmp01 > 0.0 && w.FrontQuarterM > 0.0 && w.FrontSign * w.FrontDistanceM > 0.0)
            {
                double surface = DMath.Abs(w.FrontDistanceM) / w.FrontQuarterM;
                double front = w.FrontAmp01 * DMath.SmoothStep(0.0, b.SkyLevelLowShare, share - surface);
                if (front > boost) boost = front;
            }
            if (frac > boost && z >= zb && z <= top) boost = frac;
            double rh = r + (1.0 - r) * boost;
            return rh > 1.0 ? 1.0 : (rh < 0.0 ? 0.0 : rh);
        }
    }
}
