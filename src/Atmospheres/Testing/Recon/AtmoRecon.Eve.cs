using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // P9: what EVE offers the weather field. The layer list per body (name, which parts exist, the sizes of the coverage and type maps),
    // the cloud manager's onApply event, and the experiment the cloud painter depends on: a render texture bound to a layer's coverage
    // map, read back, then a forced CloudsObject.Apply(); how many frames until the layer no longer carries our texture. The forced
    // Apply makes a second set of layer objects in the scene, so this runs last.
    //
    // Run2 read nothing back, and the game log said why: Unity prints "Material doesn't have a texture property 'CloudCoverage'" while
    // the material answers HasProperty with true. The shader EVE/RaymarchCloud declares no properties at all (its Properties block is
    // empty in eveshaders.bundle, read with UnityPy), so EVE hands the textures over by name at run time (CloudsRaymarchedVolume.
    // SetShaderTextureParams: TextureWrapper.ApplyTexture(mat, "CloudCoverage", 1), which for a 2D map is SetTexture and for a cube map
    // sets "cubeCloudCoverage" or its six faces). What the probe measures now is which of the calls Unity refuses: HasProperty, Get,
    // Set and Get again (by name and by id), counted from the log messages each call causes, and what the material lists as its texture
    // properties. If Get cannot read the texture back, the latency is taken from the material instance EVE replaces on Apply.
    internal sealed partial class AtmoRecon
    {
        private int applyEvents;

        private void OnApplyFired()
        {
            applyEvents++;
        }

        // Messages of the engine's log counted while a call runs.
        private int logErrorCount;
        private string lastLogError = "";

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                logErrorCount++;
                lastLogError = condition;
            }
        }

        // What one attempt to bind a render texture to a named texture property of a material showed.
        private sealed class BindReport
        {
            internal bool Has, HasCube;
            internal int GetErrors, SetErrors, GetBackErrors, GetByIdErrors;
            internal bool TookByName, TookById;
            internal string ReadBack = "null", Before = "null", ShaderName = "", Properties = "";
            internal int ShaderProperties = -1;
        }

        private BindReport TryBind(Material material, string property, Texture target)
        {
            var report = new BindReport { ShaderName = material.shader != null ? material.shader.name : "none" };
            try
            {
                report.ShaderProperties = material.shader != null ? material.shader.GetPropertyCount() : -1;
            }
            catch (Exception)
            {
            }
            try
            {
                string[] names = material.GetTexturePropertyNames();
                report.Properties = names == null ? "" : names.Length + (names.Length > 0 ? ": " + string.Join(",", names) : "");
            }
            catch (Exception e)
            {
                report.Properties = "unreadable (" + e.GetType().Name + ")";
            }
            report.Has = material.HasProperty(property);
            report.HasCube = material.HasProperty("cube" + property);

            int mark = logErrorCount;
            Texture before = material.GetTexture(property);
            report.GetErrors = logErrorCount - mark;
            report.Before = Describe(before);

            mark = logErrorCount;
            material.SetTexture(property, target);
            report.SetErrors = logErrorCount - mark;

            mark = logErrorCount;
            Texture got = material.GetTexture(property);
            report.GetBackErrors = logErrorCount - mark;
            report.TookByName = got == target;
            report.ReadBack = Describe(got);

            mark = logErrorCount;
            Texture gotById = material.GetTexture(Shader.PropertyToID(property));
            report.GetByIdErrors = logErrorCount - mark;
            report.TookById = gotById == target;
            return report;
        }

        private static string Describe(Texture texture)
        {
            return texture == null ? "null" : texture.GetType().Name + " '" + texture.name + "' " + texture.width + "x" + texture.height;
        }

        // The texture EVE keeps for a coverage map (its wrapper's loaded texture), or null for a cube map or a layer without one.
        private static Texture WrapperTexture(object volume)
        {
            object wrapper = ReconDyn.Get(volume, "CoverageMap");
            return wrapper == null ? null : ReconDyn.Get(wrapper, "textureValue") as Texture;
        }

        private static string MapKeywords(Material material)
        {
            var text = new StringBuilder();
            try
            {
                foreach (string keyword in material.shaderKeywords)
                {
                    if (keyword.StartsWith("MAP_TYPE_", StringComparison.Ordinal)) text.Append(keyword).Append(' ');
                }
            }
            catch (Exception)
            {
            }
            return text.ToString().Trim();
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
                    // The forced Apply goes to a layer of EVE's own configs: the two layers named ReDefinition- are the product's.
                    if (volume != null && firstKerbinVolume == null && !name.StartsWith("ReDefinition-", StringComparison.Ordinal))
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

            Application.LogCallback callback = OnLogMessage;
            Application.logMessageReceived += callback;
            var target = new RenderTexture(512, 256, 0, RenderTextureFormat.R8);
            target.Create();
            Material material = null;
            try
            {
                // Every volumetric layer on Kerbin is tried on its main material and its reflection probe material: which calls Unity
                // refuses on the property EVE hands the coverage map over under, and whether the render texture can be read back at all.
                int tried = 0, hasProperty = 0, tookByName = 0, tookById = 0, getErrors = 0, setErrors = 0, getBackErrors = 0, cubeLayers = 0;
                int shaderProperties = -1;
                string texturePropertyList = "";
                foreach (object layer in layers)
                {
                    if (Convert.ToString(ReconDyn.Get(layer, "Body")) != "Kerbin") continue;
                    object volume = ReconDyn.Get(layer, "LayerRaymarchedVolume");
                    if (volume == null) continue;
                    string layerName = Convert.ToString(ReconDyn.Get(layer, "Name"));
                    string[] kinds = { "RaymarchedCloudMaterial", "ReflectionProbeRaymarchedCloudMaterial" };
                    for (int k = 0; k < kinds.Length; k++)
                    {
                        Material candidate = ReconDyn.Get(volume, kinds[k]) as Material;
                        if (candidate == null)
                        {
                            EmitText("P9.material." + layerName + "." + kinds[k], "none");
                            continue;
                        }
                        BindReport report = TryBind(candidate, "CloudCoverage", target);
                        // EVE's own texture goes back (the wrapper's, or white where the layer has none): the property is EVE's.
                        Texture own = WrapperTexture(volume);
                        candidate.SetTexture("CloudCoverage", own != null ? own : Texture2D.whiteTexture);
                        tried++;
                        if (report.Has) hasProperty++;
                        if (report.TookByName) tookByName++;
                        if (report.TookById) tookById++;
                        getErrors += report.GetErrors;
                        setErrors += report.SetErrors;
                        getBackErrors += report.GetBackErrors;
                        if (report.HasCube) cubeLayers++;
                        shaderProperties = report.ShaderProperties;
                        if (texturePropertyList.Length == 0) texturePropertyList = report.Properties;
                        EmitText("P9.material." + layerName + "." + kinds[k], report.ShaderName + ", shader properties " + report.ShaderProperties
                                 + ", HasProperty CloudCoverage " + report.Has + ", cubeCloudCoverage " + report.HasCube + ", log errors on Get/Set/Get/GetById "
                                 + report.GetErrors + "/" + report.SetErrors + "/" + report.GetBackErrors + "/" + report.GetByIdErrors
                                 + ", read back by name " + report.TookByName + " by id " + report.TookById + " (" + report.ReadBack + "), before " + report.Before
                                 + ", map keywords [" + MapKeywords(candidate) + "], EVE's own coverage " + Describe(own));
                        if (k == 0 && (report.TookByName || report.TookById) && material == null && !layerName.StartsWith("ReDefinition-", StringComparison.Ordinal))
                        {
                            material = candidate;
                            firstKerbinVolume = volume;
                            firstKerbinObject = layer;
                        }
                    }
                }
                Emit("P9.materials_tried", tried);
                Emit("P9.materials_with_property", hasProperty);
                Emit("P9.materials_read_back_by_name", tookByName);
                Emit("P9.materials_read_back_by_id", tookById);
                Emit("P9.materials_with_cube_property", cubeLayers);
                Emit("P9.get_log_errors", getErrors);
                Emit("P9.set_log_errors", setErrors);
                Emit("P9.get_back_log_errors", getBackErrors);
                Emit("P9.shader_declared_properties", shaderProperties);
                EmitText("P9.texture_property_names", texturePropertyList);
                EmitText("P9.last_log_error", lastLogError);

                bool readable = material != null;
                Emit("P9.bind_works", readable);
                Material tracked = ReconDyn.Get(firstKerbinVolume, "RaymarchedCloudMaterial") as Material;
                if (readable) material.SetTexture("CloudCoverage", target);
                bool survives = false;
                if (readable)
                {
                    yield return ProbeWait.Frames(5);
                    survives = material.GetTexture("CloudCoverage") == target;
                }
                Emit("P9.bind_survives_frames", survives);
                if (!readable)
                {
                    EmitText("P9.note", "no read-back of the render texture: the latency is taken from the material instance EVE replaces on Apply");
                }

                // The forced Apply: the latency in frames until the layer no longer holds our texture (where it can be read back) or until the
                // material instance is not the one before (the binding made on the old instance is lost either way).
                applyEvents = 0;
                int idBefore = tracked != null ? tracked.GetInstanceID() : 0;
                ReconDyn.Call(firstKerbinObject, "Apply");
                int latency = -1;
                bool replaced = false;
                for (int frame = 0; frame < 180; frame++)
                {
                    Material now = ReconDyn.Get(firstKerbinVolume, "RaymarchedCloudMaterial") as Material;
                    bool instanceChanged = tracked != null && (now == null || now.GetInstanceID() != idBefore);
                    if (instanceChanged) replaced = true;
                    bool lost = readable && material != null && now != null && now.GetTexture("CloudCoverage") != target;
                    if ((lost || instanceChanged) && latency < 0) latency = frame;
                    if (latency >= 0 && frame >= latency + 5) break;
                    yield return ProbeWait.Frames(1);
                }
                Emit("P9.rebind_latency_frames", latency);
                EmitText("P9.latency_method", readable ? "texture read-back or material instance, whichever came first" : "material instance only");
                Emit("P9.material_replaced_by_apply", replaced);
                Emit("P9.onapply_fired_by_forced_apply", applyEvents > 0);

                // The same attempt on the materials EVE bound after the Apply: the layer's own Material is the one that counts.
                Material after = ReconDyn.Get(firstKerbinVolume, "RaymarchedCloudMaterial") as Material;
                if (after != null)
                {
                    BindReport report = TryBind(after, "CloudCoverage", target);
                    Texture own = WrapperTexture(firstKerbinVolume);
                    after.SetTexture("CloudCoverage", own != null ? own : Texture2D.whiteTexture);
                    Emit("P9.after_apply_read_back_by_name", report.TookByName);
                    Emit("P9.after_apply_read_back_by_id", report.TookById);
                    Emit("P9.after_apply_get_log_errors", report.GetErrors + report.GetBackErrors);
                }
                Say("P9: " + layers.Count + " layers, " + tried + " materials tried, read back " + tookByName + ", forced Apply: " + latency + " frames, material replaced "
                    + replaced + ", log errors Get " + getErrors + " Set " + setErrors);
            }
            finally
            {
                Application.logMessageReceived -= callback;
                target.Release();
            }
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
