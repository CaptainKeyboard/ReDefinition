using System;

namespace ReDefinition.Autopilot
{
    // The limits MechJeb's thrust controller puts on the throttle in the air (MechJebModuleThrustController: MaximumDynamicPressureThrottle and
    // TemperatureSafetyThrottle, GPL-3.0). Here they are told to the player in the navigation instead of applied. A climb over the air's
    // pressure limit overheats the nose, and a part near its temperature limit asks for little thrust. The numbers are MechJeb's.
    internal static class Heat
    {
        // The dynamic pressure a climb is held under, in pascals: MechJeb's default for its limit.
        internal const double MaxDynamicPressure = 20000.0;
        // How fast the throttle falls as the pressure goes over the limit, and how near its limit a part may come before the throttle falls.
        private const double Falloff = 15.0;
        internal const double TemperatureMargin = 0.05;

        // The throttle that holds the dynamic pressure to the limit: full under it, falling quickly over it.
        internal static double PressureThrottle(double pressure)
        {
            double ratio = pressure / MaxDynamicPressure;
            if (ratio < 1.0) return 1.0;
            return Math.Max(0.0, Math.Min(1.0, 1.0 - Falloff * (ratio - 1.0)));
        }

        // The throttle that keeps the hottest part from its limit: full until it is within a twentieth of it, then falling to none at the limit.
        internal static double TemperatureThrottle(double hottestShare)
        {
            if (hottestShare < 1.0 - TemperatureMargin) return 1.0;
            return Math.Max(0.0, (1.0 - hottestShare) / TemperatureMargin);
        }

        private static readonly double ProbeScale = ReDefinition.Core.CommandLine.Float("-redefinitionProbeHeatShareScale", 1f);

        // The hottest part's temperature against its limit; the part's name comes with it. Zero for a vessel with no part that has a limit.
        internal static double Hottest(Vessel vessel, out string name)
        {
            // A probe's switch makes the parts count as hotter, to see the live guard act where the plan allowed the pass; 1 otherwise.
            double scale = ProbeScale;
            double worst = 0.0;
            name = "";
            foreach (Part part in vessel.parts)
            {
                // A part burns where its inside or its skin goes over its limit.
                double share = part.maxTemp > 0.0 ? part.temperature / part.maxTemp : 0.0;
                if (part.skinMaxTemp > 0.0) share = Math.Max(share, part.skinTemperature / part.skinMaxTemp);
                share *= scale;
                if (share <= worst) continue;
                worst = share;
                name = part.partInfo != null ? part.partInfo.title : part.name;
            }
            return worst;
        }

        // The throttle both limits allow, the lower of the two.
        internal static double Allowed(Vessel vessel, out string reason)
        {
            string part;
            double temperature = TemperatureThrottle(Hottest(vessel, out part));
            double pressure = PressureThrottle(vessel.dynamicPressurekPa * 1000.0);
            reason = "";
            if (temperature < 1.0 && temperature <= pressure) reason = part + " is near its temperature limit";
            else if (pressure < 1.0) reason = "the air's pressure is over the limit";
            return Math.Min(temperature, pressure);
        }
    }
}
