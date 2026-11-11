using System;
using ReDefinition.Core;
using ReDefinition.Modules;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The part of RtCamera that takes the weather (WeatherHooks.View) into the packet: the fog on the rays, how far the rays go in
    // fog, how many bounces they take in a blizzard, and the sun's share under cloud. Below RtWeatherMath.LevelFloor the weather
    // changes nothing: its small moves would make the denoiser churn for no visible gain.
    //
    // Three switches (OwnSettings.RtWeatherFog, RtWeatherReachCap, RtWeatherFewerBounces; the benchmark group rt-weather):
    //  - the fog is a look and is on;
    //  - the reach cap and the bounce cut are quality costs that save time in a storm, and are off until the owner agrees to them.
    internal sealed partial class RtCamera
    {
        // Which parts of the weather the rays take. Read once per frame.
        internal struct WeatherSwitches
        {
            public bool Fog, ReachCap, FewerBounces;

            // The add-on's live settings; the defaults (fog on, the two quality costs off) where there is no add-on.
            internal static WeatherSwitches FromAddon()
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                if (addon == null) return new WeatherSwitches { Fog = true };
                OwnSettings live = addon.Live;
                return new WeatherSwitches { Fog = live.RtWeatherFog, ReachCap = live.RtWeatherReachCap, FewerBounces = live.RtWeatherFewerBounces };
            }
        }

        // The switches of this frame; tests replace it.
        internal Func<WeatherSwitches> WeatherSwitchesOf = WeatherSwitches.FromAddon;

        // The weather of this frame, or null where the atmospheres write none.
        private static WeatherView FreshWeather()
        {
            WeatherView view = WeatherHooks.View;
            return view.Active && !view.IsStale(Time.frameCount) ? view : null;
        }

        // The sun's colour times its share under cloud (WeatherView.SunTransmission), once. Raytracing, solar panels and the sky read
        // only that value; EVE's own screen-space cloud shadow darkens the frame as well, so the share counts with a weight
        // (RtWeatherMath.DoubleDarkeningWeight). The colour with the share is what the probes print as the rays' sun
        // (TracedSunColour), and RtWeatherMath.SunScale of the view's transmission is the factor in it.
        private static Color WithCloudShare(Color sunColour)
        {
            WeatherView view = FreshWeather();
            if (view == null) return sunColour;
            float scale = RtWeatherMath.SunScale(view.SunTransmission);
            return new Color(sunColour.r * scale, sunColour.g * scale, sunColour.b * scale, sunColour.a);
        }

        // The factor WithCloudShare put on the sun's colour this frame, 1 for none; the probes print it beside the rays' sun.
        internal static float CloudShareFactor()
        {
            WeatherView view = FreshWeather();
            return view == null ? 1f : RtWeatherMath.SunScale(view.SunTransmission);
        }

        // The weather's fog, and the rays' reach and bounces after the weather, written over what RtPacketBuilder.Params set.
        private void ApplyWeather()
        {
            WeatherView view = FreshWeather();
            WeatherSwitches on = WeatherSwitchesOf();
            // The packet is the same one every frame: the fog is written whatever the weather, 0 for none.
            float density = view != null && on.Fog ? RtWeatherMath.FogDensityFor(view.Level, view.FogDensity) : 0f;
            packet.Fog[0] = density;
            Color fog = view != null ? view.FogColour : Color.black;
            packet.Fog[1] = density > 0f ? fog.r : 0f;
            packet.Fog[2] = density > 0f ? fog.g : 0f;
            packet.Fog[3] = density > 0f ? fog.b : 0f;
            if (view == null) return;
            if (on.ReachCap)
            {
                float reach = RtWeatherMath.ReachFor(view.Level, view.VisibilityM, RtPacketBuilder.RayReach);
                packet.Params[RtPacketBuilder.ParamDiffuseReach] = Math.Min(packet.Params[RtPacketBuilder.ParamDiffuseReach], reach);
                packet.Params[RtPacketBuilder.ParamSpecularReach] = Math.Min(packet.Params[RtPacketBuilder.ParamSpecularReach], reach);
            }
            if (on.FewerBounces)
                packet.Params[RtPacketBuilder.ParamBounces] = RtWeatherMath.BouncesFor(view.Level, packet.Params[RtPacketBuilder.ParamBounces]);
        }
    }

    // The numbers behind RtCamera.Weather. Free of Unity, for the tests.
    internal static class RtWeatherMath
    {
        // Below this precipitation level the weather does not touch the rays.
        internal const float LevelFloor = 0.2f;

        // Above this level the rays take one bounce fewer (not below one), with RtWeatherReachCap's sibling switch on.
        internal const float HeavyLevel = 0.7f;

        // The rays go no further than this many times the visibility, with the reach switch on.
        internal const float ReachInVisibilities = 3f;

        // The shortest reach the cap gives, in metres: the near field stays in the rays.
        internal const float MinReachM = 200f;

        // The weight of the cloud share on the sun in the rays. [VERIFY] 0.5 until the measurement series (E40) says how much of the
        // darkening EVE's own screen-space cloud shadow already does: the sun in the rays under a cloud shadow, with the weight 0, 0.5
        // and 1, against the screen-space shadow's own darkening on the same pixels (K-W1 rows C24.cloud_weight_*, procedure in
        // tests/series/rows/C24.json).
        internal const float DoubleDarkeningWeight = 0.5f;

        // The most fog density the rays take, in 1 / metre: the fog of a 60 m visibility is 0.029, the weather's own limit.
        internal const float MaxFogDensity = 0.5f;

        // The fog density the packet carries: the view's below LevelFloor not at all (the fog fades in with the level on its own
        // there, and the denoiser's history would churn on it), 0 for a value that is not a positive number, within MaxFogDensity.
        internal static float FogDensityFor(float level, float density)
        {
            if (float.IsNaN(level) || level < LevelFloor || float.IsNaN(density) || density <= 0f) return 0f;
            return density > MaxFogDensity ? MaxFogDensity : density;
        }

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

        // The share of the light a fog segment of this length lets through: Unity's exp2 fog, as RtCommon.hlsli FogKeep takes it.
        // The CPU twin the tests and the harness check the shader against.
        internal static double FogKeep(double distanceM, double density)
        {
            if (!(density > 0.0) || double.IsNaN(distanceM)) return 1.0;
            double x = Math.Min(distanceM, 1.0e7) * density;
            return Math.Exp(-x * x);
        }
    }
}
