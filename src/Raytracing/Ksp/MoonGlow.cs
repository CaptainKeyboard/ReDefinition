using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The moons of the planet the camera is at made to stand out in the sky: each disc a
    // little brighter, with a soft glow round it, the more the darker the sky. On the
    // flight's scene camera, where it sees sky, before the transparent pass, so that
    // clouds and the atmosphere draw over it (ReDefinitionMoonGlow.shader). It lights
    // nothing: the moons' light on the scene is the raytracing's (RtReflectors). The
    // player, 2026-10-03: the moon "should stand out more when you look at it, not make
    // more light".
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MoonGlow : MonoBehaviour
    {
        private const CameraEvent Event = CameraEvent.BeforeForwardAlpha;
        private static readonly int MoonId = Shader.PropertyToID("_ReDefinitionMoon");
        private static readonly int ColourId = Shader.PropertyToID("_ReDefinitionMoonColour");

        private Material material;
        private Mesh screen;
        private CommandBuffer command;
        private Camera attached;
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        private readonly List<CelestialBody> moons = new List<CelestialBody>();

        private void Start()
        {
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

        private void OnDestroy()
        {
            Detach();
            command?.Release();
            if (material != null) Destroy(material);
            if (screen != null) Destroy(screen);
        }

        private void Detach()
        {
            if (attached != null) attached.RemoveCommandBuffer(Event, command);
            attached = null;
        }

        private void LateUpdate()
        {
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (camera != attached)
            {
                Detach();
                if (camera != null)
                {
                    camera.AddCommandBuffer(Event, command);
                    camera.depthTextureMode |= DepthTextureMode.Depth;
                    attached = camera;
                }
            }
            command.Clear();
            if (camera == null || MapView.MapIsEnabled) return;
            CelestialBody main = FlightGlobals.currentMainBody;
            CelestialBody star = RtKspMods.StarBody();
            if (main == null || star == null) return;

            if (!RtReflectors.SystemOf(main, star, moons)) return;
            moons.Remove(main);

            Vector3d eye = camera.transform.position;
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
                command.DrawMesh(screen, Matrix4x4.identity, material, 0, 0, properties);
            }
        }
    }
}
