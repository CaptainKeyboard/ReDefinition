using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Text;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // TUFX's post-processing, split around the upscaler.
    //
    // TUFX gives its profile to the camera it calls final -- Camera 00 in flight,
    // InternalCamera in IVA, the scaled camera in the map view, Main Camera in the
    // editors -- as a PostProcessLayer whose volumeLayer takes the profile's volume
    // (TexturesUnlimitedFXLoader.ApplyProfileToCamera). The rig redirects those
    // cameras into its render-size texture, so every effect of the profile would
    // run before the upscaler. AMD sorts them (super-resolution-upscaler.md, "Post processing A"
    // and "B"): screen-space reflections, ambient occlusion and exposure before the
    // upscaler; film grain, chromatic aberration, vignette, tonemapping, bloom,
    // depth of field and motion blur after it -- "Doing so before an upscaler might
    // cause the upscaler to amplify the noise".
    //
    // So each redirected layer keeps what belongs before the upscaler -- ambient
    // occlusion, screen-space reflections, fog, auto exposure and the effects other
    // mods inject before the built-in stack -- and a layer of ReDefinition's, on a
    // camera that never renders, draws the rest over the upscaler's output from the
    // presenter, blended from the same volumes. Auto exposure stays in front: the
    // uber pass multiplies it in before bloom and colour grading
    // (Builtins/Uber.shader), and the layer after the upscaler then reads the image
    // as exposed, as its bloom and grading would have.
    //
    // Which layer draws which effect is decided right after PostProcessManager
    // blends a layer's settings (UpdateSettings, a Harmony postfix): the other
    // half's effects are switched off for that frame, and the next blend, which
    // starts from the defaults again, brings them back -- the moment the split ends,
    // TUFX renders as without it. A redirected layer counts as the profile's by its
    // volumeLayer at that moment, so a camera change that moves the profile within a
    // frame is followed in that frame; and ReDefinition's layer draws only in a
    // frame in which one was split.
    //
    // Only while the image FSR receives is HDR: in an 8-bit render target the
    // highlights bloom and tonemapping need are clamped before FSR.
    //
    // Depth of field and motion blur read _CameraDepthTexture and
    // _CameraMotionVectorsTexture and linearise depth by _ZBufferParams
    // (PostProcessing v2, Builtins/DepthOfField.hlsl, Builtins/MotionBlur.shader).
    // After FSR those are the presenter's, so the rig's copies and the scene
    // camera's planes are bound for them -- where the profile's layer is the scene
    // camera's own. Where it is another's, InternalCamera in IVA or the scaled
    // camera in the map view, the rig has no depth or motion of that camera, and
    // the two stay in front of FSR with the camera's own. The motion vectors are
    // bound at the display size, since motion blur turns them into pixels by their
    // texture's size (_CameraMotionVectorsTexture_TexelSize.zw) and caps the blur by
    // the output's. The globals stay bound afterwards, to the rig's copies, for any
    // later camera that samples them without asking for depth of its own.
    //
    // Dithering stays out of the image FSR receives: PostProcessing v2 dithers in
    // the uber pass of every layer's last stack -- the profile's and the ones TUFX
    // adds to the galaxy and scaled cameras for HDR -- and noise added at render
    // size is noise FSR accumulates. Every layer on a redirected camera dithers with
    // a texture of alpha 0.5, which is no noise (Builtins/Dithering.hlsl);
    // ReDefinition's layer dithers the final image.
    //
    // All of it through reflection: TUFX stays optional.
    internal sealed class TufxPostProcessing
    {
        private const string LoaderTypeName = "TUFX.TexturesUnlimitedFXLoader";
        private const string PostProcessing = "UnityEngine.Rendering.PostProcessing.";
        private const string HarmonyId = "ReDefinition.TufxPostProcessing";
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // PostProcessing v2's own effects that AMD puts after the upscaler. Lens
        // distortion bends the finished image, which FSR's motion vectors would not
        // follow.
        private static readonly string[] BuiltinAfterUpscaling =
        {
            "DepthOfField", "MotionBlur", "LensDistortion", "ChromaticAberration",
            "Bloom", "Vignette", "Grain", "ColorGrading",
        };

        private static readonly object False = false;
        private static readonly object True = true;

        private static bool resolved;
        private static string unavailable;
        private static string broken;
        private static string brokenStatus;

        private static Type layerType;
        private static Type contextType;
        private static Type depthOfFieldType;
        private static Type motionBlurType;
        private static PropertyInfo loaderResources;
        private static PropertyInfo managerInstance;
        private static FieldInfo managerSettingsTypes;
        private static FieldInfo attributeBuiltin;
        private static FieldInfo attributeEvent;
        private static PropertyInfo lerperInstance;
        private static MethodInfo lerperBeginFrame;
        private static MethodInfo lerperEndFrame;
        private static MethodInfo layerInit;
        private static MethodInfo layerGetBundle;
        private static MethodInfo layerSetupContext;
        private static MethodInfo layerUpdateVolumeSystem;
        private static MethodInfo layerRender;
        private static FieldInfo layerVolumeLayer;
        private static FieldInfo layerVolumeTrigger;
        private static FieldInfo layerStopNaN;
        private static FieldInfo layerBreakBeforeGrading;
        private static FieldInfo layerAntialiasing;
        private static FieldInfo layerSettingsUpdateNeeded;
        private static PropertyInfo bundleSettings;
        private static FieldInfo settingsEnabled;
        private static FieldInfo parameterValue;
        private static MethodInfo settingsIsEnabled;
        private static MethodInfo contextReset;
        // The context's camera read by the dithering hook; its setters called with
        // an argument array kept here, where PropertyInfo.SetValue makes one per call.
        private static PropertyInfo contextCamera;
        private static MethodInfo setContextCamera;
        private static MethodInfo setContextCommand;
        private static MethodInfo setContextSource;
        private static MethodInfo setContextDestination;
        private static MethodInfo setContextSourceFormat;
        private static MethodInfo setContextFlip;
        private static FieldInfo contextUberSheet;
        private static PropertyInfo sheetProperties;
        private static object antialiasingNone;

        private static Type[] afterTypes = new Type[0];
        private static Type[] beforeTypes = new Type[0];
        private static int classified = -1;
        private static Texture2D neutralDither;
        private static readonly object[] bundleArguments = new object[1];

        // What the hooks ask about, one rig at a time: the redirected cameras, the
        // one whose depth and motion the rig copies, and ReDefinition's layer. The frame in
        // which a redirected layer carrying the profile was last split, and whether
        // each one so far was the scene camera's.
        private static readonly HashSet<Camera> redirectedCameras = new HashSet<Camera>();
        private static Camera sceneCamera;
        private static Component ownLayer;
        private static int splitFrame = -1;
        private static bool splitOnSceneCamera;

        private GameObject host;
        private Camera ownCamera;
        private Component layer;
        private object context;
        private CommandBuffer buffer;
        private RenderTexture output;
        private RenderTexture motionAtDisplaySize;
        private Behaviour copiedFrom;
        private int copiedMask = -1;
        private readonly object[] one = new object[1];
        private readonly object[] two = new object[2];
        // The context's setters' own, so a setter never replaces what `one` holds
        // for the call after it.
        private readonly object[] setterArgument = new object[1];
        // What the setters are handed, boxed once per format and texture rather
        // than every frame.
        private RenderTextureFormat boxedFormatValue;
        private object boxedFormat;
        private Texture boxedSourceOf;
        private object boxedSource;
        private Texture boxedOutputOf;
        private object boxedOutput;
        private string status = "off";

        // Kept apart, so that nothing outside the game touches Shader.
        private static class Ids
        {
            internal static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            internal static readonly int CameraMotionVectorsTexture = Shader.PropertyToID("_CameraMotionVectorsTexture");
            internal static readonly int ZBufferParams = Shader.PropertyToID("_ZBufferParams");
            internal static readonly int DitheringTex = Shader.PropertyToID("_DitheringTex");
            internal static readonly int DitheringCoords = Shader.PropertyToID("_Dithering_Coords");
        }

        // Whether an effect of PostProcessing v2 is drawn after FSR: its own named in
        // BuiltinAfterUpscaling, and another mod's where it injects itself after the
        // built-in stack. Everything else stays before.
        internal static bool AfterUpscaling(string effect, bool builtin, string injectionPoint)
        {
            return builtin ? Array.IndexOf(BuiltinAfterUpscaling, effect) >= 0 : injectionPoint == "AfterStack";
        }

        // From the rig's Update, before the frame's cameras render: whether the split
        // can run, and ReDefinition's layer on the profile's volumes.
        internal void Refresh(List<CameraRedirect> redirects, Camera scene, bool wanted, bool hdrInput, Vector2Int displaySize)
        {
            if (!wanted)
            {
                Stop("switched off");
                return;
            }
            if (!hdrInput)
            {
                Stop("the image FSR receives is not HDR, so bloom and tonemapping stay in front of its 8-bit clamp");
                return;
            }
            if (!Resolve())
            {
                Stop(unavailable);
                return;
            }
            if (broken != null)
            {
                Stop(brokenStatus);
                return;
            }

            redirectedCameras.Clear();
            int mask = 0;
            Behaviour first = null;
            foreach (CameraRedirect redirect in redirects)
            {
                Camera camera = redirect != null ? redirect.Camera : null;
                if (camera == null) continue;
                redirectedCameras.Add(camera);
                Behaviour candidate = camera.GetComponent(layerType) as Behaviour;
                if (candidate == null || !candidate.isActiveAndEnabled) continue;
                // TUFX gives its profile to the final camera only (volumeLayer 1); the
                // other cameras' layers carry antialiasing alone, which HostStack
                // switches off.
                int volumes = ((LayerMask)layerVolumeLayer.GetValue(candidate)).value;
                if (volumes == 0) continue;
                mask |= volumes;
                if (first == null) first = candidate;
            }
            if (first == null)
            {
                Stop("no redirected camera carries TUFX's profile");
                return;
            }

            try
            {
                if (!Build(displaySize)) return;
                if (mask != copiedMask || first != copiedFrom)
                {
                    layerVolumeLayer.SetValue(layer, (LayerMask)mask);
                    layerVolumeTrigger.SetValue(layer, layerVolumeTrigger.GetValue(first));
                    layerBreakBeforeGrading.SetValue(layer, layerBreakBeforeGrading.GetValue(first));
                    copiedMask = mask;
                    copiedFrom = first;
                }
                Classify();
            }
            catch (Exception e)
            {
                Break(e);
                Stop(brokenStatus);
                return;
            }

            sceneCamera = scene;
            ownLayer = layer;
            status = null;
        }

        // Draws TUFX's effects after the upscaler from source into destination. False
        // where nothing was drawn: the caller copies the image.
        internal bool Render(Texture source, RenderTexture destination, RenderTexture depth, RenderTexture motion)
        {
            if (broken != null || layer == null || !ReferenceEquals(ownLayer, layer)) return false;
            // No redirected layer was split this frame: it drew everything itself.
            if (splitFrame != Time.frameCount) return false;
            try
            {
                buffer.Clear();
                if (sceneCamera != null) ownCamera.fieldOfView = sceneCamera.fieldOfView;

                // As PostProcessLayer.BuildCommandBuffers sets up its own context and
                // blends its settings.
                contextReset.Invoke(context, null);
                SetOnContext(setContextCamera, ownCamera);
                RenderTexture sourceTexture = source as RenderTexture;
                SetOnContext(setContextSourceFormat,
                             BoxedFormat(sourceTexture != null ? sourceTexture.format : RenderTextureFormat.DefaultHDR));
                one[0] = context;
                layerSetupContext.Invoke(layer, one);
                SetOnContext(setContextCommand, buffer);
                object lerper = lerperInstance.GetValue(null, null);
                lerperBeginFrame.Invoke(lerper, one);
                two[0] = ownCamera;
                two[1] = buffer;
                layerUpdateVolumeSystem.Invoke(layer, two);

                if (!AnyAfterEffectOn())
                {
                    // As Render ends a frame, so the next one blends again.
                    lerperEndFrame.Invoke(lerper, null);
                    layerSettingsUpdateNeeded.SetValue(layer, True);
                    return false;
                }

                bool sceneBuffers = splitOnSceneCamera && sceneCamera != null;
                bool depthOfField = sceneBuffers && IsOn(depthOfFieldType);
                bool motionBlur = sceneBuffers && IsOn(motionBlurType);
                if (depthOfField || motionBlur)
                {
                    buffer.SetGlobalTexture(Ids.CameraDepthTexture, depth);
                    buffer.SetGlobalVector(Ids.ZBufferParams, ZBufferParamsOf(sceneCamera));
                }
                if (motionBlur && EnsureMotion(motion))
                {
                    buffer.Blit(motion, motionAtDisplaySize);
                    buffer.SetGlobalTexture(Ids.CameraMotionVectorsTexture, motionAtDisplaySize);
                }

                SetOnContext(setContextSource, BoxedTarget(source, ref boxedSourceOf, ref boxedSource));
                SetOnContext(setContextDestination, BoxedTarget(output, ref boxedOutputOf, ref boxedOutput));
                SetOnContext(setContextFlip, False);
                one[0] = context;
                layerRender.Invoke(layer, one);

                Graphics.ExecuteCommandBuffer(buffer);
                Graphics.Blit(output, destination);
                return true;
            }
            catch (Exception e)
            {
                Break(e);
                Stop(brokenStatus);
                return false;
            }
        }

        private void SetOnContext(MethodInfo setter, object value)
        {
            setterArgument[0] = value;
            setter.Invoke(context, setterArgument);
        }

        private object BoxedFormat(RenderTextureFormat format)
        {
            if (boxedFormat == null || format != boxedFormatValue)
            {
                boxedFormat = format;
                boxedFormatValue = format;
            }
            return boxedFormat;
        }

        private static object BoxedTarget(Texture texture, ref Texture of, ref object boxed)
        {
            if (boxed == null || !ReferenceEquals(texture, of))
            {
                boxed = new RenderTargetIdentifier(texture);
                of = texture;
            }
            return boxed;
        }

        internal string Describe()
        {
            if (status != null) return status;
            StringBuilder sb = new StringBuilder("on");
            if (splitFrame >= 0)
                sb.Append(splitOnSceneCamera ? ", profile on the scene camera" : ", profile on another camera: depth of field and motion blur before FSR");
            sb.Append("; after FSR: ").Append(Names(afterTypes)).Append("; before it: ").Append(Names(beforeTypes));
            return sb.ToString();
        }

        // A new rig tries again after a failure.
        internal void Dispose()
        {
            Stop("off");
            if (buffer != null)
            {
                buffer.Release();
                buffer = null;
            }
            broken = null;
            brokenStatus = null;
        }

        // The layers go back to drawing everything at TUFX's next blend, and what
        // ReDefinition's layer holds is let go.
        private void Stop(string reason)
        {
            status = reason;
            if (ReferenceEquals(ownLayer, layer) || ownLayer == null)
            {
                ownLayer = null;
                sceneCamera = null;
                redirectedCameras.Clear();
            }
            ReleaseTextures();
            DestroyHost();
        }

        private bool Build(Vector2Int size)
        {
            if (layer == null)
            {
                object resources = loaderResources.GetValue(null, null);
                if (resources == null)
                {
                    Stop("TUFX has not loaded its resources yet");
                    return false;
                }
                DestroyHost();
                host = new GameObject("ReDefinitionTufxAfterFsr");
                // Inactive while it is put together: the layer sets itself up as it is
                // enabled, on its camera, with its resources.
                host.SetActive(false);
                ownCamera = host.AddComponent<Camera>();
                ownCamera.enabled = false;
                ownCamera.cullingMask = 0;
                ownCamera.clearFlags = CameraClearFlags.Nothing;
                ownCamera.allowHDR = true;
                ownCamera.allowMSAA = false;
                ownCamera.useOcclusionCulling = false;
                ownCamera.depthTextureMode = DepthTextureMode.None;
                layer = host.AddComponent(layerType);
                one[0] = resources;
                layerInit.Invoke(layer, one);
                layerStopNaN.SetValue(layer, True);
                layerAntialiasing.SetValue(layer, antialiasingNone);
                host.SetActive(true);
                context = Activator.CreateInstance(contextType);
                copiedMask = -1;
                copiedFrom = null;
                if (buffer == null) buffer = new CommandBuffer { name = "ReDefinition.TufxAfterFsr" };
            }
            if (output == null || output.width != size.x || output.height != size.y)
            {
                ReleaseTextures();
                output = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default)
                {
                    name = "ReDefinition_TufxAfterFsr",
                };
                if (!output.Create())
                {
                    UpscalerRig.Release(ref output);
                    Stop("its output texture could not be created");
                    return false;
                }
            }
            // The context takes its size from the camera.
            if (ownCamera.targetTexture != output) ownCamera.targetTexture = output;
            return true;
        }

        private bool AnyAfterEffectOn()
        {
            foreach (Type type in afterTypes)
            {
                if (!splitOnSceneCamera && (type == depthOfFieldType || type == motionBlurType)) continue;
                if (IsOn(type)) return true;
            }
            return false;
        }

        private bool IsOn(Type type)
        {
            if (type == null) return false;
            object bundle = Bundle(layer, type);
            if (bundle == null) return false;
            object settings = bundleSettings.GetValue(bundle, null);
            one[0] = context;
            return (bool)settingsIsEnabled.Invoke(settings, one);
        }

        private bool EnsureMotion(RenderTexture motion)
        {
            if (motion == null || output == null) return false;
            if (motionAtDisplaySize != null && motionAtDisplaySize.width == output.width
                && motionAtDisplaySize.height == output.height) return true;
            UpscalerRig.Release(ref motionAtDisplaySize);
            motionAtDisplaySize = new RenderTexture(output.width, output.height, 0,
                RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear)
            {
                name = "ReDefinition_TufxMotionVectors",
                filterMode = FilterMode.Point,
            };
            if (motionAtDisplaySize.Create()) return true;
            UpscalerRig.Release(ref motionAtDisplaySize);
            return false;
        }

        private void ReleaseTextures()
        {
            if (ownCamera != null) ownCamera.targetTexture = null;
            UpscalerRig.Release(ref output);
            UpscalerRig.Release(ref motionAtDisplaySize);
            boxedSourceOf = null;
            boxedSource = null;
            boxedOutputOf = null;
            boxedOutput = null;
        }

        private void DestroyHost()
        {
            if (host != null) UnityEngine.Object.Destroy(host);
            host = null;
            ownCamera = null;
            layer = null;
            context = null;
            copiedMask = -1;
            copiedFrom = null;
        }

        // As UnityShaderVariables.cginc defines it, for a reversed depth buffer --
        // Direct3D 11's -- and for the other.
        private static Vector4 ZBufferParamsOf(Camera camera)
        {
            float near = camera.nearClipPlane;
            float far = camera.farClipPlane;
            if (SystemInfo.usesReversedZBuffer)
            {
                float x = -1f + far / near;
                return new Vector4(x, 1f, x / far, 1f / far);
            }
            float y = far / near;
            return new Vector4(1f - y, y, (1f - y) / far, y / far);
        }

        private static bool Resolve()
        {
            if (resolved) return unavailable == null;
            resolved = true;
            Type loader = TypeLookup.Find(LoaderTypeName);
            if (loader == null)
            {
                unavailable = "TUFX is not installed";
                return false;
            }
            try
            {
                Assembly assembly = loader.Assembly;
                layerType = TypeIn(assembly, "PostProcessLayer");
                contextType = TypeIn(assembly, "PostProcessRenderContext");
                Type manager = TypeIn(assembly, "PostProcessManager");
                Type attribute = TypeIn(assembly, "PostProcessAttribute");
                Type bundle = TypeIn(assembly, "PostProcessBundle");
                Type settings = TypeIn(assembly, "PostProcessEffectSettings");
                Type resources = TypeIn(assembly, "PostProcessResources");
                Type sheet = TypeIn(assembly, "PropertySheet");
                Type dithering = TypeIn(assembly, "Dithering");
                Type lerper = TypeIn(assembly, "TextureLerper");
                depthOfFieldType = TypeIn(assembly, "DepthOfField");
                motionBlurType = TypeIn(assembly, "MotionBlur");

                loaderResources = PropertyOf(loader, "Resources", Statics);
                managerInstance = PropertyOf(manager, "instance", Statics);
                managerSettingsTypes = FieldOf(manager, "settingsTypes");
                attributeBuiltin = FieldOf(attribute, "builtinEffect");
                attributeEvent = FieldOf(attribute, "eventType");
                lerperInstance = PropertyOf(lerper, "instance", Statics);
                lerperBeginFrame = MethodOf(lerper, "BeginFrame", contextType);
                lerperEndFrame = MethodOf(lerper, "EndFrame");
                layerInit = MethodOf(layerType, "Init", resources);
                layerGetBundle = MethodOf(layerType, "GetBundle", typeof(Type));
                layerSetupContext = MethodOf(layerType, "SetupContext", contextType);
                layerUpdateVolumeSystem = MethodOf(layerType, "UpdateVolumeSystem", typeof(Camera), typeof(CommandBuffer));
                layerRender = MethodOf(layerType, "Render", contextType);
                layerVolumeLayer = FieldOf(layerType, "volumeLayer");
                layerVolumeTrigger = FieldOf(layerType, "volumeTrigger");
                layerStopNaN = FieldOf(layerType, "stopNaNPropagation");
                layerBreakBeforeGrading = FieldOf(layerType, "breakBeforeColorGrading");
                layerAntialiasing = FieldOf(layerType, "antialiasingMode");
                layerSettingsUpdateNeeded = FieldOf(layerType, "m_SettingsUpdateNeeded");
                bundleSettings = PropertyOf(bundle, "settings", Members);
                settingsEnabled = FieldOf(settings, "enabled");
                parameterValue = FieldOf(settingsEnabled.FieldType, "value");
                settingsIsEnabled = MethodOf(settings, "IsEnabledAndSupported", contextType);
                contextReset = MethodOf(contextType, "Reset");
                contextCamera = PropertyOf(contextType, "camera", Members);
                setContextCamera = SetterOf(contextCamera);
                setContextCommand = SetterOf(PropertyOf(contextType, "command", Members));
                setContextSource = SetterOf(PropertyOf(contextType, "source", Members));
                setContextDestination = SetterOf(PropertyOf(contextType, "destination", Members));
                setContextSourceFormat = SetterOf(PropertyOf(contextType, "sourceFormat", Members));
                setContextFlip = SetterOf(PropertyOf(contextType, "flip", Members));
                contextUberSheet = FieldOf(contextType, "uberSheet");
                sheetProperties = PropertyOf(sheet, "properties", Members);
                antialiasingNone = Enum.ToObject(layerAntialiasing.FieldType, 0);
                Classify();

                HarmonyHooks.Install(HarmonyId, typeof(TufxPostProcessing), new[]
                {
                    new HarmonyHook(MethodOf(manager, "UpdateSettings", layerType, typeof(Camera)),
                        null, nameof(UpdateSettingsPostfix), null),
                    new HarmonyHook(MethodOf(dithering, "Render", contextType), nameof(DitheringPrefix), null, null),
                });
                return true;
            }
            catch (Exception e)
            {
                unavailable = "this build of TUFX is not the shape ReDefinition reads (" + CompatibilityLog.Reason(e) + ")";
                CompatibilityLog.Warn("tufx-post", "TUFX's effects stay before FSR: " + unavailable + ".");
                return false;
            }
        }

        // Anew whenever PostProcessing v2 knows another number of effects: another
        // mod's registered after the first look.
        private static void Classify()
        {
            IDictionary types = managerSettingsTypes.GetValue(managerInstance.GetValue(null, null)) as IDictionary;
            if (types == null || types.Count == classified) return;
            List<Type> after = new List<Type>();
            List<Type> before = new List<Type>();
            foreach (DictionaryEntry entry in types)
            {
                Type type = entry.Key as Type;
                if (type == null || entry.Value == null) continue;
                bool builtin = (bool)attributeBuiltin.GetValue(entry.Value);
                string injectionPoint = attributeEvent.GetValue(entry.Value).ToString();
                (AfterUpscaling(type.Name, builtin, injectionPoint) ? after : before).Add(type);
            }
            afterTypes = after.ToArray();
            beforeTypes = before.ToArray();
            classified = types.Count;
        }

        // PostProcessManager.UpdateSettings, after it blended a layer's settings: the
        // layer, its first argument -- by position, which Harmony hands over without
        // an array of all of them.
        private static void UpdateSettingsPostfix(object __0)
        {
            if (broken != null || ownLayer == null) return;
            Component blended = __0 as Component;
            if (blended == null) return;
            try
            {
                if (ReferenceEquals(blended, ownLayer))
                {
                    SwitchOff(blended, beforeTypes);
                    if (!splitOnSceneCamera)
                    {
                        SwitchOff(blended, depthOfFieldType);
                        SwitchOff(blended, motionBlurType);
                    }
                    return;
                }

                Camera camera = blended.GetComponent<Camera>();
                if (camera == null || !redirectedCameras.Contains(camera)) return;
                if (((LayerMask)layerVolumeLayer.GetValue(blended)).value == 0) return;

                bool onSceneCamera = camera == sceneCamera;
                int frame = Time.frameCount;
                splitOnSceneCamera = splitFrame == frame ? splitOnSceneCamera && onSceneCamera : onSceneCamera;
                splitFrame = frame;
                foreach (Type type in afterTypes)
                {
                    // Their inputs are this camera's own, which only it has.
                    if (!onSceneCamera && (type == depthOfFieldType || type == motionBlurType)) continue;
                    SwitchOff(blended, type);
                }
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        // Dithering.Render, for the uber pass of a layer on a redirected camera: no
        // noise. Its context by position, as above.
        private static bool DitheringPrefix(object __0)
        {
            if (broken != null || ownLayer == null || __0 == null) return true;
            try
            {
                object renderContext = __0;
                Camera camera = renderContext != null ? contextCamera.GetValue(renderContext, null) as Camera : null;
                if (camera == null || !redirectedCameras.Contains(camera)) return true;
                object uber = contextUberSheet.GetValue(renderContext);
                MaterialPropertyBlock properties = uber != null ? sheetProperties.GetValue(uber, null) as MaterialPropertyBlock : null;
                if (properties == null) return true;
                properties.SetTexture(Ids.DitheringTex, NeutralDither());
                properties.SetVector(Ids.DitheringCoords, new Vector4(1f, 1f, 0f, 0f));
                return false;
            }
            catch (Exception e)
            {
                Break(e);
                return true;
            }
        }

        private static void SwitchOff(Component on, Type[] types)
        {
            foreach (Type type in types) SwitchOff(on, type);
        }

        private static void SwitchOff(Component on, Type type)
        {
            object bundle = Bundle(on, type);
            if (bundle == null) return;
            object settings = bundleSettings.GetValue(bundle, null);
            parameterValue.SetValue(settingsEnabled.GetValue(settings), False);
        }

        // A layer built before an effect was registered has none of it.
        private static object Bundle(Component on, Type type)
        {
            if (type == null) return null;
            try
            {
                bundleArguments[0] = type;
                return layerGetBundle.Invoke(on, bundleArguments);
            }
            catch (TargetInvocationException e) when (e.InnerException is KeyNotFoundException)
            {
                return null;
            }
        }

        private static void Break(Exception e)
        {
            broken = CompatibilityLog.Reason(e);
            brokenStatus = "stopped: " + broken;
            ownLayer = null;
            redirectedCameras.Clear();
            CompatibilityLog.Warn("tufx-post", "TUFX's effects went back before FSR until the upscaler is set up again: " + broken + ".");
        }

        private static Texture NeutralDither()
        {
            if (neutralDither != null) return neutralDither;
            neutralDither = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)
            {
                name = "ReDefinition_NeutralDither",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            neutralDither.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.5f));
            neutralDither.Apply(false, true);
            return neutralDither;
        }

        private static string Names(Type[] types)
        {
            if (types.Length == 0) return "none";
            StringBuilder sb = new StringBuilder();
            foreach (Type type in types)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(type.Name);
            }
            return sb.ToString();
        }

        private static Type TypeIn(Assembly assembly, string name)
        {
            return assembly.GetType(PostProcessing + name, true);
        }

        private static FieldInfo FieldOf(Type type, string name)
        {
            FieldInfo field = type.GetField(name, Members);
            if (field == null) throw new MissingFieldException(type.Name, name);
            return field;
        }

        private static PropertyInfo PropertyOf(Type type, string name, BindingFlags flags)
        {
            PropertyInfo property = type.GetProperty(name, flags);
            if (property == null) throw new MissingMemberException(type.Name, name);
            return property;
        }

        private static MethodInfo SetterOf(PropertyInfo property)
        {
            MethodInfo setter = property.GetSetMethod(true);
            if (setter == null) throw new MissingMethodException(property.DeclaringType.Name, "set_" + property.Name);
            return setter;
        }

        private static MethodInfo MethodOf(Type type, string name, params Type[] parameters)
        {
            MethodInfo method = type.GetMethod(name, Members, null, parameters, null);
            if (method == null) throw new MissingMethodException(type.Name, name);
            return method;
        }
    }
}
