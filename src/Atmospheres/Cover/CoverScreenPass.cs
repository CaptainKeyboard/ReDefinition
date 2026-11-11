using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // What one record of the screen-space cover needs: the regions of the vessels, the cover colour, the light and the mask edges.
    internal struct CoverScreenDraw
    {
        internal CoverScreenMath.Region[] Regions;
        internal int Count;
        // The local up (unit), the cover colour, the sun's direction towards the sun (unit), the sun's colour and the ambient colour.
        internal Vector3 Up;
        internal Color Colour;
        internal Vector3 TowardsSun;
        internal Color SunColour, Ambient;
        internal float EdgeLow, EdgeHigh, Grain;
    }

    // Records the screen-space vehicle cover (shader ReDefinition/AtmospheresShellScreen): one full-screen triangle into the camera's
    // colour target, from the camera's depth texture. Engine only (no game type), so the weather player draws with the class the mod
    // uses. Owns its material and mesh; Dispose frees them. Package D34, variant (f) of the plan.
    internal sealed class CoverScreenPass : IDisposable
    {
        private readonly Material material;
        private readonly Mesh triangle;
        private readonly Vector4[] spheres = new Vector4[CoverScreenMath.MaxVessels];
        private readonly Vector4[] states = new Vector4[CoverScreenMath.MaxVessels];

        // For the player's diagnosis: 0 draws the cover, 1 to 4 write what the shader computes instead (see the shader) about DebugOrigin.
        internal int Debug;
        internal Vector3 DebugOrigin;

        internal CoverScreenPass(Shader shader)
        {
            if (shader == null) throw new ArgumentNullException("shader");
            material = new Material(shader) { name = "ReDefinition Atmospheres shell screen", hideFlags = HideFlags.HideAndDontSave };
            triangle = new Mesh { name = "ReDefinition Atmospheres screen triangle", hideFlags = HideFlags.HideAndDontSave };
            // One triangle in clip space that covers the screen; the vertex shader passes x and y through.
            triangle.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 3f, 0f), new Vector3(3f, -1f, 0f) };
            triangle.triangles = new[] { 0, 1, 2 };
            triangle.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
        }

        // Adds the draw to the buffer; nothing where no vessel is in range. The buffer's target is the camera's (set by the event).
        internal void Record(CommandBuffer buffer, ref CoverScreenDraw draw)
        {
            if (draw.Count <= 0 || draw.Regions == null) return;
            int count = Mathf.Min(draw.Count, CoverScreenMath.MaxVessels);
            for (int i = 0; i < count; i++)
            {
                CoverScreenMath.Region r = draw.Regions[i];
                spheres[i] = new Vector4(r.X, r.Y, r.Z, r.Radius);
                states[i] = new Vector4(r.FloorM, r.Amount, 0f, 0f);
            }
            material.SetVectorArray("_CoverSphere", spheres);
            material.SetVectorArray("_CoverState", states);
            material.SetFloat("_CoverCount", count);
            material.SetVector("_CoverUp", draw.Up);
            material.SetColor("_CoverColor", draw.Colour);
            material.SetVector("_CoverSunDir", draw.TowardsSun);
            material.SetColor("_CoverSunColor", draw.SunColour);
            material.SetColor("_CoverAmbient", draw.Ambient);
            material.SetFloat("_EdgeLow", draw.EdgeLow);
            material.SetFloat("_EdgeHigh", draw.EdgeHigh);
            material.SetFloat("_Grain", draw.Grain);
            material.SetFloat("_CoverDebug", Debug);
            material.SetVector("_CoverDebugOrigin", DebugOrigin);
            buffer.DrawMesh(triangle, Matrix4x4.identity, material, 0, 0);
        }

        public void Dispose()
        {
            if (material != null) UnityEngine.Object.Destroy(material);
            if (triangle != null) UnityEngine.Object.Destroy(triangle);
        }
    }
}
