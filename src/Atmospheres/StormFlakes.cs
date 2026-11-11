using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Falling snow: three particle systems, made once, that follow the flight camera. A near layer holds few large translucent flakes
    // (out-of-focus discs), a mid layer the bulk, a far layer many small ones. The flakes are soft round discs of different size and
    // brightness. They fall slowly (about 1 m/s) and drift sideways with a wind that gusts. Each one sways with the noise of its
    // particle system, so that no flake falls in a straight line. They are stretched along their velocity by a few percent only, so
    // that they never read as rain. Each system sits in the camera's local space and is turned to the vessel's up, so the camera's
    // own movement does not shear them. Count, size, speed and wind follow the storm intensity (StormLook). The material is drawn
    // after the veil (render queue 3100), so that the whiteout does not wash the flakes out. Nothing is allocated per frame.
    // The flakes end at the end of their life; nothing splashes on the ground.
    internal sealed class StormFlakes
    {
        // The render queue of the flakes: after the veil at 3000.
        private const int Queue = 3100;

        // True while the legacy storm effect shows these flakes; the precipitation renderer (Render/Precip) does not draw meanwhile.
        // The last caller package (C25) removes the legacy storm effect and with it this class.
        internal static bool LegacyDrawing { get; private set; }

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
            internal float Fall = -1f;
        }

        // The near layer is a box 7 m wide with 6 % of the flakes (0.10 to 0.26 m, opacity 0.5). The mid layer is 18 m wide with 34 %
        // (0.03 to 0.08 m, opacity 0.95). The far layer is 36 m wide with 60 % (0.02 to 0.05 m, opacity 1). They fall at 1.2 to 2.0,
        // 0.9 to 1.6 and 0.8 to 1.3 m/s, and 85 % of that in a light fall.
        private readonly Layer[] layers =
        {
            new Layer { Look = StormLook.NearLayer },
            new Layer { Look = StormLook.MidLayer },
            new Layer { Look = StormLook.FarLayer }
        };

        private Material material;
        private Texture2D disc;
        private bool playing;
        private bool failed;

        internal int Count
        {
            get
            {
                int count = 0;
                for (int i = 0; i < layers.Length; i++)
                {
                    if (layers[i].System != null) count += layers[i].System.particleCount;
                }
                return count;
            }
        }

        // Makes the systems and returns the shader they use, or says why it cannot.
        internal string Prepare(Camera cam)
        {
            if (layers[0].Go != null) return material.shader.name;
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
            StormShaders.Tint(material, new Color(0.95f, 0.97f, 1f, 1f), 1f);
            string[] names = { "near", "mid", "far" };
            for (int i = 0; i < layers.Length; i++)
            {
                Make(layers[i], cam, names[i]);
                layers[i].Renderer.sharedMaterial = material;
            }
            return material.shader.name;
        }

        // Makes the particle system of one layer. The noise module has 2 octaves, 1.3 m sideways and 0.45 m up. The stretch is size plus
        // speed times 0.0008 to 0.002; StormLook.StreakRatio keeps it within 1.5 times the width. At most 8000 flakes stand per layer,
        // and a flake is 0.22 % to 3 % of the viewport high.
        private static void Make(Layer layer, Camera cam, string name)
        {
            layer.Go = new GameObject("ReDefinition Atmospheres storm flakes " + name);
            layer.Go.layer = StormShaders.LayerOf(cam);
            layer.System = layer.Go.AddComponent<ParticleSystem>();
            layer.Renderer = layer.Go.GetComponent<ParticleSystemRenderer>();
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
            main.startLifetime = (float)StormLook.Lifetime(layer.Look, 0);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(layer.Look.SizeMin, layer.Look.SizeMax);
            // Every flake has its own brightness: between a dim and a bright white, at the layer's opacity.
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.78f, 0.8f, 0.85f, layer.Look.Alpha * 0.75f), new Color(1f, 1f, 1f, layer.Look.Alpha));
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.2831853f);
            main.gravityModifier = 0f;

            ParticleSystem.ShapeModule shape = layer.System.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(layer.Look.BoxWidth, layer.Look.BoxHeight, layer.Look.BoxWidth);
            shape.position = new Vector3(0f, layer.Look.BoxHeight * 0.35f, 0f);

            ParticleSystem.EmissionModule emission = layer.System.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;

            // The velocity: x and z get the wind, y the fall. All three are random between two constants, so that every flake has
            // its own speed (Unity needs the three curves in the same mode).
            ParticleSystem.VelocityOverLifetimeModule velocity = layer.System.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            velocity.y = new ParticleSystem.MinMaxCurve(-layer.Look.FallMax, -layer.Look.FallMin);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            // The sway: noise moves each flake to the side and a little up and down, differently from its neighbours.
            ParticleSystem.NoiseModule noise = layer.System.noise;
            noise.enabled = true;
            noise.separateAxes = true;
            noise.strengthX = StormLook.SwayHorizontal;
            noise.strengthY = StormLook.SwayVertical;
            noise.strengthZ = StormLook.SwayHorizontal;
            noise.frequency = StormLook.SwayFrequency;
            noise.scrollSpeed = StormLook.SwayScroll;
            noise.damping = true;
            noise.octaveCount = 2;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            // A flake fades in and out, so that none pops into view or out of it.
            ParticleSystem.ColorOverLifetimeModule fade = layer.System.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
            fade.color = new ParticleSystem.MinMaxGradient(gradient);

            // Nothing hits the ground: no collision, no sub emitter, no trail.
            layer.Go.SetActive(false);
        }

        // Places the systems at the camera, turned to the vessel's up, and sets their rate, size, speeds and wind for the intensity.
        // The colour is the flakes' tint (by day white, darker at night). The wind is 4.5 times the intensity in m/s, each flake 0.8 to
        // 1.2 times that, times a gust of 0.6 to 1.4 (StormLook.GustFactor). The rate is 1000 times intensity^0.8 flakes a second.
        internal void Show(Camera cam, Vector3 up, float intensity, double universalTime, int seed, float brightness)
        {
            if (layers[0].Go == null) Prepare(cam);
            if (layers[0].Go == null) return;
            LegacyDrawing = true;

            // A horizontal frame from up: the system's local z points along a fixed horizontal direction, y along up.
            Vector3 forward = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(Vector3.right, up);
            Quaternion rotation = Quaternion.LookRotation(forward.normalized, up);

            // The wind turns slowly with the time (a turn in about 3 Kerbin days) from a direction that depends on the seed, and it
            // gusts with the real time.
            float angle = (float)(((universalTime / (3 * 21600.0)) + seed * 0.37) * 2 * Math.PI);
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            float meanWind = (float)StormLook.WindSpeed(intensity);
            float wind = meanWind * (float)StormLook.GustFactor(Time.time, seed * 0.61);

            float tint = Mathf.Lerp(0.55f, 1f, brightness);
            StormShaders.Tint(material, new Color(0.95f * tint, 0.97f * tint, 1f * tint, 1f), 1f);

            for (int i = 0; i < layers.Length; i++) Place(layers[i], cam, rotation, intensity, cos, sin, meanWind, wind);
            if (!playing)
            {
                for (int i = 0; i < layers.Length; i++) layers[i].System.Play(true);
                playing = true;
            }
        }

        private static void Place(Layer layer, Camera cam, Quaternion rotation, float intensity, float cos, float sin, float meanWind, float wind)
        {
            if (!layer.Go.activeSelf) layer.Go.SetActive(true);
            layer.Go.layer = StormShaders.LayerOf(cam);
            Transform t = layer.Go.transform;
            t.position = cam.transform.position;
            t.rotation = rotation;

            float life = (float)StormLook.Lifetime(layer.Look, intensity);
            float sizeFactor = (float)StormLook.SizeFactor(intensity);
            float fall = (float)StormLook.FallFactor(intensity);
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

            float upwind = (float)StormLook.UpwindOffset(meanWind, life);
            ParticleSystem.ShapeModule shape = layer.System.shape;
            shape.position = new Vector3(upwind * cos, layer.Look.BoxHeight * 0.35f, upwind * sin);

            ParticleSystem.VelocityOverLifetimeModule velocity = layer.System.velocityOverLifetime;
            float low = 1f - StormLook.WindSpread, high = 1f + StormLook.WindSpread;
            velocity.x = new ParticleSystem.MinMaxCurve(cos * wind * low, cos * wind * high);
            velocity.z = new ParticleSystem.MinMaxCurve(sin * wind * low, sin * wind * high);
            if (Mathf.Abs(fall - layer.Fall) > 0.002f)
            {
                layer.Fall = fall;
                velocity.y = new ParticleSystem.MinMaxCurve(-layer.Look.FallMax * fall, -layer.Look.FallMin * fall);
            }
            ParticleSystem.EmissionModule emission = layer.System.emission;
            emission.rateOverTime = (float)StormLook.FlakeRate(intensity, layer.Look);
            layer.Renderer.velocityScale = (float)StormLook.StreakScale(intensity);
        }

        internal void Hide()
        {
            LegacyDrawing = false;
            if (layers[0].Go == null) return;
            if (playing)
            {
                for (int i = 0; i < layers.Length; i++) layers[i].System.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                playing = false;
            }
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].Go.activeSelf) layers[i].Go.SetActive(false);
            }
        }

        internal void Dispose()
        {
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].Go != null) UnityEngine.Object.Destroy(layers[i].Go);
                layers[i].Go = null;
            }
            if (material != null) UnityEngine.Object.Destroy(material);
            if (disc != null) UnityEngine.Object.Destroy(disc);
        }
    }
}
