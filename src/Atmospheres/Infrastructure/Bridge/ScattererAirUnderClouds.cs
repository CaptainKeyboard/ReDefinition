using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Scatterer's air in-scatter scaled (master plan 46.1 part 3, PRUEFPROTOKOLL 297). Scatterer 0.908's scattering over the scene
    // (Scatterer/DepthBufferScattering) adds the in-scatter of sun-lit air times _ScatteringExposure. Scatterer interpolates its settings in
    // SkyNode.UpdateNode (InterpolateVariables, from its Update) and writes them to its materials in SkyNode.OnPreRender, a camera message of a camera that
    // renders before the flight camera (decompiled). 279 wrote from the flight camera's pre-cull, after that write and undone by the next interpolation: no
    // effect. This writes in LateUpdate, after every Update and before any camera renders. A frame in which Scatterer did not interpolate keeps the base it
    // had, so the factor never compounds (297: written so, the in-scatter reacts; at 0 the distant island under the deck went from bright blue to dark).
    // Under the own clouds the air is lit only by the diffuse share they pass on: the own clouds publish that share (the same as KSP's probe gets,
    // OwnCloudProbeLight) and the light writer's LateUpdate drives it (Drive); the measurement key light.testScatterExposure overrides it.
    [AtmospheresAssumptions]
    internal static class AssumptionsAirUnderClouds
    {
        public static readonly ForeignAssumption AirUnderClouds =
            new ForeignAssumption("atmo-scatterer-air-under-clouds", "Scatterer", "Scatterer's air in-scatters only the light the own clouds pass on")
                .Type("Scatterer.SkyNode").Type("Scatterer.ConfigPoint")
                .Field("Scatterer.SkyNode", "interpolatedSettings", "Scatterer.ConfigPoint")
                .Field("Scatterer.ConfigPoint", "scatteringExposure", "System.Single")
                .Field("Scatterer.SkyNode", "prolandManager", "Scatterer.ProlandManager")
                .Field("Scatterer.ProlandManager", "parentCelestialBody", "CelestialBody")
                .Calls("Scatterer.SkyNode", "InterpolateVariables")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "Scatterer's air in-scatters the clear sun's light under the own clouds");
    }

    internal static class ScattererAirUnderClouds
    {
        private const float RescanSeconds = 1f;
        private static Type nodeType;
        private static FieldInfo settingsField, exposureField, managerField, bodyField;
        private static UnityEngine.Object[] nodes = new UnityEngine.Object[0];
        private static float rescanAt = -1f;
        private static bool failed, logged;
        // Per settings object: Scatterer's own value of the frame and the value written over it.
        private static readonly Dictionary<object, float[]> state = new Dictionary<object, float[]>();

        // The base of this frame: Scatterer's fresh value, or the stored base where the value is still the one written last (Scatterer did not interpolate).
        internal static float Base(float current, float storedBase, float lastWritten, bool haveStored)
        {
            return haveStored && current == lastWritten ? storedBase : current;
        }

        internal static bool Active { get { return state.Count > 0; } }

        private static float publishedShare = 1f;
        private static int publishedFrame = -10;
        private static bool everPublished;
        // The body the own clouds stand on: only its sky node is scaled (the idea check 299: one SkyNode per body).
        private static CelestialBody publishedBody;

        // Whether Drive has anything to do: published once, or still holding a write.
        internal static bool Wanted { get { return everPublished || state.Count > 0; } }

        // The air's diffuse share under the own clouds, from their frame (they publish while drawn).
        internal static void Publish(float share, CelestialBody body)
        {
            publishedBody = body;
            publishedShare = Mathf.Clamp01(share);
            publishedFrame = Time.frameCount;
            everPublished = true;
        }

        // From LateUpdate: the measurement key's factor where set (not 1), else the published share while the own clouds publish it, else Scatterer's own value.
        internal static void Drive(float testFactor)
        {
            if (testFactor != 1f) Apply(testFactor);
            else if (Time.frameCount - publishedFrame <= 2) Apply(publishedShare);
            else if (Active) Release();
        }

        // Scales each sky node's interpolated scatteringExposure by the factor (call in LateUpdate).
        internal static void Apply(float factor)
        {
            if (failed) return;
            if (!AssumptionsAirUnderClouds.AirUnderClouds.Holds)
            {
                Fail("its contract does not hold");
                return;
            }
            try
            {
                if (nodeType == null)
                {
                    nodeType = TypeLookup.Find("Scatterer.SkyNode");
                    settingsField = nodeType?.GetField("interpolatedSettings", TypeLookup.Any);
                    exposureField = settingsField?.FieldType.GetField("scatteringExposure", TypeLookup.Any);
                    managerField = nodeType?.GetField("prolandManager", TypeLookup.Any);
                    bodyField = managerField?.FieldType.GetField("parentCelestialBody", TypeLookup.Any);
                    if (exposureField == null || bodyField == null)
                    {
                        Fail("SkyNode.interpolatedSettings.scatteringExposure or prolandManager.parentCelestialBody not found");
                        return;
                    }
                }
                if (Time.realtimeSinceStartup >= rescanAt)
                {
                    rescanAt = Time.realtimeSinceStartup + RescanSeconds;
                    nodes = UnityEngine.Object.FindObjectsOfType(nodeType);
                    // Entries of nodes that are gone (a scene or body switch) are dropped.
                    if (state.Count > 0) Prune();
                }
                float share = Mathf.Max(0f, factor);
                foreach (UnityEngine.Object n in nodes)
                {
                    if (n == null || !OfBody(n, publishedBody)) continue;
                    object settings = settingsField.GetValue(n);
                    if (settings == null) continue;
                    float current = (float)exposureField.GetValue(settings);
                    float[] s;
                    bool have = state.TryGetValue(settings, out s);
                    if (!have)
                    {
                        s = new float[2];
                        state[settings] = s;
                    }
                    float b = Base(current, s[0], s[1], have);
                    float written = b * share;
                    exposureField.SetValue(settings, written);
                    s[0] = b;
                    s[1] = written;
                    if (!logged)
                    {
                        logged = true;
                        AtmospheresLog.Say("Scatterer's air: scatteringExposure on " + nodes.Length + " sky node(s), " + b.ToString("0.000") + " -> " + written.ToString("0.000"));
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        // Whether a sky node belongs to the body (no body known: every node, as for the measurement key outside the own clouds).
        private static bool OfBody(UnityEngine.Object node, CelestialBody body)
        {
            if (body == null) return true;
            object manager = managerField.GetValue(node);
            return manager != null && ReferenceEquals(bodyField.GetValue(manager), body);
        }

        // Drops the entries whose settings object no node holds any more.
        private static void Prune()
        {
            HashSet<object> live = new HashSet<object>();
            foreach (UnityEngine.Object n in nodes)
                if (n != null)
                {
                    object s = settingsField.GetValue(n);
                    if (s != null) live.Add(s);
                }
            List<object> gone = new List<object>();
            foreach (object k in state.Keys)
                if (!live.Contains(k)) gone.Add(k);
            foreach (object k in gone) state.Remove(k);
        }

        // Scatterer's own value back until its next interpolation: only where the value is still the one written (a fresh interpolation is kept).
        internal static void Release()
        {
            if (failed || exposureField == null) return;
            try
            {
                foreach (KeyValuePair<object, float[]> kv in state)
                    if ((float)exposureField.GetValue(kv.Key) == kv.Value[1]) exposureField.SetValue(kv.Key, kv.Value[0]);
            }
            catch (Exception)
            {
            }
            state.Clear();
            logged = false;
        }

        private static void Fail(string why)
        {
            failed = true;
            AtmospheresLog.WarnOnce("Scatterer's air under the clouds stops: " + why);
        }
    }
}
