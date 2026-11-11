using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Leaves out the terrain that the storm's fog hides. While a storm runs and the setting stormCullDistance is on, the flight
    // camera does not draw layer 15 (Local Scenery: the terrain quads and the buildings) beyond StormVolumeMath.CullDistanceM of the
    // visibility. It uses Camera.layerCullDistances, so the far plane, the sky, the scaled space and the other layers are not touched.
    // The distance follows the visibility, so it grows without a step as the storm fades. The camera's own distances are put back
    // when the storm ends or the setting is off.
    internal sealed class StormCull
    {
        // Unity's layer of the terrain quads and the buildings, "Local Scenery".
        internal const int SceneryLayer = 15;

        private Camera camera;
        private float[] original;
        private bool applied;

        // The distance that is set now, in m; 0 where nothing is culled.
        internal float DistanceM { get; private set; }

        // Called for the flight camera before it culls; visibilityM is the storm's visibility now, or 0 for no storm.
        internal void Apply(Camera cam, double visibilityM)
        {
            if (cam == null) return;
            if (!AtmospheresSettings.StormCullDistance || visibilityM <= 0)
            {
                Restore();
                return;
            }
            if (!applied || !ReferenceEquals(camera, cam))
            {
                Restore();
                camera = cam;
                original = cam.layerCullDistances;
                applied = true;
            }
            DistanceM = (float)StormVolumeMath.CullDistanceM(visibilityM);
            float[] distances = (float[])original.Clone();
            distances[SceneryLayer] = DistanceM;
            camera.layerCullDistances = distances;
        }

        // Puts the camera's own distances back.
        internal void Restore()
        {
            if (applied && camera != null && original != null) camera.layerCullDistances = original;
            applied = false;
            camera = null;
            original = null;
            DistanceM = 0f;
        }
    }
}
