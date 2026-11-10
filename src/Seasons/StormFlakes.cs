using System;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // Falling snow: two particle systems, made once, that follow the flight camera. A near layer is dense and close to the camera, a far
    // layer is wide with larger flakes so that they stay visible at distance. The flakes are stretched billboards: the wind and the fall
    // make streaks, longer in a stronger storm. Each system sits in the camera's local space and is turned to the vessel's up, so the
    // camera's own movement does not shear them. Count, size, speed and streak length follow the storm intensity (StormLook). The
    // material is drawn after the veil (render queue 3100), so that the whiteout does not wash the flakes out. Nothing is allocated
    // per frame.
    internal sealed class StormFlakes
    {
        // The render queue of the flakes: after the veil at 3000.
        private const int Queue = 3100;

        // The change of a value that makes the system be set again.
        private const float SizeStep = 0.01f, LifetimeStep = 0.05f;

        private sealed class Layer
        {
            internal StormLook.FlakeLayer Look;
            internal GameObject Go;
            internal ParticleSystem System;
            internal ParticleSystemRenderer Renderer;
            internal float SizeFactor = -1f;
            internal float Lifetime = -1f;
        }

        private readonly Layer near = new Layer { Look = StormLook.NearLayer };
        private readonly Layer far = new Layer { Look = StormLook.FarLayer };
        private Material material;
        private Texture2D disc;
        private bool playing;
        private bool failed;

        internal int Count
        {
            get { return (near.System != null ? near.System.particleCount : 0) + (far.System != null ? far.System.particleCount : 0); }
        }

        // Makes the systems and returns the shader they use, or says why it cannot.
        internal string Prepare(Camera cam)
        {
            if (near.Go != null) return material.shader.name;
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
            material.renderQueue = Queue;
            StormShaders.Tint(material, new Color(0.95f, 0.97f, 1f, 1f), 0.9f);
            Make(near, cam, "near");
            Make(far, cam, "far");
            return material.shader.name;
        }

        private void Make(Layer layer, Camera cam, string name)
        {
            layer.Go = new GameObject("ReDefinition Seasons storm flakes " + name);
            layer.Go.layer = StormShaders.LayerOf(cam);
            layer.System = layer.Go.AddComponent<ParticleSystem>();
            layer.Renderer = layer.Go.GetComponent<ParticleSystemRenderer>();
            layer.Renderer.sharedMaterial = material;
            layer.Renderer.renderMode = ParticleSystemRenderMode.Stretch;
            layer.Renderer.lengthScale = 1f;
            layer.Renderer.velocityScale = (float)StormLook.StreakScale(0);
            layer.Renderer.cameraVelocityScale = 0f;
            layer.Renderer.minParticleSize = StormLook.MinScreenSize;
            layer.Renderer.maxParticleSize = StormLook.MaxScreenSize;
            layer.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            layer.Renderer.receiveShadows = false;
            layer.System.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = layer.System.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = StormLook.MaxFlakesPerLayer;
            main.startLifetime = (float)StormLook.Lifetime(layer.Look, StormLook.FallSlow);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(layer.Look.SizeMin, layer.Look.SizeMax);
            main.startColor = new Color(1f, 1f, 1f, 0.9f);
            main.gravityModifier = 0f;

            ParticleSystem.ShapeModule shape = layer.System.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(layer.Look.BoxWidth, layer.Look.BoxHeight, layer.Look.BoxWidth);
            shape.position = new Vector3(0f, layer.Look.BoxHeight * 0.35f, 0f);

            ParticleSystem.EmissionModule emission = layer.System.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;

            ParticleSystem.VelocityOverLifetimeModule velocity = layer.System.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.x = 0f;
            velocity.y = -StormLook.FallSlow;
            velocity.z = 0f;
            layer.Go.SetActive(false);
        }

        // Places the systems at the camera, turned to the vessel's up, and sets their rate, size, speeds and streaks for the intensity.
        // The colour is the flakes' tint (by day white, darker at night).
        internal void Show(Camera cam, Vector3 up, float intensity, double universalTime, int seed, float brightness)
        {
            if (near.Go == null) Prepare(cam);
            if (near.Go == null) return;

            // A horizontal frame from up: the system's local z points along a fixed horizontal direction, y along up.
            Vector3 forward = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(Vector3.right, up);
            Quaternion rotation = Quaternion.LookRotation(forward.normalized, up);

            // The wind turns slowly with the time (a turn in about 3 Kerbin days) from a direction that depends on the seed.
            float angle = (float)(((universalTime / (3 * 21600.0)) + seed * 0.37) * 2 * Math.PI);
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            float wind = (float)StormLook.WindSpeed(intensity);
            float fall = (float)StormLook.FallSpeed(intensity);

            float tint = Mathf.Lerp(0.55f, 1f, brightness);
            StormShaders.Tint(material, new Color(0.95f * tint, 0.97f * tint, 1f * tint, 1f), 0.9f);

            Place(near, cam, rotation, intensity, cos, sin, wind, fall);
            Place(far, cam, rotation, intensity, cos, sin, wind, fall);
            if (!playing)
            {
                near.System.Play(true);
                far.System.Play(true);
                playing = true;
            }
        }

        private static void Place(Layer layer, Camera cam, Quaternion rotation, float intensity, float cos, float sin, float wind, float fall)
        {
            if (!layer.Go.activeSelf) layer.Go.SetActive(true);
            layer.Go.layer = StormShaders.LayerOf(cam);
            Transform t = layer.Go.transform;
            t.position = cam.transform.position;
            t.rotation = rotation;

            float life = (float)StormLook.Lifetime(layer.Look, fall);
            float sizeFactor = (float)StormLook.SizeFactor(intensity);
            ParticleSystem.MainModule main = layer.System.main;
            if (Mathf.Abs(life - layer.Lifetime) > LifetimeStep)
            {
                layer.Lifetime = life;
                main.startLifetime = life;
            }
            if (Mathf.Abs(sizeFactor - layer.SizeFactor) > SizeStep)
            {
                layer.SizeFactor = sizeFactor;
                main.startSize = new ParticleSystem.MinMaxCurve(layer.Look.SizeMin * sizeFactor, layer.Look.SizeMax * sizeFactor);
            }

            float upwind = (float)StormLook.UpwindOffset(wind, life);
            ParticleSystem.ShapeModule shape = layer.System.shape;
            shape.position = new Vector3(upwind * cos, layer.Look.BoxHeight * 0.35f, upwind * sin);

            ParticleSystem.VelocityOverLifetimeModule velocity = layer.System.velocityOverLifetime;
            velocity.x = cos * wind;
            velocity.y = -fall;
            velocity.z = sin * wind;
            ParticleSystem.EmissionModule emission = layer.System.emission;
            emission.rateOverTime = (float)StormLook.FlakeRate(intensity, layer.Look);
            layer.Renderer.velocityScale = (float)StormLook.StreakScale(intensity);
        }

        internal void Hide()
        {
            if (near.Go == null) return;
            if (playing)
            {
                near.System.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                far.System.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                playing = false;
            }
            if (near.Go.activeSelf) near.Go.SetActive(false);
            if (far.Go.activeSelf) far.Go.SetActive(false);
        }

        internal void Dispose()
        {
            if (near.Go != null) UnityEngine.Object.Destroy(near.Go);
            if (far.Go != null) UnityEngine.Object.Destroy(far.Go);
            if (material != null) UnityEngine.Object.Destroy(material);
            if (disc != null) UnityEngine.Object.Destroy(disc);
            near.Go = null;
            far.Go = null;
        }
    }
}
