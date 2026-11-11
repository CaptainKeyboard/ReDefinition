using System;

namespace AtmoSim.Core
{
    // The inputs of the weather generator for one body. Everything the weather depends on comes from here, from the seed
    // and from the height/ocean grid; there are no per-body constants in the generator code.
    public sealed class BodyParams
    {
        public string Name = "Kerbin-like";
        public double RadiusM = 600000.0;
        // Sidereal rotation period (B09 dump of KSP's Kerbin: 21549.425 s; the solar day is 21600 s).
        public double RotationPeriodS = 21549.425;
        // The orbital year in seconds (SeasonClock.YearSeconds).
        public double YearS = 9203544.6;
        // KSP has no axial tilt; the season phase is artificial. The generator needs a tilt as an input of the insolation.
        public double ObliquityRad = 0.4084;
        public double SolarFluxWm2 = 1361.0;
        public double SurfacePressurePa = 101325.0;
        public double GravityMs2 = 9.81;
        public double GasConstant = 287.0;
        public double Cp = 1004.0;
        // Stand-in for the KSP temperature curves (B09 samples): annual mean surface temperature, lapse rate, tropopause height.
        public double SurfaceMeanK = 288.0;
        public double LapseKPerM = 0.0065;
        public double TropopauseM = 11000.0;
        // Day-night surface temperature swing of dry land under a clear sky at the equator.
        public double DiurnalLandAmpK = 6.0;
        // Measured sea-level zonal-mean temperature at |latitude| 0, 15, ..., 90 degrees (the B09 dump of KSP's temperature curves); null uses the insolation model.
        public double[] ProfileK;
        // Seasonal lag of the sea surface behind the sun, in days of the body's year fraction (0.07 = 7 % of the year).
        public double SeaSeasonLagYearFraction = 0.08;
        // Season phase of the moist belt and the jet behind the sub-solar latitude, as a fraction of the year.
        public double CirculationLagYearFraction = 0.05;

        // Ocean fraction target of the synthetic maps (the real grid of B09 replaces them).
        public double OceanFractionTarget = 0.7;
        // Ice model: freezing point of the body's liquid at zero salinity, its salinity shift per psu, the salinity of the sea.
        public double FreezeK0 = 273.15;
        public double FreezeShiftKPerPsu = -0.054;
        public double SalinityPsu = 35.0;
        // Mixed-layer depth of the sea (heat store of the ice model), metres.
        public double MixedLayerDepthM = 30.0;
        public double SeaHeatCapacityPerM3 = 4.186e6;
        // Dust and sand: lifted over dry land above the wind threshold on dusty bodies.
        public bool Dusty = false;
        public double DustWindThresholdMs = 9.0;
        // A body locked to its star: the sub-solar point stays fixed.
        public bool TidallyLocked = false;

        // Precipitation: the absolute bands below are in water-equivalent mm/h times this body factor (thin or dry atmospheres rain less).
        public double PrecipBodyFactor = 1.0;

        // Switches of the A/B runs.
        public bool WavesEnabled = true;
        public bool CellsEnabled = true;
        // Iteration 2 of the B10 proof (RESULT.md H6): slots in sectors of the parallel (a comb) instead of independent uniform longitudes. Off in the signed generator.
        public bool CombPlacement = false;
        // System generations: lifetime range in days of 86400 s (a generation of a slot lasts one period drawn once per slot).
        public double SystemLifeMinDays = 4.0; // 3.0 in the signed generator and in rounds 1 and 2 (Round2() restores it)
        public double SystemLifeMaxDays = 6.0;

        // Ocean fraction and terrain height on a regular grid; null means the synthetic maps of the seed.
        public HeightGrid Grid;

        public double Omega { get { return DMath.TwoPi / RotationPeriodS; } }
        public double DayS { get { return RotationPeriodS; } }
        public double FreezePointK { get { return FreezeK0 + FreezeShiftKPerPsu * SalinityPsu; } }

