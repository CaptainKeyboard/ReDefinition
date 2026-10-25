using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The moons of the planet the camera is at made to stand out in the sky: each disc a
    // little brighter, with a soft glow round it, the more the darker the sky. Moons only:
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
        private static readonly int ColourId = Shader.PropertyToID("_ReDefinitionMoonColour");
        private static readonly int BlockersId = Shader.PropertyToID("_ReDefinitionMoonBlockers");
        private static readonly int OceanId = Shader.PropertyToID("_ReDefinitionMoonOcean");
        private static readonly int DistanceId = Shader.PropertyToID("_ReDefinitionMoonDistance");
        private readonly Vector4[] blockers = new Vector4[2];
        // Whether the camera's depth texture was asked for by this, to take back.
        private bool depthAdded;

        // For the light probe: no glow, to compare.
        internal static bool HeldOff;

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
            screen = new Mesh { name = "ReDefinition moon glow" };
            screen.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
            screen.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            screen.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
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

        // Every frame: the command buffer moved to the flight camera if it changed, then filled anew with a quad per moon
        // of the system in or near the frame; empty in the map view, while held off or with the glow switched off.
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
            float strength = Mathf.Lerp(0.15f, 1f, night);
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
                // Off the frame by more than its glow's reach: not drawn. The shader finds
                // the disc itself from each pixel's direction.
                if (Vector3.Angle(forward, direction) * Mathf.Deg2Rad > halfDiagonal + 6f * Mathf.Max(radius, 0.002f))
                    continue;
                // The disc as the sky shows it: its albedo at its phase, as the frame's colour.
                double phaseAngle = Vector3d.Angle(star.position - moon.position, eye - moon.position) * Mathf.Deg2Rad;
                Color albedo = RtReflectors.PlanetAlbedo(moon);
                float lit = (float)(2.0 / 3.0 * RtReflectorLight.Phase(phaseAngle));
                Color shown = new Color(Mathf.LinearToGammaSpace(albedo.r * lit), Mathf.LinearToGammaSpace(albedo.g * lit),
                                        Mathf.LinearToGammaSpace(albedo.b * lit), 1f);
                properties.SetVector(MoonId, new Vector4(direction.x, direction.y, direction.z, radius));
                properties.SetVector(ColourId, new Vector4(shown.r, shown.g, shown.b, strength));
                properties.SetFloat(DistanceId, (float)distance);
                command.DrawMesh(screen, Matrix4x4.identity, material, 0, 0, properties);
            }
        }

        // A body's centre relative to the eye and its radius in w, for the shader's blockers.
        private static Vector4 Blocker(CelestialBody body, Vector3d eye)
        {
            Vector3d centre = body.position - eye;
            return new Vector4((float)centre.x, (float)centre.y, (float)centre.z, (float)body.Radius);
        }
    }
}
