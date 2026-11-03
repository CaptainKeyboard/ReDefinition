using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The sea and its reflections: which ocean draws a body (KSP's own or Scatterer's) and Deferred's
    // screen-space reflections, held off while the raytraced reflections take the specular light.
    // Read by reflection from the loaded assemblies of Scatterer and Deferred, each only where it is there.
    internal static class OceanReflections
    {
        // Whether the assemblies were looked through (Look); whether Scatterer is installed.
        private static bool looked;
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
        private static Type deferredReflections;

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

        // Whether Scatterer is installed: its atmosphere colours the sun's light.
        internal static bool ScattererInstalled
        {
            get
            {
                Look();
                return scatterer;
            }
        }

        // Looks once through the loaded assemblies for Scatterer and Deferred and the members read from each; a mod
        // whose members are not found is left out.
        private static void Look()
        {
            if (looked) return;
            looked = true;
            // Scatterer's assembly is "scatterer" (GameData/scatterer/Scatterer.dll).
            RtLoadedMods.Each("Scatterer", StringComparison.OrdinalIgnoreCase, assembly =>
            {
                scatterer = true;
                oceanNode = assembly.GetType("Scatterer.OceanNode");
                nodeManager = oceanNode?.GetField("prolandManager", BindingFlags.Public | BindingFlags.Instance);
                managerBody = nodeManager?.FieldType.GetField("parentCelestialBody", BindingFlags.Public | BindingFlags.Instance);
                if (managerBody != null && managerBody.FieldType != typeof(CelestialBody)) managerBody = null;
            });
            // The component Deferred puts on the first local camera (Deferred.HandleCameras).
            RtLoadedMods.Each("Deferred", StringComparison.Ordinal, assembly =>
                deferredReflections = assembly.GetType("Deferred.ScreenSpaceReflections"));
        }

        // Whether Scatterer's ocean is there and its code is not the one the raytraced
        // reflections were checked against (ForeignAssumptions.ScattererOceanReflections):
        // then Deferred's screen-space reflections stay on, as the ocean reads their
        // result whether they run or not.
        internal static bool NeedsDeferredReflections()
        {
            return ScattererOcean(KopernicusStars.SceneBody()) && !Core.ForeignAssumptions.ScattererOceanReflections.Holds;
        }

        // Whether the scene's body has a sea and it is KSP's own: no Scatterer ocean draws it.
        internal static bool StockOcean()
        {
            CelestialBody body = KopernicusStars.SceneBody();
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
        internal static bool ReadsHeldReflections()
        {
            CelestialBody body = KopernicusStars.SceneBody();
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
