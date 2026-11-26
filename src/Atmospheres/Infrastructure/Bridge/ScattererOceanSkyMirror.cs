using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    [AtmospheresAssumptions]
    internal static class AssumptionsOceanSkyMirror
    {
        public static readonly ForeignAssumption OceanSkyMirror =
            new ForeignAssumption("atmo-scatterer-ocean-sky-mirror", "Scatterer", "the sea mirrors the own clouds")
                .Type("Scatterer.OceanNode")
                .Field("Scatterer.OceanNode", "m_oceanMaterial", "UnityEngine.Material")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the sea mirrors Scatterer's clear sky under the own clouds");
    }

    // The sea mirrors the own clouds (master plan 46.1 part 4 stage C; the owner 11-26: "Klar soll das Meer die Wolkendecke spiegeln"). Scatterer 0.908's
    // ocean mirrors its own analytic clear sky while its material holds the keyword SKY_REFLECTIONS_ON (OceanNode.InitOceanMaterial, set once from its setting
    // oceanSkyReflections); with SKY_REFLECTIONS_OFF it mirrors the reflection probe (PRUEFPROTOKOLL 312), which the own sky probe replaces with the own clouds
    // (OwnCloudSkyProbe, 314-316). While that probe runs, the ocean materials are switched to SKY_REFLECTIONS_OFF, every frame (Scatterer rebuilds its ocean on
    // a body change or a settings change, with a new material); the materials switched are put back when the probe stops. Scatterer's setting is not touched.
    internal static class ScattererOceanSkyMirror
    {
        private const string On = "SKY_REFLECTIONS_ON", Off = "SKY_REFLECTIONS_OFF";
        private const float RescanSeconds = 1f;
        private static Type nodeType;
        private static FieldInfo materialField;
        private static UnityEngine.Object[] nodes = new UnityEngine.Object[0];
        private static float rescanAt = -1f;
        private static bool failed, said;
        // The materials this switched (it puts back only these).
        private static readonly List<Material> switched = new List<Material>();

        // Each frame of the own clouds: mirror the own sky probe where it runs, else Scatterer's sky as it was.
        internal static void Apply(bool skyProbeRunning)
        {
            if (!skyProbeRunning)
            {
                Release();
                return;
            }
            if (failed || !AssumptionsOceanSkyMirror.OceanSkyMirror.Holds) return;
            try
            {
                if (nodeType == null)
                {
                    nodeType = TypeLookup.Find("Scatterer.OceanNode");
                    materialField = nodeType?.GetField("m_oceanMaterial", TypeLookup.Any);
                    if (materialField == null)
                    {
                        Fail("OceanNode.m_oceanMaterial not found");
                        return;
                    }
                }
                if (Time.realtimeSinceStartup >= rescanAt)
                {
                    rescanAt = Time.realtimeSinceStartup + RescanSeconds;
                    nodes = UnityEngine.Object.FindObjectsOfType(nodeType);
                }
                foreach (UnityEngine.Object n in nodes)
                {
                    if (n == null) continue;
                    var m = materialField.GetValue(n) as Material;
                    if (m == null || !m.IsKeywordEnabled(On)) continue;
                    m.DisableKeyword(On);
                    m.EnableKeyword(Off);
                    if (!switched.Contains(m)) switched.Add(m);
                    if (!said)
                    {
                        said = true;
                        AtmospheresLog.Say("own clouds: Scatterer's ocean mirrors the own sky probe (" + Off + " while the own clouds draw)");
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        // Puts the switched materials back to Scatterer's own sky reflections.
        internal static void Release()
        {
            if (switched.Count == 0) return;
            foreach (Material m in switched)
            {
                if (m == null) continue;
                m.DisableKeyword(Off);
                m.EnableKeyword(On);
            }
            switched.Clear();
        }

        private static void Fail(string why)
        {
            failed = true;
            AtmospheresLog.WarnOnce("own clouds: the sea mirroring the own clouds stops: " + why);
        }
    }
}
