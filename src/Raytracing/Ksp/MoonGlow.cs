using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The moons of the planet the camera is at made to stand out in the sky. The pixels their sphere covers are brighter,
    // the more the darker the sky. It is the moon as the game draws it, its phase and its craters kept, with no glow
    // past its edge. A moon in the planet's shadow is darkened, and its dark side gets its planet's light. Moons only:
    // at a moon its planet is no moon of the sky's. Where the main body, or at a moon its
    // planet, stands in front of a moon, the shader leaves those pixels out, and so where
    // Scatterer's ocean, which the depth texture does not hold, covers the sky. On the
    // flight's scene camera, where it sees sky, before the transparent pass, so that
    // clouds and the atmosphere draw over it (ReDefinitionMoonGlow.shader). It lights
    // nothing: the moons' light on the scene is the raytracing's (RtReflectors). The
    // player, 2026-10-03: the moon "should stand out more when you look at it, not make
    // more light".
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MoonGlow : MonoBehaviour
    {
        // Drawn before the transparent pass; the shader's inputs; the blockers' array handed to it each frame.
        private const CameraEvent Event = CameraEvent.BeforeForwardAlpha;
        private static readonly int MoonId = Shader.PropertyToID("_ReDefinitionMoon");
        private static readonly int GainId = Shader.PropertyToID("_ReDefinitionMoonGain");
        private static readonly int PlanetId = Shader.PropertyToID("_ReDefinitionMoonPlanet");
        private static readonly int SunId = Shader.PropertyToID("_ReDefinitionMoonSun");
        private static readonly int EarthLightId = Shader.PropertyToID("_ReDefinitionMoonEarthLight");
        // The passes of the shader: the moon's pixels times a gain, then its planet's light added.
        private const int GainPass = 0, EarthPass = 1;
        // How many times as bright a moon's lit pixels are at full night. The eye, adapted to the dark, sees a moon
        // far brighter than the scene's light makes it. A gain of 2.1 gave the Mun's lit part the mean brightness of the glow
        // this replaced. The player chose 3.2 on 2026-10-27 from pictures at 2.1, 3.2 and 4.5.
        private const float NightGain = 3.2f;
        // The smallest angular radius of a moon that is touched, in radians: half a pixel at 1080 lines and 60 degrees.
        private const float LeastRadius = 0.5f * 1.0472f / 1080f;
        private static readonly int BlockersId = Shader.PropertyToID("_ReDefinitionMoonBlockers");
        private static readonly int OceanId = Shader.PropertyToID("_ReDefinitionMoonOcean");
        private static readonly int DistanceId = Shader.PropertyToID("_ReDefinitionMoonDistance");
        private static readonly int RectId = Shader.PropertyToID("_ReDefinitionMoonRect");
        // The gain below which a moon is left as the game draws it, and the points on the cone round a disc whose projection
        // gives the rectangle that holds it.
        private const float LeastGain = 0.01f;
        private const int RimPoints = 16;
        private readonly Vector4[] blockers = new Vector4[2];
        // Whether the camera's depth texture was asked for by this, to take back.
        private bool depthAdded;

        // For the probes: no glow, to compare; a gain in place of NightGain where not negative; no earthshine.
        internal static bool HeldOff;
        internal static float GainOverride = -1f;
        internal static bool NoEarthshine;

        // The glow's material, the quad it draws over the screen, the command buffer, and the camera it is on.
        private Material material;
        private Mesh screen;
        private CommandBuffer command;
        private Camera attached;
        // Made in Start: Unity does not allow a MaterialPropertyBlock in a MonoBehaviour's
        // constructor, where a field initializer runs (a development player throws, and
        // the component's other fields stay empty).
        private MaterialPropertyBlock properties;
        private readonly List<CelestialBody> moons = new List<CelestialBody>();
        // The camera's planet's system as found, the planet and the camera's body among them: what can stand before a moon's sun.
        private readonly List<CelestialBody> system = new List<CelestialBody>();

        // Loads the shader and makes the full-screen quad and the command buffer; without the shader the add-on stays off.
        private void Start()
        {
            properties = new MaterialPropertyBlock();
            string problem;
            Shader shader = ShaderBundle.LoadShader("Hidden/ReDefinition/MoonGlow", out problem);
            if (shader == null)
            {
                Debug.LogWarning(Log.Tag + " The moons' glow does not run: " + problem);
                enabled = false;
                return;
            }
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            screen = RtScreenQuad.Make("ReDefinition moon glow");
            command = new CommandBuffer { name = "ReDefinition moon glow" };
        }

        // Takes the command buffer off and frees what Start made.
        private void OnDestroy()
        {
            Detach();
            command?.Release();
            if (material != null) Destroy(material);
            if (screen != null) Destroy(screen);
        }

        // The command buffer off the camera it is on, and the depth texture taken back where this asked for it.
        private void Detach()
        {
            if (attached != null)
            {
                attached.RemoveCommandBuffer(Event, command);
                if (depthAdded) attached.depthTextureMode &= ~DepthTextureMode.Depth;
            }
            attached = null;
            depthAdded = false;
        }

        // Every frame the command buffer moves to the flight camera if it changed, then is filled anew with a quad per
        // moon of the system in or near the frame. It is empty in the map view, while held off or with the glow
        // switched off.
        private void LateUpdate()
        {
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (camera != attached)
            {
                Detach();
                if (camera != null)
                {
                    camera.AddCommandBuffer(Event, command);
                    depthAdded = (camera.depthTextureMode & DepthTextureMode.Depth) == 0;
                    camera.depthTextureMode |= DepthTextureMode.Depth;
                    attached = camera;
                }
            }
            command.Clear();
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (HeldOff || camera == null || MapView.MapIsEnabled || (addon != null && !addon.Live.MoonGlow)) return;
            CelestialBody main = FlightGlobals.currentMainBody;
            CelestialBody star = RtKspMods.StarBody();
            if (main == null || star == null) return;

            if (!RtReflectors.SystemOf(main, star, moons)) return;
            system.Clear();
            for (int i = 0; i < moons.Count; i++) system.Add(moons[i]);
            // Backwards by index: RemoveAll's predicate, capturing the star, would be a new
            // closure every frame.
            for (int i = moons.Count - 1; i >= 0; i--)
            {
                CelestialBody body = moons[i];
                if (body == null || body == main || body.referenceBody == null || body.referenceBody == star) moons.RemoveAt(i);
            }

            Vector3d eye = camera.transform.position;
            // What stands in front of a moon: the main body, and at a moon its planet;
            // centres relative to the camera, radii in w (zero for none).
            CelestialBody planet = main.referenceBody != null && main.referenceBody != star ? main.referenceBody : null;
            blockers[0] = Blocker(main, eye);
            blockers[1] = planet != null ? Blocker(planet, eye) : Vector4.zero;
            command.SetGlobalVectorArray(BlockersId, blockers);
            command.SetGlobalFloat(OceanId, RtKspMods.ScattererOcean(main) ? 1f : 0f);
            // The darker the sky, the more a disc stands out: faint by day, full once the
            // sun is some degrees under the horizon (NightAdaptation).
            float night = NightAdaptation.Night(eye, main, star);
            Vector3 forward = camera.transform.forward;
            float halfDiagonal = Mathf.Atan(Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad)
                                            * Mathf.Sqrt(1f + camera.aspect * camera.aspect));
            foreach (CelestialBody moon in moons)
            {
                Vector3d toMoon = moon.position - eye;
                double distance = toMoon.magnitude;
                if (distance <= moon.Radius) continue;
                Vector3 direction = (Vector3)(toMoon / distance);
                float radius = Mathf.Asin(Mathf.Clamp01((float)(moon.Radius / distance)));
                // Too small to see, or off the frame: not touched. The shader finds the disc itself from each pixel's direction.
                if (radius < LeastRadius || Vector3.Angle(forward, direction) * Mathf.Deg2Rad > halfDiagonal + radius)
                    continue;
                // The moon's lit pixels brighter by night, and as dark as the planet's shadow leaves them in an eclipse.
                float nightGain = Mathf.Lerp(1f, GainOverride >= 0f ? GainOverride : NightGain, night);
                float eclipse = RtReflectors.SunVisible(moon, star, system);
                float gain = nightGain * eclipse;
                // The planet's light on its moon's dark side: the share of the sunlight it is, at the moon's albedo, brightened
                // as the rest by the eye's adaptation to the night, in the frame's gamma.
                float planetLight = NoEarthshine ? 0f : RtReflectors.PlanetLightOn(moon, star, system) * nightGain;
                bool lit = planetLight > 0f && moon.referenceBody != null;
                // By day, and out of any shadow, there is nothing to do for the moon.
                if (Mathf.Abs(gain - 1f) < LeastGain && !lit) continue;
                Vector3 toSun = (Vector3)(star.position - moon.position).normalized;
                properties.SetVector(MoonId, new Vector4(direction.x, direction.y, direction.z, radius));
                properties.SetVector(GainId, new Vector4(nightGain, eclipse, 0f, 0f));
                properties.SetVector(SunId, new Vector4(toSun.x, toSun.y, toSun.z, 0f));
                properties.SetFloat(DistanceId, (float)distance);
                properties.SetVector(RectId, RectOf(camera, direction, radius));
                if (Mathf.Abs(gain - 1f) >= LeastGain)
                    command.DrawMesh(screen, Matrix4x4.identity, material, 0, GainPass, properties);
                if (lit)
                {
                    Vector3d toPlanet = moon.referenceBody.position - moon.position;
                    Color albedo = RtReflectors.PlanetAlbedo(moon);
                    properties.SetVector(PlanetId, (Vector3)(toPlanet.normalized));
                    properties.SetVector(EarthLightId, new Vector4(Mathf.LinearToGammaSpace(albedo.r * planetLight),
                                                                  Mathf.LinearToGammaSpace(albedo.g * planetLight),
                                                                  Mathf.LinearToGammaSpace(albedo.b * planetLight), 1f));
                    command.DrawMesh(screen, Matrix4x4.identity, material, 0, EarthPass, properties);
                }
            }
        }

        // The rectangle the disc of angular radius `radius` towards `direction` fills in the camera's viewport coordinates
        // (-1 to 1): minimum then maximum, with a margin of a few pixels for the upscaler's jitter. It is the whole screen where the cone
        // reaches behind the camera's plane.
        private static Vector4 RectOf(Camera camera, Vector3 direction, float radius)
        {
            Vector3 u = Vector3.Cross(direction, Mathf.Abs(direction.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(direction, u);
            float rim = Mathf.Min(radius * 1.06f, 1.5f);
            Matrix4x4 view = camera.worldToCameraMatrix, projection = camera.projectionMatrix;
            Vector2 low = new Vector2(float.MaxValue, float.MaxValue), high = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < RimPoints; i++)
            {
                float t = i * 2f * Mathf.PI / RimPoints;
                Vector3 rimDirection = direction * Mathf.Cos(rim) + (u * Mathf.Cos(t) + v * Mathf.Sin(t)) * Mathf.Sin(rim);
                Vector3 cameraDirection = view.MultiplyVector(rimDirection);
                Vector4 clip = projection * new Vector4(cameraDirection.x, cameraDirection.y, cameraDirection.z, 1f);
                if (clip.w <= 1e-6f) return new Vector4(-1f, -1f, 1f, 1f);
                low = Vector2.Min(low, new Vector2(clip.x, clip.y) / clip.w);
                high = Vector2.Max(high, new Vector2(clip.x, clip.y) / clip.w);
            }
            float margin = 6f / Mathf.Max(camera.pixelHeight, 1);
            return new Vector4(Mathf.Max(low.x - margin, -1f), Mathf.Max(low.y - margin, -1f),
                               Mathf.Min(high.x + margin, 1f), Mathf.Min(high.y + margin, 1f));
        }

        // A body's centre relative to the eye and its radius in w, for the shader's blockers.
        private static Vector4 Blocker(CelestialBody body, Vector3d eye)
        {
            Vector3d centre = body.position - eye;
            return new Vector4((float)centre.x, (float)centre.y, (float)centre.z, (float)body.Radius);
        }
    }
}
