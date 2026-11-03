using System;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The fields of a frame's packet (RtNative.FramePacket) that follow from the values they are handed, without
    // the camera's own state: RtCamera reads its sources and settings, this writes them into the packet's arrays.
    // The arrays are the packet's own and are written in place, so a frame allocates nothing.
    internal static class RtPacketBuilder
    {
        // Slots of the packet that carry more than the name of their array says. Params is RtNative.FramePacket.Params.
        // The sun's shadow strength is in the sun colour's w, and the waves' time in the planet's w.
        internal const int ParamOcclusion = 0, ParamBounces = 1, ParamDiffuseReach = 2, ParamSpecularReach = 3, ParamClamp = 4,
                           ParamEnvironment = 5, ParamTextures = 6, ParamGBuffer = 7;
        internal const int SunShadowStrength = 3, PlanetWaveTime = 3;
        // The meadow's range and the range of the grass about the vessel are in the shadow fade's slots 6 and 11.
        internal const int ShadowFadeMeadowRange = 6, ShadowFadeVesselRange = 11;
        // How far the diffuse and the specular rays go, in metres.
        internal const float RayReach = 100000f;
        // The light a meadow's blades keep from its ground. Measured at the launch pad on the RTX A4500, 2026-10-25,
        // with the blades letting through half the sun (rtFoliageTransmission 0.5). The terrain past Parallax's grass
        // about the vessel is as bright as the terrain in it: 0.99 of it by day and 0.94 in the evening. Medians of
        // eleven views from 150 m to 4 km, single views 0.81 to 1.10. When the rays saw the grass's
        // shadow only near the camera (RtCommon.hlsli, RayOrigin), it was 0.77. It goes into the ground's
        // albedo for sun and sky alike.
        internal const float MeadowShade = 0.82f;

        // Unity's ambient light is handed over facing up, sideways and down.
        private static readonly Vector3[] AmbientDirections = { Vector3.up, Vector3.right, Vector3.down };

        // A translation by x, y, z, in single precision.
        internal static Matrix4x4 Translation(double x, double y, double z)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m03 = (float)x;
            m.m13 = (float)y;
            m.m23 = (float)z;
            return m;
        }

        // Rows first, as the proxy takes them: Unity's [row, column].
        internal static void Write(Matrix4x4 m, float[] target)
        {
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    target[row * 4 + column] = m[row, column];
        }

        // The sun: its direction normalised with the angular radius in w, and its colour. Its shadow strength is
        // how much of it Unity's shadows take (KSP's sun 0.75), for the forward light's estimate of what Unity
        // shaded with.
        internal static void Sun(ref RtNative.FramePacket packet, Vector4 towards, Color colour, float shadowStrength)
        {
            Vector3 direction = new Vector3(towards.x, towards.y, towards.z).normalized;
            packet.SunDirection[0] = direction.x;
            packet.SunDirection[1] = direction.y;
            packet.SunDirection[2] = direction.z;
            packet.SunDirection[3] = towards.w;
            packet.SunColor[0] = colour.r;
            packet.SunColor[1] = colour.g;
            packet.SunColor[2] = colour.b;
            packet.SunColor[SunShadowStrength] = shadowStrength;
        }

        // How Unity fades the sun's shadow out towards the shadow distance: its centre (turned with the camera,
        // and its w), and its scale. All 0 where it does not fade.
        internal static void ShadowFade(ref RtNative.FramePacket packet, bool fades, Vector3 centre, float centreW, Vector2 scale)
        {
            packet.ShadowFade[0] = fades ? centre.x : 0f;
            packet.ShadowFade[1] = fades ? centre.y : 0f;
            packet.ShadowFade[2] = fades ? centre.z : 0f;
            packet.ShadowFade[3] = fades ? centreW : 0f;
            packet.ShadowFade[4] = fades ? scale.x : 0f;
            packet.ShadowFade[5] = fades ? scale.y : 0f;
        }

        // The meadow's shade on the ground past Parallax's grass (RtCommon.hlsli, MeadowShade). It takes how far the
        // grass is drawn, with the light a meadow's own blades keep from it. The grass Parallax draws about the
        // vessel comes as the vessel relative to the camera, and how far.
        internal static void Meadow(ref RtNative.FramePacket packet, float range, Vector3 vesselOffset, float vesselRange)
        {
            packet.ShadowFade[ShadowFadeMeadowRange] = range;
            packet.ShadowFade[7] = MeadowShade;
            packet.ShadowFade[8] = vesselOffset.x;
            packet.ShadowFade[9] = vesselOffset.y;
            packet.ShadowFade[10] = vesselOffset.z;
            packet.ShadowFade[ShadowFadeVesselRange] = vesselRange;
        }

        // The sky's colour where no environment is given, and how much of it the sky's light loses (0 to 1).
        internal static void Sky(ref RtNative.FramePacket packet, Color sky, float neutral)
        {
            packet.SkyColor[0] = sky.r;
            packet.SkyColor[1] = sky.g;
            packet.SkyColor[2] = sky.b;
            packet.SkyColor[3] = Mathf.Clamp01(neutral);
        }

        // Unity's ambient light, as it shades by itself; none where the raytraced light replaces it. The probe is
        // asked at `at` only where it is used, and its three directions go through `values`.
        internal static void Ambient(ref RtNative.FramePacket packet, bool composite,
                                     Func<Vector3, SphericalHarmonicsL2> probe, Vector3 at, Color[] values)
        {
            for (int i = 0; i < 12; i++) packet.Ambient[i] = 0f;
            if (composite)
            {
                probe(at).Evaluate(AmbientDirections, values);
                for (int i = 0; i < 3; i++)
                {
                    packet.Ambient[i * 4] = values[i].r;
                    packet.Ambient[i * 4 + 1] = values[i].g;
                    packet.Ambient[i * 4 + 2] = values[i].b;
                }
                packet.Ambient[3] = QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1f : 0f;
            }
        }

        // The rays' settings: the occlusion, the bounces, how far the rays go, the clamp and which inputs there are.
        internal static void Params(ref RtNative.FramePacket packet, RtSettings settings, bool deferred, bool withEnvironment)
        {
            packet.Params[ParamOcclusion] = settings.OcclusionDistance;
            packet.Params[ParamBounces] = settings.Bounces;
            packet.Params[ParamDiffuseReach] = RayReach;
            packet.Params[ParamSpecularReach] = RayReach;
            packet.Params[ParamClamp] = settings.Clamp;
            packet.Params[ParamEnvironment] = withEnvironment ? 1f : 0f;
            packet.Params[ParamTextures] = 1f;
            packet.Params[ParamGBuffer] = deferred ? 1f : 0f;
        }

        // The bodies lit by the sun as lights, and the least light a surface gets. `lights` fills the packet's lights
        // and says how many there are. It returns whether the sea is KSP's own.
        internal static bool Lights(ref RtNative.FramePacket packet, Func<float[], float[], int> lights,
                                    Func<float> sunMirror, Func<bool> stockWater, Func<float> nightLight)
        {
            for (int i = 0; i < 16; i++) packet.Lights[i] = 0f;
            for (int i = 0; i < 8; i++) packet.LightSurface[i] = 0f;
            packet.LightParams[0] = Mathf.Clamp(lights(packet.Lights, packet.LightSurface), 0, 2);
            // Unity's colours are gamma-space values in gamma space; the light transport
            // takes them to linear (RtCommon.hlsli, InputColor).
            packet.LightParams[1] = QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1f : 0f;
            packet.LightParams[2] = Mathf.Max(sunMirror(), 0f);
            bool stock = stockWater();
            packet.LightParams[3] = stock ? 1f : 0f;
            // KSP's flat ambient light, as Deferred takes it (RefreshLegacyAmbient).
            Color night = RenderSettings.ambientLight * Mathf.Clamp01(nightLight());
            packet.NightLight[0] = night.r;
            packet.NightLight[1] = night.g;
            packet.NightLight[2] = night.b;
            return stock;
        }

        // The main body: its albedo, the time of its sea's waves and its sphere relative to the camera. The sea's
        // frame is a point on the sea relative to the camera, with its east and north; all 0 where there is none.
        internal static void Body(ref RtNative.FramePacket packet, Color planet, float waveTime, Vector4 body,
                                  RtPosition camera, bool hasSea, Vector3 seaOrigin, Vector3 seaEast, Vector3 seaNorth)
        {
            packet.Planet[0] = planet.r;
            packet.Planet[1] = planet.g;
            packet.Planet[2] = planet.b;
            packet.Planet[PlanetWaveTime] = waveTime;
            packet.BodySphere[0] = (float)(body.x - camera.x);
            packet.BodySphere[1] = (float)(body.y - camera.y);
            packet.BodySphere[2] = (float)(body.z - camera.z);
            packet.BodySphere[3] = body.w;
            for (int i = 0; i < 12; i++) packet.Sea[i] = 0f;
            if (hasSea)
            {
                packet.Sea[0] = seaOrigin.x;
                packet.Sea[1] = seaOrigin.y;
                packet.Sea[2] = seaOrigin.z;
                packet.Sea[4] = seaEast.x;
                packet.Sea[5] = seaEast.y;
                packet.Sea[6] = seaEast.z;
                packet.Sea[8] = seaNorth.x;
                packet.Sea[9] = seaNorth.y;
                packet.Sea[10] = seaNorth.z;
            }
        }

        // The passes to trace whatever the frame: the sun's always, as its output keeps the view depth the next
        // frame finds its pixels by. The diffuse light and the reflections as asked. Where Deferred's reflections
        // pass does not take it (forward rendering, or Unity's own deferred lighting: `composite`), the light to
        // add to the frame, which reads the diffuse light that replaces the flat ambient light.
        internal static uint Signals(bool diffuse, bool reflections, bool composite)
        {
            uint signals = RtNative.SignalSun;
            if (diffuse) signals |= RtNative.SignalDiffuse;
            if (reflections) signals |= RtNative.SignalSpecular;
            if (composite && (diffuse || reflections))
                signals |= RtNative.SignalForwardLight | RtNative.SignalDiffuse;
            return signals;
        }

        // A frame's flags. It is always the same frame. The others say that the denoiser runs, that the history is
        // reset, and that there are motion vectors. Guides are what Ray Reconstruction asks for. Legacy smoothness is
        // Deferred's from KSP's part shaders. Surface intent is the surfaces as their materials mean them.
        internal static uint Flags(bool denoise, bool reset, bool withMotion, bool guides, bool legacySmoothness,
                                   bool surfaceIntent, uint parityFlag)
        {
            return RtNative.FlagSameFrame | (denoise ? RtNative.FlagDenoise : 0u)
                   | (reset ? RtNative.FlagReset : 0u)
                   | (withMotion ? RtNative.FlagMotionVectors : 0u)
                   | (guides ? RtNative.FlagGuides : 0u)
                   | (legacySmoothness ? RtNative.FlagLegacySmoothness : 0u)
                   | (surfaceIntent ? RtNative.FlagSurfaceIntent : 0u)
                   | parityFlag;
        }
    }
}
