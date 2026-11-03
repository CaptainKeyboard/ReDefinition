using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // Parallax's scatters as the rays see them: the buffers of every active renderer, the materials, the point
    // each is culled about and how far the grass is drawn. Read through Scenery.ParallaxScatterFields; off for
    // the run once reading them failed.
    internal static class ParallaxScatterReader
    {
        // Whether reading the scatters, or only the grass's range, failed; the grass's range about the camera and
        // about the vessel, the farthest level's start, and when they were read.
        private static bool parallaxFailed;
        private static bool grassRangeFailed;
        private static float grassRange, grassRangeAboutVessel, grassLodStart;
        private static float grassRangeAt = float.MinValue;

        // When the grass was last noted in the log, whether a read of its buffers is under way, and the counts read.
        private static float grassNotedAt = -100f;
        private static bool grassReading;
        private static ComputeBuffer grassCounts;

        // One of Parallax's grass buffers the note may read: its renderer, level of detail and name.
        private sealed class GrassBuffer
        {
            public object Renderer;
            public int Lod;
            public string Name;
            public ComputeBuffer Buffer;
        }

        // Whether a scatter's name says it is grass.
        internal static bool IsGrass(string name)
        {
            return name != null && name.IndexOf("Grass", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Whether it is the main grass: grass, neither tall nor dry.
        internal static bool IsMainGrass(string name)
        {
            return IsGrass(name)
                   && name.IndexOf("Tall", StringComparison.OrdinalIgnoreCase) < 0
                   && name.IndexOf("Dry", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // The distance from its culling point past which the rays leave an instance out, for a scatter drawn
        // within `range`: a tenth over the range and 20 m.
        internal static float ReachRadius(float range)
        {
            return range * 1.1f + 20f;
        }

        // The lowest of the distances at which a grass turns to its farthest level of detail; 0 for none yet.
        // A start of 0 or less is no start.
        internal static float NearestLodStart(float lowest, float start)
        {
            if (start <= 0f) return lowest;
            return lowest > 0f ? Mathf.Min(lowest, start) : start;
        }

        // Where Parallax's main grass lies against the camera and the point it culls about, once a minute in the log.
        // It names the blades' distances from the camera in 50 m steps and the share past the range. It reads the
        // first level of detail that holds blades. A patch of grass the rays see and the picture does not (the space
        // centre, 2026-10-26) can then be told from the log alone. The buffers are read back without a stall: the
        // blades' counts first, then the blades of the first buffer that holds any.
        private static void NoteGrass(IList renderers)
        {
            if (Time.realtimeSinceStartup - grassNotedAt < (grassReading ? 180f : 60f)) return;
            grassNotedAt = Time.realtimeSinceStartup;
            if (!SystemInfo.supportsAsyncGPUReadback) return;
            Camera main = Camera.main;
            if (main == null) return;
            var found = new List<GrassBuffer>();
            for (int r = 0; r < renderers.Count; r++)
            {
                string name = Scenery.ParallaxScatterFields.ScatterName(renderers[r]);
                if (!IsMainGrass(name)) continue;
                for (int lod = 0; lod < 3; lod++)
                {
                    ComputeBuffer buffer = Scenery.ParallaxScatterFields.Output(renderers[r], lod);
                    if (buffer != null) found.Add(new GrassBuffer { Renderer = renderers[r], Lod = lod, Name = name, Buffer = buffer });
                }
            }
            if (found.Count == 0) return;
            try
            {
                if (grassCounts == null || grassCounts.count < found.Count)
                {
                    if (grassCounts != null) grassCounts.Release();
                    grassCounts = new ComputeBuffer(Math.Max(found.Count, 16), 4, ComputeBufferType.Raw);
                }
                for (int i = 0; i < found.Count; i++) ComputeBuffer.CopyCount(found[i].Buffer, grassCounts, i * 4);
                grassReading = true;
                AsyncGPUReadback.Request(grassCounts, found.Count * 4, 0, request => GrassCounted(request, found, main));
            }
            catch (Exception)
            {
                // A diagnostic only: the buffers could not be read.
                grassReading = false;
            }
        }

        // The counts are back: the blades of the first buffer that holds any are asked for.
        private static void GrassCounted(AsyncGPUReadbackRequest request, List<GrassBuffer> found, Camera main)
        {
            bool asked = false;
            try
            {
                if (request.hasError) return;
                Unity.Collections.NativeArray<int> taken = request.GetData<int>();
                for (int i = 0; i < found.Count && i < taken.Length; i++)
                {
                    GrassBuffer grass = found[i];
                    int blades = grass.Buffer.IsValid() ? Mathf.Min(taken[i], grass.Buffer.count) : 0;
                    if (blades == 0) continue;
                    asked = true;
                    AsyncGPUReadback.Request(grass.Buffer, blades * 64, 0, data => GrassRead(data, grass, blades, main));
                    return;
                }
            }
            catch (Exception)
            {
                // A diagnostic only: Parallax's buffers changed under the read.
            }
            finally
            {
                if (!asked) grassReading = false;
            }
        }

        // The blades are back: their distances from the camera and past the range, in the log.
        private static void GrassRead(AsyncGPUReadbackRequest request, GrassBuffer grass, int blades, Camera main)
        {
            try
            {
                if (request.hasError || main == null) return;
                Unity.Collections.NativeArray<float> data = request.GetData<float>();
                if (data.Length < blades * 16) return;
                object renderer = grass.Renderer;
                Vector3 eye = main.transform.position;
                Vector3 about = Scenery.ParallaxScatterFields.AboutVessel(renderer)
                    ? Scenery.ParallaxScatterFields.CraftPosition() : Scenery.ParallaxScatterFields.CameraPosition();
                float range = Scenery.ParallaxScatterFields.Range(renderer);
                var bins = new int[8];
                float nearest = float.MaxValue, farthest = 0f;
                int beyond = 0;
                for (int i = 0; i < blades; i++)
                {
                    var at = new Vector3(data[i * 16 + 12], data[i * 16 + 13], data[i * 16 + 14]);
                    float d = (at - eye).magnitude;
                    nearest = Mathf.Min(nearest, d);
                    farthest = Mathf.Max(farthest, d);
                    bins[Mathf.Min(7, (int)(d / 50f))]++;
                    if ((at - about).magnitude > ReachRadius(range)) beyond++;
                }
                Debug.Log(Core.Log.Tag + " Parallax's " + grass.Name + " in " + HighLogic.LoadedScene + ", level " + grass.Lod + ": " + blades
                          + " blades, " + nearest.ToString("F0") + " to " + farthest.ToString("F0") + " m from the camera ("
                          + string.Join("/", Array.ConvertAll(bins, b => b.ToString())) + " per 50 m), range " + range.ToString("F0")
                          + " m (farthest level from " + Scenery.ParallaxScatterFields.Lod2Start(renderer).ToString("F0") + " m) about "
                          + (Scenery.ParallaxScatterFields.AboutVessel(renderer) ? "the vessel" : "the camera") + " at "
                          + about + ", the camera at " + eye + ", " + (beyond * 100 / blades) + " % past the range.");
            }
            catch (Exception)
            {
                // A diagnostic only: Parallax went away under the read.
            }
            finally
            {
                grassReading = false;
            }
        }

        // Parallax's scatter this frame: per active renderer and level of detail, the
        // append buffer its evaluation filled in LateUpdate, with its mesh and material.
        // A shared scatter draws its parent's buffers with a mesh of its own: both are
        // there.
        internal static int Collect(List<RtGpuItem> into)
        {
            if (parallaxFailed || !Scenery.ParallaxScatterFields.Available) return 0;
            int buffers = 0;
            try
            {
                IList renderers = Scenery.ParallaxScatterFields.Renderers();
                if (renderers == null) return 0;
                NoteGrass(renderers);
                for (int r = 0; r < renderers.Count; r++)
                {
                    object renderer = renderers[r];
                    if (renderer == null) continue;
                    bool aboutVessel = Scenery.ParallaxScatterFields.AboutVessel(renderer);
                    string scatterName = Scenery.ParallaxScatterFields.ScatterName(renderer);
                    // The grass's farthest level of detail is blades the picture shows as the terrain's own look: far past
                    // a pixel each, and drawn with the terrain's colour. The rays would shade the ground with them, as
                    // a dark patch within the range and the meadow's shade beyond it (MeadowShade takes the far ground).
                    bool grass = IsGrass(scatterName);
                    for (int lod = 0; lod < (grass ? 2 : 3); lod++)
                    {
                        // Where the space centre's grass is lifted, its clumps under the
                        // space centre's ground left out (Scenery.KscGrass.RaysBuffer).
                        ComputeBuffer buffer = Scenery.KscGrass.RaysBuffer(renderer, lod,
                                                                           Scenery.ParallaxScatterFields.Output(renderer, lod));
                        Mesh mesh = Scenery.ParallaxScatterFields.Mesh(renderer, lod);
                        if (buffer == null || !buffer.IsValid() || mesh == null) continue;
                        // Parallax draws a scatter within its range of the point it culls it about (the vessel's or the
                        // camera's, which it keeps itself). Instances beyond it are not drawn about the viewer, and a
                        // ray that meets one meets grass the picture has none of: at the space centre, 800 to 1250 m
                        // from the camera of a grass 256 m in range (2026-10-26).
                        Vector3 about = aboutVessel ? Scenery.ParallaxScatterFields.CraftPosition() : Scenery.ParallaxScatterFields.CameraPosition();
                        float range = Scenery.ParallaxScatterFields.Range(renderer);
                        into.Add(new RtGpuItem
                        {
                            Transforms = buffer,
                            Mesh = mesh,
                            SubMesh = 0,
                            Material = Scenery.ParallaxScatterFields.Material(renderer, lod),
                            Mask = 0xFF,
                            Reach = range > 0f ? new Vector4(about.x, about.y, about.z, ReachRadius(range)) : Vector4.zero,
                        });
                        buffers++;
                    }
                }
            }
            catch (Exception e)
            {
                // Another version of Parallax: its scatter stays out of the rays.
                parallaxFailed = true;
                Core.CompatibilityLog.Warn("rt-parallax-scatter", "Raytracing: Parallax's scatter is left out of the rays ("
                                                                  + Core.CompatibilityLog.Reason(e) + ").");
            }
            return buffers;
        }

        // Where Parallax draws its scatters about the vessel from: its own point (RuntimeOperations.vectorCraftPos),
        // which at the space centre is the world's origin and not the camera. `camera` where Parallax cannot be read.
        internal static Vector3 Vessel(Vector3 camera)
        {
            return Scenery.ParallaxScatterFields.Available ? Scenery.ParallaxScatterFields.CraftPosition() : camera;
        }

        // The nearest distance from the camera at which the vessel-centred grass turns to its farthest level of detail,
        // 0 for none (set by GrassRange).
        internal static float GrassLodStart { get { return grassLodStart; } }

        // How far Parallax draws its grass, in metres. The result is the longest range of the active scatters named
        // grass that it culls about the camera. `aboutVessel` is the longest range of those it culls about the active
        // vessel (Scenery.ParallaxScatterFields.AboutVessel). Kerbin's main grass is 320 m of the vessel in
        // Parallax_StockScatterTextures, its tall grass 400 m of the camera. Each is 0 where none is (or another
        // version of Parallax). Asked once a second.
        internal static float GrassRange(out float aboutVessel)
        {
            float now = Time.realtimeSinceStartup;
            aboutVessel = grassRangeAboutVessel;
            if (now - grassRangeAt < 1f) return grassRange;
            grassRangeAt = now;
            grassRange = grassRangeAboutVessel = aboutVessel = grassLodStart = 0f;
            if (parallaxFailed || grassRangeFailed || !Scenery.ParallaxScatterFields.Available) return 0f;
            try
            {
                IList renderers = Scenery.ParallaxScatterFields.Renderers();
                if (renderers == null) return 0f;
                foreach (object renderer in renderers)
                {
                    string name = Scenery.ParallaxScatterFields.ScatterName(renderer);
                    if (!IsGrass(name)) continue;
                    float range = Scenery.ParallaxScatterFields.Range(renderer);
                    if (Scenery.ParallaxScatterFields.AboutVessel(renderer))
                    {
                        grassRangeAboutVessel = Mathf.Max(grassRangeAboutVessel, range);
                        grassLodStart = NearestLodStart(grassLodStart, Scenery.ParallaxScatterFields.Lod2Start(renderer));
                    }
                    else
                        grassRange = Mathf.Max(grassRange, range);
                }
            }
            catch (Exception)
            {
                grassRangeFailed = true;
                grassRange = grassRangeAboutVessel = 0f;
            }
            aboutVessel = grassRangeAboutVessel;
            return grassRange;
        }

        // The materials Parallax draws its scatter with this scene, every active renderer's
        // three levels of detail.
        internal static void Materials(List<Material> into)
        {
            if (parallaxFailed || !Scenery.ParallaxScatterFields.Available) return;
            try
            {
                IList renderers = Scenery.ParallaxScatterFields.Renderers();
                if (renderers == null) return;
                for (int r = 0; r < renderers.Count; r++)
                {
                    object renderer = renderers[r];
                    if (renderer == null) continue;
                    for (int lod = 0; lod < 3; lod++)
                    {
                        Material material = Scenery.ParallaxScatterFields.Material(renderer, lod);
                        if (material != null) into.Add(material);
                    }
                }
            }
            catch (Exception)
            {
                // Another version of Parallax: its scatter keeps its textures as they are.
            }
        }
    }
}
