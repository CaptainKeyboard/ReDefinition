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

        // The map view's fade (P69, package T06): the shell is hidden while the map camera is closer to the body than 2.5 radii and fully
        // shown from 3.5 radii, smooth between.
        internal const double MapFadeFromRadii = 2.5, MapFadeToRadii = 3.5;

        // Below this opacity the shell is not drawn at all.
        internal const float MinVisibleOpacity = 0.004f;

        // The opacity of the shell for the camera (package T06): it is the far picture of the same layers the near march draws, so its weight is
        // what the near march leaves, a smooth cross-fade through the hand-over band (FarViewMath.NearWeight) and not a second layer drawn over
        // the first. Where the near march is not drawing (nearMarchActive false: the own clouds are off or the cloud source is not ours) the shell
        // is the only picture and stays at full weight at any altitude. In the map view the near march is off and the fade of the map view applies.
        internal static float Opacity(double cameraAltitudeM, double bodyRadiusM, bool nearMarchActive, bool mapView, double mapDistanceRadii, bool mapFadeOn)
        {
            double weight = 1.0;
            if (mapView)
            {
                if (mapFadeOn) weight = FarViewMath.MapFade(mapDistanceRadii, MapFadeFromRadii, MapFadeToRadii);
            }
            else if (nearMarchActive)
            {
                weight = 1.0 - FarViewMath.NearWeight(cameraAltitudeM, bodyRadiusM * FarViewMath.BandStartRadii, bodyRadiusM * FarViewMath.BandEndRadii);
            }
            return (float)weight;
        }

        // Whether the near march draws for the body (the integration sets it; false: the shell is the only picture) and whether the map view fades the
        // shell (off until the owner agrees to the setting cloudFarMapFade: the shell stays at full weight in the map view as it was; the field is the
        // override of a caller that sets it).
        internal bool NearMarchActive;
        internal bool MapFadeEnabled;

        // The shell's opacity now: from the flight camera's altitude over the body, or in the map view and the tracking station from the map
        // camera's distance in body radii.
        private float CurrentOpacity(CelestialBody b)
        {
            bool map = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION;
            double altitude = 0.0, radii = 0.0;
            if (map)
            {
                // [VERIFY] GetCameraTransform is the map camera's transform in scaled space (the unit of the body's scaled transform).
                Transform planetarium = PlanetariumCamera.fetch != null ? PlanetariumCamera.fetch.GetCameraTransform() : null;
                if (planetarium != null)
                    radii = (planetarium.position - b.scaledBody.transform.position).magnitude / (b.Radius * ScaledSpace.InverseScaleFactor);
            }
            else
            {
                Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                if (flight != null) altitude = ((Vector3d)flight.transform.position - b.position).magnitude - b.Radius;
            }
            return Opacity(altitude, b.Radius, NearMarchActive, map, radii, MapFadeEnabled || AtmospheresSettings.CloudFarMapFade);
        }

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
            // The far picture's weight (package T06): hidden where the near march or the map fade leaves nothing of it.
            float opacity = CurrentOpacity(b);
            if (opacity < MinVisibleOpacity)
            {
                Hide();
                return;
            }
            material.SetTexture("_Picture", picture());
            material.SetFloat("_Opacity", opacity);
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
            CelestialBody sun = GameGlobals.Sun;
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
