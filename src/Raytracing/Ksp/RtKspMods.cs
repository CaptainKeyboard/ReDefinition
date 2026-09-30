using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The installed mods raytracing reaches into, each only where it is there
    // (docs/development/raytracing.md, "Installed mods").
    internal static class RtKspMods
    {
        private static bool looked;
        private static FieldInfo kopernicusCurrent;
        private static PropertyInfo starLight;
        private static FieldInfo planetShineOn;
        private static FieldInfo scatterManager;
        private static FieldInfo activeScatters;
        private static readonly FieldInfo[] scatterMeshes = new FieldInfo[3];
        private static readonly FieldInfo[] scatterMaterials = new FieldInfo[3];
        private static readonly FieldInfo[] scatterBuffers = new FieldInfo[3];

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
                    }
                    else if (name == "ParallaxContinued")
                    {
                        // ScatterManager.Instance.activeScatterRenderers, each ScatterRenderer's
                        // meshLOD0-2, instancedMaterialLOD0-2 and outputLOD0-2 (Parallax Continued,
                        // Scatter System/ScatterManager.cs, ScatterRenderer.cs; public).
                        Type manager = assembly.GetType("Parallax.ScatterManager");
                        Type renderer = assembly.GetType("Parallax.ScatterRenderer");
                        scatterManager = manager?.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                        activeScatters = manager?.GetField("activeScatterRenderers", BindingFlags.Public | BindingFlags.Instance);
                        for (int lod = 0; lod < 3 && renderer != null; lod++)
                        {
                            scatterMeshes[lod] = renderer.GetField("meshLOD" + lod, BindingFlags.Public | BindingFlags.Instance);
                            scatterMaterials[lod] = renderer.GetField("instancedMaterialLOD" + lod,
                                                                      BindingFlags.Public | BindingFlags.Instance);
                            scatterBuffers[lod] = renderer.GetField("outputLOD" + lod, BindingFlags.Public | BindingFlags.Instance);
                        }
                    }
                    else if (name == "PlanetShine")
                    {
                        Type planetShine = assembly.GetType("PlanetShine.PlanetShine");
                        planetShineOn = planetShine?.GetField("renderEnabled", BindingFlags.Public | BindingFlags.Static);
                    }
                }
                catch (Exception)
                {
                    // A mod of another version: raytracing goes on without it.
                }
            }
        }

        // The light of the star that lights the scene: Kopernicus's current star, or
        // KSP's sun.
        internal static Light SunLight()
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

        // Where KSP has no Sun, as in the editors: the scene's brightest directional
        // light, looked for once a second.
        private static Light brightest;
        private static float brightestAt = float.NegativeInfinity;

        private static Light BrightestDirectional()
        {
            if (brightest != null && brightest.isActiveAndEnabled && Time.unscaledTime - brightestAt < 1f) return brightest;
            brightestAt = Time.unscaledTime;
            brightest = null;
            foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                if (light.type == LightType.Directional && light.isActiveAndEnabled
                    && (brightest == null || light.intensity > brightest.intensity))
                    brightest = light;
            return brightest;
        }

        // Parallax's scatter this frame: per active renderer and level of detail, the
        // append buffer its evaluation filled in LateUpdate, with its mesh and material.
        // A shared scatter draws its parent's buffers with a mesh of its own: both are
        // there.
        internal static int ParallaxScatter(List<RtGpuItem> into)
        {
            Look();
            if (scatterManager == null || activeScatters == null || scatterBuffers[2] == null) return 0;
            int buffers = 0;
            try
            {
                object manager = scatterManager.GetValue(null);
                IList renderers = manager != null ? activeScatters.GetValue(manager) as IList : null;
                if (renderers == null) return 0;
                for (int r = 0; r < renderers.Count; r++)
                {
                    object renderer = renderers[r];
                    if (renderer == null) continue;
                    for (int lod = 0; lod < 3; lod++)
                    {
                        ComputeBuffer buffer = scatterBuffers[lod].GetValue(renderer) as ComputeBuffer;
                        Mesh mesh = scatterMeshes[lod]?.GetValue(renderer) as Mesh;
                        if (buffer == null || !buffer.IsValid() || mesh == null) continue;
                        into.Add(new RtGpuItem
                        {
                            Transforms = buffer,
                            Mesh = mesh,
                            SubMesh = 0,
                            Material = scatterMaterials[lod]?.GetValue(renderer) as Material,
                            Mask = 0xFF,
                        });
                        buffers++;
                    }
                }
            }
            catch (Exception)
            {
                // Another version of Parallax: its scatter stays out of the rays.
                scatterManager = null;
            }
            return buffers;
        }

        // PlanetShine's light off while the raytraced diffuse light brings the planet's
        // from the reflection probe, which sees it (FlightReflectionProbe's culling mask
        // has Scaled Scenery), and back as it was afterwards. Where the player switches it
        // on meanwhile, in PlanetShine's own window, that is the player's choice: it stays.
        private static bool planetShineHeld;
        private static bool planetShineBefore;
        private static bool planetShineLeft;

        internal static void HoldPlanetShine(bool hold)
        {
            Look();
            if (planetShineOn == null) return;
            try
            {
                bool on = (bool)planetShineOn.GetValue(null);
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
            catch (Exception)
            {
                planetShineOn = null;
            }
        }
    }
}
