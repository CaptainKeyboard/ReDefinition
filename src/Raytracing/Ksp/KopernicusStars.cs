using System;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The star that lights the scene, its light and its colour, and the body the scene stands on. Kopernicus's
    // current star where Kopernicus is installed, KSP's sun otherwise. Read by reflection from the loaded assembly.
    internal static class KopernicusStars
    {
        // Whether the assemblies were looked through (Look).
        private static bool looked;
        // Those read every frame, through compiled getters: the current star, its light, its shifter and the shifter's colour.
        private static Func<object, object> readStar, readShifter;
        private static Func<object, Light> readStarLight;
        private static Func<object, Color> readShifterColour;

        // Looks once through the loaded assemblies for Kopernicus and the members read from it; a Kopernicus whose
        // members are not found is left out.
        private static void Look()
        {
            if (looked) return;
            looked = true;
            RtLoadedMods.Each("Kopernicus", StringComparison.Ordinal, assembly =>
            {
                // KopernicusStar.Current, the star nearest the focus (StarLightSwitcher),
                // a Sun whose sunLight lights the scene; Sun.Instance is the last star
                // cloned, as each clone's Awake sets it.
                Type star = assembly.GetType("Kopernicus.Components.KopernicusStar");
                FieldInfo current = star?.GetField("Current", BindingFlags.Public | BindingFlags.Static);
                PropertyInfo light = typeof(Sun).GetProperty("sunLight", BindingFlags.Public | BindingFlags.Instance);
                // The star's own colour: KopernicusStar.shifter, a LightShifter, its
                // sunlightColor, which Kopernicus gives the star's light
                // (KopernicusStar.cs, LightShifter.cs; public).
                FieldInfo shifter = star?.GetField("shifter", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo colour = shifter?.FieldType.GetField("sunlightColor", BindingFlags.Public | BindingFlags.Instance);
                if (colour != null && colour.FieldType != typeof(Color)) colour = null;
                readStar = Core.CompiledGetter.Of<object>(current);
                readStarLight = Core.CompiledGetter.Of<Light>(light);
                readShifter = Core.CompiledGetter.Of<object>(shifter);
                readShifterColour = Core.CompiledGetter.Of<Color>(colour);
            });
        }

        // The body the scene stands on: the flight's main body, the home body at the space
        // centre; null elsewhere.
        internal static CelestialBody SceneBody()
        {
            return HighLogic.LoadedSceneIsFlight ? FlightGlobals.currentMainBody
                   : HighLogic.LoadedScene == GameScenes.SPACECENTER ? FlightGlobals.GetHomeBody() : null;
        }

        // The star, its light and its colour as this frame found them: asked for several
        // times a frame, by reflection where Kopernicus is installed.
        private static int starFrame = -1;
        private static Light frameSunLight;
        private static Color frameStarColour;
        private static CelestialBody frameStarBody;

        // Finds the star, its light and its colour once per frame.
        private static void LookAtStar()
        {
            if (starFrame == Time.frameCount) return;
            starFrame = Time.frameCount;
            frameSunLight = FindSunLight();
            frameStarColour = FindStarColour();
            frameStarBody = FindStarBody();
        }

        // The light of the star that lights the scene: Kopernicus's current star, or
        // KSP's sun. Not the brightest directional light: KSP's scaled-space copy of the
        // sun is at least as bright, and PlanetShine's and Kopernicus's other stars'
        // lights are directional.
        internal static Light SunLight()
        {
            LookAtStar();
            return frameSunLight;
        }

        // The colour of the star's light as it leaves the star, before any atmosphere
        // tints the scene's light (Scatterer does): Kopernicus's for its current star,
        // white for KSP's sun.
        internal static Color StarColour()
        {
            LookAtStar();
            return frameStarColour;
        }

        // The star that lights the scene as a body: Kopernicus's current star, or KSP's
        // sun (Sun.sun); null where there is none, as in the editors.
        internal static CelestialBody StarBody()
        {
            LookAtStar();
            return frameStarBody;
        }

        // The same body alone, for a caller outside the raytracing that runs every frame
        // (DistantObjectBehaviour): this frame's where it was looked at already, else
        // looked for without the light and its colour, which allocate.
        internal static CelestialBody StarBodyOnly()
        {
            return starFrame == Time.frameCount ? frameStarBody : FindStarBody();
        }

        // SunLight's lookup: Kopernicus's current star's light, KSP's sun's, or the brightest directional light.
        private static Light FindSunLight()
        {
            Look();
            if (readStar != null && readStarLight != null)
            {
                Sun current = readStar(null) as Sun;
                if (current != null)
                {
                    Light light = readStarLight(current);
                    if (light != null) return light;
                }
            }
            if (Sun.Instance != null && Sun.Instance.sunLight != null) return Sun.Instance.sunLight;
            return BrightestDirectional();
        }

        // StarColour's lookup: the colour Kopernicus's current star gives its light, white without Kopernicus.
        private static Color FindStarColour()
        {
            Look();
            if (readStar != null && readShifter != null && readShifterColour != null)
            {
                object current = readStar(null);
                object shifter = current != null ? readShifter(current) : null;
                if (shifter != null) return readShifterColour(shifter);
            }
            return Color.white;
        }

        // StarBody's lookup: Kopernicus's current star, KSP's Sun, or the planetarium's sun.
        private static CelestialBody FindStarBody()
        {
            Look();
            if (readStar != null)
            {
                Sun current = readStar(null) as Sun;
                if (current != null && current.sun != null) return current.sun;
            }
            if (Sun.Instance != null && Sun.Instance.sun != null) return Sun.Instance.sun;
            return Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
        }

        // Where KSP has no Sun, as in the editors: the scene's brightest directional
        // light, looked for once a second, and none found kept for that second too.
        private static Light brightest;
        private static bool brightestFound;
        private static float brightestAt = float.NegativeInfinity;

        // The scene's brightest active directional light, as described above; null for none.
        private static Light BrightestDirectional()
        {
            if (Time.unscaledTime - brightestAt < 1f)
            {
                if (!brightestFound) return null;
                if (brightest != null && brightest.isActiveAndEnabled) return brightest;
            }
            brightestAt = Time.unscaledTime;
            brightest = null;
            foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                if (light.type == LightType.Directional && light.isActiveAndEnabled
                    && (brightest == null || light.intensity > brightest.intensity))
                    brightest = light;
            brightestFound = brightest != null;
            return brightest;
        }
    }
}
