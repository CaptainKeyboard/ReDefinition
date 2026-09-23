using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // The distant planets' own motion in the motion vectors.
    //
    // Camera ScaledSpace draws the planets seen from afar, their atmospheres and
    // the clouds on them; Camera 00 draws the near scene over it. The rig's motion
    // vectors are Camera 00's, and where it draws nothing -- depth at the far
    // plane -- Unity fills them with its camera motion at infinity: the camera's
    // turn, nothing of the planet's rotation or of the vessel travelling along its
    // orbit. A planet that moves across the screen, fast under time warp, is then
    // reprojected as if it stood still, and trails.
    //
    // Unity makes the scaled camera's own motion vectors where it is asked to
    // (DepthTextureMode.MotionVectors): its camera motion from its own depth, and
    // object motion for the planets, which KSP turns by their transforms. They are
    // copied at that camera's BeforeImageEffects, as the scene camera's are, and
    // take the place of Camera 00's wherever Camera 00 drew nothing, before EVE's
    // clouds are blended over (CloudMotionVectors). What a shader moves -- EVE's
    // two-dimensional cloud layers drifting over the planet -- has none, and keeps
    // the planet's under it.
    internal sealed class ScaledSpaceMotion
    {
        private const string CameraName = "Camera ScaledSpace";
        private const int MergePass = 2;

        private static readonly int UnityMotionId = Shader.PropertyToID("_ReDefinitionUnityMotion");
        private static readonly int ScaledMotionId = Shader.PropertyToID("_ReDefinitionScaledMotion");
        private static readonly int SceneDepthId = Shader.PropertyToID("_ReDefinitionCloudSceneDepth");
        private static readonly int TexelId = Shader.PropertyToID("_ReDefinitionCloudTexel");

        // Outside any motion vector Unity writes, which are changes of the viewport
        // position within [-1, 1].
        private static readonly Color NotWritten = new Color(-2f, -2f, 0f, 0f);

        private static string loggedState;

        private Camera camera;
        private DepthTextureMode addedModes;
        private CommandBuffer buffer;
        private RenderTexture scaled;
        private RenderTexture scene;
        private RenderTexture merged;
        private Material material;
        private Shader outdatedShader;

        // Whether this frame's capture takes the scaled camera's motion over the sky.
        internal bool Active { get; private set; }

        // Why not, for the diagnostics; null while it does.
        internal string State { get; private set; }

        // The merged motion vectors, for the clouds to be blended over.
        internal RenderTexture Merged
        {
            get { return merged; }
        }

        // Once the cameras are redirected: the scaled camera among them.
        internal void Attach(List<CameraRedirect> redirects, Vector2Int size)
        {
            Detach();
            foreach (CameraRedirect redirect in redirects)
            {
                if (redirect == null || redirect.Camera == null || redirect.Camera.name != CameraName) continue;
                camera = redirect.Camera;
                break;
            }
            State = Prepare(size);
            LogState();
            if (State != null)
            {
                Detach();
                return;
            }

            DepthTextureMode wanted = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            addedModes = wanted & ~camera.depthTextureMode;
            camera.depthTextureMode |= wanted;

            buffer = new CommandBuffer { name = "ReDefinition.ScaledSpaceMotion" };
            buffer.Blit(BuiltinRenderTextureType.MotionVectors, scaled);
            camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, buffer);
            Active = true;
        }

        // In the scene camera's capture: its motion vectors into a copy, and the
        // scaled camera's over them where the scene camera drew nothing.
        internal void Merge(CommandBuffer capture, RenderTexture depth, Vector2Int size)
        {
            capture.Blit(BuiltinRenderTextureType.MotionVectors, scene);
            capture.SetGlobalVector(TexelId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
            capture.SetGlobalTexture(UnityMotionId, scene);
            capture.SetGlobalTexture(ScaledMotionId, scaled);
            capture.SetGlobalTexture(SceneDepthId, depth);
            capture.Blit(Texture2D.blackTexture, merged, material, MergePass);
            // Marked as not written: a frame the scaled camera does not render, or
            // whose event does not come, leaves the mark, and the pass keeps the
            // scene camera's motion there rather than taking none.
            capture.SetRenderTarget(scaled);
            capture.ClearRenderTarget(false, true, NotWritten);
        }

        internal void Detach()
        {
            Active = false;
            if (camera != null)
            {
                if (buffer != null) camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, buffer);
                camera.depthTextureMode &= ~addedModes;
            }
            addedModes = DepthTextureMode.None;
            if (buffer != null) buffer.Release();
            buffer = null;
            camera = null;
            UpscalerRig.Release(ref scaled);
            UpscalerRig.Release(ref scene);
            UpscalerRig.Release(ref merged);
        }

        internal void Dispose()
        {
            Detach();
            if (material != null) Object.Destroy(material);
            material = null;
        }

        private string Prepare(Vector2Int size)
        {
            if (camera == null) return "no scaled space camera in this scene";
            if (material == null)
            {
                string error;
                Shader shader = FsrShaderBundle.LoadShader(FsrShaderBundle.CloudMotionShaderName, out error);
                if (shader == null) return error;
                const string outdated = FsrShaderBundle.CloudMotionShaderName + " in the shader bundle is older than this build";
                if (shader == outdatedShader) return outdated;
                material = new Material(shader) { name = "ReDefinition scaled space motion", hideFlags = HideFlags.DontSave };
                if (material.passCount <= MergePass)
                {
                    Object.Destroy(material);
                    material = null;
                    outdatedShader = shader;
                    return outdated;
                }
            }
            bool created;
            if (!UpscalerMasks.Ensure(ref scaled, size, RenderTextureFormat.RGHalf, "ReDefinition_ScaledMotion", out created)
                || !UpscalerMasks.Ensure(ref scene, size, RenderTextureFormat.RGHalf, "ReDefinition_SceneMotion", out created)
                || !UpscalerMasks.Ensure(ref merged, size, RenderTextureFormat.RGHalf, "ReDefinition_MergedMotion", out created))
                return "its textures could not be created";
            return null;
        }

        // Each change once, not every frame.
        private void LogState()
        {
            if (State == loggedState) return;
            loggedState = State;
            Debug.Log(Log.Tag + " The distant planets' motion vectors: "
                      + (State == null ? "taken from " + CameraName + " where the scene camera draws nothing."
                                       : "left out -- " + State + "."));
        }
    }
}
