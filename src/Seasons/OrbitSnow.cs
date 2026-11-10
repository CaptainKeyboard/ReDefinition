using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using ReDefinition.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ReDefinition.Seasons
{
    // The season on Kerbin's scaled-space planet, which orbit and map view draw. The scaled body's material reads one colour map
    // (Parallax's Custom/ParallaxScaled: _ColorMap; the stock scaled planet shaders and Kopernicus: _MainTex). This component copies
    // that map into a render texture (base copy), draws the snow mask over it into a second one (target) and sets the target as the
    // material's map. The mask is OrbitMask: the ground's SnowMask.Coverage, with heights read from the PQS.
    //
    // The mask is made on a worker thread. The main thread uploads 2 MB, draws one quad and builds the mips.
    // A composition runs at most every 30 s of real time, and after 2 s if the phase moved by 0.05 of a year.
    // The material's map is polled once a second. If Parallax loaded a new one, it becomes the base copy.
    // At the end the original map is put back. The planet's own texture is never written to.
    // Memory, 4 bytes a texel for both maps with mips: 7 MB at 1024 wide, 29 MB at 2048, 117 MB at 4096.
    internal sealed class OrbitSnow : MonoBehaviour
    {
        // Seconds of real time between two looks at the material and between two checks of the schedule.
        private const float PollSeconds = 1f;

        // The time one frame spends on reading the planet's heights, in milliseconds.
        private const long HeightBudgetMilliseconds = 2;

        // The least mask texture width the original has to have to be taken for a real map (1 x 1 placeholders while it loads are not).
        private const int LeastOriginalWidth = 256;

        // The colour map properties of the shaders known to draw the scaled planet, in the order they are tried.
        private static readonly string[] MapProperties = { "_ColorMap", "_MainTex" };

        private static readonly string[] OverlayNames = { "UI/Default" };

        private MeshRenderer renderer;
        private Material material;
        private string property;
        private Texture original;
        private RenderTexture baseCopy;
        private RenderTexture target;
        private Texture2D maskTexture;
        private Material overlay;
        private bool overlayKeepsAlpha;
        private float nextPoll;
        private bool unavailableLogged;
        private bool broken;

        // The work of the worker thread.
        private sbyte[] noise;
        private volatile bool noiseReady;
        private bool noiseStarted;
        private volatile bool maskReady;
        private bool workerBusy;
        private byte[] maskBytes;
        private double workerPhase;
        private float workerStrength;
        private double workerLine;
        private int workerHeightsVersion;
        private long workerMilliseconds;

        // The heights of the planet, sampled a few at a time from the PQS.
        private float[] heights;
        private int heightsDone;
        private bool heightsComplete;
        private bool heightsFailedLogged;
        private int heightsVersion;

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
            if (broken || !SeasonsSettings.Enabled || !SeasonsSettings.OrbitSnow) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now >= nextPoll)
                {
                    nextPoll = now + PollSeconds;
                    Poll();
                }
                if (target == null) return;
                if (!noiseStarted) StartNoise();
                SampleHeights();
                if (maskReady) Upload();
                if (needsCompose && target != null && maskTexture != null) Compose();
                ScheduleWorker(now);
            }
            catch (Exception e)
            {
                broken = true;
                Debug.LogWarning(Log.Tag + " Seasons: orbit snow failed and is switched off for this session (" + CompatibilityLog.Reason(e) + ").");
                Restore();
            }
        }

        private void OnDestroy()
        {
            Restore();
        }

        private void OnApplicationQuit()
        {
            Restore();
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
            if (property == null || !ReferenceEquals(material.GetTexture(property), target) && material.GetTexture(property) != null
                && material.GetTexture(property).width < LeastOriginalWidth)
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
                if (property == null || widest < LeastOriginalWidth) Why("the textures of " + material.shader.name + " are " + TextureList(material));
                if (property == null)
                {
                    if (!unavailableLogged)
                    {
                        unavailableLogged = true;
                        Debug.Log(Log.Tag + " Seasons: orbit snow: Kerbin's scaled body material (shader " + material.shader.name
                                  + ") has no colour map yet in " + string.Join(", ", MapProperties) + "; looking again every second.");
                    }
                    material = null;
                    return;
                }
            }
            Texture now = material.GetTexture(property);
            if (now == null) { Why("the colour map " + property + " is empty"); return; }
            if (ReferenceEquals(now, target)) return;
            if (now.width < LeastOriginalWidth) { Why("the colour map " + property + " is " + now.width + " wide, under " + LeastOriginalWidth); return; }
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
        private string lastWhy;

        private void Why(string reason)
        {
            if (reason == lastWhy) return;
            lastWhy = reason;
            Debug.Log(Log.Tag + " Seasons: orbit snow waits: " + reason + ".");
        }

        // Takes the material's map as the original: copies it into the base copy and prepares the target and the overlay.
        private void Capture(Texture source)
        {
            int width, height;
            OrbitMask.WorkingSize(source.width, source.height, SeasonsSettings.OrbitSnowWidth, out width, out height);
            bool newSize = baseCopy == null || baseCopy.width != width || baseCopy.height != height;
            if (newSize)
            {
                ReleaseTextures();
                baseCopy = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "ReDefinition Seasons orbit base", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat };
                baseCopy.Create();
                target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "ReDefinition Seasons orbit map",
                    useMipMap = true,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Repeat,
                    anisoLevel = Mathf.Max(1, source.anisoLevel)
                };
                target.Create();
            }
            Graphics.Blit(source, baseCopy);
            original = source;
            if (overlay == null) MakeOverlay();
            if (overlay == null)
            {
                broken = true;
                Debug.LogWarning(Log.Tag + " Seasons: orbit snow: no shader found to draw the mask with; orbit snow is off.");
                Restore();
                return;
            }
            if (maskTexture == null)
            {
                maskTexture = new Texture2D(OrbitMask.MaskWidth, OrbitMask.MaskHeight, TextureFormat.RGBA32, false)
                {
                    name = "ReDefinition Seasons orbit mask",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear
                };
                overlay.mainTexture = maskTexture;
            }
            Debug.Log(Log.Tag + " Seasons: orbit snow: Kerbin's scaled body material uses shader " + material.shader.name + ", colour map property " + property
                      + ", original " + source.width + "x" + source.height + " " + (source is Texture2D ? ((Texture2D)source).format.ToString() : source.GetType().Name)
                      + "; working maps " + width + "x" + height + " (" + (OrbitMask.Bytes(width, height) / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)
                      + " MB of video memory), overlay shader " + overlay.shader.name + (overlayKeepsAlpha ? " (alpha channel kept)" : " (alpha channel not kept)")
                      + ", colour space " + QualitySettings.activeColorSpace + ", composed on a worker thread at most every "
                      + OrbitMask.SmallStepSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
            composedPhase = double.NaN;
            composedCount = 0;
            needsCompose = false;
            // The first composition comes from the worker; until it is there the material keeps its own map.
        }

        private void MakeOverlay()
        {
            Shader shader = StormShaders.Find(OverlayNames);
            if (shader != null)
            {
                overlay = new Material(shader) { name = "ReDefinition Seasons orbit overlay" };
                if (overlay.HasProperty("_ColorMask"))
                {
                    // Red, green and blue only (Alpha 1, Blue 2, Green 4, Red 8): the original's alpha channel stays as it is.
                    overlay.SetFloat("_ColorMask", 14f);
                    overlayKeepsAlpha = true;
                }
                return;
            }
            overlay = StormShaders.Blended(null);
            if (overlay != null) StormShaders.Tint(overlay, Color.white, 1f);
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
                catch (Exception)
                {
                    noise = null;
                }
                noiseReady = true;
            });
        }

        // Reads the planet's heights from the PQS for a few texels per frame until the grid is complete.
        private void SampleHeights()
        {
            if (heightsComplete) return;
            CelestialBody kerbin = FlightGlobals.GetBodyByName(SeasonClock.BodyName);
            PQS pqs = kerbin != null ? kerbin.pqsController : null;
            if (pqs == null || !pqs.isStarted) return;
            if (heights == null) heights = new float[OrbitMask.HeightWidth * OrbitMask.HeightHeight];
            var watch = Stopwatch.StartNew();
            try
            {
                while (heightsDone < heights.Length && watch.ElapsedMilliseconds < HeightBudgetMilliseconds)
                {
                    int row = heightsDone / OrbitMask.HeightWidth, column = heightsDone % OrbitMask.HeightWidth;
                    double x, y, z;
                    OrbitMask.Direction((column + 0.5) / OrbitMask.HeightWidth, (row + 0.5) / OrbitMask.HeightHeight, out x, out y, out z);
                    double h = pqs.GetSurfaceHeight(new Vector3d(x, y, z)) - pqs.radius;
                    heights[heightsDone++] = double.IsNaN(h) || double.IsInfinity(h) ? 0f : (float)h;
                }
            }
            catch (Exception e)
            {
                // The planet's heights are not readable now (the sphere is being rebuilt): start the grid again later.
                heightsDone = 0;
                if (!heightsFailedLogged)
                {
                    heightsFailedLogged = true;
                    Debug.Log(Log.Tag + " Seasons: orbit snow: the planet's heights could not be read yet (" + CompatibilityLog.Reason(e) + "); the snow line has no height bonus until they can.");
                }
                return;
            }
            if (heightsDone >= heights.Length)
            {
                heightsComplete = true;
                heightsVersion++;
                Debug.Log(Log.Tag + " Seasons: orbit snow: planet heights read, " + OrbitMask.HeightWidth + "x" + OrbitMask.HeightHeight + " samples.");
            }
        }

        private void ScheduleWorker(float now)
        {
            if (workerBusy || !noiseReady || noise == null || target == null) return;
            double phase = SeasonState.Phase();
            float strength = SeasonsSettings.SnowStrength;
            double line = SeasonsSettings.WinterSnowLine;
            // The first composition after a capture is made at once; later ones follow the schedule.
            bool first = composedCount == 0;
            bool inputsChanged = composedStrength != strength || composedLine != line || composedHeightsVersion != heightsVersion;
            if (!first && !OrbitMask.Due(composedPhase, phase, now - composedAt, inputsChanged)) return;
            workerBusy = true;
            workerPhase = phase;
            workerStrength = strength;
            workerLine = line;
            float[] heightCopy = heights != null && heightsComplete ? (float[])heights.Clone() : null;
            sbyte[] noiseCopy = noise;
            if (maskBytes == null) maskBytes = new byte[OrbitMask.MaskWidth * OrbitMask.MaskHeight * 4];
            byte[] buffer = maskBytes;
            workerHeightsVersion = heightCopy != null ? heightsVersion : 0;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    OrbitMask.Fill(buffer, OrbitMask.MaskWidth, OrbitMask.MaskHeight, noiseCopy, heightCopy, OrbitMask.HeightWidth, OrbitMask.HeightHeight,
                        phase, strength, line);
                }
                catch (Exception)
                {
                    // The buffer is uploaded anyway; the next pass makes it again.
                }
                workerMilliseconds = watch.ElapsedMilliseconds;
                maskReady = true;
            });
        }

        // The worker is done: the mask goes to the texture.
        private void Upload()
        {
            maskReady = false;
            workerBusy = false;
            if (maskTexture == null || maskBytes == null) return;
            maskTexture.LoadRawTextureData(maskBytes);
            maskTexture.Apply(false, false);
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
            if (baseCopy == null || !baseCopy.IsCreated()) baseCopy.Create();
            if (!target.IsCreated()) target.Create();
            RenderTexture previous = RenderTexture.active;
            bool fog = RenderSettings.fog;
            // The overlay's shader may apply fog; the fog of a storm must not tint the map.
            RenderSettings.fog = false;
            try
            {
                Graphics.Blit(baseCopy, target);
                RenderTexture.active = target;
                GL.PushMatrix();
                GL.LoadOrtho();
                overlay.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.Color(Color.white);
                GL.TexCoord2(0f, 0f);
                GL.Vertex3(0f, 0f, 0f);
                GL.TexCoord2(1f, 0f);
                GL.Vertex3(1f, 0f, 0f);
                GL.TexCoord2(1f, 1f);
                GL.Vertex3(1f, 1f, 0f);
                GL.TexCoord2(0f, 1f);
                GL.Vertex3(0f, 1f, 0f);
                GL.End();
                GL.PopMatrix();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderSettings.fog = fog;
            }
            target.GenerateMips();
            if (!ReferenceEquals(material.GetTexture(property), target)) material.SetTexture(property, target);
            Debug.Log(Log.Tag + " Seasons: orbit snow composed " + composedCount + " for phase " + composedPhase.ToString("0.000", CultureInfo.InvariantCulture)
                      + " (strength " + composedStrength.ToString("0.00", CultureInfo.InvariantCulture) + ", winter line " + composedLine.ToString("0", CultureInfo.InvariantCulture)
                      + " deg, heights " + (heightsComplete ? "read" : "not yet") + "), mask made in " + workerMilliseconds + " ms on the worker, map "
                      + target.width + "x" + target.height + " set as " + property + " of " + material.shader.name);
        }

        // Puts the original map back and frees what was made.
        private void Restore()
        {
            try
            {
                if (material != null && property != null && target != null && ReferenceEquals(material.GetTexture(property), target) && original != null)
                {
                    material.SetTexture(property, original);
                    Debug.Log(Log.Tag + " Seasons: orbit snow: the original colour map of the scaled body is back.");
                }
            }
            catch (Exception)
            {
                // The material is gone with its scene.
            }
            ReleaseTextures();
            if (overlay != null) Destroy(overlay);
            if (maskTexture != null) Destroy(maskTexture);
            overlay = null;
            maskTexture = null;
            original = null;
            property = null;
            material = null;
            composedPhase = double.NaN;
            composedCount = 0;
            needsCompose = false;
        }

        private void ReleaseTextures()
        {
            if (target != null)
            {
                target.Release();
                Destroy(target);
            }
            if (baseCopy != null)
            {
                baseCopy.Release();
                Destroy(baseCopy);
            }
            target = null;
            baseCopy = null;
        }
    }
}
