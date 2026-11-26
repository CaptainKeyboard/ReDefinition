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
    // had, so the factor never compounds. Today only the measurement key light.testScatterExposure drives it (LightWriter).
    [AtmospheresAssumptions]
    internal static class AssumptionsAirUnderClouds
    {
        public static readonly ForeignAssumption AirUnderClouds =
            new ForeignAssumption("atmo-scatterer-air-under-clouds", "Scatterer", "Scatterer's air in-scatters only the light the own clouds pass on")
                .Type("Scatterer.SkyNode").Type("Scatterer.ConfigPoint")
                .Field("Scatterer.SkyNode", "interpolatedSettings", "Scatterer.ConfigPoint")
                .Field("Scatterer.ConfigPoint", "scatteringExposure", "System.Single")
                .Calls("Scatterer.SkyNode", "InterpolateVariables")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "Scatterer's air in-scatters the clear sun's light under the own clouds");
    }

    internal static class ScattererAirUnderClouds
    {
        private const float RescanSeconds = 1f;
        private static Type nodeType;
        private static FieldInfo settingsField, exposureField;
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
                    if (exposureField == null)
                    {
                        Fail("SkyNode.interpolatedSettings.scatteringExposure not found");
                        return;
                    }
                }
                if (Time.realtimeSinceStartup >= rescanAt)
                {
                    rescanAt = Time.realtimeSinceStartup + RescanSeconds;
                    nodes = UnityEngine.Object.FindObjectsOfType(nodeType);
                }
                float share = Mathf.Max(0f, factor);
                foreach (UnityEngine.Object n in nodes)
                {
                    if (n == null) continue;
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

        // Scatterer's own value back until its next interpolation.
        internal static void Release()
        {
            if (failed || exposureField == null) return;
            try
            {
                foreach (KeyValuePair<object, float[]> kv in state)
                    exposureField.SetValue(kv.Key, kv.Value[0]);
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
