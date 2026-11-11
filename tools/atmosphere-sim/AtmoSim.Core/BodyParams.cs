using System;

namespace AtmoSim.Core
{
    // The inputs of the simulation for one body. Everything the weather depends on comes from here and from the seed;
    // there are no storm positions and no per-body constants in the step.
    public sealed class BodyParams
    {
        public string Name = "Kerbin-like";
        public double RadiusM = 600000.0;
        public double RotationPeriodS = 21600.0;
        // The orbital year in seconds (SeasonClock.YearSeconds: orbit period times the year-length factor).
        public double YearS = 9203545.0;
        // KSP has no axial tilt; the season phase is artificial. The simulation needs a tilt as an input of insolation.
        public double ObliquityRad = 0.4084;
        public double Eccentricity = 0.0;
        public double PerihelionPhase = 0.0;
        public double SolarFluxWm2 = 1361.0;

        public double SurfacePressurePa = 101325.0;
        public double GravityMs2 = 9.81;
        public double GasConstant = 287.0;
        public double Cp = 1004.0;
        // Stand-in for the target radiative-equilibrium profile (B09 temperature samples): mean surface temperature, lapse rate, tropopause.
        public double SurfaceMeanK = 288.0;
        public double LapseKPerM = 0.0065;
        public double TropopauseM = 11000.0;
        // The share of the global-mean insolation that heat transport spreads evenly (derived from pressure when negative).
        public double HeatTransportFraction = -1.0;
        // Day-night surface temperature swing of dry land under a clear sky at the equator.
        public double DiurnalLandAmpK = 6.0;

        // The condensible (water by default).
        public double Rv = 461.5;
        public double LatentVapour = 2.5e6;
        public double LatentSublimation = 2.83e6;
        public double TripleK = 273.16;
        public double TripleVapourPa = 611.2;

        // Sea: heat capacity of the mixed layer and the freezing point are forcing parameters.
        public double SeaMixedDepthM = 40.0;
        public double SeaHeatCapacityPerM = 4.186e6;
        public double SeaFreezeK = 271.35;
        public double IceLatentDensity = 3.06e8;
        public double SoilCapacityMm = 150.0;

        // Dust: lifted over land when the 10 m wind exceeds the threshold (dry bodies only).
        public bool Dusty = false;
        public double DustWindThresholdMs = 9.0;

        // Scales the baroclinic deformation radius (1 = the physical value). A gameplay scale of the weather systems.
        public double EddyScale = 1.0;

        // Ocean fraction and terrain height on the physics grid (row 0 north); null means the synthetic maps of the seed.
        public double[] OceanFraction;
        public double[] TerrainM;
        public double OceanFractionTarget = 0.7;

        public double Omega { get { return DMath.TwoPi / RotationPeriodS; } }
        public double MidLatCoriolis { get { return DMath.Max(2.0 * Omega * 0.70710678118654752, 1e-7); } }

        public BodyParams Clone() { return (BodyParams)MemberwiseClone(); }

        public static BodyParams KerbinLike()
        {
            return new BodyParams();
        }

        // Dry, thin, cold: no oceans, a lot of dust.
        public static BodyParams DunaLike()
        {
            var p = new BodyParams
            {
                Name = "Duna-like",
                RadiusM = 320000.0, RotationPeriodS = 65517.0, YearS = 17315400.0, ObliquityRad = 0.44, Eccentricity = 0.05,
                SolarFluxWm2 = 590.0, SurfacePressurePa = 6000.0, GravityMs2 = 2.94, SurfaceMeanK = 215.0, LapseKPerM = 0.0045,
                TropopauseM = 20000.0, OceanFractionTarget = 0.0, Dusty = true, DustWindThresholdMs = 10.0, DiurnalLandAmpK = 25.0
            };
            return p;
        }

        // Thick and hot.
        public static BodyParams EveLike()
        {
            var p = new BodyParams
            {
                Name = "Eve-like",
                RadiusM = 700000.0, RotationPeriodS = 80500.0, YearS = 5657995.0, ObliquityRad = 0.05, SolarFluxWm2 = 2900.0,
                SurfacePressurePa = 506625.0, GravityMs2 = 16.7, SurfaceMeanK = 340.0, LapseKPerM = 0.006, TropopauseM = 14000.0,
                OceanFractionTarget = 0.7, DiurnalLandAmpK = 2.0
            };
            return p;
        }

        // An ocean world.
        public static BodyParams LaytheLike()
        {
            var p = new BodyParams
            {
                Name = "Laythe-like",
                RadiusM = 500000.0, RotationPeriodS = 52980.0, YearS = 9203545.0, ObliquityRad = 0.1, SolarFluxWm2 = 1361.0,
                SurfacePressurePa = 60795.0, GravityMs2 = 7.85, SurfaceMeanK = 290.0, LapseKPerM = 0.0055, TropopauseM = 9000.0,
                OceanFractionTarget = 0.95, DiurnalLandAmpK = 4.0
            };
            return p;
        }

        // A slow rotator (rotation period of ten days): a day-side/night-side circulation expected instead of zonal bands.
        public static BodyParams SlowRotator()
        {
            var p = KerbinLike();
            p.Name = "Slow-rotator";
            p.RotationPeriodS = 864000.0;
            p.ObliquityRad = 0.05;
            p.DiurnalLandAmpK = 10.0;
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

        // An Earth-like body with the real Earth's rotation, for the ten-year runs and a sanity check of the scales.
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
