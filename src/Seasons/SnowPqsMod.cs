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

        // The quad being built, and the phase it is built for: the clock is read once per quad.
        private PQ currentQuad;
        private double phase;
        private static bool orderLogged;

        public override void OnSetup()
        {
            requirements = PQS.ModiferRequirements.MeshColorChannel;
            order = SortOrder;
            LogOrder();
        }

        public override void OnVertexBuild(PQS.VertexBuildData data)
        {
            PQ quad = data.buildQuad;
            if (quad == null || !SeasonsSettings.Enabled) return;
            if (data.vertIndex == 0 || !ReferenceEquals(quad, currentQuad))
            {
                currentQuad = quad;
                phase = SeasonState.Phase();
            }
            double x = data.directionFromCenter.x, y = data.directionFromCenter.y, z = data.directionFromCenter.z;
            double latitude = Math.Asin(y < -1 ? -1 : y > 1 ? 1 : y) * (180.0 / Math.PI);
            double height = data.vertHeight - sphere.radius;
            double noise = SnowNoise.Value(x, y, z);
            float amount = QuadSnow.Amount(latitude, height, phase, noise, SeasonsSettings.SnowStrength, SeasonsSettings.WinterSnowLine);
            if (SeasonsSettings.GroundRefreshMode != SeasonsSettings.GroundRefresh.Off)
            {
                QuadSnow snow = SnowQuads.For(quad, quad.verts != null && quad.verts.Length > 0 ? quad.verts.Length : 225);
                snow.Set(data.vertIndex, (Color32)data.vertColor, latitude, height, noise);
                snow.PaintPhase = phase;
            }
            data.vertColor = SnowPaint.Blend(data.vertColor, amount);
        }

        public override void OnQuadDestroy(PQ quad)
        {
            SnowQuads.Remove(quad);
            if (ReferenceEquals(quad, currentQuad)) currentQuad = null;
        }

        public override void OnSphereReset()
        {
            SnowQuads.Clear();
            currentQuad = null;
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
                Debug.Log(Log.Tag + " Seasons: PQS mods of " + sphere.name + " in build order (" + mods.Length + "): " + text);
            }
            catch (Exception e)
            {
                Debug.Log(Log.Tag + " Seasons: the PQS mod order could not be listed (" + CompatibilityLog.Reason(e) + ")");
            }
        }
    }
}
