using System;

namespace ReDefinition.Landing
{
    // The constants of the game's heating, as Physics.cfg gives them: the ones the bound of the convective flux is worked from.
    internal struct HeatConstants
    {
        public double MachFactor;
        public double MachDensityExponent;
        public double MachVelocityExponent;
        public double TurbulentStart;
        public double TurbulentEnd;
        public double TurbulentMultiplier;
        public double LerpStartMach;
        public double LerpEndMach;
        public double LerpExponent;
        public double LowMachTemperatureFactor;
        public double HighMachTemperatureScalar;
        public double HighMachTemperatureExponent;
    }

    // An upper bound of the convective flux the game puts on a part's skin, in kW per m2, for air at a density and a speed. The hypersonic
    // coefficient is 1e-7 times the Mach factor times density and speed to the game's exponents, as a developer of the game gives it
    // (forum.kerbalspaceprogram.com, "How does KSP compute aerodynamic heating?"). It is taken times its turbulent multiplier where the flow is
    // turbulent, for the largest drag coefficient, and times the whole shock temperature, as for a cold skin. It is zero below the Mach range where
    // the game moves from its low-speed law to this one. Against 299 points the game reported in Kerbin's air at Mach 7 to 9
    // (tests/ReDefinition.Tests/Data/heat-flux-points.csv) it never came under the flux the game reported, which was 13 to 27 % of it.
    internal static class HeatBound
    {
        // The temperature of the shock at this speed, before the difficulty's heat and the body's own multiplier scale it.
        internal static double ShockTemperature(double speed, double mach, HeatConstants constants)
        {
            double slow = speed * constants.LowMachTemperatureFactor;
            double blend = Math.Pow(Math.Max(0.0, Math.Min(1.0, (mach - constants.LerpStartMach) / (constants.LerpEndMach - constants.LerpStartMach))), constants.LerpExponent);
            if (blend <= 0.0) return slow;
            double fast = constants.HighMachTemperatureScalar * Math.Pow(speed, constants.HighMachTemperatureExponent);
            return slow + (fast - slow) * blend;
        }

        // The bound, with the scale the difficulty and the body give the shock temperature.
        internal static double FluxKW(double density, double speed, double mach, double temperatureScale, HeatConstants constants)
        {
            if (mach < constants.LerpStartMach) return 0.0;
            double reynolds = density * speed;
            double turbulence = 1.0 + Math.Max(0.0, Math.Min(1.0, (reynolds - constants.TurbulentStart) / (constants.TurbulentEnd - constants.TurbulentStart)))
                                      * (constants.TurbulentMultiplier - 1.0);
            double coefficient = 1e-7 * constants.MachFactor * Math.Pow(density, constants.MachDensityExponent) * Math.Pow(speed, constants.MachVelocityExponent) * turbulence;
            return coefficient * ShockTemperature(speed, mach, constants) * temperatureScale / 1000.0;
        }
    }
}
