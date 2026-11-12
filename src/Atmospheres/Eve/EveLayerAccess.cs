using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // One cloud layer of EVE Redux's volumetric clouds (a CloudsObject) as the painter sees it. The objects are EVE's, held as object:
    // the assembly does not reference Atmosphere.dll. Read in Atmosphere.dll 3.2.2.0 (source tree release-5, .local/plans/eve-findings.md).
    internal sealed class EveLayer
    {
        // The name in EVE's config and the body it belongs to.
        internal string Name = "";
        internal string BodyName = "";
        // CloudsObject, its CloudsRaymarchedVolume, the two TextureWrappers, the 2D layer (Clouds2D, may be null) and the CloudsPQS.
        internal object Object, Volume, CoverageWrapper, TypeWrapper, Layer2D, Pqs;
        // The materials EVE draws with now: the main one, the reflection probe's, the 2D layer's shadow material.
        internal Material Main, Probe, Shadow2D;
        // The layer has timeSettings (EVE overwrites the amount every frame): not fought.
        internal bool HasTimeSettings;
        // The map of the layer is a native cube map (MAP_TYPE_CUBE_1): a 2D map does not fit.
        internal bool CubeMap;
        // The name of the layer whose shadow this layer receives, empty if none.
        internal string ReceivesShadowsFrom = "";
        // The cloud type names in the order of the layer's list.
        internal string[] TypeNames = new string[0];
        internal TypeSlots Slots = new TypeSlots(0);
        // The deck's inner and outer sphere radius in m.
        internal float InnerRadiusM, OuterRadiusM;
        // The reason the layer is left alone, or empty.
        internal string LeftReason = "";

        internal readonly BindingTracker Tracker = new BindingTracker();

        // Where the layer's map lies on the body (EveMapLayout) and whether it is known yet.
        internal double ShiftDeg;
        internal bool Mirrored;
        internal bool LayoutKnown;

        // The materials the maps were bound on, to see that EVE made new ones (the shader declares no properties, so the binding itself
        // cannot be read back: this identity and the paint set below are what the binding knows about itself).
        internal Material BoundMain, BoundProbe, BoundShadow2D;

        // The paint set whose targets the materials carry; null while nothing is bound.
        internal PaintSet BoundSet;

        // Whether the caster layer's coverage target was handed to this (dependent) layer, which switches CLOUD_SHADOW_CASTER_OFF off on
        // its main material. The sentinel looks at that keyword only then: with the caster not painted EVE's own state stands.
        internal bool ShadowCasterSet;

        // The watchdog's sentinel as a delegate made once (the binding asks it at most once per interval).
        internal Func<bool> SentinelCheck;

        // The textures of the two wrappers before the mirror went in (EVE's own, put back at the end), and whether the mirror is in.
        internal Texture2D OriginalCoverage, OriginalType;
        internal bool MirrorInjected;

        // The layout this layer was calibrated for; the time of the last calibration (real seconds).
        internal double CalibratedAt = double.NegativeInfinity;

        // Whether the layer's drift was stopped (CloudsPQS.mainPeriod zero).
        internal bool DriftFrozen;
    }

    // The members of EVE the painter reaches by name, looked up once; a member that is missing is named in Summary and the painter does
    // not run (the clouds stay as EVE has them). Every member is in AssumptionsC23.
    internal static class EveLayerAccess
    {
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        private static bool looked;
        private static string summary = "not looked for";
        private static bool complete;

        private static MethodInfo getList;
        private static FieldInfo onApplyField;
        private static PropertyInfo nameProperty, bodyProperty, volumeProperty, layer2DProperty;
        private static FieldInfo pqsField, mainPeriodField, offsetField, upwardsField;
        private static PropertyInfo timeSettingsProperty;
        private static PropertyInfo coverageMapProperty, typeMapProperty, mainMaterialProperty, probeMaterialProperty, typesProperty;
        private static PropertyInfo innerProperty, outerProperty, rotationProperty, receivesProperty;
        private static MethodInfo setShadowCaster, sampleCoverage;
        private static FieldInfo typeNameField;
        private static PropertyInfo shadowMaterialProperty;
        private static FieldInfo textureValueField, textureInitializedField;

        internal static string Summary
        {
            get
            {
                Look();
                return summary;
            }
        }

        // Whether every member of the path exists.
        internal static bool Available
        {
            get
            {
                Look();
                return complete;
            }
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            try
            {
                var link = new ReflectLink();
                Type manager = TypeLookup.Find("Atmosphere.CloudsManager");
                if (manager == null)
                {
                    summary = "EVE is not installed";
                    return;
                }
                Type layer = link.Type("Atmosphere.CloudsObject");
                Type volume = link.Type("Atmosphere.CloudsRaymarchedVolume");
                Type pqs = link.Type("Atmosphere.CloudsPQS");
                Type clouds2D = link.Type("Atmosphere.Clouds2D");
                Type cloudType = link.Type("Atmosphere.CloudType");
                Type wrapper = link.Type("Utils.TextureWrapper");
                getList = link.Method(manager, "CloudsManager", "GetObjectList", Static, null);
                onApplyField = link.Field(manager, "CloudsManager", "onApply", Static);
                nameProperty = link.Property(layer, "CloudsObject", "Name", Instance);
                bodyProperty = link.Property(layer, "CloudsObject", "Body", Instance);
                volumeProperty = link.Property(layer, "CloudsObject", "LayerRaymarchedVolume", Instance);
                layer2DProperty = link.Property(layer, "CloudsObject", "Layer2D", Instance);
                pqsField = link.Field(layer, "CloudsObject", "cloudsPQS", Instance);
                timeSettingsProperty = link.Property(pqs, "CloudsPQS", "TimeSettings", Instance);
                mainPeriodField = link.Field(pqs, "CloudsPQS", "mainPeriod", Instance);
                offsetField = link.Field(pqs, "CloudsPQS", "offset", Instance, typeof(Vector3));
                coverageMapProperty = link.Property(volume, "CloudsRaymarchedVolume", "CoverageMap", Instance);
                typeMapProperty = link.Property(volume, "CloudsRaymarchedVolume", "CloudTypeMap", Instance);
                mainMaterialProperty = link.Property(volume, "CloudsRaymarchedVolume", "RaymarchedCloudMaterial", Instance);
                probeMaterialProperty = link.Property(volume, "CloudsRaymarchedVolume", "ReflectionProbeRaymarchedCloudMaterial", Instance);
                typesProperty = link.Property(volume, "CloudsRaymarchedVolume", "CloudTypes", Instance);
                innerProperty = link.Property(volume, "CloudsRaymarchedVolume", "InnerSphereRadius", Instance);
                outerProperty = link.Property(volume, "CloudsRaymarchedVolume", "OuterSphereRadius", Instance);
                rotationProperty = link.Property(volume, "CloudsRaymarchedVolume", "CloudRotationMatrix", Instance);
                receivesProperty = link.Property(volume, "CloudsRaymarchedVolume", "ReceiveShadowsFromLayer", Instance);
                setShadowCaster = link.Method(volume, "CloudsRaymarchedVolume", "SetShadowCasterTextureParams", Instance,
                    new[] { typeof(RenderTexture), typeof(bool) });
                sampleCoverage = link.Method(volume, "CloudsRaymarchedVolume", "SampleCoverage", Instance,
                    new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(bool) });
                typeNameField = link.Field(cloudType, "CloudType", "typeName", Instance, typeof(string));
                // E43: optional, not part of the painter's path (a missing member leaves upwardsCloudSpeed to EVE, see AssumptionsE43)
                upwardsField = volume.GetField("upwardsCloudSpeed", Instance);
                if (upwardsField != null && upwardsField.FieldType != typeof(float)) upwardsField = null;
                shadowMaterialProperty = link.Property(clouds2D, "Clouds2D", "ScreenSpaceShadowMaterial", Instance);
                textureValueField = link.Field(wrapper, "TextureWrapper", "textureValue", Instance, typeof(Texture2D));
                textureInitializedField = link.Field(wrapper, "TextureWrapper", "textureInitialized", Instance, typeof(bool));
                complete = link.Complete;
                if (complete && !AtmospheresAssumptions.Holds(AssumptionsC23.Id))
                {
                    ForeignAssumption assumption = AtmospheresAssumptions.Find(AssumptionsC23.Id);
                    complete = false;
                    summary = "not used (" + (assumption != null ? assumption.State + ": " + assumption.Problem : "assumption unknown") + ")";
                    return;
                }
                summary = complete
                    ? "found (CloudsManager -> CloudsObject -> CloudsRaymarchedVolume -> materials, TextureWrapper)"
                    : "not controllable (missing " + link.MissingText() + ")";
            }
            catch (Exception e)
            {
                complete = false;
                summary = "not controllable (" + CompatibilityLog.Reason(e) + ")";
            }
        }

        // Subscribes to CloudsManager.onApply; false where it cannot be done (the watchdog still finds a lost binding).
        internal static bool SubscribeApply(Action handler)
        {
            if (!Available || onApplyField == null) return false;
            try
            {
                object ev = onApplyField.GetValue(null);
                if (ev == null) return false;
                MethodInfo add = ev.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                if (add == null) return false;
                ParameterInfo[] parameters = add.GetParameters();
                if (parameters.Length != 1) return false;
                Delegate d = Delegate.CreateDelegate(parameters[0].ParameterType, handler.Target, handler.Method);
                add.Invoke(ev, new object[] { d });
                return true;
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-eve-apply", "the subscription to EVE's onApply failed; the watchdog covers it", e);
                return false;
            }
        }

        // The layers of a body that can carry the weather maps, by role: the layers the body's weatherLayers names first, else every
        // layer of the body that has a coverage map and a type map, without the base layer, lowest deck first; at most cap of them.
        // Handles of layers that are still there are kept (their binding state with them). A layer whose volume is a new object is a
        // new handle.
        internal static List<EveLayer> Scan(string bodyName, CloudRules rules, int cap, IList<EveLayer> previous)
        {
            var found = new List<EveLayer>();
            if (!Available || string.IsNullOrEmpty(bodyName)) return found;
            IEnumerable list = getList.Invoke(null, null) as IEnumerable;
            if (list == null) return found;
            var candidates = new List<EveLayer>();
            foreach (object layer in list)
            {
                if (layer == null) continue;
                if (!string.Equals(bodyProperty.GetValue(layer, null) as string, bodyName, StringComparison.Ordinal)) continue;
                object volume = volumeProperty.GetValue(layer, null);
                if (volume == null) continue;
                object coverage = coverageMapProperty.GetValue(volume, null);
                object type = typeMapProperty.GetValue(volume, null);
                if (coverage == null || type == null) continue;
                // A layer EVE built again (CloudsManager.Apply) is the same layer by name: its handle, with the binding state and the
                // counters, is kept and takes the new objects.
                string layerName = nameProperty.GetValue(layer, null) as string ?? "";
                EveLayer handle = null;
                if (previous != null)
                {
                    foreach (EveLayer old in previous)
                    {
                        if (string.Equals(old.Name, layerName, StringComparison.Ordinal) && string.Equals(old.BodyName, bodyName, StringComparison.Ordinal))
                        {
                            handle = old;
                            break;
                        }
                    }
                }
                if (handle == null) handle = new EveLayer();
                Fill(handle, layer, volume, coverage, type, bodyName);
                candidates.Add(handle);
            }
            string baseLayer = rules != null ? rules.BaseLayer : "";
            IReadOnlyList<string> named = rules != null ? rules.WeatherLayers : null;
            if (named != null && named.Count > 0)
            {
                foreach (string name in named)
                {
                    foreach (EveLayer c in candidates)
                    {
                        if (string.Equals(c.Name, name, StringComparison.Ordinal) && !found.Contains(c)) found.Add(c);
                    }
                }
            }
            else
            {
                candidates.Sort((a, b) => a.InnerRadiusM.CompareTo(b.InnerRadiusM));
                foreach (EveLayer c in candidates)
                {
                    if (!string.IsNullOrEmpty(baseLayer) && string.Equals(c.Name, baseLayer, StringComparison.Ordinal)) continue;
                    found.Add(c);
                }
            }
            if (cap > 0 && found.Count > cap) found.RemoveRange(cap, found.Count - cap);
            return found;
        }

        private static void Fill(EveLayer h, object layer, object volume, object coverage, object type, string bodyName)
        {
            // New wrappers are new objects whose textures are EVE's again: the mirror has to be put in once more.
            if (!ReferenceEquals(h.CoverageWrapper, coverage) || !ReferenceEquals(h.TypeWrapper, type)) h.MirrorInjected = false;
            h.Object = layer;
            h.Volume = volume;
            h.CoverageWrapper = coverage;
            h.TypeWrapper = type;
            h.Name = nameProperty.GetValue(layer, null) as string ?? "";
            h.BodyName = bodyName;
            object newPqs = pqsField.GetValue(layer);
            if (!ReferenceEquals(h.Pqs, newPqs))
            {
                // A new CloudsPQS carries the drift of EVE's config again: stop it again and find how the map lies.
                h.DriftFrozen = false;
                h.LayoutKnown = false;
            }
            h.Pqs = newPqs;
            h.Layer2D = layer2DProperty.GetValue(layer, null);
            ReadMaterials(h);
            h.InnerRadiusM = ToFloat(innerProperty.GetValue(volume, null));
            h.OuterRadiusM = ToFloat(outerProperty.GetValue(volume, null));
            h.ReceivesShadowsFrom = receivesProperty.GetValue(volume, null) as string ?? "";
            h.CubeMap = h.Main != null && IsCubeMap(h.Main);
            if (h.Pqs == null) h.LeftReason = "the layer has no CloudsPQS to read timeSettings from";
            else
            {
                h.HasTimeSettings = timeSettingsProperty.GetValue(h.Pqs, null) != null;
                if (h.HasTimeSettings) h.LeftReason = "timeSettings";
            }
            if (h.CubeMap) h.LeftReason = "native cube map";
            var names = new List<string>();
            IEnumerable types = typesProperty.GetValue(volume, null) as IEnumerable;
            if (types != null)
            {
                foreach (object t in types) names.Add(t != null ? typeNameField.GetValue(t) as string ?? "" : "");
            }
            h.TypeNames = names.ToArray();
        }

        // Whether the coverage or the type map is a native cube map (TextureWrapper.ApplyCubeMap enables MAP_TYPE_CUBE_n, MAP_TYPE_CUBE2_n
        // or MAP_TYPE_CUBE6_n with the map's index: 1 for the coverage, 2 for the type): a 2D map does not fit.
        internal static bool IsCubeMap(Material m)
        {
            for (int index = 1; index <= 2; index++)
            {
                string n = index.ToString();
                if (m.IsKeywordEnabled("MAP_TYPE_CUBE_" + n) || m.IsKeywordEnabled("MAP_TYPE_CUBE2_" + n) || m.IsKeywordEnabled("MAP_TYPE_CUBE6_" + n)) return true;
            }
            return false;
        }

        // The number of volumetric layers EVE holds on any body: the layers that make EVE run its raymarch renderer.
        internal static int VolumetricLayerCount()
        {
            if (!Available) return 0;
            int n = 0;
            try
            {
                IEnumerable list = getList.Invoke(null, null) as IEnumerable;
                if (list == null) return 0;
                foreach (object layer in list)
                {
                    if (layer != null && volumeProperty.GetValue(layer, null) != null) n++;
                }
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-eve-count", "counting EVE's volumetric layers failed", e);
            }
            return n;
        }

        // Reads the layer's materials; true where one of them is not the object it was before.
        internal static bool ReadMaterials(EveLayer h)
        {
            Material oldMain = h.Main, oldProbe = h.Probe, oldShadow = h.Shadow2D;
            h.Main = mainMaterialProperty.GetValue(h.Volume, null) as Material;
            h.Probe = probeMaterialProperty.GetValue(h.Volume, null) as Material;
            h.Shadow2D = h.Layer2D != null ? shadowMaterialProperty.GetValue(h.Layer2D, null) as Material : null;
            return !ReferenceEquals(oldMain, h.Main) || !ReferenceEquals(oldProbe, h.Probe) || !ReferenceEquals(oldShadow, h.Shadow2D);
        }

        // Sets TypeSlots from the type names and the body's role lists.
        internal static void AssignSlots(EveLayer h, CloudRules rules)
        {
            h.Slots = TypeRoles.FromNames(h.TypeNames, rules);
        }

        // Where a place lies in the layer's map, by the layer's own rotation matrix (EVE's formula on the world position).
        internal static bool LayerCoordinates(EveLayer h, Vector3 worldPosition, out double u, out double v)
        {
            u = 0.5;
            v = 0.5;
            if (!(rotationProperty.GetValue(h.Volume, null) is Matrix4x4)) return false;
            var m = (Matrix4x4)rotationProperty.GetValue(h.Volume, null);
            Vector3 n = m.MultiplyPoint(worldPosition).normalized;
            EveCoverageMath.Uv(n.x, n.y, n.z, out u, out v);
            return true;
        }

        // Finds how the layer's map lies on the body, from three places; false when no shift fits.
        internal static bool Calibrate(EveLayer h, CelestialBody body, out double worstErrorDeg)
        {
            double shift;
            bool mirrored;
            EveMapLayout.LayerOfPlace layerOf = delegate (double lat, double lon, out double u, out double v)
            {
                Vector3d p = body.GetWorldSurfacePosition(lat, lon, 0.0);
                LayerCoordinates(h, new Vector3((float)p.x, (float)p.y, (float)p.z), out u, out v);
            };
            bool ok = EveMapLayout.Calibrate(layerOf, 1.0, out shift, out mirrored, out worstErrorDeg);
            h.ShiftDeg = shift;
            h.Mirrored = mirrored;
            h.LayoutKnown = ok;
            return ok;
        }

        // The coverage EVE's own CPU path returns at a world position (what the ground effects see), and the cloud type value there.
        internal static float SampleCoverage(EveLayer h, Vector3 worldPosition, out float cloudType)
        {
            var args = new object[] { worldPosition, 0f, true };
            object result = sampleCoverage.Invoke(h.Volume, args);
            cloudType = args[1] is float ? (float)args[1] : 0f;
            return result is float ? (float)result : 0f;
        }

        // Gives the dependent layers of the body the caster's coverage target as the shadow caster.
        internal static void SetShadowCaster(EveLayer dependent, RenderTexture coverage)
        {
            setShadowCaster.Invoke(dependent.Volume, new object[] { coverage, true });
        }

        // The texture of the wrapper's private field (what EVE's CPU readers sample), and a setter for it.
        internal static Texture2D WrapperTexture(object wrapper)
        {
            return textureValueField.GetValue(wrapper) as Texture2D;
        }

        internal static void SetWrapperTexture(object wrapper, Texture2D texture)
        {
            textureValueField.SetValue(wrapper, texture);
            textureInitializedField.SetValue(wrapper, true);
        }

        // Stops the layer's own drift so that the map stays fixed to the body: mainPeriod becomes zero and the offset takes the phase
        // the layer had, so nothing jumps (CloudsPQS.GetMainRotationAtUT: rotation = fract(ut x mainPeriod) x 360 + offset).
        internal static bool FreezeDrift(EveLayer h, double ut)
        {
            if (h.Pqs == null || mainPeriodField == null || offsetField == null) return false;
            object period = mainPeriodField.GetValue(h.Pqs);
            if (period == null) return false;
            Type vt = period.GetType();
            FieldInfo fx = vt.GetField("x"), fy = vt.GetField("y"), fz = vt.GetField("z");
            if (fx == null || fy == null || fz == null) return false;
            double px = (double)fx.GetValue(period), py = (double)fy.GetValue(period), pz = (double)fz.GetValue(period);
            if (px == 0.0 && py == 0.0 && pz == 0.0) return true;
            var offset = (Vector3)offsetField.GetValue(h.Pqs);
            offset.x += (float)(Fract(ut * px) * 360.0);
            offset.y += (float)(Fract(ut * py) * 360.0);
            offset.z += (float)(Fract(ut * pz) * 360.0);
            offsetField.SetValue(h.Pqs, offset);
            object zero = Activator.CreateInstance(vt);
            mainPeriodField.SetValue(h.Pqs, zero);
            return true;
        }

        // The layer's own drift in revolutions per second (E43): the component of CloudsPQS.mainPeriod that turns the layer about the polar axis
        // (DriftAxis, [VERIFY] in the game), zero while the drift is frozen.
        internal const char DriftAxis = 'y';

        internal static bool TryGetDriftRev(EveLayer h, out double rev)
        {
            rev = 0.0;
            if (h.Pqs == null || mainPeriodField == null) return false;
            try
            {
                object period = mainPeriodField.GetValue(h.Pqs);
                if (period == null) return false;
                FieldInfo f = period.GetType().GetField(DriftAxis.ToString());
                if (f == null) return false;
                rev = Convert.ToDouble(f.GetValue(period));
                return true;
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-eve-driftread", "reading the drift of EVE layer '" + h.Name + "' failed", e);
                return false;
            }
        }

        // The layer's offset angle about the drift axis in degrees (what SetDriftRev wraps into 0 .. 360).
        internal static bool TryGetOffsetDeg(EveLayer h, out float degrees)
        {
            degrees = 0f;
            if (h.Pqs == null || offsetField == null) return false;
            try
            {
                var offset = (Vector3)offsetField.GetValue(h.Pqs);
                degrees = DriftAxis == 'x' ? offset.x : (DriftAxis == 'y' ? offset.y : offset.z);
                return true;
            }
            catch (Exception) { return false; }
        }

        // An angle in degrees wrapped into 0 .. 360 (the offset repeats every full turn).
        internal static float WrapDegrees(float degrees)
        {
            double d = degrees % 360.0;
            if (d < 0.0) d += 360.0;
            return (float)d;
        }

        // Sets the layer's own drift to rev revolutions per second about the polar axis and puts the difference of the phase into the offset, so
        // that the layer does not jump (rotation = fract(ut x mainPeriod) x 360 + offset): offset' = offset + fract(ut x old) x 360 - fract(ut x new) x 360.
        internal static bool SetDriftRev(EveLayer h, double ut, double rev)
        {
            if (h.Pqs == null || mainPeriodField == null || offsetField == null) return false;
            try
            {
                object period = mainPeriodField.GetValue(h.Pqs);
                if (period == null) return false;
                Type vt = period.GetType();
                FieldInfo axis = vt.GetField(DriftAxis.ToString());
                if (axis == null) return false;
                double old = Convert.ToDouble(axis.GetValue(period));
                var offset = (Vector3)offsetField.GetValue(h.Pqs);
                float delta = (float)((Fract(ut * old) - Fract(ut * rev)) * 360.0);
                // the offset is an angle: it is wrapped into 0 .. 360 every step, so the float keeps its precision over a session (the layer turns by the same amount)
                if (DriftAxis == 'x') offset.x = WrapDegrees(offset.x + delta); else if (DriftAxis == 'y') offset.y = WrapDegrees(offset.y + delta); else offset.z = WrapDegrees(offset.z + delta);
                offsetField.SetValue(h.Pqs, offset);
                axis.SetValue(period, rev);
                mainPeriodField.SetValue(h.Pqs, period);
                return true;
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-eve-driftset", "setting the drift of EVE layer '" + h.Name + "' failed", e);
                return false;
            }
        }

        // CloudsRaymarchedVolume.upwardsCloudSpeed, read every frame by EVE (live); false where the field is not there (AssumptionsE43).
        internal static bool UpwardsAvailable
        {
            get
            {
                Look();
                return upwardsField != null && AtmospheresAssumptions.Holds(AssumptionsE43.Id);
            }
        }

        internal static bool TryGetUpwards(EveLayer h, out float speed)
        {
            speed = 0f;
            if (!UpwardsAvailable || h.Volume == null) return false;
            try
            {
                speed = Convert.ToSingle(upwardsField.GetValue(h.Volume));
                return true;
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-eve-upread", "reading upwardsCloudSpeed of EVE layer '" + h.Name + "' failed", e);
                return false;
            }
        }

        internal static bool SetUpwards(EveLayer h, float speed)
        {
            if (!UpwardsAvailable || h.Volume == null) return false;
            try
            {
                upwardsField.SetValue(h.Volume, speed);
                return true;
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-eve-upset", "setting upwardsCloudSpeed of EVE layer '" + h.Name + "' failed", e);
                return false;
            }
        }

        // Puts the wrappers' own textures back and has EVE bind them to the materials again (ApplyShaderParams is public): used when the
        // painter stops, so that no material keeps a render texture that is going away.
        internal static void Restore(EveLayer h)
        {
            if (h.Volume == null) return;
            try
            {
                if (h.MirrorInjected)
                {
                    if (h.CoverageWrapper != null) PutBack(h.CoverageWrapper, h.OriginalCoverage);
                    if (h.TypeWrapper != null) PutBack(h.TypeWrapper, h.OriginalType);
                    h.MirrorInjected = false;
                }
                MethodInfo apply = h.Volume.GetType().GetMethod("ApplyShaderParams", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (apply != null) apply.Invoke(h.Volume, null);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-eve-restore", "putting EVE layer '" + h.Name + "' back failed", e);
            }
        }

        // The wrapper gets its old texture back; with none, it is made "not loaded", so that EVE loads the pack's map by name again.
        private static void PutBack(object wrapper, Texture2D original)
        {
            if (original != null)
            {
                SetWrapperTexture(wrapper, original);
                return;
            }
            textureValueField.SetValue(wrapper, null);
            textureInitializedField.SetValue(wrapper, false);
        }

        // The picture of the orbit and map view for the layer's 2D layer: the material EVE draws the 2D clouds with takes it as _MainTex.
        // Set again at every repaint of the picture and right after EVE touched the layer (a SetTexture of a texture that is there costs
        // nothing, and no read-back is needed: whether the 2D shader declares _MainTex is [VERIFY], the volume materials declare nothing).
        // False where the layer has no 2D layer or the member is missing.
        internal static bool Bind2D(EveLayer h, Texture picture)
        {
            Material rendering = Rendering2D(h);
            if (rendering == null || picture == null) return false;
            rendering.SetTexture("_MainTex", picture);
            return true;
        }

        private static Material Rendering2D(EveLayer h)
        {
            if (h.Layer2D == null) return null;
            PropertyInfo p = h.Layer2D.GetType().GetProperty("CloudRenderingMaterial", BindingFlags.Public | BindingFlags.Instance);
            return p != null ? p.GetValue(h.Layer2D, null) as Material : null;
        }

        private static double Fract(double x)
        {
            return x - Math.Floor(x);
        }

        private static float ToFloat(object o)
        {
            return o is float ? (float)o : 0f;
        }
    }
}
