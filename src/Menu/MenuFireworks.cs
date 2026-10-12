using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Menu
{
    // Fireworks on Kerbin's night side in the main menu (OwnSettings.MenuFireworks).
    //
    // The main menu scene (level2, read with UnityPy) has two areas, of which
    // MainMenuEnvLogic.Awake activates one (decompiled): OrbitScene, Kerbin
    // large with the Mun, and MunScene, Kerbin small over the Mun's ground. In
    // each, "Kerbin" is a sphere mesh of radius 1000 (GeoSphere1000mSphereMapped)
    // under a scale, on layer 15, which only one directional light lights
    // (PlanetLight, "Directional light": culling mask 1 << 15). Rotato turns the
    // sphere; the light stands still. The light shines along its forward, so the
    // sun lies against it. The menu's camera is MainMenuEnvLogic.landscapeCamera,
    // which draws layer 15.
    //
    // A burst is a rocket rising from a point of the sphere, then a ball of sparks
    // that slow, fall towards the planet and fade. Points are taken where the sun
    // is below the horizon (normal against the sun below NightDot), the camera
    // sees the ground, and the menu's buttons on the left of the screen are not.
    // Sizes follow the sphere's radius, with a least angle on the screen so the
    // small Kerbin over the Mun shows them too. Each burst has a particle system
    // of its own from a pool, so its sparks fall towards its own ground.
    //
    // -redefinitionFireworksBurst fires one every half second, for screenshots.
    // Anything that fails ends the fireworks with one line in KSP.log and leaves
    // the menu as KSP built it.
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public class MenuFireworks : MonoBehaviour
    {
        internal const string BurstArgument = "-redefinitionFireworksBurst";
        // With it as well: a screenshot into ReDefinitionCaptures after some bursts, then quit.
        internal const string ShotArgument = "-redefinitionFireworksShot";

        // Below this, the sun stands under the ground's horizon.
        private const float NightDot = -0.1f;
        // Points closer to the planet's rim than this, in the cosine between the
        // ground and the view, lie too flat to see.
        private const float FacingDot = 0.15f;
        // The menu's buttons stand left of this, in the screen's width.
        private const float ButtonsRight = 0.35f;
        private const float ScreenMargin = 0.03f;

        // A burst's size in the sphere's radius, and at least this angle on the
        // screen, in radians.
        private const float SizeOfRadius = 0.03f;
        private const float LeastAngle = 0.004f;

        private const int PoolSize = 8;
        private const int MaxParticles = 400;
        private const float RiseSeconds = 0.7f;
        private const float TrailPerSecond = 90f;
        private const float TestInterval = 0.5f;

        private static readonly Color[] Colours =
        {
            new Color(1f, 0.25f, 0.2f), new Color(1f, 0.75f, 0.25f), new Color(0.35f, 1f, 0.4f),
            new Color(0.35f, 0.6f, 1f), new Color(1f, 0.35f, 0.9f), new Color(0.9f, 0.95f, 1f),
            new Color(0.3f, 1f, 0.95f),
        };

        private sealed class Burst
        {
            public ParticleSystem System;
            public bool Busy;
            public bool Exploded;
            public float Age;
            public Vector3 Ground;
            public Vector3 Up;
            public float Size;
            public Color Colour;
            public Color Second;
            public float Trail;
        }

        private readonly List<Burst> pool = new List<Burst>();
        private Transform kerbin;
        private Light sun;
        private Camera view;
        private float radius;
        private bool test;
        private float nextBurst;
        private readonly List<float> salvo = new List<float>();
        private Material material;
        private Texture2D dot;
        private bool firstLogged;
        private bool failed;

        private IEnumerator Start()
        {
            // MainMenuEnvLogic.Awake has activated its area by now; one frame on,
            // the scene stands as the menu shows it.
            yield return null;
            try
            {
                test = Array.IndexOf(Environment.GetCommandLineArgs(), BurstArgument) >= 0;
                if (!FindScene()) yield break;
                BuildPool();
                nextBurst = Time.time + (test ? TestInterval : UnityEngine.Random.Range(0.5f, 1.5f));
                Debug.Log(Log.Tag + " Main menu fireworks: " + Describe());
            }
            catch (Exception e)
            {
                Fail("start", e);
            }
            if (!test || Array.IndexOf(Environment.GetCommandLineArgs(), ShotArgument) < 0) yield break;
            yield return new WaitForSeconds(6f);
            string folder = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "ReDefinitionCaptures");
            System.IO.Directory.CreateDirectory(folder);
            string file = System.IO.Path.Combine(folder, "fireworks-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
            ScreenCapture.CaptureScreenshot(file);
            Debug.Log(Log.Tag + " Main menu fireworks: screenshot " + file + ".");
            yield return new WaitForSeconds(2f);
            Application.Quit();
        }

        private void Update()
        {
            if (failed || kerbin == null || pool.Count == 0) return;
            try
            {
                for (int i = 0; i < pool.Count; i++)
                    if (pool[i].Busy) Advance(pool[i], Time.deltaTime);

                if (!Enabled()) return;
                for (int i = salvo.Count - 1; i >= 0; i--)
                {
                    if (Time.time < salvo[i]) continue;
                    salvo.RemoveAt(i);
                    Launch();
                }
                if (Time.time < nextBurst) return;
                Launch();
                if (test)
                {
                    nextBurst = Time.time + TestInterval;
                    return;
                }
                // About two a second, as the player liked them, now and then several together.
                nextBurst = Time.time + UnityEngine.Random.Range(0.3f, 0.8f);
                if (UnityEngine.Random.value < 0.25f)
                {
                    int more = UnityEngine.Random.Range(1, 4);
                    for (int i = 0; i < more; i++) salvo.Add(Time.time + UnityEngine.Random.Range(0.2f, 1.2f));
                }
            }
            catch (Exception e)
            {
                Fail("update", e);
            }
        }

        private void OnDestroy()
        {
            if (material != null) Destroy(material);
            if (dot != null) Destroy(dot);
            for (int i = 0; i < pool.Count; i++)
                if (pool[i].System != null) Destroy(pool[i].System.gameObject);
        }

        private static bool Enabled()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            return addon == null || addon.Live.MenuFireworks;
        }

        private void Fail(string where, Exception e)
        {
            failed = true;
            Debug.LogWarning(Log.Tag + " Main menu fireworks stopped (" + where + "): " + e.GetType().Name + ": " + e.Message);
        }

        // The active area's Kerbin, the light that lights it, and the menu's camera.
        private bool FindScene()
        {
            MainMenuEnvLogic env = FindObjectOfType<MainMenuEnvLogic>();
            if (env == null || env.landscapeCamera == null || env.areas == null)
            {
                Debug.Log(Log.Tag + " Main menu fireworks: no menu scene found, none shown.");
                return false;
            }
            view = env.landscapeCamera;
            foreach (GameObject area in env.areas)
            {
                if (area == null || !area.activeInHierarchy) continue;
                foreach (MeshFilter filter in area.GetComponentsInChildren<MeshFilter>())
                {
                    if (filter.name != "Kerbin" || filter.sharedMesh == null) continue;
                    kerbin = filter.transform;
                    radius = filter.sharedMesh.bounds.extents.y * kerbin.lossyScale.y;
                    break;
                }
                if (kerbin == null) continue;
                int layer = 1 << kerbin.gameObject.layer;
                // The light that shades Kerbin as the menu shows it: PlanetLight in the orbit
                // scene, "Directional light" in the Mun's; BackLight, brighter, only rims it
                // from behind (measured: its night side was the lit face the camera sees).
                foreach (Light light in area.GetComponentsInChildren<Light>())
                    if (light.type == LightType.Directional && (light.cullingMask & layer) != 0
                        && (sun == null || Rank(light) > Rank(sun)
                            || (Rank(light) == Rank(sun) && light.intensity > sun.intensity)))
                        sun = light;
                break;
            }
            if (kerbin == null || sun == null || radius <= 0f)
            {
                Debug.Log(Log.Tag + " Main menu fireworks: no Kerbin or no sun in the menu's area, none shown.");
                kerbin = null;
                return false;
            }
            return true;
        }

        private static int Rank(Light light)
        {
            return light.name == "PlanetLight" || light.name == "Directional light" ? 2
                   : light.name.IndexOf("Back", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
        }

        private void BuildPool()
        {
            dot = SoftDot(32);
            Shader shader = FindShader("KSP/Particles/Additive", "Mobile/Particles/Additive",
                "Legacy Shaders/Particles/Additive", "Particles/Additive");
            if (shader == null) throw new InvalidOperationException("no additive particle shader");
            material = new Material(shader) { mainTexture = dot, name = "ReDefinition fireworks" };
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));

            for (int i = 0; i < PoolSize; i++)
            {
                GameObject go = new GameObject("ReDefinition fireworks " + i);
                go.layer = kerbin.gameObject.layer;
                ParticleSystem system = go.AddComponent<ParticleSystem>();
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                ParticleSystem.MainModule main = system.main;
                main.playOnAwake = false;
                main.loop = true;
                main.duration = 10f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = MaxParticles;
                main.startSpeed = 0f;

                ParticleSystem.EmissionModule emission = system.emission;
                emission.enabled = false;
                ParticleSystem.ShapeModule shape = system.shape;
                shape.enabled = false;

                ParticleSystem.ColorOverLifetimeModule fade = system.colorOverLifetime;
                fade.enabled = true;
                Gradient gradient = new Gradient();
                gradient.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 0.5f),
                            new GradientColorKey(Color.black, 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
                fade.color = new ParticleSystem.MinMaxGradient(gradient);

                ParticleSystem.SizeOverLifetimeModule shrink = system.sizeOverLifetime;
                shrink.enabled = true;
                shrink.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.35f));

                // The sparks fly out fast and slow down, as in air.
                ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
                velocity.enabled = true;
                velocity.speedModifier = new ParticleSystem.MinMaxCurve(1f,
                    new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.3f, 0.35f), new Keyframe(1f, 0.12f)));

                ParticleSystem.ForceOverLifetimeModule force = system.forceOverLifetime;
                force.enabled = true;
                force.space = ParticleSystemSimulationSpace.World;

                ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                system.Play();
                pool.Add(new Burst { System = system });
            }
        }

        private static Shader FindShader(params string[] names)
        {
            foreach (string name in names)
            {
                Shader shader = Shader.Find(name);
                if (shader != null) return shader;
            }
            return null;
        }

        // A round spot, bright in the middle and falling off to nothing at the edge.
        private static Texture2D SoftDot(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ReDefinition fireworks dot",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            Color[] pixels = new Color[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half, dy = (y - half) / half;
                float r2 = dx * dx + dy * dy;
                float v = r2 >= 1f ? 0f : Mathf.Exp(-r2 * 4f) * (1f - r2);
                pixels[y * size + x] = new Color(v, v, v, v);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // A point of the night side the camera sees, with the planet's up there.
        private bool PickPoint(out Vector3 ground, out Vector3 up)
        {
            Vector3 toSun = -sun.transform.forward;
            Vector3 centre = kerbin.position;
            Vector3 eye = view.transform.position;
            for (int attempt = 0; attempt < 200; attempt++)
            {
                up = UnityEngine.Random.onUnitSphere;
                if (Vector3.Dot(up, toSun) >= NightDot) continue;
                ground = centre + up * radius;
                Vector3 toEye = eye - ground;
                if (Vector3.Dot(up, toEye.normalized) < FacingDot) continue;
                Vector3 screen = view.WorldToViewportPoint(ground);
                if (screen.z <= 0f || screen.x < ButtonsRight || screen.x > 1f - ScreenMargin
                    || screen.y < ScreenMargin || screen.y > 1f - ScreenMargin) continue;
                return true;
            }
            ground = up = Vector3.zero;
            return false;
        }

        private void Launch()
        {
            Burst burst = null;
            for (int i = 0; i < pool.Count; i++)
                if (!pool[i].Busy && pool[i].System.particleCount == 0)
                {
                    burst = pool[i];
                    break;
                }
            Vector3 ground, up;
            if (burst == null || !PickPoint(out ground, out up)) return;

            float distance = Vector3.Distance(view.transform.position, ground);
            burst.Size = Mathf.Max(radius * SizeOfRadius, distance * LeastAngle);
            burst.Ground = ground;
            burst.Up = up;
            burst.Age = 0f;
            burst.Exploded = false;
            burst.Trail = 0f;
            burst.Busy = true;
            burst.Colour = Colours[UnityEngine.Random.Range(0, Colours.Length)];
            burst.Second = UnityEngine.Random.value < 0.3f ? Colours[UnityEngine.Random.Range(0, Colours.Length)] : burst.Colour;

            // Sparks fall towards this burst's ground: about half a burst's size
            // over their life.
            Vector3 fall = -up * (burst.Size * 0.25f);
            ParticleSystem.ForceOverLifetimeModule force = burst.System.forceOverLifetime;
            force.x = fall.x;
            force.y = fall.y;
            force.z = fall.z;

            if (!firstLogged)
            {
                firstLogged = true;
                Vector3 screen = view.WorldToViewportPoint(ground);
                Debug.Log(Log.Tag + " Main menu fireworks: first burst at screen ("
                          + screen.x.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                          + screen.y.ToString("0.00", CultureInfo.InvariantCulture) + "), size "
                          + burst.Size.ToString("0.0", CultureInfo.InvariantCulture) + ", sun height "
                          + Vector3.Dot(up, -sun.transform.forward).ToString("0.00", CultureInfo.InvariantCulture) + ".");
            }
        }

        private void Advance(Burst burst, float dt)
        {
            burst.Age += dt;
            float height = burst.Size * 2.5f;
            if (!burst.Exploded)
            {
                float t = Mathf.Clamp01(burst.Age / RiseSeconds);
                // Fast off the ground, slowing at the top.
                float along = 1f - (1f - t) * (1f - t);
                Vector3 head = burst.Ground + burst.Up * (height * along);
                ParticleSystem.EmitParams trail = new ParticleSystem.EmitParams
                {
                    position = head,
                    velocity = -burst.Up * (height * 0.1f),
                    startSize = burst.Size * 0.15f,
                    startLifetime = 0.45f,
                    startColor = new Color(1f, 0.8f, 0.55f, 1f),
                };
                burst.Trail += dt * TrailPerSecond;
                int count = (int)burst.Trail;
                burst.Trail -= count;
                if (count > 0) burst.System.Emit(trail, count);
                if (t >= 1f) Explode(burst);
                return;
            }
            if (burst.Age > RiseSeconds + 0.5f && burst.System.particleCount == 0) burst.Busy = false;
        }

        private void Explode(Burst burst)
        {
            burst.Exploded = true;
            Vector3 top = burst.Ground + burst.Up * (burst.Size * 2.5f);

            ParticleSystem.EmitParams flash = new ParticleSystem.EmitParams
            {
                position = top,
                velocity = Vector3.zero,
                startSize = burst.Size * 0.9f,
                startLifetime = 0.25f,
                startColor = Color.Lerp(burst.Colour, Color.white, 0.6f),
            };
            burst.System.Emit(flash, 1);

            int sparks = UnityEngine.Random.Range(70, 110);
            float speed = burst.Size * 1.5f;
            for (int i = 0; i < sparks; i++)
            {
                ParticleSystem.EmitParams spark = new ParticleSystem.EmitParams
                {
                    position = top,
                    velocity = UnityEngine.Random.onUnitSphere * (speed * UnityEngine.Random.Range(0.85f, 1.15f)),
                    startSize = burst.Size * UnityEngine.Random.Range(0.18f, 0.26f),
                    startLifetime = UnityEngine.Random.Range(1.6f, 2.4f),
                    startColor = (i & 1) == 0 ? burst.Colour : burst.Second,
                };
                burst.System.Emit(spark, 1);
            }
        }

        // The self-check: where the scene put Kerbin, the sun and the camera, and
        // how much of the night side the camera sees, from points taken as a burst
        // takes them.
        private string Describe()
        {
            int hits = 0;
            float left = 1f, right = 0f, bottom = 1f, topY = 0f;
            const int Samples = 400;
            for (int i = 0; i < Samples; i++)
            {
                Vector3 ground, up;
                if (!PickPoint(out ground, out up)) continue;
                hits++;
                Vector3 s = view.WorldToViewportPoint(ground);
                left = Mathf.Min(left, s.x);
                right = Mathf.Max(right, s.x);
                bottom = Mathf.Min(bottom, s.y);
                topY = Mathf.Max(topY, s.y);
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            string where = hits == 0
                ? "no visible night side found"
                : hits + "/" + Samples + " picks land, screen x " + left.ToString("0.00", c) + ".." + right.ToString("0.00", c)
                  + ", y " + bottom.ToString("0.00", c) + ".." + topY.ToString("0.00", c);
            return "'" + kerbin.name + "' in " + (kerbin.parent != null ? kerbin.parent.name : "the scene") + ", radius " + radius.ToString("0", c)
                   + ", " + Vector3.Distance(view.transform.position, kerbin.position).ToString("0", c) + " from the camera '"
                   + view.name + "', sun '" + sun.name + "'; " + where + "; shader " + material.shader.name
                   + "; " + PoolSize + " bursts at most at once, "
                   + (test ? "one every " + TestInterval.ToString("0.0", c) + " s (" + BurstArgument + ")"
                           : "one every 0.3-0.8 s, a quarter of them with 1-3 more")
                   + (Enabled() ? "." : "; switched off in the settings.");
        }
    }
}
