using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The binding of the painted maps to EVE's layers (brief C23, item 2), done the way EVE's own painter does it
    // (Painting/CloudsPainter.cs:221-262): on the layer's main material, its reflection probe material and the 2D layer's shadow material
    // the render textures are CloudCoverage and CloudType, with the keywords ALPHAMAP_1 and ALPHAMAP_2, alphaMask1/2 = (1, 0, 0, 0)
    // and useAlphaMask1/2 = 1. A layer that receives its shadows from a painted layer gets the caster's coverage target through
    // SetShadowCasterTextureParams(rt, true).
    //
    // EVE discards all of it in every CloudsManager.Apply, ApplyShaderParams and SetShaderParams (new materials, or the wrapper's own
    // texture again, timeFade* back to 1 and CLOUD_SHADOW_CASTER_OFF on).
    //
    // The binding cannot be read back. EVE's raymarch shader declares no properties (RECON P9: m_PropInfo is empty), so
    // Material.GetTexture and GetFloat of CloudCoverage, CloudType or useAlphaMask1 return null or 0 and log an error whatever was set.
    // What the binding knows about itself instead: the identity of the three materials and of the paint set it was made with
    // (EVE's Apply replaces the materials in the same frame, RECON P9), the keywords (IsKeywordEnabled is not refused for an undeclared
    // name) and its own record of what it set. BindingTracker decides when to bind again: after CloudsManager.onApply, when a material or
    // the set is another object, when the keywords EVE rewrites are not as left, and, with nothing seen, by setting the same textures
    // again every watchdog interval (a lost binding on the same material, as ApplyShaderParams makes it, leaves no other trace).
    // Layers with timeSettings are not fought.
    internal sealed class EveBinding
    {
        private readonly List<EveLayer> layers = new List<EveLayer>();
        private bool applySeen;

        internal IList<EveLayer> Layers
        {
            get { return layers; }
        }

        // The watchdog interval used for the layers (the tuning key eve.watchdogS; default 1 s).
        internal double WatchdogIntervalS = BindingTracker.WatchdogIntervalS;

        // Counters of the whole binding for the log and the series rows.
        internal int TotalLosses
        {
            get
            {
                int n = 0;
                foreach (EveLayer l in layers) n += l.Tracker.Losses;
                return n;
            }
        }

        internal int TotalRebinds
        {
            get
            {
                int n = 0;
                foreach (EveLayer l in layers) n += l.Tracker.Rebinds;
                return n;
            }
        }

        internal int TotalReasserts
        {
            get
            {
                int n = 0;
                foreach (EveLayer l in layers) n += l.Tracker.Reasserts;
                return n;
            }
        }

        internal double MaxLatencyS
        {
            get
            {
                double m = 0;
                foreach (EveLayer l in layers) m = Math.Max(m, l.Tracker.MaxLatencyS);
                return m;
            }
        }

        // Called from CloudsManager.onApply: every layer must be looked at again at once.
        internal void OnApply()
        {
            applySeen = true;
        }

        // Replaces the list by a fresh scan (handles that stay keep their state).
        internal void SetLayers(IList<EveLayer> found)
        {
            foreach (EveLayer old in layers)
            {
                if (!found.Contains(old)) Forget(old);
            }
            layers.Clear();
            layers.AddRange(found);
        }

        private static void Forget(EveLayer layer)
        {
            layer.Tracker.Reset();
            layer.BoundMain = layer.BoundProbe = layer.BoundShadow2D = null;
            layer.BoundSet = null;
        }

        // One pass over the layers: those whose binding is gone are bound again to the set the chooser names, and the others set it
        // again when the watchdog's interval is over. Returns the number of layers bound or set again in this pass. choose gives the paint
        // set of a layer (null where the layer cannot be laid out).
        internal int Tick(double now, Func<EveLayer, PaintSet> choose, Action<string> log)
        {
            int bound = 0;
            bool apply = applySeen;
            applySeen = false;
            foreach (EveLayer layer in layers)
            {
                if (!string.IsNullOrEmpty(layer.LeftReason) && layer.Tracker.State != BindState.Left)
                {
                    layer.Tracker.Leave();
                    if (log != null) log("layer '" + layer.Name + "' is left to EVE (" + layer.LeftReason + ")");
                }
                if (layer.Tracker.State == BindState.Left) continue;
                layer.Tracker.IntervalS = WatchdogIntervalS;
                PaintSet set = choose(layer);
                bool identity = false;
                if (apply || layer.Tracker.State == BindState.Bound)
                {
                    EveLayerAccess.ReadMaterials(layer);
                    identity = IdentityChanged(layer, set);
                }
                if (set == null)
                {
                    // Nothing to bind yet (the layout is not known, or the set is not painted): a loss is still counted when it happens.
                    if (identity || apply) layer.Tracker.Observe(now, apply, identity, null);
                    continue;
                }
                if (layer.SentinelCheck == null)
                {
                    EveLayer self = layer;
                    layer.SentinelCheck = () => Sentinel(self);
                }
                BindAction action = layer.Tracker.Observe(now, apply, identity, layer.SentinelCheck);
                if (action == BindAction.None) continue;
                try
                {
                    if (!Bind(layer, set)) continue;
                    if (action == BindAction.Bind)
                    {
                        layer.Tracker.Bound(now);
                        if (log != null)
                        {
                            log("bound '" + layer.Name + "' (losses " + layer.Tracker.Losses + ", rebinds " + layer.Tracker.Rebinds
                                + (layer.Tracker.Rebinds > 0 ? ", last latency " + layer.Tracker.LastLatencyS.ToString("0.00") + " s" : "") + ")");
                        }
                    }
                    else
                    {
                        layer.Tracker.Reasserted(now);
                    }
                    bound++;
                }
                catch (Exception e)
                {
                    layer.LeftReason = "binding failed: " + CompatibilityLog.Reason(e);
                    layer.Tracker.Leave();
                    AtmospheresLog.Failed("atmospheres-eve-bind", "binding the maps to EVE layer '" + layer.Name + "' failed; it is left to EVE", e);
                }
            }
            if (bound > 0) BindShadowCasters(choose);
            return bound;
        }

        // Whether a material the layer holds is not the one the maps were bound on, or the set it should carry is another one.
        internal static bool IdentityChanged(EveLayer layer, PaintSet set)
        {
            if (layer.BoundMain == null) return false;
            if (!ReferenceEquals(layer.Main, layer.BoundMain) || !ReferenceEquals(layer.Probe, layer.BoundProbe)
                || !ReferenceEquals(layer.Shadow2D, layer.BoundShadow2D)) return true;
            return set != null && layer.BoundSet != null && !ReferenceEquals(layer.BoundSet, set);
        }

        // Makes the binding on all three materials. False where the layer has no main material yet.
        internal static bool Bind(EveLayer layer, PaintSet set)
        {
            EveLayerAccess.ReadMaterials(layer);
            if (layer.Main == null) return false;
            BindMaterial(layer.Main, set);
            if (layer.Probe != null) BindMaterial(layer.Probe, set);
            if (layer.Shadow2D != null) BindMaterial(layer.Shadow2D, set);
            layer.BoundMain = layer.Main;
            layer.BoundProbe = layer.Probe;
            layer.BoundShadow2D = layer.Shadow2D;
            layer.BoundSet = set;
            return true;
        }

        private static void BindMaterial(Material m, PaintSet set)
        {
            m.EnableKeyword("ALPHAMAP_1");
            m.SetVector("alphaMask1", new Vector4(1f, 0f, 0f, 0f));
            m.SetFloat("useAlphaMask1", 1f);
            m.SetTexture("CloudCoverage", set.Coverage);
            m.EnableKeyword("ALPHAMAP_2");
            m.SetVector("alphaMask2", new Vector4(1f, 0f, 0f, 0f));
            m.SetFloat("useAlphaMask2", 1f);
            m.SetTexture("CloudType", set.Type);
        }

        // Layers that take their shadow from a painted layer get that layer's coverage target as the caster.
        private void BindShadowCasters(Func<EveLayer, PaintSet> choose)
        {
            foreach (EveLayer dependent in layers)
            {
                if (string.IsNullOrEmpty(dependent.ReceivesShadowsFrom) || dependent.Tracker.State == BindState.Left) continue;
                foreach (EveLayer caster in layers)
                {
                    if (!string.Equals(caster.Name, dependent.ReceivesShadowsFrom, StringComparison.Ordinal)) continue;
                    PaintSet set = choose(caster);
                    if (set != null && caster.Tracker.State == BindState.Bound) EveLayerAccess.SetShadowCaster(dependent, set.Coverage);
                }
            }
        }

        // The watchdog's sentinel: what can be known of the binding without reading a texture. The keywords EVE's ApplyTexture
        // (ALPHAMAP_1/2 off for a wrapper that is no alpha map) and SetShaderParams (CLOUD_SHADOW_CASTER_OFF on) rewrite are as the
        // binding left them. False where one is not.
        internal static bool Sentinel(EveLayer layer)
        {
            if (!KeywordsAsLeft(layer.Main, layer.ReceivesShadowsFrom.Length > 0)) return false;
            if (layer.Probe != null && !KeywordsAsLeft(layer.Probe, false)) return false;
            if (layer.Shadow2D != null && !KeywordsAsLeft(layer.Shadow2D, false)) return false;
            return true;
        }

        // The full check: identity of the materials and of the set, then the sentinel. For the probe and the tests; the frame path asks
        // the tracker, which asks the same two things.
        internal static bool Holds(EveLayer layer, PaintSet set)
        {
            if (layer.BoundMain == null || set == null) return false;
            if (IdentityChanged(layer, set)) return false;
            return Sentinel(layer);
        }

        // The keyword part of the check, on a material.
        internal static bool KeywordsAsLeft(Material m, bool dependent)
        {
            if (m == null) return false;
            if (!m.IsKeywordEnabled("ALPHAMAP_1") || !m.IsKeywordEnabled("ALPHAMAP_2")) return false;
            if (dependent && m.IsKeywordEnabled("CLOUD_SHADOW_CASTER_OFF")) return false;
            return true;
        }

        // Forces a loss for a test: what EVE's SetShaderParams does to the keywords of a material that carries a plain wrapper.
        internal static void Wipe(EveLayer layer)
        {
            if (layer.Main != null)
            {
                layer.Main.DisableKeyword("ALPHAMAP_1");
                layer.Main.DisableKeyword("ALPHAMAP_2");
            }
        }

        internal void Clear()
        {
            foreach (EveLayer l in layers) Forget(l);
            layers.Clear();
        }
    }
}
