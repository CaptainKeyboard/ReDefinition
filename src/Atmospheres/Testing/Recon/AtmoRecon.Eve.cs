using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // P9: what EVE offers the weather field. The layer list per body (name, which parts exist, the sizes of the coverage and type maps),
    // the cloud manager's onApply event, and the experiment the cloud painter depends on: a render texture bound to a layer's coverage
    // map, then a forced CloudsObject.Apply(); how many frames until the layer's material no longer carries our texture. The forced
    // Apply makes a second set of layer objects in the scene, so this runs last.
    internal sealed partial class AtmoRecon
    {
        private int applyEvents;

        private void OnApplyFired()
        {
            applyEvents++;
        }

        private IEnumerable<ProbeWait> EveLayers()
        {
            Type manager = ReconDyn.Type("Atmosphere.CloudsManager");
            Emit("P9.eve_present", manager != null);
            if (manager == null) yield break;

            List<object> layers = ReconDyn.List(ReconDyn.CallStatic(manager, "GetObjectList"));
            Emit("P9.layers_total", layers.Count);
            var json = new ReconJson().BeginObject().BeginArray("layers");
            int onKerbin = 0, complete = 0, volumes = 0, flats = 0;
            object firstKerbinVolume = null, firstKerbinObject = null;
            foreach (object layer in layers)
            {
                string name = Convert.ToString(ReconDyn.Get(layer, "Name"));
                string bodyName = Convert.ToString(ReconDyn.Get(layer, "Body"));
                object volume = ReconDyn.Get(layer, "LayerRaymarchedVolume");
                object flat = ReconDyn.Get(layer, "Layer2D");
                string coverage = MapSize(volume, "CoverageMap"), types = MapSize(volume, "CloudTypeMap");
                json.BeginObject().Text("name", name).Text("body", bodyName).Flag("volume", volume != null).Flag("layer2D", flat != null)
                    .Text("coverageMap", coverage).Text("typeMap", types).EndObject();
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(bodyName)) complete++;
                if (volume != null) volumes++;
                if (flat != null) flats++;
                if (bodyName == "Kerbin")
                {
                    onKerbin++;
                    if (volume != null && firstKerbinVolume == null)
                    {
                        firstKerbinVolume = volume;
                        firstKerbinObject = layer;
                    }
                }
                EmitText("P9.layer." + bodyName + "." + name, (volume != null ? "volume " + coverage + " " : "") + (flat != null ? "2D" : ""));
            }
            json.EndArray().EndObject();
            json.Save(DumpPath("eve-layers.json"));
            Emit("P9.layers_kerbin", onKerbin);
            Emit("P9.layer_list_complete", layers.Count > 0 && complete == layers.Count);
            Emit("P9.volume_layers", volumes);
            Emit("P9.flat_layers", flats);

            // onApply: subscribed here, so it counts only what happens from now on.
            object applyEvent = ReconDyn.GetStatic(manager, "onApply");
            var voidEvent = applyEvent as EventVoid;
            Emit("P9.onapply_event_found", voidEvent != null);
            if (voidEvent != null) voidEvent.Add(new EventVoid.OnEvent(OnApplyFired));

            if (firstKerbinVolume == null || firstKerbinObject == null)
            {
                EmitText("P9.error", "no volumetric layer on Kerbin to bind a texture to");
                yield break;
            }
            // Run1 tried the first layer only and its material did not take the texture. Every volumetric layer on Kerbin is tried now,
            // with the shader name and whether the material has the property at all; the first that takes it is used for the latency.
            var target = new RenderTexture(512, 256, 0, RenderTextureFormat.R8);
            target.Create();
            Material material = null;
            foreach (object layer in layers)
            {
                if (Convert.ToString(ReconDyn.Get(layer, "Body")) != "Kerbin") continue;
                object volume = ReconDyn.Get(layer, "LayerRaymarchedVolume");
                if (volume == null) continue;
                string layerName = Convert.ToString(ReconDyn.Get(layer, "Name"));
                Material candidate = ReconDyn.Get(volume, "RaymarchedCloudMaterial") as Material;
                if (candidate == null)
                {
                    EmitText("P9.material." + layerName, "none");
                    continue;
                }
                bool hasProperty = candidate.HasProperty("CloudCoverage");
                Texture previous = hasProperty ? candidate.GetTexture("CloudCoverage") : null;
                candidate.SetTexture("CloudCoverage", target);
                bool took = candidate.GetTexture("CloudCoverage") == target;
                candidate.SetTexture("CloudCoverage", previous);
                EmitText("P9.material." + layerName, candidate.shader.name + ", CloudCoverage " + (hasProperty ? "present" : "absent") + ", takes a render texture " + took);
                if (took && material == null)
                {
                    material = candidate;
                    firstKerbinVolume = volume;
                    firstKerbinObject = layer;
                }
            }
            if (material == null)
            {
                EmitText("P9.error", "no volumetric layer on Kerbin has a material that takes a render texture as CloudCoverage");
                Emit("P9.bind_works", false);
                target.Release();
                yield break;
            }
            material.SetTexture("CloudCoverage", target);
            bool bound = material.GetTexture("CloudCoverage") == target;
            Emit("P9.bind_works", bound);
            if (!bound)
            {
                // Without a binding that took, "our texture is gone after Apply" would be true from the first frame and say nothing.
                EmitText("P9.error", "the render texture did not bind to CloudCoverage: no latency measured");
                target.Release();
                yield break;
            }
            yield return ProbeWait.Frames(5);
            bool stillBound = material.GetTexture("CloudCoverage") == target;
            Emit("P9.bind_survives_frames", stillBound);

            applyEvents = 0;
            ReconDyn.Call(firstKerbinObject, "Apply");
            int latency = -1;
            bool replaced = false;
            for (int frame = 0; frame < 180; frame++)
            {
                Material now = ReconDyn.Get(firstKerbinVolume, "RaymarchedCloudMaterial") as Material;
                if (now != material) replaced = true;
                Texture held = now != null ? now.GetTexture("CloudCoverage") : null;
                if (held != target && latency < 0) latency = frame;
                if (latency >= 0 && frame >= latency + 5) break;
                yield return ProbeWait.Frames(1);
            }
            Emit("P9.rebind_latency_frames", latency);
            Emit("P9.material_replaced_by_apply", replaced);
            Emit("P9.onapply_fired_by_forced_apply", applyEvents > 0);
            Say("P9: " + layers.Count + " layers, forced Apply dropped our texture after " + latency + " frames, material replaced " + replaced);
            target.Release();
        }

        // The size of a coverage or type map of a volumetric layer, from the wrapper's texture; "none" where there is no texture.
        private static string MapSize(object volume, string property)
        {
            if (volume == null) return "none";
            object wrapper = ReconDyn.Get(volume, property);
            if (wrapper == null) return "none";
            Texture2D texture = ReconDyn.Get(wrapper, "textureValue") as Texture2D;
            return texture != null ? texture.width + "x" + texture.height : "not loaded";
        }
    }
}
