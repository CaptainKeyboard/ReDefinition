using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // The snow cloud of the storm cells as layers of EVE Redux's volumetric clouds, by reflection and guarded.
    //
    // A static config of ours (GameData/ReDefinition/Seasons/Clouds/SeasonsStormLayer.cfg) defines two layers on Kerbin. EVE reads it.
    // Their coverage maps are placeholders. The cells around the camera are painted into a map of our own (EveCoverageMath).
    //
    // The path, each step a reflection that may fail with a line in the log.
    // CloudsManager.GetObjectList() lists the CloudsObject layers. Name and Body pick ours, and LayerRaymarchedVolume is its volume.
    // CoverageMap is a Utils.TextureWrapper. Its private textureValue is the texture EVE samples. We put our texture there and set
    // textureInitialized, so that every later apply sets ours. The two volume materials get it as "CloudCoverage".
    // CloudRotationMatrix takes a world position into the layer's frame, and SampleCoverage reads the coverage there.
    // The private cloudsPQS of the layer has an `enabled` property that switches the layer off.
    //
    // The orbit and map view show the 2D layer of the cloud's OBJECT: its _MainTex wrapper and its material get the picture of
    // EveSwirlMath (docs/development/flow-seasons.md has the path).
    internal static class EveCellLayer
    {
        // The name of the layer in the config.
        internal const string LayerName = "ReDefinition-Seasons-Storm";

        // The name of the second layer of the config, which carries EVE's snow particle field and sound. A volume gives no coverage below
        // its lowest altitude (SampleCoverage returns 0 there) and the cloud starts at 500 m. So the field has a layer that reaches the ground.
        internal const string SnowLayerName = "ReDefinition-Seasons-Snow";

        // The cells that are painted lie within this distance of the camera, in m.
        internal const double RangeM = 150000;

        // From this camera altitude, in m, and in the map view, the cells of the whole planet are painted into the 2D picture.
        // EVE's 2D layer starts to show at its scaledFadeStartAltitude, 200 km.
        internal const double WideFromAltitudeM = 150000;

        // The volume is current (EVE updates its matrix) below this camera altitude, in m: EVE's scaledFadeEndAltitude is 260 km.
        internal const double VolumeLiveBelowM = 250000;

        // How often the picture of the 2D layer is painted and uploaded, in seconds: a cell drifts a few metres a second, a texel is 1.8 km.
        private const float SwirlInterval = 2f;

        // The render queue of the 2D layer's material: EVE's own is the one before the transparent queue (3000).
        private const int SwirlQueue = 3000;

        // The base of the cloud in the config (cloudTypes minAltitude), in m above sea level. The curtain of the volume starts here.
        internal const double BaseAslM = 500;

        // The cloud's height as a share of the layer used for the calibration: the plateau of the config's coverage curve.
        private const double CalibrationHeightShare = 0.33;

        // The map is painted at most this often, in seconds, and the layer is looked for this often.
        private const float PaintInterval = 0.5f, ScanInterval = 3f;

        // How many ticks the calibration is tried before it is given up.
        private const int CalibrationTries = 8;

        internal enum Status
        {
            Unknown,
            // EVE is not installed.
            NoEve,
            // EVE is installed but has no raymarched (volumetric) layers.
            NoVolumetric,
            // The layer of our config is not in EVE's list (config missing or EVE has not applied it).
            NoLayer,
            // A symbol of the path is missing in this EVE.
            NotControllable,
            // The layer is bound to our map.
            Bound,
            // Bound, but the calibration found the map is not read where it is painted.
            Mismatch
        }

        private static bool looked;
        private static string summary = "not looked for";
        private static Status status = Status.Unknown;
        private static MethodInfo getList;
        private static PropertyInfo nameProperty, bodyProperty, volumeProperty, coverageMapProperty, materialProperty, probeMaterialProperty;
        private static PropertyInfo rotationProperty, innerProperty, outerProperty, pqsEnabled;
        private static FieldInfo pqsField, textureField, initializedField;
        private static MethodInfo sampleCoverage;

        // The 2D layer and the snow field (optional: the volume works without them).
        private static PropertyInfo layer2DProperty, cloudsMatProperty, renderingMaterialProperty;
        private static FieldInfo mainTexField, particleFieldField;
        private static string swirlSummary = "not looked for";
        private static Texture2D swirlTexture;
        private static byte[] swirlData;
        private static Material swirlMaterial;
        private static readonly EveCoverageMath.Rect[] swirlPainted = new EveCoverageMath.Rect[StormCells.Capacity];
        private static int swirlPaintedCount;
        private static float nextSwirl;
        private static bool swirlDirty, swirlFailed, ownField, frameChecked, loggedNoSnow, snowHiddenByUs, loggedQueue;

        // The snow field's layer (config OBJECT SnowLayerName): bound to the same texture as the cloud layer's coverage.
        private static object boundSnowVolume, boundSnowPqs;
        private static int swirlCells = -1;

        private static object boundVolume, boundLayer, boundPqs, boundWrapper;
        private static Material material, probeMaterial;
        private static Texture2D texture;
        private static byte[] data;
        private static readonly EveCoverageMath.Rect[] painted = new EveCoverageMath.Rect[StormCells.Capacity];
        private static int paintedCount;
        private static bool dirty;
        private static float nextScan, nextPaint;
        private static bool hiddenByUs;
        private static int calibrationTries;
        private static bool calibrated;
        private static double longitudeShift;
        private static int lastLoggedCells = -1;
        private static float lastCellLog;
        private static double paintMsTotal;
        private static int paints;
        private static bool loggedStatus, loggedMissing, released;

        // What the lookup found, for the log.
        internal static string Summary
        {
            get { return summary; }
        }

        internal static Status Current
        {
            get { return status; }
        }

        // Whether the layer carries the cloud of the cells now: bound, and the calibration did not find a mismatch. Our own volume then
        // draws the falling snow only.
        internal static bool Supplying
        {
            get { return SeasonsSettings.StormEveClouds && status == Status.Bound; }
        }

        // Whether EVE's volumetric layers were found at all (a 2D-only EVE or none has no cloud for the cell).
        internal static bool HasVolumetric
        {
            get
            {
                Look();
                return status != Status.NoEve && status != Status.NoVolumetric && status != Status.NotControllable;
            }
        }

        // Whether the 2D layer's picture is bound (the orbit and map view show the cells), and what the lookup of it found.
        internal static bool SwirlBound
        {
            get { return swirlTexture != null && !swirlFailed; }
        }

        internal static string SwirlSummary
        {
            get { return swirlSummary; }
        }

        // Whether the layer carries a snow particle field of EVE (the config's particleField), which falls where the coverage is.
        internal static bool OwnSnowField
        {
            get { return ownField && SeasonsSettings.StormEveClouds && status == Status.Bound; }
        }

        // The distance from the camera within which cells are collected: RangeM near the ground, the whole planet (the farthest a
        // great circle goes is half the circumference) from WideFromAltitudeM and in the map view.
        internal static double RangeFor(double cameraAltitudeM, double bodyRadiusM, bool map)
        {
            return map || cameraAltitudeM >= WideFromAltitudeM ? Math.PI * bodyRadiusM : RangeM;
        }

        // The base of the cloud above the ground at a place with that ground height, in m, for the curtain's start.
        internal static double BaseAboveGroundM(double groundAslM)
        {
            return Math.Max(120.0, BaseAslM - groundAslM);
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
                    summary = "this EVE has no volumetric (raymarched) cloud layers; only 2D layers can exist";
                    return;
                }
                var link = new ReflectLink();
                Type layer = link.Type("Atmosphere.CloudsObject");
                Type pqs = link.Type("Atmosphere.CloudsPQS");
                Type wrapper = link.Type("Utils.TextureWrapper");
                getList = link.Method(manager, "CloudsManager", "GetObjectList", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy, null);
                nameProperty = link.Property(layer, "CloudsObject", "Name", BindingFlags.Public | BindingFlags.Instance);
                bodyProperty = link.Property(layer, "CloudsObject", "Body", BindingFlags.Public | BindingFlags.Instance);
                volumeProperty = link.Property(layer, "CloudsObject", "LayerRaymarchedVolume", BindingFlags.Public | BindingFlags.Instance);
                pqsField = link.Field(layer, "CloudsObject", "cloudsPQS", TypeLookup.Any);
                pqsEnabled = pqs != null ? pqs.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) : null;
                if (pqsEnabled == null) link.Missing.Add("CloudsPQS.enabled");
                coverageMapProperty = link.Property(volume, "CloudsRaymarchedVolume", "CoverageMap", BindingFlags.Public | BindingFlags.Instance);
                materialProperty = link.Property(volume, "CloudsRaymarchedVolume", "RaymarchedCloudMaterial", BindingFlags.Public | BindingFlags.Instance);
                probeMaterialProperty = link.Property(volume, "CloudsRaymarchedVolume", "ReflectionProbeRaymarchedCloudMaterial", BindingFlags.Public | BindingFlags.Instance);
                rotationProperty = link.Property(volume, "CloudsRaymarchedVolume", "CloudRotationMatrix", BindingFlags.Public | BindingFlags.Instance);
                innerProperty = link.Property(volume, "CloudsRaymarchedVolume", "InnerSphereRadius", BindingFlags.Public | BindingFlags.Instance);
                outerProperty = link.Property(volume, "CloudsRaymarchedVolume", "OuterSphereRadius", BindingFlags.Public | BindingFlags.Instance);
                sampleCoverage = link.Method(volume, "CloudsRaymarchedVolume", "SampleCoverage", BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(bool) });
                textureField = link.Field(wrapper, "TextureWrapper", "textureValue", TypeLookup.Any, typeof(Texture2D));
                initializedField = link.Field(wrapper, "TextureWrapper", "textureInitialized", TypeLookup.Any, typeof(bool));
                LookSwirl(layer, volume);
                if (link.Complete)
                {
                    status = Status.NoLayer;
                    summary = "found (CloudsManager -> CloudsObject '" + LayerName + "' -> CloudsRaymarchedVolume.CoverageMap -> TextureWrapper.textureValue)";
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

        // The members of the 2D layer and the snow field, which the volume does not need. A missing one leaves the cells without a
        // picture in the orbit and map view, with the names in the summary.
        private static void LookSwirl(Type layer, Type volume)
        {
            var optional = new ReflectLink();
            Type clouds2D = optional.Type("Atmosphere.Clouds2D");
            Type cloudsMaterial = optional.Type("Atmosphere.CloudsMaterial");
            layer2DProperty = optional.Property(layer, "CloudsObject", "Layer2D", BindingFlags.Public | BindingFlags.Instance);
            cloudsMatProperty = optional.Property(clouds2D, "Clouds2D", "CloudsMat", BindingFlags.Public | BindingFlags.Instance);
            renderingMaterialProperty = optional.Property(clouds2D, "Clouds2D", "CloudRenderingMaterial", BindingFlags.Public | BindingFlags.Instance);
            mainTexField = optional.Field(cloudsMaterial, "CloudsMaterial", "_MainTex", TypeLookup.Any);
            particleFieldField = volume.GetField("particleField", TypeLookup.Any);
            swirlSummary = optional.Complete
                ? "found (CloudsObject.Layer2D -> Clouds2D.CloudsMat -> CloudsMaterial._MainTex, Clouds2D.CloudRenderingMaterial)"
                : "not available (missing " + optional.MissingText() + "); the cells have no picture in the orbit and map view";
            if (!optional.Complete) layer2DProperty = null;
        }

        // Called every frame while the storm effect runs: finds and binds the layer, paints the cells into its coverage map (at most
        // twice a second) and shows or hides the layer. cells are the cells within RangeFor of the camera.
        internal static void Update(CelestialBody body, StormCells.Cell[] cells, int count, double cameraAltitudeM)
        {
            if (body == null)
            {
                Release();
                return;
            }
            if (!SeasonsSettings.StormEveClouds)
            {
                // The layer of the cfg exists whatever the setting says. It is bound and hidden, so that it does not raymarch an empty map.
                Look();
                if (status == Status.NoEve || status == Status.NoVolumetric || status == Status.NotControllable) return;
                float asked = Time.realtimeSinceStartup;
                try
                {
                    if (asked >= nextScan)
                    {
                        nextScan = asked + ScanInterval;
                        Scan(body);
                        released = false;
                    }
                    Release();
                }
                catch (Exception)
                {
                    // The layer is gone with its scene; the next scan binds it again.
                }
                return;
            }
            released = false;
            Look();
            if (status == Status.NoEve || status == Status.NoVolumetric || status == Status.NotControllable)
            {
                if (!loggedStatus)
                {
                    loggedStatus = true;
                    SeasonsLog.Say("storm layer: " + summary + "; the cells draw their own cloud");
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
                if (status != Status.Bound && status != Status.Mismatch) return;
                if (now < nextPaint) return;
                nextPaint = now + PaintInterval;
                Paint(body, cells, count, cameraAltitudeM);
            }
            catch (Exception e)
            {
                SeasonsLog.Failed("seasons-eve-layer", "the storm cloud layer of EVE failed and is released", e);
                Release();
                status = Status.NotControllable;
                summary = "not controllable (" + CompatibilityLog.Reason(e) + ")";
            }
        }

        // Looks for our layer on the body; binds it when it is a new one (or none before), and unbinds when it has gone.
        private static void Scan(CelestialBody body)
        {
            object found = null, foundVolume = null, fxFound = null, fxFoundVolume = null;
            IEnumerable list = getList.Invoke(null, null) as IEnumerable;
            if (list != null)
            {
                foreach (object layer in list)
                {
                    if (layer == null) continue;
                    string name = nameProperty.GetValue(layer, null) as string;
                    bool isCloud = string.Equals(name, LayerName, StringComparison.Ordinal);
                    bool isSnow = string.Equals(name, SnowLayerName, StringComparison.Ordinal);
                    if (!isCloud && !isSnow) continue;
                    if (!string.Equals(bodyProperty.GetValue(layer, null) as string, body.name, StringComparison.Ordinal)) continue;
                    if (isSnow)
                    {
                        fxFound = layer;
                        fxFoundVolume = volumeProperty.GetValue(layer, null);
                    }
                    else
                    {
                        found = layer;
                        foundVolume = volumeProperty.GetValue(layer, null);
                    }
                }
            }
            if (found == null || foundVolume == null)
            {
                if (boundVolume != null) Unbind();
                if (!loggedMissing)
                {
                    loggedMissing = true;
                    SeasonsLog.Say("storm layer: '" + LayerName + "' is not among EVE's layers on " + body.name
                                   + (found != null ? " (it has no volumetric layer)" : " (config missing or not applied yet)") + "; the cells draw their own cloud");
                }
                return;
            }
            if (!ReferenceEquals(foundVolume, boundVolume))
            {
                if (boundVolume != null) Unbind();
                Bind(body, found, foundVolume);
            }
            SyncSnow(fxFound, fxFoundVolume);
        }

        // Binds the snow field's layer to the same map when it is a new one, and unbinds it when it has gone. Needs the map of the cloud
        // layer, so it follows its bind.
        private static void SyncSnow(object layer, object volume)
        {
            if (texture == null || boundVolume == null) return;
            if (layer == null || volume == null)
            {
                if (boundSnowVolume != null) UnbindSnow();
                if (!loggedNoSnow)
                {
                    loggedNoSnow = true;
                    SeasonsLog.Say("storm layer: '" + SnowLayerName + "' is not among EVE's layers (config not applied); the storm uses the stock snow fields or its own flakes");
                }
                return;
            }
            if (ReferenceEquals(volume, boundSnowVolume)) return;
            if (boundSnowVolume != null) UnbindSnow();
            object wrapper = coverageMapProperty.GetValue(volume, null);
            if (wrapper == null)
            {
                SeasonsLog.Say("storm layer: '" + SnowLayerName + "' has no coverage map; the storm uses the stock snow fields or its own flakes");
                return;
            }
            textureField.SetValue(wrapper, texture);
            initializedField.SetValue(wrapper, true);
            Material snowMain = materialProperty.GetValue(volume, null) as Material;
            Material snowProbe = probeMaterialProperty.GetValue(volume, null) as Material;
            if (snowMain != null) snowMain.SetTexture("CloudCoverage", texture);
            if (snowProbe != null) snowProbe.SetTexture("CloudCoverage", texture);
            boundSnowVolume = volume;
            boundSnowPqs = pqsField.GetValue(layer);
            snowHiddenByUs = false;
            ownField = particleFieldField != null && particleFieldField.GetValue(volume) != null;
            SeasonsLog.Say("storm layer: bound '" + SnowLayerName + "' to the coverage map, snow field " + (ownField ? "present" : "MISSING (the config's particleField was not applied)"));
        }

        private static void UnbindSnow()
        {
            boundSnowVolume = null;
            boundSnowPqs = null;
            snowHiddenByUs = false;
            ownField = false;
        }

        private static void Bind(CelestialBody body, object layer, object volume)
        {
            object wrapper = coverageMapProperty.GetValue(volume, null);
            Material main = materialProperty.GetValue(volume, null) as Material;
            Material probe = probeMaterialProperty.GetValue(volume, null) as Material;
            if (wrapper == null || main == null)
            {
                status = Status.NoLayer;
                SeasonsLog.Say("storm layer: the layer of '" + LayerName + "' has no coverage map or no material; the cells draw their own cloud");
                return;
            }
            if (data == null) data = new byte[EveCoverageMath.Width * EveCoverageMath.Height];
            else Array.Clear(data, 0, data.Length);
            paintedCount = 0;
            texture = new Texture2D(EveCoverageMath.Width, EveCoverageMath.Height, TextureFormat.R8, false, true)
            {
                name = "ReDefinition Seasons storm coverage",
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            Upload();
            textureField.SetValue(wrapper, texture);
            initializedField.SetValue(wrapper, true);
            main.SetTexture("CloudCoverage", texture);
            if (probe != null) probe.SetTexture("CloudCoverage", texture);
            boundLayer = layer;
            boundVolume = volume;
            boundWrapper = wrapper;
            boundPqs = pqsField.GetValue(layer);
            material = main;
            probeMaterial = probe;
            calibrated = false;
            calibrationTries = 0;
            longitudeShift = 0;
            hiddenByUs = false;
            status = Status.Bound;
            BindSwirl(body, layer);
            string keyword = main.IsKeywordEnabled("ALPHAMAP_1") ? "ALPHAMAP_1 on" : "ALPHAMAP_1 OFF";
            float inner = (float)innerProperty.GetValue(volume, null), outer = (float)outerProperty.GetValue(volume, null);
            SeasonsLog.Say("storm layer: bound '" + LayerName + "' on " + body.name + ", coverage map " + EveCoverageMath.Width.ToString(CultureInfo.InvariantCulture)
                           + "x" + EveCoverageMath.Height.ToString(CultureInfo.InvariantCulture) + " R8 ("
                           + (EveCoverageMath.Width > 0 ? (2 * Math.PI * body.Radius / EveCoverageMath.Width / 1000).ToString("0.00", CultureInfo.InvariantCulture) : "?")
                           + " km a texel), " + keyword + ", reflection probe material " + (probe != null ? "set" : "missing")
                           + ", cloud from " + (inner - body.Radius).ToString("0", CultureInfo.InvariantCulture) + " to "
                           + (outer - body.Radius).ToString("0", CultureInfo.InvariantCulture) + " m above sea level, 2D picture "
                           + (swirlTexture != null ? "bound" : "not bound (" + swirlSummary + ")"));
        }

        // Puts the cell picture into the 2D layer of the same OBJECT: its _MainTex wrapper (so that EVE's reassign keeps it) and the
        // material it draws with. A layer without a 2D node, or a member that is missing, leaves it unbound.
        private static void BindSwirl(CelestialBody body, object layer)
        {
            swirlFailed = false;
            frameChecked = false;
            swirlPaintedCount = 0;
            swirlCells = -1;
            swirlDirty = false;
            if (layer2DProperty == null) return;
            try
            {
                object layer2D = layer2DProperty.GetValue(layer, null);
                if (layer2D == null)
                {
                    swirlSummary = "the layer has no layer2D node in its OBJECT; the cells have no picture in the orbit and map view";
                    return;
                }
                object cloudsMat = cloudsMatProperty.GetValue(layer2D, null);
                Material rendering = renderingMaterialProperty.GetValue(layer2D, null) as Material;
                object wrapper = cloudsMat != null ? mainTexField.GetValue(cloudsMat) : null;
                if (wrapper == null || rendering == null)
                {
                    swirlSummary = "the 2D layer has no _MainTex wrapper or no material (the config has no _MainTex value); the cells have no picture in the orbit and map view";
                    return;
                }
                if (swirlData == null) swirlData = new byte[EveSwirlMath.Width * EveSwirlMath.Height * 4];
                EveSwirlMath.Fill(swirlData);
                swirlTexture = new Texture2D(EveSwirlMath.Width, EveSwirlMath.Height, TextureFormat.RGBA32, false, false)
                {
                    name = "ReDefinition Seasons storm 2D",
                    wrapModeU = TextureWrapMode.Repeat,
                    wrapModeV = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                swirlTexture.LoadRawTextureData(swirlData);
                swirlTexture.Apply(false, false);
                textureField.SetValue(wrapper, swirlTexture);
                initializedField.SetValue(wrapper, true);
                rendering.SetTexture("_MainTex", swirlTexture);
                swirlMaterial = rendering;
                swirlSummary = "bound (" + EveSwirlMath.Width.ToString(CultureInfo.InvariantCulture) + "x" + EveSwirlMath.Height.ToString(CultureInfo.InvariantCulture)
                               + " RGBA, " + (EveSwirlMath.TexelMetres(body.Radius) / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " km a texel)";
            }
            catch (Exception e)
            {
                swirlFailed = true;
                swirlSummary = "failed (" + CompatibilityLog.Reason(e) + "); the cells have no picture in the orbit and map view";
                SeasonsLog.Failed("seasons-eve-swirl", "the 2D cloud picture of EVE failed", e);
            }
        }

        private static void Unbind()
        {
            if (swirlTexture != null) UnityEngine.Object.Destroy(swirlTexture);
            swirlTexture = null;
            swirlMaterial = null;
            swirlPaintedCount = 0;
            UnbindSnow();
            boundVolume = null;
            boundLayer = null;
            boundWrapper = null;
            boundPqs = null;
            material = null;
            probeMaterial = null;
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null;
            paintedCount = 0;
            hiddenByUs = false;
            calibrated = false;
            status = Status.NoLayer;
        }

        // One paint of both pictures. The coverage map is painted only while the volume is current: below its fade and not in the map
        // view, the only times EVE updates its matrix. The 2D picture is painted from the cells of the whole planet.
        private static void Paint(CelestialBody body, StormCells.Cell[] cells, int count, double cameraAltitudeM)
        {
            if (boundVolume == null || data == null || texture == null) return;
            bool map = MapView.MapIsEnabled;
            bool live = cameraAltitudeM < VolumeLiveBelowM && !map;
            bool wide = map || cameraAltitudeM >= WideFromAltitudeM;
            int near = live ? PaintCoverage(body, cells, count) : 0;
            int seen = PaintSwirl(body, cells, count);
            // Show or hide the layer: nothing is raymarched while no cell is near, and the 2D layer shows where the camera is high.
            SetShown(near > 0 || (wide && seen > 0), near > 0);
        }

        private static readonly EveCoverageMath.Blob[] swirlBlobs = new EveCoverageMath.Blob[StormCells.Capacity];

        // The 2D picture. The cells are taken with the direction of the body-fixed frame (GetRelSurfaceNVector, the one EVE's World2Planet
        // matrices turn the world into), the radius and the peak. It is repainted every SwirlInterval or when the number of cells
        // changes. Returns the cells that are visible at all.
        private static int PaintSwirl(CelestialBody body, StormCells.Cell[] cells, int count)
        {
            if (swirlTexture == null || swirlFailed) return 0;
            KeepQueue();
            int n = 0;
            for (int i = 0; i < count && n < swirlBlobs.Length; i++)
            {
                double peak = Mathx.SmoothStep(0.02, 0.45, cells[i].Strength);
                if (peak <= 0.0) continue;
                Vector3d direction = body.GetRelSurfaceNVector(cells[i].Latitude, cells[i].Longitude);
                swirlBlobs[n++] = new EveCoverageMath.Blob
                {
                    X = direction.x, Y = direction.y, Z = direction.z,
                    RadiusM = cells[i].RadiusM * 1.05,
                    Peak = peak,
                    Seed = cells[i].Key
                };
            }
            float now = Time.realtimeSinceStartup;
            if (now < nextSwirl && n == swirlCells && !swirlDirty) return n;
            if (n == 0 && swirlPaintedCount == 0 && !swirlDirty) { swirlCells = 0; return 0; }
            nextSwirl = now + SwirlInterval;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < swirlPaintedCount; i++) EveSwirlMath.Clear(swirlData, swirlPainted[i]);
                swirlPaintedCount = 0;
                for (int i = 0; i < n; i++) swirlPainted[swirlPaintedCount++] = EveSwirlMath.Paint(swirlData, swirlBlobs[i], body.Radius, longitudeShift);
                swirlTexture.LoadRawTextureData(swirlData);
                swirlTexture.Apply(false, false);
            }
            catch (Exception e)
            {
                swirlFailed = true;
                swirlSummary = "failed (" + CompatibilityLog.Reason(e) + ")";
                SeasonsLog.Failed("seasons-eve-swirl", "the 2D cloud picture of EVE failed and is left alone", e);
                return 0;
            }
            swirlDirty = false;
            stopwatch.Stop();
            if (n != swirlCells)
            {
                swirlCells = n;
                SeasonsLog.Say("storm layer: 2D picture painted for " + n.ToString(CultureInfo.InvariantCulture) + " cells, "
                               + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            }
            return n;
        }

        // EVE draws its 2D layers in the queue before the transparent one, in an order nobody sets, and sets the queue again whenever
        // the layer goes between scaled and local space. The storm's swirl is drawn after them, over the other clouds.
        private static void KeepQueue()
        {
            if (swirlMaterial == null || swirlMaterial.renderQueue == SwirlQueue) return;
            int before = swirlMaterial.renderQueue;
            swirlMaterial.renderQueue = SwirlQueue;
            if (!loggedQueue)
            {
                loggedQueue = true;
                SeasonsLog.Say("storm layer: the 2D layer's render queue was " + before.ToString(CultureInfo.InvariantCulture) + ", set to " + SwirlQueue.ToString(CultureInfo.InvariantCulture));
            }
        }

        // The coverage map: returns the number of cells painted into it.
        private static int PaintCoverage(CelestialBody body, StormCells.Cell[] cells, int count)
        {
            Matrix4x4 rotation = (Matrix4x4)rotationProperty.GetValue(boundVolume, null);
            var blobs = new EveCoverageMath.Blob[Math.Min(count, painted.Length)];
            int n = 0;
            for (int i = 0; i < count && n < blobs.Length; i++)
            {
                double peak = Mathx.SmoothStep(0.02, 0.45, cells[i].Strength);
                if (peak <= 0.0 || cells[i].DistanceM - cells[i].RadiusM > RangeM) continue;
                Vector3 world = (Vector3)body.GetWorldSurfacePosition(cells[i].Latitude, cells[i].Longitude, 0.0);
                Vector3 direction = rotation.MultiplyPoint(world).normalized;
                blobs[n++] = new EveCoverageMath.Blob
                {
                    X = direction.x, Y = direction.y, Z = direction.z,
                    RadiusM = cells[i].RadiusM * 1.05,
                    Peak = peak,
                    Seed = cells[i].Key
                };
            }

            if (n == 0 && paintedCount == 0 && !dirty) return 0;
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < paintedCount; i++) EveCoverageMath.Clear(data, painted[i]);
            paintedCount = 0;
            for (int i = 0; i < n; i++)
            {
                painted[paintedCount++] = EveCoverageMath.Paint(data, blobs[i], body.Radius, longitudeShift);
            }
            Upload();
            dirty = false;
            stopwatch.Stop();
            paintMsTotal += stopwatch.Elapsed.TotalMilliseconds;
            paints++;
            float now = Time.realtimeSinceStartup;
            if (n != lastLoggedCells && now - lastCellLog > 10f)
            {
                lastLoggedCells = n;
                lastCellLog = now;
                long texels = 0;
                for (int i = 0; i < paintedCount; i++) texels += (long)painted[i].Cols * painted[i].Rows;
                SeasonsLog.Say("storm layer: coverage written for " + n.ToString(CultureInfo.InvariantCulture) + " cells, " + texels.ToString(CultureInfo.InvariantCulture)
                               + " texels, " + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms (average "
                               + (paintMsTotal / paints).ToString("0.0", CultureInfo.InvariantCulture) + " ms over " + paints.ToString(CultureInfo.InvariantCulture) + " paints)");
            }
            if (n > 0 && !calibrated && calibrationTries < CalibrationTries) Calibrate(body, blobs, cells, count);
            if (n > 0 && !frameChecked) CheckFrame(body, cells, count, rotation);
            return n;
        }

        // Once per bind, while the volume is current: the angle between the volume's direction of a cell (the world position turned by the
        // volume's matrix, which the coverage uses) and the body-fixed direction the 2D picture uses. They are the same frame when it is 0.
        private static void CheckFrame(CelestialBody body, StormCells.Cell[] cells, int count, Matrix4x4 rotation)
        {
            for (int i = 0; i < count; i++)
            {
                if (Mathx.SmoothStep(0.02, 0.45, cells[i].Strength) <= 0.0 || cells[i].DistanceM - cells[i].RadiusM > RangeM) continue;
                Vector3 world = (Vector3)body.GetWorldSurfacePosition(cells[i].Latitude, cells[i].Longitude, 0.0);
                Vector3 viaVolume = rotation.MultiplyPoint(world).normalized;
                Vector3 bodyFixed = (Vector3)body.GetRelSurfaceNVector(cells[i].Latitude, cells[i].Longitude);
                if (viaVolume.sqrMagnitude < 0.5f) return;
                frameChecked = true;
                SeasonsLog.Say("storm layer: frame check of the 2D picture: the body-fixed direction of a cell is " + Vector3.Angle(viaVolume, bodyFixed).ToString("0.00", CultureInfo.InvariantCulture)
                               + " degrees from the volume's direction");
                return;
            }
        }

        private static void Upload()
        {
            texture.LoadRawTextureData(data);
            texture.Apply(false, false);
        }

        // Switches the cloud layer and the snow layer on or off through their CloudsPQS, once per change.
        private static void SetShown(bool shown, bool snowShown)
        {
            Switch(boundPqs, ref hiddenByUs, shown);
            Switch(boundSnowPqs, ref snowHiddenByUs, snowShown);
        }

        private static void Switch(object pqs, ref bool hidden, bool shown)
        {
            if (pqs == null || pqsEnabled == null) return;
            bool current = (bool)pqsEnabled.GetValue(pqs, null);
            if (shown)
            {
                if (hidden && !current) pqsEnabled.SetValue(pqs, true, null);
                hidden = false;
            }
            else if (current)
            {
                pqsEnabled.SetValue(pqs, false, null);
                hidden = true;
            }
        }

        // Checks that EVE reads the coverage where it was painted: its own SampleCoverage at the centre of the strongest cell against
        // the map at that cell's texture coordinates, and at the opposite side of the planet. If the centre reads nothing where the map
        // holds cloud, a search over longitudes and latitudes finds where it reads the peak, and the map is shifted along u by the
        // difference from then on. The layer's matrix is current only after a frame with the layer on, so the first tries may find the
        // layer off; they are repeated on the next paints.
        private static void Calibrate(CelestialBody body, EveCoverageMath.Blob[] blobs, StormCells.Cell[] cells, int count)
        {
            calibrationTries++;
            int best = -1;
            double bestPeak = 0;
            for (int i = 0; i < blobs.Length; i++) if (blobs[i].Peak > bestPeak) { bestPeak = blobs[i].Peak; best = i; }
            if (best < 0 || bestPeak < 0.3) return;
            // The cell of the strongest blob: the cells were turned into blobs in order, skipping the ones without a peak.
            int index = -1, seen = 0;
            for (int i = 0; i < count; i++)
            {
                if (Mathx.SmoothStep(0.02, 0.45, cells[i].Strength) <= 0.0 || cells[i].DistanceM - cells[i].RadiusM > RangeM) continue;
                if (seen == best) { index = i; break; }
                seen++;
            }
            if (index < 0) return;

            float inner = (float)innerProperty.GetValue(boundVolume, null), outer = (float)outerProperty.GetValue(boundVolume, null);
            double altitude = inner - body.Radius + CalibrationHeightShare * (outer - inner);
            double latitude = cells[index].Latitude, longitude = cells[index].Longitude;
            double centre = ReadCoverage(body, latitude, longitude, altitude);
            double u, v;
            EveCoverageMath.Uv(blobs[best].X, blobs[best].Y, blobs[best].Z, out u, out v);
            double mapValue = EveCoverageMath.Sample(data, u + longitudeShift, v);
            // The antipode: the other side of the planet at the same height.
            double opposite = ReadCoverage(body, -latitude, longitude + 180.0, altitude);
            double dLat = 0, dLon = 0;
            bool ok = centre > 0.5 * mapValue && mapValue > 0.1;
            if (!ok && mapValue > 0.1 && centre < 0.02)
            {
                // Search: where does EVE read the most within 90 degrees of longitude and 30 degrees of latitude?
                double bestRead = centre, bestLat = latitude, bestLon = longitude;
                for (double dla = -30; dla <= 30; dla += 5)
                {
                    for (double dlo = -90; dlo <= 90; dlo += 3)
                    {
                        double la = latitude + dla;
                        if (la > 89 || la < -89) continue;
                        double read = ReadCoverage(body, la, longitude + dlo, altitude);
                        if (read > bestRead) { bestRead = read; bestLat = la; bestLon = longitude + dlo; }
                    }
                }
                dLat = bestLat - latitude;
                dLon = bestLon - longitude;
                if (bestRead > 0.5 * mapValue && Math.Abs(dLat) < 1e-9)
                {
                    // Only a shift along the longitude can be corrected: u moves by the opposite of what the reading shows.
                    longitudeShift += -dLon / 360.0;
                    calibrationTries = CalibrationTries;
                    status = Status.Bound;
                    dirty = true;
                    nextPaint = 0f;
                    SeasonsLog.Say("storm layer calibration: centre coverage " + Format(centre) + ", opposite side " + Format(opposite) + ", offset (dlat "
                                   + Format(dLat) + ", dlon " + Format(dLon) + "), the map is shifted by " + Format(longitudeShift * 360.0) + " degrees of longitude");
                    return;
                }
            }
            if (ok)
            {
                calibrated = true;
                SeasonsLog.Say("storm layer calibration: centre coverage " + Format(centre) + " (painted " + Format(mapValue) + "), opposite side " + Format(opposite)
                               + ", offset (dlat 0, dlon 0)");
            }
            else if (calibrationTries >= CalibrationTries)
            {
                status = Status.Mismatch;
                SeasonsLog.Say("storm layer calibration FAILED: centre coverage " + Format(centre) + " (painted " + Format(mapValue) + "), opposite side " + Format(opposite)
                               + ", best reading offset (dlat " + Format(dLat) + ", dlon " + Format(dLon) + "); the cells draw their own cloud");
            }
        }

        private static double ReadCoverage(CelestialBody body, double latitude, double longitude, double altitude)
        {
            Vector3 world = (Vector3)body.GetWorldSurfacePosition(latitude, longitude, altitude);
            var args = new object[] { world, 0f, false };
            object result = sampleCoverage.Invoke(boundVolume, args);
            return result is float ? (double)(float)result : 0.0;
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // Empties the map and hides the layer (the setting is off, or the effect is put away). The map stays in the layer, which
        // EVE keeps showing the empty map from; it is destroyed when the layer goes (Unbind).
        internal static void Release()
        {
            if (boundVolume == null || released) return;
            released = true;
            try
            {
                if (data != null && texture != null)
                {
                    Array.Clear(data, 0, data.Length);
                    paintedCount = 0;
                    Upload();
                }
                if (swirlData != null && swirlTexture != null && !swirlFailed)
                {
                    EveSwirlMath.Fill(swirlData);
                    swirlPaintedCount = 0;
                    swirlCells = -1;
                    swirlTexture.LoadRawTextureData(swirlData);
                    swirlTexture.Apply(false, false);
                }
                SetShown(false, false);
            }
            catch (Exception)
            {
                // The layer is gone with its scene.
            }
        }
    }
}
