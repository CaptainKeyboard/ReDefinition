using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The drawing of the tracks (stage 2 of the plan): the strips of TrackRibbons for the tiles near the active vessel, stood on the
    // ground and drawn as one mesh with one draw (Graphics.DrawMesh, shader ReDefinition/AtmospheresShellTrack, which multiplies the lit
    // frame). The height of a strip is where a ray down from above it meets the ground collider (GroundRay), the surface the wheels
    // roll on, plus a lift of 2 cm; where no ray meets it the PQS height stands in. Heights are cached per strip. The mesh is built
    // again at most twice a second, when a tile changed or its fade moved a level, or the vessel went 40 m from where the mesh was
    // built. Vertices are kept relative to an anchor; the anchor's world position is read every frame, so the floating origin does not
    // move the strips. Tuning keys: tracks.rangeM 300, tracks.liftM 0.02, tracks.rebuildSeconds 0.5.
    internal sealed class TrackDraw
    {
        internal const string ShaderName = "ReDefinition/AtmospheresShellTrack";

        private sealed class TileCache
        {
            internal int Version = -1;
            internal int Bucket = -1;
            internal readonly List<TrackRibbons.Strip> Strips = new List<TrackRibbons.Strip>();
        }

        private readonly Dictionary<TrackTile, TileCache> caches = new Dictionary<TrackTile, TileCache>();
        private readonly Dictionary<long, float> heights = new Dictionary<long, float>();
        private readonly List<TrackRibbons.Strip> strips = new List<TrackRibbons.Strip>();
        private readonly List<TrackTile> nearTiles = new List<TrackTile>();
        private Mesh mesh;
        private Material material;
        private bool materialTried;
        private CelestialBody anchorBody;
        private double anchorLat, anchorLon, anchorAlt;
        private Vector3d anchorWorldAtBuild;
        private bool built;
        private float nextBuild;
        private long builtSignature = long.MinValue;
        private double builtLat, builtLon;
        // The rays one build may cast (the rest of the strips follow in the next builds), the rays cast by this build, and whether the
        // build left strips at the PQS height for want of rays.
        private const int RaysPerBuild = 300;
        private int raysThisBuild;
        private bool incomplete;

        // For the probe: the strips in the mesh, the vertices, the milliseconds of the last build, the rays cast in all, the draw calls.
        internal int StripCount { get; private set; }
        internal double LastBuildMs { get; private set; }
        internal int RaysCast { get; private set; }
        internal int Draws { get; private set; }
        internal int RayMisses { get; private set; }

        // The mesh stays but is not drawn.
        internal void Hide()
        {
            StripCount = 0;
            built = false;
        }

        internal void Dispose()
        {
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            mesh = null;
            if (material != null) UnityEngine.Object.Destroy(material);
            material = null;
            caches.Clear();
            heights.Clear();
        }

        private Material Mat()
        {
            if (material != null || materialTried) return material;
            materialTried = true;
            string error;
            Shader shader = AtmosphereShaders.Get(ShaderName, out error);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("tracks: the ribbon shader is not available (" + error + "); tracks are kept but not drawn.");
                return null;
            }
            material = new Material(shader) { name = "ReDefinition Atmospheres track ribbons", hideFlags = HideFlags.HideAndDontSave };
            return material;
        }

        internal void Tick(double ut, Vessel active)
        {
            if (!HighLogic.LoadedSceneIsFlight || active == null || active.mainBody == null) return;
            CoverBodyState state = CoverBodies.Of(active.mainBody);
            if (state == null || !state.HasTracks) return;
            Material m = Mat();
            if (m == null) return;
            float now = Time.realtimeSinceStartup;
            double lat = active.latitude, lon = active.longitude;
            if (now >= nextBuild && Due(state, ut, lat, lon)) Build(state, ut, lat, lon, now);
            if (!built || mesh == null || anchorBody == null) return;
            // The anchor's place in the world now; the mesh was built around its place then.
            Vector3d anchorNow = anchorBody.GetWorldSurfacePosition(anchorLat, anchorLon, anchorAlt);
            Matrix4x4 matrix = Matrix4x4.TRS((Vector3)(anchorNow - anchorWorldAtBuild), Quaternion.identity, Vector3.one);
            Graphics.DrawMesh(mesh, matrix, m, 0, null, 0, null, false, true, false);
            Draws++;
        }

        // Whether the mesh has to be built: the tiles or their fade moved, or the vessel went away from where it was built.
        private bool Due(CoverBodyState state, double ut, double lat, double lon)
        {
            double dLat = (lat - builtLat) * Math.PI / 180.0 * state.Body.Radius;
            double dLon = (lon - builtLon) * Math.PI / 180.0 * state.Body.Radius * Math.Cos(lat * Math.PI / 180.0);
            if (!built || dLat * dLat + dLon * dLon > 40.0 * 40.0) return true;
            return Signature(state, ut) != builtSignature;
        }

        // A number that changes when a tile near the vessel changed or its fade reached another bucket.
        private long Signature(CoverBodyState state, double ut)
        {
            long sig = 17;
            foreach (TrackTile t in state.Tracks.Live)
            {
                if (ut < t.StampUt) continue;
                unchecked
                {
                    sig = sig * 31 + t.Version + t.Tx * 7919L + t.Ty * 104729L + Bucket(t, ut);
                }
            }
            return sig;
        }

        private static int Bucket(TrackTile t, double ut)
        {
            return (int)Math.Floor((ut - t.StampUt) / Math.Max(1.0, t.TauSeconds * 0.15));
        }

        private void Build(CoverBodyState state, double ut, double lat, double lon, float now)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            nextBuild = now + Tuning.Get("tracks.rebuildSeconds", 0.5f);
            double range = Tuning.Get("tracks.rangeM", 300f);
            TrackTiles tiles = state.Tracks;
            // The strips of the tiles in range, from the tile caches, nearest tiles first.
            int ctx, cty;
            double ce, cn;
            TrackTiles.Locate(state.Body.Radius, lat, lon, out ctx, out cty, out ce, out cn);
            nearTiles.Clear();
            double radiusTiles = Math.Ceiling(range / TrackTiles.TileMetres) + 1;
            foreach (TrackTile t in tiles.Live)
            {
                if (ut < t.StampUt || Math.Abs(t.Ty - cty) > radiusTiles) continue;
                // Tiles of other rows have other column widths: their distance is read from their corner's place.
                double tLat, tLon;
                TrackTiles.PlaceOf(state.Body.Radius, t.Tx, t.Ty, TrackTiles.TileMetres * 0.5, TrackTiles.TileMetres * 0.5, out tLat, out tLon);
                double dy = (tLat - lat) * Math.PI / 180.0 * state.Body.Radius;
                double dx = (tLon - lon) * Math.PI / 180.0 * state.Body.Radius * Math.Cos(lat * Math.PI / 180.0);
                if (dx * dx + dy * dy > (range + TrackTiles.TileMetres) * (range + TrackTiles.TileMetres)) continue;
                nearTiles.Add(t);
            }
            nearTiles.Sort((a, b) => Distance2(a, ctx, cty).CompareTo(Distance2(b, ctx, cty)));
            strips.Clear();
            foreach (TrackTile t in nearTiles)
            {
                TileCache cache;
                if (!caches.TryGetValue(t, out cache)) caches[t] = cache = new TileCache();
                int bucket = Bucket(t, ut);
                if (cache.Version != t.Version || cache.Bucket != bucket)
                {
                    cache.Strips.Clear();
                    TrackRibbons.AddTile(t, ut, cache.Strips, TrackRibbons.PoolSize);
                    cache.Version = t.Version;
                    cache.Bucket = bucket;
                }
                foreach (TrackRibbons.Strip s in cache.Strips)
                {
                    if (strips.Count >= TrackRibbons.PoolSize) break;
                    strips.Add(s);
                }
            }
            raysThisBuild = 0;
            incomplete = false;
            BuildMesh(state, ut, lat, lon);
            // A build that left strips without their ray asks for the next one at once.
            builtSignature = incomplete ? long.MinValue : Signature(state, ut);
            builtLat = lat;
            builtLon = lon;
            built = true;
            LastBuildMs = watch.Elapsed.TotalMilliseconds;
            // Caches of tiles that are gone let go.
            if (caches.Count > 3 * TrackTiles.MaxLive)
            {
                caches.Clear();
                heights.Clear();
            }
        }

        private static double Distance2(TrackTile t, int cx, int cy)
        {
            double dx = t.Tx - cx, dy = t.Ty - cy;
            return dx * dx + dy * dy;
        }

        private void BuildMesh(CoverBodyState state, double ut, double lat, double lon)
        {
            CelestialBody body = state.Body;
            float lift = Tuning.Get("tracks.liftM", 0.02f);
            anchorBody = body;
            anchorLat = lat;
            anchorLon = lon;
            anchorAlt = body.TerrainAltitude(lat, lon, true);
            anchorWorldAtBuild = body.GetWorldSurfacePosition(anchorLat, anchorLon, anchorAlt);
            int n = strips.Count;
            var vertices = new Vector3[n * 4];
            var colors = new Color32[n * 4];
            var triangles = new int[n * 6];
            Color tint = new Color(state.Def.Red * 0.5f, state.Def.Green * 0.5f, state.Def.Blue * 0.55f, 1f);
            for (int k = 0; k < n; k++)
            {
                TrackRibbons.Strip s = strips[k];
                // The four corners of the strip: west and east edges at the south and north sides.
                Vector3 a = Corner(body, s, 0f, 0f, lift), b = Corner(body, s, s.Length, 0f, lift);
                Vector3 c = Corner(body, s, s.Length, s.Height, lift), d = Corner(body, s, 0f, s.Height, lift);
                vertices[k * 4] = a; vertices[k * 4 + 1] = b; vertices[k * 4 + 2] = c; vertices[k * 4 + 3] = d;
                Color32 color = new Color32((byte)(tint.r * 255f), (byte)(tint.g * 255f), (byte)(tint.b * 255f), (byte)(Mathf.Clamp01(s.Alpha * 0.7f) * 255f));
                colors[k * 4] = color; colors[k * 4 + 1] = color; colors[k * 4 + 2] = color; colors[k * 4 + 3] = color;
                triangles[k * 6] = k * 4; triangles[k * 6 + 1] = k * 4 + 3; triangles[k * 6 + 2] = k * 4 + 2;
                triangles[k * 6 + 3] = k * 4; triangles[k * 6 + 4] = k * 4 + 2; triangles[k * 6 + 5] = k * 4 + 1;
            }
            if (mesh == null) mesh = new Mesh { name = "ReDefinition track ribbons", hideFlags = HideFlags.HideAndDontSave };
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.colors32 = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            // The mesh is stood around the anchor; the bounds cover the range plus the lift, whatever the strips are.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(2000f, 2000f, 2000f));
            StripCount = n;
        }

        // The corner of a strip at east and north metres inside its tile: the vertex relative to the anchor, on the ground plus the lift.
        // The height is cached per strip origin (a corner shares its strip's ray).
        private Vector3 Corner(CelestialBody body, TrackRibbons.Strip s, float eastM, float northM, float lift)
        {
            double lat, lon;
            TrackTiles.PlaceOf(body.Radius, s.Tx, s.Ty, s.East + eastM, s.North + northM, out lat, out lon);
            long key = (((long)s.Ty * 1000003L + s.Tx) * 65536L + (int)(s.North * 2f)) * 512L + (int)(s.East * 2f);
            float alt;
            if (!heights.TryGetValue(key, out alt))
            {
                bool measured;
                alt = GroundAltitude(body, lat, lon, out measured);
                // A strip beyond the ray budget of this build stands at the PQS height until a later build casts its ray.
                if (measured) heights[key] = alt;
                else if (raysThisBuild >= RaysPerBuild) incomplete = true;
            }
            Vector3d world = body.GetWorldSurfacePosition(lat, lon, alt + lift);
            return (Vector3)(world - anchorWorldAtBuild);
        }

        // The altitude of the ground collider at a place: a ray down from 30 m above the PQS height; the PQS height where it hits nothing.
        private float GroundAltitude(CelestialBody body, double lat, double lon, out bool measured)
        {
            measured = false;
            double terrain = body.TerrainAltitude(lat, lon, true);
            if (raysThisBuild >= RaysPerBuild) return (float)terrain;
            Vector3 from = (Vector3)body.GetWorldSurfacePosition(lat, lon, terrain + 30.0);
            Vector3 up = (Vector3)body.GetSurfaceNVector(lat, lon);
            Vector3 point;
            RaysCast++;
            raysThisBuild++;
            if (GroundRay.Hit(from, -up, 80f, out point))
            {
                measured = true;
                return (float)body.GetAltitude((Vector3d)point);
            }
            RayMisses++;
            return (float)terrain;
        }
    }
}
