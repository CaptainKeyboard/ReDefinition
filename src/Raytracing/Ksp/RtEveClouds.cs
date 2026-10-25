using System;
using System.Collections;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // EVE's cloud layer over the main body, for the rays (RtCommon.hlsli: CloudTransmittance,
    // CloudOverSky): the moons' light, the sun's and the moons' discs on the water and the
    // sun's light at the rays' hits are darkened under it as EVE darkens the sun's light on
    // the frame, and rays that escape the scene see it. Read from EVE (Blackrack's, with the
    // raymarched volumetric layer; its assembly decompiled, 1.12 builds of 2026-10):
    // GenericEVEManager<CloudsObject>.ObjectList (static, not public) holds the loaded
    // bodies' layers; a CloudsObject's Body, Layer2D (Clouds2D) and LayerRaymarchedVolume
    // (CloudsRaymarchedVolume). The volume's material carries the maps (CloudCoverage,
    // CloudType, DensityCurve, their alpha masks) and radii, its CloudRotationMatrix the
    // world-to-map transform EVE sets each frame, its CurrentTimeFade* the fades; the 2D
    // layer's ScreenSpaceShadowMaterial the shadow's sphere (_Radius), strength
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
        // -redefinitionProbeCloudValue 1, 2, 3: with the probe's cloud check, the rays write
        // the map's u, v or coverage (RtCommon.hlsli, CloudProbeValue); the brightness 1 else.
        private static readonly float ProbeMode = ProbeArgument();
        private static readonly int TimeFadeDensityId = Shader.PropertyToID("cloudTimeFadeDensity");
        private static readonly int MapViewFadeId = Shader.PropertyToID("mapViewFade");

        // The value of -redefinitionProbeCloudValue as the rays take it: 10 plus the mode, 1 without it.
        private static float ProbeArgument()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-redefinitionProbeCloudValue");
            int mode;
            return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out mode) ? 10f + mode : 1f;
        }

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
                return FillFrom(clouds, body, camera);
            }
            catch (Exception e)
            {
                failed = true;
                CompatibilityLog.Warn("rt-eve-clouds", "Raytracing: EVE's clouds are left out of the rays (" + CompatibilityLog.Reason(e) + ").");
                for (int i = 0; i < clouds.Length; i++) clouds[i] = 0f;
                return false;
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
                || wrapperTexture == null)
            {
                objectList = null;
                CompatibilityLog.Warn("rt-eve-clouds", "Raytracing: EVE's clouds are left out of the rays (this EVE has not the members read).");
                return false;
            }
            Debug.Log(Log.Tag + " Raytracing: EVE's volumetric clouds found; the rays take their layer.");
            return true;
        }

        // Fill's work: the first of the body's cloud objects with an enabled volume and a 2D layer out of scaled space,
        // packed and turned into the packet's constants; false and a logged reason where none is.
        private bool FillFrom(float[] clouds, CelestialBody body, Vector3 camera)
        {
            if (!Resolve()) return false;
            IList objects = objectList.GetValue(null) as IList;
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
            foreach (object cloudsObject in objects)
            {
                if (cloudsObject == null || (bodyName.GetValue(cloudsObject, null) as string) != name) continue;
                object layer = volume.GetValue(cloudsObject, null);
                object flat = layer2D.GetValue(cloudsObject, null);
                if (layer == null || flat == null)
                {
                    why = "a layer without " + (layer == null ? "a volume" : "a 2D layer");
                    continue;
                }
                if (readEnabled != null && !readEnabled(layer))
                {
                    why = "the volume off";
                    continue;
                }
                if (readScaled2D != null && readScaled2D(flat))
                {
                    why = "the 2D layer in scaled space";
                    continue;
                }
                Material material = volumeMaterial.GetValue(layer, null) as Material;
                Material shadow = shadowMaterial.GetValue(flat, null) as Material;
                if (material == null || shadow == null || !shadow.HasProperty("_Radius"))
                {
                    why = material == null ? "no volume material" : shadow == null ? "no shadow material" : "no _Radius on the shadow material";
                    continue;
                }
                float radius = shadow.GetFloat("_Radius");
                float innerRadius = readInner(layer), outerRadius = readOuter(layer);
                if (!Pack(layer, radius, innerRadius, outerRadius)) return false;

                // World to map, taken to the camera-relative world the rays work in.
                Matrix4x4 m = readRotation(layer) * Matrix4x4.Translate(camera);
                for (int row = 0; row < 3; row++)
                    for (int column = 0; column < 4; column++)
                        clouds[row * 4 + column] = m[row, column];
                clouds[12] = radius;
                clouds[13] = innerRadius;
                clouds[14] = outerRadius;
                clouds[15] = 1f;
                Color colour = shadow.HasProperty("_Color") ? shadow.GetColor("_Color") : Color.white;
                float factor = shadow.HasProperty("_ShadowFactor") ? shadow.GetFloat("_ShadowFactor") : 0.75f;
                clouds[16] = factor * colour.a;
                clouds[17] = readFadeCoverage != null ? readFadeCoverage(layer) : 1f;
                // The fades read without HasProperty, which asks the shader for the property
                // (Unity: "Checks if material's shader has a property of a given name") while
                // EVE only sets them on the material, from the layer's setup on
                // (Clouds2D.Apply, Reassign, SetTimeFade, SetMapViewFade): where the shader did not declare
                // them, the layer counted as never faded. Their names are checked in EVE's
                // code (ForeignAssumptions.EveRtClouds).
                float fade = shadow.GetFloat(TimeFadeDensityId) * shadow.GetFloat(MapViewFadeId)
                             * (readScaledFade != null ? readScaledFade(layer) : 1f);
                clouds[18] = fade;
                clouds[19] = readFadeDensity != null ? readFadeDensity(layer) : 1f;
                clouds[20] = colour.r;
                clouds[21] = colour.g;
                clouds[22] = colour.b;
                clouds[23] = ProbeMode;
                Last = this;
                lastLayer = layer;
                lastRadius = radius;
                lastCentre = (Vector3)body.position;
                MapFromCentre = readRotation(layer) * Matrix4x4.Translate(lastCentre);
                MapFades = new Vector3(clouds[17], clouds[19], clouds[18]);
                filledFrame = Time.frameCount;
                if (loggedBody != name)
                {
                    loggedBody = name;
                    Debug.Log(Log.Tag + " Raytracing: " + body.name + "'s clouds in the rays: shadow at " + radius.ToString("0")
                              + " m from the centre, the layer " + innerRadius.ToString("0") + " to " + outerRadius.ToString("0")
                              + " m, shadow strength " + clouds[16].ToString("0.00") + ", fades " + clouds[17].ToString("0.00") + " / "
                              + clouds[18].ToString("0.00") + " / " + clouds[19].ToString("0.00") + ", colour " + colour + ".");
                }
                return packedHandle != IntPtr.Zero;
            }
            int count = objects.Count;
            if (whyLogged != why || whyCount != count || whyBody != name || !whyAny)
            {
                whyLogged = why;
                whyCount = count;
                whyBody = name;
                whyAny = true;
                Debug.Log(Log.Tag + " Raytracing: no clouds in the rays over " + name + ": "
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
            object coverageWrapper = coverageMap.GetValue(layer, null);
            object typeWrapper = typeMap.GetValue(layer, null);
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
            bool alphaMap = false;
            Vector4 channel = Vector4.zero;
            if (wrapper != null && wrapperType != null && wrapperMask != null)
            {
                object type = wrapperType.GetValue(wrapper);
                alphaMap = type != null && type.ToString().IndexOf("AlphaMap", StringComparison.Ordinal) >= 0;
                string name = wrapperMask.GetValue(wrapper)?.ToString() ?? "";
                channel = new Vector4(name.EndsWith("_R") ? 1f : 0f, name.EndsWith("_G") ? 1f : 0f, name.EndsWith("_B") ? 1f : 0f,
                                      name.EndsWith("_A") ? 1f : 0f);
            }
            pack.SetFloat(use, alphaMap ? 1f : 0f);
            pack.SetVector(mask, channel);
        }

        // The last layer filled, for the probe's check (EvePrediction) and the moon's light on
        // the clouds (EveCloudRelight).
        internal static RtEveClouds Last { get; private set; }

        // The packed map of the layer filled in the last frame or the one before, for the
        // moon's light on the clouds; null for none.
        internal RenderTexture PackedMap { get { return Time.frameCount - filledFrame <= 1 ? packed : null; } }

        // From the planet's centre, world axes, to the packed map's direction.
        internal Matrix4x4 MapFromCentre { get; private set; }

        // The layer's coverage fade, density fade and its other fades together, as the rays take them.
        internal Vector3 MapFades { get; private set; }

        // The frame Fill last filled a layer in.
        private int filledFrame = -10;

        // The frame the layer was last filled in, for the log.
        internal int FilledFrame { get { return filledFrame; } }
        // The last layer filled, its shadow's radius and its body's centre, for the probe's checks; EVE's
        // SampleCoverage, looked up on first use.
        private object lastLayer;
        private float lastRadius;
        private Vector3 lastCentre;
        private MethodInfo sampleCoverage;

        // What EVE's own code (CloudsRaymarchedVolume.SampleCoverage: coverage times the
        // density curve, CPU) gives where the light's ray from `world` towards the sun leaves
        // the shadow's sphere, as the transmittance and the shadow the rays take there
        // (RtCommon.hlsli, CloudTransmittance, its fades 1); -1 where there is no layer.
        internal float EvePrediction(Vector3 world, Vector3 towardsSun, Color colour, float strength)
        {
            if (lastLayer == null) return -1f;
            if (sampleCoverage == null)
                sampleCoverage = lastLayer.GetType().GetMethod("SampleCoverage", new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(bool) });
            if (sampleCoverage == null) return -1f;
            Vector3 toCentre = lastCentre - world;
            float distance = toCentre.magnitude;
            if (distance > lastRadius) return -1f;
            float along = Vector3.Dot(toCentre, towardsSun);
            float t = along + Mathf.Sqrt(Mathf.Max(lastRadius * lastRadius - (distance * distance - along * along), 0f));
            Vector3 at = world + towardsSun * t;
            object[] arguments = { at, 0f, false };
            float density = (float)sampleCoverage.Invoke(lastLayer, arguments);
            float tr = 1f / (1f + 7f * density + 26f * density * density);
            float fade = Mathf.Clamp01(100f * Vector3.Dot(toCentre / distance, -towardsSun)) * Mathf.Clamp01(0.01f * (lastRadius - distance));
            return 1f + fade * strength * (1f - tr) * (Mathf.Clamp01(tr * colour.r) - 1f);
        }

        // For the probe: EVE's own density of the last layer filled at a world point
        // (CloudsRaymarchedVolume.SampleCoverage, CPU), 0 to 1; -1 where there is none.
        internal float DensityAt(Vector3 world)
        {
            if (lastLayer == null) return -1f;
            if (sampleCoverage == null)
                sampleCoverage = lastLayer.GetType().GetMethod("SampleCoverage", new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(bool) });
            if (sampleCoverage == null) return -1f;
            object[] arguments = { world, 0f, false };
            return (float)sampleCoverage.Invoke(lastLayer, arguments);
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

        // For the probe: at the exit point towards the sun, EVE's density, its coverage and type
        // maps there (TextureWrapper.Sample), the map's direction, and our packed map's texel
        // at the same place as the CPU reads it.
        internal string EveRaw(Vector3 world, Vector3 towardsSun)
        {
            if (lastLayer == null) return "no layer";
            Vector3 toCentre = lastCentre - world;
            float distance = toCentre.magnitude;
            float along = Vector3.Dot(toCentre, towardsSun);
            float t = along + Mathf.Sqrt(Mathf.Max(lastRadius * lastRadius - (distance * distance - along * along), 0f));
            Vector3 at = world + towardsSun * t;
            Matrix4x4 m = (Matrix4x4)rotation.GetValue(lastLayer, null);
            Vector3 n = m.MultiplyPoint(at).normalized;
            object coverageWrapper = coverageMap.GetValue(lastLayer, null);
            object typeWrapper = typeMap.GetValue(lastLayer, null);
            MethodInfo sample = coverageWrapper != null ? coverageWrapper.GetType().GetMethod("Sample", new[] { typeof(Vector3) }) : null;
            Color coverage = sample != null ? (Color)sample.Invoke(coverageWrapper, new object[] { n }) : Color.clear;
            Color type = sample != null && typeWrapper != null ? (Color)sample.Invoke(typeWrapper, new object[] { n }) : Color.clear;
            object[] arguments = { at, 0f, false };
            float density = sampleCoverage != null ? (float)sampleCoverage.Invoke(lastLayer, arguments) : -1f;
            float u = 0.5f + Mathf.Atan2(n.x, n.z) / (2f * Mathf.PI), v = Mathf.Acos(Mathf.Clamp(n.y, -1f, 1f)) / Mathf.PI;
            Color texel = Color.clear;
            if (packed != null)
            {
                RenderTexture before = RenderTexture.active;
                RenderTexture.active = packed;
                var read = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(Mathf.Clamp((int)(u * Width), 0, Width - 1), Mathf.Clamp((int)(v * Height), 0, Height - 1), 1, 1), 0, 0, false);
                texel = read.GetPixel(0, 0);
                RenderTexture.active = before;
                UnityEngine.Object.Destroy(read);
            }
            CelestialBody body = FlightGlobals.currentMainBody;
            string centres = body == null ? "" : " [distances: body.position " + (world - (Vector3)body.position).magnitude.ToString("0")
                + ", transform " + (world - body.transform.position).magnitude.ToString("0")
                + ", pqs " + (body.pqsController != null ? (world - body.pqsController.transform.position).magnitude.ToString("0") : "-")
                + ", radius " + body.Radius.ToString("0") + "]";
            float coverageOurs = texel.r, curveOurs = texel.g;
            float densityOurs = curveOurs < 0.01f || coverageOurs < 0.01f ? 0f : curveOurs * coverageOurs;
            float trOurs = 1f / (1f + 7f * densityOurs + 26f * densityOurs * densityOurs);
            // The camera-relative path the proxy takes: the matrix with the camera's translation.
            Vector3 camera = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            centres += " ours on the CPU: shadow " + (1f + (1f - trOurs) * (Mathf.Clamp01(trOurs * 2f) - 1f)).ToString("0.00");
            return centres + " exit " + (at - lastCentre).magnitude.ToString("0") + " m up, uv " + u.ToString("0.000") + "," + v.ToString("0.000")
                   + ", EVE density " + density.ToString("0.000") + " coverage " + coverage + " type " + type
                   + ", packed " + texel;
        }

        // For the probe: EVE's coverage map against our packed map at random directions, as
        // packed and with v flipped; the mean differences.
        internal string MapCheck()
        {
            if (lastLayer == null || packed == null) return "no layer";
            object coverageWrapper = coverageMap.GetValue(lastLayer, null);
            MethodInfo sample = coverageWrapper.GetType().GetMethod("Sample", new[] { typeof(Vector3) });
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = packed;
            var read = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            RenderTexture.active = before;
            var random = new System.Random(7);
            double same = 0, flipped = 0, mirroredU = 0;
            int n = 0;
            for (int i = 0; i < 400; i++)
            {
                Vector3 d = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 1.6f - 0.8f, (float)random.NextDouble() * 2f - 1f).normalized;
                float eve = ((Color)sample.Invoke(coverageWrapper, new object[] { d })).r;
                float u = 0.5f + Mathf.Atan2(d.x, d.z) / (2f * Mathf.PI), v = Mathf.Acos(d.y) / Mathf.PI;
                same += Math.Abs(read.GetPixelBilinear(u, v).r - eve);
                flipped += Math.Abs(read.GetPixelBilinear(u, 1f - v).r - eve);
                mirroredU += Math.Abs(read.GetPixelBilinear(1f - u, v).r - eve);
                n++;
            }
            UnityEngine.Object.Destroy(read);
            return "map check over " + n + " directions, mean difference as packed " + (same / n).ToString("0.000") + ", v flipped "
                   + (flipped / n).ToString("0.000") + ", u mirrored " + (mirroredU / n).ToString("0.000");
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
