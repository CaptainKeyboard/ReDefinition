using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The installed mods raytracing reaches into (Kopernicus, PlanetShine, Scatterer, Deferred, Parallax), each only
    // where it is there. Read by reflection from the loaded assemblies. Each mod has its class: KopernicusStars,
    // ParallaxScatterReader, PlanetShineHold and OceanReflections; this is where the rest of the raytracing asks.
    internal static class RtKspMods
    {
        // Whether Scatterer is installed: its atmosphere colours the sun's light.
        internal static bool ScattererInstalled { get { return OceanReflections.ScattererInstalled; } }

        // The body the scene stands on: the flight's main body, the home body at the space
        // centre; null elsewhere.
        internal static CelestialBody SceneBody()
        {
            return KopernicusStars.SceneBody();
        }

        // The light of the star that lights the scene (KopernicusStars.SunLight).
        internal static Light SunLight()
        {
            return KopernicusStars.SunLight();
        }

        // The colour of the star's light as it leaves the star (KopernicusStars.StarColour).
        internal static Color StarColour()
        {
            return KopernicusStars.StarColour();
        }

        // The star that lights the scene as a body (KopernicusStars.StarBody).
        internal static CelestialBody StarBody()
        {
            return KopernicusStars.StarBody();
        }

        // The same body alone, for a caller outside the raytracing that runs every frame (KopernicusStars.StarBodyOnly).
        internal static CelestialBody StarBodyOnly()
        {
            return KopernicusStars.StarBodyOnly();
        }

        // Parallax's scatter this frame, per active renderer and level of detail (ParallaxScatterReader.Collect).
        internal static int ParallaxScatter(List<RtGpuItem> into)
        {
            return ParallaxScatterReader.Collect(into);
        }

        // Where Parallax draws its scatters about the vessel from (ParallaxScatterReader.Vessel).
        internal static Vector3 ParallaxVessel(Vector3 camera)
        {
            return ParallaxScatterReader.Vessel(camera);
        }

        // The nearest distance from the camera at which the vessel-centred grass turns to its farthest level of
        // detail, 0 for none (set by ParallaxGrassRange).
        internal static float ParallaxGrassLodStart { get { return ParallaxScatterReader.GrassLodStart; } }

        // How far Parallax draws its grass, in metres (ParallaxScatterReader.GrassRange).
        internal static float ParallaxGrassRange(out float aboutVessel)
        {
            return ParallaxScatterReader.GrassRange(out aboutVessel);
        }

        // The materials Parallax draws its scatter with this scene (ParallaxScatterReader.Materials).
        internal static void ParallaxScatterMaterials(List<Material> into)
        {
            ParallaxScatterReader.Materials(into);
        }

        // Holds (true) or gives back (false) PlanetShine's light (PlanetShineHold.Hold).
        internal static void HoldPlanetShine(bool hold)
        {
            PlanetShineHold.Hold(hold);
        }

        // Whether Scatterer's ocean needs Deferred's screen-space reflections to stay on (OceanReflections.NeedsDeferredReflections).
        internal static bool OceanNeedsDeferredReflections()
        {
            return OceanReflections.NeedsDeferredReflections();
        }

        // Whether the scene's body has a sea and it is KSP's own: no Scatterer ocean draws it.
        internal static bool StockOcean()
        {
            return OceanReflections.StockOcean();
        }

        // Whether Scatterer draws an ocean on `body`; true where its fields are not found (OceanReflections.ScattererOcean).
        internal static bool ScattererOcean(CelestialBody body)
        {
            return OceanReflections.ScattererOcean(body);
        }

        // Whether Scatterer's ocean reads Deferred's reflections while they are held for the raytraced ones
        // (OceanReflections.ReadsHeldReflections).
        internal static bool OceanReadsHeldReflections()
        {
            return OceanReflections.ReadsHeldReflections();
        }

        // Holds (true) or gives back (false) Deferred's screen-space reflections (OceanReflections.HoldDeferredReflections).
        internal static void HoldDeferredReflections(bool hold, Camera sceneCamera)
        {
            OceanReflections.HoldDeferredReflections(hold, sceneCamera);
        }
    }
}
