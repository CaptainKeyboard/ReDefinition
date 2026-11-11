using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The season on Kerbin's scaled-space planet, which orbit and map view draw. The scaled body's material reads one colour map
    // (Parallax's Custom/ParallaxScaled: _ColorMap; the stock scaled planet shaders and Kopernicus: _MainTex). This component copies
    // that map into a render texture (base copy), draws the snow mask over it into a second one (target) and sets the target as the
    // material's map. The mask is OrbitMask: the ground's SnowMask.Coverage, with heights read from the PQS (OrbitHeights). The
    // textures are OrbitTextures.
    //
    // The mask is made on a worker thread. The main thread uploads 2 MB, draws one quad and builds the mips. A composition runs at
    // most every 30 s of real time, and after 2 s if the phase moved by 0.05 of a year (OrbitMask.Due). The material's map is polled
    // once a second; a new one from Parallax becomes the base copy, and a lost base copy is copied from the original again. A
    // failing worker switches the orbit snow off for the session. A result that arrives after the original map was put back is
    // dropped (the generation counter). The planet's own texture is never written to. Memory for both maps with mips: 7 MB at 1024
    // wide, 29 MB at 2048, 117 MB at 4096.
    internal sealed class OrbitSnow : MonoBehaviour
    {
        // Seconds of real time between two looks at the material and between two checks of the schedule.
        private const float PollSeconds = 1f;

        // The least mask texture width the original has to have to be taken for a real map (1 x 1 placeholders while it loads are not).
        private const int LeastOriginalWidth = 256;

        // The colour map properties of the shaders known to draw the scaled planet, in the order they are tried.
        private static readonly string[] MapProperties = { "_ColorMap", "_MainTex" };

        private MeshRenderer renderer;
        private Material material;
        private string property;
        private Texture original;
        private readonly OrbitTextures textures = new OrbitTextures();
        private readonly OrbitHeights heights = new OrbitHeights();
        private float nextPoll;
        private bool unavailableLogged;
        private bool broken;
        private string lastWhy;

        // The work of the worker thread. A result counts only if it carries the generation of now: Restore starts a new one.
        private sbyte[] noise;
        private volatile bool noiseReady;
        private bool noiseStarted;
        private volatile int generation;
        private volatile int maskReadyGeneration = -1;
        private volatile string workerError;
        private bool workerBusy;
        private byte[] maskBytes;
        private double workerPhase;
        private float workerStrength;
        private double workerLine;
        private int workerHeightsVersion;
        private long workerMilliseconds;

        // What the target holds now.
        private double composedPhase = double.NaN;
        private float composedStrength = -1f;
        private double composedLine = -1.0;
        private int composedHeightsVersion = -1;
        private float composedAt = -1000f;
        private bool needsCompose;
        private int composedCount;

        private void Update()
        {
            if (!AtmospheresSettings.Enabled || !AtmospheresSettings.OrbitSnow)
            {
                if (textures.Target != null) Restore();
                return;
            }
            if (broken) return;
            try
            {
                string error = workerError;
                if (error != null)
                {
                    Fail("a worker thread failed (" + error + ")");
                    return;
                }
                float now = Time.realtimeSinceStartup;
                if (now >= nextPoll)
                {
                    nextPoll = now + PollSeconds;
                    Poll();
                }
                if (textures.Target == null) return;
                if (!noiseStarted) StartNoise();
                SampleHeights();
                if (workerBusy && maskReadyGeneration == generation) Upload();
                if (needsCompose && textures.Target != null && textures.Mask != null) Compose();
                ScheduleWorker(now);
            }
            catch (Exception e)
            {
                Fail(CompatibilityLog.Reason(e));
            }
        }

        // Switches the orbit snow off for this session and puts the original map back.
        private void Fail(string reason)
        {
            broken = true;
            AtmospheresLog.WarnOnce("orbit snow failed and is switched off for this session (" + reason + ").");
            Restore();
        }

        private void OnDestroy()
        {
            Restore();
        }

        private void OnApplicationQuit()
        {
            Restore();
        }

        // The material's map now; null where the material has no such property or no map in it.
        private Texture MapNow()
        {
            return material != null && property != null && material.HasProperty(property) ? material.GetTexture(property) : null;
        }

        // Finds Kerbin's scaled-body material, and follows a change of its map.
        private void Poll()
        {
            if (FlightGlobals.fetch == null || FlightGlobals.Bodies == null) { Why("no FlightGlobals in this scene"); return; }
            CelestialBody kerbin = FlightGlobals.GetBodyByName(SeasonClock.BodyName);
            if (kerbin == null || kerbin.scaledBody == null) { Why("Kerbin or its scaled body is not there"); return; }
            if (renderer == null || renderer.gameObject != kerbin.scaledBody)
            {
                renderer = kerbin.scaledBody.GetComponent<MeshRenderer>();
                if (renderer == null) renderer = kerbin.scaledBody.GetComponentInChildren<MeshRenderer>(true);
                if (renderer == null) { Why("the scaled body has no renderer"); return; }
            }
            Material current = renderer.sharedMaterial;
            if (current == null) { Why("the scaled body has no material"); return; }
            if (!ReferenceEquals(current, material))
            {
                Restore();
                material = current;
                property = null;
            }
            if (property != null && !material.HasProperty(property))
            {
                // The shader of the material changed: the map is gone with its property.
                Restore();
                return;
            }
            Texture held = MapNow();
            if (property == null || held != null && !ReferenceEquals(held, textures.Target) && held.width < LeastOriginalWidth)
            {
                // The widest of the known colour maps: a scene can leave a small stand-in in one of them while the real map is in another.
                int widest = 0;
                string widestName = null;
                for (int i = 0; i < MapProperties.Length; i++)
                {
                    if (!material.HasProperty(MapProperties[i])) continue;
                    Texture candidate = material.GetTexture(MapProperties[i]);
                    if (candidate != null && candidate.width > widest)
                    {
                        widest = candidate.width;
                        widestName = MapProperties[i];
                    }
                }
                property = widestName;
                if (property == null || widest < LeastOriginalWidth) Why("small-maps", () => "the textures of " + material.shader.name + " are " + TextureList(material));
                if (property == null)
                {
                    if (!unavailableLogged)
                    {
                        unavailableLogged = true;
                        AtmospheresLog.Say("orbit snow: Kerbin's scaled body material (shader " + material.shader.name
                                       + ") has no colour map yet in " + string.Join(", ", MapProperties) + "; looking again every second.");
                    }
                    material = null;
                    return;
                }
            }
            Texture now = MapNow();
            if (now == null) { Why("map-empty", () => "the colour map " + property + " is empty"); return; }
            if (ReferenceEquals(now, textures.Target)) return;
            if (now.width < LeastOriginalWidth) { Why("map-small", () => "the colour map " + property + " is " + now.width + " wide, under " + LeastOriginalWidth); return; }
            Capture(now);
        }

        // The texture properties of a material with their sizes, for the log.
        private static string TextureList(Material m)
        {
            var parts = new System.Text.StringBuilder();
            try
            {
                foreach (string name in m.GetTexturePropertyNames())
                {
                    Texture t = m.GetTexture(name);
                    if (parts.Length > 0) parts.Append(", ");
                    parts.Append(name).Append(t == null ? " none" : " " + t.width + "x" + t.height);
                }
            }
            catch (Exception e)
            {
                parts.Append("not readable: ").Append(e.GetType().Name);
            }
            return parts.ToString();
        }

        // The reason the map was not taken, logged when it changes: a silent poll is hard to read from the outside.
        private void Why(string reason)
        {
            if (reason == lastWhy) return;
            lastWhy = reason;
            AtmospheresLog.Say("orbit snow waits: " + reason + ".");
        }

        // The same for a reason with details: the text is made only when the reason (its key) is new.
        private void Why(string key, Func<string> detail)
        {
            if (key == lastWhy) return;
            lastWhy = key;
            AtmospheresLog.Say("orbit snow waits: " + detail() + ".");
        }

        // Takes the material's map as the original: copies it into the base copy and prepares the target and the overlay.
        private void Capture(Texture source)
        {
            int width, height;
            OrbitMask.WorkingSize(source.width, source.height, AtmospheresSettings.OrbitSnowWidth, out width, out height);
            textures.Ensure(width, height, source.anisoLevel);
            textures.FillBase(source);
            original = source;
            if (!textures.EnsureOverlay())
            {
                Fail("no shader found to draw the mask with");
                return;
            }
            AtmospheresLog.Say("orbit snow: Kerbin's scaled body material uses shader " + material.shader.name + ", colour map property " + property
                           + ", original " + source.width + "x" + source.height + " " + (source is Texture2D ? ((Texture2D)source).format.ToString() : source.GetType().Name)
                           + "; working maps " + width + "x" + height + " (" + (OrbitMask.Bytes(width, height) / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)
                           + " MB of video memory), overlay shader " + textures.Overlay.shader.name + (textures.OverlayKeepsAlpha ? " (alpha channel kept)" : " (alpha channel not kept)")
                           + ", colour space " + QualitySettings.activeColorSpace + ", composed on a worker thread at most every "
                           + OrbitMask.SmallStepSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
            composedPhase = double.NaN;
            composedCount = 0;
            needsCompose = false;
            // The first composition comes from the worker; until it is there the material keeps its own map.
        }

        private void StartNoise()
        {
            noiseStarted = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    noise = OrbitMask.BuildNoise(OrbitMask.MaskWidth, OrbitMask.MaskHeight);
                }
                catch (Exception e)
                {
                    noise = null;
                    workerError = "noise table: " + e.GetType().Name + " " + e.Message;
                }
                finally
                {
                    noiseReady = true;
                }
            });
        }

        // Reads the planet's heights from the PQS for a few texels per frame until the grid is complete.
        private void SampleHeights()
        {
            if (heights.Complete) return;
            CelestialBody kerbin = FlightGlobals.GetBodyByName(SeasonClock.BodyName);
            heights.Sample(kerbin != null ? kerbin.pqsController : null);
        }

        private void ScheduleWorker(float now)
        {
            if (workerBusy || !noiseReady || noise == null || textures.Target == null) return;
            double? clock = SeasonState.Phase();
            if (!clock.HasValue) return;
            double phase = clock.Value;
            float strength = AtmospheresSettings.SnowStrength;
            double line = AtmospheresSettings.WinterSnowLine;
            // The first composition after a capture is made at once; later ones follow the schedule.
            bool first = composedCount == 0;
            bool inputsChanged = composedStrength != strength || composedLine != line || composedHeightsVersion != heights.Version;
            if (!first && !OrbitMask.Due(composedPhase, phase, now - composedAt, inputsChanged)) return;
            workerBusy = true;
            workerPhase = phase;
            workerStrength = strength;
            workerLine = line;
            float[] heightCopy = heights.CopyIfComplete();
            sbyte[] noiseCopy = noise;
            if (maskBytes == null) maskBytes = new byte[OrbitMask.MaskWidth * OrbitMask.MaskHeight * 4];
            byte[] buffer = maskBytes;
            workerHeightsVersion = heightCopy != null ? heights.Version : 0;
            int myGeneration = generation;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    OrbitMask.Fill(buffer, OrbitMask.MaskWidth, OrbitMask.MaskHeight, noiseCopy, heightCopy, OrbitMask.HeightWidth, OrbitMask.HeightHeight,
                        phase, strength, line);
                }
                catch (Exception e)
                {
                    workerError = "mask: " + e.GetType().Name + " " + e.Message;
                }
                finally
                {
                    workerMilliseconds = watch.ElapsedMilliseconds;
                    // A result of an older generation (the original map was put back meanwhile) is dropped.
                    if (myGeneration == generation) maskReadyGeneration = myGeneration;
                }
            });
        }

        // The worker is done: the mask goes to the texture.
        private void Upload()
        {
            maskReadyGeneration = -1;
            workerBusy = false;
            if (textures.Mask == null || maskBytes == null) return;
            textures.Mask.LoadRawTextureData(maskBytes);
            textures.Mask.Apply(false, false);
            composedPhase = workerPhase;
            composedStrength = workerStrength;
            composedLine = workerLine;
            composedHeightsVersion = workerHeightsVersion;
            composedAt = Time.realtimeSinceStartup;
            needsCompose = true;
            composedCount++;
        }

        // Draws the mask over the base copy into the target and sets the target as the material's map.
        private void Compose()
        {
            needsCompose = false;
            if (textures.BaseLost)
            {
                // The base copy is gone: copy the original again; the next composition comes from the worker.
                Texture source = original;
                if (source == null)
                {
                    Restore();
                    return;
                }
                AtmospheresLog.Say("orbit snow: the base copy of the colour map was lost; copying the original again.");
                Capture(source);
                return;
            }
            if (!material.HasProperty(property))
            {
                Restore();
                return;
            }
            textures.Compose();
            if (!ReferenceEquals(material.GetTexture(property), textures.Target)) material.SetTexture(property, textures.Target);
            AtmospheresLog.Say("orbit snow composed " + composedCount + " for phase " + composedPhase.ToString("0.000", CultureInfo.InvariantCulture)
                           + " (strength " + composedStrength.ToString("0.00", CultureInfo.InvariantCulture) + ", winter line " + composedLine.ToString("0", CultureInfo.InvariantCulture)
                           + " deg, heights " + (heights.Complete ? "read" : "not yet") + "), mask made in " + workerMilliseconds + " ms on the worker, map "
                           + textures.Target.width + "x" + textures.Target.height + " set as " + property + " of " + material.shader.name);
        }

        // Puts the original map back and frees what was made.
        private void Restore()
        {
            // A worker still running belongs to the generation that ends here; its result is dropped and its buffer is not used again.
            generation++;
            maskReadyGeneration = -1;
            workerBusy = false;
            maskBytes = null;
            try
            {
                if (material != null && property != null && textures.Target != null && original != null && material.HasProperty(property)
                    && ReferenceEquals(material.GetTexture(property), textures.Target))
                {
                    material.SetTexture(property, original);
                    AtmospheresLog.Say("orbit snow: the original colour map of the scaled body is back.");
                }
            }
            catch (Exception)
            {
                // The material is gone with its scene.
            }
            textures.ReleaseAll();
            original = null;
            property = null;
            material = null;
            composedPhase = double.NaN;
            composedCount = 0;
            needsCompose = false;
        }
    }
}
