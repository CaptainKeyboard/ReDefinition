using System;
using System.Reflection;
using System.Text;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // A PQS mod on a body that carries cover: when a quad is built, it turns the vertex colours toward the cover layer's colour by the
    // depth the field's surface layer gives at the vertex (CoverDepth), sub-grid roughened by SnowNoise. It runs after the colour mods.
    // The installed Parallax orders its own mod at 999999, before the last colour mods, and reads the colour from the finished mesh, as
    // the stock terrain shaders do, so the cover reaches both. The vertex alpha is left as it is. One instance per body, added by
    // SnowRefresher by body object (Kopernicus-safe: no body name). [VERIFY: the order against BurstPQS; the log lists the mods of the
    // sphere in their order once]
    internal sealed class CoverPqsMod : PQSMod
    {
        // After every colour mod.
        internal const int SortOrder = int.MaxValue - 10;

        // The vertex count of a quad whose vertex array is not there yet when its first vertex is built (a quad of 15 x 15 vertices
        // in the build checked).
        internal const int DefaultQuadVertexCount = 225;

        // The body's cover state, set by SnowRefresher before the sphere sets up.
        internal CoverBodyState State;

        // The quad being built, its record and the look: the frame and the clock are read once per quad.
        private PQ currentQuad;
        private QuadCover currentCover;
        private CoverMap.Frame frame;
        private CoverLook look;
        private int settingsStamp;
        private bool failedLogged;
        private bool slopeWarned;
        private bool paintedQuad;
        private static bool orderLogged;

        public override void OnSetup()
        {
            requirements = PQS.ModiferRequirements.MeshColorChannel;
            order = SortOrder;
            LogOrder();
        }

        public override void OnVertexBuild(PQS.VertexBuildData data)
        {
            try
            {
                Build(data);
            }
            catch (Exception e)
            {
                if (failedLogged) return;
                failedLogged = true;
                AtmospheresLog.WarnOnce("the cover of a ground vertex failed; the vertex keeps its colour (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        private void Build(PQS.VertexBuildData data)
        {
            PQ quad = data.buildQuad;
            if (quad == null || State == null || !CoverModule.Enabled) return;
            if (data.vertIndex == 0 || !ReferenceEquals(quad, currentQuad))
            {
                currentQuad = quad;
                currentCover = null;
                paintedQuad = false;
                BeginQuad(data);
            }
            double x = data.directionFromCenter.x, y = data.directionFromCenter.y, z = data.directionFromCenter.z;
            double latitude, longitude;
            State.Frame.LatLon(x, y, z, out latitude, out longitude);
            double height = data.vertHeight - sphere.radius;
            double noise = SnowNoise.Value(x, y, z);
            if (currentCover == null || data.vertIndex >= currentCover.Length)
                currentCover = CoverQuads.For(quad, State, quad.verts != null && quad.verts.Length > 0 ? quad.verts.Length : DefaultQuadVertexCount);
            currentCover.Set(data.vertIndex, (Color32)data.vertColor, latitude, longitude, height, noise);
            // Without a published map nothing is painted and the quad stays due for its first paint.
            if (frame == null || frame.Number == 0) return;
            float amount = currentCover.AmountAt(data.vertIndex, frame, look, State.Body.ocean && height <= 0.0);
            data.vertColor = CoverPaint.Blend(data.vertColor, State.Def, amount);
            paintedQuad = true;
        }

        // The quad is complete: it was painted for the frame, the settings and the time it was begun with.
        public override void OnQuadBuilt(PQ quad)
        {
            try
            {
                QuadCover cover;
                if (!paintedQuad || !ReferenceEquals(quad, currentQuad) || frame == null || !CoverQuads.TryGet(quad, out cover)) return;
                cover.MarkPainted(frame, settingsStamp, look.Ut);
                paintedQuad = false;
            }
            catch (Exception)
            {
                // The quad is painted again at the next pass.
            }
        }

        // The frame, the look and the wind of a quad are read once at its first vertex.
        private void BeginQuad(PQS.VertexBuildData data)
        {
            double ut;
            CoverClock.TryUt(out ut);
            frame = State.Map.Current;
            settingsStamp = CoverLooks.SettingsStamp(State);
            look = CoverLooks.Make(State, ut);
            if (look.Drift > 0)
            {
                double lat, lon;
                State.Frame.LatLon(data.directionFromCenter.x, data.directionFromCenter.y, data.directionFromCenter.z, out lat, out lon);
                CoverLooks.WindAt(State, lat, lon, data.vertHeight - sphere.radius, ut, out look.WindEast, out look.WindNorth);
            }
        }

        // The mesh normals are final: the slope of every vertex is read, and the quad is due for a paint with it.
        public override void OnQuadUpdateNormals(PQ quad)
        {
            try
            {
                QuadCover cover;
                if (State == null || !AtmospheresAssumptions.Holds(AssumptionsD34.QuadId) || !CoverQuads.TryGet(quad, out cover)) return;
                FillSlope(quad, cover);
            }
            catch (Exception e)
            {
                if (failedLogged) return;
                failedLogged = true;
                AtmospheresLog.WarnOnce("the slope of a ground quad could not be read; the cover ignores slope here (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        // The slope from the quad's own normals: the normal and the vertex position go through the quad's transform to the world, the
        // up is the direction from the sphere's centre to the vertex. The check: a quad whose mean slope is over 75 degrees is taken for
        // a frame mismatch and keeps slope 0 (logged once). [VERIFY in game: quadTransform is the mesh's transform]
        private void FillSlope(PQ quad, QuadCover cover)
        {
            if (quad.quadTransform == null || quad.vertNormals == null || quad.verts == null || sphere == null || sphere.transform == null) return;
            int n = Math.Min(cover.Length, Math.Min(quad.vertNormals.Length, quad.verts.Length));
            if (n < 4) return;
            Transform t = quad.quadTransform;
            Vector3 centre = sphere.transform.position;
            bool drift = CoverLooks.Drift(State) > 0;
            var slopes = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 normal = t.TransformDirection(quad.vertNormals[i]).normalized;
                Vector3 up = (t.TransformPoint(quad.verts[i]) - centre).normalized;
                slopes[i] = CoverDepth.SlopeDegrees(Vector3.Dot(normal, up));
                sum += slopes[i];
            }
            if (sum / n > 75.0)
            {
                if (!slopeWarned)
                {
                    slopeWarned = true;
                    AtmospheresLog.WarnOnce("cover: the normals of a quad of " + State.Body.name + " do not read as ground normals (mean slope "
                                        + (sum / n).ToString("0") + " deg); slope is ignored there.");
                }
                return;
            }
            for (int i = 0; i < n; i++)
            {
                double east = double.NaN, north = double.NaN;
                if (drift)
                {
                    Vector3 normal = t.TransformDirection(quad.vertNormals[i]).normalized;
                    Vector3 up = (t.TransformPoint(quad.verts[i]) - centre).normalized;
                    double ex, ey, ez, nx, ny, nz;
                    // The east and north of the vertex in the sphere's frame, turned to the world by the sphere's transform.
                    Vector3 upLocal = sphere.transform.InverseTransformDirection(up);
                    CoverWind.LocalFrame(upLocal.x, upLocal.y, upLocal.z, out ex, out ey, out ez, out nx, out ny, out nz);
                    Vector3 e = sphere.transform.TransformDirection(new Vector3((float)ex, (float)ey, (float)ez));
                    Vector3 nn = sphere.transform.TransformDirection(new Vector3((float)nx, (float)ny, (float)nz));
                    east = Vector3.Dot(normal, e);
                    north = Vector3.Dot(normal, nn);
                }
                cover.SetSlope(i, slopes[i], east, north);
            }
            cover.HasSlope = true;
            cover.PaintedVersion = -1;
        }

        public override void OnQuadDestroy(PQ quad)
        {
            CoverQuads.Remove(quad);
            if (ReferenceEquals(quad, currentQuad))
            {
                currentQuad = null;
                currentCover = null;
            }
        }

        public override void OnSphereReset()
        {
            if (State != null) CoverQuads.RemoveBody(State);
            currentQuad = null;
            currentCover = null;
        }

        // Writes the mods of the sphere in their order once, so that the log shows ours between the colour mods and Parallax.
        private void LogOrder()
        {
            if (orderLogged || sphere == null) return;
            orderLogged = true;
            try
            {
                FieldInfo field = typeof(PQS).GetField("mods", BindingFlags.Instance | BindingFlags.NonPublic);
                PQSMod[] mods = field != null ? field.GetValue(sphere) as PQSMod[] : null;
                if (mods == null) mods = sphere.GetComponentsInChildren<PQSMod>(true);
                var text = new StringBuilder();
                for (int i = 0; i < mods.Length; i++)
                {
                    if (mods[i] == null) continue;
                    if (text.Length > 0) text.Append(", ");
                    text.Append(mods[i].GetType().Name).Append('(').Append(mods[i].order).Append(')');
                }
                AtmospheresLog.Say("PQS mods of " + sphere.name + " in build order (" + mods.Length + "): " + text);
            }
            catch (Exception e)
            {
                AtmospheresLog.Say("the PQS mod order could not be listed (" + CompatibilityLog.Reason(e) + ")");
            }
        }
    }
}
