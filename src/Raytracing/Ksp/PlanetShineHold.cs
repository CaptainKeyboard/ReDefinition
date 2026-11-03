using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // PlanetShine's light is off while the raytraced diffuse light brings the planet's
    // and the moons'. The planet's comes from the reflection probe, which sees it
    // (FlightReflectionProbe's culling mask has Scaled Scenery), or as a light where
    // there is none. The moons' come as lights (RtReflectors). It goes back as it was afterwards. Where the player
    // switches it on meanwhile, in PlanetShine's own window, that is the player's choice, and it stays.
    internal static class PlanetShineHold
    {
        // Whether the assemblies were looked through (Look); PlanetShine's switch.
        private static bool looked;
        private static FieldInfo planetShineOn;
        // PlanetShine's switch, read every frame through a compiled getter: a
        // FieldInfo's GetValue boxes the bool.
        private static Func<bool> planetShineRead;

        // The hold's state: whether the light is held now, whether it was on before, and whether the player switched it on meanwhile.
        private static bool planetShineHeld;
        private static bool planetShineBefore;
        private static bool planetShineLeft;

        // Looks once through the loaded assemblies for PlanetShine and its switch; a PlanetShine whose switch is not
        // found is left out.
        private static void Look()
        {
            if (looked) return;
            looked = true;
            RtLoadedMods.Each("PlanetShine", StringComparison.Ordinal, assembly =>
            {
                Type planetShine = assembly.GetType("PlanetShine.PlanetShine");
                planetShineOn = planetShine?.GetField("renderEnabled", BindingFlags.Public | BindingFlags.Static);
                planetShineRead = planetShineOn != null && planetShineOn.FieldType == typeof(bool)
                    ? Expression.Lambda<Func<bool>>(Expression.Field(null, planetShineOn)).Compile()
                    : null;
            });
        }

        // Holds (true) or gives back (false) PlanetShine's light as described above; called every frame while the
        // raytracing runs and with false on Detach.
        internal static void Hold(bool hold)
        {
            Look();
            if (planetShineOn == null || planetShineRead == null) return;
            try
            {
                bool on = planetShineRead();
                if (hold && !planetShineHeld)
                {
                    if (planetShineLeft) return;
                    planetShineBefore = on;
                    planetShineOn.SetValue(null, false);
                    planetShineHeld = true;
                    if (on)
                        Debug.Log(Core.Log.Tag + " Raytracing: PlanetShine's light off while the raytraced diffuse light brings"
                                  + " the planet's.");
                }
                else if (hold && on)
                {
                    // Switched on by the player while held.
                    planetShineHeld = false;
                    planetShineLeft = true;
                }
                else if (!hold)
                {
                    if (planetShineHeld && !on) planetShineOn.SetValue(null, planetShineBefore);
                    planetShineHeld = false;
                    planetShineLeft = false;
                }
            }
            catch (Exception e)
            {
                planetShineOn = null;
                planetShineRead = null;
                Core.CompatibilityLog.Warn("rt-planetshine", "Raytracing: PlanetShine's light is no longer switched off under"
                                                             + " the raytraced diffuse light ("
                                                             + Core.CompatibilityLog.Reason(e) + ").");
            }
        }
    }
}
