namespace AtmoSim.Core
{
    // Moist-air relations of the new sky. The Magnus pair is the condensing substance's (BodyParams.CondensateMagnusA/B/ZeroK; water by default); the renderer's height model uses the
    // water pair below.
    public static class MoistAir
    {
        // The Magnus constants for the saturation vapour pressure over water as the WMO CIMO Guide (2008) gives them, ew = 6.112 exp(17.62 t / (243.12 + t)); a dimensionless and b in C.
        public const double MagnusA = 17.62, MagnusB = 243.12;
        public const double ZeroCelsiusK = 273.15;

        // a t / (b + t), t counted from zeroK: ln(e_s) up to a constant.
        public static double LnSaturation(double a, double b, double zeroK, double temperatureK)
        {
            double t = temperatureK - zeroK;
            return a * t / (b + t);
        }

        // The dew point in kelvin of air at a temperature and a relative humidity from 0 to 1 (clamped to 0.01 to 1, where the formula holds). DMath, so the result is the same on every platform.
        public static double DewPointK(double a, double b, double zeroK, double temperatureK, double humidity01)
        {
            double rh = DMath.Clamp(humidity01, 0.01, 1.0);
            double gamma = DMath.Log(rh) + LnSaturation(a, b, zeroK, temperatureK);
            return b * gamma / (a - gamma) + zeroK;
        }

        // Water.
        public static double DewPointK(double temperatureK, double humidity01)
        {
            return DewPointK(MagnusA, MagnusB, ZeroCelsiusK, temperatureK, humidity01);
        }

        // d ln(e_s) / dT of the Magnus formula at a temperature (1/K): a b / (b + t)^2.
        public static double MagnusSlope(double a, double b, double zeroK, double temperatureK)
        {
            double c = b + (temperatureK - zeroK);
            return a * b / (c * c);
        }

        // The fall of the dew point with height of air lifted without condensing (K/m). The mixing ratio is kept, so the vapour pressure falls with the pressure,
        // d ln e / dz = -g / (Rd T) (hydrostatic); at the dew point d ln e = (d ln e_s / dT) dTd, so dTd/dz = -g / (Rd T) / (d ln e_s / dT at Td). Derived [D]; with the dry-adiabatic
        // lapse g / cp it gives the lifting condensation level below.
        public static double DewPointLapseKPerM(BodyParams p, double temperatureK, double dewPointK)
        {
            return p.GravityMs2 / (p.GasConstant * temperatureK) / MagnusSlope(p.CondensateMagnusA, p.CondensateMagnusB, p.CondensateZeroK, dewPointK);
        }

        // The metres of lifting condensation level per kelvin of dew-point spread: 1 / (g / cp - dew-point lapse). For water with g 9.81, cp 1004, Rd 287 at 10 C it is 125.1 m/K,
        // the coefficient of Lawrence (2005, BAMS 86, eq. 22: z_LCL ~ 125 (t - td), the principle being the dry-adiabatic lapse rate). Infinity where the air is not lifted
        // dry-adiabatically faster than its dew point falls (no such body; guarded).
        public static double LclMetresPerKelvin(BodyParams p, double temperatureK, double dewPointK)
        {
            double gap = p.GravityMs2 / p.Cp - DewPointLapseKPerM(p, temperatureK, dewPointK);
            return gap > 0.0 ? 1.0 / gap : double.PositiveInfinity;
        }

        // The relative humidity (0 to 1) at height z of surface air lifted dry-adiabatically (a well-mixed layer): temperature and dew point fall at their lapse rates; 1 at and above the
        // condensation level. The Magnus formula at both, no other constant.
        public static double MixedLayerHumidity(BodyParams p, double temperatureK, double dewPointK, double heightM)
        {
            double t = temperatureK - p.GravityMs2 / p.Cp * heightM;
            double td = dewPointK - DewPointLapseKPerM(p, temperatureK, dewPointK) * heightM;
            if (td >= t) return 1.0;
            double a = p.CondensateMagnusA, b = p.CondensateMagnusB, z = p.CondensateZeroK;
            return DMath.Exp(LnSaturation(a, b, z, td) - LnSaturation(a, b, z, t));
        }
    }

