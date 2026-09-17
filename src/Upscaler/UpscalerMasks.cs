using System;
using System.Collections.Generic;
using ReDefinition.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition
{
    // The two masks FSR takes beside colour, depth and motion vectors
    // (docs/development/upscaler.md, "Masks (FSR 3)").
    //
    // Transparency and composition: what the motion vectors do not follow though
    // nothing is blended -- EVE's raymarched clouds, rebuilt over several frames, and
    // Scatterer's ocean, refracting and reflecting. AMD: "a softer alternative
    // compared to the reactive mask, as it only affects locks and color clamping".
    // The clouds' opacity comes from EVE's own history, whose alpha is the
    // transmittance (EVE/CompositeRaymarchedClouds draws background * alpha +
    // colour, disassembled from EVE's bundle), scaled by the fade EVE sets on that
    // composite (DeferredRaymarchedRendererToScreen.SetFade); the ocean's coverage
    // from Scatterer's ocean G-buffer depth, gated by Scatterer's own switch for the
    // camera.
    //
    // Reactive: what is blended over the scene -- particles and their trails,
    // engine effects, re-entry. AMD: "applications should write alpha to the
    // reactive mask ... we recommend clamping the maximum reactive value to around
    // 0.9". Either drawn from the transparent renderers in view with the mask
    // shader -- their texture's alpha, what they add where they blend additively,
    // layers over one another composited like alpha -- and occluded by a copy of
    // the scene's depth in the shader, since a mask target paired with the camera's
    // own depth buffer draws nothing. Or FSR's own generator, a luminance heuristic
    // comparing an opaque-only copy with the image right after the transparent
    // queue; EVE's cloud composite (queue 2998) and Scatterer's sky draw in that
    // queue, so it sees them too. Measured in Unity 2019.4:
    // CommandBuffer.DrawRenderer draws a particle system and, as submesh 1, its
    // trails, and the depth test keeps what is behind the scene out.
    //
    // Recorded at the scene camera's AfterForwardAlpha, anew every frame. Not
    // later: the camera's OnPostRender, where EVE and Scatterer put back the
    // globals the transparency mask reads, runs before BeforeImageEffects
    // (measured), and until then the projection is jittered like the colour.
    // Unity gives the camera its own target back after the buffer -- a buffer after
    // it at the same event draws into the camera's colour, depth-tested (measured).
    // Both masks are off by default.
    internal sealed class UpscalerMasks
    {
        internal enum ReactiveSource
        {
            Off,
            Renderers,
            Automatic,
        }

        internal const float ReactiveMaximum = 0.9f;
        private const int TransparentQueueFrom = 2501;
        private const float ScanInterval = 1f;

        // Transparent queues that are not something blended over the scene: what
        // composites the clouds, the sky and the atmosphere -- the transparency
        // mask's, or the whole sky --, what draws nothing, and decals, which lie
        // still on the ground with their geometry's motion vectors. Read from the
        // transparent materials a flight draws.
        //
        // And Waterfall's effects: their shaders move the vertices and take their
        // opacity from fresnel and falloff terms (Waterfall's ShaderLab sources),
        // which a copy of texture and tint cannot follow -- it would mark the whole
        // mesh. FSR's own shading change detection is left to them.
        private static readonly string[] NotReactive =
        {
            "Invisible", "invisible", "CompositeRaymarchedClouds", "SkySphere", "AtmosphereFromGround", "Decal",
            "Waterfall/", "ReDefinition",
        };

        private static class Ids
        {
            internal static readonly int MainTex = Shader.PropertyToID("_MainTex");
            internal static readonly int TintColor = Shader.PropertyToID("_TintColor");
            internal static readonly int Color = Shader.PropertyToID("_Color");
            internal static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
            internal static readonly int Tint = Shader.PropertyToID("_ReDefinitionTint");
            internal static readonly int VertexColour = Shader.PropertyToID("_ReDefinitionVertexColour");
            internal static readonly int Additive = Shader.PropertyToID("_ReDefinitionAdditive");
            internal static readonly int Max = Shader.PropertyToID("_ReDefinitionMax");
            internal static readonly int SceneDepth = Shader.PropertyToID("_ReDefinitionSceneDepth");
            internal static readonly int MaskTexel = Shader.PropertyToID("_ReDefinitionMaskTexel");
            internal static readonly int Clouds = Shader.PropertyToID("_ReDefinitionClouds");
            internal static readonly int CloudFade = Shader.PropertyToID("_ReDefinitionCloudFade");
            internal static readonly int Ocean = Shader.PropertyToID("_ReDefinitionOcean");
            // EVE's name for its clouds' history.
            internal static readonly int CloudTexture = Shader.PropertyToID("scattererReconstructedCloud");
        }

        [Flags]
        private enum Kind : byte
        {
            None = 0,
            Reactive = 1,
            Additive = 2,
        }

        private struct MaterialKind
        {
            public Kind Kind;
            public int Queue;
            public int ShaderId;
            public float DstBlend;
        }

        private struct Candidate
        {
            public Renderer Renderer;
            public ParticleSystem Particles;
            public bool VertexColours;
            public int Submeshes;
        }

        private sealed class MaskCopy
        {
            public Material Material;
            public int PreparedFrame = -1;
        }

        private readonly List<Candidate> candidates = new List<Candidate>();
        private readonly List<Material> materials = new List<Material>();
        private readonly Dictionary<int, MaterialKind> kinds = new Dictionary<int, MaterialKind>();
        private readonly Dictionary<long, MaskCopy> copies = new Dictionary<long, MaskCopy>();
        private readonly HashSet<int> seenMaterials = new HashSet<int>();
        private readonly List<int> goneMaterials = new List<int>();
        private readonly List<long> goneCopies = new List<long>();
        private float nextScan;
        private Material fullscreen;
        private bool modsChecked;
        private bool clouds;
        private bool ocean;
        private Shader outdatedShader;
        private RenderTexture transparency;
        private RenderTexture reactive;
        private RenderTexture opaqueOnly;
        private RenderTexture postAlpha;
        private CommandBuffer maskBuffer;
        private Camera maskCamera;
        private CommandBuffer opaqueBuffer;
        private Camera opaqueCamera;

        internal RenderTexture TransparencyTexture { get { return transparency; } }
        internal RenderTexture ReactiveTexture { get { return reactive; } }
        internal RenderTexture OpaqueOnly { get { return opaqueBuffer != null ? opaqueOnly : null; } }
        internal RenderTexture PostAlpha { get { return maskBuffer != null ? postAlpha : null; } }
        internal int Candidates { get { return candidates.Count; } }
        internal int Drawn { get; private set; }
        internal string Problem { get; private set; }

        // Records what is asked for on the scene camera: the masks, and the two
        // copies FSR's own generator compares. sceneDepth receives the depth the
        // reactive mask is occluded by, from depthSource.
        internal void Record(Camera camera, bool transparencyWanted, bool renderersWanted, bool automaticWanted,
                             RenderTargetIdentifier depthSource, RenderTexture sceneDepth,
                             RenderTextureFormat colourFormat, Vector2Int size)
        {
            Drawn = 0;
            if (!transparencyWanted) UpscalerRig.Release(ref transparency);
            if (!renderersWanted) UpscalerRig.Release(ref reactive);
            if (!automaticWanted) UpscalerRig.Release(ref postAlpha);
            SyncOpaqueCopy(camera, automaticWanted, colourFormat, size);

            if (camera == null || (!transparencyWanted && !renderersWanted && !automaticWanted))
            {
                DetachMaskBuffer();
                return;
            }
            AttachMaskBuffer(camera);
            maskBuffer.Clear();

            bool created;
            if (automaticWanted && Ensure(ref postAlpha, size, colourFormat, "ReDefinition_PostAlpha", out created))
                maskBuffer.Blit(BuiltinRenderTextureType.CameraTarget, postAlpha);

            if ((!transparencyWanted && !renderersWanted) || !EnsureMaterial()) return;
            maskBuffer.SetGlobalVector(Ids.MaskTexel, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));

            if (transparencyWanted && Ensure(ref transparency, size, RenderTextureFormat.R8, "ReDefinition_TransparencyMask", out created))
                RecordTransparency();

            if (renderersWanted && Ensure(ref reactive, size, RenderTextureFormat.R8, "ReDefinition_ReactiveMask", out created))
                RecordReactive(camera, depthSource, sceneDepth);
        }

        internal void Dispose()
        {
            DetachMaskBuffer();
            DetachOpaqueCopy();
            UpscalerRig.Release(ref transparency);
            UpscalerRig.Release(ref reactive);
            UpscalerRig.Release(ref opaqueOnly);
            UpscalerRig.Release(ref postAlpha);
            foreach (MaskCopy copy in copies.Values)
                if (copy.Material != null) UnityEngine.Object.Destroy(copy.Material);
            copies.Clear();
            candidates.Clear();
            kinds.Clear();
            if (fullscreen != null) UnityEngine.Object.Destroy(fullscreen);
            fullscreen = null;
        }

        private void RecordTransparency()
        {
            CheckMods();
            // Not before EVE has bound its clouds' history once: unset, the global
            // would be read as whatever Unity binds for it. EVE and Scatterer share
            // it, so it is only read here.
            bool cloudsBound = clouds && Shader.GetGlobalTexture(Ids.CloudTexture) != null;

            fullscreen.SetFloat(Ids.Clouds, cloudsBound ? 1f : 0f);
            fullscreen.SetFloat(Ids.CloudFade, cloudsBound ? EveCloudMotion.Fade() : 0f);
            fullscreen.SetFloat(Ids.Ocean, ocean ? 1f : 0f);
            maskBuffer.Blit(Texture2D.blackTexture, transparency, fullscreen, 0);
        }

        private void RecordReactive(Camera camera, RenderTargetIdentifier depthSource, RenderTexture sceneDepth)
        {
            maskBuffer.Blit(depthSource, sceneDepth);
            maskBuffer.SetRenderTarget(reactive);
            maskBuffer.ClearRenderTarget(false, true, Color.clear);
            maskBuffer.SetGlobalTexture(Ids.SceneDepth, sceneDepth);

            if (Time.unscaledTime >= nextScan) Scan();
            int layers = camera.cullingMask;
            int frame = Time.frameCount;
            foreach (Candidate candidate in candidates)
            {
                Renderer renderer = candidate.Renderer;
                // Visible in the frame before, which is the one known now. Drawn with
                // the scene camera's matrices, so only what that camera renders.
                if (renderer == null || !renderer.enabled || !renderer.isVisible
                    || (layers & (1 << renderer.gameObject.layer)) == 0) continue;
                renderer.GetSharedMaterials(materials);
                // A particle system's second material is its trails', drawn as
                // submesh 1 while its trails are on -- asked now, not at the scan.
                int submeshes = candidate.Particles != null
                    ? (candidate.Particles.trails.enabled ? 2 : 1)
                    : candidate.Submeshes;
                int count = Math.Min(materials.Count, submeshes);
                for (int i = 0; i < count; i++)
                {
                    Kind kind = KindOf(materials[i], false);
                    if ((kind & Kind.Reactive) == 0) continue;
                    maskBuffer.DrawRenderer(renderer, CopyFor(materials[i], kind, candidate.VertexColours, frame), i, 1);
                    Drawn++;
                }
            }

            maskBuffer.Blit(Texture2D.blackTexture, reactive, fullscreen, 2);
        }

        // For FSR's own generator: the image before the transparent queue, as
        // FSR3Unity takes it -- a Blit of the camera target at BeforeForwardAlpha.
        private void SyncOpaqueCopy(Camera camera, bool wanted, RenderTextureFormat format, Vector2Int size)
        {
            if (!wanted || camera == null)
            {
                DetachOpaqueCopy();
                UpscalerRig.Release(ref opaqueOnly);
                return;
            }
            bool created;
            if (!Ensure(ref opaqueOnly, size, format, "ReDefinition_OpaqueOnly", out created))
            {
                DetachOpaqueCopy();
                return;
            }
            if (!created && opaqueBuffer != null && opaqueCamera == camera) return;
            DetachOpaqueCopy();
            opaqueBuffer = new CommandBuffer { name = "ReDefinition.OpaqueOnly" };
            opaqueBuffer.Blit(BuiltinRenderTextureType.CameraTarget, opaqueOnly);
            camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, opaqueBuffer);
            opaqueCamera = camera;
        }

        private bool EnsureMaterial()
        {
            if (fullscreen != null) return true;
            string error;
            Shader shader = FsrShaderBundle.LoadShader(FsrShaderBundle.MaskShaderName, out error);
            if (shader == null)
            {
                if (Problem == null)
                    CompatibilityLog.Warn("masks", "The masks for FSR cannot be drawn: " + error + ".");
                Problem = error;
                return false;
            }
            if (shader == outdatedShader) return false;
            fullscreen = new Material(shader) { name = "ReDefinition masks", hideFlags = HideFlags.DontSave };
            // A bundle built before the clamp pass has two passes, and a pass 1
            // that clamps by a value no longer set.
            if (fullscreen.passCount < 3)
            {
                UnityEngine.Object.Destroy(fullscreen);
                fullscreen = null;
                outdatedShader = shader;
                Problem = "the mask shader in the shader bundle is older than this build -- rebuild the bundle";
                CompatibilityLog.Warn("masks", "The masks for FSR cannot be drawn: " + Problem + ".");
                return false;
            }
            fullscreen.SetFloat(Ids.Max, ReactiveMaximum);
            Problem = null;
            return true;
        }

        private void CheckMods()
        {
            if (modsChecked) return;
            modsChecked = true;
            clouds = EveCloudMotion.Present;
            ocean = TypeLookup.Find("Scatterer.OceanCommandBuffer") != null;
        }

        // Once a second: the renderers with a transparent material. Visibility is
        // checked in every frame; what a material is, once while it is in use.
        private void Scan()
        {
            nextScan = Time.unscaledTime + ScanInterval;
            candidates.Clear();
            seenMaterials.Clear();
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null) continue;
                renderer.GetSharedMaterials(materials);
                bool any = false;
                foreach (Material material in materials)
                {
                    if (material == null) continue;
                    // Checked once per scan, not once for every renderer that shares it.
                    bool first = seenMaterials.Add(material.GetInstanceID());
                    if ((KindOf(material, first) & Kind.Reactive) != 0) any = true;
                }
                if (!any) continue;

                ParticleSystemRenderer particles = renderer as ParticleSystemRenderer;
                ParticleSystem system = particles != null ? particles.GetComponent<ParticleSystem>() : null;
                Mesh mesh = null;
                int submeshes = 1;
                if (particles == null)
                {
                    SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null) mesh = skinned.sharedMesh;
                    else if (renderer is MeshRenderer)
                    {
                        MeshFilter filter = renderer.GetComponent<MeshFilter>();
                        mesh = filter != null ? filter.sharedMesh : null;
                    }
                    if (mesh != null) submeshes = mesh.subMeshCount;
                }
                candidates.Add(new Candidate
                {
                    Renderer = renderer,
                    Particles = system,
                    // Particles, trails and lines carry their colour and fade in the
                    // vertices.
                    VertexColours = particles != null || renderer is TrailRenderer || renderer is LineRenderer
                                    || (mesh != null && mesh.HasVertexAttribute(VertexAttribute.Color)),
                    Submeshes = submeshes,
                });
            }

            // What no renderer uses any more leaves the caches.
            goneMaterials.Clear();
            foreach (int id in kinds.Keys)
                if (!seenMaterials.Contains(id)) goneMaterials.Add(id);
            foreach (int id in goneMaterials) kinds.Remove(id);

            goneCopies.Clear();
            foreach (KeyValuePair<long, MaskCopy> entry in copies)
                if (!seenMaterials.Contains((int)(entry.Key >> 1))) goneCopies.Add(entry.Key);
            foreach (long key in goneCopies)
            {
                if (copies[key].Material != null) UnityEngine.Object.Destroy(copies[key].Material);
                copies.Remove(key);
            }
        }

        // What a material is, remembered while it is in use. At each scan the
        // remembered answer is checked against the material's queue, shader and
        // blend, which effects change at run time, and worked out again where
        // they differ.
        private Kind KindOf(Material material, bool revalidate)
        {
            if (material == null) return Kind.None;
            int id = material.GetInstanceID();
            MaterialKind known;
            bool have = kinds.TryGetValue(id, out known);
            if (have && !revalidate) return known.Kind;

            Shader shader = material.shader;
            int queue = material.renderQueue;
            int shaderId = shader != null ? shader.GetInstanceID() : 0;
            float dstBlend = material.HasProperty(Ids.DstBlend) ? material.GetFloat(Ids.DstBlend) : -1f;
            if (have && known.Queue == queue && known.ShaderId == shaderId && known.DstBlend == dstBlend)
                return known.Kind;

            Kind kind = Kind.None;
            if (queue >= TransparentQueueFrom && shader != null)
            {
                string name = shader.name;
                if (!Excluded(name))
                {
                    kind = Kind.Reactive;
                    if (name.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                        || Mathf.RoundToInt(dstBlend) == (int)BlendMode.One)
                        kind |= Kind.Additive;
                }
            }
            kinds[id] = new MaterialKind { Kind = kind, Queue = queue, ShaderId = shaderId, DstBlend = dstBlend };
            return kind;
        }

        private static bool Excluded(string shader)
        {
            foreach (string part in NotReactive)
                if (shader.IndexOf(part, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // The mask shader with what it needs of the source material, as the source
        // is this frame -- plumes and re-entry animate their materials --, once per
        // frame however many renderers share it.
        private Material CopyFor(Material source, Kind kind, bool vertexColours, int frame)
        {
            long key = ((long)source.GetInstanceID() << 1) | (vertexColours ? 1L : 0L);
            MaskCopy copy;
            if (!copies.TryGetValue(key, out copy) || copy.Material == null)
            {
                copy = new MaskCopy
                {
                    Material = new Material(fullscreen.shader) { name = "ReDefinition reactive " + source.name, hideFlags = HideFlags.DontSave },
                };
                copies[key] = copy;
            }
            if (copy.PreparedFrame == frame) return copy.Material;
            copy.PreparedFrame = frame;

            Material target = copy.Material;
            Texture texture = source.HasProperty(Ids.MainTex) ? source.GetTexture(Ids.MainTex) : null;
            target.SetTexture(Ids.MainTex, texture != null ? texture : Texture2D.whiteTexture);
            target.SetTextureScale(Ids.MainTex, texture != null ? source.GetTextureScale(Ids.MainTex) : Vector2.one);
            target.SetTextureOffset(Ids.MainTex, texture != null ? source.GetTextureOffset(Ids.MainTex) : Vector2.zero);
            // Unity's particle shaders double their tint colour.
            Color tint = source.HasProperty(Ids.TintColor) ? source.GetColor(Ids.TintColor) * 2f
                : source.HasProperty(Ids.Color) ? source.GetColor(Ids.Color)
                : Color.white;
            target.SetColor(Ids.Tint, tint);
            target.SetFloat(Ids.Additive, (kind & Kind.Additive) != 0 ? 1f : 0f);
            target.SetFloat(Ids.VertexColour, vertexColours ? 1f : 0f);
            return target;
        }

        private void AttachMaskBuffer(Camera camera)
        {
            if (maskBuffer != null && maskCamera == camera) return;
            DetachMaskBuffer();
            maskBuffer = new CommandBuffer { name = "ReDefinition.Masks" };
            camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, maskBuffer);
            maskCamera = camera;
        }

        private void DetachMaskBuffer()
        {
            if (maskBuffer == null) return;
            if (maskCamera != null) maskCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, maskBuffer);
            maskBuffer.Release();
            maskBuffer = null;
            maskCamera = null;
        }

        private void DetachOpaqueCopy()
        {
            if (opaqueBuffer == null) return;
            if (opaqueCamera != null) opaqueCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, opaqueBuffer);
            opaqueBuffer.Release();
            opaqueBuffer = null;
            opaqueCamera = null;
        }

        internal static bool Ensure(ref RenderTexture texture, Vector2Int size, RenderTextureFormat format, string name, out bool created)
        {
            created = false;
            if (texture != null && texture.width == size.x && texture.height == size.y && texture.format == format
                && texture.IsCreated()) return true;
            UpscalerRig.Release(ref texture);
            texture = new RenderTexture(size.x, size.y, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = FilterMode.Point,
            };
            if (!texture.Create())
            {
                UpscalerRig.Release(ref texture);
                return false;
            }
            created = true;
            return true;
        }
    }
}
