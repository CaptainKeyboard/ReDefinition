using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Raytracing
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
    // again every second; the seas themselves are looked for again every RescanSeconds
    // and when the main body changes, as they are made anew with a scene or a planet. The
    // node's own value, its configuration's, stays as it is, so 100 % is Scatterer's.
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class SeaHighlights : MonoBehaviour
    {
        // Scatterer's glint property and how often the seas are looked for again; the shaders' properties set.
        private const string Property = "sunReflectionStrength";
        private const float RescanSeconds = 10f;
        private static readonly int PropertyId = Shader.PropertyToID(Property);
        private static readonly int SpecColorId = Shader.PropertyToID("_SpecColor");

        // Scatterer's OceanNode type, its material and strength fields, and the nodes found.
        private Type node;
        private FieldInfo nodeMaterial, nodeStrength;
        private UnityEngine.Object[] nodes = new UnityEngine.Object[0];
        // KSP's sea materials, and those whose highlight is off with their own colour.
        private readonly List<Material> stockSeas = new List<Material>();
        private readonly Dictionary<Material, Color> stockSpecular = new Dictionary<Material, Color>();
        private float rescanAt;
        private CelestialBody scannedBody;
        private bool reported;

        // Reads Scatterer's ocean members where its code is the one checked, then every second: the seas looked for
        // again when due, the glint and the stock highlight set; stops for the scene after a failure.
        private IEnumerator Start()
        {
            node = ForeignAssumptions.ScattererOceanSun.Holds ? TypeLookup.Find("Scatterer.OceanNode") : null;
            nodeMaterial = node != null ? node.GetField("m_oceanMaterial", TypeLookup.Any) : null;
            nodeStrength = node != null ? node.GetField(Property, TypeLookup.Any) : null;
            var wait = new WaitForSeconds(1f);
            bool failed = false;
            while (!failed)
            {
                try
                {
                    if (Time.realtimeSinceStartup >= rescanAt || scannedBody != RtKspMods.SceneBody()) Rescan();
                    SetScattererGlint();
                    StockSea(RtCamera.StockSeaTraced);
                }
                catch (Exception e)
                {
                    // Another mod's version: the seas as they are, the stock highlight back.
                    Debug.LogWarning(Log.Tag + " The sea's highlights stop: " + e.Message);
                    StockSea(false);
                    failed = true;
                    continue;
                }
                yield return wait;
            }
        }

        // KSP's sea gets its highlight back as the scene goes.
        private void OnDestroy()
        {
            StockSea(false);
        }

        // Looks for Scatterer's ocean nodes and KSP's sea materials anew.
        private void Rescan()
        {
            rescanAt = Time.realtimeSinceStartup + RescanSeconds;
            scannedBody = RtKspMods.SceneBody();
            nodes = node != null && nodeMaterial != null && nodeStrength != null ? FindObjectsOfType(node) : new UnityEngine.Object[0];
            stockSeas.Clear();
            foreach (PQS pqs in FindObjectsOfType<PQS>())
            {
                if (pqs == null) continue;
                AddStockSea(pqs.surfaceMaterial);
                AddStockSea(pqs.fallbackMaterial);
            }
            // The quads draw with their own copies where a mod gave them one: looked for among
            // all renderers only where the body has a sea, not every ten seconds over every
            // body without one, in every scene.
            if (stockSeas.Count == 0 && scannedBody != null && scannedBody.ocean)
                foreach (MeshRenderer renderer in FindObjectsOfType<MeshRenderer>())
                    if (renderer != null && renderer.gameObject.name.Contains("Ocean"))
                        AddStockSea(renderer.sharedMaterial);
        }

        // A material of KSP's sea shader with a highlight colour, kept once.
        private void AddStockSea(Material sea)
        {
            if (sea != null && sea.shader != null && sea.shader.name.StartsWith("Terrain/PQS/Ocean", StringComparison.Ordinal)
                && sea.HasProperty(SpecColorId) && !stockSeas.Contains(sea))
                stockSeas.Add(sea);
        }

        // Scatterer's glint on each ocean node's material: its configured strength at the player's percentage, none
        // while the ocean takes the raytraced reflections.
        private void SetScattererGlint()
        {
            if (nodes.Length == 0) return;
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            float percent = RtKspMods.OceanReadsHeldReflections() ? 0f : addon != null ? addon.Live.SunReflection : 100f;
            foreach (UnityEngine.Object found in nodes)
            {
                if (found == null) continue;
                Material ocean = nodeMaterial.GetValue(found) as Material;
                // Without HasProperty, which asks the shader for the property (Unity:
                // "Checks if material's shader has a property of a given name") while
                // Scatterer only sets it on the material (OceanNode.InitOceanMaterial);
                // its name is checked in Scatterer's code (ScattererOceanSun).
                if (ocean == null) continue;
                float wanted = (float)nodeStrength.GetValue(found) * Mathf.Max(percent, 0f) / 100f * OwnSettings.SunReflectionAt100;
                if (ocean.GetFloat(PropertyId) != wanted) ocean.SetFloat(PropertyId, wanted);
            }
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
            int darkened = 0;
            foreach (Material sea in stockSeas)
            {
                if (sea == null || stockSpecular.ContainsKey(sea)) continue;
                stockSpecular[sea] = sea.GetColor(SpecColorId);
                sea.SetColor(SpecColorId, Color.black);
                darkened++;
            }
            if (darkened > 0)
                Debug.Log(Log.Tag + " KSP's sea without its highlight while the raytracing mirrors the sun (" + darkened
                          + " materials).");
            else if (stockSpecular.Count == 0 && !reported)
            {
                reported = true;
                Debug.Log(Log.Tag + " KSP's sea not found to turn its highlight off.");
            }
        }
    }
}