        // ---- levers of tuning round 1 (RESULT.md, TUNING ROUND 1). The values below are the round-1 values; Signed() restores the signed generator. ----
        // L1: factor on the seasonal anomaly of the insolation model (thermal inertia of the seasons). 1 = signed.
        public double SeasonalResponse = 0.40;
        // L2: ocean heat convergence (W/m2) that lifts the equilibrium of the ice-free mixed layer by flux / SeaExchangeWm2K. 0 = signed.
        public double SeaHeatFluxWm2 = 30.0;
        // L3: the snow window as a fraction of the body's year (0 = the signed 160 steps of 6 h).
        public double SnowWindowYearFraction = 40.0 / 365.25;
        // L4: share of the cloud and stratiform driver that does not depend on the temperature (0 = the signed H = h_T Hrel).
        public double LiftSaturationFloor = 0.20;
        public double StratThreshold = 0.08; // 0.075 in round 1 (Round1() restores it)
        public double CellRainThreshold = 0.02;
        // L4 (c): convergence lift of the moist belt (0 = signed) and the shift of the belt centre by a longitude wave (rad).
        public double ItczLift = 1.6; // 0.45 in round 1 (Round1() restores it)
        public double ItczWiggleRad = 0.06;
        // L5/L7: cells in the extratropics need a trigger (a front); the front term of the presence probability (0.8 = signed).
        public bool CellTrigger = true;
        public double CellFrontTerm = 0.35;
        // The trigger factor of the extratropics: SmoothStep(lo, hi, front strength).
        public double CellTriggerLo = 0.6, CellTriggerHi = 1.0;
        // L6: soft-saturation constant of the geostrophic wind as a multiple of the jet speed (the signed generator used max(15, 2 U_jet)); gust noise amplitude.
        public double WindSatJetFactor = 1.2;
        public double WindSatMinMs = 15.0;
        public double GustNoise = 0.25;
        // L10: systems are periodic around their parallel (images of the envelope, integer wavenumber); false = the signed generator, whose systems of small circumference have a seam at the antipode of the centre.
        public bool PeriodicSystems = true;
        // L8 (e): snow and the rain/snow type freeze at the fresh-water point (snow is fresh water); the signed generator used the freezing point of the sea (271.26 K at 35 psu).
        public bool SnowUsesFreshFreezingPoint = true;
        // L8 (f): the albedo of the melting ice follows the snow depth continuously (the signed step at 0.02 m made the ice jump when the snow crossed it).
        public bool SmoothSnowAlbedo = true;
        public double SnowFreezeK { get { return SnowUsesFreshFreezingPoint ? FreezeK0 : FreezePointK; } }

        // ---- levers of tuning round 2 (RESULT.md, TUNING ROUND 2). The values below are the round-2 values; Round1() restores the round-1 generator, Signed() the signed one. ----
        // R2a: every system generation has its own tilt, curl (comma head), wavelength, widths, latitude, speed and a longitude-dependent amplitude (storm-track zones); false = the round-1 comb.
        public bool Round2Systems = true;
        // Front tilt of a generation in units of the wave phase gradient (the cold front trails equatorward and westward), range; the curl of the poleward head, range (in units of the half-length ly).
        public double SystemTiltMin = -0.30, SystemTiltMax = 0.85;
        public double SystemCurlMax = 1.0;
        // Wavelength factor range (times 3 r) of the wave number distribution; the along-track envelope in wavelengths; the share of the generations that are isolated single lows.
        public double SystemLambdaMin = 0.7, SystemLambdaMax = 1.5;
        // Zonal modulation of the system amplitude (storm-track zones, share of the amplitude) and the per-generation speed factor spread.
        public double SystemZonalAmp = 0.25, SystemSpeedSpread = 0.22; // 0.18 in round 2 (Round2() restores it)
        // R2b: the moist belt meanders and breaks (longitude-dependent position, width and strength, land/sea shift), the subtropical dry zone meanders, trade-wind cumulus fills the dry zone; false = round 1.
        public bool Round2Itcz = true;
        public double ItczMeanderScale = 0.9, ItczBreakLo = 0.05, ItczBreakHi = 0.85;
        // Cover of the trade-wind cumulus field in the dry zone (share of cloud water).
        public double FairWeatherCloud = 0.25;
        // R2c: soft onset of the stratiform rain (softplus width, in units of the driver) and rain-cell structure inside the rain areas (amplitude, 0 = none); false = round 1.
        public bool Round2Rain = true;
        public double RainSoftness = 0.04, RainCellAmp = 0.40;
        // R2d: snow melt by positive degree-days of a temperature distribution (spread in K) with a radiation term (mm per K and day per unit of mean insolation); false = round 1.
        public bool Round2Snow = true;
        public double SnowTempSpreadK = 6.0, SnowMeltRadPerUnit = 8.0;

