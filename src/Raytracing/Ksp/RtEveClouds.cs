using System;
using System.Collections;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // EVE's cloud layer over the main body, for the rays (RtCommon.hlsli: CloudTransmittance,
    // CloudOverSky). The moons' light, the sun's and the moons' discs on the water and the
    // sun's light at the rays' hits are darkened under it as EVE darkens the sun's light on
    // the frame. Rays that escape the scene see it. Read from EVE (Blackrack's, with the
    // raymarched volumetric layer; its assembly decompiled, 1.12 builds of 2026-10):
    // GenericEVEManager<CloudsObject>.ObjectList (static, not public) holds the loaded
    // bodies' layers. A CloudsObject has Body, Layer2D (Clouds2D) and LayerRaymarchedVolume
    // (CloudsRaymarchedVolume). The volume's material carries the maps (CloudCoverage,
    // CloudType, DensityCurve, their alpha masks) and radii, its CloudRotationMatrix the
    // world-to-map transform EVE sets each frame, its CurrentTimeFade* the fades. The 2D
    // layer's ScreenSpaceShadowMaterial carries the shadow's sphere (_Radius), strength
    // (_ShadowFactor, _Color's alpha), colour and fades (cloudTimeFadeDensity, mapViewFade).
    // The maps go into one texture of ours (ReDefinitionRtClouds.shader), packed again every
    // few seconds and when they change. Nothing without EVE or its volumetric layer; another
    // build of it turns this off with one warning.
    internal sealed class RtEveClouds
    {
        // 4096 by 2048, two bytes a texel (16 MB): at 2048 by 1024 the layer's fine structure averaged out and the
        // shadow came out 0.62 where EVE's own is 0.76 (measured at sea, 2026-10-13).
        private const int Width = 4096, Height = 2048;
        private const float RepackSeconds = 5f;
        private static readonly int TimeFadeDensityId = Shader.PropertyToID("cloudTimeFadeDensity");
        private static readonly int MapViewFadeId = Shader.PropertyToID("mapViewFade");
        private static readonly int RadiusId = Shader.PropertyToID("_Radius");
        private static readonly int ShadowColourId = Shader.PropertyToID("_Color");
        private static readonly int ShadowFactorId = Shader.PropertyToID("_ShadowFactor");

        // Whether reading EVE threw (the clouds are then left out) and whether its members were looked up; EVE's cloud
        // objects list and the properties read from its cloud objects, volumes and 2D layers.
        private bool failed, resolved;
        private FieldInfo objectList;
        private PropertyInfo bodyName, layer2D, volume, volumeEnabled, rotation, fadeCoverage, fadeDensity, scaledFade, volumeMaterial,
                             inner, outer, shadowMaterial, scaled2D, coverageMap, typeMap;
        // The volume's density curve (private), and a TextureWrapper's texture, type and alpha
        // mask: the maps are not properties of EVE's raymarching shader, so its material does
        // not give them back.
        private FieldInfo curves, wrapperType, wrapperMask;
        private MethodInfo wrapperTexture;
        // The value-typed members read every frame, through compiled getters: a
        // PropertyInfo's GetValue boxes each (CompiledGetter).
        private Func<object, bool> readEnabled, readScaled2D;
        private Func<object, Matrix4x4> readRotation;
        // The reference-typed members read every frame, through compiled getters: a reflection call each otherwise.
        private Func<object, object> readObjects, readBodyName, readLayer2D, readVolume, readVolumeMaterial, readShadowMaterial,
                                     readCoverageMap, readTypeMap;
        private Func<object, float> readInner, readOuter, readFadeCoverage, readFadeDensity, readScaledFade;
        // The body whose name was read last, and its name: Object.name is a new string
        // each time it is read.
        private CelestialBody namedBody;
        private string bodyNameOf;
        private RenderTexture packed;
        private IntPtr packedHandle;
        private Material pack;
        private Texture packedCoverage;
        private float packedRadius, packedAt = -100f;
        private string loggedBody;

        // The packed map's native texture, for the packet; zero for none.
        internal IntPtr Texture { get { return packedHandle; } }

        // The packet's cloud constants (Raytracing.h, RtFramePacket.clouds) for the body's
        // layer, relative to `camera`; false and all 0 where there is none.
        internal bool Fill(float[] clouds, CelestialBody body, Vector3 camera)
        {
            for (int i = 0; i < clouds.Length; i++) clouds[i] = 0f;
            if (failed || body == null) return false;
            try
            {
                return FillFrom(clouds, body, camera, true);
            }
            catch (Exception e)
            {
                failed = true;
                CompatibilityLog.Warn("rt-eve-clouds", "Raytracing: EVE's clouds are left out of the rays (" + CompatibilityLog.Reason(e) + ").");
                for (int i = 0; i < clouds.Length; i++) clouds[i] = 0f;
                return false;
            }
        }

        // The name of an EVE cloud object, or null where it cannot be read (then every object counts as weather).
        private PropertyInfo objectName;
        private bool objectNameLooked;

        private string NameOfObject(object cloudsObject)
        {
            try
            {
                if (!objectNameLooked)
                {
                    objectNameLooked = true;
                    objectName = cloudsObject.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                return objectName != null ? objectName.GetValue(cloudsObject, null) as string : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // EVE's members looked up once; false without EVE, with another build of it, or where one is missing.
        private bool Resolve()
        {
            if (resolved) return objectList != null;
            resolved = true;
            if (TypeLookup.Find("Atmosphere.CloudsManager") == null) return false;
            if (!ForeignAssumptions.EveRtClouds.Holds)
            {
                CompatibilityLog.Warn("rt-eve-clouds", "Raytracing: EVE's clouds are left out of the rays (this EVE is not the one checked).");
                return false;
            }
            Type manager = TypeLookup.Find("Atmosphere.CloudsManager");
            Type objectType = TypeLookup.Find("Atmosphere.CloudsObject");
            Type volumeType = TypeLookup.Find("Atmosphere.CloudsRaymarchedVolume");
            Type layerType = TypeLookup.Find("Atmosphere.Clouds2D");
            if (manager == null || objectType == null || volumeType == null || layerType == null) return false;
            for (Type t = manager; t != null && objectList == null; t = t.BaseType)
                objectList = t.GetField("ObjectList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            bodyName = objectType.GetProperty("Body");
            layer2D = objectType.GetProperty("Layer2D");
            volume = objectType.GetProperty("LayerRaymarchedVolume");
            volumeEnabled = volumeType.GetProperty("enabled");
            rotation = volumeType.GetProperty("CloudRotationMatrix");
            fadeCoverage = volumeType.GetProperty("CurrentTimeFadeCoverage");
            fadeDensity = volumeType.GetProperty("CurrentTimeFadeDensity");
            scaledFade = volumeType.GetProperty("VolumetricLayerScaledFade");
            volumeMaterial = volumeType.GetProperty("RaymarchedCloudMaterial");
            inner = volumeType.GetProperty("InnerSphereRadius");
            outer = volumeType.GetProperty("OuterSphereRadius");
            shadowMaterial = layerType.GetProperty("ScreenSpaceShadowMaterial");
            coverageMap = volumeType.GetProperty("CoverageMap");
            typeMap = volumeType.GetProperty("CloudTypeMap");
            curves = volumeType.GetField("curvesTexture", BindingFlags.Instance | BindingFlags.NonPublic);
            Type wrapper = TypeLookup.Find("Utils.TextureWrapper");
            if (wrapper == null && coverageMap != null) wrapper = coverageMap.PropertyType;
            wrapperTexture = wrapper != null ? wrapper.GetMethod("GetTexture", Type.EmptyTypes) : null;
            wrapperType = wrapper != null ? wrapper.GetField("type", BindingFlags.Instance | BindingFlags.NonPublic) : null;
            wrapperMask = wrapper != null ? wrapper.GetField("alphaMask", BindingFlags.Instance | BindingFlags.NonPublic) : null;
            scaled2D = layerType.GetProperty("Scaled");
            readObjects = CompiledGetter.Of<object>(objectList);
            readBodyName = CompiledGetter.Of<object>(bodyName);
            readLayer2D = CompiledGetter.Of<object>(layer2D);
            readVolume = CompiledGetter.Of<object>(volume);
            readVolumeMaterial = CompiledGetter.Of<object>(volumeMaterial);
            readShadowMaterial = CompiledGetter.Of<object>(shadowMaterial);
            readCoverageMap = CompiledGetter.Of<object>(coverageMap);
            readTypeMap = CompiledGetter.Of<object>(typeMap);
            readEnabled = CompiledGetter.Of<bool>(volumeEnabled);
            readScaled2D = CompiledGetter.Of<bool>(scaled2D);
            readRotation = CompiledGetter.Of<Matrix4x4>(rotation);
            readInner = CompiledGetter.Of<float>(inner);
            readOuter = CompiledGetter.Of<float>(outer);
            readFadeCoverage = CompiledGetter.Of<float>(fadeCoverage);
            readFadeDensity = CompiledGetter.Of<float>(fadeDensity);
            readScaledFade = CompiledGetter.Of<float>(scaledFade);
            if (objectList == null || bodyName == null || layer2D == null || volume == null || readRotation == null || volumeMaterial == null
                || readInner == null || readOuter == null || shadowMaterial == null || coverageMap == null || typeMap == null || curves == null
                || wrapperTexture == null || readObjects == null || readBodyName == null || readLayer2D == null || readVolume == null
                || readVolumeMaterial == null || readShadowMaterial == null || readCoverageMap == null || readTypeMap == null)
            {
                objectList = null;
                CompatibilityLog.Warn("rt-eve-clouds", "Raytracing: EVE's clouds are left out of the rays (this EVE has not the members read).");
                return false;
            }
            Debug.Log(Log.Tag + " Raytracing: EVE's volumetric clouds found; the rays take their layer.");
            return true;
        }

        // Fill's work: the first of the body's cloud objects with an enabled volume. Where it has a 2D layer, that
        // must not be in scaled space. It is packed and turned into the packet's constants. False and a logged reason
        // where there is none.
        private bool FillFrom(float[] clouds, CelestialBody body, Vector3 camera, bool pack)
        {
            if (!Resolve()) return false;
            IList objects = readObjects(null) as IList;
            if (objects == null) return false;
            if (body != namedBody)
            {
                namedBody = body;
                bodyNameOf = body.name;
            }
            string name = bodyNameOf;
            // Why there is no layer, as a text only where it is logged; null for none of
            // the body's.
            string why = null;
            for (int o = 0; o < objects.Count; o++)
            {
                object cloudsObject = objects[o];
                if (cloudsObject == null || (readBodyName(cloudsObject) as string) != name) continue;
                object layer = readVolume(cloudsObject);
                object flat = readLayer2D(cloudsObject);
                if (layer == null)
                {
                    why = "a layer without a volume";
                    continue;
                }
                // Not an aurora or another layer that shines by itself: it is no cloud for the rays or the moon's light.
                if (CloudLayerKinds.IsEmissive(NameOfObject(cloudsObject), readVolumeMaterial(layer) as Material))
                {
                    why = "a layer that shines by itself";
                    continue;
                }
                if (readEnabled != null && !readEnabled(layer))
                {
                    why = "the volume off";
                    continue;
                }
                if (flat != null && readScaled2D != null && readScaled2D(flat))
                {
                    why = "the 2D layer in scaled space";
                    continue;
                }
                Material material = readVolumeMaterial(layer) as Material;
                // A layer of the volume alone (Jool's): no shadow of a 2D layer, so the shadow stands in the middle of the
                // layer's shell, at EVE's default strength, white and never faded but by the volume's own fade.
                Material shadow = flat != null ? readShadowMaterial(flat) as Material : null;
                if (material == null || (flat != null && (shadow == null || !shadow.HasProperty(RadiusId))))
                {
                    why = material == null ? "no volume material" : shadow == null ? "no shadow material" : "no _Radius on the shadow material";
                    continue;
                }
                float innerRadius = readInner(layer), outerRadius = readOuter(layer);
                float radius = shadow != null ? shadow.GetFloat(RadiusId) : 0.5f * (innerRadius + outerRadius);
                if (pack && !Pack(layer, radius, innerRadius, outerRadius)) return false;

                // World to map, taken to the camera-relative world the rays work in.
                Matrix4x4 m = readRotation(layer) * Matrix4x4.Translate(camera);
                for (int row = 0; row < 3; row++)
                    for (int column = 0; column < 4; column++)
                        clouds[row * 4 + column] = m[row, column];
                clouds[12] = radius;
                clouds[13] = innerRadius;
                clouds[14] = outerRadius;
                clouds[15] = 1f;
                Color colour = shadow != null && shadow.HasProperty(ShadowColourId) ? shadow.GetColor(ShadowColourId) : Color.white;
                float factor = shadow != null && shadow.HasProperty(ShadowFactorId) ? shadow.GetFloat(ShadowFactorId) : 0.75f;
                clouds[16] = factor * colour.a;
                clouds[17] = readFadeCoverage != null ? readFadeCoverage(layer) : 1f;
                // The fades read without HasProperty, which asks the shader for the property
                // (Unity: "Checks if material's shader has a property of a given name") while
                // EVE only sets them on the material, from the layer's setup on
                // (Clouds2D.Apply, Reassign, SetTimeFade, SetMapViewFade): where the shader did not declare
                // them, the layer counted as never faded. Their names are checked in EVE's
                // code (ForeignAssumptions.EveRtClouds).
                float fade = (shadow != null ? shadow.GetFloat(TimeFadeDensityId) * shadow.GetFloat(MapViewFadeId) : 1f)
                             * (readScaledFade != null ? readScaledFade(layer) : 1f);
                clouds[18] = fade;
                clouds[19] = readFadeDensity != null ? readFadeDensity(layer) : 1f;
                clouds[20] = colour.r;
                clouds[21] = colour.g;
                clouds[22] = colour.b;
                clouds[23] = 1f;
                Last = this;
                lastLayer = layer;
                lastCentre = (Vector3)body.position;
                MapFromCentre = readRotation(layer) * Matrix4x4.Translate(lastCentre);
                MapFades = new Vector3(clouds[17], clouds[19], clouds[18]);
                filledFrame = Time.frameCount;
                if (loggedBody != name)
                {
                    loggedBody = name;
                    Debug.Log(Log.Tag + (pack ? " Raytracing: " : " Clouds: ") + body.name + (pack ? "'s clouds in the rays" : "'s layer for the moon's light") + ": shadow at " + radius.ToString("0")
                              + " m from the centre, the layer " + innerRadius.ToString("0") + " to " + outerRadius.ToString("0")
                              + " m, shadow strength " + clouds[16].ToString("0.00") + ", fades " + clouds[17].ToString("0.00") + " / "
                              + clouds[18].ToString("0.00") + " / " + clouds[19].ToString("0.00") + ", colour " + colour + ".");
                }
                return !pack || packedHandle != IntPtr.Zero;
            }
            int count = objects.Count;
            if (whyLogged != why || whyCount != count || whyBody != name || !whyAny)
            {
                whyLogged = why;
                whyCount = count;
                whyBody = name;
                whyAny = true;
                Debug.Log(Log.Tag + (pack ? " Raytracing: no clouds in the rays over " : " Clouds: no layer for the moon's light over ") + name + ": "
                          + (why ?? count + " cloud objects, none of " + name) + ".");
            }
            return false;
        }

        // What the last "no clouds" line said, for which body and with how many cloud objects, and whether one was
        // logged: the line comes again only when one of them changes.
        private string whyLogged, whyBody;
        private int whyCount;
        private bool whyAny;

        // The maps packed at the shadow's height, when they or the height change and every
        // few seconds (the coverage map is loaded on demand).
        private bool Pack(object layer, float radius, float innerRadius, float outerRadius)
        {
            object coverageWrapper = readCoverageMap(layer);
            Texture coverage = coverageWrapper != null ? wrapperTexture.Invoke(coverageWrapper, null) as Texture : null;
            if (coverage == null) return false;
            if (packed != null && coverage == packedCoverage && Mathf.Approximately(radius, packedRadius)
                && Time.unscaledTime - packedAt < RepackSeconds)
                return true;
            if (pack == null)
            {
                string error;
                Shader shader = ShaderBundle.LoadShader("Hidden/ReDefinition/RtClouds", out error);
                if (shader == null) return false;
                pack = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (packed == null)
            {
                packed = new RenderTexture(Width, Height, 0, RenderTextureFormat.RG16, RenderTextureReadWrite.Linear)
                {
                    name = "ReDefinition raytracing clouds",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                };
                packed.Create();
                packedHandle = packed.GetNativeTexturePtr();
            }
            object typeWrapper = readTypeMap(layer);
            Texture type = typeWrapper != null ? wrapperTexture.Invoke(typeWrapper, null) as Texture : null;
            pack.SetTexture("CloudCoverage", coverage);
            pack.SetTexture("CloudType", type != null ? type : Texture2D.whiteTexture);
            pack.SetTexture("DensityCurve", curves.GetValue(layer) as Texture);
            Mask(coverageWrapper, "useAlphaMask1", "alphaMask1");
            Mask(typeWrapper, "useAlphaMask2", "alphaMask2");
            pack.SetFloat("_ReDefinitionCloudHeight", (radius - innerRadius) / Mathf.Max(outerRadius - innerRadius, 1f));
            Graphics.Blit(null, packed, pack);
            packedCoverage = coverage;
            packedRadius = radius;
            packedAt = Time.unscaledTime;
            return true;
        }

        // A map's alpha mask as EVE's TextureWrapper.SetAlphaMask sets it: where its type is
        // an alpha map, the channel its alphaMask names (ALPHAMAP_R..A); otherwise none.
        private void Mask(object wrapper, string use, string mask)
        {
            bool alphaMap;
            Vector4 channel;
            MaskOf(wrapper, out alphaMap, out channel);
            pack.SetFloat(use, alphaMap ? 1f : 0f);
            pack.SetVector(mask, channel);
        }

        // Whether a map is read through an alpha mask, and the channel it names.
        private void MaskOf(object wrapper, out bool alphaMap, out Vector4 channel)
        {
            alphaMap = false;
            channel = Vector4.zero;
            if (wrapper == null || wrapperType == null || wrapperMask == null) return;
            object type = wrapperType.GetValue(wrapper);
            alphaMap = type != null && type.ToString().IndexOf("AlphaMap", StringComparison.Ordinal) >= 0;
            string name = wrapperMask.GetValue(wrapper)?.ToString() ?? "";
            channel = new Vector4(name.EndsWith("_R") ? 1f : 0f, name.EndsWith("_G") ? 1f : 0f, name.EndsWith("_B") ? 1f : 0f,
                                  name.EndsWith("_A") ? 1f : 0f);
        }

        // The layer's own maps as EVE's raymarching reads them, for the moon's light per pixel (EveCloudRelight): the
        // coverage and the type texture with the masks that pick their channels, and the density curve. Looked up again every
        // second. False where the layer was not filled in the last frame or the one before.
        internal bool Maps(out Texture coverage, out Texture type, out Texture curve, out Vector4 masks1, out Vector4 masks2,
                           out Vector2 uses)
        {
            coverage = mapsCoverage;
            type = mapsType;
            curve = mapsCurve;
            masks1 = mapsMask1;
            masks2 = mapsMask2;
            uses = mapsUses;
            if (lastLayer == null || Time.frameCount - filledFrame > 1) return false;
            if (mapsLayer != lastLayer || Time.unscaledTime - mapsAt > 1f || mapsCoverage == null)
            {
                object coverageWrapper = readCoverageMap(lastLayer), typeWrapper = readTypeMap(lastLayer);
                mapsCoverage = coverageWrapper != null ? wrapperTexture.Invoke(coverageWrapper, null) as Texture : null;
                mapsType = typeWrapper != null ? wrapperTexture.Invoke(typeWrapper, null) as Texture : null;
                mapsCurve = curves.GetValue(lastLayer) as Texture;
                bool use1, use2;
                MaskOf(coverageWrapper, out use1, out mapsMask1);
                MaskOf(typeWrapper, out use2, out mapsMask2);
                mapsUses = new Vector2(use1 ? 1f : 0f, use2 ? 1f : 0f);
                mapsLayer = lastLayer;
                mapsAt = Time.unscaledTime;
            }
            coverage = mapsCoverage;
            type = mapsType;
            curve = mapsCurve;
            masks1 = mapsMask1;
            masks2 = mapsMask2;
            uses = mapsUses;
            return coverage != null && type != null && curve != null;
        }

        // What Maps found last, the layer it was for and when.
        private Texture mapsCoverage, mapsType, mapsCurve;
        private Vector4 mapsMask1, mapsMask2;
        private Vector2 mapsUses;
        private object mapsLayer;
        private float mapsAt = -100f;

        // The body's layer kept for the moon's light on the clouds where the rays keep none, as with the raytracing off: this frame's
        // layer is found without packing its map for the rays. Where the rays filled a layer in the last frame or this one, nothing is
        // done.
        internal static void Keep(CelestialBody body)
        {
            RtEveClouds last = Last;
            if (last != null && Time.frameCount - last.filledFrame <= 1 && last.namedBody == body) return;
            if (own == null) own = new RtEveClouds();
            if (own.failed || body == null) return;
            try
            {
                own.FillFrom(keepScratch, body, Vector3.zero, false);
            }
            catch (Exception e)
            {
                own.failed = true;
                CompatibilityLog.Warn("rt-eve-clouds", "The clouds' maps for the moon's light could not be read (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The instance Keep fills, and the constants it throws away.
        private static RtEveClouds own;
        private static readonly float[] keepScratch = new float[32];

        // The last layer filled, for the probe's cloud cover (DensityAt) and the moon's light on
        // the clouds (EveCloudRelight).
        internal static RtEveClouds Last { get; private set; }

        // From the planet's centre, world axes, to the packed map's direction.
        internal Matrix4x4 MapFromCentre { get; private set; }

        // The layer's coverage fade, density fade and its other fades together, as the rays take them.
        internal Vector3 MapFades { get; private set; }

        // The frame Fill last filled a layer in.
        private int filledFrame = -10;

        // The frame the layer was last filled in, for the log.
        internal int FilledFrame { get { return filledFrame; } }
        // The last layer filled and its body's centre, for the probe's checks; EVE's SampleCoverage, looked up on
        // first use.
        private object lastLayer;
        private Vector3 lastCentre;
        private MethodInfo sampleCoverage;

        // For the probe: EVE's own density of the last layer filled at a world point
        // (CloudsRaymarchedVolume.SampleCoverage, CPU), 0 to 1; -1 where there is none.
        internal float DensityAt(Vector3 world)
        {
            if (!HasSampleCoverage()) return -1f;
            object[] arguments = { world, 0f, false };
            return (float)sampleCoverage.Invoke(lastLayer, arguments);
        }

        // Whether there is a layer to sample and EVE's SampleCoverage was found on it, looked up on first use.
        private bool HasSampleCoverage()
        {
            if (lastLayer == null) return false;
            if (sampleCoverage == null)
                sampleCoverage = lastLayer.GetType().GetMethod("SampleCoverage", new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(bool) });
            return sampleCoverage != null;
        }

        // The layer's shell, from the planet's centre, for the probe; false where none is filled.
        internal bool Shell(out float inner, out float outer)
        {
            inner = outer = 0f;
            if (lastLayer == null || readInner == null || readOuter == null) return false;
            inner = readInner(lastLayer);
            outer = readOuter(lastLayer);
            return outer > inner;
        }

        // No layer: the constants all 0, no texture.
        internal static IntPtr None(float[] clouds)
        {
            for (int i = 0; i < clouds.Length; i++) clouds[i] = 0f;
            return IntPtr.Zero;
        }

        // Frees the packed map and its material; the next Fill packs anew.
        internal void Release()
        {
            if (packed != null)
            {
                packed.Release();
                UnityEngine.Object.Destroy(packed);
            }
            packed = null;
            packedHandle = IntPtr.Zero;
            if (pack != null) UnityEngine.Object.Destroy(pack);
            pack = null;
            packedCoverage = null;
        }
    }
}
