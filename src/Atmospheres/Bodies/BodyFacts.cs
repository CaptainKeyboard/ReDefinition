using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // What the game says about one body, as plain data: the input of the parser and of the envelope check. BodyProbe fills it on the
    // main thread; tests build it by hand, so that the rules run without the game. Members that could not be read keep their
    // "unknown" value (0, NaN, null).
    internal sealed class BodyFacts
    {
        internal string Name = "";
        internal int Index;
        internal double RadiusM;
        internal double DaySeconds;
        internal bool IsStar;
        // The atmosphere: present, its depth in metres, the pressure at the surface in kPa and the mean temperature there in kelvin.
        internal bool HasAtmosphere;
        internal double AtmosphereDepthM;
        internal double SeaLevelPressureKPa;
        internal double SeaLevelTemperatureK = double.NaN;
        // The surface gravity in m/s^2, the stellar flux at the body in W/m^2 and the gas constant and heat capacity of the air in J/(kg K);
        // NaN where not read.
        internal double SurfaceGravityMs2 = double.NaN;
        internal double SolarFluxWm2 = double.NaN;
        internal double GasConstant = double.NaN;
        internal double Cp = double.NaN;
        // A solid surface (PQS) or an ocean: where neither exists nothing can lie on the ground.
        internal bool HasSurface;
        internal bool HasOcean;
        // The period of the body's own orbit in seconds, 0 where it has none.
        internal double OrbitPeriod;
        // A moon: its parent is not the star. ParentOrbitPeriod is the period of the planet whose parent is the star (0 where unknown).
        internal bool IsMoon;
        internal double ParentOrbitPeriod;
        // The period of the player's home planet, the unit of the year cap; 0 where the game has none.
        internal double HomeOrbitPeriod;
        internal bool TidallyLocked;
        // The angle in degrees between the body's rotation axis and the normal of the reference plane (an add-on that tilts a body
        // changes it), and between the axis and the normal of the planet's orbit (the obliquity). NaN where not read.
        internal double AxisFromReferenceDeg = double.NaN;
        internal double ObliquityDeg = double.NaN;
        // The air temperature in kelvin over altitude in metres, sampled from the game; null where it could not be sampled.
        internal Curve TemperatureTarget;
        // The names of EVE's particle fields that belong to the body (for example the field of a snow layer).
        internal List<string> EveFields = new List<string>();
    }
}
