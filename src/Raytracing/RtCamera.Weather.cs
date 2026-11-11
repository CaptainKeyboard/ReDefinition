using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The part of RtCamera that takes the weather (WeatherHooks.View) into the packet: how far the rays go in fog, how many bounces they
    // take in a blizzard, and the sun's share under cloud. Below RtWeatherMath.LevelFloor the weather changes nothing: its small
    // moves would make the denoiser churn for no visible gain.
    internal sealed partial class RtCamera
    {
        // The weather of this frame, or null where the atmospheres write none.
        private static WeatherView FreshWeather()
        {
            WeatherView view = WeatherHooks.View;
            return view.Active && !view.IsStale(Time.frameCount) ? view : null;
        }

        // The sun's colour times its share under cloud (WeatherView.SunTransmission), once. Raytracing, solar panels and the sky read
        // only that value; EVE's own screen-space cloud shadow darkens the frame as well, so the share counts with a weight
        // (RtWeatherMath.DoubleDarkeningWeight).
        private static Color WithCloudShare(Color sunColour)
        {
            WeatherView view = FreshWeather();
            if (view == null) return sunColour;
            float scale = RtWeatherMath.SunScale(view.SunTransmission);
            return new Color(sunColour.r * scale, sunColour.g * scale, sunColour.b * scale, sunColour.a);
        }

        // The rays' reach and bounces after the weather, written over what RtPacketBuilder.Params set.
        private void ApplyWeather()
        {
            WeatherView view = FreshWeather();
            if (view == null) return;
            float reach = RtWeatherMath.ReachFor(view.Level, view.VisibilityM, RtPacketBuilder.RayReach);
            packet.Params[RtPacketBuilder.ParamDiffuseReach] = Math.Min(packet.Params[RtPacketBuilder.ParamDiffuseReach], reach);
            packet.Params[RtPacketBuilder.ParamSpecularReach] = Math.Min(packet.Params[RtPacketBuilder.ParamSpecularReach], reach);
            packet.Params[RtPacketBuilder.ParamBounces] = RtWeatherMath.BouncesFor(view.Level, packet.Params[RtPacketBuilder.ParamBounces]);
        }
    }

    // The numbers behind RtCamera.Weather. Free of Unity, for the tests.
    internal static class RtWeatherMath
    {
        // Below this precipitation level the weather does not touch the rays.
        internal const float LevelFloor = 0.2f;

        // Above this level the rays take one bounce fewer (not below one).
        internal const float HeavyLevel = 0.7f;

        // The rays go no further than this many times the visibility.
        internal const float ReachInVisibilities = 3f;

        // The shortest reach the cap gives, in metres: the near field stays in the rays.
        internal const float MinReachM = 200f;

        // The weight of the cloud share on the sun in the rays. [VERIFY] 0.5 until the measurement series (E40) says how much of the
        // darkening EVE's own screen-space cloud shadow already does.
        internal const float DoubleDarkeningWeight = 0.5f;

        // The reach of the diffuse and specular rays in metres: `full` below LevelFloor or without a usable visibility, else
        // ReachInVisibilities times the visibility, within MinReachM and `full`.
        internal static float ReachFor(float level, float visibilityM, float full)
        {
            if (float.IsNaN(level) || level < LevelFloor || float.IsNaN(visibilityM) || visibilityM <= 0f) return full;
            float reach = ReachInVisibilities * visibilityM;
            return reach < MinReachM ? Math.Min(MinReachM, full) : reach > full ? full : reach;
        }

        // The bounces after the weather: one fewer in heavy weather, never below one (nor above `bounces`).
        internal static float BouncesFor(float level, float bounces)
        {
            if (float.IsNaN(level) || level <= HeavyLevel || bounces <= 1f) return bounces;
            return Math.Max(bounces - 1f, 1f);
        }

        // The factor on the sun's colour for a transmission from 0 to 1: 1 for none, falling to the transmission with weight
        // DoubleDarkeningWeight.
        internal static float SunScale(float transmission)
        {
            if (float.IsNaN(transmission)) return 1f;
            float t = transmission < 0f ? 0f : transmission > 1f ? 1f : transmission;
            return 1f + DoubleDarkeningWeight * (t - 1f);
        }
    }
}
