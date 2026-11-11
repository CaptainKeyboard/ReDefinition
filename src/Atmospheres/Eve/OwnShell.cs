using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The mod's own cloud shell in scaled space (brief C23, item 7): a sphere slightly above the planet that draws the orbit picture
    // (OrbitPicture: the field's coverage as grey cloud, flurries faint, a swirl from Moderate up) in the orbit and map view, for bodies
    // whose clouds are ours (cloudSource Ours) or that have no 2D layer of EVE's to carry the picture. It draws itself from the
    // module's tick and is also an IOrbitOverlayLayer, so that the orbit overlay host (D34) can call Draw instead.
    //
    // The sphere is a child of the body's scaled-space transform (layer 10, Scaled Scenery) and turns with it. Its picture is laid out
    // as the field's map (shift 0, not mirrored): where the scaled body's mesh has another zero meridian the shell finds the shift once
    // from the transform (EveMapLayout.Calibrate over the scaled transform) and the module paints the orbit picture with it.
    internal sealed class OwnShell : IOrbitOverlayLayer
    {
        internal const string ShaderName = "ReDefinition/AtmospheresMapPaintShell";

        // The shell stands this much above the planet's radius, as a share of it: above the terrain and below EVE's 2D layer.
        internal const float RadiusShare = 1.0012f;

        private readonly Func<CelestialBody, bool> wants;
        private readonly Func<RenderTexture> picture;
        private GameObject shell;
        private Material material;
        private CelestialBody body;
        private Shader shader;
        private int lastFrame = -1;
        private double lastShift;
        private bool lastMirrored;
        private bool calibrated;

        // The picture is taken from the painter when the shell draws; wants says whether the shell is for the body.
        internal OwnShell(Shader shader, Func<CelestialBody, bool> wants, Func<RenderTexture> picture)
        {
            this.shader = shader;
            this.wants = wants;
            this.picture = picture;
        }

        public string Name { get { return "ReDefinition Atmospheres cloud shell"; } }

        // After the planet and the ground cover of the overlay layers, before the markers.
        public int Order { get { return 40; } }

        // Whether the shell is shown now.
        internal bool Visible { get; private set; }

        // The layout of the picture the shell needs: where the field's map lies on the scaled body (found by Calibrate).
        internal double ShiftDeg { get { return lastShift; } }
        internal bool Mirrored { get { return lastMirrored; } }
        internal bool Calibrated { get { return calibrated; } }

        public bool Wants(CelestialBody b)
        {
            return b != null && wants(b);
        }

        public void Draw(CelestialBody b, bool focused, double ut)
        {
            Update(b, focused);
        }

        // Shows the shell for the body (created on first use) or hides it; once per frame.
        internal void Update(CelestialBody b, bool show)
        {
            if (lastFrame == Time.frameCount && ReferenceEquals(b, body)) return;
            lastFrame = Time.frameCount;
            if (b == null || !show || b.scaledBody == null)
            {
                Hide();
                return;
            }
            if (!ReferenceEquals(b, body) || shell == null) Build(b);
            if (shell == null) return;
            if (!calibrated)
            {
                // The picture cannot be laid on this body's scaled mesh (its pole axis is not the body's): no shell.
                Hide();
                return;
            }
            if (picture() == null)
            {
                // Built and calibrated; the module paints the picture next (the layout is known now) and the shell shows from then on.
                Hide();
                return;
            }
            material.SetTexture("_Picture", picture());
            Vector3 sun = SunDirection(b);
            material.SetVector("_SunDir", sun);
            if (!shell.activeSelf) shell.SetActive(true);
            Visible = true;
        }

        private void Build(CelestialBody b)
        {
            Destroy();
            if (shader == null) return;
            body = b;
            shell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            shell.name = "ReDefinition Atmospheres cloud shell " + b.name;
            // The collider by name: the physics module is not referenced.
            UnityEngine.Object.Destroy(shell.GetComponent("Collider"));
            shell.layer = 10;
            Transform parent = b.scaledBody.transform;
            shell.transform.SetParent(parent, false);
            shell.transform.localPosition = Vector3.zero;
            // The primitive sphere has the radius 0.5; the body's scaled transform has the scale that turns a unit into the planet.
            float planetScaled = (float)(b.Radius * ScaledSpace.InverseScaleFactor);
            float parentScale = parent.lossyScale.x > 0f ? parent.lossyScale.x : 1f;
            shell.transform.localScale = Vector3.one * (2f * planetScaled * RadiusShare / parentScale);
            material = new Material(shader) { name = "ReDefinition Atmospheres cloud shell", renderQueue = 3000 - 10 };
            shell.GetComponent<Renderer>().sharedMaterial = material;
            var renderer = shell.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            calibrated = Calibrate(b, out lastShift, out lastMirrored);
        }

        // Where the field's map lies on the scaled body's sphere, from three places.
        internal bool Calibrate(CelestialBody b, out double shiftDeg, out bool mirrored)
        {
            double worst;
            Transform scaled = b.scaledBody.transform;
            EveMapLayout.LayerOfPlace layerOf = delegate (double lat, double lon, out double u, out double v)
            {
                Vector3d world = b.GetWorldSurfacePosition(lat, lon, 0.0);
                Vector3 scaledPosition = ScaledSpace.LocalToScaledSpace(world);
                Vector3 local = scaled.InverseTransformPoint(scaledPosition).normalized;
                EveCoverageMath.Uv(local.x, local.y, local.z, out u, out v);
            };
            return EveMapLayout.Calibrate(layerOf, 1.0, out shiftDeg, out mirrored, out worst);
        }

        private static Vector3 SunDirection(CelestialBody b)
        {
            CelestialBody sun = FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 ? FlightGlobals.Bodies[0] : null;
            if (sun == null) return Vector3.up;
            return ((Vector3)(sun.scaledBody.transform.position - b.scaledBody.transform.position)).normalized;
        }

        internal void Hide()
        {
            if (shell != null && shell.activeSelf) shell.SetActive(false);
            Visible = false;
        }

        internal void Destroy()
        {
            if (shell != null) UnityEngine.Object.Destroy(shell);
            if (material != null) UnityEngine.Object.Destroy(material);
            shell = null;
            material = null;
            body = null;
            Visible = false;
            calibrated = false;
        }
    }
}
