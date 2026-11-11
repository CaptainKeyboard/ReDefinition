using System.IO;

namespace ReDefinition.Core
{
    // How far a weather field is along on its way to the state it should have at the asked time.
    public enum FieldReadiness
    {
        // The state is the simulated state at the asked time.
        Ready = 0,
        // The state is behind the asked time and runs forward; the output is an estimate (WeatherSample.Approximate).
        CatchingUp = 1,
        // No state yet: the output is the climatological blend while the first state is built.
        Seeding = 2,
        // The field failed (a guard froze it); the output is the climatological blend and stays so.
        Broken = 3
    }

    // The maps a field can give of a whole body. Equirectangular, row 0 at the north pole, column 0 at longitude -180.
    public enum WeatherMapKind
    {
        // Cloud cover, 0 to 1.
        Coverage = 0,
        // The cloud type per texel (the index of the cloud type of the body's configuration).
        Type = 1,
        // The precipitation level, 0 to 1.
        Precipitation = 2,
        // The relative humidity near the surface, 0 to 1.
        Humidity = 3,
        // The depth of snow (or sand or dust) on the ground, in metres.
        SnowCover = 4,
        // The temperature of the ground or the sea, in kelvin.
        SurfaceTemperature = 5,
        // The fraction of the sea covered by ice, 0 to 1.
        IceFraction = 6,
        // The thickness of the sea ice in metres.
        IceThickness = 7,
        // The wind speed near the surface in m/s.
        WindSpeed = 8
    }

    // A provider of weather at a place, besides the field: another mod's weather or a test source. Providers are asked in the
    // order of their priority (WeatherHub). The default for the wind forecast is "none": derive from WeatherSourceBase to get it.
    public interface IWeatherSource
    {
        // A name for the log and for the wind label of a forecast.
        string Name { get; }
        // The higher number wins the wind. The field counts as priority 0.
        int Priority { get; }
        // Whether TrySample may be called from any thread.
        bool ThreadSafe { get; }
        // The weather this source knows at the place, or false where it knows nothing there.
        bool TrySample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample);
        // The wind this source expects at a site at a later time, in m/s east and north, with a gust and a confidence from 0 to 1;
        // false where it has no forecast.
        bool TryWindForecast(ForecastSite site, double ut, out float eastMs, out float northMs, out float gustMs, out float confidence01);
    }

    // The base of a provider: no wind forecast unless it overrides TryWindForecast. A provider derives from it so that a later
    // addition to IWeatherSource does not break it.
    public abstract class WeatherSourceBase : IWeatherSource
    {
        public abstract string Name { get; }
        public virtual int Priority { get { return 0; } }
        public virtual bool ThreadSafe { get { return false; } }
        public abstract bool TrySample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample);

        public virtual bool TryWindForecast(ForecastSite site, double ut, out float eastMs, out float northMs, out float gustMs, out float confidence01)
        {
            eastMs = 0f;
            northMs = 0f;
            gustMs = 0f;
            confidence01 = 0f;
            return false;
        }
    }

    // The weather field of the bodies: the one source of temperature, cold, precipitation, wind and surface cover for everything
    // that is drawn, forecast, heard or simulated. Calls are made from the main thread unless ThreadSafe is true.
    public interface IWeatherField
    {
        // The weather at a place and time. False where the field does not cover the body; the sample is then clear.
        bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample);
        // Fills target with the map of a body (the target is resized when it does not fit). False where the field has none.
        bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target);
        // Fills target with a local 3D cut-out around a place. False where the field has none.
        bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target);
        // Runs the simulation for at most that many milliseconds of this call.
        void Advance(double budgetMs);
        // Changes whenever the output changed, and may change at every step. Readers cache on it.
        long Version { get; }
        // A hash of the seed, the resolved rules and the algorithm version: the same number means the same trajectory. Caches and
        // forecast generation use it.
        long DefinitionId { get; }
        // Whether Sample may be called from any thread.
        bool ThreadSafe { get; }
        // The length of one simulation step in seconds of game time.
        double StepSeconds { get; }
        // How far the field is along.
        FieldReadiness Readiness { get; }
    }

    // A field that can run on a copy of itself into the future: the forecast makes one forward run per body on a clone.
    public interface IForecastableField : IWeatherField
    {
        // A copy of the field with its own state; the copy never writes side files.
        IForecastableField Clone();
        // Runs this field forward by that many hours of game time.
        void RunForward(double hours);
    }

    // A field that keeps checkpoints of its state keyed by game time, so that a rewind loads the latest one before the target.
    public interface ICheckpointedField : IWeatherField
    {
        // Writes the state to the stream.
        void SaveCheckpoint(Stream stream);
        // Reads a state from the stream and sets the field to it at that time of the game. False where the stream does not fit.
        bool LoadCheckpoint(Stream stream, double ut);
        // The game time between two checkpoints in seconds.
        double CheckpointIntervalSeconds { get; }
    }

    // A field that accepts a precipitation test forcing. A forced run never writes side files.
    public interface ISurfaceForcing
    {
        // Lets it fall on a disc of that radius at that rate (mm of water per hour) of that type between the two times.
        void AddPrecipitationDisc(int bodyIndex, double latDeg, double lonDeg, double radiusKm, float rateMmPerH, PrecipType type, double fromUt, double toUt);
    }
}
