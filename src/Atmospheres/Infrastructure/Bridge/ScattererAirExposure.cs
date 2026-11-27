using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Scatterer's exposures scaled on its per-frame copy, for two purposes: (1) the air under the own clouds in-scatters only the diffuse share the clouds pass on
    // (scatteringExposure of the clouds' body; master plan 46.1 part 3); (2) the air and the sea seen from above the atmosphere take the space factor (scatteringExposure
    // and skyExposure of the camera's body; master plan 47.2S, a setting of ours, never a patch of Scatterer's config). Each has its own contract below.
    // How the write works (PRUEFPROTOKOLL 297): Scatterer 0.908's scattering over the scene
    // (Scatterer/DepthBufferScattering) adds the in-scatter of sun-lit air times _ScatteringExposure. Scatterer interpolates its settings in
    // SkyNode.UpdateNode (InterpolateVariables, from its Update) and writes them to its materials in SkyNode.OnPreRender, a camera message of a camera that
    // renders before the flight camera (decompiled). 279 wrote from the flight camera's pre-cull, after that write and undone by the next interpolation: no
    // effect. This writes in LateUpdate, after every Update and before any camera renders. A frame in which Scatterer did not interpolate keeps the base it
    // had, so the factor never compounds (297: written so, the in-scatter reacts; at 0 the distant island under the deck went from bright blue to dark).
    // Under the own clouds the air is lit only by the diffuse share they pass on: the own clouds publish that share (the same as KSP's probe gets,
    // OwnCloudProbeLight) and the light writer's LateUpdate drives it (Drive); the measurement key light.testScatterExposure overrides it.
    [AtmospheresAssumptions]
    internal static class AssumptionsScattererAir
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

        // The air from space (master plan 47.2S) writes a second field of the same per-frame copy; it has its own contract so that a Scatterer without that field loses the
        // space factor alone, not the share under the clouds.
        public static readonly ForeignAssumption AirFromSpace =
            new ForeignAssumption("atmo-scatterer-air-from-space", "Scatterer", "Scatterer's air and sea seen from above the atmosphere take the space factor")
                .Type("Scatterer.SkyNode").Type("Scatterer.ConfigPoint")
                .Field("Scatterer.SkyNode", "interpolatedSettings", "Scatterer.ConfigPoint")
                .Field("Scatterer.ConfigPoint", "scatteringExposure", "System.Single")
                .Field("Scatterer.ConfigPoint", "skyExposure", "System.Single")
                .Field("Scatterer.SkyNode", "prolandManager", "Scatterer.ProlandManager")
                .Field("Scatterer.ProlandManager", "parentCelestialBody", "CelestialBody")
                .Calls("Scatterer.SkyNode", "InterpolateVariables")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the air and the sea from space keep Scatterer's own level");
    }

    internal static class ScattererAirExposure
    {
        private const float RescanSeconds = 1f;
        private static Type nodeType;
        private static FieldInfo settingsField, exposureField, skyField, managerField, bodyField;
        private static UnityEngine.Object[] nodes = new UnityEngine.Object[0];
        private static float rescanAt = -1f;
        private static bool failed, logged;
        // Per settings object: Scatterer's own value of the frame and the value written over it, for scatteringExposure (0, 1) and for skyExposure (2, 3).
        private static readonly Dictionary<object, float[]> state = new Dictionary<object, float[]>();

        // THE AIR FROM SPACE (master plan 47.2S; owner 2026-11-26 / 27: a foreign value is written only as a setting of ours, never as a fixed patch): seen from above the
        // atmosphere Scatterer's air and sea are brighter against the clouds than the reference pictures show. The space factor scales the body's scatteringExposure AND
        // skyExposure the same way the share under the clouds scales the first: on Scatterer's interpolated copy of the frame, never on its config. 1: no change.
        // light.spaceAir: the factor far above the atmosphere (1: off, Scatterer's own level).
        internal const string TuneSpaceAir = "light.spaceAir";
        // The least space factor: the air and the sea are never switched off.
        internal const float MinSpace = 0.05f;
        private static float spaceFactor = 1f;
        private static int spaceFrame = -10;
        private static CelestialBody spaceBody;

        // The space factor at a camera altitude: 1 up to the atmosphere's top, the value from twice that height on, linear between (Kerbin: 70 to 140 km). A body without
        // atmosphere (top 0): 1.
        internal static float SpaceFactor(double altitudeM, double atmosphereTopM, float value)
        {
            if (!(atmosphereTopM > 0.0) || float.IsNaN(value) || value >= 1f) return 1f;
            float v = Mathf.Max(value, MinSpace);
            double t = (altitudeM - atmosphereTopM) / atmosphereTopM;
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            return 1f + (v - 1f) * (float)t;
        }

        // The space factor of this frame for the camera's body (the own clouds publish it with their frame).
        internal static void PublishSpace(float factor, CelestialBody body)
        {
            spaceBody = body;
            spaceFactor = Mathf.Clamp(factor, MinSpace, 1f);
            spaceFrame = Time.frameCount;
            if (spaceFactor != 1f) everPublished = true;
        }

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
            bool fresh = Time.frameCount - publishedFrame <= 2;
            float space = Time.frameCount - spaceFrame <= 2 ? spaceFactor : 1f;
            if (testFactor != 1f) Apply(testFactor, space);
            else if (fresh || space != 1f) Apply(fresh ? publishedShare : 1f, space);
            else if (Active) Release();
        }

        // Scales each sky node's interpolated values (call in LateUpdate): the share on the scatteringExposure of the own clouds' body, the space factor on the scatteringExposure
        // and the skyExposure of the camera's body. A value that would not change is not written.
        internal static void Apply(float factor, float space)
        {
            if (failed) return;
            if (!AssumptionsScattererAir.AirUnderClouds.Holds)
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
                    // The air from space's own contract (AirFromSpace): where it does not hold the space factor is not applied at all.
                    skyField = AssumptionsScattererAir.AirFromSpace.Holds ? settingsField?.FieldType.GetField("skyExposure", TypeLookup.Any) : null;
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
                // Without the space contract (skyField is null then) both of its fields keep Scatterer's level: the factor is never half applied.
                float spaceNow = skyField != null ? Mathf.Clamp(space, MinSpace, 1f) : 1f;
                foreach (UnityEngine.Object n in nodes)
                {
                    if (n == null) continue;
                    bool own = OfBody(n, publishedBody), far = spaceNow != 1f && spaceBody != null && OfBody(n, spaceBody);
                    object settings = settingsField.GetValue(n);
                    if (settings == null) continue;
                    float[] s;
                    bool have = state.TryGetValue(settings, out s);
                    if (!own && !far)
                    {
                        // A node that is ours no longer (the clouds' or the camera's body changed, the space factor is back at 1 while another body's share is
                        // published): Scatterer's own values back where our last write still stands, as Release does for all.
                        if (have)
                        {
                            Back(settings, s);
                            state.Remove(settings);
                        }
                        continue;
                    }
                    float current = (float)exposureField.GetValue(settings);
                    if (!have)
                    {
                        s = new float[4];
                        state[settings] = s;
                    }
                    float b = Base(current, s[0], s[1], have);
                    float written = b * (own ? share : 1f) * (far ? spaceNow : 1f);
                    if (written != current) exposureField.SetValue(settings, written);
                    s[0] = b;
                    s[1] = written;
                    if (skyField != null)
                    {
                        // The sky's exposure takes the space factor alone. Off (the factor 1, the camera inside the atmosphere): nothing is written, except Scatterer's own
                        // value over a last write of ours that still stands (a frame in which Scatterer did not interpolate).
                        float skyCurrent = (float)skyField.GetValue(settings);
                        float skyBase = Base(skyCurrent, s[2], s[3], have);
                        float skyWritten = far ? skyBase * spaceNow : skyBase;
                        if (skyWritten != skyCurrent) skyField.SetValue(settings, skyWritten);
                        s[2] = skyBase;
                        s[3] = skyWritten;
                    }
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
                foreach (KeyValuePair<object, float[]> kv in state) Back(kv.Key, kv.Value);
            }
            catch (Exception)
            {
            }
            state.Clear();
            logged = false;
        }

        // One settings object back to Scatterer's own values (s: base and last write of the scattering, base and last write of the sky): only where a value is still the
        // one written and differs from its base.
        private static void Back(object settings, float[] s)
        {
            if (s[1] != s[0] && (float)exposureField.GetValue(settings) == s[1]) exposureField.SetValue(settings, s[0]);
            if (skyField != null && s[3] != s[2] && (float)skyField.GetValue(settings) == s[3]) skyField.SetValue(settings, s[2]);
        }

        private static void Fail(string why)
        {
            failed = true;
            AtmospheresLog.WarnOnce("Scatterer's air under the clouds stops: " + why);
        }
    }
}
