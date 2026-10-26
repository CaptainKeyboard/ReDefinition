using ReDefinition.Api.Internal;
using ReDefinition.Proxy;
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
    // EVE binds the clouds' history at the scene camera's AfterForwardOpaque
    // (scattererReconstructedCloud) and, from Scatterer's reconstruction, their
    // motion vectors (scattererReconstructedCloudMotionVectors). They are read at
    // AfterForwardAlpha -- the camera's OnPostRender, where EVE puts the globals
    // back, runs before BeforeImageEffects (measured for UpscalerMasks) -- into a
    // copy of the rig's, without the jitter EveCloudMotion gave EVE, and blended
    // over Unity's where the rig captures those, at BeforeImageEffects, the way
    // Scatterer's TemporalAntialiasing blends the two (disassembled): the clouds'
    // where they cover the pixel, over the sky by their transmittance, over
    // geometry once less than a tenth of it is left.
    //
    // With any other reconstruction -- EVE's own, without Scatterer, leaves no
    // motion vectors in that texture (EveCloudMotion.KnownReconstruction) -- the
    // clouds' motion is worked out instead (pass 4): each pixel's ray meets the
    // cloud layer's shell around the main body (EveCloudMotion.Shell) where it
    // enters it from below or above, or half way to where it leaves it from
    // inside, and that point, fixed to the body, moves by the camera's move
    // against the body (SceneMotion). The clouds' own drift over the body and
    // their depth inside the layer are left out. Without a main body, the clouds
    // keep Unity's.
    internal sealed class CloudMotionVectors
    {
        // The cloud motion shader's passes: the clouds' motion from Scatterer's reconstruction, the blend over
        // Unity's motion vectors, and the clouds' motion worked out from the shell.
        private const int EvePass = 0;
        private const int BlendPass = 1;
        private const int ShellPass = 4;

        // EVE's globals of the clouds' history and motion vectors, and the globals the passes read: fade,
        // texel size, the two motion textures and the depth for the blend, and pass 4's camera and shell.
        private static readonly int CloudTextureId = Shader.PropertyToID("scattererReconstructedCloud");
        private static readonly int CloudMotionTextureId = Shader.PropertyToID("scattererReconstructedCloudMotionVectors");
        private static readonly int FadeId = Shader.PropertyToID("_ReDefinitionCloudFade");
        private static readonly int TexelId = Shader.PropertyToID("_ReDefinitionCloudTexel");
        private static readonly int UnityMotionId = Shader.PropertyToID("_ReDefinitionUnityMotion");
        private static readonly int CloudMotionId = Shader.PropertyToID("_ReDefinitionCloudMotion");
        private static readonly int SceneDepthId = Shader.PropertyToID("_ReDefinitionCloudSceneDepth");
        private static readonly int ProjectionId = Shader.PropertyToID("_ReDefinitionCloudProjection");
        private static readonly int InverseProjectionId = Shader.PropertyToID("_ReDefinitionCloudInverseProjection");
        private static readonly int ViewToPrevClipId = Shader.PropertyToID("_ReDefinitionCloudViewToPrevClip");
        private static readonly int OriginId = Shader.PropertyToID("_ReDefinitionCloudOrigin");
        private static readonly int ShellId = Shader.PropertyToID("_ReDefinitionCloudShell");

        // What LogState last wrote, and whether it wrote at all: the log says each change once.
        private static string loggedState;
        private static bool loggedFromShell;
        private static bool logged;

        // The material of the bundle's cloud motion shader, made once it is wanted.
        private Material material;
        // The reconstruction shader last judged, with its verdict: null where it is
        // Scatterer's, why the clouds' motion comes from the shell otherwise.
        private Shader judgedReconstruction;
        private string reconstructionVerdict;
        private RenderTexture clouds;
        private RenderTexture unityMotion;
        private CommandBuffer buffer;
        private Camera bufferCamera;
        // Whether the clouds take their motion from the shell (pass 4) rather
        // than from Scatterer's reconstruction, as judged last.
        private bool fromShell;

        // The camera's pose against the main body the frame before, for pass 4.
        private Matrix4x4 previousProjection;
        private Vector3d previousPosition;
        private Quaternion previousRotation;
        private CelestialBody previousBody;
        private int previousFrame = -1;
        private bool havePrevious;
        private int updatedFrame = -1;

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
                && (!RenderTextures.Ensure(ref clouds, size, RenderTextureFormat.ARGBHalf, "ReDefinition_CloudMotion", out created)
                    || !RenderTextures.Ensure(ref unityMotion, size, RenderTextureFormat.RGHalf, "ReDefinition_UnityMotion",
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
            buffer.Blit(Texture2D.blackTexture, clouds, material, fromShell ? ShellPass : EvePass);
            Active = true;
        }

        // As the scene camera culls, once a frame, after LateUpdate, where KSP places
        // the flight camera, and with the frame's state decided (SharedFrame): pass
        // 4's inputs as globals, read by the buffer recorded for this frame at
        // AfterForwardAlpha. The projection without jitter, built from the camera's
        // planes and field of view as UnityMotionRepair builds it; the camera against
        // the body's centre in camera space, with the squared distances to the
        // shell's two spheres (|o|^2 - r^2) taken in double, which single precision
        // would lose hundreds of kilometres from the centre; and from camera space
        // into the frame before's clip space through the camera's move against the
        // body -- SceneMotion's matrix with an identity for this frame's projection.
        // Until there is a frame before with the same body, w of the origin is 0 and
        // the shader leaves the clouds out.
        internal void Sample(Camera camera)
        {
            if (!Active || !fromShell || camera == null || updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            SharedFrame.EnsureBegun();
            CelestialBody body = FlightGlobals.currentMainBody;
            float inner, outer;
            if (body == null || !EveCloudMotion.Shell(out inner, out outer))
            {
                havePrevious = false;
                Shader.SetGlobalVector(OriginId, Vector4.zero);
                return;
            }
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(
                Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, camera.nearClipPlane, camera.farClipPlane), false);
            Quaternion toBody = SceneMotion.Conjugate(body.transform.rotation);
            Vector3d fromCentre = (Vector3d)camera.transform.position - (Vector3d)body.transform.position;
            Vector3d position = SceneMotion.Rotate(toBody, fromCentre);
            Quaternion rotation = toBody * camera.transform.rotation;
            // Unity's camera space looks down -z.
            Vector3d local = SceneMotion.Rotate(SceneMotion.Conjugate(camera.transform.rotation), fromCentre);
            double squared = fromCentre.sqrMagnitude;

            bool ready = havePrevious && previousBody == body && previousFrame == Time.frameCount - 1
                         && SharedFrame.RigResetReason == null;
            Matrix4x4 viewToPrevious = ready
                ? SceneMotion.ClipToPreviousClip(Matrix4x4.identity, previousProjection, position, rotation,
                    previousPosition, previousRotation)
                : Matrix4x4.identity;
            Shader.SetGlobalMatrix(ProjectionId, projection);
            Shader.SetGlobalMatrix(InverseProjectionId, StreamlineCamera.Invert(projection));
            Shader.SetGlobalMatrix(ViewToPrevClipId, viewToPrevious);
            Shader.SetGlobalVector(OriginId, new Vector4((float)local.x, (float)local.y, (float)-local.z, ready ? 1f : 0f));
            Shader.SetGlobalVector(ShellId, new Vector4((float)(squared - (double)inner * inner),
                                                        (float)(squared - (double)outer * outer), 0f, 0f));

            previousProjection = projection;
            previousPosition = position;
            previousRotation = rotation;
            previousBody = body;
            previousFrame = Time.frameCount;
            havePrevious = true;
        }

        // The scene's motion vectors into the rig's capture, after the depth is
        // copied -- Unity's, or those with the distant planets' merged in
        // (ScaledSpaceMotion): straight into motionVectors, or with the clouds
        // recorded for this frame into a copy of their own and blended from there.
        internal void Capture(CommandBuffer capture, RenderTargetIdentifier source, RenderTexture motionVectors,
                              RenderTexture depth)
        {
            if (!Active)
            {
                capture.Blit(source, motionVectors);
                return;
            }
            capture.Blit(source, unityMotion);
            capture.SetGlobalTexture(UnityMotionId, unityMotion);
            capture.SetGlobalTexture(CloudMotionId, clouds);
            capture.SetGlobalTexture(SceneDepthId, depth);
            capture.Blit(Texture2D.blackTexture, motionVectors, material, BlendPass);
        }

        // Takes the buffer off its camera and frees the textures and the material.
        internal void Dispose()
        {
            Detach();
            RenderTextures.Release(ref clouds);
            RenderTextures.Release(ref unityMotion);
            if (material != null) Object.Destroy(material);
            material = null;
            Active = false;
        }

        // Null where the clouds can be blended in this frame, the reason otherwise; decides fromShell and
        // makes the material on the way.
        private string Why(Camera camera)
        {
            if (!EveCloudMotion.Present) return "EVE's volumetric clouds are not installed";
            if (!EveCloudMotion.Enabled) return "switched off in the diagnostics window";
            if (camera == null) return "no scene camera";
            Shader reconstruction = EveCloudMotion.ReconstructionShader();
            if (reconstruction == null) return "EVE has not rendered clouds yet";
            // Judged once per shader: its name is a new string on every read.
            if (reconstruction != judgedReconstruction)
            {
                judgedReconstruction = reconstruction;
                reconstructionVerdict = reconstruction.name != EveCloudMotion.KnownReconstruction
                    ? "EVE reconstructs its clouds with " + reconstruction.name + ", which leaves no motion vectors"
                      + " this build reads (it reads " + EveCloudMotion.KnownReconstruction + "'s, Scatterer's)"
                    // The same name over other code: what it writes is not known to be
                    // what is read (ForeignAssumptions; the diagnostics window says what changed).
                    : !ForeignAssumptions.EveCloudMotion.Holds || !ForeignAssumptions.ScattererCloudReconstruction.Holds
                        ? "the clouds' reconstruction is not the code this build reads"
                        : null;
            }
            fromShell = reconstructionVerdict != null;
            if (fromShell)
            {
                if (FlightGlobals.currentMainBody == null) return "there is no main body to work the clouds' motion out against";
                float inner, outer;
                if (!EveCloudMotion.Shell(out inner, out outer)) return "EVE has not set its clouds' shell yet";
            }
            // Bound at all, once: which ones is decided as the camera renders, after
            // this -- EVE binds each camera's own, reflection probes' included, and
            // puts a white one back after each. Read by position, so a frame in
            // which EVE's are still of the size before a change reads them scaled.
            if ((!fromShell && Shader.GetGlobalTexture(CloudMotionTextureId) == null)
                || Shader.GetGlobalTexture(CloudTextureId) == null)
                return "EVE has not bound its clouds' textures yet";
            if (material == null)
            {
                string error;
                bool outdated;
                material = ShaderBundle.MakeMaterial(ShaderBundle.CloudMotionShaderName, "ReDefinition cloud motion", ShellPass + 1,
                    out error, out outdated);
                if (material == null) return error;
            }
            return null;
        }

        // Each change once, not every frame.
        private void LogState()
        {
            if (!EveCloudMotion.Present
                || (logged && State == loggedState && (State != null || fromShell == loggedFromShell))) return;
            logged = true;
            loggedState = State;
            loggedFromShell = fromShell;
            string line = State != null
                ? "left out -- " + State + "."
                : fromShell
                    ? "worked out from the cloud layer's shell and blended into the motion vectors -- "
                      + reconstructionVerdict + "."
                    : "blended into the motion vectors.";
            Debug.Log(Log.Tag + " EVE's cloud motion vectors: " + line);
        }

        // Takes the command buffer off the camera it was added to and releases it.
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
