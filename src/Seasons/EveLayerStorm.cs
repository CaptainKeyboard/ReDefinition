using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // The storm cells inside the volumetric cloud layers EVE already has on Kerbin (setting stormClouds = Existing), by reflection.
    // A cell raises the coverage of the layers that make the weather and turns their cloud type to the snow-bearing one.
    // From orbit it is a swirl on EVE's 2D layer. So it is lit and textured like the clouds around it.
    //
    // A layer with a snow type ("Polar-snow") is a snow layer: Kerbin-Weather-1 and 2. A layer with a low "Stratus" type is
    // the body layer, a stratiform deck: Kerbin-base-layer. Others are left alone. docs/development/flow-seasons.md has the evidence.
    //
    // Each step of the path is a reflection that may fail with a line in the log.
    // The volume's CoverageMap and CloudTypeMap wrap the textures EVE samples, and its two materials hold them too.
    // We composite the cell into a copy of each and put the copy there. The original goes back when the setting goes off,
    // no cell is near for a while, the scene unloads, or a step fails.
    // CloudsPQS.GetMainRotationAtUT is the layer's own rotation: a body-frame direction times it is where EVE samples the maps.
    // The 2D layer's _MainTex becomes a render texture: the picture blitted in, the swirl drawn over it.
    internal static class EveLayerStorm
    {
        // How far the cells are painted into the volumes' maps, the height up to which the volumes are current, and from where the swirl
        // is drawn: the same as EveCellLayer's.
        private const double RangeM = EveCellLayer.RangeM;
        private const double VolumeLiveBelowM = EveCellLayer.VolumeLiveBelowM;
        private const double WideFromAltitudeM = EveCellLayer.WideFromAltitudeM;

        // The maps go back to the originals this long after the last cell was near, in s; the swirl's picture this long after the last
        // time it was needed.
        private const float ReleaseDelay = 30f, SwirlReleaseDelay = 10f;

        // How often the layers are looked for, the maps are painted (the big map and the small ones), and the swirl is drawn, in s.
        private const float ScanInterval = 3f, PaintIntervalLarge = 1f, PaintIntervalSmall = 0.5f, SwirlInterval = 2f;

        // The width of the swirl's picture at most, in texels. The stock picture is 8192 wide and takes 134 MB. This one takes 44 MB with mips.
        private const int SwirlMaxWidth = 4096;

        // The types that mark a snow layer, a body layer, and the highest start of the body layer's cloud in m above sea level.
        private static readonly string[] SnowTypeNames = { "Polar-snow", "Snow" };
        private static readonly string[] BodyTypeNames = { "Stratus" };
        private const double BodyLayerBaseMaxAslM = 3000;

        // The cloud base and top of the body layer, for the veil's start and the warning about ground that is higher.
        private const double DefaultBaseAslM = 1750, DefaultTopAslM = 3450;

        // Names of the shaders that draw the swirl, the first that exists: premultiplied alpha first, so that the result is the union of
        // the picture's alpha and the puff's, as the clouds over each other are.
        private static readonly string[] SwirlShaderNames = { "Sprites/Default", "UI/Default" };

        // The vertices of a puff: the segments and rings of its fan.
        private const int FanSegments = 20, FanRings = 4;

        internal enum Status
        {
            Unknown,
            NoEve,
            NoVolumetric,
            NotControllable,
            // The lookup is complete; the layers are looked for.
            Ready,
            // Broken at run time: back on the originals, for the rest of the scene.
            Broken
        }

        // One layer that carries the cell.
        private sealed class Layer
        {
            internal string Name;
            internal object Object, Volume, Pqs, CoverageWrapper, TypeWrapper;
            internal bool SnowRole;
            internal int TypeIndex, TypeCount;
            internal byte TargetType;
            internal double BaseAslM, TopAslM;
            internal Material Main, Probe;

            internal bool Swapped;
            internal int Width, Height;
            internal Texture2D CoverageOriginal, TypeOriginal, CoverageWork, TypeWork;
            internal byte[] CoverageBytes, CoverageOriginalBytes, TypeBytes, TypeOriginalBytes;
            internal byte[] Scratch = new byte[0];
            internal readonly EveCoverageMath.Rect[] Painted = new EveCoverageMath.Rect[StormCells.Capacity];
            internal int PaintedCount;
            internal float NextPaint;
            internal bool Checked;
            internal double PaintMsTotal;
            internal int Paints;

            // The 2D layer of the object (the body layer only).
            internal object Layer2D, MainTexWrapper;
            internal Material Rendering;
        }

        private static bool looked;
        private static Status status = Status.Unknown;
        private static string summary = "not looked for";
        private static MethodInfo getList, getRotation, sampleCoverage;
        private static PropertyInfo nameProperty, bodyProperty, volumeProperty, layer2DProperty, coverageMapProperty, typeMapProperty, typesProperty;
        private static PropertyInfo materialProperty, probeMaterialProperty, rotationProperty, innerProperty, outerProperty, fadeProperty;
        private static PropertyInfo typeNameProperty, typeMinProperty, typeMaxProperty, cloudsMatProperty, renderingMaterialProperty;
        private static FieldInfo pqsField, textureField, initializedField, mainTexField;

        private static readonly List<Layer> layers = new List<Layer>();
        private static readonly object[] rotationArgs = new object[4];
        private static readonly object[] sampleArgs = new object[3];
        private static readonly EveStormMath.Lobe[] lobes = new EveStormMath.Lobe[EveStormMath.MaxLobes];
        private static readonly EveStormMath.Puff[] puffs = new EveStormMath.Puff[EveStormMath.MaxPuffs];
        private static readonly double[] fanU = new double[1 + FanSegments * FanRings], fanV = new double[1 + FanSegments * FanRings], fanAlpha = new double[1 + FanSegments * FanRings];
        private static readonly int[] fanIndices = new int[(FanSegments + (FanRings - 1) * FanSegments * 2) * 3];
        private static readonly HashSet<ulong> groundChecked = new HashSet<ulong>();
        private static float nextScan, lastCellTime, lastSwirlNeeded, nextSwirl;
        private static bool loggedStatus, loggedNone, frameLogged, brokenLogged, scanned;
        private static string layersLogged = "";

        // The 2D layer's picture.
        private static Layer swirlLayer;
        private static RenderTexture swirlTarget;
        private static Texture swirlOriginal;
        private static Material swirlMaterial;
        private static bool swirlBound, swirlFailed, swirlLogged, swirlShaderLogged;
        private static int swirlCells = -1;

        // A diagnostics switch for screenshots: the swirl is drawn as solid red, so that it can be told from the clouds.
        internal static bool Probe;

        // The base and the top of the body layer's cloud above sea level, in m (the config's, once a layer is found).
        private static double baseAslM = DefaultBaseAslM, topAslM = DefaultTopAslM;

        internal static string Summary
        {
            get { return summary; }
        }

        internal static Status Current
        {
            get { return status; }
        }

        // Whether the storm cloud is in EVE's own layers now: the setting asks for it, the lookup was complete and a layer was found.
        // EveCellLayer then does not show its layer of the mod's config, and the volume draws the veil only.
        internal static bool Supplying
        {
            get
            {
                return SeasonsSettings.StormEveClouds && SeasonsSettings.StormClouds == SeasonsSettings.StormCloudModes.Existing
                       && status == Status.Ready && BodyLayerFound;
            }
        }

        // Whether EVE's own layers cannot carry the cell, so that the mod's own layer does (EveCellLayer.OwnCloud).
        // The lookup failed, the effect broke, or no layer is a snow or Stratus layer with both maps.
        internal static bool Fallback
        {
            get
            {
                Look();
                if (status == Status.NoEve || status == Status.NoVolumetric || status == Status.NotControllable || status == Status.Broken) return true;
                return scanned && !BodyLayerFound;
            }
        }

        // Whether the lookup of EVE's symbols was complete (not whether a layer was found).
        internal static bool Controllable
        {
            get { return status == Status.Ready; }
        }

        private static bool BodyLayerFound
        {
            get
            {
                for (int i = 0; i < layers.Count; i++) if (!layers[i].SnowRole) return true;
                return false;
            }
        }

        // The base of the cloud above the ground at a place of that ground height, in m, for the veil's start.
        internal static double BaseAboveGroundM(double groundAslM)
        {
            return Math.Max(120.0, baseAslM - groundAslM);
        }

        internal static double BaseAslM
        {
            get { return baseAslM; }
        }

        internal static double TopAslM
        {
            get { return topAslM; }
        }

        // Whether the cloud of a place is under the ground there: the ground is higher than the top of the layers' cloud.
        internal static bool GroundAboveCloud(double groundAslM)
        {
            return groundAslM > topAslM;
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            try
            {
                Type manager = TypeLookup.Find("Atmosphere.CloudsManager");
                if (manager == null)
                {
                    status = Status.NoEve;
                    summary = "EVE is not installed";
                    return;
                }
                Type volume = TypeLookup.Find("Atmosphere.CloudsRaymarchedVolume");
                if (volume == null)
                {
                    status = Status.NoVolumetric;
                    summary = "this EVE has no volumetric (raymarched) cloud layers";
                    return;
                }
                var link = new ReflectLink();
                Type layer = link.Type("Atmosphere.CloudsObject");
                Type pqs = link.Type("Atmosphere.CloudsPQS");
                Type wrapper = link.Type("Utils.TextureWrapper");
                Type cloudType = link.Type("Atmosphere.CloudType");
                Type clouds2D = link.Type("Atmosphere.Clouds2D");
                Type cloudsMaterial = link.Type("Atmosphere.CloudsMaterial");
                BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
                getList = link.Method(manager, "CloudsManager", "GetObjectList", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy, null);
                nameProperty = link.Property(layer, "CloudsObject", "Name", pub);
                bodyProperty = link.Property(layer, "CloudsObject", "Body", pub);
                volumeProperty = link.Property(layer, "CloudsObject", "LayerRaymarchedVolume", pub);
                layer2DProperty = link.Property(layer, "CloudsObject", "Layer2D", pub);
                pqsField = link.Field(layer, "CloudsObject", "cloudsPQS", TypeLookup.Any);
                getRotation = link.Method(pqs, "CloudsPQS", "GetMainRotationAtUT", pub, null);
                coverageMapProperty = link.Property(volume, "CloudsRaymarchedVolume", "CoverageMap", pub);
                typeMapProperty = link.Property(volume, "CloudsRaymarchedVolume", "CloudTypeMap", pub);
                typesProperty = link.Property(volume, "CloudsRaymarchedVolume", "CloudTypes", pub);
                materialProperty = link.Property(volume, "CloudsRaymarchedVolume", "RaymarchedCloudMaterial", pub);
                probeMaterialProperty = link.Property(volume, "CloudsRaymarchedVolume", "ReflectionProbeRaymarchedCloudMaterial", pub);
                rotationProperty = link.Property(volume, "CloudsRaymarchedVolume", "CloudRotationMatrix", pub);
                innerProperty = link.Property(volume, "CloudsRaymarchedVolume", "InnerSphereRadius", pub);
                outerProperty = link.Property(volume, "CloudsRaymarchedVolume", "OuterSphereRadius", pub);
                fadeProperty = link.Property(volume, "CloudsRaymarchedVolume", "CurrentTimeFadeCoverage", pub);
                sampleCoverage = link.Method(volume, "CloudsRaymarchedVolume", "SampleCoverage", pub,
                    new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(bool) });
                typeNameProperty = link.Property(cloudType, "CloudType", "TypeName", pub);
                typeMinProperty = link.Property(cloudType, "CloudType", "MinAltitude", pub);
                typeMaxProperty = link.Property(cloudType, "CloudType", "MaxAltitude", pub);
                textureField = link.Field(wrapper, "TextureWrapper", "textureValue", TypeLookup.Any, typeof(Texture2D));
                initializedField = link.Field(wrapper, "TextureWrapper", "textureInitialized", TypeLookup.Any, typeof(bool));
                // The 2D layer is optional: without it the cell has no swirl in the orbit view and everything else works.
                var optional = new ReflectLink();
                cloudsMatProperty = optional.Property(clouds2D, "Clouds2D", "CloudsMat", pub);
                renderingMaterialProperty = optional.Property(clouds2D, "Clouds2D", "CloudRenderingMaterial", pub);
                mainTexField = optional.Field(cloudsMaterial, "CloudsMaterial", "_MainTex", TypeLookup.Any);
                if (link.Complete)
                {
                    status = Status.Ready;
                    summary = "found (CloudsManager -> CloudsObject -> CloudsRaymarchedVolume.CoverageMap/CloudTypeMap -> TextureWrapper.textureValue, "
                              + "CloudsPQS.GetMainRotationAtUT); 2D layer " + (optional.Complete ? "found" : "not available (missing " + optional.MissingText() + ")");
                    if (!optional.Complete) layer2DProperty = null;
                }
                else
                {
                    status = Status.NotControllable;
                    summary = "not controllable (missing " + link.MissingText() + ")";
                }
            }
            catch (Exception e)
            {
                status = Status.NotControllable;
                summary = "not controllable (" + CompatibilityLog.Reason(e) + ")";
            }
        }

        // Called every frame while the storm effect runs. cells are the cells within EveCellLayer.RangeFor of the camera. The coverage
        // and type maps of the layers are written while the volumes are current (below VolumeLiveBelowM, not in the map view); the 2D
        // picture from WideFromAltitudeM and in the map view.
        internal static void Update(CelestialBody body, StormCells.Cell[] cells, int count, double cameraAltitudeM)
        {
            if (body == null || !SeasonsSettings.StormEveClouds || SeasonsSettings.StormClouds != SeasonsSettings.StormCloudModes.Existing)
            {
                Release();
                return;
            }
            Look();
            if (status == Status.NoEve || status == Status.NoVolumetric || status == Status.NotControllable || status == Status.Broken)
            {
                if (!loggedStatus)
                {
                    loggedStatus = true;
                    SeasonsLog.Say("storm clouds in EVE's layers: " + summary + "; the cells use the mod's own EVE layer (stormClouds = Own)");
                }
                return;
            }
            float now = Time.realtimeSinceStartup;
            try
            {
                if (now >= nextScan)
                {
                    nextScan = now + ScanInterval;
                    Scan(body);
                }
                if (layers.Count == 0) return;
                bool map = MapView.MapIsEnabled;
                bool live = cameraAltitudeM < VolumeLiveBelowM && !map;
                bool wide = map || cameraAltitudeM >= WideFromAltitudeM;
                double ut = Planetarium.GetUniversalTime();
                int near = live ? CountNear(cells, count) : 0;
                if (near > 0) lastCellTime = now;
                // A layer that still holds a painted cell is painted once more without it, then it waits to be put back.
                for (int i = 0; i < layers.Count; i++)
                {
                    if (near > 0 || layers[i].PaintedCount > 0) PaintLayer(body, layers[i], cells, near > 0 ? count : 0, ut, live);
                }
                if (near == 0 && now - lastCellTime > ReleaseDelay)
                {
                    for (int i = 0; i < layers.Count; i++) Unswap(layers[i]);
                }
                bool visible = HasVisible(cells, count);
                if (visible) lastSwirlNeeded = now;
                if (wide && (visible || swirlBound))
                {
                    PaintSwirl(body, cells, count, ut, now);
                }
                if (swirlBound && (!wide || !visible) && now - lastSwirlNeeded > SwirlReleaseDelay)
                {
                    UnbindSwirl();
                }
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        private static void Break(Exception e)
        {
            status = Status.Broken;
            summary = "failed at run time (" + CompatibilityLog.Reason(e) + ")";
            if (!brokenLogged)
            {
                brokenLogged = true;
                SeasonsLog.Failed("seasons-eve-layers", "the storm cloud in EVE's layers failed and the original maps are put back; the cells use the mod's own layer", e);
            }
            Release();
        }

        private static double Peak(double strength)
        {
            return Mathx.SmoothStep(0.02, 0.45, strength);
        }

        private static int CountNear(StormCells.Cell[] cells, int count)
        {
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                if (Peak(cells[i].Strength) <= 0.0) continue;
                if (cells[i].DistanceM - cells[i].RadiusM * EveStormMath.MaxReachFactor > RangeM) continue;
                n++;
            }
            return n;
        }

        private static bool HasVisible(StormCells.Cell[] cells, int count)
        {
            for (int i = 0; i < count; i++) if (Peak(cells[i].Strength) > 0.0) return true;
            return false;
        }

        // Looks for the layers of the body that carry the cell, and drops the ones that went away with their scene.
        private static void Scan(CelestialBody body)
        {
            var found = new List<object>();
            IEnumerable list = getList.Invoke(null, null) as IEnumerable;
            if (list != null)
            {
                foreach (object obj in list)
                {
                    if (obj == null) continue;
                    string name = nameProperty.GetValue(obj, null) as string;
                    if (name == null || name.StartsWith("ReDefinition-", StringComparison.Ordinal)) continue;
                    if (!string.Equals(bodyProperty.GetValue(obj, null) as string, body.name, StringComparison.Ordinal)) continue;
                    object volume = volumeProperty.GetValue(obj, null);
                    if (volume == null) continue;
                    found.Add(volume);
                    if (Known(volume)) continue;
                    Layer layer = Describe(name, obj, volume);
                    if (layer != null) layers.Add(layer);
                }
            }
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                if (found.Contains(layers[i].Volume)) continue;
                DropGone(layers[i]);
                layers.RemoveAt(i);
            }
            if (layers.Count == 0 && !loggedNone)
            {
                loggedNone = true;
                SeasonsLog.Say("storm clouds in EVE's layers: no layer of " + body.name + " has a snow type or a Stratus type with a coverage map and a type map; the cells use the mod's own layer");
            }
            if (layers.Count > 0) LogLayers();
            UpdateBase();
            scanned = true;
        }

        private static bool Known(object volume)
        {
            for (int i = 0; i < layers.Count; i++) if (ReferenceEquals(layers[i].Volume, volume)) return true;
            return false;
        }

        // Reads a layer's cloud types and decides whether and how it carries the cell. Null: it does not.
        private static Layer Describe(string name, object obj, object volume)
        {
            object coverage = coverageMapProperty.GetValue(volume, null);
            object type = typeMapProperty.GetValue(volume, null);
            if (coverage == null || type == null) return null;
            var names = new List<string>();
            var minimums = new List<double>();
            var maximums = new List<double>();
            IEnumerable types = typesProperty.GetValue(volume, null) as IEnumerable;
            if (types == null) return null;
            foreach (object t in types)
            {
                names.Add(t != null ? typeNameProperty.GetValue(t, null) as string : null);
                minimums.Add(t != null ? (float)typeMinProperty.GetValue(t, null) : 0);
                maximums.Add(t != null ? (float)typeMaxProperty.GetValue(t, null) : 0);
            }
            string[] nameArray = names.ToArray();
            int index = EveStormMath.FindType(nameArray, SnowTypeNames);
            bool snow = index >= 0;
            if (!snow)
            {
                index = EveStormMath.FindType(nameArray, BodyTypeNames);
                if (index < 0 || minimums[index] >= BodyLayerBaseMaxAslM) return null;
            }
            var layer = new Layer
            {
                Name = name,
                Object = obj,
                Volume = volume,
                Pqs = pqsField.GetValue(obj),
                CoverageWrapper = coverage,
                TypeWrapper = type,
                SnowRole = snow,
                TypeIndex = index,
                TypeCount = nameArray.Length,
                TargetType = EveStormMath.CloudTypeByte(index, nameArray.Length),
                BaseAslM = minimums[index],
                TopAslM = maximums[index]
            };
            if (layer.Pqs == null) return null;
            if (!snow && layer2DProperty != null)
            {
                layer.Layer2D = layer2DProperty.GetValue(obj, null);
                object cloudsMat = layer.Layer2D != null ? cloudsMatProperty.GetValue(layer.Layer2D, null) : null;
                layer.MainTexWrapper = cloudsMat != null ? mainTexField.GetValue(cloudsMat) : null;
                layer.Rendering = layer.Layer2D != null ? renderingMaterialProperty.GetValue(layer.Layer2D, null) as Material : null;
            }
            return layer;
        }

        private static void LogLayers()
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < layers.Count; i++)
            {
                Layer l = layers[i];
                if (i > 0) text.Append("; ");
                text.Append(l.Name).Append(l.SnowRole ? " (snow, type " : " (cloud, type ").Append(l.TypeIndex.ToString(CultureInfo.InvariantCulture))
                    .Append(" of ").Append(l.TypeCount.ToString(CultureInfo.InvariantCulture)).Append(" = ")
                    .Append(l.TargetType.ToString(CultureInfo.InvariantCulture)).Append("/255, ")
                    .Append(l.BaseAslM.ToString("0", CultureInfo.InvariantCulture)).Append(" to ").Append(l.TopAslM.ToString("0", CultureInfo.InvariantCulture))
                    .Append(" m, 2D ").Append(l.Rendering != null && l.MainTexWrapper != null ? "yes" : "no").Append(')');
            }
            string joined = text.ToString();
            if (joined == layersLogged) return;
            layersLogged = joined;
            SeasonsLog.Say("storm clouds in EVE's layers: " + joined);
        }

        private static void UpdateBase()
        {
            double b = double.MaxValue, t = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i].SnowRole) continue;
                b = Math.Min(b, layers[i].BaseAslM);
                t = Math.Max(t, layers[i].TopAslM);
            }
            baseAslM = b < double.MaxValue ? b : DefaultBaseAslM;
            topAslM = t > 0 ? t : DefaultTopAslM;
        }

        // A layer that is gone: its objects belong to a scene that unloaded, so nothing of EVE's is put back; ours are freed.
        private static void DropGone(Layer layer)
        {
            if (swirlLayer == layer) UnbindSwirl();
            FreeWork(layer);
        }

        // Makes the layer's working maps from the originals: reads the bytes, makes the textures, and puts them in the wrapper and the
        // materials. False where the original cannot be read (the layer is then left as it is).
        private static bool Swap(Layer layer)
        {
            if (layer.Swapped) return true;
            Texture2D coverage = textureField.GetValue(layer.CoverageWrapper) as Texture2D;
            Texture2D type = textureField.GetValue(layer.TypeWrapper) as Texture2D;
            if (coverage == null || type == null || !(bool)initializedField.GetValue(layer.CoverageWrapper))
            {
                if (!layer.Checked) SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "' has not loaded its maps yet");
                layer.Checked = true;
                return false;
            }
            string howCoverage, howType;
            byte[] coverageBytes = ReadRed(coverage, out howCoverage);
            byte[] typeBytes = ReadRed(type, out howType);
            if (coverageBytes == null || typeBytes == null || coverage.width != type.width || coverage.height != type.height)
            {
                if (!layer.Checked)
                    SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "' left alone: coverage " + coverage.width + "x" + coverage.height + " (" + howCoverage
                                   + "), type " + type.width + "x" + type.height + " (" + howType + ")");
                layer.Checked = true;
                return false;
            }
            layer.Width = coverage.width;
            layer.Height = coverage.height;
            layer.CoverageOriginal = coverage;
            layer.TypeOriginal = type;
            layer.CoverageOriginalBytes = coverageBytes;
            layer.TypeOriginalBytes = typeBytes;
            layer.CoverageBytes = (byte[])coverageBytes.Clone();
            layer.TypeBytes = (byte[])typeBytes.Clone();
            layer.CoverageWork = MakeMap(coverage, layer.CoverageBytes, "ReDefinition Seasons storm in " + layer.Name + " coverage");
            layer.TypeWork = MakeMap(type, layer.TypeBytes, "ReDefinition Seasons storm in " + layer.Name + " type");
            layer.Main = materialProperty.GetValue(layer.Volume, null) as Material;
            layer.Probe = probeMaterialProperty.GetValue(layer.Volume, null) as Material;
            SetMaps(layer, layer.CoverageWork, layer.TypeWork);
            layer.PaintedCount = 0;
            layer.Swapped = true;
            layer.Checked = false;
            double mb = 4.0 * layer.CoverageBytes.Length / (1024.0 * 1024.0) + 2.0 * layer.CoverageBytes.Length / (1024.0 * 1024.0) * 2;
            SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "' takes the cell: coverage " + layer.Width + "x" + layer.Height + " " + coverage.format + " (read by "
                           + howCoverage + ", " + (coverage.isReadable ? "readable" : "not readable") + "), type map " + type.format + " (" + howType + "), "
                           + mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB of copies, target type " + layer.TargetType.ToString(CultureInfo.InvariantCulture)
                           + "/255, time fade " + Fade(layer).ToString("0.00", CultureInfo.InvariantCulture));
            return true;
        }

        private static float Fade(Layer layer)
        {
            object value = fadeProperty != null ? fadeProperty.GetValue(layer.Volume, null) : null;
            return value is float ? (float)value : -1f;
        }

        // The first byte of every texel of a texture (its red value). Readable R8 textures give their raw data; other readable
        // ones their pixels; a texture that is not readable is drawn into a render texture and read back.
        private static byte[] ReadRed(Texture2D texture, out string how)
        {
            int w = texture.width, h = texture.height;
            var result = new byte[w * h];
            if (texture.isReadable && texture.format == TextureFormat.R8)
            {
                byte[] raw = texture.GetRawTextureData();
                if (raw.Length >= result.Length)
                {
                    Buffer.BlockCopy(raw, 0, result, 0, result.Length);
                    how = "raw data";
                    return result;
                }
            }
            if (texture.isReadable)
            {
                Color32[] pixels = texture.GetPixels32();
                for (int i = 0; i < result.Length && i < pixels.Length; i++) result[i] = pixels[i].r;
                how = "pixels";
                return result;
            }
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(texture, temporary);
                RenderTexture.active = temporary;
                copy = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                Color32[] pixels = copy.GetPixels32();
                for (int i = 0; i < result.Length && i < pixels.Length; i++) result[i] = pixels[i].r;
                how = "blit and read back";
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
        }

        private static Texture2D MakeMap(Texture2D original, byte[] bytes, string name)
        {
            var texture = new Texture2D(original.width, original.height, TextureFormat.R8, false, true)
            {
                name = name,
                wrapMode = original.wrapMode,
                filterMode = original.filterMode
            };
            texture.LoadRawTextureData(bytes);
            texture.Apply(false, false);
            return texture;
        }

        private static void SetMaps(Layer layer, Texture coverage, Texture type)
        {
            textureField.SetValue(layer.CoverageWrapper, coverage);
            textureField.SetValue(layer.TypeWrapper, type);
            if (layer.Main != null)
            {
                layer.Main.SetTexture("CloudCoverage", coverage);
                layer.Main.SetTexture("CloudType", type);
            }
            if (layer.Probe != null)
            {
                layer.Probe.SetTexture("CloudCoverage", coverage);
                layer.Probe.SetTexture("CloudType", type);
            }
        }

        // Puts EVE's originals back and frees ours.
        private static void Unswap(Layer layer)
        {
            if (!layer.Swapped) return;
            try
            {
                SetMaps(layer, layer.CoverageOriginal, layer.TypeOriginal);
            }
            catch (Exception)
            {
                // The layer is gone with its scene; the originals went with it.
            }
            FreeWork(layer);
            if (layer.Paints > 0)
                SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "' is back on its own maps (" + layer.Paints.ToString(CultureInfo.InvariantCulture)
                               + " paints, average " + (layer.PaintMsTotal / layer.Paints).ToString("0.0", CultureInfo.InvariantCulture) + " ms)");
            layer.Paints = 0;
            layer.PaintMsTotal = 0;
        }

        private static void FreeWork(Layer layer)
        {
            if (layer.CoverageWork != null) UnityEngine.Object.Destroy(layer.CoverageWork);
            if (layer.TypeWork != null) UnityEngine.Object.Destroy(layer.TypeWork);
            layer.CoverageWork = null;
            layer.TypeWork = null;
            layer.CoverageOriginal = null;
            layer.TypeOriginal = null;
            layer.CoverageBytes = null;
            layer.CoverageOriginalBytes = null;
            layer.TypeBytes = null;
            layer.TypeOriginalBytes = null;
            layer.Main = null;
            layer.Probe = null;
            layer.Scratch = new byte[0];
            layer.PaintedCount = 0;
            layer.Swapped = false;
        }

        // The rotation of a layer's frame now: a body-frame direction times it is the direction EVE samples the maps at.
        private static bool Rotation(Layer layer, CelestialBody body, double ut, out Matrix4x4 matrix)
        {
            rotationArgs[0] = ut;
            rotationArgs[1] = body.rotationAngle;
            rotationArgs[2] = null;
            rotationArgs[3] = null;
            getRotation.Invoke(layer.Pqs, rotationArgs);
            if (rotationArgs[3] is Matrix4x4)
            {
                matrix = (Matrix4x4)rotationArgs[3];
                return true;
            }
            matrix = Matrix4x4.identity;
            return false;
        }

        // The direction of a place in the body's own frame, KSP's: the first run found Geo's convention 16 degrees off the layer's frame.
        private static void BodyDirection(CelestialBody body, double latitude, double longitude, out double x, out double y, out double z)
        {
            Vector3d d = body.GetRelSurfaceNVector(latitude, longitude);
            x = d.x; y = d.y; z = d.z;
        }

        // The direction of a place in a layer's frame. Live (the volume's matrix is current) the world position through the volume's
        // own matrix, which EVE samples with; otherwise the body-frame direction through the layer's rotation. The first time both are
        // known their angle is logged.
        private static void CellDirection(Layer layer, CelestialBody body, Matrix4x4 rotation, StormCells.Cell cell, bool live, out double x, out double y, out double z)
        {
            double bx, by, bz;
            BodyDirection(body, cell.Latitude, cell.Longitude, out bx, out by, out bz);
            Vector3 viaBody = rotation.MultiplyPoint3x4(new Vector3((float)bx, (float)by, (float)bz)).normalized;
            if (live)
            {
                Matrix4x4 volume = (Matrix4x4)rotationProperty.GetValue(layer.Volume, null);
                Vector3 world = (Vector3)body.GetWorldSurfacePosition(cell.Latitude, cell.Longitude, 0.0);
                Vector3 viaVolume = volume.MultiplyPoint(world).normalized;
                if (viaVolume.sqrMagnitude > 0.5f)
                {
                    if (!frameLogged)
                    {
                        frameLogged = true;
                        SeasonsLog.Say("storm clouds in EVE's layers: frame check on '" + layer.Name + "': the body-frame direction of a cell through the layer's rotation is "
                                       + Vector3.Angle(viaVolume, viaBody).ToString("0.00", CultureInfo.InvariantCulture)
                                       + " degrees from the volume's own direction; the volume's is used while it is current");
                    }
                    x = viaVolume.x; y = viaVolume.y; z = viaVolume.z;
                    return;
                }
            }
            x = viaBody.x; y = viaBody.y; z = viaBody.z;
        }

        private static void PaintLayer(CelestialBody body, Layer layer, StormCells.Cell[] cells, int count, double ut, bool live)
        {
            float now = Time.realtimeSinceStartup;
            if (now < layer.NextPaint) return;
            if (!Swap(layer)) return;
            layer.NextPaint = now + (layer.Width >= 2048 ? PaintIntervalLarge : PaintIntervalSmall);
            var stopwatch = Stopwatch.StartNew();
            Matrix4x4 rotation;
            if (!Rotation(layer, body, ut, out rotation)) return;
            for (int i = 0; i < layer.PaintedCount; i++)
            {
                EveStormMath.Restore(layer.CoverageBytes, layer.CoverageOriginalBytes, layer.Painted[i], layer.Width);
                EveStormMath.Restore(layer.TypeBytes, layer.TypeOriginalBytes, layer.Painted[i], layer.Width);
            }
            layer.PaintedCount = 0;
            long texels = 0;
            int painted = 0;
            for (int i = 0; i < count && layer.PaintedCount < layer.Painted.Length; i++)
            {
                double strength = cells[i].Strength;
                double peak = Peak(strength);
                if (peak <= 0.0 || cells[i].DistanceM - cells[i].RadiusM * EveStormMath.MaxReachFactor > RangeM) continue;
                double radiusM = cells[i].RadiusM * 1.05 * EveStormMath.GrowthScale(strength);
                double cx, cy, cz;
                CellDirection(layer, body, rotation, cells[i], live, out cx, out cy, out cz);
                int lobeCount = EveStormMath.Lobes(cells[i].Key, radiusM, cells[i].Heading, ut, lobes);
                EveCoverageMath.Rect rect = EveStormMath.CellRect(cx, cy, cz, radiusM, body.Radius, layer.Width, layer.Height);
                int size = EveStormMath.MaskSize(rect);
                if (size == 0) continue;
                if (layer.Scratch.Length < size) layer.Scratch = new byte[size];
                EveStormMath.PaintMask(layer.Scratch, rect, layer.Width, layer.Height, cx, cy, cz, body.Radius, lobes, lobeCount, peak, cells[i].Key);
                EveStormMath.Composite(layer.CoverageBytes, layer.TypeBytes, layer.TargetType, layer.Scratch, rect, layer.Width);
                layer.Painted[layer.PaintedCount++] = rect;
                texels += size;
                painted++;
                CheckGround(body, cells[i]);
            }
            layer.CoverageWork.LoadRawTextureData(layer.CoverageBytes);
            layer.CoverageWork.Apply(false, false);
            layer.TypeWork.LoadRawTextureData(layer.TypeBytes);
            layer.TypeWork.Apply(false, false);
            stopwatch.Stop();
            layer.PaintMsTotal += stopwatch.Elapsed.TotalMilliseconds;
            layer.Paints++;
            if (!layer.Checked && painted > 0 && live && layer.Paints >= 3) Verify(body, layer, cells, count, ut, texels, stopwatch.Elapsed.TotalMilliseconds);
        }

        // Once per swap, when the volume has been current for a frame: EVE's own SampleCoverage at the strongest cell's centre against
        // the composited map there, and the layer's time fade.
        private static void Verify(CelestialBody body, Layer layer, StormCells.Cell[] cells, int count, double ut, long texels, double ms)
        {
            int best = -1;
            double bestPeak = 0;
            for (int i = 0; i < count; i++)
            {
                double p = Peak(cells[i].Strength);
                if (p > bestPeak && cells[i].DistanceM - cells[i].RadiusM * EveStormMath.MaxReachFactor <= RangeM) { bestPeak = p; best = i; }
            }
            if (best < 0 || bestPeak < 0.3) return;
            layer.Checked = true;
            double altitude = layer.BaseAslM + 0.3 * (layer.TopAslM - layer.BaseAslM);
            Vector3 world = (Vector3)body.GetWorldSurfacePosition(cells[best].Latitude, cells[best].Longitude, altitude);
            sampleArgs[0] = world;
            sampleArgs[1] = 0f;
            sampleArgs[2] = false;
            object result = sampleCoverage.Invoke(layer.Volume, sampleArgs);
            double read = result is float ? (double)(float)result : -1;
            SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "' check: EVE's SampleCoverage at the centre of the strongest cell reads "
                           + read.ToString("0.###", CultureInfo.InvariantCulture) + " at " + altitude.ToString("0", CultureInfo.InvariantCulture) + " m (time fade "
                           + Fade(layer).ToString("0.00", CultureInfo.InvariantCulture) + "), painted " + texels.ToString(CultureInfo.InvariantCulture) + " texels in "
                           + ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
        }

        // Warns once per cell when the ground at its centre is higher than the top of the cloud: EVE's altitudes are above sea level, so the
        // cloud would be under the ground there.
        private static void CheckGround(CelestialBody body, StormCells.Cell cell)
        {
            if (groundChecked.Contains(cell.Key)) return;
            if (groundChecked.Count > 256) groundChecked.Clear();
            groundChecked.Add(cell.Key);
            double ground = 0;
            try
            {
                ground = body.TerrainAltitude(cell.Latitude, cell.Longitude, false);
            }
            catch (Exception)
            {
                return;
            }
            string place = "(" + cell.Latitude.ToString("0.0", CultureInfo.InvariantCulture) + ", " + cell.Longitude.ToString("0.0", CultureInfo.InvariantCulture) + ")";
            if (GroundAboveCloud(ground))
                SeasonsLog.WarnOnce("storm cell " + place + " stands over ground at " + ground.ToString("0", CultureInfo.InvariantCulture)
                                    + " m, higher than the top of EVE's cloud layer (" + topAslM.ToString("0", CultureInfo.InvariantCulture) + " m above sea level); its cloud is under the ground there");
            else
                SeasonsLog.Say("storm cell " + place + ": ground " + ground.ToString("0", CultureInfo.InvariantCulture) + " m, EVE's cloud layer from "
                               + baseAslM.ToString("0", CultureInfo.InvariantCulture) + " to " + topAslM.ToString("0", CultureInfo.InvariantCulture) + " m above sea level");
        }

        // ----- The 2D layer -----

        private static void PaintSwirl(CelestialBody body, StormCells.Cell[] cells, int count, double ut, float now)
        {
            if (swirlFailed) return;
            Layer layer = null;
            for (int i = 0; i < layers.Count; i++) if (layers[i].Rendering != null && layers[i].MainTexWrapper != null) { layer = layers[i]; break; }
            if (layer == null) return;
            int visible = 0;
            for (int i = 0; i < count; i++) if (Peak(cells[i].Strength) > 0.0) visible++;
            if (swirlBound && now < nextSwirl && visible == swirlCells) return;
            nextSwirl = now + SwirlInterval;
            try
            {
                if (!swirlBound && !BindSwirl(layer)) return;
                Matrix4x4 rotation;
                if (!Rotation(layer, body, ut, out rotation)) return;
                var stopwatch = Stopwatch.StartNew();
                RenderTexture previous = RenderTexture.active;
                bool fog = RenderSettings.fog;
                RenderSettings.fog = false;
                try
                {
                    if (!swirlTarget.IsCreated()) swirlTarget.Create();
                    Graphics.Blit(swirlOriginal, swirlTarget);
                    RenderTexture.active = swirlTarget;
                    GL.PushMatrix();
                    GL.LoadOrtho();
                    swirlMaterial.SetPass(0);
                    for (int i = 0; i < count; i++)
                    {
                        double strength = cells[i].Strength;
                        if (Peak(strength) <= 0.0) continue;
                        double cx, cy, cz;
                        double bx, by, bz;
                        BodyDirection(body, cells[i].Latitude, cells[i].Longitude, out bx, out by, out bz);
                        Vector3 d = rotation.MultiplyPoint3x4(new Vector3((float)bx, (float)by, (float)bz)).normalized;
                        cx = d.x; cy = d.y; cz = d.z;
                        int n = EveStormMath.Swirl(cells[i].Key, cells[i].RadiusM, strength, cells[i].Latitude >= 0, cells[i].Heading, puffs);
                        for (int p = 0; p < n; p++) DrawPuff(body, cx, cy, cz, puffs[p], cells[i].Key + (ulong)p);
                    }
                    GL.PopMatrix();
                }
                finally
                {
                    RenderTexture.active = previous;
                    RenderSettings.fog = fog;
                }
                swirlTarget.GenerateMips();
                stopwatch.Stop();
                swirlLayer.Rendering.SetTexture("_MainTex", swirlTarget);
                if (visible != swirlCells)
                {
                    swirlCells = visible;
                    SeasonsLog.Say("storm clouds in EVE's layers: 2D swirl drawn for " + visible.ToString(CultureInfo.InvariantCulture) + " cells in "
                                   + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms, display radius at least "
                                   + (EveStormMath.MinSwirlRadiusM / 1000).ToString("0", CultureInfo.InvariantCulture) + " km");
                }
            }
            catch (Exception e)
            {
                swirlFailed = true;
                SeasonsLog.Failed("seasons-eve-swirl", "the swirl in EVE's 2D layer failed and the original picture is put back", e);
                UnbindSwirl();
            }
        }

        private static bool BindSwirl(Layer layer)
        {
            Texture2D original = textureField.GetValue(layer.MainTexWrapper) as Texture2D;
            if (original == null)
            {
                if (!swirlLogged)
                {
                    swirlLogged = true;
                    SeasonsLog.Say("storm clouds in EVE's layers: the 2D layer of '" + layer.Name + "' has no picture loaded; the swirl is not drawn");
                }
                return false;
            }
            if (swirlMaterial == null)
            {
                Shader shader = StormShaders.Find(SwirlShaderNames);
                if (shader == null)
                {
                    swirlFailed = true;
                    SeasonsLog.Say("storm clouds in EVE's layers: no shader to draw the swirl with (" + string.Join(", ", SwirlShaderNames) + "); the 2D layer stays as it is");
                    return false;
                }
                swirlMaterial = new Material(shader) { name = "ReDefinition Seasons swirl" };
                if (!swirlShaderLogged)
                {
                    swirlShaderLogged = true;
                    SeasonsLog.Say("storm clouds in EVE's layers: the swirl is drawn with " + shader.name);
                }
            }
            int width = Math.Min(original.width, SwirlMaxWidth);
            int height = Math.Max(1, (int)Math.Round(width * (double)original.height / original.width));
            swirlTarget = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "ReDefinition Seasons storm 2D",
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Trilinear,
                wrapMode = original.wrapMode,
                anisoLevel = Math.Max(1, original.anisoLevel)
            };
            swirlTarget.Create();
            swirlOriginal = original;
            swirlLayer = layer;
            swirlBound = true;
            swirlCells = -1;
            double mb = (width * (double)height * 4.0) * 4.0 / 3.0 / (1024.0 * 1024.0);
            SeasonsLog.Say("storm clouds in EVE's layers: the 2D layer of '" + layer.Name + "' shows a copy of its picture (" + original.width + "x" + original.height + " "
                           + original.format + ") at " + width + "x" + height + " with the swirl, " + mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB of video memory");
            return true;
        }

        // Draws one puff as a fan of triangles into the active render texture, once more shifted by a map width where it crosses the
        // edge of the map.
        private static void DrawPuff(CelestialBody body, double cx, double cy, double cz, EveStormMath.Puff puff, ulong seed)
        {
            if (!(puff.RadiusM > 0) || !(puff.Alpha > 0)) return;
            double px, py, pz;
            EveStormMath.Offset(cx, cy, cz, puff.EastM, puff.NorthM, body.Radius, out px, out py, out pz);
            int vertices = EveStormMath.FanVertices(px, py, pz, puff.RadiusM * EveCoverageMath.ReachShare / body.Radius, seed, FanSegments, FanRings, fanU, fanV, fanAlpha);
            if (vertices == 0) return;
            int indices = EveStormMath.FanIndices(FanSegments, FanRings, fanIndices);
            double minU = double.MaxValue, maxU = double.MinValue;
            for (int i = 0; i < vertices; i++)
            {
                minU = Math.Min(minU, fanU[i]);
                maxU = Math.Max(maxU, fanU[i]);
            }
            DrawFan(vertices, indices, puff, 0.0);
            if (minU < 0.0) DrawFan(vertices, indices, puff, 1.0);
            if (maxU > 1.0) DrawFan(vertices, indices, puff, -1.0);
        }

        private static void DrawFan(int vertices, int indices, EveStormMath.Puff puff, double shift)
        {
            float grey = (float)puff.Grey;
            GL.Begin(GL.TRIANGLES);
            for (int k = 0; k < indices; k++)
            {
                int i = fanIndices[k];
                GL.Color(Probe ? new Color(1f, 0.1f, 0.1f, (float)fanAlpha[i]) : new Color(grey, grey, grey, (float)(puff.Alpha * fanAlpha[i])));
                GL.Vertex3((float)(fanU[i] + shift), (float)fanV[i], 0f);
            }
            GL.End();
        }

        // Puts the 2D layer's own picture back and frees the render texture.
        private static void UnbindSwirl()
        {
            if (!swirlBound && swirlTarget == null) return;
            try
            {
                if (swirlLayer != null && swirlLayer.Rendering != null && swirlOriginal != null) swirlLayer.Rendering.SetTexture("_MainTex", swirlOriginal);
            }
            catch (Exception)
            {
                // The layer is gone with its scene.
            }
            if (swirlTarget != null)
            {
                swirlTarget.Release();
                UnityEngine.Object.Destroy(swirlTarget);
            }
            swirlTarget = null;
            swirlOriginal = null;
            swirlLayer = null;
            swirlBound = false;
            swirlCells = -1;
        }

        // Puts everything back to EVE's own maps and pictures and frees what was made (the setting is off, the scene unloads, or a step
        // failed). Each step on its own.
        internal static void Release()
        {
            for (int i = 0; i < layers.Count; i++)
            {
                try
                {
                    Unswap(layers[i]);
                }
                catch (Exception e)
                {
                    SeasonsLog.Failed("seasons-eve-layers-release", "putting EVE's maps back failed", e);
                }
            }
            try
            {
                UnbindSwirl();
            }
            catch (Exception e)
            {
                SeasonsLog.Failed("seasons-eve-swirl-release", "putting EVE's 2D picture back failed", e);
            }
            lastCellTime = 0f;
        }

        // The layers of a scene that is gone: nothing of theirs is touched again.
        internal static void Forget()
        {
            Release();
            for (int i = 0; i < layers.Count; i++) FreeWork(layers[i]);
            layers.Clear();
            groundChecked.Clear();
            layersLogged = "";
            nextScan = 0f;
            loggedNone = false;
            frameLogged = false;
            scanned = false;
        }
    }
}
