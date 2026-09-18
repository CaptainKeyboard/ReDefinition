using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // EVE's raymarched clouds in the motion vectors every upscaler and frame
    // generation read. Unity's motion vectors know only the depth buffer, which
    // the clouds do not write: over them they carry the sky's or the ground's
    // motion. NVIDIA: "Make sure all objects visible on screen have motion vectors
    // calculated correctly. Common places to miss out motion vectors include
    // animated foliage, the sky" (DLSS Programming Guide, 31 March 2026).
    //
    // EVE makes the clouds' own and binds them with the clouds' history at the
    // scene camera's AfterForwardOpaque (scattererReconstructedCloudMotionVectors,
    // scattererReconstructedCloud). They are read at AfterForwardAlpha -- the
    // camera's OnPostRender, where EVE puts the globals back, runs before
    // BeforeImageEffects (measured for UpscalerMasks) -- into a copy of the rig's,
    // without the jitter EveCloudMotion gave EVE, and blended over Unity's where
    // the rig captures those, at BeforeImageEffects, the way Scatterer's
    // TemporalAntialiasing blends the two (disassembled): the clouds' where they
    // cover the pixel, over the sky by their transmittance, over geometry once
    // less than a tenth of it is left.
    //
    // Only with the reconstruction whose output was read
    // (EveCloudMotion.KnownReconstruction).
    internal sealed class CloudMotionVectors
    {
        private static readonly int CloudTextureId = Shader.PropertyToID("scattererReconstructedCloud");
        private static readonly int CloudMotionTextureId = Shader.PropertyToID("scattererReconstructedCloudMotionVectors");
        private static readonly int FadeId = Shader.PropertyToID("_ReDefinitionCloudFade");
        private static readonly int TexelId = Shader.PropertyToID("_ReDefinitionCloudTexel");
        private static readonly int UnityMotionId = Shader.PropertyToID("_ReDefinitionUnityMotion");
        private static readonly int CloudMotionId = Shader.PropertyToID("_ReDefinitionCloudMotion");
        private static readonly int SceneDepthId = Shader.PropertyToID("_ReDefinitionCloudSceneDepth");

        private static string loggedState;

        private Material material;
        // A shader from the bundle with too few passes, not made into a material
        // again every frame; and the reconstruction last judged, with its verdict.
        private Shader outdatedShader;
        private Shader judgedReconstruction;
        private string reconstructionVerdict;
        private RenderTexture clouds;
        private RenderTexture unityMotion;
        private CommandBuffer buffer;
        private Camera bufferCamera;

        // Whether this frame's capture blends the clouds in, as recorded last.
        internal bool Active { get; private set; }

        // Why not, for the diagnostics; null while they are blended in.
        internal string State { get; private set; }

        // Every frame, before the scene camera renders.
        internal void Record(Camera camera, Vector2Int size)
        {
            Active = false;
            State = Why(camera);
            bool created;
            if (State == null
                && (!UpscalerMasks.Ensure(ref clouds, size, RenderTextureFormat.ARGBHalf, "ReDefinition_CloudMotion", out created)
                    || !UpscalerMasks.Ensure(ref unityMotion, size, RenderTextureFormat.RGHalf, "ReDefinition_UnityMotion",
                                             out created)))
                State = "their textures could not be created";
            LogState();
            if (State != null)
            {
                Detach();
                return;
            }

            if (buffer == null || bufferCamera != camera)
            {
                Detach();
                buffer = new CommandBuffer { name = "ReDefinition.CloudMotion" };
                camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, buffer);
                bufferCamera = camera;
            }
            buffer.Clear();
            buffer.SetGlobalVector(TexelId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
            buffer.SetGlobalFloat(FadeId, EveCloudMotion.Fade());
            buffer.Blit(Texture2D.blackTexture, clouds, material, 0);
            Active = true;
        }

        // Unity's motion vectors into the rig's capture, after the depth is copied:
        // straight into motionVectors, or with the clouds recorded for this frame
        // into a copy of their own and blended from there.
        internal void Capture(CommandBuffer capture, RenderTexture motionVectors, RenderTexture depth)
        {
            if (!Active)
            {
                capture.Blit(BuiltinRenderTextureType.MotionVectors, motionVectors);
                return;
            }
            capture.Blit(BuiltinRenderTextureType.MotionVectors, unityMotion);
            capture.SetGlobalTexture(UnityMotionId, unityMotion);
            capture.SetGlobalTexture(CloudMotionId, clouds);
            capture.SetGlobalTexture(SceneDepthId, depth);
            capture.Blit(Texture2D.blackTexture, motionVectors, material, 1);
        }

        internal void Dispose()
        {
            Detach();
            UpscalerRig.Release(ref clouds);
            UpscalerRig.Release(ref unityMotion);
            if (material != null) Object.Destroy(material);
            material = null;
            Active = false;
        }

        private string Why(Camera camera)
        {
            if (!EveCloudMotion.Present) return "EVE's volumetric clouds are not installed";
            if (!EveCloudMotion.Enabled) return "switched off in the Debug tab";
            if (camera == null) return "no scene camera";
            Shader reconstruction = EveCloudMotion.ReconstructionShader();
            if (reconstruction == null) return "EVE has not rendered clouds yet";
            // Judged once per shader: its name is a new string on every read.
            if (reconstruction != judgedReconstruction)
            {
                judgedReconstruction = reconstruction;
                reconstructionVerdict = reconstruction.name == EveCloudMotion.KnownReconstruction
                    ? null
                    : "EVE reconstructs its clouds with " + reconstruction.name + ", whose motion vectors this build does"
                      + " not know (it knows " + EveCloudMotion.KnownReconstruction + ", Scatterer's)";
            }
            if (reconstructionVerdict != null) return reconstructionVerdict;
            // Bound at all, once: which ones is decided as the camera renders, after
            // this -- EVE binds each camera's own, reflection probes' included, and
            // puts a white one back after each. Read by position, so a frame in
            // which EVE's are still of the size before a change reads them scaled.
            if (Shader.GetGlobalTexture(CloudMotionTextureId) == null || Shader.GetGlobalTexture(CloudTextureId) == null)
                return "EVE has not bound its clouds' textures yet";
            if (material == null)
            {
                string error;
                Shader shader = FsrShaderBundle.LoadShader(FsrShaderBundle.CloudMotionShaderName, out error);
                if (shader == null) return error;
                const string outdated = FsrShaderBundle.CloudMotionShaderName + " in the shader bundle is older than this build";
                if (shader == outdatedShader) return outdated;
                material = new Material(shader) { name = "ReDefinition cloud motion", hideFlags = HideFlags.DontSave };
                if (material.passCount < 2)
                {
                    Object.Destroy(material);
                    material = null;
                    outdatedShader = shader;
                    return outdated;
                }
            }
            return null;
        }

        // Each change once, not every frame.
        private void LogState()
        {
            if (State == loggedState || !EveCloudMotion.Present) return;
            loggedState = State;
            Debug.Log(Log.Tag + " EVE's cloud motion vectors: "
                      + (State == null ? "blended into the motion vectors." : "left out -- " + State + "."));
        }

        private void Detach()
        {
            Active = false;
            if (buffer == null) return;
            if (bufferCamera != null) bufferCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, buffer);
            buffer.Release();
            buffer = null;
            bufferCamera = null;
        }
    }
}
