using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Autopilot.Planning
{
    // What a pass through the air does to the vessel's parts, as far as it can be told before it is flown, and what it showed once it was. The
    // flux of a pass comes from the descent's simulation (HeatBound); here it becomes the temperature a part settles at, and the share of its
    // limit. After a pass the share the game showed is set against the share predicted, which judges the next.
    internal static class PassHeat
    {
        // One line in the log of what the game says of the heat on the vessel's hottest exposed part, with the air round it. These are the figures
        // the bound of the flux is checked against; tests/ReDefinition.Tests/Data/heat-flux-points.csv is made of them.
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
            UnityEngine.Debug.Log(Log.Tag + " Heat: " + hottest.name + " density " + vessel.atmDensity.ToString("0.000000") + " speed " + vessel.srfSpeed.ToString("0.0")
                                  + " mach " + vessel.mach.ToString("0.00") + " skin " + hottest.skinTemperature.ToString("0.0") + "/" + hottest.skinMaxTemp.ToString("0")
                                  + " inside " + hottest.temperature.ToString("0.0") + "/" + hottest.maxTemp.ToString("0") + " convection " + hottest.thermalConvectionFlux.ToString("0.00")
                                  + " radiation " + hottest.thermalRadiationFlux.ToString("0.00") + " exposed " + hottest.exposedArea.ToString("0.000"));
        }

        // Stefan-Boltzmann, in kW per m2 and K to the fourth.
        private const double Stefan = 5.670374e-11;

        // The skin temperature a part settles at under an upper bound of the convective flux of a pass, in K: the flux balanced by the part
        // radiating it as a plate of the same area would. It is an upper bound: the flux is one for a cold skin, and the part radiates from more
        // than its exposed face and gives heat to what it touches. A part behind a heat shield that still ablates is not exposed, and none that
        // has no limit.
        internal static double SettlingTemperature(Part part, double fluxKW)
        {
            if (part.skinMaxTemp <= 0.0 || part.emissiveConstant <= 0.0 || Ablates(part)) return 0.0;
            return Math.Pow(fluxKW / (part.emissiveConstant * Stefan), 0.25);
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

        // The last pass's bound, as the share of the hottest part's limit the search predicted for the pass it chose, and what the pass then
        // showed, per vessel. The ratio of the two is what the next pass is judged by: the game's own heating, measured, over the bound.
        private static readonly Dictionary<Guid, double> measured = new Dictionary<Guid, double>();
        internal static double LastPredicted;

        // What a pass showed against what the bound said of it: the hottest part's share of its limit, over the predicted. NaN until a pass has
        // been measured. The factor is held to what a bound and a measurement can differ by.
        internal static double Calibration(Vessel vessel)
        {
            double ratio;
            return measured.TryGetValue(vessel.id, out ratio) ? ratio : double.NaN;
        }

        // Takes in the pass that was flown: its hottest share of a part's limit against what was predicted of it.
        internal static void Learn(Vessel vessel, double hottestShare)
        {
            if (LastPredicted <= 0.0 || hottestShare <= 0.0) return;
            double ratio = Math.Max(0.1, Math.Min(2.0, hottestShare / LastPredicted));
            measured[vessel.id] = ratio;
            UnityEngine.Debug.Log(Log.Tag + " Heat: the pass showed " + (hottestShare * 100.0).ToString("0") + " % of a part's limit where the bound said "
                                  + (LastPredicted * 100.0).ToString("0") + " %; the next is judged by " + ratio.ToString("0.00") + " of the bound.");
        }

        // How near the hottest part of the vessel comes to its limit in a pass with this bound of the flux: its settling temperature over the limit.
        internal static double PassShare(Vessel vessel, double fluxKW)
        {
            double worst = 0.0;
            foreach (Part part in vessel.parts)
                if (part.skinMaxTemp > 0.0) worst = Math.Max(worst, SettlingTemperature(part, fluxKW) / part.skinMaxTemp);
            return worst;
        }

    }
}