    // What the new sky reads per column (package B of the cloud master plan). Heights in metres above the surface and as shares of the body's troposphere (BodyParams.TropopauseM).
    // Made from a Weather of WeatherGenerator.Sample(..., sky: true).
    public struct SkyColumn
    {
        // The convective base (lifting condensation level of the surface air), the convective top, the top of the mixed layer (the stable layer).
        public double BaseM, TopM, StableM;
        public double BaseShare, TopShare, StableShare;
        // The relative humidity of the surface air (the mixed layer's, SurfaceHumidity) that sets the base, and its dew point (K).
        public double SurfaceHumidity, SurfaceDewPointK;
        // Net large-scale ascent 0 to 1 (uplift beyond the descent): where it is above 0 the mixed layer has no equilibrium cap; deep convection is the cells' and the deck the
        // dynamic cloud's, so StableShare keeps its value at no net motion there (StableShareOf).
        public double Ascent01;
        // The sky shares, as seen, of the column's cloud (the field's Coverage = 1 - exp(-3 CloudWater)), split by the parts of its cloud water: CumuliformAmount the cumuliform family (the
        // convective water: cells and trade-wind cumulus, plus the humidity background where it is shallow cumulus), StratiformAmount the rest (the deck from the dynamic lift and fronts,
        // DeckAmount, plus the background where it is no cumulus). CumuliformAmount + StratiformAmount = Coverage <= 1; each part is the coverage times its share of the cloud water.
        public double CumuliformAmount, StratiformAmount, DeckAmount;
        // The share of the humidity background that is shallow cumulus (the instability, where convection has depth below the stable layer and no deck saturates the column).
        public double ShallowShare;
        // The relative humidity at the low, middle and high level of the troposphere (BodyParams.SkyLevelLow/Mid/HighShare), 0 to 1.
        public double RhLow, RhMid, RhHigh;
        // The fronts, copied from the Weather (continuous).
        public double WarmFront01, ColdFront01, FrontAmp01, FrontSurfaceShare;

        // The life stage of a cell's tower, 0 to 1: the square root of the cell's own life envelope sin^2(pi phase), i.e. sin(pi phase): the tower rises faster than the cell's strength,
        // stands highest at mid-life and sinks to nothing at death, so the top map has no jump at a cell's birth or death. (A top that stays up while the cell decays, the anvil,
        // would jump at the death; the anvil is the renderer's.)
        public static double CellStage(double envelope)
        {
            return envelope > 0.0 ? DMath.Sqrt(envelope) : 0.0;
        }

        // The top of a storm cell's tower as a share of the troposphere: from the base share toward 1 by the cell's energy share and life stage. A full-energy cell at mid-life
        // reaches the tropopause (WMO: cumulonimbus tops reach the high etage; the anvil spreads where rising stops).
        public static double CellTopShare(double baseShare, double energy01, double stage)
        {
            return baseShare + (1.0 - baseShare) * energy01 * stage;
        }

        // The height of the stable layer as a share of the troposphere. Mixed-layer equilibrium z_i = w_e / D with w_e proportional to the forcing over the inversion jump
        // (Bretherton, Atm S 547 lecture 16, eqs. 16.33, 16.38a, 16.39: "The inversion height is increased by more radiative cooling, weaker subsidence or a weaker inversion
        // jump"). nu = descent - min(1, uplift) is the net large-scale descent; D grows as 1 + SkyDescentGain nu, the jump as 1 + SkyInversionGain nu (descent warms the air above the
        // layer). Where nu <= 0 there is no subsidence to balance the entrainment: that regime has no equilibrium, and the column's depth is the deck's and the cells', not the mixed
        // layer's, so the share stays at its value for nu = 0 (continuous at the regime edge).
        public static double StableShareOf(BodyParams b, double heating01, double descent01, double uplift)
        {
            double up = uplift > 1.0 ? 1.0 : (uplift < 0.0 ? 0.0 : uplift);
            double nu = descent01 - up;
            if (nu < 0.0) nu = 0.0;
            double dn = (1.0 + b.SkyDescentGain * nu) * (1.0 + b.SkyInversionGain * nu);
            double s = dn > 0.0 ? b.SkyStableShare * heating01 / dn : 1.0;
            return DMath.Clamp(s, b.SkyStableShareMin, 1.0);
        }