        // ---- levers of tuning round 3 (RESULT.md, TUNING ROUND 3). The values below are the round-3 values; Round2() restores the round-2 generator. ----
        // R3a: the orientation of the front of a generation is drawn as an ANGLE from the meridian (degrees, positive = the front trails equatorward and westward) over a wide range instead of
        // a tilt range of one sign, so neighbouring systems do not share a diagonal; the systems fade toward the pole from SystemPoleFadeLo to SystemPoleFadeHi (rad of latitude).
        public bool Round3Systems = true;
        public double SystemAngleMinDeg = -55.0, SystemAngleMaxDeg = 70.0;
        // The pressure amplitude of a system as a share of the surface pressure (0.025 in the signed generator and in rounds 1 and 2), and the lifetime of a generation (3 to 6 days before).
        public double SystemAmpFraction = 0.025;
        // The steering speed of a system as a multiple of the jet speed of its latitude (1 in rounds 1 and 2).
        public double SystemSpeedFactor = 1.0;
        // Half-range of the latitude scatter of a generation around the jet latitude, in units of its half-length (0.6 in round 2).
        public double SystemLatScatter = 0.4;
        // The amplitude window of a generation: 0 = sin^2 of the life phase (signed, rounds 1 and 2); above 0 a flat-topped smoothstep window rising over this share of the life.
        public double WindowRise = 0.0;
        public double SystemPoleFadeLo = 1.0, SystemPoleFadeHi = 1.35;
        // R3b: mature-stage structure of a system as a function of its life phase: a cloud spiral around the low (windings grow with the age) and a dry slot (a clear wedge between
        // the cold-front band and the cloud head). Amplitudes are shares of the available moisture; the spiral radius is SpiralRadiusFactor times the half-length of the system.
        public bool MatureStructure = true;
        public double SpiralAmp = 0.55, SpiralRadiusFactor = 1.25, SpiralTurnsMin = 0.35, SpiralTurnsMax = 1.25;
        public double DrySlotDepth = 0.90, DrySlotAngleDeg = 38.0;
        public double MaturityLo = 0.30, MaturityHi = 0.70;
        // R3c: clusters of the moist belt with a common envelope (a broad longitude pattern of wavenumbers 2 and 3) and softer cores; false = the round-2 patches.
        public bool Round3Itcz = true;
        public double ItczClusterLo = -0.45, ItczClusterHi = 1.05, ItczEnvelopeLo = -0.55, ItczEnvelopeHi = 0.45, ItczOffClusterFloor = 0.12;
        // R3d: the radiation heating of the snow surface above the air temperature (K per unit of mean insolation, an enhanced degree-day model); 0 = round 2.
        public double SnowRadiativeHeatK = 6.0;
        // R3e: the snow on sea ice enters the ice integration with a memory (days): the snow of the evaluation time counts less for the steps long before it, so a quick change of the snow
        // does not move the whole integrated history; 0 = round 2 (the same snow depth for every step).
        public double IceSnowMemoryDays = 8.0;

        // The round-2 generator: every lever of round 3 switched off (20 000-query hash D01F43163C7A1646).
        public BodyParams Round2()
        {
            var p = Clone();
            p.Round3Systems = false; p.MatureStructure = false; p.Round3Itcz = false; p.SnowRadiativeHeatK = 0.0; p.IceSnowMemoryDays = 0.0;
            p.SystemSpeedSpread = 0.18; p.SystemLifeMinDays = 3.0; p.SystemLatScatter = 0.6; p.SystemAmpFraction = 0.025; p.SystemSpeedFactor = 1.0; p.WindowRise = 0.0;
            return p;
        }

        // The round-1 generator: every lever of round 2 switched off (20 000-query hash 3A3A34F462C55CAC).
        public BodyParams Round1()
        {
            var p = Round2();
            p.Round2Systems = false; p.Round2Itcz = false; p.Round2Rain = false; p.Round2Snow = false; p.ItczLift = 0.45; p.StratThreshold = 0.075;
            return p;
        }

