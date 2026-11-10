using System;
using System.Reflection;
using System.Text;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // A PQS mod on Kerbin: when a quad is built, it turns the vertex colours toward snow by the mask. It runs after the colour mods.
    // The installed Parallax orders its own mod at 999999, before the last colour mods, and reads the colour from the finished mesh,
    // as the stock terrain shaders do, so the snow reaches both. The vertex alpha is left as it is.
    internal sealed class SnowPqsMod : PQSMod
    {
        // After every colour mod.
        internal const int SortOrder = int.MaxValue - 10;

        // The vertex count of a quad whose vertex array is not there yet when its first vertex is built (a quad of 15 x 15 vertices
        // in the build checked).
        internal const int DefaultQuadVertexCount = 225;

        // The quad being built, its record, and the phase it is built for: the clock is read once per quad.
        private PQ currentQuad;
        private QuadSnow currentSnow;
        private double? phase;
        private bool failedLogged;
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
                SeasonsLog.WarnOnce("the snow of a ground vertex failed; the vertex keeps its colour (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        private void Build(PQS.VertexBuildData data)
        {
            PQ quad = data.buildQuad;
            if (quad == null || !SeasonsSettings.Enabled) return;
            bool track = SeasonsSettings.GroundRefreshMode != SeasonsSettings.GroundRefresh.Off;
            if (data.vertIndex == 0 || !ReferenceEquals(quad, currentQuad))
            {
                currentQuad = quad;
                currentSnow = null;
                phase = SeasonState.Phase();
            }
            double x = data.directionFromCenter.x, y = data.directionFromCenter.y, z = data.directionFromCenter.z;
            double latitude = Geo.LatitudeOf(y);
            double height = data.vertHeight - sphere.radius;
            double noise = SnowNoise.Value(x, y, z);
            double? seasonPhase = phase;
            if (track)
            {
                // The record of the quad is looked up once per quad; a vertex past its end (the vertex array was not there for the first
                // vertex) asks for a record of the real size.
                if (currentSnow == null || data.vertIndex >= currentSnow.Length)
                    currentSnow = SnowQuads.For(quad, quad.verts != null && quad.verts.Length > 0 ? quad.verts.Length : DefaultQuadVertexCount);
                currentSnow.Set(data.vertIndex, (Color32)data.vertColor, latitude, height, noise);
                // Without a phase nothing is painted and the quad stays due for its first paint (NaN).
                currentSnow.PaintPhase = seasonPhase.HasValue ? seasonPhase.Value : double.NaN;
            }
            if (!seasonPhase.HasValue) return;
            float amount = QuadSnow.Amount(latitude, height, seasonPhase.Value, noise, SeasonsSettings.SnowStrength, SeasonsSettings.WinterSnowLine);
            data.vertColor = SnowPaint.Blend(data.vertColor, amount);
        }

        public override void OnQuadDestroy(PQ quad)
        {
            SnowQuads.Remove(quad);
            if (ReferenceEquals(quad, currentQuad))
            {
                currentQuad = null;
                currentSnow = null;
            }
        }

        public override void OnSphereReset()
        {
            SnowQuads.Clear();
            currentQuad = null;
            currentSnow = null;
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
                SeasonsLog.Say("PQS mods of " + sphere.name + " in build order (" + mods.Length + "): " + text);
            }
            catch (Exception e)
            {
                SeasonsLog.Say("the PQS mod order could not be listed (" + CompatibilityLog.Reason(e) + ")");
            }
        }
    }
}
