using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The own cloud layers of ReDefinition Atmospheres for the rays (RtCommon.hlsli: OwnCloudTau, CloudTransmittance, CloudOverSky in the own mode): the
    // packet's cloud block (Raytracing.h, RtFramePacket.clouds) from the view the atmospheres publish (RayCloudSeam.View, ReDefinition.Common), with
    // the view's texture as RT_INPUT_CLOUDS. Taken instead of EVE's layer (RtEveClouds) wherever the view is fresh for the scene's body, so that the
    // rays never see a cloud layer twice; the atmospheres publish only under cloudSource Ours and while the rays ask (RayCloudSeam.Ask).
    //
    // The block in the own mode (RT_OWN_CLOUDS_*):
    //   0-11  rows 0-2 of the camera-relative world to the map frame: the body's centre at the origin, x towards longitude 0, y towards longitude
    //         90 east, z north, in m.
    //   12-15 the highest top and the lowest base of the layers (m from the centre), the layer count, the mode (RayCloudLayout.ModeOwn).
    //   16-19 the texture's width and a slice's height in texels, the fade of the whole (1), the most taps a span takes.
    //   20-23 the shadow's tint (white) and the cloud's brightness in a reflection (1, as EVE's).
    internal static class RtOwnCloudBlock
    {
        // Fills the block for the scene's body and the camera (world space); false and nothing written where the view is not fresh for it.
        internal static bool Fill(float[] clouds, CelestialBody body, Vector3 camera)
        {
            if (body == null || !RayCloudSeam.Fresh(Time.frameCount, body.flightGlobalsIndex)) return false;
            Write(clouds, RayCloudSeam.View, (Vector3)body.position, camera);
            return true;
        }

        // The block from a view, the body's centre and the camera, both world space.
        internal static void Write(float[] clouds, RayCloudView view, Vector3 centre, Vector3 camera)
        {
            for (int i = 0; i < clouds.Length; i++) clouds[i] = 0f;
            Matrix4x4 m = view.WorldToMap * Matrix4x4.Translate(camera - centre);
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 4; column++)
                    clouds[row * 4 + column] = m[row, column];
            clouds[12] = view.OuterRadiusM;
            clouds[13] = view.InnerRadiusM;
            clouds[14] = view.LayerCount;
            clouds[15] = RayCloudLayout.ModeOwn;
            clouds[16] = view.Width;
            clouds[17] = view.SliceHeight;
            clouds[18] = 1f;
            clouds[19] = Mathf.Clamp(view.Taps, 1, 16);
            clouds[20] = 1f;
            clouds[21] = 1f;
            clouds[22] = 1f;
            clouds[23] = 1f;
        }
    }
}
