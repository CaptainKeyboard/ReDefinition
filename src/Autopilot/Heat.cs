using System;

namespace ReDefinition.Autopilot
{
    // The limits MechJeb's thrust controller puts on the throttle in the air (MechJebModuleThrustController: MaximumDynamicPressureThrottle and
    // TemperatureSafetyThrottle, GPL-3.0). Here they are told to the player in the autonavigation instead of applied. A climb over the air's
    // pressure limit overheats the nose, and a part near its temperature limit asks for little thrust. The numbers are MechJeb's.
    internal static class Heat
    {
        // The dynamic pressure a climb is held under, in pascals: MechJeb's default for its limit.
        internal const double MaxDynamicPressure = 20000.0;
        // How fast the throttle falls as the pressure goes over the limit, and how near its limit a part may come before the throttle falls.
        private const double Falloff = 15.0;
        private const double TemperatureMargin = 0.05;

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

        // The hottest part's temperature against its limit; the part's name comes with it. Zero for a vessel with no part that has a limit.
        internal static double Hottest(Vessel vessel, out string name)
        {
            double worst = 0.0;
            name = "";
            foreach (Part part in vessel.parts)
            {
                // A part burns where its inside or its skin goes over its limit.
                double share = part.maxTemp > 0.0 ? part.temperature / part.maxTemp : 0.0;
                if (part.skinMaxTemp > 0.0) share = Math.Max(share, part.skinTemperature / part.skinMaxTemp);
                if (share <= worst) continue;
                worst = share;
                name = part.partInfo != null ? part.partInfo.title : part.name;
            }
            return worst;
        }

        // The temperature of the shock in front of the vessel at this speed, as the game works it out. It is the same the descent's simulation takes
        // for its parachutes. The speed gives it by a factor, which is blended towards a fast-flight law over the Mach range. The difficulty's
        // heat and the body's own multiplier scale it.
        internal static double ShockTemperature(CelestialBody body, double speed, double mach)
        {
            double slow = speed * PhysicsGlobals.NewtonianTemperatureFactor;
            double blend = Math.Pow(
                Math.Max(0.0, Math.Min(1.0, (mach - PhysicsGlobals.NewtonianMachTempLerpStartMach) / (PhysicsGlobals.NewtonianMachTempLerpEndMach - PhysicsGlobals.NewtonianMachTempLerpStartMach))),
                PhysicsGlobals.NewtonianMachTempLerpExponent);
            if (blend > 0.0)
            {
                double fast = PhysicsGlobals.MachTemperatureScalar * Math.Pow(speed, PhysicsGlobals.MachTemperatureVelocityExponent);
                slow = slow + (fast - slow) * blend;
            }
            return slow * HighLogic.CurrentGame.Parameters.Difficulty.ReentryHeatScale * body.shockTemperatureMultiplier;
        }

        // One line in the log of what the game says of the heat on the vessel's hottest exposed part, with the air round it. It is there to
        // check the model the heat of a pass is judged by against the game's own.
        internal static void LogFlux(Vessel vessel)
        {
            Part hottest = null;
            double worst = 0.0;
            foreach (Part part in vessel.parts)
            {
                double share = part.skinMaxTemp > 0.0 ? part.skinTemperature / part.skinMaxTemp : 0.0;
                if (share > worst || hottest == null)
                {
                    worst = share;
                    hottest = part;
                }
            }
            if (hottest == null) return;
            UnityEngine.Debug.Log(ReDefinition.Core.Log.Tag + " Heat: " + hottest.name + " density " + vessel.atmDensity.ToString("0.000000") + " speed " + vessel.srfSpeed.ToString("0.0")
                                  + " mach " + vessel.mach.ToString("0.00") + " skin " + hottest.skinTemperature.ToString("0.0") + "/" + hottest.skinMaxTemp.ToString("0")
                                  + " inside " + hottest.temperature.ToString("0.0") + "/" + hottest.maxTemp.ToString("0") + " convection " + hottest.thermalConvectionFlux.ToString("0.00")
                                  + " radiation " + hottest.thermalRadiationFlux.ToString("0.00") + " exposed " + hottest.exposedArea.ToString("0.000")
                                  + " skinMass " + hottest.skinThermalMass.ToString("0.000") + " skinArea " + hottest.skinExposedArea.ToString("0.000") + " shock "
                                  + ShockTemperature(vessel.mainBody, vessel.srfSpeed, vessel.mach).ToString("0") + " mach " + PhysicsGlobals.MachConvectionFactor.ToString("0.0000") + "/"
                                  + PhysicsGlobals.MachConvectionDensityExponent.ToString("0.00") + "/" + PhysicsGlobals.MachConvectionVelocityExponent.ToString("0.00")
                                  + " newton " + PhysicsGlobals.NewtonianConvectionFactorTotal.ToString("0.0000") + "/" + PhysicsGlobals.NewtonianDensityExponent.ToString("0.00") + "/"
                                  + PhysicsGlobals.NewtonianVelocityExponent.ToString("0.00") + " turbulent " + PhysicsGlobals.TurbulentConvectionMult.ToString("0.00"));
        }

        // What the game's convective flux is per unit of its own law, which is the Mach factor times density and speed to its exponents. It was
        // measured against the flux the game reports on a part in Kerbin's air at Mach 7 to 9: about 1.1e-6 kW per m2 where the factor is 7.
        private const double FluxPerFactor = 1.6e-7;
        // Stefan-Boltzmann, in kW per m2 and K to the fourth.
        private const double Stefan = 5.670374e-11;

        // The skin temperature a part settles at where the heat parameter of a pass is met flat on, in K: the convective flux balanced by the part
        // radiating it as a plate of the same area would. It is an upper bound: the part radiates from more than its exposed face and gives heat
        // to what it touches. A part behind a heat shield that still ablates is not exposed, and none that has no limit.
        internal static double SettlingTemperature(Part part, double heatParameter)
        {
            if (part.skinMaxTemp <= 0.0 || part.emissiveConstant <= 0.0 || Ablates(part)) return 0.0;
            double flux = PhysicsGlobals.MachConvectionFactor * FluxPerFactor * heatParameter;
            return Math.Pow(flux / (part.emissiveConstant * Stefan), 0.25);
        }

        // Whether the part is a heat shield with ablator left, which carries the heat away.
        private static bool Ablates(Part part)
        {
            foreach (ModuleAblator ablator in part.FindModulesImplementing<ModuleAblator>())
            {
                PartResource resource = part.Resources.Get(ablator.ablativeResource);
                if (resource != null && resource.amount > 0.0) return true;
            }
            return false;
        }

        // How near the hottest part of the vessel comes to its limit in a pass with this heat parameter: its settling temperature over the limit.
        internal static double PassShare(Vessel vessel, double heatParameter)
        {
            double worst = 0.0;
            foreach (Part part in vessel.parts)
                if (part.skinMaxTemp > 0.0) worst = Math.Max(worst, SettlingTemperature(part, heatParameter) / part.skinMaxTemp);
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