        // The relative humidity of the surface air. The mixed layer's water is (1 - lambda) of air last in contact with the surface plus lambda of entrained free-tropospheric air
        // (Bretherton lecture 16, eq. 16.38b: q_M = lambda q+ + (1 - lambda) q0, lambda = BodyParams.SkyEntrainmentShare). The surface air carries the surface's supply of water
        // (saturation over sea, MoistureSource of it over land); the entrained air has the free troposphere's relative humidity at the layer top, brought to the surface with its
        // mixing ratio: its share of the surface saturation is e_s(T_top) / e_s(T_s) x p_s / p_top (the environment's lapse rate, hydrostatic). So the surface air over the sea is
        // below saturation by the entrained dry air, and the base is above the ground unless the free troposphere is saturated too.
        public static double SurfaceHumidityOf(BodyParams b, double temperatureK, double moistureSource, double freeHumidity, double stableM)
        {
            double lambda = DMath.Clamp(b.SkyEntrainmentShare, 0.0, 1.0);
            double tTop = temperatureK - b.LapseKPerM * stableM;
            double a = b.CondensateMagnusA, c = b.CondensateMagnusB, z = b.CondensateZeroK;
            double ratio = DMath.Exp(MoistAir.LnSaturation(a, c, z, tTop) - MoistAir.LnSaturation(a, c, z, temperatureK)
                + b.GravityMs2 * stableM / (b.GasConstant * 0.5 * (temperatureK + tTop)));
            double source = DMath.Clamp(moistureSource, 0.0, 1.0);
            return DMath.Clamp((1.0 - lambda) * source + lambda * freeHumidity * ratio, 0.0, 1.0);
        }

        // The convective base: the lifting condensation level of the surface air (m above the surface), at most the tropopause.
        public static double ConvectiveBaseM(BodyParams b, double temperatureK, double dewPointK)
        {
            double spread = temperatureK - dewPointK;
            if (spread < 0.0) spread = 0.0;
            double zb = spread * MoistAir.LclMetresPerKelvin(b, temperatureK, dewPointK);
            return zb < b.TropopauseM ? zb : b.TropopauseM;
        }

        // The column of a sample of the generator: a pure function of the Weather made by WeatherGenerator.Sample(..., sky: true) (at any altitude: only column values are read) and the
        // body.
        public static void Derive(BodyParams b, ref Weather w, out SkyColumn s)
        {
            s = new SkyColumn();
            double h = b.TropopauseM;
            double up = w.Uplift > 1.0 ? 1.0 : (w.Uplift < 0.0 ? 0.0 : w.Uplift);
            double si = StableShareOf(b, w.Heating01, w.Descent01, w.Uplift);
            double zi = si * h;
            double free = FreeHumidity(ref w);
            double t = w.SurfaceTempK;
            double rhS = SurfaceHumidityOf(b, t, w.MoistureSource, free, zi);
            double td = MoistAir.DewPointK(b.CondensateMagnusA, b.CondensateMagnusB, b.CondensateZeroK, t, rhS);
            double zb = ConvectiveBaseM(b, t, td);

            // shallow convection: from the base toward the stable layer, as deep as the instability carries it; the instability index runs 0 to 1 by its definition (the generator's
            // inst), and only a column at its top end carries the cumulus to the stable layer (WMO: the depth of cumulus is the distance from its base to the stable layer)
            double x = DMath.Clamp(w.Instability, 0.0, 1.0);
            double depth = zi > zb ? zi - zb : 0.0;
            double zs = zb + depth * x;
            // a storm cell: from the base toward the tropopause by its energy, life stage and footprint
            double zc = zb + (h - zb) * w.CellReach01;
            double top = zs > zc ? zs : zc;

            // which part of the humidity background is shallow cumulus: the instability where the layer has depth below its cap (ramped over the inversion's width), on the part of
            // the ground the deck leaves in the sun (the deck's own cover by the field's cover law 1 - exp(-3 water); under a deck no surface-driven cumulus, stack rule S7)
            double bg = w.CloudBackground > 0.0 ? w.CloudBackground : 0.0, dyn = w.CloudDynamic > 0.0 ? w.CloudDynamic : 0.0, conv = w.CloudConvective > 0.0 ? w.CloudConvective : 0.0;
            double ramp = DMath.SmoothStep(0.0, b.SkyInversionWidth * zi, depth);
            double shallow = x * ramp * DMath.Exp(-3.0 * dyn);
            double cumuliformW = conv + bg * shallow, total = cumuliformW + dyn + bg * (1.0 - shallow);
            double cover = DMath.Clamp(w.Coverage, 0.0, 1.0);
            double cumuliform = total > 0.0 ? cover * cumuliformW / total : 0.0;

            s.BaseM = zb; s.TopM = top; s.StableM = zi;
            s.BaseShare = h > 0.0 ? zb / h : 0.0; s.TopShare = h > 0.0 ? top / h : 0.0; s.StableShare = si;
            s.SurfaceHumidity = rhS; s.SurfaceDewPointK = td;
            s.Ascent01 = up > w.Descent01 ? up - w.Descent01 : 0.0;
            s.CumuliformAmount = cumuliform; s.StratiformAmount = cover - cumuliform; s.DeckAmount = total > 0.0 ? cover * dyn / total : 0.0;
            s.ShallowShare = shallow;
            s.WarmFront01 = w.WarmFront01; s.ColdFront01 = w.ColdFront01; s.FrontAmp01 = w.FrontAmp01; s.FrontSurfaceShare = w.FrontSurfaceShare;

            s.RhLow = HumidityAt(b, ref w, ref s, b.SkyLevelLowShare);
            s.RhMid = HumidityAt(b, ref w, ref s, b.SkyLevelMidShare);
            s.RhHigh = HumidityAt(b, ref w, ref s, b.SkyLevelHighShare);
        }

