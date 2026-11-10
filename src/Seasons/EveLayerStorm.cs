using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ReDefinition.Core;
using Unity.Collections;
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

        // The types a cell turns the body layer to, the first that the layer has; the layer's own deck type where it has none.
        private static readonly string[] BodyCellTypeNames = { "Cumulus" };
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
            // EVE's maps, and our working maps: readable R8 copies (EVE samples them on the CPU). A cell is composited into a
            // rectangle read from the original; the rectangle is written into the working map's data and copied on the GPU.
            internal Texture2D CoverageOriginal, TypeOriginal, CoverageWork, TypeWork;
            // The originals as bytes, only where a rectangle of the original cannot be read (not readable, or its format refuses).
            internal byte[] CoverageOriginalBytes, TypeOriginalBytes;
            internal readonly EveCoverageMath.Rect[] Painted = new EveCoverageMath.Rect[StormCells.Capacity];
            internal int PaintedCount;
            internal ulong Signature;
            internal int Ticks;
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
        private static PropertyInfo materialProperty, probeMaterialProperty, rotationProperty, innerProperty, outerProperty, fadeProperty, fadeDensityProperty, enabledProperty;
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

        // Another one: the swirl is replaced by EveStormMath.Pattern, black, white, red and erasing discs at full and half opacity,
        // to see how EVE's shader treats the picture's colour and opacity.
        internal static bool ProbePattern;

        // Whether the swirl's material can erase (our shader from the bundle); the fallback shaders only paint.
        private static bool swirlErases;

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

        // Whether the storm cloud is in EVE's own layers now: the setting stormClouds is Existing, the lookup was complete and a layer was found.
        // EveCellLayer then does not show its layer of the mod's config, and the volume draws the veil only.
        internal static bool Supplying
        {
            get
            {
                return SeasonsSettings.StormClouds == SeasonsSettings.StormCloudModes.Existing && status == Status.Ready && BodyLayerFound;
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
                // The density fade is only read for the log: EVE multiplies it into SampleCoverage.
                fadeDensityProperty = volume.GetProperty("CurrentTimeFadeDensity", pub);
                enabledProperty = volume.GetProperty("enabled", pub);
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
            if (body == null || SeasonsSettings.StormClouds != SeasonsSettings.StormCloudModes.Existing)
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
                    SeasonsLog.Say("storm clouds in EVE's layers: " + summary + "; the mod's own EVE layer carries the cells where it can (cells need EVE with Volumetric Clouds)");
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
            // The body layer's cell is its dense type (Cumulus: eight times the density of Stratus, and taller), not the thin deck's own,
            // so that the cell is a darker, heavier mass than the overcast around it. The base and top are the deck's.
            int target = snow ? index : EveStormMath.FindType(nameArray, BodyCellTypeNames);
            if (target < 0) target = index;
            var layer = new Layer
            {
                Name = name,
                Object = obj,
                Volume = volume,
                Pqs = pqsField.GetValue(obj),
                CoverageWrapper = coverage,
                TypeWrapper = type,
                SnowRole = snow,
                TypeIndex = target,
                TypeCount = nameArray.Length,
                TargetType = EveStormMath.CloudTypeByte(target, nameArray.Length),
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
            if (coverage.width != type.width || coverage.height != type.height)
            {
                if (!layer.Checked)
                    SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "' left alone: coverage " + coverage.width + "x" + coverage.height + ", type "
                                   + type.width + "x" + type.height);
                layer.Checked = true;
                return false;
            }
            if (!SystemInfo.copyTextureSupport.HasFlag(UnityEngine.Rendering.CopyTextureSupport.Basic))
            {
                if (!layer.Checked) SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "' left alone: the graphics device cannot copy a region of a texture (" + SystemInfo.copyTextureSupport + ")");
                layer.Checked = true;
                return false;
            }
            layer.Width = coverage.width;
            layer.Height = coverage.height;
            string howCoverage = "unreadable", howType = "unreadable";
            try
            {
                layer.CoverageWork = MakeWorkMap(coverage, "ReDefinition Seasons storm in " + layer.Name + " coverage", out howCoverage);
                layer.TypeWork = MakeWorkMap(type, "ReDefinition Seasons storm in " + layer.Name + " type", out howType);
            }
            catch (Exception e)
            {
                FreeWork(layer);
                layer.Width = 0;
                layer.Height = 0;
                if (!layer.Checked) SeasonsLog.Failed("seasons-eve-copy-" + layer.Name, "storm clouds in EVE's layers: '" + layer.Name + "' left alone: its working maps could not be made", e);
                layer.Checked = true;
                return false;
            }
            layer.CoverageOriginal = coverage;
            layer.TypeOriginal = type;
            layer.CoverageOriginalBytes = null;
            layer.TypeOriginalBytes = null;
            if (!coverage.isReadable || !type.isReadable)
            {
                // A texture that is not readable can only be read whole, by drawing it into a target.
                string unused;
                layer.CoverageOriginalBytes = ReadRed(coverage, out unused);
                layer.TypeOriginalBytes = ReadRed(type, out unused);
            }
            layer.Main = materialProperty.GetValue(layer.Volume, null) as Material;
            layer.Probe = probeMaterialProperty.GetValue(layer.Volume, null) as Material;
            SetMaps(layer, layer.CoverageWork, layer.TypeWork);
            layer.PaintedCount = 0;
            layer.Signature = 0;
            layer.Ticks = 0;
            layer.Swapped = true;
            layer.Checked = false;
            double mapMb = (double)layer.Width * layer.Height / (1024.0 * 1024.0);
            SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "' takes the cell: coverage " + layer.Width + "x" + layer.Height + " " + coverage.format + " ("
                           + (coverage.isReadable ? "readable" : "not readable") + "), type map " + type.format + ", working maps R8, readable, no other copy: "
                           + (4 * mapMb).ToString("0.0", CultureInfo.InvariantCulture) + " MB in main and video memory (whole-map copies took "
                           + (8 * mapMb).ToString("0.0", CultureInfo.InvariantCulture) + " MB), " + howCoverage + ", " + howType + ", target type "
                           + layer.TargetType.ToString(CultureInfo.InvariantCulture) + "/255, time fade " + Fade(layer).ToString("0.00", CultureInfo.InvariantCulture));
            return true;
        }

        // The R8 working map of an original, readable (EVE samples a layer's coverage on the CPU too, SampleCoverage). It is read in
        // bands of rows, so that the 16 bytes a pixel of GetPixels are never held for the whole map at once.
        private static Texture2D MakeWorkMap(Texture2D original, string name, out string how)
        {
            int w = original.width, h = original.height;
            var work = new Texture2D(w, h, TextureFormat.R8, false, true)
            {
                name = name,
                wrapMode = original.wrapMode,
                filterMode = original.filterMode
            };
            NativeArray<byte> raw = work.GetRawTextureData<byte>();
            how = "read in bands of " + BandRows + " rows";
            try
            {
                if (!original.isReadable) throw new InvalidOperationException("the original is not readable");
                var band = new byte[w * BandRows];
                for (int y = 0; y < h; y += BandRows)
                {
                    int rows = Math.Min(BandRows, h - y);
                    ReadPixelsInto(original, 0, y, w, rows, band, w, 0);
                    NativeArray<byte>.Copy(band, 0, raw, y * w, w * rows);
                }
            }
            catch (Exception)
            {
                byte[] bytes = ReadRed(original, out how);
                how = "read whole by " + how;
                NativeArray<byte>.Copy(bytes, 0, raw, 0, bytes.Length);
            }
            work.Apply(false, false);
            return work;
        }

        // The rows of a band of the original read at once.
        private const int BandRows = 128;

        // The layer's time fade as SampleCoverage applies it: the coverage fade times the density fade. 0 where the layer's schedule
        // (timeSettings) has it off at this time; Kerbin-Weather-1 and -weather-2 take turns.
        private static float Fade(Layer layer)
        {
            object value = fadeProperty != null ? fadeProperty.GetValue(layer.Volume, null) : null;
            if (!(value is float)) return -1f;
            object density = fadeDensityProperty != null ? fadeDensityProperty.GetValue(layer.Volume, null) : null;
            return (float)value * (density is float ? (float)density : 1f);
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

        // The values of a rectangle of an original map, row by row (rect.Cols per row; the columns wrap round the map's width), into
        // destination. A map that was read whole is read from its bytes. Otherwise the original's pixels are read for the rectangle only,
        // and where that fails the whole map is read once into bytes (the log says so).
        private static void ReadRect(Layer layer, bool coverage, EveCoverageMath.Rect rect, byte[] destination)
        {
            Texture2D original = coverage ? layer.CoverageOriginal : layer.TypeOriginal;
            byte[] bytes = coverage ? layer.CoverageOriginalBytes : layer.TypeOriginalBytes;
            if (bytes == null)
            {
                try
                {
                    int first = Math.Min(rect.Cols, layer.Width - rect.Col0);
                    ReadPixelsInto(original, rect.Col0, rect.Row0, first, rect.Rows, destination, rect.Cols, 0);
                    if (rect.Cols > first) ReadPixelsInto(original, 0, rect.Row0, rect.Cols - first, rect.Rows, destination, rect.Cols, first);
                    return;
                }
                catch (Exception e)
                {
                    string how;
                    bytes = ReadRed(original, out how);
                    if (coverage) layer.CoverageOriginalBytes = bytes; else layer.TypeOriginalBytes = bytes;
                    SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "': a rectangle of the original could not be read (" + CompatibilityLog.Reason(e)
                                   + "), the whole map is read by " + how + " instead (" + ((double)bytes.Length / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB)");
                }
            }
            for (int r = 0; r < rect.Rows; r++)
            {
                int rowStart = (rect.Row0 + r) * layer.Width;
                for (int k = 0; k < rect.Cols; k++) destination[r * rect.Cols + k] = bytes[rowStart + (rect.Col0 + k) % layer.Width];
            }
        }

        private static void ReadPixelsInto(Texture2D original, int x, int y, int cols, int rows, byte[] destination, int destinationCols, int destinationCol)
        {
            Color[] pixels = original.GetPixels(x, y, cols, rows, 0);
            for (int r = 0; r < rows; r++)
            {
                for (int k = 0; k < cols; k++) destination[r * destinationCols + destinationCol + k] = (byte)Mathf.RoundToInt(pixels[r * cols + k].r * 255f);
            }
        }

        private static Texture2D uploadTexture;

        // Copies a rectangle's bytes (row by row, rect.Cols per row) into a working map on the GPU. They go through a small readable
        // texture. Two copies are made where the rectangle crosses the map's edge.
        private static void WriteRect(Layer layer, Texture2D work, EveCoverageMath.Rect rect, byte[] data)
        {
            int first = Math.Min(rect.Cols, layer.Width - rect.Col0);
            WriteSegment(work, rect.Col0, rect.Row0, first, rect.Rows, rect.Cols == first ? data : Segment(data, rect.Cols, rect.Rows, 0, first));
            if (rect.Cols > first) WriteSegment(work, 0, rect.Row0, rect.Cols - first, rect.Rows, Segment(data, rect.Cols, rect.Rows, first, rect.Cols - first));
        }

        private static byte[] Segment(byte[] data, int cols, int rows, int colStart, int colCount)
        {
            var segment = new byte[colCount * rows];
            EveStormMath.Columns(data, cols, rows, colStart, colCount, segment);
            return segment;
        }

        private static void WriteSegment(Texture2D work, int x, int y, int cols, int rows, byte[] data)
        {
            // The copy in main memory, which EVE's SampleCoverage reads, then the same rectangle on the GPU.
            NativeArray<byte> raw = work.GetRawTextureData<byte>();
            for (int r = 0; r < rows; r++) NativeArray<byte>.Copy(data, r * cols, raw, (y + r) * work.width + x, cols);
            if (uploadTexture == null) uploadTexture = new Texture2D(Math.Max(cols, 16), Math.Max(rows, 16), TextureFormat.R8, false, true) { name = "ReDefinition Seasons storm upload" };
            if (uploadTexture.width != cols || uploadTexture.height != rows) uploadTexture.Resize(cols, rows);
            uploadTexture.LoadRawTextureData(data);
            uploadTexture.Apply(false, false);
            Graphics.CopyTexture(uploadTexture, 0, 0, 0, 0, cols, rows, work, 0, 0, x, y);
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
            layer.CoverageOriginalBytes = null;
            layer.TypeOriginalBytes = null;
            layer.Main = null;
            layer.Probe = null;
            layer.PaintedCount = 0;
            layer.Signature = 0;
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
            layer.Ticks++;

            // The cells' masks, each in the rectangle of its own, and the number that says whether they are the same as at the last paint.
            int masks = 0;
            ulong signature = (ulong)(long)Math.Floor(ut / SignatureSeconds);
            for (int i = 0; i < count && masks < maskRects.Length; i++)
            {
                double strength = cells[i].Strength;
                double peak = Peak(strength);
                if (peak <= 0.0 || cells[i].DistanceM - cells[i].RadiusM * EveStormMath.MaxReachFactor > RangeM) continue;
                double radiusM = cells[i].RadiusM * 1.05 * EveStormMath.GrowthScale(strength);
                double cx, cy, cz;
                CellDirection(layer, body, rotation, cells[i], live, out cx, out cy, out cz);
                EveCoverageMath.Rect rect = EveStormMath.CellRect(cx, cy, cz, radiusM, body.Radius, layer.Width, layer.Height);
                if (EveStormMath.MaskSize(rect) == 0) continue;
                double u, v;
                EveCoverageMath.Uv(cx, cy, cz, out u, out v);
                signature = EveStormMath.Signature(signature, cells[i].Key, u, v, radiusM, strength, layer.Width, layer.Height);
                maskRects[masks] = rect;
                maskCells[masks] = i;
                maskCentres[masks] = new Vector3((float)cx, (float)cy, (float)cz);
                maskRadii[masks] = radiusM;
                masks++;
            }
            if (signature == 0) signature = 1;
            // Nothing changed since the last paint (the cells are where they were to a texel): the maps are as they should be.
            if (signature == layer.Signature && (masks > 0 || layer.PaintedCount == 0)) return;
            if (masks == 0 && layer.PaintedCount == 0) return;
            for (int m = 0; m < masks; m++)
            {
                StormCells.Cell cell = cells[maskCells[m]];
                Vector3 c = maskCentres[m];
                int lobeCount = EveStormMath.Lobes(cell.Key, maskRadii[m], cell.Heading, ut, lobes);
                var mask = new byte[EveStormMath.MaskSize(maskRects[m])];
                EveStormMath.PaintMask(mask, maskRects[m], layer.Width, layer.Height, c.x, c.y, c.z, body.Radius, lobes, lobeCount, Peak(cell.Strength), cell.Key);
                maskData[m] = mask;
                CheckGround(body, cell);
            }

            // Cells whose rectangles touch are one group: their masks are merged (the larger value wins) and composited once.
            int groups = 0;
            for (int i = 0; i < masks; i++)
            {
                EveCoverageMath.Rect rect = maskRects[i];
                byte[] mask = maskData[i];
                bool merged = true;
                while (merged)
                {
                    merged = false;
                    for (int g = 0; g < groups; g++)
                    {
                        EveCoverageMath.Rect union;
                        if (!EveStormMath.TryUnion(groupRects[g], rect, layer.Width, out union)) continue;
                        var combined = new byte[EveStormMath.MaskSize(union)];
                        EveStormMath.MergeMax(combined, union, groupData[g], groupRects[g], layer.Width);
                        EveStormMath.MergeMax(combined, union, mask, rect, layer.Width);
                        groups--;
                        groupRects[g] = groupRects[groups];
                        groupData[g] = groupData[groups];
                        groupData[groups] = null;
                        rect = union;
                        mask = combined;
                        merged = true;
                        break;
                    }
                }
                groupRects[groups] = rect;
                groupData[groups] = mask;
                groups++;
            }

            // The old rectangles go back to the layer's own values, then the groups are composited from the originals.
            long texels = 0;
            for (int i = 0; i < layer.PaintedCount; i++) RestoreRect(layer, layer.Painted[i]);
            layer.PaintedCount = 0;
            for (int g = 0; g < groups; g++)
            {
                EveCoverageMath.Rect rect = groupRects[g];
                int size = EveStormMath.MaskSize(rect);
                var coverage = new byte[size];
                var type = new byte[size];
                ReadRect(layer, true, rect, coverage);
                ReadRect(layer, false, rect, type);
                EveStormMath.CompositeRect(coverage, type, layer.TargetType, groupData[g], rect);
                WriteRect(layer, layer.CoverageWork, rect, coverage);
                WriteRect(layer, layer.TypeWork, rect, type);
                layer.Painted[layer.PaintedCount++] = rect;
                texels += size;
                groupData[g] = null;
            }
            for (int i = 0; i < masks; i++) maskData[i] = null;
            layer.Signature = signature;
            stopwatch.Stop();
            layer.PaintMsTotal += stopwatch.Elapsed.TotalMilliseconds;
            layer.Paints++;
            if (!layer.Checked && groups > 0 && live && layer.Ticks >= 3) Verify(body, layer, cells, count, ut, texels, stopwatch.Elapsed.TotalMilliseconds);
        }

        // Puts a rectangle of a layer's working maps back to the originals' values.
        private static void RestoreRect(Layer layer, EveCoverageMath.Rect rect)
        {
            int size = EveStormMath.MaskSize(rect);
            if (size == 0) return;
            var coverage = new byte[size];
            var type = new byte[size];
            ReadRect(layer, true, rect, coverage);
            ReadRect(layer, false, rect, type);
            WriteRect(layer, layer.CoverageWork, rect, coverage);
            WriteRect(layer, layer.TypeWork, rect, type);
        }

        // The seconds of universal time that count as one for the paint's signature: the lobes breathe over hours, the layers turn slowly.
        private const double SignatureSeconds = 60.0;

        private static readonly EveCoverageMath.Rect[] maskRects = new EveCoverageMath.Rect[StormCells.Capacity];
        private static readonly byte[][] maskData = new byte[StormCells.Capacity][];
        private static readonly int[] maskCells = new int[StormCells.Capacity];
        private static readonly Vector3[] maskCentres = new Vector3[StormCells.Capacity];
        private static readonly double[] maskRadii = new double[StormCells.Capacity];
        private static readonly EveCoverageMath.Rect[] groupRects = new EveCoverageMath.Rect[StormCells.Capacity];
        private static readonly byte[][] groupData = new byte[StormCells.Capacity][];

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
            // A layer that EVE has switched off (its schedule, timeSettings, or the camera outside its range) is not updated: its matrix
            // is stale and it reads 0 though the map holds the cell.
            object enabled = enabledProperty != null ? enabledProperty.GetValue(layer.Volume, null) : null;
            string state = enabled is bool ? ((bool)enabled ? "enabled" : "switched off by EVE (its schedule or range), its matrix is not updated") : "state unknown";
            SeasonsLog.Say("storm clouds in EVE's layers: '" + layer.Name + "' check: EVE's SampleCoverage at the centre of the strongest cell reads "
                           + read.ToString("0.###", CultureInfo.InvariantCulture) + " at " + altitude.ToString("0", CultureInfo.InvariantCulture) + " m (time fade "
                           + Fade(layer).ToString("0.00", CultureInfo.InvariantCulture) + ", volume " + state + "), painted " + texels.ToString(CultureInfo.InvariantCulture)
                           + " texels in " + ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
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
                    int pass = 0;
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
                        int n = ProbePattern ? EveStormMath.Pattern(cells[i].RadiusM, puffs)
                            : EveStormMath.Swirl(cells[i].Key, cells[i].RadiusM, strength, cells[i].Latitude >= 0, cells[i].Heading, puffs);
                        for (int p = 0; p < n; p++)
                        {
                            // A puff that erases needs the erasing pass of our shader; the fallback shaders only paint.
                            if (puffs[p].Erase && !swirlErases) continue;
                            int wanted = puffs[p].Erase ? 1 : 0;
                            if (wanted != pass)
                            {
                                swirlMaterial.SetPass(wanted);
                                pass = wanted;
                            }
                            DrawPuff(body, cx, cy, cz, puffs[p], cells[i].Key + (ulong)p);
                        }
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
                string bundleReason;
                Shader shader = StormShaders.LoadSwirlShader(out bundleReason);
                swirlErases = shader != null;
                if (shader == null)
                {
                    SeasonsLog.Say("storm clouds in EVE's layers: the swirl shader of the bundle is not available (" + bundleReason + "); the swirl only paints");
                    shader = StormShaders.Find(SwirlShaderNames);
                }
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
            double red, green, blue;
            EveStormMath.Colour(puff, out red, out green, out blue);
            GL.Begin(GL.TRIANGLES);
            for (int k = 0; k < indices; k++)
            {
                int i = fanIndices[k];
                GL.Color(Probe && !puff.Erase ? new Color(1f, 0.1f, 0.1f, (float)fanAlpha[i])
                    : new Color((float)red, (float)green, (float)blue, (float)(puff.Alpha * fanAlpha[i])));
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
            if (uploadTexture != null) UnityEngine.Object.Destroy(uploadTexture);
            uploadTexture = null;
            groundChecked.Clear();
            layersLogged = "";
            nextScan = 0f;
            loggedNone = false;
            frameLogged = false;
            scanned = false;
        }
    }
}