        // The generator exactly as signed (iteration 1): every lever of round 1 at its signed value.
        public BodyParams Signed()
        {
            var p = Round2();
            p.Round2Systems = false; p.Round2Itcz = false; p.Round2Rain = false; p.Round2Snow = false;
            p.SeasonalResponse = 1.0; p.SeaHeatFluxWm2 = 0.0; p.MixedLayerDepthM = 12.0; p.SnowWindowYearFraction = 0.0; p.LiftSaturationFloor = 0.0;
            p.StratThreshold = 0.12; p.CellRainThreshold = 0.15; p.ItczLift = 0.0; p.ItczWiggleRad = 0.0; p.CellTrigger = false; p.CellFrontTerm = 0.8;
            p.WindSatJetFactor = 2.0; p.WindSatMinMs = 15.0; p.GustNoise = 0.0; p.SnowUsesFreshFreezingPoint = false; p.PeriodicSystems = false; p.SmoothSnowAlbedo = false;
            return p;
        }

        public BodyParams Clone() { return (BodyParams)MemberwiseClone(); }

        public static BodyParams KerbinLike() { return new BodyParams(); }

        // Dry, thin, cold: no oceans, dusty.
        public static BodyParams DunaLike()
        {
            return new BodyParams
            {
                Name = "Duna-like",
                RadiusM = 320000.0, RotationPeriodS = 65517.86, YearS = 17315400.14, ObliquityRad = 0.44,
                SolarFluxWm2 = 590.0, SurfacePressurePa = 6000.0, GravityMs2 = 2.94, SurfaceMeanK = 215.0, LapseKPerM = 0.0045,
                TropopauseM = 20000.0, OceanFractionTarget = 0.0, Dusty = true, DustWindThresholdMs = 10.0,
                DiurnalLandAmpK = 25.0, PrecipBodyFactor = 0.15
            };
        }

        // Thick and hot.
        public static BodyParams EveLike()
        {
            return new BodyParams
            {
                Name = "Eve-like",
                RadiusM = 700000.0, RotationPeriodS = 80500.0, YearS = 5657995.15, ObliquityRad = 0.05,
                SolarFluxWm2 = 2900.0, SurfacePressurePa = 506625.0, GravityMs2 = 16.7, SurfaceMeanK = 340.0, LapseKPerM = 0.006,
                TropopauseM = 14000.0, OceanFractionTarget = 0.7, DiurnalLandAmpK = 2.0, PrecipBodyFactor = 2.0
            };
        }

        // An ocean world.
        public static BodyParams LaytheLike()
        {
            return new BodyParams
            {
                Name = "Laythe-like",
                RadiusM = 500000.0, RotationPeriodS = 52980.88, YearS = 104661432.1, ObliquityRad = 0.1,
                SurfacePressurePa = 60795.0, GravityMs2 = 7.85, SurfaceMeanK = 290.0, LapseKPerM = 0.0055,
                TropopauseM = 9000.0, OceanFractionTarget = 0.95, DiurnalLandAmpK = 4.0, PrecipBodyFactor = 0.8
            };
        }

        // A slow rotator (rotation period ten days): a day-night circulation instead of zonal bands.
        public static BodyParams SlowRotator()
        {
            var p = KerbinLike();
            p.Name = "Slow-rotator";
            p.RotationPeriodS = 864000.0;
            p.ObliquityRad = 0.05;
            p.DiurnalLandAmpK = 20.0;
            p.OceanFractionTarget = 0.3;
            return p;
        }

        // A body without oceans (the ocean-off control of the Kerbin-like set).
        public static BodyParams NoOcean()
        {
            var p = KerbinLike();
            p.Name = "No-ocean";
            p.OceanFractionTarget = 0.0;
            return p;
        }

        // A cold body: sea ice stays all year.
        public static BodyParams ColdWorld()
        {
            var p = KerbinLike();
            p.Name = "Cold-world";
            p.SurfaceMeanK = 258.0;
            return p;
        }

        // A tidally locked body.
        public static BodyParams TidallyLockedWorld()
        {
            var p = KerbinLike();
            p.Name = "Tidally-locked";
            p.RotationPeriodS = p.YearS;
            p.TidallyLocked = true;
            p.ObliquityRad = 0.0;
            p.OceanFractionTarget = 0.5;
            return p;
        }

        // An Earth-like body with the real Earth's size and rotation (scale sanity check, not part of the gate).
        public static BodyParams EarthLike()
        {
            var p = KerbinLike();
            p.Name = "Earth-like";
            p.RadiusM = 6371000.0;
            p.RotationPeriodS = 86164.0;
            p.YearS = 31556926.0;
            return p;
        }
    }
}
