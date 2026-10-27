using System;
using System.Collections;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Scenery
{
    // The space centre's lawns drawn as Parallax's terrain beside them. KSP's ground shader (KSP/Scenery/
    // Diffuse Ground KSC, as Deferred replaces it) gives its lawn a grass texture of its own, tinted by
    // _GrassColor. That is lighter and greyer than the terrain round the space centre, by day and from afar (the
    // player, 2026-10-25), and the raytracing had to lift and shade it to match. Its materials take this
    // project's ground shader instead (ReDefinitionKscGround.shader): the tarmac as before, the lawn from
    // Parallax's own terrain material of the home body. That takes its meadow texture, normals, influence and
    // occlusion maps, its tiling and shine. It is coloured by the vertex colours of the terrain's quads under
    // the space centre (the colour map below), as the terrain is. ShaderLookup says why Parallax's own
    // shader for this is not used.
    //
    // Only with Parallax, while OwnSettings.KscLawnTerrain is on; the materials go back as they were when it
    // goes off, or the scene ends. The space centre's lawns only: the island's airfield and the other
    // launch sites keep KSP's.
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class KscGround : MonoBehaviour
    {
        // Texels of the colour map, in metres, and how far it reaches past the space centre's ground.
        private const float ColourTexel = 8f;
        private const float ColourMargin = 64f;
        // How far past the map a terrain quad's middle may lie and its vertices still reach into it. The
        // terrain's finest quads round the space centre are some hundreds of metres across (the nearest middle
        // 509 m from the middle of the map, 2026-10-25).
        private const float QuadReach = 2000f;
        private const string StockShader = "KSP/Scenery/Diffuse Ground KSC";
        private const string StockSpecularShader = "KSP/Scenery/Diffuse Ground KSC Specular";
        // How often the space centre's renderers are looked through for new ground: KSP switches its parts
        // on by distance and swaps a facility's models when it is upgraded.
        private const float LookSeconds = 5f;

        // The globals the ground shader reads the colour map, its plane and its placement from; Parallax's textures
        // and values the lawn takes.
        private static readonly int ColoursId = Shader.PropertyToID("_ReDefinitionKscColours");
        private static readonly int WorldToRootId = Shader.PropertyToID("_ReDefinitionKscWorldToRoot");
        private static readonly int GridId = Shader.PropertyToID("_ReDefinitionKscColourGrid");
        private static readonly string[] ParallaxTextures = { "_MainTexMid", "_BumpMapMid", "_InfluenceMap", "_OcclusionMap" };
        private static readonly string[] ParallaxFloats = { "_Tiling", "_BumpScale", "_SpecularPower", "_SpecularIntensity" };

        // The ground shader; Parallax's terrain material the lawn is taken from; the space centre's root, whose plane
        // the colour map lies in; the map and its placement (origin x and z, reciprocal extents); whether the
        // replacements are in.
        private Shader shader;
        private Material parallax;
        private Transform root;
        // The sphere the space centre stands on, the home body's terrain: only its quads give the colour map. The ocean's
        // quads, a sphere of their own, lie under the same ground with white vertex colours and would be averaged in.
        private PQS sphere;
        private Texture2D colours;
        private Vector4 grid;
        private bool applied;
        // Per space centre material, the one drawn in its place; per renderer, its materials as they were.
        private readonly Dictionary<Material, Material> replacements = new Dictionary<Material, Material>();
        private readonly Dictionary<MeshRenderer, Material[]> originals = new Dictionary<MeshRenderer, Material[]>();
        private readonly List<Material> shared = new List<Material>();
        private readonly List<MeshRenderer> scanned = new List<MeshRenderer>();

        // The lawn's mean colour as drawn, for the rays' bounces off it (Raytracing.RtStockTerrain): the
        // terrain's mean vertex colour under the space centre through Parallax's colour formula with its
        // textures' means.
        internal static Color LawnColour { get; private set; } = new Color(0.3f, 0.32f, 0.17f, 1f);

        // Whether the material is one this draws the space centre's ground with.
        internal static bool Draws(Material material)
        {
            return material != null && material.shader != null && material.shader.name == ShaderLookup;
        }

        // The name of this project's ground shader in the bundle. Parallax's own shader for this
        // (Custom/ParallaxKSCTerrain) is unfinished and left out of its build. It reads the tarmac's mask from
        // the third texture coordinates, where the space centre's meshes carry it on the second. It lays the
        // tarmac on by position rather than its own coordinates.
        private const string ShaderLookup = "ReDefinition/Scenery/Diffuse Ground KSC";

        // Waits up to a minute for Parallax's textures and the terrain under the space centre, then every LookSeconds
        // applies or restores the materials by the setting; any failure restores them and stops.
        private IEnumerator Start()
        {
            if (!KscGrass.ParallaxInstalled) yield break;
            string problem;
            shader = ShaderBundle.LoadShader(ShaderLookup, out problem);
            if (shader == null)
            {
                Debug.LogWarning(Log.Tag + " Space centre lawns: as KSP draws them (" + (problem ?? "the shader is not in the bundle") + ").");
                yield break;
            }
            // Parallax loads the home body's textures some seconds into the scene, and the terrain's quads
            // under the space centre are built about then too.
            bool ready = false;
            string said = null;
            for (int attempt = 0; attempt < 120 && !ready; attempt++)
            {
                ready = Ready();
                if (ready) break;
                // What it waits for, every ten seconds at most.
                if (notReady != said && attempt % 20 == 0) Debug.Log(Log.Tag + " Space centre lawns: waiting (" + (said = notReady) + ").");
                yield return new WaitForSeconds(0.5f);
            }
            if (!ready)
            {
                Debug.Log(Log.Tag + " Space centre lawns: as KSP draws them (" + notReady + ").");
                yield break;
            }
            while (true)
            {
                try
                {
                    if (Enabled()) Apply();
                    else if (applied) Restore();
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " Space centre lawns: as KSP draws them (" + e.GetType().Name + ": " + e.Message + ").");
                    Restore();
                    yield break;
                }
                yield return new WaitForSeconds(LookSeconds);
            }
        }

        // The colour map's plane follows the space centre as the floating origin moves it.
        private void LateUpdate()
        {
            if (applied && root != null) Shader.SetGlobalMatrix(WorldToRootId, root.worldToLocalMatrix);
        }

        // The materials back as they were, the replacements and the colour map destroyed.
        private void OnDestroy()
        {
            Restore();
            foreach (Material material in replacements.Values)
                if (material != null) Destroy(material);
            replacements.Clear();
            if (colours != null) Destroy(colours);
        }

        // Whether the player keeps the lawns as the terrain (OwnSettings.KscLawnTerrain); on where no settings exist.
        private static bool Enabled()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            return addon == null || addon.Live.KscLawnTerrain;
        }

        // The space centre's city on the planet, the last one named KSC; null where there is none.
        internal static PQSCity City()
        {
            PQSCity ksc = null;
            foreach (PQSCity city in FindObjectsOfType<PQSCity>())
                if (city.name == "KSC") ksc = city;
            return ksc;
        }

        // What Ready waits for, for the log.
        private string notReady = "not looked yet";

        // Parallax's terrain material with its textures loaded, the space centre, and the colour map made.
        private bool Ready()
        {
            if (root == null)
            {
                PQSCity city = City();
                if (city != null)
                {
                    root = city.transform;
                    sphere = city.sphere;
                }
            }
            notReady = "the space centre not found";
            if (root == null) return false;
            if (parallax == null) parallax = ParallaxTerrain();
            notReady = "Parallax's terrain material not found with its textures";
            if (parallax == null) return false;
            if (colours == null && !MakeColours()) return false;
            Debug.Log(Log.Tag + " Space centre lawns: drawn as Parallax's terrain (" + parallax.name + ", " + parallax.GetTexture("_MainTexMid").name
                      + "; the terrain's colours under them " + colours.width + " x " + colours.height + " texels of "
                      + ColourTexel.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m, mean " + lawnVertexColour + "; " + Census() + ").");
            return true;
        }

        // Parallax's terrain material of the main body with every texture the lawn takes.
        private static Material ParallaxTerrain()
        {
            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null || material.shader == null || material.shader.name != "Custom/Parallax") continue;
                bool complete = true;
                foreach (string texture in ParallaxTextures)
                    complete &= material.HasProperty(texture) && material.GetTexture(texture) != null;
                foreach (string value in ParallaxFloats)
                    complete &= material.HasProperty(value);
                if (complete) return material;
            }
            return null;
        }

        // The terrain's mean vertex colour over the colour map, for the log and LawnColour.
        private Color lawnVertexColour;

        // The vertex colours of the terrain's quads under and round the space centre, in the plane of its root
        // (the colours Parallax's terrain is drawn with, the planet's colour map). Each texel gets the colour between
        // the corners of the quads' triangles over it, as the terrain is drawn (their vertices lie some 40 m
        // apart), the mean where several cover it. The empty ones are filled from their neighbours, then smoothed
        // once. It returns false while the quads there are not built.
        private bool MakeColours()
        {
            var materials = new List<Material>();
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            Matrix4x4 worldToRoot = root.worldToLocalMatrix;
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!IsGround(renderer, materials)) continue;
                // The mesh's own bounds, which hold for a renderer KSP has not switched on yet too.
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                Bounds b = filter.sharedMesh.bounds;
                Matrix4x4 meshToRoot = worldToRoot * renderer.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = meshToRoot.MultiplyPoint3x4(new Vector3((c & 1) == 0 ? b.min.x : b.max.x,
                        (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z));
                    x0 = Mathf.Min(x0, corner.x);
                    x1 = Mathf.Max(x1, corner.x);
                    z0 = Mathf.Min(z0, corner.z);
                    z1 = Mathf.Max(z1, corner.z);
                }
            }
            if (x0 > x1)
            {
                notReady = "no ground of KSP's under the space centre (" + Census() + ")";
                return false;
            }
            x0 -= ColourMargin;
            z0 -= ColourMargin;
            x1 += ColourMargin;
            z1 += ColourMargin;
            int width = Mathf.CeilToInt((x1 - x0) / ColourTexel), depth = Mathf.CeilToInt((z1 - z0) / ColourTexel);
            var sum = new Vector3[width * depth];
            var count = new int[width * depth];
            int quads = 0;
            float reach = Mathf.Max(x1 - x0, z1 - z0);
            int pqs = 0, usable = 0, otherSpheres = 0;
            float nearest = float.MaxValue;
            foreach (PQ quad in FindObjectsOfType<PQ>())
            {
                pqs++;
                if (quad == null || quad.mesh == null || !quad.isVisible || quad.isSubdivided) continue;
                if (sphere != null && quad.sphereRoot != sphere)
                {
                    otherSpheres++;
                    continue;
                }
                usable++;
                Vector3 at = worldToRoot.MultiplyPoint3x4(quad.transform.position);
                nearest = Mathf.Min(nearest, new Vector2(at.x - (x0 + x1) * 0.5f, at.z - (z0 + z1) * 0.5f).magnitude);
                if (Mathf.Abs(at.x - (x0 + x1) * 0.5f) > reach + QuadReach || Mathf.Abs(at.z - (z0 + z1) * 0.5f) > reach + QuadReach) continue;
                Vector3[] vertices = quad.mesh.vertices;
                Color32[] vertexColours = quad.mesh.colors32;
                int[] triangles = quad.mesh.triangles;
                if (vertexColours.Length != vertices.Length) continue;
                Matrix4x4 toRoot = worldToRoot * quad.transform.localToWorldMatrix;
                var flat = new Vector2[vertices.Length];
                var colour = new Vector3[vertices.Length];
                for (int v = 0; v < vertices.Length; v++)
                {
                    Vector3 p = toRoot.MultiplyPoint3x4(vertices[v]);
                    flat[v] = new Vector2((p.x - x0) / ColourTexel, (p.z - z0) / ColourTexel);
                    Color32 c = vertexColours[v];
                    colour[v] = new Vector3(c.r, c.g, c.b) / 255f;
                }
                bool any = false;
                // Each triangle's texels, their colour between its corners' as the terrain is drawn.
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int ia = triangles[t], ib = triangles[t + 1], ic = triangles[t + 2];
                    Vector2 a = flat[ia], ab = flat[ib] - a, ac = flat[ic] - a;
                    float det = ab.x * ac.y - ab.y * ac.x;
                    if (Mathf.Abs(det) < 1e-6f) continue;
                    int left = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(flat[ib].x, flat[ic].x))));
                    int right = Mathf.Min(width - 1, Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(flat[ib].x, flat[ic].x))));
                    int bottom = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(flat[ib].y, flat[ic].y))));
                    int top = Mathf.Min(depth - 1, Mathf.FloorToInt(Mathf.Max(a.y, Mathf.Max(flat[ib].y, flat[ic].y))));
                    for (int z = bottom; z <= top; z++)
                        for (int x = left; x <= right; x++)
                        {
                            Vector2 q = new Vector2(x + 0.5f, z + 0.5f) - a;
                            float u = (q.x * ac.y - q.y * ac.x) / det, w = (ab.x * q.y - ab.y * q.x) / det;
                            if (u < 0f || w < 0f || u + w > 1f) continue;
                            int i = z * width + x;
                            sum[i] += colour[ia] * (1f - u - w) + colour[ib] * u + colour[ic] * w;
                            count[i]++;
                            any = true;
                        }
                }
                if (any) quads++;
            }
            int filled = 0;
            for (int i = 0; i < count.Length; i++)
                if (count[i] > 0)
                {
                    sum[i] /= count[i];
                    filled++;
                }
            // Under half of the map covered: the terrain's quads under the space centre not built yet.
            notReady = "the terrain's quads under the space centre not built: " + filled + " of " + count.Length + " texels from " + quads + " quads; "
                       + pqs + " quads, " + usable + " drawn, the nearest " + nearest.ToString("0") + " m off; " + Census();
            if (filled < count.Length / 2) return false;
            Fill(sum, count, width, depth);
            Vector3[] smooth = Smooth(sum, width, depth);
            var pixels = new Color32[smooth.Length];
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < smooth.Length; i++)
            {
                pixels[i] = new Color32((byte)Mathf.Clamp(smooth[i].x * 255f + 0.5f, 0f, 255f), (byte)Mathf.Clamp(smooth[i].y * 255f + 0.5f, 0f, 255f),
                                        (byte)Mathf.Clamp(smooth[i].z * 255f + 0.5f, 0f, 255f), 255);
                mean += smooth[i];
            }
            mean /= smooth.Length;
            lawnVertexColour = new Color(mean.x, mean.y, mean.z, 1f);
            colours = new Texture2D(width, depth, TextureFormat.RGBA32, false, true)
            {
                name = "ReDefinition space centre terrain colours",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            colours.SetPixels32(pixels);
            colours.Apply(false, true);
            grid = new Vector4(x0, z0, 1f / (width * ColourTexel), 1f / (depth * ColourTexel));
            LawnColour = Raytracing.RtTerrainColour.Meadow(parallax, lawnVertexColour);
            Debug.Log(Log.Tag + " Space centre lawns: the terrain's colours from " + quads + " quads, " + filled + " of " + count.Length + " texels hit; " + otherSpheres + " quads of other spheres left out.");
            return true;
        }

        // Every empty texel the mean of its filled neighbours, pass by pass, until none is empty.
        private static void Fill(Vector3[] values, int[] count, int width, int depth)
        {
            var known = new bool[values.Length];
            for (int i = 0; i < known.Length; i++) known[i] = count[i] > 0;
            for (int pass = 0; pass < width + depth; pass++)
            {
                var next = (bool[])known.Clone();
                bool missing = false;
                for (int z = 0; z < depth; z++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = z * width + x;
                        if (known[i]) continue;
                        Vector3 s = Vector3.zero;
                        int n = 0;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, nz = z + dz;
                                if (nx < 0 || nz < 0 || nx >= width || nz >= depth || !known[nz * width + nx]) continue;
                                s += values[nz * width + nx];
                                n++;
                            }
                        if (n > 0)
                        {
                            values[i] = s / n;
                            next[i] = true;
                        }
                        else missing = true;
                    }
                known = next;
                if (!missing) return;
            }
        }

        // A 3 by 3 mean.
        private static Vector3[] Smooth(Vector3[] values, int width, int depth)
        {
            var result = new Vector3[values.Length];
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                {
                    Vector3 s = Vector3.zero;
                    int n = 0;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
                            s += values[nz * width + nx];
                            n++;
                        }
                    result[z * width + x] = s / n;
                }
            return result;
        }

        // The space centre's renderers' ground shaders and how many materials each, for the log.
        private string Census()
        {
            var shaders = new Dictionary<string, int>();
            var materials = new List<Material>();
            int renderers = 0;
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderers++;
                renderer.GetSharedMaterials(materials);
                foreach (Material material in materials)
                {
                    if (material == null || material.shader == null || material.shader.name.IndexOf("Ground", StringComparison.Ordinal) < 0) continue;
                    int n;
                    shaders.TryGetValue(material.shader.name, out n);
                    shaders[material.shader.name] = n + 1;
                }
            }
            var text = new System.Text.StringBuilder(renderers + " renderers");
            foreach (KeyValuePair<string, int> entry in shaders) text.Append("; ").Append(entry.Key).Append(' ').Append(entry.Value);
            return text.ToString();
        }

        // Whether one of the renderer's materials is KSP's ground of the space centre.
        private static bool IsGround(MeshRenderer renderer, List<Material> materials)
        {
            if (renderer == null) return false;
            renderer.GetSharedMaterials(materials);
            foreach (Material material in materials)
                if (Stock(material)) return true;
            return false;
        }

        // KSP's ground shader and its specular variant, whose tarmac has a normal map.
        private static bool Stock(Material material)
        {
            if (material == null) return false;
            Shader shader = material.shader;
            if (shader == null) return false;
            bool stock;
            if (!stockShaders.TryGetValue(shader, out stock))
                stockShaders[shader] = stock = shader.name == StockShader || shader.name == StockSpecularShader;
            return stock;
        }

        // Whether each shader seen is one of those two, so a pass over the renderers reads no shader name twice.
        private static readonly Dictionary<Shader, bool> stockShaders = new Dictionary<Shader, bool>();

        // Every ground material of the space centre's renderers in its replacement's place; the replacements'
        // values from Parallax's material again, which its settings window may change.
        private void Apply()
        {
            Shader.SetGlobalTexture(ColoursId, colours);
            Shader.SetGlobalVector(GridId, grid);
            Shader.SetGlobalMatrix(WorldToRootId, root.worldToLocalMatrix);
            foreach (Material replacement in replacements.Values) CopyParallax(replacement);
            int swapped = 0;
            root.GetComponentsInChildren(true, scanned);
            foreach (MeshRenderer renderer in scanned)
            {
                if (renderer == null || originals.ContainsKey(renderer)) continue;
                renderer.GetSharedMaterials(shared);
                bool any = false;
                for (int m = 0; m < shared.Count && !any; m++) any = Stock(shared[m]);
                if (!any) continue;
                var materials = shared.ToArray();
                for (int m = 0; m < materials.Length; m++)
                    if (Stock(materials[m])) materials[m] = Replacement(materials[m]);
                originals[renderer] = shared.ToArray();
                renderer.sharedMaterials = materials;
                swapped++;
            }
            if (swapped > 0)
                Debug.Log(Log.Tag + " Space centre lawns: " + swapped + " renderer(s) drawn as Parallax's terrain, " + originals.Count + " in all, "
                          + replacements.Count + " material(s).");
            applied = true;
        }

        // The replacement for a stock ground material, made once: its tarmac, mask and colours, the specular
        // variant's tarmac normal map, and Parallax's lawn.
        private Material Replacement(Material stock)
        {
            Material replacement;
            if (replacements.TryGetValue(stock, out replacement) && replacement != null) return replacement;
            replacement = new Material(shader) { name = stock.name + " (ReDefinition, as the terrain)", renderQueue = stock.renderQueue };
            foreach (string texture in new[] { "_TarmacTexture", "_BlendMaskTexture" })
                if (stock.HasProperty(texture))
                {
                    replacement.SetTexture(texture, stock.GetTexture(texture));
                    replacement.SetTextureScale(texture, stock.GetTextureScale(texture));
                    replacement.SetTextureOffset(texture, stock.GetTextureOffset(texture));
                }
            foreach (string colour in new[] { "_TarmacColor", "_GrassColor" })
                if (stock.HasProperty(colour)) replacement.SetColor(colour, stock.GetColor(colour));
            // The specular variant's tarmac as Deferred draws it: its normal map on the tarmac's coordinates,
            // a smoothness of 0.55 rather than 0.25 times the root of its alpha.
            bool specular = stock.shader.name == StockSpecularShader;
            if (specular && stock.HasProperty("_TarmacNormal")) replacement.SetTexture("_TarmacNormal", stock.GetTexture("_TarmacNormal"));
            replacement.SetFloat("_TarmacNormalOn", specular && stock.HasProperty("_TarmacNormal") && stock.GetTexture("_TarmacNormal") != null ? 1f : 0f);
            replacement.SetFloat("_TarmacSmoothness", specular ? 0.55f : 0.25f);
            CopyParallax(replacement);
            replacements[stock] = replacement;
            return replacement;
        }

        // Parallax's textures and values of the lawn into a replacement, from its material as it is now.
        private void CopyParallax(Material replacement)
        {
            if (parallax == null || replacement == null) return;
            foreach (string texture in ParallaxTextures) replacement.SetTexture(texture, parallax.GetTexture(texture));
            foreach (string value in ParallaxFloats) replacement.SetFloat(value, parallax.GetFloat(value));
        }

        // The renderers' materials as they were.
        private void Restore()
        {
            foreach (KeyValuePair<MeshRenderer, Material[]> entry in originals)
                if (entry.Key != null) entry.Key.sharedMaterials = entry.Value;
            if (originals.Count > 0) Debug.Log(Log.Tag + " Space centre lawns: as KSP draws them again (" + originals.Count + " renderer(s)).");
            originals.Clear();
            applied = false;
        }
    }
}
