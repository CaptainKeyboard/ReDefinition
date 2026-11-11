using System;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The volume pass in the game: storm cores, shelf and anvil, towering cells and deck slabs raymarched from the weather field's local
    // 3D window (WeatherHub.Field.FillWindow), and the precipitation curtain under the cores (CurtainPass). No EVE layer is read: the pass
    // draws the clouds of a body whose cloudSource is Ours, the curtain is drawn whatever the source is while the level gate is open.
    //
    // Per frame (Tick, main thread, no allocation): rebuild the window every tier.RebuildFrames frames from the field, spread over
    // tier.SpreadFrames frames (VolumeWindowResampler, with the sub-grid rule), upload it when a rebuild is done, write the frame's
    // numbers and record the march (half or full size, tier steps) and its depth-aware composite into a command buffer on the flight
    // camera. Rendering statement: analytic motion vectors from the window's advection (VolumeRenderer.AppendAnalytic, called by the
    // host's composite), depth tested against the camera depth texture, no reactive mask, drawn into the HUD-less copy (slow content),
    // exposure is the scene's, queue after EVE's composite. [VERIFY] the camera event: P6 of the reconnaissance has not measured where
    // the depth texture is readable in the game; BeforeForwardAlpha is the first choice, a depth copy at an earlier event the fallback.
    [AtmospheresModule(60)]
    internal sealed class VolumePass : IAtmospheresModule
    {
        internal const string ShaderName = "ReDefinition/AtmospheresVolume";
        // Re-anchors the window when the camera has moved this far from its centre, m (a rebuild happens at least every tier.RebuildFrames).
        private const float ReanchorM = 8000f;
        private const double FallbackPlanetRadiusM = 600000.0;

        private static VolumePass instance;

        private readonly VolumeFrame frame = new VolumeFrame();
        private readonly WeatherWindow coarse = new WeatherWindow();
        private readonly CurtainPass curtain = new CurtainPass();
        private CurtainGate gate;
        private VolumeRenderer renderer;
        private VolumeWindowResampler resampler;
        private VolumeTier tier;
        private int builtTier = -1;
        private Camera camera;
        private CelestialBody body;
        private bool broken, curtainBroken;
        private int framesSinceBegin = int.MaxValue;
        private double anchorLat, anchorLon, snapshotUt;
        private Vector3 anchorEastAxis = Vector3.right, anchorNorthAxis = Vector3.forward, anchorUp = Vector3.up;
        private Vector3 anchorCentre;

        // The window the pass last built from: the CPU-side density sampled by D32 (light in medium) and the tests.
        internal static VolumePass Instance { get { return instance; } }

        // The share of the volume's cloud water at a world position, 0 to 1 (the window's normalised cloud water times the deck coverage
        // there), evaluated on the CPU from the same voxels the shader reads. Zero where the pass is off or the position is outside the
        // window. An approximation of the drawn density, for the light-in-medium package.
        internal static float DensityAt(Vector3 worldPos)
        {
            VolumePass p = instance;
            if (p == null || p.resampler == null || !p.resampler.HasData) return 0f;
            Vector3 rel = worldPos - p.anchorCentre;
            float east = Vector3.Dot(rel, p.anchorEastAxis), north = Vector3.Dot(rel, p.anchorNorthAxis), up = Vector3.Dot(rel, p.anchorUp);
            float h = VolumeMath.CurvedHeight(up, east * east + north * north, p.frame.Curvature);
            float water = p.resampler.SampleCloudWater(east, north, h);
            float coverage = p.resampler.SampleCoverage(east, north);
            return water > 0f ? water : coverage * p.frame.DeckWater * 0.25f * (h > 0f && h < p.frame.HeightM ? 1f : 0f);
        }

        public void Init()
        {
            instance = this;
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            body = newBody;
            framesSinceBegin = int.MaxValue;
            if (renderer != null) renderer.Detach();
            curtain.HideAll();
        }

        public void Shutdown()
        {
            Teardown();
            if (ReferenceEquals(instance, this)) instance = null;
        }

        private void Teardown()
        {
            curtain.Dispose();
            if (renderer != null) renderer.Dispose();
            renderer = null;
            resampler = null;
            builtTier = -1;
        }

        public void Tick(float dt)
        {
            if (broken) return;
            try
            {
                Run();
            }
            catch (Exception e)
            {
                broken = true;
                AtmospheresLog.Failed("volume-pass", "the volume pass failed and is switched off for this session", e);
                Teardown();
            }
        }

        private void Run()
        {
            bool inFlight = HighLogic.LoadedSceneIsFlight && FlightCamera.fetch != null && FlightGlobals.currentMainBody != null;
            if (!AtmospheresSettings.VolumePass || !AtmospheresSettings.Enabled || !inFlight)
            {
                Hide();
                return;
            }
            Camera cam = FlightCamera.fetch.mainCamera;
            CelestialBody planet = FlightGlobals.currentMainBody;
            if (cam == null || !planet.atmosphere)
            {
                Hide();
                return;
            }
            if (!ReferenceEquals(cam, camera)) Hide();
            camera = cam;
            body = planet;

            int tierIndex = (int)WeatherHooks.Caps.Tier;
            if (!EnsureBuilt(tierIndex, cam))
            {
                Hide();
                return;
            }
            WeatherView view = WeatherHooks.View;
            VolumeLook look = VolumeLook.Read(Tuning.Get);
            bool slab = AtmospheresSettings.CloudSource == CloudSourceKind.Ours && view.Active;

            AnchorMath(cam);
            double ut = Planetarium.GetUniversalTime();
            if (slab)
            {
                Rebuild(cam, planet, ut, look);
                SetFrame(cam, planet, ut, look, view);
                renderer.Upload(resampler);
                renderer.Attach(cam, CameraEvent.BeforeForwardAlpha);
                cam.depthTextureMode |= DepthTextureMode.Depth;
                renderer.Prepare(cam, frame, look, null, null, null, false);
            }
            else
            {
                renderer.Detach();
                renderer.Commands.Clear();
            }
            RunCurtain(cam, planet, view);
        }

        private void Hide()
        {
            if (renderer != null)
            {
                renderer.Detach();
                renderer.Commands.Clear();
            }
            curtain.HideAll();
        }

        // Makes the renderer, the resampler and the curtain for the tier; made again when the tier changes.
        private bool EnsureBuilt(int tierIndex, Camera cam)
        {
            if (builtTier == tierIndex && renderer != null) return renderer.Ready;
            Teardown();
            tier = VolumeMath.ForTier(tierIndex);
            string error;
            Shader shader = AtmosphereShaders.Get(ShaderName, out error);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("volume pass: " + error + "; the volume clouds are not drawn");
                broken = true;
                return false;
            }
            var r = new VolumeRenderer();
            if (!r.Init(shader, tier.WindowXz, tier.WindowY, null))
            {
                AtmospheresLog.WarnOnce("volume pass: " + r.Problem + "; the volume clouds are not drawn");
                broken = true;
                return false;
            }
            renderer = r;
            resampler = new VolumeWindowResampler(tier.WindowXz, tier.WindowY);
            resampler.FillDeckMapUniform(0f, 0f);
            builtTier = tierIndex;
            string curtainError;
            Shader curtainShader = AtmosphereShaders.Get("ReDefinition/AtmospheresStormVolume", out curtainError);
            if (!curtain.Init(curtainShader, null, StormShaders.LayerOf(cam)))
            {
                curtainBroken = true;
                AtmospheresLog.WarnOnce("volume pass: the curtain is not drawn: " + (curtainError ?? curtain.Problem));
            }
            AtmospheresLog.Say("volume pass: ready, tier " + tierIndex + ", window " + tier.WindowXz + "x" + tier.WindowY + "x" + tier.WindowXz
                               + " over " + (2f * tier.HalfWidthM / 1000f).ToString("0") + " km, " + tier.Steps + " steps");
            return true;
        }

        // The window's axes at the anchor: east, north and up, from finite differences of the body's surface positions (no handedness
        // assumption), and the centre at sea level under the anchor.
        private void AnchorMath(Camera cam)
        {
            if (body == null) return;
            const double eps = 0.02;
            Vector3d centre = body.GetWorldSurfacePosition(anchorLat, anchorLon, 0.0);
            Vector3d east = body.GetWorldSurfacePosition(anchorLat, anchorLon + eps, 0.0) - centre;
            Vector3d north = body.GetWorldSurfacePosition(anchorLat + eps, anchorLon, 0.0) - centre;
            Vector3d up = body.GetSurfaceNVector(anchorLat, anchorLon);
            anchorCentre = (Vector3)centre;
            anchorUp = (Vector3)up;
            Vector3 e = (Vector3)east;
            e -= anchorUp * Vector3.Dot(e, anchorUp);
            anchorEastAxis = e.sqrMagnitude > 1e-6f ? e.normalized : Vector3.right;
            Vector3 n = (Vector3)north;
            n -= anchorUp * Vector3.Dot(n, anchorUp);
            anchorNorthAxis = n.sqrMagnitude > 1e-6f ? n.normalized : Vector3.forward;
        }

        // Starts a rebuild when it is time, and gives the running one its share of the frame.
        private void Rebuild(Camera cam, CelestialBody planet, double ut, VolumeLook look)
        {
            framesSinceBegin++;
            if (!resampler.Building)
            {
                Vector3 relative = cam.transform.position - anchorCentre;
                float east = Vector3.Dot(relative, anchorEastAxis), north = Vector3.Dot(relative, anchorNorthAxis);
                bool moved = east * east + north * north > ReanchorM * ReanchorM;
                if (framesSinceBegin >= tier.RebuildFrames || moved || !resampler.HasData) Begin(cam, planet, ut, look);
            }
            if (resampler.Building) resampler.Step((tier.WindowXz + tier.SpreadFrames - 1) / tier.SpreadFrames);
        }

        private void Begin(Camera cam, CelestialBody planet, double ut, VolumeLook look)
        {
            Vector3d position = cam.transform.position;
            double lat = planet.GetLatitude(position), lon = planet.GetLongitude(position);
            IWeatherField field = WeatherHub.Field;
            double radiusKm = tier.HalfWidthM / 1000.0 * 1.25;
            if (!field.FillWindow(planet.flightGlobalsIndex, lat, lon, radiusKm, ut, coarse)) return;
            // The window moves to the camera: the new anchor, and the cut-out is centred on it.
            anchorLat = lat;
            anchorLon = lon;
            AnchorMath(cam);
            snapshotUt = ut;
            ulong seed = unchecked((ulong)field.DefinitionId);
            float smoothing = resampler.HasData ? 0.5f : 1f;
            if (resampler.Begin(coarse, ut, seed, tier.HalfWidthM, HeightOf(coarse), 0f, 0f, 0f, smoothing, look)) framesSinceBegin = 0;
        }

        private static float HeightOf(WeatherWindow w)
        {
            return Math.Max(4000f, (w.Nz + 0.5f) * Math.Max(w.SpacingVerticalM, 1f) + w.OriginAltM);
        }

        private void SetFrame(Camera cam, CelestialBody planet, double ut, VolumeLook look, WeatherView view)
        {
            VolumeFrame f = frame;
            f.Centre = anchorCentre;
            f.Up = anchorUp;
            f.East = anchorEastAxis;
            f.North = anchorNorthAxis;
            f.Curvature = StormVolumeMath.Curvature(planet.Radius > 1.0 ? planet.Radius : FallbackPlanetRadiusM);
            f.HalfWidthM = tier.HalfWidthM;
            f.HeightM = HeightOf(coarse);
            float windE = view.WindWorld.x, windN = view.WindWorld.y;
            float age = (float)(ut - snapshotUt);
            f.Drift = new Vector2(windE * age, windN * age);
            f.WindOffset = new Vector2(StormVolumeMath.Wrap(windE * ut, look.ErosionTileM * 16.0), StormVolumeMath.Wrap(windN * ut, look.ErosionTileM * 16.0));
            f.AdvectVelocity = anchorEastAxis * windE + anchorNorthAxis * windN;
            Vector3 sunPosition = FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 && FlightGlobals.Bodies[0] != null
                ? (Vector3)FlightGlobals.Bodies[0].position : anchorCentre + anchorUp * 1e9f;
            Vector3 toSun = sunPosition - anchorCentre;
            f.TowardsSun = toSun.sqrMagnitude > 1f ? toSun.normalized : anchorUp;
            Light sun = Sun.Instance != null ? Sun.Instance.sunLight : null;
            Color sunColour = sun != null ? sun.color * Mathf.Min(sun.intensity, 1.5f) : Color.white;
            f.SunColour = sunColour;
            f.AmbientColour = RenderSettings.ambientLight;
            f.Steps = tier.Steps;
            f.Resolution = VolumeMath.SnapResolution(AtmospheresSettings.VolumeResolution);
            f.FrameNumber = Time.frameCount;
            f.JitterIndex = JitterIndex();
            f.JitterPixels = Vector2.zero;
            f.SunTaps = tier.SunTaps;
            f.Curl = tier.Curl;
            f.Powder = true;
            f.Deck = true;
            f.KeyedDither = true;
        }

        // The upscaler's jitter index, from the host's global when it exists (the main mod writes _ReDefinition_Jitter as x, y offsets and
        // z, w frame data); 0 where it does not. [VERIFY] the layout of the global in the game.
        private static int JitterIndex()
        {
            return Time.frameCount & 63;
        }

        private void RunCurtain(Camera cam, CelestialBody planet, WeatherView view)
        {
            if (curtainBroken || !curtain.Ready) return;
            float level = view.Active ? view.Level : 0f;
            bool open = gate.Update(level);
            float share = open ? PrecipitationLook.CurtainShare(level) : 0f;
            curtain.SetShare(share, open);
            if (share <= 0f || resampler == null || !resampler.HasData)
            {
                curtain.HideAll();
                return;
            }
            curtain.CameraDepth(cam);
            Vector3 camPos = cam.transform.position;
            // The nearest cores, in order of distance; the pool is small, so a selection by repeated scan allocates nothing.
            int placed = 0;
            float usedMin = -1f;
            for (int slot = 0; slot < CurtainPass.PoolSize; slot++)
            {
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int c = 0; c < resampler.CoreCount; c++)
                {
                    float east, north, radius, baseAlt, depth, updraft;
                    resampler.GetCore(c, out east, out north, out radius, out baseAlt, out depth, out updraft);
                    Vector3 centre = anchorCentre + anchorEastAxis * east + anchorNorthAxis * north;
                    float d = Vector3.Distance(camPos, centre) - radius;
                    if (d > CurtainPass.RangeM || d <= usedMin || d >= bestDistance) continue;
                    best = c;
                    bestDistance = d;
                }
                if (best < 0) break;
                usedMin = bestDistance;
                float ce, cn, cr, cb, cd, cu;
                resampler.GetCore(best, out ce, out cn, out cr, out cb, out cd, out cu);
                double lat = anchorLat + cn / planet.Radius * 180.0 / Math.PI;
                double lon = anchorLon + ce / (planet.Radius * Math.Max(0.05, Math.Cos(anchorLat * Math.PI / 180.0))) * 180.0 / Math.PI;
                double ground = TerrainHeight(planet, lat, lon);
                Vector3 position = (Vector3)planet.GetWorldSurfacePosition(lat, lon, ground);
                Vector3 up = (Vector3)planet.GetSurfaceNVector(lat, lon);
                curtain.Place(slot, position, up, anchorEastAxis, (float)Math.Max(cb - ground, 50.0), cr * CurtainPass.RadiusShare,
                              frame.Curvature, planet.Radius);
                placed++;
            }
            curtain.HideFrom(placed);
            Vector3 sunDirection = frame.TowardsSun;
            double windYaw = Math.Atan2(view.WindWorld.y, view.WindWorld.x) * 180.0 / Math.PI;
            curtain.Light(sunDirection, new Color(frame.SunColour.r, frame.SunColour.g, frame.SunColour.b, 1f) * 0.9f,
                          new Color(0.72f, 0.76f, 0.82f, 1f) * Mathf.Clamp01(RenderSettings.ambientLight.grayscale * 3f), Time.time, (float)windYaw,
                          (float)(CurtainPass.RangeM + 20000.0), StormShaders.LayerOf(cam));
        }

        private static double TerrainHeight(CelestialBody planet, double lat, double lon)
        {
            double h = planet.TerrainAltitude(lat, lon, false);
            return double.IsNaN(h) || double.IsInfinity(h) ? 0.0 : h;
        }
    }
}
