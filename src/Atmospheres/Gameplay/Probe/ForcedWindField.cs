using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // A field for the wind probe: the weather of the field it wraps with the wind, the level, the optical depth and the visibility replaced
    // by the probe's own numbers, so that every row has a known input. The gust factor is zero and the probe sets gustStrength to 0: the
    // wind is the number set. Installed in WeatherHub.Field by the probe for its scenario and taken out again at the end.
    internal sealed class ForcedWindField : IWeatherField
    {
        private readonly IWeatherField inner;

        internal ForcedWindField(IWeatherField inner)
        {
            this.inner = inner ?? NullField.Instance;
        }

        internal IWeatherField Inner { get { return inner; } }

        // The wind in the east-north-up frame, in m/s.
        internal float EastMs, NorthMs;
        // The precipitation level the sample reports (it lifts the speed cap towards the violent wind) and the type.
        internal float Level01;
        // The vertical optical depth of the cloud.
        internal float OpticalDepth;
        // The visibility in metres.
        internal float VisibilityM = WeatherSample.ClearVisibilityM;

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            if (!inner.Sample(bodyIndex, latDeg, lonDeg, altM, ut, out sample)) sample = WeatherSample.Clear(bodyIndex);
            sample.WindWorld = new Vector3(EastMs, NorthMs, 0f);
            sample.GustFactor = 0f;
            sample.Level01 = Level01;
            sample.OpticalDepthVertical = OpticalDepth;
            sample.VisibilityM = VisibilityM;
            sample.Readiness = FieldReadiness.Ready;
            return true;
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
        {
            return inner.GetMap(bodyIndex, kind, ut, target);
        }

        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target)
        {
            return inner.FillWindow(bodyIndex, centreLatDeg, centreLonDeg, radiusKm, ut, target);
        }

        public void Advance(double budgetMs)
        {
            inner.Advance(budgetMs);
        }

        public long Version { get { return inner.Version + 1; } }
        public long DefinitionId { get { return inner.DefinitionId; } }
        public bool ThreadSafe { get { return inner.ThreadSafe; } }
        public double StepSeconds { get { return inner.StepSeconds; } }
        public FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
    }
}
