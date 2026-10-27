using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The quad over the whole screen that the raytracing's and the moons' glow's passes draw with their own materials.
    internal static class RtScreenQuad
    {
        // A new quad over clip space, named `name`, with bounds no camera culls.
        internal static Mesh Make(string name)
        {
            var quad = new Mesh { name = name };
            quad.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return quad;
        }
    }
}
