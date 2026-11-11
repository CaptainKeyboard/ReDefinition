using System;
using System.Collections;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the atmospheres ask of Parallax Continued when the snow on a built quad changes, by reflection and guarded. A symbol that is
    // missing is logged once and that part is left out. Parallax reads the vertex colours of a quad when it makes its own data.
    // A repaint of the mesh has to be announced:
    //
    //   Terrain: PQSMod_Parallax.terrainQuadData[quad].OnQuadNormalUpdate() is the call Parallax makes itself after the mesh of a
    //   quad changed. In the build checked, it reads the vertices, normals and colours of the mesh again for the subdivided mesh.
    //
    //   Scatters: ScatterComponent.scatterQuadData[quad] keeps a copy of the colours (colors) and a compute buffer of them
    //   (sourceColorsBuffer). The scatters take their colour from the buffer when they are made. After the buffer is set, each
    //   ScatterData is cleaned up and started again: the sequence Parallax's own ReinitializeScatters runs.
    //
    // Only the FieldInfos are kept. The dictionaries are read from them at each use, so one that Parallax replaces is followed.
    // The lookup is tried again every few seconds while Parallax is not loaded. A path is switched off after several failures in a
    // row, not at the first bad quad.
    internal static class ParallaxLink
    {
        // The lookup of the types is tried this often (real seconds) and this many times.
        internal const float LookIntervalSeconds = 5f;
        internal const int LookTries = 6;

        // The failures in a row after which the terrain path or the scatter path is switched off.
        internal const int FailuresToSwitchOff = 5;

        private static readonly RetryGate Gate = new RetryGate(LookIntervalSeconds, LookTries);
        private static readonly FailureStreak TerrainFailures = new FailureStreak(FailuresToSwitchOff);
        private static readonly FailureStreak ScatterFailures = new FailureStreak(FailuresToSwitchOff);
        private static bool resolved;
        private static string summary = "Parallax not installed";
        private static FieldInfo terrainField;
        private static MethodInfo terrainChanged;
        private static FieldInfo scatterField;
        private static FieldInfo scatterColors;
        private static FieldInfo scatterBuffer;
        private static FieldInfo scatterList;
        private static MethodInfo scatterStart;
        private static MethodInfo scatterCleanup;

        // What the lookup found, for the log.
        internal static string Summary
        {
            get
            {
                Look();
                return summary;
            }
        }

        internal static bool Terrain
        {
            get
            {
                Look();
                return terrainChanged != null && !TerrainFailures.Disabled && Quads(terrainField) != null;
            }
        }

        internal static bool Scatters
        {
            get
            {
                Look();
                return scatterColors != null && scatterBuffer != null && scatterList != null && scatterStart != null && scatterCleanup != null
                       && !ScatterFailures.Disabled && Quads(scatterField) != null;
            }
        }

        // The dictionary a static field holds now; null where the field or the dictionary is not there.
        private static IDictionary Quads(FieldInfo field)
        {
            return field != null ? field.GetValue(null) as IDictionary : null;
        }

        private static void Look()
        {
            if (resolved || !Gate.Due(Time.realtimeSinceStartup)) return;
            try
            {
                Type terrainMod = TypeLookup.Find("Parallax.PQSMod_Parallax");
                if (terrainMod == null) return;
                resolved = true;
                var link = new ReflectLink();
                Type terrainData = TypeLookup.Find("Parallax.TerrainShaderQuadData");
                terrainField = link.Field(terrainMod, "PQSMod_Parallax", "terrainQuadData", BindingFlags.Public | BindingFlags.Static);
                terrainChanged = link.Method(terrainData, "TerrainShaderQuadData", "OnQuadNormalUpdate", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                if (terrainField != null && !typeof(IDictionary).IsAssignableFrom(terrainField.FieldType)) terrainField = null;

                Type scatterComponent = TypeLookup.Find("Parallax.ScatterComponent");
                Type quadData = TypeLookup.Find("Parallax.ScatterSystemQuadData");
                Type scatterData = TypeLookup.Find("Parallax.ScatterData");
                scatterField = link.Field(scatterComponent, "ScatterComponent", "scatterQuadData", BindingFlags.Public | BindingFlags.Static);
                scatterColors = link.Field(quadData, "ScatterSystemQuadData", "colors", BindingFlags.Public | BindingFlags.Instance, typeof(Color[]));
                scatterBuffer = link.Field(quadData, "ScatterSystemQuadData", "sourceColorsBuffer", BindingFlags.Public | BindingFlags.Instance, typeof(ComputeBuffer));
                scatterList = link.Field(quadData, "ScatterSystemQuadData", "quadScatters", BindingFlags.Public | BindingFlags.Instance);
                scatterStart = link.Method(scatterData, "ScatterData", "Start", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                scatterCleanup = link.Method(scatterData, "ScatterData", "Cleanup", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                if (scatterField != null && !typeof(IDictionary).IsAssignableFrom(scatterField.FieldType)) scatterField = null;

                bool terrain = terrainField != null && terrainChanged != null;
                bool scatter = scatterField != null && scatterColors != null && scatterBuffer != null && scatterList != null
                               && scatterStart != null && scatterCleanup != null;
                summary = "Parallax found, terrain refresh " + (terrain ? "ok" : "missing") + ", scatter recolour " + (scatter ? "ok" : "missing");
                if (!terrain || !scatter)
                    AtmospheresLog.Warn("atmospheres-parallax", "The atmospheres cannot reach all of Parallax (" + summary + ", missing " + link.MissingText() + "); what is missing is left out.");
            }
            catch (Exception e)
            {
                terrainField = null;
                scatterField = null;
                summary = "Parallax found, lookup failed (" + CompatibilityLog.Reason(e) + ")";
                AtmospheresLog.Failed("atmospheres-parallax", "The atmospheres cannot reach Parallax", e);
            }
        }

        // Tells Parallax's terrain that the quad's mesh changed. False where the quad has no terrain data or the call fails. In the
        // source of 1.0.6 the call drops the extra mesh of the quad, which is made again from the quad's mesh.
        internal static bool TerrainChanged(PQ quad)
        {
            if (!Terrain) return false;
            try
            {
                IDictionary quads = Quads(terrainField);
                if (quads == null || !quads.Contains(quad)) return false;
                terrainChanged.Invoke(quads[quad], null);
                TerrainFailures.Succeeded();
                return true;
            }
            catch (Exception e)
            {
                if (TerrainFailures.Failed())
                    AtmospheresLog.Failed("atmospheres-parallax-terrain", "Parallax's terrain refresh failed " + FailuresToSwitchOff + " times in a row and is left out", e);
                return false;
            }
        }

        // Whether Parallax has scatter data for the quad.
        internal static bool HasScatters(PQ quad)
        {
            if (!Scatters) return false;
            IDictionary quads = Quads(scatterField);
            return quads != null && quads.Contains(quad);
        }

        // Gives the scatters of the quad the painted colours: the copy and the buffer of the colours; with regenerate, each scatter on
        // the quad is made again from the buffer. False where the quad has none or the call fails.
        internal static bool Recolour(PQ quad, Color32[] painted, bool regenerate)
        {
            if (!HasScatters(quad)) return false;
            try
            {
                object data = Quads(scatterField)[quad];
                Color[] colors = scatterColors.GetValue(data) as Color[];
                ComputeBuffer buffer = scatterBuffer.GetValue(data) as ComputeBuffer;
                if (colors == null || buffer == null || colors.Length != painted.Length) return false;
                for (int i = 0; i < colors.Length; i++) colors[i] = painted[i];
                buffer.SetData(colors);
                if (regenerate)
                {
                    IList scatters = scatterList.GetValue(data) as IList;
                    if (scatters != null)
                    {
                        var copy = new object[scatters.Count];
                        scatters.CopyTo(copy, 0);
                        foreach (object scatter in copy)
                        {
                            scatterCleanup.Invoke(scatter, null);
                            scatterStart.Invoke(scatter, null);
                        }
                    }
                }
                ScatterFailures.Succeeded();
                return true;
            }
            catch (Exception e)
            {
                if (ScatterFailures.Failed())
                    AtmospheresLog.Failed("atmospheres-parallax-scatters", "Parallax's scatter recolour failed " + FailuresToSwitchOff + " times in a row and is left out", e);
                return false;
            }
        }
    }
}
