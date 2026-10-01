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
        private static bool looked;
        private static FieldInfo kopernicusCurrent;
        private static PropertyInfo starLight;
        private static FieldInfo planetShineOn;
        private static Type deferredReflections;
        // Parallax's fields, read through compiled getters: a FieldInfo's GetValue costs
        // a reflection call per field, nine per scatter renderer a frame.
        private static Func<object, object> scatterManager;
        private static Func<object, IList> activeScatters;
        private static readonly Func<object, Mesh>[] scatterMeshes = new Func<object, Mesh>[3];
        private static readonly Func<object, Material>[] scatterMaterials = new Func<object, Material>[3];
        private static readonly Func<object, ComputeBuffer>[] scatterBuffers = new Func<object, ComputeBuffer>[3];

        // A getter of the field on an object of its declaring type (null for a static
        // field), or null where there is no such field or it is not a T.
        private static Func<object, T> Getter<T>(FieldInfo field) where T : class
        {
            if (field == null || !typeof(T).IsAssignableFrom(field.FieldType)) return null;
            ParameterExpression target = Expression.Parameter(typeof(object), "target");
            Expression owner = field.IsStatic ? null : Expression.Convert(target, field.DeclaringType);
            Expression value = Expression.Field(owner, field);
            if (field.FieldType != typeof(T)) value = Expression.Convert(value, typeof(T));
            return Expression.Lambda<Func<object, T>>(value, target).Compile();
        }

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
                        scatterManager = Getter<object>(manager?.GetField("Instance", BindingFlags.Public | BindingFlags.Static));
                        activeScatters = Getter<IList>(manager?.GetField("activeScatterRenderers",
                                                                         BindingFlags.Public | BindingFlags.Instance));
                        for (int lod = 0; lod < 3 && renderer != null; lod++)
                        {
                            scatterMeshes[lod] = Getter<Mesh>(renderer.GetField("meshLOD" + lod, BindingFlags.Public | BindingFlags.Instance));
                            scatterMaterials[lod] = Getter<Material>(renderer.GetField("instancedMaterialLOD" + lod,
                                                                                       BindingFlags.Public | BindingFlags.Instance));
                            scatterBuffers[lod] = Getter<ComputeBuffer>(renderer.GetField("outputLOD" + lod,
                                                                                          BindingFlags.Public | BindingFlags.Instance));
                        }
                    }
                    else if (name == "PlanetShine")
                    {
                        Type planetShine = assembly.GetType("PlanetShine.PlanetShine");
                        planetShineOn = planetShine?.GetField("renderEnabled", BindingFlags.Public | BindingFlags.Static);
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
        // light, looked for once a second, and none found kept for that second too.
        private static Light brightest;
        private static bool brightestFound;
        private static float brightestAt = float.NegativeInfinity;

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
            Look();
            if (scatterManager == null || activeScatters == null || scatterBuffers[2] == null) return 0;
            int buffers = 0;
            try
            {
                object manager = scatterManager(null);
                IList renderers = manager != null ? activeScatters(manager) : null;
                if (renderers == null) return 0;
                for (int r = 0; r < renderers.Count; r++)
                {
                    object renderer = renderers[r];
                    if (renderer == null) continue;
                    for (int lod = 0; lod < 3; lod++)
                    {
                        ComputeBuffer buffer = scatterBuffers[lod](renderer);
                        Mesh mesh = scatterMeshes[lod]?.Invoke(renderer);
                        if (buffer == null || !buffer.IsValid() || mesh == null) continue;
                        into.Add(new RtGpuItem
                        {
                            Transforms = buffer,
                            Mesh = mesh,
                            SubMesh = 0,
                            Material = scatterMaterials[lod]?.Invoke(renderer),
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

        // Deferred's screen-space reflections off on the scene camera while the raytraced
        // reflections take the specular light, and back as they were afterwards. With
        // them on, Deferred's reflection pass leaves the probe's specular out
        // (useSSROnCurrentCamera, Internal-DeferredReflections.shader) and their own pass
        // adds the traced or the probe's in its place (ScreenSpaceReflections.shader,
        // pass 0, Blend One One); ReDefinition's reflection pass writes the raytraced
        // specular regardless, which then counted twice. Switched in Update, outside any
        // camera's render: the component adds its command buffers in OnPreRender and
        // takes those same ones off in OnPostRender, which sets useSSROnCurrentCamera
        // back to 0. Where the player or another mod switches it on meanwhile, it stays.
        private static Behaviour reflectionsHeld;
        private static bool reflectionsLeft;

        internal static void HoldDeferredReflections(bool hold, Camera sceneCamera)
        {
            Look();
            if (deferredReflections == null) return;
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
            catch (Exception)
            {
                deferredReflections = null;
            }
        }
    }
}
