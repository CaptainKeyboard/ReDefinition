using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The moon's light on EVE's raymarched clouds per pixel, as a second sun with the moon's
    // values, while the sun sets and the clouds still in its light keep it. Scatterer's
    // raymarching shader lights the clouds with one light, the sun's. It shades each cloud by
    // the planet's shadow at its place, so that the clouds still in the sunset are lit by it and
    // the others dark. Right after EVE has reconstructed a camera's clouds for the frame (its
    // command buffer at AfterForwardOpaque, which DeferredRaymarchedVolumetricCloudsRenderer
    // .OnPreRender adds), a command buffer of this class adds the moon's light at each pixel's
    // cloud. It adds it into a copy of EVE's buffer (ReDefinitionCloudMoonlight.shader) and binds the copy
    // where EVE bound its own (colorBuffer, scattererReconstructedCloud). EVE's composite,
    // Scatterer's sky and the upscaler's cloud masks read it this frame, and EVE's history stays its
    // own. No cloud is drawn a second time. EveCloudMoonlight says when and with what light
    // (Light), and turns the clouds' one light to the moon once the sun reaches none of them in
    // view, as this light gives way. Only on cameras that are no reflection probe, in mono.
    // The compiled getters below read EVE's members.
    internal static class EveCloudRelight
    {
        // The Harmony id, EVE's renderer and volume types, and the camera event EVE's command buffer runs at.
        private const string HarmonyId = "ReDefinition.EveCloudRelight";
        private const string RendererType = "Atmosphere.DeferredRaymarchedVolumetricCloudsRenderer";
        private const string VolumeType = "Atmosphere.CloudsRaymarchedVolume";
        private const CameraEvent Event = CameraEvent.AfterForwardOpaque;

        // The per-pixel light's strength against the light the raymarching gives the clouds in the moon's light. The shader
        // repeats the raymarching's sum, which a gain of 1 would match. The probe's matrix (LightProbe, CloudMatrix) read the
        // gain that equals the two at nine views, with the moon 25, 45 and 65 degrees up. It came out from 0.8 to 4.3, the
        // thin clouds needing most, with a median of 2.1: the raymarching erodes the density with a noise the maps lack.
        // The gain stands at that median. The sky's light is exact at 1 and negligible beside the direct.
        internal const float DefaultDirectGain = 2.1f;
        internal const float DefaultSkyGain = 1f;
        internal static float DirectGain = DefaultDirectGain;
        internal static float SkyGain = DefaultSkyGain;

        // The scale of the optical depth towards the moon, which the layer's maps give without the noise the raymarching erodes the
        // density with. The probe measures it so that the direct gain comes out at 1.
        internal const float DefaultTauScale = 1f;
        internal static float TauScale = DefaultTauScale;

        // For the light probe: the clouds without this light.
        internal static bool HeldOff;

        // The value of -redefinitionCloudRelightDebug. With 1 each pixel's cloud point is green inside the
        // layer's shell, red outside it, blue without a distance. With 2 the image is the moon's direct
        // light alone, with 3 its sky's, with 4 to 7 the parts of the light (ReDefinitionCloudMoonlight.shader).
        // The probe sets it too.
        internal static float DebugMode = DebugArgument();

        // The colours last handed to the shader, for the probe's log: the moon's light, the
        // clouds' colour, EVE's skylight multiplier times Scatterer's exposure.
        internal static Color LastMoonLight, LastCloudColour;
        internal static float LastSkyScale;

        // For the probe's calibration: the copy made even where no light is to be added, so
        // that EVE's own buffer can be read.
        internal static bool ProbeAlways;

        // The buffers EVE binds its reconstructed clouds as, which get the copy, and the shader's inputs
        // (ReDefinitionCloudMoonlight.shader).
        private static readonly int ColourBufferId = Shader.PropertyToID("colorBuffer");
        private static readonly int ReconstructedId = Shader.PropertyToID("scattererReconstructedCloud");
        private static readonly int DistanceId = Shader.PropertyToID("_ReDefinitionCloudDistance");
        private static readonly int InvProjectionId = Shader.PropertyToID("_ReDefinitionCloudInvProjection");
        private static readonly int CameraToWorldId = Shader.PropertyToID("_ReDefinitionCloudCameraToWorld");
        private static readonly int CameraId = Shader.PropertyToID("_ReDefinitionCloudCamera");
        private static readonly int MoonId = Shader.PropertyToID("_ReDefinitionCloudMoon");
        private static readonly int MoonColourId = Shader.PropertyToID("_ReDefinitionCloudMoonColour");
        private static readonly int HeightsId = Shader.PropertyToID("_ReDefinitionCloudHeights");
        private static readonly int BetaRId = Shader.PropertyToID("_ReDefinitionCloudBetaR");
        private static readonly int BetaMExId = Shader.PropertyToID("_ReDefinitionCloudBetaMEx");
        private static readonly int DirectPhaseId = Shader.PropertyToID("_ReDefinitionCloudDirectPhase");
        private static readonly int MultiplePhaseId = Shader.PropertyToID("_ReDefinitionCloudMultiplePhase");
        private static readonly int AtlasId = Shader.PropertyToID("_ReDefinitionCloudAtlas");
        private static readonly int TransmittanceRectId = Shader.PropertyToID("_ReDefinitionCloudTransmittanceRect");
        private static readonly int AtlasSizesId = Shader.PropertyToID("_ReDefinitionCloudAtlasSizes");
        private static readonly int CoverageId = Shader.PropertyToID("_ReDefinitionCloudCoverage");
        private static readonly int TypeId = Shader.PropertyToID("_ReDefinitionCloudType");
        private static readonly int CurveId = Shader.PropertyToID("_ReDefinitionCloudCurve");
        private static readonly int UseMask1Id = Shader.PropertyToID("_ReDefinitionCloudUseMask1");
        private static readonly int UseMask2Id = Shader.PropertyToID("_ReDefinitionCloudUseMask2");
        private static readonly int Mask1Id = Shader.PropertyToID("_ReDefinitionCloudMask1");
        private static readonly int Mask2Id = Shader.PropertyToID("_ReDefinitionCloudMask2");
        private static readonly int TypeDensityId = Shader.PropertyToID("_ReDefinitionCloudTypeDensity");
        private static readonly int TypeBrightnessId = Shader.PropertyToID("_ReDefinitionCloudTypeBrightness");
        private static readonly int TypeCountId = Shader.PropertyToID("_ReDefinitionCloudTypes");
        private static readonly int MapRow0Id = Shader.PropertyToID("_ReDefinitionCloudMapRow0");
        private static readonly int MapRow1Id = Shader.PropertyToID("_ReDefinitionCloudMapRow1");
        private static readonly int MapRow2Id = Shader.PropertyToID("_ReDefinitionCloudMapRow2");
        private static readonly int MapFadesId = Shader.PropertyToID("_ReDefinitionCloudMapFades");
        private static readonly int DebugId = Shader.PropertyToID("_ReDefinitionCloudDebug");
        private static readonly int SkyColourId = Shader.PropertyToID("_ReDefinitionCloudSkyColour");
        private static readonly int IrradianceRectId = Shader.PropertyToID("_ReDefinitionCloudIrradianceRect");
        private static readonly int SkySizeId = Shader.PropertyToID("_ReDefinitionCloudSkySize");

        // On Scatterer's and EVE's material.
        private static readonly int SphereCenterId = Shader.PropertyToID("sphereCenter");
        private static readonly int RgId = Shader.PropertyToID("Rg");
        private static readonly int RtId = Shader.PropertyToID("Rt");
        private static readonly int HrId = Shader.PropertyToID("HR");
        private static readonly int HmId = Shader.PropertyToID("HM");
        private static readonly int BetaRSourceId = Shader.PropertyToID("betaR");
        private static readonly int BetaMExSourceId = Shader.PropertyToID("betaMEx");
        private static readonly int TransmittanceSourceId = Shader.PropertyToID("TransmittanceAtlasScaleAndOffset");
        private static readonly int AtlasDimensionsId = Shader.PropertyToID("AtmosphereAtlasDimensions");
        private static readonly int TransmittanceWId = Shader.PropertyToID("TRANSMITTANCE_W");
        private static readonly int TransmittanceHId = Shader.PropertyToID("TRANSMITTANCE_H");
        private static readonly int CloudColourId = Shader.PropertyToID("cloudColor");
        private static readonly int DirectSourceId = Shader.PropertyToID("directPhaseFunctionProperties");
        private static readonly int MultipleSourceId = Shader.PropertyToID("multiplePhaseFunctionProperties");
        private static readonly int SkylightId = Shader.PropertyToID("skylightMultiplier");
        private static readonly int SkylightTintId = Shader.PropertyToID("skylightTintMultiplier");
        private static readonly int ExposureId = Shader.PropertyToID("_ScatteringExposure");
        private static readonly int IrradianceSourceId = Shader.PropertyToID("IrradianceAtlasScaleAndOffset");
        private static readonly int SkyWId = Shader.PropertyToID("SKY_W");
        private static readonly int SkyHId = Shader.PropertyToID("SKY_H");
        private static readonly int TypePropertiesSourceId = Shader.PropertyToID("cloudTypeProperties0");
        private static readonly int TypeBrightnessSourceId = Shader.PropertyToID("multipleScatteringBrightnessArray");
        private static readonly int TypeCountSourceId = Shader.PropertyToID("numberOfCloudTypes");

        // The cloud types' density and multiple scattering brightness as the shader takes them: at most twelve, as EVE's
        // arrays hold; and the lists they are read into.
        private const int MostTypes = 12;
        private static readonly float[] typeDensity = new float[MostTypes], typeBrightness = new float[MostTypes];
        private static readonly List<Vector4> typeProperties = new List<Vector4>();
        private static readonly List<float> typeBrightnessList = new List<float>();

        // Compiled getters of EVE's members (InstallHook), read from Atmosphere.dll 3.2.2, decompiled
        // (its renderer's source on GitHub, LGhassen/EnvironmentalVisualEnhancements): the renderer's targetCamera.
        private static Func<object, Camera> readCamera;
        // The renderer's renderingEnabled, reflectionProbeCamera and useFlipUpscalingBuffer (the
        // history EVE writes this frame, flipped in OnPostRender).
        private static Func<object, bool> readRendering, readProbe, readFlip;
        // The renderer's volumesAdded.
        private static Func<object, IList> readVolumes;
        // The histories historyRT (colour, rgb premultiplied light, a the transmittance) and historyDistanceRT
        // (the distance to the clouds' weighted point in hundreds of metres), indexed by
        // HistoryManager[flip, right eye, cubemap face].
        private static Func<object, bool, RenderTexture> readColour, readDistance;
        // A volume's RaymarchedCloudMaterial, which carries Scatterer's atmosphere
        // (SkyNode.InitPostprocessMaterialUniforms) and EVE's phase functions and colour.
        private static Func<object, Material> readMaterial;
        // A volume's radii.
        private static Func<object, float> readInner, readOuter;
        // Whether the hooks are in, whether a failure turned the light per pixel off for the session, whether it
        // was logged.
        private static bool installed, failed, warned;
        // The material of ReDefinitionCloudMoonlight.shader.
        private static Material material;

        // What EveCloudMoonlight asked for this frame: the body, from its centre towards the
        // moon, the moon's light on the clouds and its strength; none where `requested` is
        // older than the frame before.
        private static CelestialBody body;
        // Scatterer's atmosphere atlas of that body (SkyNode.atmosphereAtlas), which
        // EveCloudMoonlight reads: the cloud materials' shader does not declare it, so a
        // material does not give it back.
        internal static Texture Atlas;
        private static Vector3 towardsMoon;
        private static Color moonLight;
        private static float strength;
        private static int requested = -10;
        // Frames asked for in a row in which the light could not be added.
        private static int missed;

        // A camera's pass: its own command buffer and copy, made once.
        private sealed class PerCamera
        {
            // The command buffer and the copy it fills; EVE's buffer copied this frame; the camera; whether the command
            // buffer is on the camera; whether the camera was logged; the frame it was last prepared in.
            internal CommandBuffer Command;
            internal RenderTexture Target;
            internal RenderTexture Source;
            internal Camera Camera;
            internal bool Attached;
            internal bool Logged;
            internal int Frame = -10;
        }

        // By camera instance id: each camera's command buffer and copy.
        private static readonly Dictionary<int, PerCamera> cameras = new Dictionary<int, PerCamera>();

        // Whether the light can be added per pixel: the hooks in, the shader there, and not
        // asked for in vain for some frames. The shader is loaded the first time this is asked,
        // in a scene, where the bundle is open.
        internal static bool Ready
        {
            get
            {
                if (!installed || failed) return false;
                // Missed for some frames in a row (no packed map, the rays off, another body's
                // clouds): the light all at once meanwhile, and tried again some seconds later.
                if (missed >= 10)
                {
                    if (missedSince < 0)
                    {
                        missedSince = Time.frameCount;
                        Debug.Log(Log.Tag + " Clouds: the moon's light per pixel paused, " + missed + " frames missed.");
                    }
                    if (Time.frameCount - missedSince < 300) return false;
                    missed = 0;
                    missedSince = -1;
                }
                if (!shaderTried)
                {
                    shaderTried = true;
                    string problem;
                    Shader shader = ShaderBundle.LoadShader("Hidden/ReDefinition/CloudMoonlight", out problem);
                    if (shader != null) material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    else Debug.LogWarning(Log.Tag + " Clouds: the moon's light per pixel does not run: " + problem);
                }
                return material != null;
            }
        }

        // Whether loading the shader was tried, and the frame the light per pixel paused in (-1: not paused).
        private static bool shaderTried;
        private static int missedSince = -1;

        // The moon's light for this frame's clouds over `main`: from the planet's centre
        // towards the moon, its light as the clouds take it, and the share of it added per pixel.
        internal static void Light(CelestialBody main, Vector3 towards, Color light, float share)
        {
            body = main;
            towardsMoon = towards;
            moonLight = light;
            strength = share;
            requested = Time.frameCount;
        }

        // For the probe's log.
        internal static string Describe()
        {
            return !installed ? "not installed" : failed ? "failed" : material == null ? "no shader"
                 : Time.frameCount - requested > 1 ? "not asked for" : "the moon's light per pixel at "
                   + strength.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ", gains "
                   + DirectGain.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " and "
                   + SkyGain.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + (missed > 0 ? ", " + missed + " frames missed" : "");
        }

        // The number after -redefinitionCloudRelightDebug on the command line; 0 without it.
        private static float DebugArgument()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-redefinitionCloudRelightDebug");
            float mode;
            return at >= 0 && at + 1 < args.Length && float.TryParse(args[at + 1], System.Globalization.NumberStyles.Float,
                                                                    System.Globalization.CultureInfo.InvariantCulture, out mode)
                ? mode : 0f;
        }

        // From the addon's Awake, before any clouds render.
        public static void InstallHook()
        {
            Type renderer = TypeLookup.Find(RendererType);
            Type volume = TypeLookup.Find(VolumeType);
            if (renderer == null || volume == null) return;
            if (!ForeignAssumptions.EveCloudRelight.Holds || !ForeignAssumptions.ScattererCloudAtmosphere.Holds) return;
            MethodInfo onPreRender = renderer.GetMethod("OnPreRender", TypeLookup.Any, null, Type.EmptyTypes, null);
            MethodInfo onPostRender = renderer.GetMethod("OnPostRender", TypeLookup.Any, null, Type.EmptyTypes, null);
            readCamera = CompiledGetter.Of<Camera>(renderer.GetField("targetCamera", TypeLookup.Any));
            readRendering = CompiledGetter.Of<bool>(renderer.GetField("renderingEnabled", TypeLookup.Any));
            readProbe = CompiledGetter.Of<bool>(renderer.GetField("reflectionProbeCamera", TypeLookup.Any));
            readFlip = CompiledGetter.Of<bool>(renderer.GetField("useFlipUpscalingBuffer", TypeLookup.Any));
            readVolumes = CompiledGetter.Of<IList>(renderer.GetField("volumesAdded", TypeLookup.Any));
            readColour = History(renderer.GetField("historyRT", TypeLookup.Any));
            readDistance = History(renderer.GetField("historyDistanceRT", TypeLookup.Any));
            readMaterial = CompiledGetter.Of<Material>(volume.GetProperty("RaymarchedCloudMaterial", TypeLookup.Any));
            readInner = CompiledGetter.Of<float>(volume.GetProperty("InnerSphereRadius", TypeLookup.Any));
            readOuter = CompiledGetter.Of<float>(volume.GetProperty("OuterSphereRadius", TypeLookup.Any));
            if (onPreRender == null || onPostRender == null || readCamera == null || readRendering == null || readProbe == null
                || readFlip == null || readVolumes == null || readColour == null || readDistance == null || readMaterial == null
                || readInner == null || readOuter == null)
            {
                CompatibilityLog.Warn("eve-cloud-relight", "EVE's clouds take the moon's light only once the sun has set on all of"
                                                           + " them: this EVE has not the members read.");
                return;
            }
            try
            {
                HarmonyHooks.Install(HarmonyId, typeof(EveCloudRelight), new List<HarmonyHook>
                {
                    new HarmonyHook(onPreRender, null, nameof(AfterPreRender), null),
                    new HarmonyHook(onPostRender, nameof(BeforePostRender), null, null),
                });
                Camera.onPreRender += Unprepared;
                installed = true;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("eve-cloud-relight", "EVE's clouds could not be hooked for the moon's light per pixel ("
                                                           + CompatibilityLog.Reason(e) + ").");
            }
        }

        // A getter of one of the renderer's histories at [flip, left eye or mono, face 0].
        private static Func<object, bool, RenderTexture> History(FieldInfo field)
        {
            if (field == null) return null;
            MethodInfo item = field.FieldType.GetMethod("get_Item", new[] { typeof(bool), typeof(bool), typeof(int) });
            if (item == null || item.ReturnType != typeof(RenderTexture)) return null;
            ParameterExpression target = Expression.Parameter(typeof(object), "target");
            ParameterExpression flip = Expression.Parameter(typeof(bool), "flip");
            Expression history = Expression.Field(Expression.Convert(target, field.DeclaringType), field);
            Expression call = Expression.Call(history, item, flip, Expression.Constant(false), Expression.Constant(0));
            return Expression.Lambda<Func<object, bool, RenderTexture>>(call, target, flip).Compile();
        }

        // Runs inside EVE, after it added its command buffer for the camera: nothing may escape.
        private static void AfterPreRender(object __instance)
        {
            if (failed) return;
            try
            {
                Prepare(__instance);
            }
            catch (Exception e)
            {
                failed = true;
                if (warned) return;
                warned = true;
                CompatibilityLog.Warn("eve-cloud-relight", "EVE's clouds could not take the moon's light per pixel ("
                                                           + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Runs for every camera before it renders. Where this frame's light was not prepared for it, our
        // command buffer comes off, so that it never binds a copy of an older frame. That happens where
        // EVE renders no clouds there this frame, or where its renderer was turned off before its
        // OnPostRender took ours off. Whether Unity calls this before or after EVE's OnPreRender, a command
        // buffer prepared this frame stays and one from before goes.
        private static void Unprepared(Camera camera)
        {
            if (camera == null || cameras.Count == 0) return;
            PerCamera state;
            if (!cameras.TryGetValue(camera.GetInstanceID(), out state) || !state.Attached || state.Frame == Time.frameCount) return;
            camera.RemoveCommandBuffer(Event, state.Command);
            state.Attached = false;
        }

        // Before EVE takes its command buffer off the camera: ours off too.
        private static void BeforePostRender(object __instance)
        {
            try
            {
                Camera camera = readCamera(__instance);
                PerCamera state;
                if (camera == null || !cameras.TryGetValue(camera.GetInstanceID(), out state) || !state.Attached) return;
                camera.RemoveCommandBuffer(Event, state.Command);
                state.Attached = false;
            }
            catch (Exception)
            {
                // The camera is gone; its state is swept with the others.
            }
        }

        // Runs for one camera of EVE's renderer, after it set up its clouds. Where the light was asked for this frame
        // and all inputs are there, it sizes the copy to EVE's buffer. It fills the command buffer with the moon's light,
        // the atmosphere and the rays' cloud map, and attaches it once. Otherwise the frame counts as missed.
        private static void Prepare(object renderer)
        {
            if (HeldOff || Time.frameCount - requested > 1 || (strength <= 0f && !ProbeAlways) || body == null) return;
            if (!readRendering(renderer) || readProbe(renderer)) return;
            Camera camera = readCamera(renderer);
            if (camera == null || camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Mono) return;
            IList volumes = readVolumes(renderer);
            if (volumes == null || volumes.Count == 0) return;
            // The first volume that is weather: an aurora shines by itself and is not lit (CloudLayerKinds). It is not the layer
            // the maps come from (RtEveClouds): with SPVE that is a thin cirrus first, and its material gives next to no light,
            // measured 2026-10-28 (the moon added 0.0001 to the clouds against 0.0022 with this one).
            Material raymarched = null;
            for (int i = 0; i < volumes.Count && raymarched == null; i++)
            {
                if (volumes[i] == null) continue;
                Material candidate = readMaterial(volumes[i]);
                if (candidate != null && !CloudLayerKinds.IsEmissive(null, candidate)) raymarched = candidate;
            }
            if (raymarched == null) return;
            RtEveClouds clouds = RtEveClouds.Last;
            Texture coverage = null, kind = null, curve = null;
            Vector4 mask1 = Vector4.zero, mask2 = Vector4.zero;
            Vector2 uses = Vector2.zero;
            bool maps = clouds != null && clouds.Maps(out coverage, out kind, out curve, out mask1, out mask2, out uses);
            RenderTexture source = readColour(renderer, readFlip(renderer));
            RenderTexture distance = readDistance(renderer, readFlip(renderer));
            if (raymarched == null || !maps || source == null || distance == null || !source.IsCreated())
            {
                Miss(raymarched == null ? "no raymarching material" : !maps
                         ? (clouds == null ? "no cloud layer in the rays" : "the layer's maps not there, the layer filled in frame " + clouds.FilledFrame)
                         : source == null || !source.IsCreated() ? "no reconstructed clouds" : "no distances");
                return;
            }

            // The clouds' planet: where EVE centres the volume, which must be the body asked for.
            Vector3 centre = raymarched.GetVector(SphereCenterId);
            if (((Vector3d)centre - body.position).sqrMagnitude > 1.0e6)
            {
                Miss("the clouds centred at " + centre + ", " + body.bodyName + " at " + (Vector3)body.position);
                return;
            }
            float inner = float.MaxValue, outer = 0f;
            for (int i = 0; i < volumes.Count; i++)
            {
                if (volumes[i] == null || CloudLayerKinds.IsEmissive(null, readMaterial(volumes[i]))) continue;
                inner = Mathf.Min(inner, readInner(volumes[i]));
                outer = Mathf.Max(outer, readOuter(volumes[i]));
            }
            Texture atlas = Atlas;
            float rg = raymarched.GetFloat(RgId), rt = raymarched.GetFloat(RtId);
            if (atlas == null || rg <= 0f || rt <= rg || outer <= inner)
            {
                Miss((atlas == null ? "no atmosphere atlas" : "no atmosphere") + " (Rg " + rg + ", Rt " + rt + ", the layers " + inner
                     + " to " + outer + ")");
                return;
            }

            PerCamera state = For(camera);
            if (state.Target == null || state.Target.width != source.width || state.Target.height != source.height
                || state.Target.format != source.format)
            {
                if (state.Target != null)
                {
                    state.Target.Release();
                    UnityEngine.Object.Destroy(state.Target);
                }
                state.Target = new RenderTexture(source.width, source.height, 0, source.format, RenderTextureReadWrite.Linear)
                {
                    name = "ReDefinition clouds in the moon's light",
                    filterMode = source.filterMode,
                    wrapMode = TextureWrapMode.Clamp,
                };
                state.Target.Create();
            }
            state.Source = source;

            Color cloudColour = raymarched.GetColor(CloudColourId);
            float skyScale = raymarched.GetFloat(SkylightId) * raymarched.GetFloat(ExposureId);
            Color light = moonLight * cloudColour * (DirectGain * strength);
            Color sky = moonLight * moonLight * cloudColour * (SkyGain * strength * skyScale);
            LastMoonLight = moonLight;
            LastCloudColour = cloudColour;
            LastSkyScale = skyScale;
            Vector3 eye = (Vector3)((Vector3d)camera.transform.position - body.position);
            Matrix4x4 fromCentre = clouds.MapFromCentre;
            Vector3 fades = clouds.MapFades;
            Vector2 dimensions = raymarched.GetVector(AtlasDimensionsId);
            int types = Mathf.Clamp(Mathf.RoundToInt(raymarched.GetFloat(TypeCountSourceId)), 1, MostTypes);
            raymarched.GetVectorArray(TypePropertiesSourceId, typeProperties);
            raymarched.GetFloatArray(TypeBrightnessSourceId, typeBrightnessList);
            for (int i = 0; i < MostTypes; i++)
            {
                typeDensity[i] = i < typeProperties.Count ? typeProperties[i].x : 0f;
                typeBrightness[i] = i < typeBrightnessList.Count ? typeBrightnessList[i] : 1f;
            }

            CommandBuffer command = state.Command;
            command.Clear();
            Probe.GpuProfiler.Mark(command, "ReDefinition clouds: the moon's light per pixel");
            command.SetGlobalTexture(DistanceId, distance);
            command.SetGlobalMatrix(InvProjectionId, camera.projectionMatrix.inverse);
            command.SetGlobalMatrix(CameraToWorldId, camera.cameraToWorldMatrix);
            command.SetGlobalVector(CameraId, new Vector4(eye.x, eye.y, eye.z, rg));
            command.SetGlobalVector(MoonId, new Vector4(towardsMoon.x, towardsMoon.y, towardsMoon.z, rt));
            command.SetGlobalVector(MoonColourId, new Vector4(light.r, light.g, light.b, 0f));
            command.SetGlobalVector(SkyColourId, new Vector4(sky.r, sky.g, sky.b, raymarched.GetFloat(SkylightTintId)));
            command.SetGlobalVector(IrradianceRectId, raymarched.GetVector(IrradianceSourceId));
            command.SetGlobalVector(SkySizeId, new Vector4(0f, 0f, raymarched.GetFloat(SkyWId), raymarched.GetFloat(SkyHId)));
            command.SetGlobalVector(HeightsId, new Vector4(raymarched.GetFloat(HrId), raymarched.GetFloat(HmId), inner, outer));
            command.SetGlobalVector(BetaRId, raymarched.GetVector(BetaRSourceId));
            command.SetGlobalVector(BetaMExId, raymarched.GetVector(BetaMExSourceId));
            command.SetGlobalVector(DirectPhaseId, raymarched.GetVector(DirectSourceId));
            command.SetGlobalVector(MultiplePhaseId, raymarched.GetVector(MultipleSourceId));
            command.SetGlobalTexture(AtlasId, atlas);
            command.SetGlobalVector(TransmittanceRectId, raymarched.GetVector(TransmittanceSourceId));
            command.SetGlobalVector(AtlasSizesId, new Vector4(dimensions.x, dimensions.y, raymarched.GetFloat(TransmittanceWId),
                                                              raymarched.GetFloat(TransmittanceHId)));
            command.SetGlobalTexture(CoverageId, coverage);
            command.SetGlobalTexture(TypeId, kind);
            command.SetGlobalTexture(CurveId, curve);
            command.SetGlobalFloat(UseMask1Id, uses.x);
            command.SetGlobalFloat(UseMask2Id, uses.y);
            command.SetGlobalVector(Mask1Id, mask1);
            command.SetGlobalVector(Mask2Id, mask2);
            command.SetGlobalFloatArray(TypeDensityId, typeDensity);
            command.SetGlobalFloatArray(TypeBrightnessId, typeBrightness);
            command.SetGlobalFloat(TypeCountId, types);
            command.SetGlobalVector(MapRow0Id, fromCentre.GetRow(0));
            command.SetGlobalVector(MapRow1Id, fromCentre.GetRow(1));
            command.SetGlobalVector(MapRow2Id, fromCentre.GetRow(2));
            command.SetGlobalVector(MapFadesId, new Vector4(fades.x, fades.y, fades.z, TauScale));
            command.SetGlobalFloat(DebugId, DebugMode);
            command.Blit(source, state.Target, material);
            command.SetGlobalTexture(ColourBufferId, state.Target);
            command.SetGlobalTexture(ReconstructedId, state.Target);
            state.Frame = Time.frameCount;
            if (!state.Attached)
            {
                camera.AddCommandBuffer(Event, command);
                state.Attached = true;
            }
            if (!state.Logged)
            {
                state.Logged = true;
                Debug.Log(Log.Tag + " Clouds: the moon's light per pixel on " + camera.name + ", " + source.width + " by " + source.height
                          + ", the layers " + inner.ToString("0") + " to " + outer.ToString("0") + " m from the centre.");
            }
            missed = 0;
        }

        // A frame in which the light could not be added, and why: each reason logged the
        // first time it comes.
        private static readonly HashSet<string> missesLogged = new HashSet<string>();

        // Counts a missed frame towards the pause in Ready and logs its reason once by its first 24 characters.
        private static void Miss(string why)
        {
            missed++;
            string kind = why.Length > 24 ? why.Substring(0, 24) : why;
            if (missesLogged.Add(kind)) Debug.Log(Log.Tag + " Clouds: the moon's light per pixel missed a frame: " + why + ".");
        }

        // The camera's state, made with its command buffer the first time; a new camera sweeps the gone ones first.
        private static PerCamera For(Camera camera)
        {
            PerCamera state;
            if (cameras.TryGetValue(camera.GetInstanceID(), out state)) return state;
            Sweep();
            state = new PerCamera { Camera = camera, Command = new CommandBuffer { name = "ReDefinition clouds in the moon's light" } };
            cameras[camera.GetInstanceID()] = state;
            return state;
        }

        // The cameras gone since, with their targets.
        private static readonly List<int> gone = new List<int>();

        // Drops the cameras that are gone, releasing their command buffers and copies.
        private static void Sweep()
        {
            gone.Clear();
            foreach (KeyValuePair<int, PerCamera> entry in cameras)
                if (entry.Value.Camera == null)
                    gone.Add(entry.Key);
            foreach (int id in gone)
            {
                PerCamera state = cameras[id];
                state.Command.Release();
                if (state.Target != null)
                {
                    state.Target.Release();
                    UnityEngine.Object.Destroy(state.Target);
                }
                cameras.Remove(id);
            }
        }

        // For the probe's calibration. It gives the mean light over the frame of EVE's clouds as EVE
        // reconstructed them and of the copy (with DebugMode 2 or 3 the one light alone). It reads the
        // camera's last frame with this pass, and returns false where there was none this frame or the one
        // before.
        internal static bool MeanColour(Camera camera, out Color eve, out Color relit)
        {
            eve = relit = Color.black;
            PerCamera state;
            if (camera == null || !cameras.TryGetValue(camera.GetInstanceID(), out state) || Time.frameCount - state.Frame > 1
                || state.Source == null || state.Target == null)
                return false;
            eve = Mean(state.Source);
            relit = Mean(state.Target);
            return true;
        }

        // The mean red, green and blue of a render texture, read back on the CPU: for the probe only, as it stalls.
        private static Color Mean(RenderTexture texture)
        {
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = texture;
            var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBAHalf, false, true);
            read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
            RenderTexture.active = before;
            Color[] pixels = read.GetPixels();
            UnityEngine.Object.Destroy(read);
            double r = 0.0, g = 0.0, b = 0.0;
            foreach (Color c in pixels)
            {
                r += c.r;
                g += c.g;
                b += c.b;
            }
            int n = Math.Max(pixels.Length, 1);
            return new Color((float)(r / n), (float)(g / n), (float)(b / n), 1f);
        }
    }
}
