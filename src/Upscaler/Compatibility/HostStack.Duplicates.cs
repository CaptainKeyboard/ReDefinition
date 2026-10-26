using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The other mods' antialiasing without a second one on the same camera, while
    // ReDefinition neither upscales nor generates frames (OwnSettings.OneAntialiasing).
    // Measured without ReDefinition, 2026-10-23 (an AaSurvey run, TUFX's Default
    // profiles): in the space centre, in flight and in IVA the scene camera ran
    // Scatterer's TAA (JitterAndResolve), Scatterer's SMAA and TUFX's TAA at once, and in
    // the map view the scaled space camera Scatterer's TAA and TUFX's SMAA. None of the
    // mods looks at another's (Scatterer, TUFX and Deferred decompiled): two temporal
    // filters averaged the frames twice over one jitter, and PostProcessing's read the
    // motion vectors of Scatterer's replacement shader.
    //
    // Where a Scatterer TAA resolves on a camera -- its EffectiveRole JitterAndResolve:
    // the near camera, and the scaled space camera in the map view -- that camera's
    // PostProcessLayer is set to antialiasing None, TUFX's profile left as it is, and
    // given its mode back once that TAA no longer resolves there, or this stops.
    // Scatterer's SMAA stays: it smooths alone when Scatterer pauses its TAA under
    // disableTaaBelowFrameRateThreshold (TemporalAntiAliasing.OnPreCull). Where no
    // Scatterer TAA resolves -- the editors, the tracking station, the main menu --
    // nothing changes.
    internal static partial class HostStack
    {
        // Post Processing v2's layer, the component TUFX's antialiasing runs on.
        private const string PostProcessLayerTypeName = "UnityEngine.Rendering.PostProcessing.PostProcessLayer";

        // The layers set to None here, with the mode each had.
        private static readonly Dictionary<Component, object> duplicateLayers = new Dictionary<Component, object>();
        private static readonly List<Component> resolvingLayers = new List<Component>();
        private static readonly List<Component> givenBack = new List<Component>();
        private static float nextDuplicateCheck;
        private static MethodInfo effectiveRole;
        private static bool duplicatesFailed;

        // Whether a layer is set to None here at the moment.
        public static bool DuplicatesTaken
        {
            get { return duplicateLayers.Count > 0; }
        }

        // Every frame from the add-on: wanted, the check once a second (or at once with
        // now); not wanted, every layer given back.
        internal static void SyncDuplicates(bool wanted, bool now)
        {
            if (!wanted)
            {
                if (duplicateLayers.Count > 0) GiveBackDuplicates("taken over or switched off");
                return;
            }
            if (duplicatesFailed || (!now && Time.unscaledTime < nextDuplicateCheck)) return;
            nextDuplicateCheck = Time.unscaledTime + ReassertInterval;
            try
            {
                TakeDuplicates();
            }
            catch (Exception e)
            {
                duplicatesFailed = true;
                CompatibilityLog.Warn("aa-duplicates", "The mods' second antialiasing on one camera is left as it is ("
                                                       + CompatibilityLog.Reason(e) + ").");
                GiveBackDuplicates("failed");
            }
        }

        // Sets None on the PostProcessLayer of every camera a Scatterer TAA resolves on, and gives the layers back where
        // it no longer does.
        private static void TakeDuplicates()
        {
            Type taaType = TypeLookup.Find(ScattererTaaTypeName);
            Type layerType = TypeLookup.Find(PostProcessLayerTypeName);
            resolvingLayers.Clear();
            if (taaType != null && layerType != null && ForeignAssumptions.ScattererTemporalAa.Holds)
            {
                if (effectiveRole == null)
                    effectiveRole = taaType.GetMethod("EffectiveRole", Any, null, Type.EmptyTypes, null);
                foreach (Component taa in ScattererTaaComponents())
                {
                    if (taa == null || !IsEnabled(taa) || effectiveRole == null) continue;
                    object role = effectiveRole.Invoke(taa, null);
                    if (role == null || role.ToString() != "JitterAndResolve") continue;
                    Component layer = taa.GetComponent(layerType);
                    if (layer != null && !resolvingLayers.Contains(layer)) resolvingLayers.Add(layer);
                }
            }

            // Given back where Scatterer's TAA no longer resolves, or the layer is gone.
            givenBack.Clear();
            foreach (KeyValuePair<Component, object> entry in duplicateLayers)
                if (entry.Key == null || !resolvingLayers.Contains(entry.Key)) givenBack.Add(entry.Key);
            foreach (Component layer in givenBack)
            {
                object before = duplicateLayers[layer];
                duplicateLayers.Remove(layer);
                if (layer != null) GiveBack(layer, before);
            }

            int taken = 0;
            foreach (Component layer in resolvingLayers)
            {
                object mode;
                if (!TryGet(layer, layer.GetType(), "antialiasingMode", out mode) || mode == null) continue;
                bool none = mode.ToString() == "None";
                // Kept anew where TUFX handed the layer another mode since (a profile
                // applied, its window): that is the one given back.
                if (!none || !duplicateLayers.ContainsKey(layer)) duplicateLayers[layer] = mode;
                if (none) continue;
                object noneValue = Enum.Parse(mode.GetType(), "None");
                if (TrySet(layer, layer.GetType(), "antialiasingMode", noneValue)) taken++;
            }
            if (taken > 0 || givenBack.Count > 0)
                Debug.Log(Log.Tag + " One antialiasing per camera: TUFX's taken on " + taken + " camera(s) where Scatterer's TAA"
                          + " resolves, given back on " + givenBack.Count + ".");
        }

        // Gives a layer its mode back, where it still holds None.
        private static void GiveBack(Component layer, object before)
        {
            object now;
            // Only where it still holds the None set here.
            if (before == null || !TryGet(layer, layer.GetType(), "antialiasingMode", out now) || now == null
                || now.ToString() != "None")
                return;
            TrySet(layer, layer.GetType(), "antialiasingMode", before);
        }

        // Gives every layer set to None here its mode back and logs `why`.
        private static void GiveBackDuplicates(string why)
        {
            int count = 0;
            foreach (KeyValuePair<Component, object> entry in duplicateLayers)
            {
                if (entry.Key == null) continue;
                GiveBack(entry.Key, entry.Value);
                count++;
            }
            duplicateLayers.Clear();
            if (count > 0) Debug.Log(Log.Tag + " One antialiasing per camera: TUFX's given back on " + count + " camera(s) (" + why + ").");
        }
    }
}
