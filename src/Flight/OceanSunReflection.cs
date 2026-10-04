using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The sun's own glint on the sea, where the raytraced reflections do not carry it:
    // Scatterer's (OceanNode.InitOceanMaterial sets it on the material once from the
    // node's sunReflectionStrength; ForeignAssumptions.ScattererOceanSun) at the player's
    // percentage (Sun reflection), and none while the ocean takes the raytraced
    // reflections, which carry the sun's path themselves -- with both, a flat round glint
    // at the horizon lay over the path (the player, 2026-10-03). KSP's own sea, without
    // Scatterer, the same: its shader's highlight (_SpecColor) off while the raytracing
    // traces that sea's reflections, as water with the sun's disc (RtCamera.StockSeaTraced;
    // RtCommon.hlsli, IsStockWater), its colour put back otherwise. The materials are set
    // again every second, as the seas are made anew with a scene or a planet; the
    // node's own value, its configuration's, stays as it is, so 100 % is Scatterer's.
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class OceanSunReflection : MonoBehaviour
    {
        private const string Property = "sunReflectionStrength";
        private static readonly int PropertyId = Shader.PropertyToID(Property);
        private static readonly int SpecColorId = Shader.PropertyToID("_SpecColor");
        // Scatterer's ocean multiplies all its reflections with its node's reflectionColor
        // but its own sun glint; the raytraced glints stand in for that glint, so the
        // reflections' shader takes it back from them (ReDefinitionRtCapture.shader,
        // fragOceanCompose).
        private static readonly int ReflectionColorId = Shader.PropertyToID("_ReDefinitionOceanReflectionColor");

        // KSP's sea materials whose highlight is off, with their own colour.
        private readonly Dictionary<Material, Color> stockSpecular = new Dictionary<Material, Color>();

        private IEnumerator Start()
        {
            Type node = ForeignAssumptions.ScattererOceanSun.Holds ? TypeLookup.Find("Scatterer.OceanNode") : null;
            FieldInfo material = node != null ? node.GetField("m_oceanMaterial", TypeLookup.Any) : null;
            FieldInfo strength = node != null ? node.GetField(Property, TypeLookup.Any) : null;
            FieldInfo reflectionColor = node != null ? node.GetField("reflectionColor", TypeLookup.Any) : null;
            var wait = new WaitForSeconds(1f);
            while (true)
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                if (material != null && strength != null)
                {
                    float percent = addon != null ? addon.Live.SunReflection : 100f;
                    if (RtKspMods.OceanReadsHeldReflections()) percent = 0f;
                    foreach (UnityEngine.Object found in FindObjectsOfType(node))
                    {
                        Material ocean = material.GetValue(found) as Material;
                        if (ocean == null || !ocean.HasProperty(PropertyId)) continue;
                        float wanted = (float)strength.GetValue(found) * Mathf.Max(percent, 0f) / 100f;
                        if (ocean.GetFloat(PropertyId) != wanted) ocean.SetFloat(PropertyId, wanted);
                        if (reflectionColor != null && reflectionColor.GetValue(found) is Vector3 colour)
                            Shader.SetGlobalVector(ReflectionColorId, new Vector4(colour.x, colour.y, colour.z, 1f));
                    }
                }
                StockSea(RtCamera.StockSeaTraced);
                yield return wait;
            }
        }

        private void OnDestroy()
        {
            StockSea(false);
        }

        // KSP's sea shader's highlight off (`off`) or back as it was.
        private void StockSea(bool off)
        {
            if (!off)
            {
                foreach (KeyValuePair<Material, Color> entry in stockSpecular)
                    if (entry.Key != null) entry.Key.SetColor(SpecColorId, entry.Value);
                stockSpecular.Clear();
                return;
            }
            var seen = new HashSet<string>();
            foreach (PQS pqs in FindObjectsOfType<PQS>())
            {
                if (pqs == null) continue;
                foreach (Material sea in new[] { pqs.surfaceMaterial, pqs.fallbackMaterial })
                    Darken(sea, seen);
            }
            // The quads draw with their own copies where a mod gave them one.
            if (stockSpecular.Count == 0)
                foreach (MeshRenderer renderer in FindObjectsOfType<MeshRenderer>())
                    if (renderer != null && renderer.gameObject.name.Contains("Ocean"))
                        Darken(renderer.sharedMaterial, seen);
            if (stockSpecular.Count == 0 && !reported)
            {
                reported = true;
                Debug.Log(Log.Tag + " KSP's sea not found to turn its highlight off; its materials' shaders: "
                          + string.Join(", ", new List<string>(seen).ToArray()) + ".");
            }
        }

        private bool reported;

        private void Darken(Material sea, HashSet<string> seen)
        {
            if (sea == null || sea.shader == null) return;
            seen.Add(sea.shader.name);
            if (!sea.shader.name.StartsWith("Terrain/PQS/Ocean", StringComparison.Ordinal) || !sea.HasProperty(SpecColorId)
                || stockSpecular.ContainsKey(sea))
                return;
            stockSpecular[sea] = sea.GetColor(SpecColorId);
            sea.SetColor(SpecColorId, Color.black);
            Debug.Log(Log.Tag + " KSP's sea (" + sea.shader.name + ") without its highlight while the raytracing mirrors the sun.");
        }
    }
}
