using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // EVE's ground effects from the field's type fractions at the camera (brief C23, item 4; plan 09 section 3.6). EVE draws rain, snow,
    // droplets, wet surfaces, lightning and the ambient sound from the coverage the mirror gives it (SampleCoverage) and the values of
    // the cloud type there. Per frame, in Camera.onPreCull after EVE's Update:
    //   - the rain particle field's coverage is scaled by the rain share (a sleet fall counts half), the snow field is raised to the
    //     level times the snow share, the droplets, wet surfaces, lightning and sounds follow GroundEffectMath;
    //   - cloud type values are written as original x factor and put back by Release;
    //   - with cloudSource Both and useEveWeather, EVE's own weather on layers we do not paint counts as a level of its own
    //     (Peve = 0.55 x coverage^0.8, thunder up to 0.8) and is joined to the field's level at the camera.
    // It is also the IEveFieldSuppressor: while Suppressed, EVE's particle fields (rain and snow) are held at 0 so that our precipitation
    // renderer is the only one; droplets, wet surfaces, lightning and sound still follow the factors.
    internal sealed class EveGroundEffects : IEveFieldSuppressor
    {
        private bool suppressed;
        private string suppressReason = "";
        private string bodyName = "";

        public bool Suppressed { get { return suppressed; } }

        public void SetSuppressed(bool suppress, string reason)
        {
            if (suppressed == suppress) return;
            suppressed = suppress;
            suppressReason = reason ?? "";
            AtmospheresLog.Say("EVE particle fields " + (suppress ? "suppressed" : "released") + " (" + suppressReason + ")");
            if (!suppress) EveSnowLink.Release();
        }

        // The factors in force at the camera for a sample, with the join of EVE's own weather where it applies.
        internal static GroundEffectMath.Factors FactorsFor(WeatherSample sample, float eveCoverage, bool useEveWeather, float centreC, float widthC)
        {
            float level = sample.Level01;
            float snow = sample.SnowFraction, sleet = sample.SleetFraction, rain = sample.RainFraction;
            if (useEveWeather)
            {
                float peve = GroundEffectMath.EveLevel(eveCoverage, false, false);
                if (peve > level)
                {
                    float joined = GroundEffectMath.Union(level, peve);
                    if (!(level > 0f))
                    {
                        TypeMix mix = PrecipTypeModel.Mix(sample.TemperatureK, joined, centreC, widthC);
                        snow = mix.Snow;
                        sleet = mix.Sleet;
                        rain = mix.Rain;
                    }
                    level = joined;
                }
            }
            return GroundEffectMath.From(snow, sleet, rain, level);
        }

        // Applies the factors for this frame; level is the level that goes with them (snow field and sound).
        internal void Apply(string body, GroundEffectMath.Factors f, float level)
        {
            if (!string.Equals(bodyName, body, StringComparison.Ordinal))
            {
                Release();
                bodyName = body;
            }
            if (EveRainLink.Controllable) EveRainLink.Scan(body);
            if (EveSnowLink.Controllable) EveSnowLink.Scan(body);
            if (suppressed)
            {
                f.RainField = 0f;
                f.SnowField = 0f;
                f.RainSound = 0f;
                f.SnowSound = 0f;
            }
            EveRainLink.Scale(f);
            float snow = Mathf.Clamp01(level * f.SnowField);
            if (snow > 0f)
            {
                EveSnowLink.Raise(snow);
                EveSnowLink.RaiseSound(level * f.SnowSound);
            }
            else
            {
                EveSnowLink.Release();
            }
        }

        // Puts everything EVE had back.
        internal void Release()
        {
            EveRainLink.Release();
            EveSnowLink.Release();
            bodyName = "";
        }
    }
}
