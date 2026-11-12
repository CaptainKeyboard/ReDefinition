using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // What one draw of the wet overlay needs. Engine types only, so that the weather player draws with the class the mod uses.
    internal struct WetDraw
    {
        internal WetLook Look;
        internal WetnessTuning Tuning;
        // The body's centre relative to the camera in world axes, and the body-fixed east and north axes in the world (unit).
        internal Vector3 CentreRel, East, North;
        // The camera's body-fixed position along east and north, metres modulo WrapM.
        internal float EastM, NorthM;
        // The clock of the rings in seconds.
        internal float ClockS;
        // 0 switches the overlay off while every other state stays (the A/B pictures), 1 is on.
        internal float Strength;
        // Scale of the biggest depression feature in cycles per metre.
        internal float NoiseScale;
        // The weather player's diagnosis: 1 in a component leaves that G-buffer target as it is (x diffuse, y smoothness, z normal,
        // w emission and ambient). The default (all 0) writes every target, as the mod does.
        internal Vector4 SkipTargets;
        // The fourth target is the G-buffer's emission texture instead of the camera's own target (the weather player's diagnosis).
        internal bool EmissionIsGBuffer3;
    }

    // Records the wet overlay (shader ReDefinition/AtmospheresWet) into a command buffer: one full-screen triangle into the camera's
    // G-buffer. The caller adds the buffer to the camera at CameraEvent.BeforeLighting. Owns its material; Dispose frees it.
    internal sealed class WetSurfacesPass : IDisposable
    {
        internal const string ShaderName = "ReDefinition/AtmospheresWet";
        // The body-fixed offsets wrap here so that float precision holds far from the origin of the body-fixed frame.
        internal const float WrapM = 8192f;
        private const int PassGBuffer = 0;
        private const int PassMap = 1;

        private static readonly int IdA = Shader.PropertyToID("_WetA");
        private static readonly int IdB = Shader.PropertyToID("_WetB");
        private static readonly int IdCentre = Shader.PropertyToID("_WetCentre");
        private static readonly int IdEast = Shader.PropertyToID("_WetEast");
        private static readonly int IdNorth = Shader.PropertyToID("_WetNorth");
        private static readonly int IdC = Shader.PropertyToID("_WetC");
        private static readonly int IdT = Shader.PropertyToID("_WetT");

        private readonly Material material;
        // The three G-buffer targets and, in the fourth slot, the camera's own target: at BeforeLighting that is the buffer that holds the
        // emission and the ambient light the geometry pass baked in (the weather player measured that a write to GBuffer3 at this event
        // changes nothing and a write to the camera target changes the ambient part of the picture).
        private static readonly RenderTargetIdentifier[] GBuffer =
        {
            new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer0),
            new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer1),
            new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer2),
            new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget)
        };
        // The same with the G-buffer's own fourth texture (the weather player's diagnosis).
        private static readonly RenderTargetIdentifier[] GBufferEmissionTexture =
        {
            new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer0),
            new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer1),
            new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer2),
            new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer3)
        };

        internal WetSurfacesPass(Shader shader)
        {
            if (shader == null) throw new ArgumentNullException("shader");
            material = new Material(shader) { name = "ReDefinition Atmospheres wet", hideFlags = HideFlags.HideAndDontSave };
        }

        // Whether a draw has anything to do (the look is wetter than the tuning's skip level).
        internal static bool NeedsDraw(ref WetDraw d)
        {
            return d.Strength > 0f && d.Look.Wetness > d.Tuning.SkipBelow;
        }

        private void Set(ref WetDraw d)
        {
            WetLook l = d.Look;
            material.SetVector(IdA, new Vector4(l.DiffuseFactor, l.SmoothAdd, WetnessLookMath.FieldThreshold(l.PuddleCover), l.Ripples));
            material.SetVector(IdB, new Vector4(l.PuddleDiffuse, l.PuddleSmooth, l.Frozen, d.ClockS));
            material.SetVector(IdCentre, new Vector4(d.CentreRel.x, d.CentreRel.y, d.CentreRel.z, 0f));
            material.SetVector(IdEast, new Vector4(d.East.x, d.East.y, d.East.z, d.EastM));
            material.SetVector(IdNorth, new Vector4(d.North.x, d.North.y, d.North.z, d.NorthM));
            // Puddle edge softness, the least up-facing share a puddle needs (cos 25 degrees), the strength.
            material.SetVector(IdT, d.SkipTargets);
            material.SetVector(IdC, new Vector4(d.NoiseScale > 0f ? d.NoiseScale : 1f / 32f, 0.04f, 0.906f, d.Strength));
        }

        // Adds the G-buffer overlay to the buffer. Nothing is recorded where the draw has nothing to do. The buffer's render target is
        // set here (the four G-buffer targets and the camera's depth), so the caller adds it at BeforeLighting and nothing else.
        internal void RecordGBuffer(CommandBuffer cmd, ref WetDraw d)
        {
            if (!NeedsDraw(ref d)) return;
            Set(ref d);
            cmd.SetRenderTarget(d.EmissionIsGBuffer3 ? GBufferEmissionTexture : GBuffer, BuiltinRenderTextureType.CameraTarget);
            cmd.DrawProcedural(Matrix4x4.identity, material, PassGBuffer, MeshTopology.Triangles, 3, 1);
        }

        // Adds the aid map (wetness, puddles, up-facing, ripples) to the buffer, drawn into the target the caller has set.
        internal void RecordMap(CommandBuffer cmd, ref WetDraw d)
        {
            Set(ref d);
            cmd.DrawProcedural(Matrix4x4.identity, material, PassMap, MeshTopology.Triangles, 3, 1);
        }

        public void Dispose()
        {
            if (material != null) UnityEngine.Object.Destroy(material);
        }
    }
}
