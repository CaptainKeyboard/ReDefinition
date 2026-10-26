using System.Globalization;
using ReDefinition.Api.Internal;
using ReDefinition.Core;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // Unity's motion vectors of the flight camera, repaired where Unity has not written
    // them. The readers are the upscalers and frame generation, and with
    // ReDefinition's upscaler off Scatterer's TAA, TUFX's effects and Deferred's
    // reflections.
    //
    // In KSP, Camera 00's motion vector buffer is at times not written for many frames:
    // the ground then carries the same vectors frame after frame, whatever the camera
    // does. Measured in the recordings' frame generation inputs after zooming out to the
    // farthest and back, and from the start of other flights at 97 to 100% of the
    // pixels. What stops Unity is not known (docs/development/upscaler.md).
    //
    // At the end of the camera's frame the buffer is cleared to Missing. At
    // BeforeImageEffectsOpaque of the next, after Unity writes its motion vectors and
    // before anything reads them, a copy is taken. Pass 3 of the cloud motion shader
    // writes it back with the camera's own move where it still holds Missing: against
    // the main body where something is drawn (SceneMotion), the camera's turn alone where
    // nothing is. Both matrices are set here as the camera culls; pass 2 uses the first
    // for far ground as well (ScaledSpaceMotion). Only while something reads them (Wanted).
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class UnityMotionRepair : MonoBehaviour
    {
        // The cloud motion shader's repair pass; the frames between two readbacks of the copy; the seconds between two
        // reports of unwritten pixels. With the diagnostic measurements on, a row of the copy is read back every
        // CheckEvery frames, and the share of unwritten pixels logged every ten seconds while there are any.
        private const int RepairPass = 3;
        private const int CheckEvery = 30;
        private const float ReportSeconds = 10f;

        // From this view depth on, pass 2 replaces the scene camera's own motion vectors
        // by those of points fixed to the main body, while the camera is beyond
        // FarFromOrigin from the origin. Nearer the
        // camera, other vessels, kerbals and debris keep Unity's, which moves them as
        // objects. The active vessel is repaired after this in any case
        // (VesselMotionVectors). Nearer to the origin, Unity's are precise and stay
        // everywhere: a vessel in orbit a kilometre from the active one would otherwise
        // move by the camera's travel against the body. Beyond it, such a vessel is at
        // least that far from the camera and a few pixels large.
        private const float PreciseFrom = 500f;
        // Unity's motion vectors are noise farther from the world's origin than this, worked out in single
        // precision from world positions that far out.
        private const float FarFromOrigin = 20000f;

        // Far outside the range of a motion vector in viewport units; half precision
        // holds it exactly. The shader tests for below -50.
        private static readonly Color Missing = new Color(-100f, -100f, 0f, 0f);

        // The shader globals: the scene's and the sky's clip-to-previous-clip matrices, the view range with PreciseFrom
        // and whether the matrices hold, and the repair pass's copies of motion and depth with their texel size.
        private static readonly int SceneClipToPrevClipId = Shader.PropertyToID("_ReDefinitionSceneClipToPrevClip");
        private static readonly int SkyClipToPrevClipId = Shader.PropertyToID("_ReDefinitionSkyClipToPrevClip");
        private static readonly int SceneRangeId = Shader.PropertyToID("_ReDefinitionSceneRange");
        private static readonly int RepairMotionId = Shader.PropertyToID("_ReDefinitionRepairMotion");
        private static readonly int RepairDepthId = Shader.PropertyToID("_ReDefinitionRepairDepth");
        private static readonly int RepairTexelId = Shader.PropertyToID("_ReDefinitionRepairTexel");

        // The Debug switch; on at every start.
        internal static bool Enabled = true;

        // The camera the repair and mark buffers are on.
        private Camera attached;
        // Set once the shader cannot be had: not tried again in this scene.
        private bool unavailable;
        // Taken off: the mark is gone, the repair runs once more to take its value out
        // of the buffer, and is removed after that frame.
        private int retireAfter = -1;
        private CommandBuffer repair;
        private CommandBuffer mark;
        private Material material;
        private string state;
        private RenderTexture motionCopy;
        private RenderTexture depthCopy;
        private Vector2Int size;
        private int preparedFrame = -1;

        // The camera's projection, position and turn against the main body, and turn in the world, of the frame before,
        // with the body and frame they belong to, for UpdateMatrices.
        private Matrix4x4 previousProjection;
        private Vector3d previousPosition;
        private Quaternion previousRotation;
        private Quaternion previousWorldRotation;
        private CelestialBody previousBody;
        private int previousFrame = -1;
        private bool havePrevious;

        // Readbacks in the current report window, and those of them with unwritten pixels.
        private int checkedFrames, missingFrames;
        // The sum of each readback's share of unwritten pixels in the window.
        private double missingShare;
        // When the current report window began, in unscaled time; negative before the first.
        private float windowStart = -1f;

        // Hooks every camera's pre-cull, where the flight camera is found.
        private void Start()
        {
            Camera.onPreCull += OnAnyPreCull;
        }

        // Takes the buffers off the camera and clears the scene range, so pass 2 does no far-ground replacement.
        private void OnDestroy()
        {
            Camera.onPreCull -= OnAnyPreCull;
            Detach();
            Shader.SetGlobalVector(SceneRangeId, Vector4.zero);
            if (material != null) Destroy(material);
            material = null;
        }

        // For the flight camera, once per frame: the matrices updated, the buffers attached, kept, retired or taken off,
        // and this frame's repair recorded.
        private void OnAnyPreCull(Camera cam)
        {
            Camera main = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null || cam != main) return;
            if (preparedFrame == Time.frameCount) return;
            preparedFrame = Time.frameCount;

            // Pass 2 reads them for far ground whether the repair runs or not.
            UpdateMatrices(cam);

            if (attached != null && attached != cam) Detach();
            if (retireAfter >= 0)
            {
                if (Time.frameCount > retireAfter) Detach();
                else
                {
                    RecordRepair();
                    return;
                }
            }
            if (!Enabled || unavailable || !Wanted(cam))
            {
                Retire();
                return;
            }

            if (attached == null) Attach(cam);
            if (attached == null) return;
            Vector2Int now = new Vector2Int(cam.pixelWidth, cam.pixelHeight);
            bool created;
            if (!RenderTextures.Ensure(ref motionCopy, now, RenderTextureFormat.RGHalf, "ReDefinition_RepairMotion", out created)
                || !RenderTextures.Ensure(ref depthCopy, now, RenderTextureFormat.RFloat, "ReDefinition_RepairDepth", out created))
            {
                SetState("its textures could not be created");
                // Without them the last mark cannot be repaired: taken out as none.
                repair.Clear();
                repair.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
                repair.ClearRenderTarget(false, true, Color.clear);
                attached.RemoveCommandBuffer(CameraEvent.AfterEverything, mark);
                retireAfter = Time.frameCount;
                return;
            }
            size = now;

            RecordRepair();
            ReportMissing();
        }

        // While the rig hands frames to an upscaler or to frame generation, and while
        // another mod has the camera draw motion vectors -- Scatterer's TAA, TUFX's
        // effects, Deferred's reflections. Without either nothing reads them.
        private static bool Wanted(Camera cam)
        {
            return UpscalerRig.Feeding
                   || ((cam.depthTextureMode & DepthTextureMode.MotionVectors) != 0 && !UpscalerRig.AskedForMotionVectors(cam));
        }

        // Makes the material once and attaches the repair at BeforeImageEffectsOpaque and the mark at AfterEverything.
        private void Attach(Camera cam)
        {
            Detach();
            if (material == null)
            {
                string error;
                bool outdated;
                material = ShaderBundle.MakeMaterial(ShaderBundle.CloudMotionShaderName, "ReDefinition motion vector repair",
                    RepairPass + 1, out error, out outdated);
                if (material == null)
                {
                    unavailable = true;
                    SetState(error);
                    return;
                }
            }
            repair = new CommandBuffer { name = "ReDefinition.RepairMotionVectors" };
            mark = new CommandBuffer { name = "ReDefinition.MarkMotionVectors" };
            mark.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
            mark.ClearRenderTarget(false, true, Missing);
            cam.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, repair);
            cam.AddCommandBuffer(CameraEvent.AfterEverything, mark);
            attached = cam;
            retireAfter = -1;
            SetState(null);
        }

        // Switched off: the mark comes off at once, and the repair takes this frame's
        // mark out of the buffer before it goes, so what reads the buffer next sees
        // Unity's own motion vectors, or none, never the mark.
        private void Retire()
        {
            if (attached == null) return;
            attached.RemoveCommandBuffer(CameraEvent.AfterEverything, mark);
            retireAfter = Time.frameCount + 1;
        }

        // Takes both buffers off the camera, releases them and the copies.
        private void Detach()
        {
            if (attached != null)
            {
                if (repair != null) attached.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, repair);
                if (mark != null) attached.RemoveCommandBuffer(CameraEvent.AfterEverything, mark);
            }
            attached = null;
            if (repair != null) repair.Release();
            if (mark != null) mark.Release();
            repair = null;
            mark = null;
            RenderTextures.Release(ref motionCopy);
            RenderTextures.Release(ref depthCopy);
            retireAfter = -1;
        }

        // This frame's: Unity's motion vectors and depth copied, and written back with the
        // camera's move where Unity left them.
        private void RecordRepair()
        {
            repair.Clear();
            repair.Blit(BuiltinRenderTextureType.MotionVectors, motionCopy);
            repair.Blit(BuiltinRenderTextureType.ResolvedDepth, depthCopy);
            repair.SetGlobalTexture(RepairMotionId, motionCopy);
            repair.SetGlobalTexture(RepairDepthId, depthCopy);
            repair.SetGlobalVector(RepairTexelId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
            repair.Blit(Texture2D.blackTexture, BuiltinRenderTextureType.MotionVectors, material, RepairPass);
            if (ReDefinitionAddon.DebugMode && Time.frameCount % CheckEvery == 0) CheckMissing();
        }

        // The camera's move against the main body, and its turn, from this frame's clip
        // space into the previous frame's (SceneMotion). The projection is built from
        // the camera's planes and field of view, free of any jitter a mod has put on
        // it; KSP's flight camera keeps Unity's own.
        private void UpdateMatrices(Camera cam)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null)
            {
                havePrevious = false;
                Shader.SetGlobalVector(SceneRangeId, Vector4.zero);
                return;
            }
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(
                Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, cam.nearClipPlane, cam.farClipPlane), false);
            Quaternion toBody = SceneMotion.Conjugate(body.transform.rotation);
            // Where the body's ground is drawn this frame: its transform, which KSP
            // places with the scene.
            Vector3d position = SceneMotion.Rotate(toBody,
                (Vector3d)cam.transform.position - (Vector3d)body.transform.position);
            Quaternion rotation = toBody * cam.transform.rotation;
            Quaternion worldRotation = cam.transform.rotation;

            bool ready = havePrevious && previousBody == body
                         && previousFrame == Time.frameCount - 1 && SharedFrame.RigResetReason == null;
            Matrix4x4 clipToPrevious = ready
                ? SceneMotion.ClipToPreviousClip(projection, previousProjection, position, rotation,
                    previousPosition, previousRotation)
                : Matrix4x4.identity;
            Matrix4x4 skyToPrevious = ready
                ? SceneMotion.ClipToPreviousClip(projection, previousProjection, Vector3d.zero, worldRotation,
                    Vector3d.zero, previousWorldRotation)
                : Matrix4x4.identity;
            bool far = cam.transform.position.magnitude > FarFromOrigin;
            Shader.SetGlobalMatrix(SceneClipToPrevClipId, clipToPrevious);
            Shader.SetGlobalMatrix(SkyClipToPrevClipId, skyToPrevious);
            Shader.SetGlobalVector(SceneRangeId, new Vector4(cam.nearClipPlane, cam.farClipPlane,
                far ? PreciseFrom : float.MaxValue, ready ? 1f : 0f));

            previousProjection = projection;
            previousPosition = position;
            previousRotation = rotation;
            previousWorldRotation = worldRotation;
            previousBody = body;
            previousFrame = Time.frameCount;
            havePrevious = true;
        }

        // A row across the middle of the copy: the share of pixels Unity left unwritten.
        private void CheckMissing()
        {
            int width = size.x;
            if (missingRead == null) missingRead = MissingRead;
            repair.RequestAsyncReadback(motionCopy, 0, 0, width, size.y / 2, 1, 0, 1, missingRead);
        }

        // Made once: the method group passed as is would be a new delegate at every check.
        private System.Action<AsyncGPUReadbackRequest> missingRead;

        // Counts the pixels of the read-back row that still hold the mark.
        private void MissingRead(AsyncGPUReadbackRequest request)
        {
            if (request.hasError) return;
            NativeArray<ushort> data = request.GetData<ushort>();
            int missing = 0, pixels = data.Length / 2;
            for (int i = 0; i < pixels; i++)
                if (Mathf.HalfToFloat(data[i * 2]) < -50f) missing++;
            checkedFrames++;
            if (missing > 0) missingFrames++;
            missingShare += pixels > 0 ? (double)missing / pixels : 0.0;
        }

        // Logs the share of unwritten pixels every ReportSeconds, where there were any, and starts a new window.
        private void ReportMissing()
        {
            if (windowStart < 0f) windowStart = Time.unscaledTime;
            if (Time.unscaledTime - windowStart < ReportSeconds) return;
            windowStart = Time.unscaledTime;
            if (missingFrames > 0)
                Debug.Log(Log.Tag + " Unity's motion vectors not written, last "
                          + ReportSeconds.ToString("0", CultureInfo.InvariantCulture) + " s: in " + missingFrames
                          + " of " + checkedFrames + " checked frames, "
                          + (missingShare / missingFrames * 100.0).ToString("0.0", CultureInfo.InvariantCulture)
                          + "% of their pixels; the camera's own move written there.");
            checkedFrames = 0;
            missingFrames = 0;
            missingShare = 0.0;
        }

        // Each change once.
        // Logs the repair's state: on the flight camera, or why it is left out.
        private void SetState(string now)
        {
            if (now == state) return;
            state = now;
            Debug.Log(Log.Tag + " Motion vector repair: "
                      + (now == null ? "on the flight camera." : "left out -- " + now + "."));
        }
    }
}
