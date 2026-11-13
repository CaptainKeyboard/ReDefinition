using System;

namespace ReDefinition.Atmospheres
{
    // Which optical depth the sky and the sun dimming use over a place: the host's cloud hook (WeatherHooks.CloudOpticalDepth) or the
    // field's own value. The hook has three answers. No hook: the field's value. A reading of 0 or more: that is the sky over the place,
    // 0 included (a clear sky between the clouds stays clear and does not take the field's value, which would dim the sun with no cloud
    // to see). A negative or non-finite reading (RtEveClouds.OpticalDepthUnreadable, an unbound own-cloud hook): the hook cannot read
    // this place, and the field's value counts. The frame driver and the solar dimming both call it, so that the panels and the picture
    // agree.
    internal static class CloudDepthRule
    {
        // The reading of a hook that cannot read a place.
        internal const float Unreadable = -1f;

        internal static float Resolve(Func<double, double, double, double, float> hook, double latDeg, double lonDeg, double altitudeM, double ut, float fieldDepth)
        {
            if (hook == null) return fieldDepth;
            float t = hook(latDeg, lonDeg, altitudeM, ut);
            if (!(t >= 0f) || float.IsInfinity(t)) return fieldDepth;
            return t;
        }
    }
}