        // The free troposphere's relative humidity before lift: the moisture index dried by the large-scale descent, 0 to 1.
        private static double FreeHumidity(ref Weather w)
        {
            return DMath.Clamp(w.MoistureIndex * (1.0 - w.Descent01), 0.0, 1.0);
        }

        // The relative humidity at a level (share of the troposphere) of a derived column. The mixed layer (surface air lifted dry-adiabatically) blends into the free troposphere over the
        // inversion's width around the stable layer. The free troposphere: its humidity raised toward saturation by the strongest of the dynamic lift (up to the stratiform rain onset),
        // the front (the overrunning warm air above the frontal surface, near a front), and the cumuliform clouds between base and top (their sky share). Every edge is a smooth step whose
        // width scales with its layer's depth, so the profile is continuous in height and in time.
        public static double HumidityAt(BodyParams b, ref Weather w, ref SkyColumn s, double share)
        {
            double h = b.TropopauseM, z = share * h, zi = s.StableM;
            double wi = b.SkyInversionWidth * zi;
            double mixed = 1.0 - DMath.SmoothStep(zi - wi, zi + wi, z);
            double rhMixed = mixed > 0.0 ? MoistAir.MixedLayerHumidity(b, w.SurfaceTempK, s.SurfaceDewPointK, z) : 0.0;
            double r = FreeHumidity(ref w);
            double boost = w.LiftSat01;
            double near = w.WarmFront01 > w.ColdFront01 ? w.WarmFront01 : w.ColdFront01;
            double front = near * DMath.SmoothStep(0.0, b.SkyFrontSurfaceWidth, share - w.FrontSurfaceShare);
            if (front > boost) boost = front;
            double wc = b.SkyInversionWidth * (s.TopM - s.BaseM > zi ? s.TopM - s.BaseM : zi);
            double gate = DMath.SmoothStep(s.BaseM - wc, s.BaseM + wc, z) * (1.0 - DMath.SmoothStep(s.TopM - wc, s.TopM + wc, z));
            double conv = s.CumuliformAmount * gate;
            if (conv > boost) boost = conv;
            double rhFree = r + (1.0 - r) * boost;
            double rh = mixed * rhMixed + (1.0 - mixed) * rhFree;
            return rh > 1.0 ? 1.0 : (rh < 0.0 ? 0.0 : rh);
        }
    }
}
