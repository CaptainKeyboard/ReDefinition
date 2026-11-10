using System;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // Falling snow: one particle system, made once, that follows the flight camera. Flakes start in a box above the camera.
    // The system sits in the camera's local space and is turned to the vessel's up, so the camera's own movement does not shear them.
    // Wind is a constant velocity in that space. Count and speed follow the storm intensity. Nothing is allocated per frame.
    internal sealed class StormFlakes
    {
        // The box the flakes start in: width in metres, and how far above the camera.
        private const float BoxWidth = 44f;
        private const float BoxHeight = 14f;

        // Fall speed in m/s at intensity 0 and 1, wind speed at 1 (horizontal), and the flake size range in metres.
        private const float FallSlow = 3.5f, FallFast = 6f;
        private const float WindAtFull = 9f;
        private const float SizeMin = 0.05f, SizeMax = 0.13f;

        private GameObject go;
        private ParticleSystem system;
        private ParticleSystemRenderer systemRenderer;
        private Material material;
        private Texture2D disc;
        private bool playing;
        private bool failed;

        internal int Count { get { return system != null ? system.particleCount : 0; } }

        // Makes the system and returns the shader it uses, or says why it cannot.
        internal string Prepare(Camera cam)
        {
            if (go != null) return material.shader.name;
            if (failed) return "none found (snowfall off)";
            disc = StormShaders.SoftDisc(32);
            material = StormShaders.Blended(disc);
            if (material == null)
            {
                // Tried once: the texture is not made again at every frame.
                failed = true;
                UnityEngine.Object.Destroy(disc);
                disc = null;
                return "none found (snowfall off)";
            }
            StormShaders.Tint(material, new Color(0.95f, 0.97f, 1f, 1f), 0.9f);
            go = new GameObject("ReDefinition Seasons storm flakes");
            go.layer = StormShaders.LayerOf(cam);
            system = go.AddComponent<ParticleSystem>();
            systemRenderer = go.GetComponent<ParticleSystemRenderer>();
            systemRenderer.sharedMaterial = material;
            systemRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            systemRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            systemRenderer.receiveShadows = false;
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = StormLook.MaxFlakes;
            main.startLifetime = 5f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(SizeMin, SizeMax);
            main.startColor = new Color(1f, 1f, 1f, 0.9f);
            main.gravityModifier = 0f;

            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(BoxWidth, 1f, BoxWidth);
            shape.position = new Vector3(0f, BoxHeight, 0f);

            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;

            ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.x = 0f;
            velocity.y = -FallSlow;
            velocity.z = 0f;
            go.SetActive(false);
            return material.shader.name;
        }

        // Places the system at the camera, turned to the vessel's up, and sets its rate and speeds for the intensity.
        internal void Show(Camera cam, Vector3 up, float intensity, double universalTime, int seed)
        {
            if (go == null) Prepare(cam);
            if (go == null) return;
            if (!go.activeSelf) go.SetActive(true);
            go.layer = StormShaders.LayerOf(cam);

            // A horizontal frame from up: the system's local z points along a fixed horizontal direction, y along up.
            Vector3 forward = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(Vector3.right, up);
            Transform t = go.transform;
            t.position = cam.transform.position;
            t.rotation = Quaternion.LookRotation(forward.normalized, up);

            // The wind turns slowly with the time (a turn in about 3 Kerbin days) from a direction that depends on the seed.
            float angle = (float)(((universalTime / (3 * 21600.0)) + seed * 0.37) * 2 * Math.PI);
            float wind = WindAtFull * intensity;
            float fall = Mathf.Lerp(FallSlow, FallFast, intensity);

            ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
            velocity.x = Mathf.Cos(angle) * wind;
            velocity.y = -fall;
            velocity.z = Mathf.Sin(angle) * wind;
            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTime = (float)StormLook.FlakeRate(intensity);
            if (!playing)
            {
                system.Play(true);
                playing = true;
            }
        }

        internal void Hide()
        {
            if (go == null) return;
            if (playing)
            {
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                playing = false;
            }
            if (go.activeSelf) go.SetActive(false);
        }

        internal void Dispose()
        {
            if (go != null) UnityEngine.Object.Destroy(go);
            if (material != null) UnityEngine.Object.Destroy(material);
            if (disc != null) UnityEngine.Object.Destroy(disc);
            go = null;
        }
    }
}
