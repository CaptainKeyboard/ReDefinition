using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // The snow cloud of the storm cells as a layer of EVE Redux's volumetric clouds, by reflection and guarded.
    //
    // A static config of ours (GameData/ReDefinition/Seasons/Clouds/SeasonsStormLayer.cfg) defines the layer LayerName on Kerbin. EVE
    // reads it. Its coverage map is a placeholder, and the cells around the camera are painted into a map of our own (EveCoverageMath).
    //
    // The path, each step a reflection that may fail with a line in the log.
    // CloudsManager.GetObjectList() lists the CloudsObject layers. Name and Body pick ours, and LayerRaymarchedVolume is its volume.
    // CoverageMap is a Utils.TextureWrapper. Its private textureValue is the texture EVE samples on the CPU and sets on the materials
    // whenever it applies the wrapper. We put our texture there and set textureInitialized, so that every later apply sets ours.
    // RaymarchedCloudMaterial and ReflectionProbeRaymarchedCloudMaterial get it as "CloudCoverage", as EVE's own painter does.
    // CloudRotationMatrix takes a world position into the layer's frame, and SampleCoverage reads the coverage there.
    // The private cloudsPQS of the layer has an `enabled` property that stops the layer's update and switches the volume off.
    internal static class EveCellLayer
    {
        // The name of the layer in the config.
        internal const string LayerName = "ReDefinition-Seasons-Storm";

        // The cells that are painted lie within this distance of the camera, in m.
        internal const double RangeM = 150000;

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

        // Called every frame while the storm effect runs: finds and binds the layer, paints the cells into its coverage map (at most
        // twice a second) and shows or hides the layer. cells are the cells within RangeM of the camera.
        internal static void Update(CelestialBody body, StormCells.Cell[] cells, int count)
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
                Paint(body, cells, count);
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
            object found = null, foundVolume = null;
            IEnumerable list = getList.Invoke(null, null) as IEnumerable;
            if (list != null)
            {
                foreach (object layer in list)
                {
                    if (layer == null) continue;
                    if (!string.Equals(nameProperty.GetValue(layer, null) as string, LayerName, StringComparison.Ordinal)) continue;
                    if (!string.Equals(bodyProperty.GetValue(layer, null) as string, body.name, StringComparison.Ordinal)) continue;
                    found = layer;
                    foundVolume = volumeProperty.GetValue(layer, null);
                    break;
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
            if (ReferenceEquals(foundVolume, boundVolume)) return;
            if (boundVolume != null) Unbind();
            Bind(body, found, foundVolume);
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
            string keyword = main.IsKeywordEnabled("ALPHAMAP_1") ? "ALPHAMAP_1 on" : "ALPHAMAP_1 OFF";
            float inner = (float)innerProperty.GetValue(volume, null), outer = (float)outerProperty.GetValue(volume, null);
            SeasonsLog.Say("storm layer: bound '" + LayerName + "' on " + body.name + ", coverage map " + EveCoverageMath.Width.ToString(CultureInfo.InvariantCulture)
                           + "x" + EveCoverageMath.Height.ToString(CultureInfo.InvariantCulture) + " R8 ("
                           + (EveCoverageMath.Width > 0 ? (2 * Math.PI * body.Radius / EveCoverageMath.Width / 1000).ToString("0.00", CultureInfo.InvariantCulture) : "?")
                           + " km a texel), " + keyword + ", reflection probe material " + (probe != null ? "set" : "missing")
                           + ", cloud from " + (inner - body.Radius).ToString("0", CultureInfo.InvariantCulture) + " to "
                           + (outer - body.Radius).ToString("0", CultureInfo.InvariantCulture) + " m above sea level");
        }

        private static void Unbind()
        {
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

        private static void Paint(CelestialBody body, StormCells.Cell[] cells, int count)
        {
            if (boundVolume == null || data == null || texture == null) return;
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

            // Show or hide the layer: nothing is raymarched while no cell is near.
            SetShown(n > 0);

            if (n == 0 && paintedCount == 0 && !dirty) return;
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
        }

        private static void Upload()
        {
            texture.LoadRawTextureData(data);
            texture.Apply(false, false);
        }

        // Switches the layer on or off through its CloudsPQS, once per change.
        private static void SetShown(bool shown)
        {
            if (boundPqs == null || pqsEnabled == null) return;
            bool current = (bool)pqsEnabled.GetValue(boundPqs, null);
            if (shown)
            {
                if (hiddenByUs && !current) pqsEnabled.SetValue(boundPqs, true, null);
                hiddenByUs = false;
            }
            else if (current)
            {
                pqsEnabled.SetValue(boundPqs, false, null);
                hiddenByUs = true;
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
                SetShown(false);
            }
            catch (Exception)
            {
                // The layer is gone with its scene.
            }
        }
    }
}
