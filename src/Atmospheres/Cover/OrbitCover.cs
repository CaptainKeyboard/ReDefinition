using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The cover on the scaled-space planet, which orbit and map view draw: the overlay layer of the cover (IOrbitOverlayLayer, order
    // 10: after the planet, before the cloud shell at 40), drawn by the host (OrbitOverlayHost) for each body it picks. The scaled
    // body's material reads one colour map (Parallax's Custom/ParallaxScaled: _ColorMap; the stock scaled planet shaders and Kopernicus:
    // _MainTex). For each body this copies that map into a render texture (base copy), draws the cover mask over it into a second one
    // (target) and sets the target as the material's map. The mask is OrbitMask: the field's depth under the layer's rules, with heights
    // read from the PQS (OrbitHeights). The textures are OrbitTextures.
    //
    // The mask is made on a worker thread. The main thread uploads 2 MB, draws one quad and builds the mips. A composition follows a new
    // frame of the CoverMap after 2 s at the earliest (OrbitMask.Due). The material's map is polled once a second; a new one from
    // Parallax becomes the base copy, and a lost base copy is copied from the original again. A failing worker switches the orbit cover
    // of that body off for the session. A result that arrives after the original map was put back is dropped (the generation counter).
    // The planet's own texture is never written to. Memory for both maps with mips: 7 MB at 1024 wide, 29 MB at 2048, 117 MB at 4096,
    // for each body that is drawn; a body that the host stops drawing gives it back (Release).
    internal sealed class OrbitCover : IOrbitOverlayLayer
    {
        // Seconds of real time between two looks at a material and between two checks of the schedule.
        private const float PollSeconds = 1f;

        // The least mask texture width the original has to have to be taken for a real map (1 x 1 placeholders while it loads are not).
        private const int LeastOriginalWidth = 256;

        // The colour map properties of the shaders known to draw the scaled planet, in the order they are tried.
        private static readonly string[] MapProperties = { "_ColorMap", "_MainTex" };

        // The layer's place among the overlay layers.
        internal const int LayerOrder = 10;

        private readonly System.Collections.Generic.Dictionary<int, BodyOverlay> overlays = new System.Collections.Generic.Dictionary<int, BodyOverlay>();

        // The noise table of the texels, made once on a worker for all bodies (0.5 MB).
        private static sbyte[] noise;
        private static volatile bool noiseReady;
        private static bool noiseStarted;
        private static volatile string noiseError;

        public string Name { get { return "ReDefinition Atmospheres surface cover"; } }

        public int Order { get { return LayerOrder; } }

        // The number of bodies with textures now, for the probe.
        internal int ActiveBodies { get { return overlays.Count; } }

        internal int ComposedCount(CelestialBody body)
        {
            BodyOverlay o;
            return body != null && overlays.TryGetValue(body.flightGlobalsIndex, out o) ? o.ComposedCount : 0;
        }

        // The layer has something for a body that carries cover and has a scaled body, while ground cover and the overlay are on.
        public bool Wants(CelestialBody body)
        {
            return body != null && body.scaledBody != null && AtmospheresSettings.Enabled && AtmospheresSettings.OrbitOverlay
                   && AtmospheresSettings.GroundCover && CoverBodies.Of(body) != null;
        }

        public void Draw(CelestialBody body, bool focused, double ut)
        {
            CoverBodyState state = CoverBodies.Of(body);
            if (state == null) return;
            BodyOverlay o;
            if (!overlays.TryGetValue(body.flightGlobalsIndex, out o)) overlays[body.flightGlobalsIndex] = o = new BodyOverlay(body, state);
            CoverModule.Request(body);
            o.Update(ut);
        }

        // Gives the textures of a body back and puts its original map in the material.
        internal void Release(int bodyIndex)
        {
            BodyOverlay o;
            if (!overlays.TryGetValue(bodyIndex, out o)) return;
            o.Restore();
            overlays.Remove(bodyIndex);
        }

        // Releases the bodies that are not in the list of bodies the host drew this round.
        internal void ReleaseOthers(System.Collections.Generic.HashSet<int> keep)
        {
            var gone = new System.Collections.Generic.List<int>();
            foreach (int key in overlays.Keys)
                if (keep == null || !keep.Contains(key)) gone.Add(key);
            foreach (int key in gone) Release(key);
        }

        internal void ReleaseAll()
        {
            ReleaseOthers(null);
        }

        private static void StartNoise()
        {
            if (noiseStarted) return;
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
                    noiseError = "noise table: " + e.GetType().Name + " " + e.Message;
                }
                finally
                {
                    noiseReady = true;
                }
            });
        }

        // The orbit cover of one body.
        private sealed class BodyOverlay
        {
            private readonly CelestialBody body;
            private readonly CoverBodyState state;
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
            private volatile int generation;
            private volatile int maskReadyGeneration = -1;
            private volatile string workerError;
            private bool workerBusy;
            private byte[] maskBytes;
            private float[] latitudes, longitudes;
            private int workerFrame;
            private float workerStrength;
            private double workerEdge;
            private int workerHeightsVersion;
            private long workerMilliseconds;

            // What the target holds now.
            private int composedFrame;
            private float composedStrength = -1f;
            private double composedEdge = -1.0;
            private int composedHeightsVersion = -1;
            private float composedAt = -1000f;
            private bool needsCompose;

            internal int ComposedCount { get; private set; }

            internal BodyOverlay(CelestialBody body, CoverBodyState state)
            {
                this.body = body;
                this.state = state;
            }

            internal void Update(double ut)
            {
                if (broken) return;
                try
                {
                    string error = workerError ?? noiseError;
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
                    StartNoise();
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

            // Switches the orbit cover of this body off for this session and puts the original map back.
            private void Fail(string reason)
            {
                broken = true;
                AtmospheresLog.WarnOnce("orbit cover of " + body.name + " failed and is switched off for this session (" + reason + ").");
                Restore();
            }

            // The material's map now; null where the material has no such property or no map in it.
            private Texture MapNow()
            {
                return material != null && property != null && material.HasProperty(property) ? material.GetTexture(property) : null;
            }

            // Finds the body's scaled-body material, and follows a change of its map.
            private void Poll()
            {
                if (body.scaledBody == null) { Why("the scaled body is not there"); return; }
                if (renderer == null || renderer.gameObject != body.scaledBody)
                {
                    renderer = body.scaledBody.GetComponent<MeshRenderer>();
                    if (renderer == null) renderer = body.scaledBody.GetComponentInChildren<MeshRenderer>(true);
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
                            AtmospheresLog.Say("orbit cover: " + body.name + "'s scaled body material (shader " + material.shader.name
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
                AtmospheresLog.Say("orbit cover of " + body.name + " waits: " + reason + ".");
            }

            private void Why(string key, Func<string> detail)
            {
                if (key == lastWhy) return;
                lastWhy = key;
                AtmospheresLog.Say("orbit cover of " + body.name + " waits: " + detail() + ".");
            }

            // Takes the material's map as the original: copies it into the base copy and prepares the target and the overlay.
            private void Capture(Texture source)
            {
                int width, height;
                OrbitMask.WorkingSize(source.width, source.height, AtmospheresSettings.OrbitMapWidth, out width, out height);
                textures.Ensure(width, height, source.anisoLevel);
                textures.FillBase(source);
                original = source;
                if (!textures.EnsureOverlay())
                {
                    Fail("no shader found to draw the mask with");
                    return;
                }
                AtmospheresLog.Say("orbit cover: " + body.name + "'s scaled body material uses shader " + material.shader.name + ", colour map property " + property
                               + ", original " + source.width + "x" + source.height + " " + (source is Texture2D ? ((Texture2D)source).format.ToString() : source.GetType().Name)
                               + "; working maps " + width + "x" + height + " (" + (OrbitMask.Bytes(width, height) / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)
                               + " MB of video memory), overlay shader " + textures.Overlay.shader.name + (textures.OverlayKeepsAlpha ? " (alpha channel kept)" : " (alpha channel not kept)")
                               + ", colour space " + QualitySettings.activeColorSpace);
                composedFrame = 0;
                ComposedCount = 0;
                needsCompose = false;
                // The first composition comes from the worker; until it is there the material keeps its own map.
            }

            // Reads the planet's heights from the PQS for a few texels per frame until the grid is complete.
            private void SampleHeights()
            {
                if (heights.Complete) return;
                heights.Sample(body.pqsController);
            }

            private void ScheduleWorker(float now)
            {
                if (workerBusy || !noiseReady || noise == null || textures.Target == null) return;
                CoverMap.Frame frame = state.Map.Current;
                if (frame.Number == 0) return;
                float strength = AtmospheresSettings.CoverStrength;
                double edge = Tuning.Get(CoverLooks.EdgeNoiseKey, CoverLooks.EdgeNoiseDefault);
                bool first = ComposedCount == 0;
                bool inputsChanged = composedStrength != strength || composedEdge != edge || composedHeightsVersion != heights.Version;
                if (!first && !OrbitMask.Due(composedFrame, frame.Number, now - composedAt, inputsChanged)) return;
                workerBusy = true;
                workerFrame = frame.Number;
                workerStrength = strength;
                workerEdge = edge;
                float[] heightCopy = heights.CopyIfComplete();
                sbyte[] noiseCopy = noise;
                if (maskBytes == null) maskBytes = new byte[OrbitMask.MaskWidth * OrbitMask.MaskHeight * 4];
                byte[] buffer = maskBytes;
                workerHeightsVersion = heightCopy != null ? heights.Version : 0;
                KspFrame ksp = state.Frame;
                CoverDef def = state.Def;
                bool ocean = body.ocean;
                float[] lat = latitudes, lon = longitudes;
                int myGeneration = generation;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        if (lat == null)
                        {
                            OrbitMask.BuildCoordinates(OrbitMask.MaskWidth, OrbitMask.MaskHeight, ksp, out lat, out lon);
                            latitudes = lat;
                            longitudes = lon;
                        }
                        OrbitMask.Fill(buffer, OrbitMask.MaskWidth, OrbitMask.MaskHeight, noiseCopy, heightCopy, OrbitMask.HeightWidth, OrbitMask.HeightHeight,
                            lat, lon, frame, def, strength, edge, ocean);
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
                composedFrame = workerFrame;
                composedStrength = workerStrength;
                composedEdge = workerEdge;
                composedHeightsVersion = workerHeightsVersion;
                composedAt = Time.realtimeSinceStartup;
                needsCompose = true;
                ComposedCount++;
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
                    AtmospheresLog.Say("orbit cover: the base copy of " + body.name + "'s colour map was lost; copying the original again.");
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
                AtmospheresLog.Say("orbit cover of " + body.name + " composed " + ComposedCount + " for frame " + composedFrame + " (strength "
                               + composedStrength.ToString("0.00", CultureInfo.InvariantCulture) + ", heights " + (heights.Complete ? "read" : "not yet")
                               + "), mask made in " + workerMilliseconds + " ms on the worker, map " + textures.Target.width + "x" + textures.Target.height
                               + " set as " + property + " of " + material.shader.name);
            }

            // Puts the original map back and frees what was made.
            internal void Restore()
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
                        AtmospheresLog.Say("orbit cover: the original colour map of " + body.name + " is back.");
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
                composedFrame = 0;
                ComposedCount = 0;
                needsCompose = false;
            }
        }
    }
}
