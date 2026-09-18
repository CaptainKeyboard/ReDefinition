using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Redirects a camera into a shared RenderTexture and puts everything back
    // the way it was afterwards.
    //
    // A smaller viewport (camera.rect) cannot scale KSP's image: KSP composes it
    // from several cameras, and with an image effect on one camera Unity renders
    // that camera into a temporary texture at camera size, which loses what the
    // cameras before it drew into the frame buffer. Camera 00 clears only depth,
    // so the result stays black and Unity reports "Dimensions of color surface
    // does not match dimensions of depth surface".
    //
    // So all 3D cameras get the same targetTexture at render resolution. They
    // compose there in their usual order, and the upscaler writes the result into
    // the frame buffer (docs/development/upscaler.md).
    public class CameraRedirect : MonoBehaviour
    {
        // The jitter has to reach every participating camera, not just the near
        // one: otherwise the starfield moves differently from the rocket and the
        // upscaler sees contradictory subpixel motion. The value applies to one
        // frame and is set by the rig.
        public static Vector2 JitterNdc;
        public static bool JitterActive;

        // The jitter is applied in OnPreRender. Scatterer's TemporalAntiAliasing
        // assigns projectionMatrix in its own OnPreCull on Camera 00 and Camera
        // ScaledSpace, and the order of two OnPreCull handlers is undefined;
        // OnPreRender runs after every OnPreCull. Unity culls and builds the shadow
        // cascades between OnPreCull and OnPreRender, so the shadows are built from
        // the unjittered matrix while the image renders offset.

        private Camera cam;
        private RenderTexture previousTarget;
        private RenderTexture target;
        private bool screenLent;
        private Matrix4x4 previousProjection;
        private bool projectionWasUnitys;
        private bool everTouchedProjection;
        private bool previousJitteredTransparents;
        private bool transparentFlagSaved;
        private bool projectionOverridden;
        private bool redirected;

        // For diagnostics: what was found and what was set while rendering.
        public Matrix4x4 CapturedProjection { get; private set; }
        public Matrix4x4 AppliedProjection { get; private set; }
        // The jitter in AppliedProjection, in normalized device coordinates.
        public Vector2 AppliedJitter { get; private set; }
        public bool DidJitter { get; private set; }

        // The matrix the camera had when rendering finished. Other code writes
        // camera.projectionMatrix too; where it overwrote the jitter, the upscaler
        // gets an image without subpixel offset while DidJitter still reports
        // true.
        public Matrix4x4 RenderedProjection { get; private set; }
        public bool CapturedRendered { get; private set; }

        // How often the jitter was applied and released within one frame. Catches
        // a camera rendering more than once per frame and a second CameraRedirect
        // on the same camera, either of which would confuse the reading above.
        public int ApplyCount { get; private set; }
        public int ReleaseCount { get; private set; }
        private int countedFrame = -1;

        public Camera Camera { get { return cam != null ? cam : (cam = GetComponent<Camera>()); } }

        private void Awake()
        {
            cam = GetComponent<Camera>();
        }

        // The camera keeps the rig's texture for as long as it is redirected.
        // Unity's mouse events, which skip cameras with a texture, reach it through
        // UnityMouseEvents.
        public void Redirect(RenderTexture target)
        {
            if (Camera == null || redirected) return;

            previousTarget = cam.targetTexture;
            this.target = target;
            cam.targetTexture = target;
            redirected = true;
        }

        // For UnityMouseEvents: the camera's own target back for the length of
        // Unity's mouse event pass. False when there is nothing to lend -- a
        // camera that rendered into a texture of its own before the redirect is
        // skipped by that pass anyway.
        public bool LendScreen()
        {
            if (!redirected || previousTarget != null || cam == null || cam.targetTexture != target) return false;

            cam.targetTexture = null;
            screenLent = true;
            return true;
        }

        public void TakeBackScreen()
        {
            if (!screenLent) return;
            screenLent = false;

            // Only if nothing took the camera meanwhile: a handler of the mouse
            // events may have ended the redirect (Restore) or set a target of
            // its own.
            if (redirected && cam != null && cam.targetTexture == null) cam.targetTexture = target;
        }

        // Cleared each frame, so the diagnostics show this frame's values.
        private void OnPreCull()
        {
            DidJitter = false;
            CapturedRendered = false;
        }

        // Immediately before rendering: KSP sets the projection matrices anew every
        // frame, and Unity recomputes the projection matrix between OnPreCull and
        // OnPreRender.
        private void OnPreRender()
        {
            EnsureJitter();
        }

        // The jitter for this render, applied now if it is not yet: for a component
        // that reads the projection in its own OnPreRender, which Unity may call
        // before this one's (EveCloudMotion). Once per render -- OnPreCull clears
        // DidJitter. True when the camera is jittered.
        internal bool EnsureJitter()
        {
            if (!DidJitter) ApplyJitter();
            return DidJitter;
        }

        private void ApplyJitter()
        {
            if (!JitterActive || Camera == null) return;

            CountFrame();
            ApplyCount++;

            previousProjection = cam.projectionMatrix;
            projectionWasUnitys = ProjectionIsUnitys(cam, previousProjection);
            projectionOverridden = true;
            everTouchedProjection = true;

            // Unity needs nonJitteredProjectionMatrix to produce correct motion
            // vectors despite the offset projection.
            cam.nonJitteredProjectionMatrix = previousProjection;
            cam.projectionMatrix =
                Matrix4x4.Translate(new Vector3(JitterNdc.x, JitterNdc.y, 0f)) * previousProjection;

            // Saved before the first assignment: KSP or another mod may have
            // changed it from its default, true.
            if (!transparentFlagSaved)
            {
                previousJitteredTransparents = cam.useJitteredProjectionMatrixForTransparentRendering;
                transparentFlagSaved = true;
            }
            cam.useJitteredProjectionMatrixForTransparentRendering = true;

            CapturedProjection = previousProjection;
            AppliedProjection = cam.projectionMatrix;
            AppliedJitter = JitterNdc;
            DidJitter = true;
        }

        private void OnPostRender()
        {
            // Read before restoring: the matrix the frame was rendered with,
            // whoever wrote it last.
            if (projectionOverridden && cam != null)
            {
                RenderedProjection = cam.projectionMatrix;
                CapturedRendered = true;
            }

            ReleaseJitter();
        }

        // Puts back what ApplyJitter found. Unity on Camera.projectionMatrix: "If
        // you change this matrix, the camera no longer updates its rendering based
        // on its fieldOfView. This lasts until you call ResetProjectionMatrix." A
        // saved matrix written back would freeze the camera at that projection --
        // the mouse wheel's zoom in IVA, which KSP makes through fieldOfView
        // (InternalCamera.UpdateState), would not arrive. So a projection Unity
        // computed is reset to Unity's, and one a script set is written back: KSP's
        // CameraOffCenter sets one in the editors every LateUpdate.
        private void ReleaseJitter()
        {
            if (cam == null || !projectionOverridden) return;

            CountFrame();
            ReleaseCount++;

            // Only while the matrix is still the jittered one. A component that
            // sets its own for the length of the render and resets it after --
            // Scatterer's TemporalAntiAliasing, in OnPreCull and OnPostRender --
            // has already put back what it wants; writing its jittered matrix
            // back over that would leave the camera frozen on it.
            if (cam.projectionMatrix == AppliedProjection)
            {
                if (projectionWasUnitys) cam.ResetProjectionMatrix();
                else cam.projectionMatrix = previousProjection;
            }
            projectionOverridden = false;
        }

        // Whether a projection is the one Unity computes from the camera's
        // field of view, aspect and clipping planes, or one a script set.
        // Unity has no getter for that; the matrix it would compute is the
        // test -- Matrix4x4's == allows 1e-5 per column (decompiled).
        internal static bool ProjectionIsUnitys(Camera camera, Matrix4x4 projection)
        {
            Matrix4x4 own = camera.orthographic
                ? Matrix4x4.Ortho(-camera.orthographicSize * camera.aspect, camera.orthographicSize * camera.aspect,
                                  -camera.orthographicSize, camera.orthographicSize,
                                  camera.nearClipPlane, camera.farClipPlane)
                : Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, camera.nearClipPlane, camera.farClipPlane);
            return own == projection;
        }

        private void CountFrame()
        {
            if (countedFrame == Time.frameCount) return;
            countedFrame = Time.frameCount;
            ApplyCount = 0;
            ReleaseCount = 0;
        }

        public void Restore()
        {
            if (cam == null) return;
            ReleaseJitter();

            // ReleaseJitter has put the projection back as it found it, Unity's
            // own or the script's. A camera left with the matrix of the last
            // rendered frame gets shadow cascades built against a frustum that no
            // longer matches the camera.
            if (everTouchedProjection)
            {
                // nonJitteredProjectionMatrix has no reset: once assigned, Unity no
                // longer computes it. Set to the projection, it stays in step with
                // it, and motion vectors for every other user of this camera do
                // not compute against a frozen view.
                cam.nonJitteredProjectionMatrix = cam.projectionMatrix;

                everTouchedProjection = false;
            }

            if (transparentFlagSaved)
            {
                cam.useJitteredProjectionMatrixForTransparentRendering = previousJitteredTransparents;
                transparentFlagSaved = false;
            }

            if (redirected)
            {
                cam.targetTexture = previousTarget;
                redirected = false;
                target = null;
                screenLent = false;
            }
        }

        private void OnDisable()
        {
            Restore();
        }
    }
}
