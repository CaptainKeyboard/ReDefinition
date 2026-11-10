using System;
using System.Collections;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // What the seasons ask of Parallax Continued when the snow on a built quad changes, by reflection and guarded: a symbol that is
    // missing is logged once and that part is left out. Parallax reads the vertex colours of a quad when it makes its own data for
    // it. A repaint of the mesh has to be announced:
    //
    //   Terrain: PQSMod_Parallax.terrainQuadData[quad].OnQuadNormalUpdate() is the call Parallax makes itself after the mesh of a
    //   quad changed. In the build checked, it reads the vertices, normals and colours of the quad's mesh again for the
    //   subdivided mesh. In the source of 1.0.6, it drops the extra mesh of the quad; that is made again from the quad's mesh.
    //
    //   Scatters: ScatterComponent.scatterQuadData[quad] keeps a copy of the colours (colors) and a compute buffer of them
    //   (sourceColorsBuffer). The scatters take their colour from the buffer when they are made. After the buffer is set, each
    //   ScatterData is cleaned up and started again: the sequence Parallax's own ReinitializeScatters runs.
    internal static class ParallaxLink
    {
        private static bool looked;
        private static string summary = "Parallax not installed";
        private static IDictionary terrainQuads;
        private static MethodInfo terrainChanged;
        private static IDictionary scatterQuads;
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
                return terrainQuads != null && terrainChanged != null;
            }
        }

        internal static bool Scatters
        {
            get
            {
                Look();
                return scatterQuads != null && scatterColors != null && scatterBuffer != null && scatterList != null
                       && scatterStart != null && scatterCleanup != null;
            }
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            try
            {
                Type terrainMod = TypeLookup.Find("Parallax.PQSMod_Parallax");
                if (terrainMod == null) return;
                string terrain = "terrain refresh missing";
                string scatter = "scatter recolour missing";
                FieldInfo terrainField = terrainMod.GetField("terrainQuadData", BindingFlags.Public | BindingFlags.Static);
                Type terrainData = TypeLookup.Find("Parallax.TerrainShaderQuadData");
                MethodInfo changed = terrainData != null ? terrainData.GetMethod("OnQuadNormalUpdate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) : null;
                IDictionary quads = terrainField != null ? terrainField.GetValue(null) as IDictionary : null;
                if (quads != null && changed != null)
                {
                    terrainQuads = quads;
                    terrainChanged = changed;
                    terrain = "terrain refresh ok";
                }

                Type scatterComponent = TypeLookup.Find("Parallax.ScatterComponent");
                Type quadData = TypeLookup.Find("Parallax.ScatterSystemQuadData");
                Type scatterData = TypeLookup.Find("Parallax.ScatterData");
                FieldInfo scatterField = scatterComponent != null ? scatterComponent.GetField("scatterQuadData", BindingFlags.Public | BindingFlags.Static) : null;
                IDictionary scatterDict = scatterField != null ? scatterField.GetValue(null) as IDictionary : null;
                FieldInfo colors = quadData != null ? quadData.GetField("colors", BindingFlags.Public | BindingFlags.Instance) : null;
                FieldInfo buffer = quadData != null ? quadData.GetField("sourceColorsBuffer", BindingFlags.Public | BindingFlags.Instance) : null;
                FieldInfo list = quadData != null ? quadData.GetField("quadScatters", BindingFlags.Public | BindingFlags.Instance) : null;
                MethodInfo start = scatterData != null ? scatterData.GetMethod("Start", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) : null;
                MethodInfo cleanup = scatterData != null ? scatterData.GetMethod("Cleanup", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) : null;
                if (scatterDict != null && colors != null && buffer != null && list != null && start != null && cleanup != null
                    && colors.FieldType == typeof(Color[]) && buffer.FieldType == typeof(ComputeBuffer))
                {
                    scatterQuads = scatterDict;
                    scatterColors = colors;
                    scatterBuffer = buffer;
                    scatterList = list;
                    scatterStart = start;
                    scatterCleanup = cleanup;
                    scatter = "scatter recolour ok";
                }
                summary = "Parallax found, " + terrain + ", " + scatter;
                if (terrainQuads == null || scatterQuads == null)
                    CompatibilityLog.Warn("seasons-parallax", "The seasons cannot reach all of Parallax (" + summary + "); what is missing is left out.");
            }
            catch (Exception e)
            {
                terrainQuads = null;
                scatterQuads = null;
                summary = "Parallax found, lookup failed (" + CompatibilityLog.Reason(e) + ")";
                CompatibilityLog.Warn("seasons-parallax", "The seasons cannot reach Parallax (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Tells Parallax's terrain that the quad's mesh changed. False where the quad has no terrain data or the call fails.
        internal static bool TerrainChanged(PQ quad)
        {
            if (!Terrain) return false;
            try
            {
                if (!terrainQuads.Contains(quad)) return false;
                terrainChanged.Invoke(terrainQuads[quad], null);
                return true;
            }
            catch (Exception e)
            {
                terrainQuads = null;
                CompatibilityLog.Warn("seasons-parallax-terrain", "Parallax's terrain refresh failed and is left out (" + CompatibilityLog.Reason(e) + ").");
                return false;
            }
        }

        // Whether Parallax has scatter data for the quad.
        internal static bool HasScatters(PQ quad)
        {
            return Scatters && scatterQuads.Contains(quad);
        }

        // Gives the scatters of the quad the painted colours: the copy and the buffer of the colours; with regenerate, each scatter on
        // the quad is made again from the buffer. False where the quad has none or the call fails.
        internal static bool Recolour(PQ quad, Color32[] painted, bool regenerate)
        {
            if (!HasScatters(quad)) return false;
            try
            {
                object data = scatterQuads[quad];
                Color[] colors = scatterColors.GetValue(data) as Color[];
                ComputeBuffer buffer = scatterBuffer.GetValue(data) as ComputeBuffer;
                if (colors == null || buffer == null || colors.Length != painted.Length) return false;
                for (int i = 0; i < colors.Length; i++) colors[i] = painted[i];
                buffer.SetData(colors);
                if (!regenerate) return true;
                IList scatters = scatterList.GetValue(data) as IList;
                if (scatters == null) return true;
                var copy = new object[scatters.Count];
                scatters.CopyTo(copy, 0);
                foreach (object scatter in copy)
                {
                    scatterCleanup.Invoke(scatter, null);
                    scatterStart.Invoke(scatter, null);
                }
                return true;
            }
            catch (Exception e)
            {
                scatterQuads = null;
                CompatibilityLog.Warn("seasons-parallax-scatters", "Parallax's scatter recolour failed and is left out (" + CompatibilityLog.Reason(e) + ").");
                return false;
            }
        }
    }
}
