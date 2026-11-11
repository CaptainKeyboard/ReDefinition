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
    // texture again, timeFade* back to 1 and CLOUD_SHADOW_CASTER_OFF on). BindingTracker decides when to bind again: after
    // CloudsManager.onApply, when the material is another object, and every second by reading the binding back. Layers with timeSettings
    // are not fought.
    internal sealed class EveBinding
    {
        private readonly List<EveLayer> layers = new List<EveLayer>();
        private bool applySeen;

        internal IList<EveLayer> Layers
        {
            get { return layers; }
        }

        // The watchdog interval used for new layers (set from B09's measurement of how long EVE takes to rebuild; default 1 s).
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
        }

        // One pass over the layers: those whose binding is gone are bound again to the set the chooser names. Returns the number of
        // layers bound in this pass. choose gives the paint set of a layer (null where the layer cannot be laid out).
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
                bool materialChanged = false;
                if (apply || layer.Tracker.State == BindState.Bound)
                {
                    EveLayerAccess.ReadMaterials(layer);
                    materialChanged = layer.BoundMain != null && (!ReferenceEquals(layer.Main, layer.BoundMain)
                        || !ReferenceEquals(layer.Probe, layer.BoundProbe) || !ReferenceEquals(layer.Shadow2D, layer.BoundShadow2D));
                }
                PaintSet set = choose(layer);
                if (set == null) continue;
                EveLayer captured = layer;
                bool needed = layer.Tracker.Observe(now, apply, materialChanged, () => ReadBack(captured, set));
                if (!needed) continue;
                try
                {
                    if (!Bind(layer, set)) continue;
                    layer.Tracker.Bound(now);
                    bound++;
                    if (log != null)
                    {
                        log("bound '" + layer.Name + "' (losses " + layer.Tracker.Losses + ", rebinds " + layer.Tracker.Rebinds
                            + (layer.Tracker.Rebinds > 0 ? ", last latency " + layer.Tracker.LastLatencyS.ToString("0.00") + " s" : "") + ")");
                    }
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

        // Reads the binding back: our targets are the textures of the properties, the alpha mask is as set, and a dependent layer's
        // shadow caster is switched on. False where anything is not as the binding left it.
        internal static bool ReadBack(EveLayer layer, PaintSet set)
        {
            if (!Intact(layer.Main, set, layer.ReceivesShadowsFrom.Length > 0)) return false;
            if (layer.Probe != null && !Intact(layer.Probe, set, false)) return false;
            if (layer.Shadow2D != null && !Intact(layer.Shadow2D, set, false)) return false;
            return true;
        }

        private static bool Intact(Material m, PaintSet set, bool dependent)
        {
            if (m == null) return false;
            if (!ReferenceEquals(m.GetTexture("CloudCoverage"), set.Coverage)) return false;
            if (!ReferenceEquals(m.GetTexture("CloudType"), set.Type)) return false;
            if (!m.IsKeywordEnabled("ALPHAMAP_1") || !m.IsKeywordEnabled("ALPHAMAP_2")) return false;
            if (m.GetFloat("useAlphaMask1") < 0.5f || m.GetFloat("useAlphaMask2") < 0.5f) return false;
            if (dependent && m.IsKeywordEnabled("CLOUD_SHADOW_CASTER_OFF")) return false;
            return true;
        }

        // Forces a loss for a test: EVE's Apply would have done this.
        internal static void Wipe(EveLayer layer)
        {
            if (layer.Main != null)
            {
                layer.Main.SetTexture("CloudCoverage", Texture2D.whiteTexture);
                layer.Main.SetTexture("CloudType", Texture2D.blackTexture);
            }
        }

        internal void Clear()
        {
            foreach (EveLayer l in layers) Forget(l);
            layers.Clear();
        }
    }
}
