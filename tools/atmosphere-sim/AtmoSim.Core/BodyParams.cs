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
        public double MixedLayerDepthM = 12.0;
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
        // System generations: lifetime range in days of 86400 s (a generation of a slot lasts one period drawn once per slot).
        public double SystemLifeMinDays = 3.0;
        public double SystemLifeMaxDays = 6.0;

        // Ocean fraction and terrain height on a regular grid; null means the synthetic maps of the seed.
        public HeightGrid Grid;

        public double Omega { get { return DMath.TwoPi / RotationPeriodS; } }
        public double DayS { get { return RotationPeriodS; } }
        public double FreezePointK { get { return FreezeK0 + FreezeShiftKPerPsu * SalinityPsu; } }

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
