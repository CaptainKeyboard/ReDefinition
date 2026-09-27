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

        private const string NotLogged = "not logged";
        private static string loggedState = NotLogged;

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
        internal void Merge(CommandBuffer capture, RenderTexture depth, Vector2Int size)
        {
            capture.Blit(BuiltinRenderTextureType.MotionVectors, scene);
            capture.SetGlobalVector(TexelId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
            capture.SetGlobalTexture(UnityMotionId, scene);
            capture.SetGlobalTexture(SceneDepthId, depth);
            capture.Blit(Texture2D.blackTexture, merged, material, MergePass);
        }

        // As the scene camera culls, once a frame: the bodies in view and the
        // scaled camera, as this frame draws them. Globals, read by the capture at
        // the scene camera's BeforeImageEffects.
        internal void Update()
        {
            if (!Active || camera == null || updatedFrame == Time.frameCount) return;
            updatedFrame = Time.frameCount;

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

        internal void Detach()
        {
            Active = false;
            camera = null;
            havePrevious = false;
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
