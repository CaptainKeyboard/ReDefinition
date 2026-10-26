using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The installed mods raytracing reaches into, each only where it is there
    // (docs/development/raytracing.md, "Installed mods").
    internal static class RtKspMods
    {
        // Whether the assemblies were looked through (Look); Kopernicus's current star and the light of a Sun;
        // PlanetShine's switch.
        private static bool looked;
        private static FieldInfo kopernicusCurrent;
        private static PropertyInfo starLight;
        private static FieldInfo planetShineOn;
        // PlanetShine's switch, read every frame through a compiled getter: a
        // FieldInfo's GetValue boxes the bool.
        private static Func<bool> planetShineRead;
        private static Type deferredReflections;
        private static FieldInfo starShifter;
        private static bool scatterer;
        // Scatterer's OceanNode, its prolandManager and that one's parentCelestialBody
        // (Scatterer, OceanNode.cs, ProlandManager.cs; public); the body last looked at,
        // when, and whether one of the nodes belongs to it.
        private static Type oceanNode;
        private static FieldInfo nodeManager, managerBody;
        private static CelestialBody oceanBody;
        private static float oceanLookedAt = float.NegativeInfinity;
        private static bool oceanOnBody;
        private static bool oceanReadsLogged;
        private static FieldInfo shifterColour;
        // Parallax's scatters, read through Scenery.ParallaxScatterFields; off for the run
        // once reading them failed.
        private static bool parallaxFailed;
        private static bool grassRangeFailed;
        private static float grassRange, grassRangeAboutVessel;
        private static float grassRangeAt = float.MinValue;

        // Whether Scatterer is installed: its atmosphere colours the sun's light.
        internal static bool ScattererInstalled
        {
            get
            {
                Look();
                return scatterer;
            }
        }

        // Looks once through the loaded assemblies for Kopernicus, PlanetShine, Scatterer and Deferred and the members
        // read from each; a mod whose members are not found is left out.
        private static void Look()
        {
            if (looked) return;
            looked = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                try
                {
                    if (name == "Kopernicus")
                    {
                        // KopernicusStar.Current, the star nearest the focus (StarLightSwitcher),
                        // a Sun whose sunLight lights the scene; Sun.Instance is the last star
                        // cloned, as each clone's Awake sets it.
                        Type star = assembly.GetType("Kopernicus.Components.KopernicusStar");
                        kopernicusCurrent = star?.GetField("Current", BindingFlags.Public | BindingFlags.Static);
                        starLight = typeof(Sun).GetProperty("sunLight", BindingFlags.Public | BindingFlags.Instance);
                        // The star's own colour: KopernicusStar.shifter, a LightShifter, its
                        // sunlightColor, which Kopernicus gives the star's light
                        // (KopernicusStar.cs, LightShifter.cs; public).
                        starShifter = star?.GetField("shifter", BindingFlags.Public | BindingFlags.Instance);
                        shifterColour = starShifter?.FieldType.GetField("sunlightColor", BindingFlags.Public | BindingFlags.Instance);
                        if (shifterColour != null && shifterColour.FieldType != typeof(Color)) shifterColour = null;
                    }
                    else if (name == "PlanetShine")
                    {
                        Type planetShine = assembly.GetType("PlanetShine.PlanetShine");
                        planetShineOn = planetShine?.GetField("renderEnabled", BindingFlags.Public | BindingFlags.Static);
                        planetShineRead = planetShineOn != null && planetShineOn.FieldType == typeof(bool)
                            ? Expression.Lambda<Func<bool>>(Expression.Field(null, planetShineOn)).Compile()
                            : null;
                    }
                    // Scatterer's assembly is "scatterer" (GameData/scatterer/Scatterer.dll).
                    else if (string.Equals(name, "Scatterer", StringComparison.OrdinalIgnoreCase))
                    {
                        scatterer = true;
                        oceanNode = assembly.GetType("Scatterer.OceanNode");
                        nodeManager = oceanNode?.GetField("prolandManager", BindingFlags.Public | BindingFlags.Instance);
                        managerBody = nodeManager?.FieldType.GetField("parentCelestialBody", BindingFlags.Public | BindingFlags.Instance);
                        if (managerBody != null && managerBody.FieldType != typeof(CelestialBody)) managerBody = null;
                    }
                    else if (name == "Deferred")
                    {
                        // The component Deferred puts on the first local camera
                        // (Deferred.HandleCameras).
                        deferredReflections = assembly.GetType("Deferred.ScreenSpaceReflections");
                    }
                }
                catch (Exception)
                {
                    // A mod of another version: raytracing goes on without it.
                }
            }
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
            if (kopernicusCurrent != null && starLight != null)
            {
                Sun current = kopernicusCurrent.GetValue(null) as Sun;
                if (current != null)
                {
                    Light light = starLight.GetValue(current, null) as Light;
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
            if (kopernicusCurrent != null && starShifter != null && shifterColour != null)
            {
                object current = kopernicusCurrent.GetValue(null);
                object shifter = current != null ? starShifter.GetValue(current) : null;
                if (shifter != null) return (Color)shifterColour.GetValue(shifter);
            }
            return Color.white;
        }

        // StarBody's lookup: Kopernicus's current star, KSP's Sun, or the planetarium's sun.
        private static CelestialBody FindStarBody()
        {
            Look();
            if (kopernicusCurrent != null)
            {
                Sun current = kopernicusCurrent.GetValue(null) as Sun;
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

        // Parallax's scatter this frame: per active renderer and level of detail, the
        // append buffer its evaluation filled in LateUpdate, with its mesh and material.
        // A shared scatter draws its parent's buffers with a mesh of its own: both are
        // there.
        internal static int ParallaxScatter(List<RtGpuItem> into)
        {
            if (parallaxFailed || !Scenery.ParallaxScatterFields.Available) return 0;
            int buffers = 0;
            try
            {
                IList renderers = Scenery.ParallaxScatterFields.Renderers();
                if (renderers == null) return 0;
                for (int r = 0; r < renderers.Count; r++)
                {
                    object renderer = renderers[r];
                    if (renderer == null) continue;
                    for (int lod = 0; lod < 3; lod++)
                    {
                        // Where the space centre's grass is lifted, its clumps under the
                        // space centre's ground left out (Scenery.KscGrass.RaysBuffer).
                        ComputeBuffer buffer = Scenery.KscGrass.RaysBuffer(renderer, lod,
                                                                           Scenery.ParallaxScatterFields.Output(renderer, lod));
                        Mesh mesh = Scenery.ParallaxScatterFields.Mesh(renderer, lod);
                        if (buffer == null || !buffer.IsValid() || mesh == null) continue;
                        into.Add(new RtGpuItem
                        {
                            Transforms = buffer,
                            Mesh = mesh,
                            SubMesh = 0,
                            Material = Scenery.ParallaxScatterFields.Material(renderer, lod),
                            Mask = 0xFF,
                        });
                        buffers++;
                    }
                }
            }
            catch (Exception e)
            {
                // Another version of Parallax: its scatter stays out of the rays.
                parallaxFailed = true;
                Core.CompatibilityLog.Warn("rt-parallax-scatter", "Raytracing: Parallax's scatter is left out of the rays ("
                                                                  + Core.CompatibilityLog.Reason(e) + ").");
            }
            return buffers;
        }

        // Where Parallax draws its scatters about the vessel from (RuntimeOperations.vectorCraftPos): the
        // active vessel's position, without one the camera's.
        internal static Vector3 ParallaxVessel(Vector3 camera)
        {
            Vessel vessel = FlightGlobals.fetch != null ? FlightGlobals.ActiveVessel : null;
            return vessel != null ? vessel.transform.position : camera;
        }

        // How far Parallax draws its grass, in metres. The result is the longest range of the active scatters named
        // grass that it culls about the camera. `aboutVessel` is the longest range of those it culls about the active
        // vessel (Scenery.ParallaxScatterFields.AboutVessel). Kerbin's main grass is 320 m of the vessel in
        // Parallax_StockScatterTextures, its tall grass 400 m of the camera. Each is 0 where none is (or another
        // version of Parallax). Asked once a second.
        internal static float ParallaxGrassRange(out float aboutVessel)
        {
            float now = Time.realtimeSinceStartup;
            aboutVessel = grassRangeAboutVessel;
            if (now - grassRangeAt < 1f) return grassRange;
            grassRangeAt = now;
            grassRange = grassRangeAboutVessel = aboutVessel = 0f;
            if (parallaxFailed || grassRangeFailed || !Scenery.ParallaxScatterFields.Available) return 0f;
            try
            {
                IList renderers = Scenery.ParallaxScatterFields.Renderers();
                if (renderers == null) return 0f;
                foreach (object renderer in renderers)
                {
                    string name = Scenery.ParallaxScatterFields.ScatterName(renderer);
                    if (name == null || name.IndexOf("Grass", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    float range = Scenery.ParallaxScatterFields.Range(renderer);
                    if (Scenery.ParallaxScatterFields.AboutVessel(renderer))
                        grassRangeAboutVessel = Mathf.Max(grassRangeAboutVessel, range);
                    else
                        grassRange = Mathf.Max(grassRange, range);
                }
            }
            catch (Exception)
            {
                grassRangeFailed = true;
                grassRange = grassRangeAboutVessel = 0f;
            }
            aboutVessel = grassRangeAboutVessel;
            return grassRange;
        }

        // The materials Parallax draws its scatter with this scene, every active renderer's
        // three levels of detail.
        internal static void ParallaxScatterMaterials(List<Material> into)
        {
            if (parallaxFailed || !Scenery.ParallaxScatterFields.Available) return;
            try
            {
                IList renderers = Scenery.ParallaxScatterFields.Renderers();
                if (renderers == null) return;
                for (int r = 0; r < renderers.Count; r++)
                {
                    object renderer = renderers[r];
                    if (renderer == null) continue;
                    for (int lod = 0; lod < 3; lod++)
                    {
                        Material material = Scenery.ParallaxScatterFields.Material(renderer, lod);
                        if (material != null) into.Add(material);
                    }
                }
            }
            catch (Exception)
            {
                // Another version of Parallax: its scatter keeps its textures as they are.
            }
        }

        // PlanetShine's light is off while the raytraced diffuse light brings the planet's
        // and the moons'. The planet's comes from the reflection probe, which sees it
        // (FlightReflectionProbe's culling mask has Scaled Scenery), or as a light where
        // there is none. The moons' come as lights (RtReflectors). It goes back as it was afterwards. Where the player
        // switches it on meanwhile, in PlanetShine's own window, that is the player's choice, and it stays.
        private static bool planetShineHeld;
        private static bool planetShineBefore;
        private static bool planetShineLeft;

        // Holds (true) or gives back (false) PlanetShine's light as described above; called every frame while the
        // raytracing runs and with false on Detach.
        internal static void HoldPlanetShine(bool hold)
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

        // Deferred's screen-space reflections off on the scene camera while the raytraced
        // reflections take the specular light, and back as they were afterwards. With
        // them on, Deferred's reflection pass leaves the probe's specular out
        // (useSSROnCurrentCamera, Internal-DeferredReflections.shader). Their own pass
        // adds the traced or the probe's in its place (ScreenSpaceReflections.shader,
        // pass 0, Blend One One). ReDefinition's reflection pass writes the raytraced
        // specular regardless, which then counted twice. Where the player or another mod
        // switches it on meanwhile, it stays.
        private static Behaviour reflectionsHeld;
        private static bool reflectionsLeft;

        // Whether Scatterer's ocean is there and its code is not the one the raytraced
        // reflections were checked against (ForeignAssumptions.ScattererOceanReflections):
        // then Deferred's screen-space reflections stay on, as the ocean reads their
        // result whether they run or not.
        internal static bool OceanNeedsDeferredReflections()
        {
            return ScattererOcean(SceneBody()) && !Core.ForeignAssumptions.ScattererOceanReflections.Holds;
        }

        // Whether the scene's body has a sea and it is KSP's own: no Scatterer ocean draws it.
        internal static bool StockOcean()
        {
            CelestialBody body = SceneBody();
            return body != null && body.ocean && !ScattererOcean(body);
        }

        // Whether Scatterer draws an ocean on the body: one of its ocean nodes belongs to it,
        // looked for at most once a second. A planet pack's sea without Scatterer's ocean
        // configuration, or Scatterer's ocean switched off, leaves KSP's sea. Where the
        // fields are not found, Scatterer's presence decides.
        private static readonly List<UnityEngine.Object> oceanNodes = new List<UnityEngine.Object>();
        private static float oceanSearchedAt = float.NegativeInfinity;

        // Whether Scatterer draws an ocean on `body`, as described above; true where its fields are not found.
        internal static bool ScattererOcean(CelestialBody body)
        {
            Look();
            if (!scatterer || body == null || !body.ocean) return false;
            if (oceanNode == null || nodeManager == null || managerBody == null) return true;
            float now = Time.realtimeSinceStartup;
            if (body == oceanBody && now < oceanLookedAt + 1f) return oceanOnBody;
            // The ocean nodes found are kept and asked; the scene is searched again only
            // for another body, when one of them is gone, or every ten seconds while none
            // is found: FindObjectsOfType walks every object, and once a second it held a
            // frame for 3.3 ms (measured 2026-10-06).
            bool search = body != oceanBody || oceanNodes.Count == 0 && now >= oceanSearchedAt + 10f;
            foreach (UnityEngine.Object node in oceanNodes)
                if (node == null) search = true;
            oceanBody = body;
            oceanLookedAt = now;
            oceanOnBody = false;
            try
            {
                if (search)
                {
                    oceanSearchedAt = now;
                    oceanNodes.Clear();
                    oceanNodes.AddRange(UnityEngine.Object.FindObjectsOfType(oceanNode));
                }
                foreach (UnityEngine.Object node in oceanNodes)
                {
                    object manager = node != null ? nodeManager.GetValue(node) : null;
                    if (manager != null && (CelestialBody)managerBody.GetValue(manager) == body)
                    {
                        oceanOnBody = true;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                oceanOnBody = true;
            }
            return oceanOnBody;
        }

        // Whether Scatterer's ocean is drawn and reads Deferred's screen-space reflections
        // while they are held for the raytraced ones: then it takes the raytraced
        // reflections in their place (RtCamera.OceanReflections). Scatterer registers its
        // ocean with Deferred's reflections and reads their result, ssrOutput, whether
        // they run or not (measured: useOceanSSR 1 with them off, KSP.log).
        internal static bool OceanReadsHeldReflections()
        {
            CelestialBody body = SceneBody();
            bool ocean = ScattererOcean(body);
            bool reads = ocean && reflectionsHeld != null && Core.ForeignAssumptions.ScattererOceanReflections.Holds;
            if (reads != oceanReadsLogged)
            {
                oceanReadsLogged = reads;
                Debug.Log(Core.Log.Tag + " Raytracing: Scatterer " + (scatterer ? "found" : "not found") + ", Deferred's reflections "
                          + (reflectionsHeld != null ? "held" : "not held") + ", its ocean on " + (body != null ? body.bodyName : "no body") + " " + ocean
                          + ": the ocean " + (reads ? "takes" : "does not take") + " the raytraced reflections.");
            }
            return reads;
        }

        // Holds (true) or gives back (false) the reflections as described above; called every frame from RtKsp.Update
        // and with false on Detach. It switches them in Update, outside any camera's render: the component adds its
        // command buffers in OnPreRender and takes those same ones off in OnPostRender, which sets
        // useSSROnCurrentCamera back to 0.
        internal static void HoldDeferredReflections(bool hold, Camera sceneCamera)
        {
            Look();
            // How the component switches its pass on and off, checked in Deferred's code
            // (ForeignAssumptions.DeferredSsrHold): where it changed, the reflections stay on.
            if (deferredReflections == null || !Core.ForeignAssumptions.DeferredSsrHold.Holds) return;
            try
            {
                if (hold && reflectionsHeld == null)
                {
                    if (reflectionsLeft || sceneCamera == null) return;
                    Behaviour reflections = sceneCamera.GetComponent(deferredReflections) as Behaviour;
                    if (reflections == null || !reflections.enabled) return;
                    reflections.enabled = false;
                    reflectionsHeld = reflections;
                    Debug.Log(Core.Log.Tag + " Raytracing: Deferred's screen-space reflections off on " + sceneCamera.name
                              + " while the raytraced reflections take their place.");
                }
                else if (hold && reflectionsHeld.enabled)
                {
                    reflectionsHeld = null;
                    reflectionsLeft = true;
                }
                else if (!hold)
                {
                    if (reflectionsHeld != null && !reflectionsHeld.enabled)
                    {
                        reflectionsHeld.enabled = true;
                        Debug.Log(Core.Log.Tag + " Raytracing: Deferred's screen-space reflections on again.");
                    }
                    reflectionsHeld = null;
                    reflectionsLeft = false;
                }
            }
            catch (Exception e)
            {
                deferredReflections = null;
                // Not left off for good where the hold had switched them off.
                try
                {
                    if (reflectionsHeld != null && !reflectionsHeld.enabled) reflectionsHeld.enabled = true;
                }
                catch (Exception)
                {
                    // The same failure, or the component gone: the warning below stands.
                }
                reflectionsHeld = null;
                reflectionsLeft = false;
                Core.CompatibilityLog.Warn("rt-deferred-ssr", "Raytracing: Deferred's screen-space reflections are no longer"
                                                              + " held for the raytraced ones ("
                                                              + Core.CompatibilityLog.Reason(e) + ").");
            }
        }
    }
}
