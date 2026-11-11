using UnityEngine;

namespace ReDefinition.Core
{
    // The weather at one place and time: what IWeatherField.Sample and WeatherHub.Sample return. A plain struct of values, no
    // Unity objects, so any thread may hold a copy; worker threads copy it and never read WeatherView.
    public struct WeatherSample
    {
        // The visibility, in metres, when nothing limits it. Every visibility in the weather seam is in metres.
        public const float ClearVisibilityM = 100000f;

        // The precipitation level from 0 to 1 (PrecipScale gives the band).
        public float Level01;
        // What falls, and how a mixed fall divides: the three fractions add up to 1 where something falls and are 0 otherwise.
        public PrecipType Type;
        public float SnowFraction;
        public float SleetFraction;
        public float RainFraction;
        // The visibility along the ground in metres.
        public float VisibilityM;
        // The mean wind in m/s in the body-fixed frame: x east, y north, z up (the body rotates, the world origin floats, so the
        // frame is the body's, not the world's).
        public Vector3 WindWorld;
        // How strong the gusts are, 0 to 1; the consumer applies WindNoise.Gust.
        public float GustFactor;
        // The air temperature in kelvin; NaN where the source does not know it.
        public float TemperatureK;
        // The cloud cover above the place, 0 to 1.
        public float CloudCover01;
        // The vertical optical depth of the cloud above the place, dimensionless; from the field or from the host's
        // WeatherHooks.CloudOpticalDepth where EVE's decks are present. SunTransmission.From turns it into a transmission.
        public float OpticalDepthVertical;
        // The index of the body in FlightGlobals.Bodies.
        public int BodyIndex;
        // How far the field is along: Ready, or one of the states in which the value is an estimate.
        public FieldReadiness Readiness;
        // True where the value is a catch-up or climatological estimate (a focused body not simulated in real time).
        public bool Approximate;

        // A sample in which nothing falls, nothing limits the visibility and the air is calm.
        public static WeatherSample Clear(int bodyIndex)
        {
            var sample = new WeatherSample();
            sample.Type = PrecipType.None;
            sample.VisibilityM = ClearVisibilityM;
            sample.TemperatureK = float.NaN;
            sample.BodyIndex = bodyIndex;
            sample.Readiness = FieldReadiness.Ready;
            return sample;
        }

        // The band of this sample for the given sustained wind; the wind at the place is used when the caller has no better one.
        public PrecipBand Band
        {
            get { return PrecipScale.BandOf(Level01, (float)System.Math.Sqrt(WindWorld.x * WindWorld.x + WindWorld.y * WindWorld.y)); }
        }
    }
}
