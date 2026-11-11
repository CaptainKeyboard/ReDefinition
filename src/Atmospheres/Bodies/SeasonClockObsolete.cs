using System;

namespace ReDefinition.Atmospheres
{
    // The single-body API of the season clock, kept until the last caller package removes its use (OrbitSnow.cs and SnowRefresher.cs:
    // D34; StormEffect.cs: C20; LegacyBridge.cs: A01 bridge). Nothing new may call it; the model-based API in SeasonClock.cs and
    // SeasonModel.cs replaces it. This file is on the allow-list of the body-name test and leaves when the callers do.
    internal static partial class SeasonClock
    {
        // The body the old seasons applied to.
        [Obsolete("The season clock applies to every body with a SeasonModel; use Bodies.Get(body).Seasons.")]
        internal const string BodyName = "Kerbin";

        // The phase of the southern hemisphere, in years, against the northern one.
        [Obsolete("Use SeasonModel.EffectiveHemisphereOffset.")]
        internal const double SouthernOffset = 0.5;

        // Whether the seasons applied to that body.
        [Obsolete("Use Bodies.Get(body).Seasons.Clock.")]
        internal static bool Applies(string bodyName)
        {
            return string.Equals(bodyName, "Kerbin", StringComparison.Ordinal);
        }
    }
}
