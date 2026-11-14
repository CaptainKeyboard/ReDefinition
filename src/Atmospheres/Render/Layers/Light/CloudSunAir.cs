using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The sun colour of the cloud pass is the colour of the sun at the cloud's altitude (E40 sun colour). Scatterer multiplies Unity's sun light by the air between the
    // active vessel and the sun, so the colour the pass reads is the sun as filtered by the air above the CAMERA: a low camera reddened every cloud, a camera high above a
    // low deck whitened it. The pass takes the camera's air out of that colour here (the shader's SunAir puts the air above each sample back), with the air that made the
    // colour: Scatterer's own node of the body (ScattererAirReader), the same two components, scale heights and air mass as the shader.
    // Scatterer's ozone, its extinctionThickness and its noon blend act above both altitudes and stay in the colour untouched.
    internal static class CloudSunAir
    {
        // The sun colour that goes to the pass: viewerSun is Scatterer's colour at the viewer (light colour times intensity), unextinguished the colour of the light
        // before Scatterer modulates it (null: unknown). The air above the viewer is divided out per channel, with the share floored at CloudLightMath.CameraAirFloor, and the
        // result never exceeds the unextinguished sun: the sun cannot gain light. sinElevation is the sun's elevation at the viewer, altitudeM its height above the sea level.
        internal static Color OutsideAir(Color viewerSun, Color? unextinguished, double sinElevation, double altitudeM, CloudLightSettings air)
        {
            double fr = Factor(sinElevation, altitudeM, air, air.SunAirTau.x), fg = Factor(sinElevation, altitudeM, air, air.SunAirTau.y), fb = Factor(sinElevation, altitudeM, air, air.SunAirTau.z);
            float r = (float)(viewerSun.r * fr), g = (float)(viewerSun.g * fg), b = (float)(viewerSun.b * fb);
            if (unextinguished.HasValue)
            {
                Color u = unextinguished.Value;
                r = Math.Min(r, u.r);
                g = Math.Min(g, u.g);
                b = Math.Min(b, u.b);
            }
            return new Color(r, g, b, viewerSun.a);
        }

        private static double Factor(double sinElevation, double altitudeM, CloudLightSettings air, float tauChannel)
        {
            return CloudLightMath.CameraAirFactor(sinElevation, tauChannel, air.SunAirHeightM, air.SunAirMieTau, air.SunAirMieHeightM, altitudeM, air.SunAirWhiteBalance);
        }
    }
}
