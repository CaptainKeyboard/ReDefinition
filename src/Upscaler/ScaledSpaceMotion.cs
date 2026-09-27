using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Shared;
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
    // KSP places and turns every scaled body in LateUpdate (ScaledMovement:
    // position from ScaledSpace.LocalToScaledSpace, rotation from the body's), and
    // hides a faded one by disabling its renderer (ScaledSpaceFader, decompiled).
    // As the scene camera culls, after LateUpdate, the bodies in view are taken
    // with their transforms and the scaled camera's view and projection. A pass of
    // the cloud motion shader then follows the scaled camera's ray through every
    // pixel Camera 00 left empty to the nearest body it meets, on the surface or,
    // passing it, in the atmosphere around it, moves that point back by the body's
    // change of transform since the frame before, and writes the difference of the
    // two projections in Unity's encoding. EVE's clouds are blended over the
    // result (CloudMotionVectors). The scaled camera itself is left as it is.
    //
    // What a shader moves -- EVE's two-dimensional cloud layers drifting over the
    // planet -- keeps the planet's motion under it.
    //
    // In KSP, Camera 00's motion vector buffer is at times not written for many
    // frames: the ground then carries the same vectors frame after frame, whatever
    // the camera does (measured in the recordings' frame generation inputs after
    // zooming out to the farthest and back, and in the audit from the start of a
    // flight). Once copied, the buffer is therefore cleared to Missing. Where the next
    // frame's copy still holds it, Unity has not written that pixel, and pass 2 takes
    // the camera's own move instead: against the main body where something is drawn
    // (SceneMotion), the camera's turn where nothing is. A row of the copy is read
    // back every CheckEvery frames and the share of such pixels logged every ten
    // seconds while there are any.
    internal sealed class ScaledSpaceMotion
    {
        private const string CameraName = "Camera ScaledSpace";
        private const int MergePass = 2;
        private const int MaxBodies = 8;

        private static readonly int UnityMotionId = Shader.PropertyToID("_ReDefinitionUnityMotion");
        private static readonly int SceneDepthId = Shader.PropertyToID("_ReDefinitionCloudSceneDepth");
        private static readonly int TexelId = Shader.PropertyToID("_ReDefinitionCloudTexel");
        private static readonly int SpheresId = Shader.PropertyToID("_ReDefinitionBodySpheres");
        private static readonly int MotionsId = Shader.PropertyToID("_ReDefinitionBodyMotions");
        private static readonly int BodyCountId = Shader.PropertyToID("_ReDefinitionBodyCount");
        private static readonly int ViewProjectionId = Shader.PropertyToID("_ReDefinitionScaledViewProjection");
        private static readonly int PreviousViewProjectionId = Shader.PropertyToID("_ReDefinitionScaledPreviousViewProjection");
        private static readonly int InverseViewProjectionId = Shader.PropertyToID("_ReDefinitionScaledInverseViewProjection");
        private static readonly int CameraPositionId = Shader.PropertyToID("_ReDefinitionScaledCameraPosition");
        private static readonly int SceneClipToPrevClipId = Shader.PropertyToID("_ReDefinitionSceneClipToPrevClip");
        private static readonly int SceneRangeId = Shader.PropertyToID("_ReDefinitionSceneRange");
        private static readonly int SkyClipToPrevClipId = Shader.PropertyToID("_ReDefinitionSkyClipToPrevClip");

        // From this distance on, the scene camera's own motion vectors are
        // replaced by those of points fixed to the main body (SceneMotion), while
        // the camera stands farther than FarFromOrigin from the world's origin.
        // Nearer to the camera, other vessels, kerbals and debris keep Unity's,
        // which moves them as objects; the active vessel is repaired after this in
        // any case (VesselMotionVectors). Nearer to the origin, Unity's are precise
        // and stay everywhere: a vessel in orbit a kilometre from the active one
        // would otherwise move by the camera's travel against the body. Beyond it,
        // such a vessel is at least that far from the camera and a few pixels
        // large.
        private const float PreciseFrom = 500f;
        private const float FarFromOrigin = 20000f;

        private const string NotLogged = "not logged";
        private static string loggedState = NotLogged;

        // Far outside the range of a motion vector in viewport units; half precision
        // holds it exactly. The shader tests for below -50.
        private static readonly Color Missing = new Color(-100f, -100f, 0f, 0f);
        private const int CheckEvery = 30;
        private const float ReportSeconds = 10f;
        private int checkedFrames, missingFrames;
        private double missingShare;
        private float windowStart = -1f;

        private struct Body
        {
            internal float AngularSize;
            internal Vector4 Sphere;
            internal float Outer;
            internal Matrix4x4 Motion;
        }

        private readonly Dictionary<CelestialBody, Matrix4x4> previousLocalToWorld = new Dictionary<CelestialBody, Matrix4x4>();
        private readonly List<Body> bodies = new List<Body>();
        private readonly Vector4[] spheres = new Vector4[MaxBodies * 2];
        private readonly Matrix4x4[] motions = new Matrix4x4[MaxBodies];
        private static readonly System.Comparison<Body> Larger = (a, b) => b.AngularSize.CompareTo(a.AngularSize);

        private Camera camera;
        private RenderTexture scene;
        private RenderTexture merged;
        private Material material;
        private Shader outdatedShader;
        private Matrix4x4 previousViewProjection;
        private bool havePrevious;
        private Matrix4x4 previousSceneProjection;
        private Vector3d previousScenePosition;
        private Quaternion previousSceneRotation;
        private Quaternion previousSceneWorldRotation;
        private CelestialBody previousSceneBody;
        private int previousSceneFrame = -1;
        private bool haveScenePrevious;
        private int updatedFrame = -1;

        // Whether this frame's capture takes the distant planets' motion.
        internal bool Active { get; private set; }

        // Why not, for the diagnostics; null while it does.
        internal string State { get; private set; }

        // The merged motion vectors, for the clouds to be blended over.
        internal RenderTexture Merged
        {
            get { return merged; }
        }

        // Once the cameras are redirected: the scaled camera among them, whose
        // drawing is in the rig's image.
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
            Active = true;
        }

        // In the scene camera's capture: its motion vectors into a copy, and the
        // distant planets' over them where it drew nothing.
        // `clearAfterCopy`: false while TUFX's effects run on the scene camera before the
        // capture, whose motion blur reads Unity's buffer and would take Missing for a
        // motion.
        internal void Merge(CommandBuffer capture, RenderTexture depth, Vector2Int size, bool clearAfterCopy)
        {
            capture.Blit(BuiltinRenderTextureType.MotionVectors, scene);
            if (clearAfterCopy)
            {
                capture.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
                capture.ClearRenderTarget(false, true, Missing);
                if (Time.frameCount % CheckEvery == 0) CheckMissing(capture, size);
            }
            ReportMissing();
            capture.SetGlobalVector(TexelId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
            capture.SetGlobalTexture(UnityMotionId, scene);
            capture.SetGlobalTexture(SceneDepthId, depth);
            capture.Blit(Texture2D.blackTexture, merged, material, MergePass);
        }

        // A row across the middle of the copy: the share of pixels Unity left unwritten.
        private void CheckMissing(CommandBuffer capture, Vector2Int size)
        {
            capture.RequestAsyncReadback(scene, 0, 0, size.x, size.y / 2, 1, 0, 1, request =>
            {
                if (request.hasError) return;
                Unity.Collections.NativeArray<ushort> data = request.GetData<ushort>();
                int missing = 0, pixels = data.Length / 2;
                for (int i = 0; i < pixels; i++)
                    if (Mathf.HalfToFloat(data[i * 2]) < -50f) missing++;
                checkedFrames++;
                if (missing > 0) missingFrames++;
                missingShare += pixels > 0 ? (double)missing / pixels : 0.0;
            });
        }

        private void ReportMissing()
        {
            if (windowStart < 0f) windowStart = Time.unscaledTime;
            if (Time.unscaledTime - windowStart < ReportSeconds) return;
            windowStart = Time.unscaledTime;
            if (missingFrames > 0)
                Debug.Log(Log.Tag + " Unity's motion vectors not written, last " + ReportSeconds.ToString("0")
                          + " s: in " + missingFrames + " of " + checkedFrames + " checked frames, "
                          + (missingShare / missingFrames * 100.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                          + "% of their pixels on average; the camera's own move taken there.");
            checkedFrames = 0;
            missingFrames = 0;
            missingShare = 0.0;
        }

        // As the scene camera culls, once a frame: the bodies in view and the
        // scaled camera, as this frame draws them, and the scene camera's move
        // against the main body. Globals, read by the capture at the scene
        // camera's BeforeImageEffects.
        internal void Update(Camera scene)
        {
            if (!Active || camera == null || updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;
            UpdateScene(scene);

            // Its jitter is taken back by now: the scaled camera rendered before
            // the scene camera culls (CameraRedirect.OnPostRender).
            Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false)
                                       * camera.worldToCameraMatrix;
            if (!havePrevious) previousViewProjection = viewProjection;
            Vector3 cameraPosition = camera.transform.position;

            bodies.Clear();
            List<CelestialBody> all = FlightGlobals.Bodies;
            if (all != null)
            {
                foreach (CelestialBody body in all)
                {
                    if (body == null || body.scaledBody == null) continue;
                    Transform transform = body.scaledBody.transform;
                    Matrix4x4 now = transform.localToWorldMatrix;
                    Matrix4x4 before;
                    bool known = previousLocalToWorld.TryGetValue(body, out before);
                    previousLocalToWorld[body] = now;

                    Renderer renderer = body.scaledBody.GetComponent<Renderer>();
                    if (renderer == null || !renderer.enabled || !body.scaledBody.activeInHierarchy) continue;
                    float radius = (float)(body.Radius * ScaledSpace.InverseScaleFactor);
                    float outer = body.atmosphere
                        ? (float)((body.Radius + body.atmosphereDepth) * ScaledSpace.InverseScaleFactor)
                        : radius;
                    Vector3 center = transform.position;
                    float distance = (center - cameraPosition).magnitude;
                    // Inside the body the near scene draws it; inside its
                    // atmosphere the empty pixels are sky, which turns with the
                    // camera.
                    if (distance <= radius) continue;
                    bodies.Add(new Body
                    {
                        AngularSize = outer / distance,
                        Sphere = new Vector4(center.x, center.y, center.z, radius),
                        Outer = distance > outer ? outer : 0f,
                        Motion = known ? before * now.inverse : Matrix4x4.identity,
                    });
                }
            }
            bodies.Sort(Larger);

            int count = Mathf.Min(bodies.Count, MaxBodies);
            for (int i = 0; i < MaxBodies; i++)
            {
                Body body = i < count ? bodies[i] : default(Body);
                spheres[i] = body.Sphere;
                spheres[MaxBodies + i] = new Vector4(body.Outer, 0f, 0f, 0f);
                motions[i] = i < count ? body.Motion : Matrix4x4.identity;
            }
            Shader.SetGlobalVectorArray(SpheresId, spheres);
            Shader.SetGlobalMatrixArray(MotionsId, motions);
            Shader.SetGlobalFloat(BodyCountId, count);
            Shader.SetGlobalMatrix(ViewProjectionId, viewProjection);
            Shader.SetGlobalMatrix(PreviousViewProjectionId, previousViewProjection);
            Shader.SetGlobalMatrix(InverseViewProjectionId, viewProjection.inverse);
            Shader.SetGlobalVector(CameraPositionId, cameraPosition);

            previousViewProjection = viewProjection;
            havePrevious = true;
        }

        // The scene camera's clip space into the frame before's, against the main
        // body (SceneMotion); off where there is no body or no frame before to
        // compare with, and across a change of body.
        private void UpdateScene(Camera scene)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (scene == null || body == null)
            {
                haveScenePrevious = false;
                Shader.SetGlobalVector(SceneRangeId, Vector4.zero);
                return;
            }
            // OnPreCull: CameraRedirect jitters the projection only in OnPreRender.
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(scene.projectionMatrix, false);
            Quaternion toBody = SceneMotion.Conjugate(body.transform.rotation);
            // Where the body's ground is drawn this frame: its transform, which KSP
            // places with the scene.
            Vector3d position = SceneMotion.Rotate(toBody,
                (Vector3d)scene.transform.position - (Vector3d)body.transform.position);
            Quaternion rotation = toBody * scene.transform.rotation;

            Quaternion worldRotation = scene.transform.rotation;

            bool ready = haveScenePrevious && previousSceneBody == body
                         && previousSceneFrame == Time.frameCount - 1 && SharedFrame.RigResetReason == null;
            Matrix4x4 clipToPrevious = ready
                ? SceneMotion.ClipToPreviousClip(projection, previousSceneProjection, position, rotation,
                    previousScenePosition, previousSceneRotation)
                : Matrix4x4.identity;
            // Where nothing is drawn and no body met, the sky: at infinity, moved
            // only by the camera's turn.
            Matrix4x4 skyToPrevious = ready
                ? SceneMotion.ClipToPreviousClip(projection, previousSceneProjection, Vector3d.zero, worldRotation,
                    Vector3d.zero, previousSceneWorldRotation)
                : Matrix4x4.identity;
            bool far = scene.transform.position.magnitude > FarFromOrigin;
            Shader.SetGlobalMatrix(SceneClipToPrevClipId, clipToPrevious);
            Shader.SetGlobalMatrix(SkyClipToPrevClipId, skyToPrevious);
            Shader.SetGlobalVector(SceneRangeId, new Vector4(scene.nearClipPlane, scene.farClipPlane,
                far ? PreciseFrom : float.MaxValue, ready ? 1f : 0f));

            previousSceneProjection = projection;
            previousScenePosition = position;
            previousSceneRotation = rotation;
            previousSceneWorldRotation = worldRotation;
            previousSceneBody = body;
            previousSceneFrame = Time.frameCount;
            haveScenePrevious = true;
        }

        internal void Detach()
        {
            Active = false;
            camera = null;
            havePrevious = false;
            haveScenePrevious = false;
            previousSceneBody = null;
            Shader.SetGlobalVector(SceneRangeId, Vector4.zero);
            updatedFrame = -1;
            previousLocalToWorld.Clear();
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
            if (!UpscalerMasks.Ensure(ref scene, size, RenderTextureFormat.RGHalf, "ReDefinition_SceneMotion", out created)
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
                      + (State == null ? "computed from the scaled bodies' transforms where the scene camera draws nothing."
                                       : "left out -- " + State + "."));
        }
    }
}
