using System;
using System.Threading;

namespace ReDefinition.Core
{
    // A field that has no weather: every sample is clear, every map is zero. The hub holds it until the atmospheres assign a field.
    public sealed class NullField : IWeatherField
    {
        // The one instance.
        public static readonly NullField Instance = new NullField();

        private NullField()
        {
        }

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            sample = WeatherSample.Clear(bodyIndex);
            return false;
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
        {
            if (target == null) return false;
            if (target.Width < 1 || target.Height < 1) target.Resize(1, 1);
            Array.Clear(target.Values, 0, target.Values.Length);
            target.Kind = kind;
            target.BodyIndex = bodyIndex;
            target.Ut = ut;
            target.Version = 0;
            target.Readiness = FieldReadiness.Ready;
            target.Approximate = false;
            return false;
        }

        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target)
        {
            return false;
        }

        public void Advance(double budgetMs)
        {
        }

        public long Version { get { return 0; } }
        public long DefinitionId { get { return 0; } }
        public bool ThreadSafe { get { return true; } }
        public double StepSeconds { get { return 0.0; } }
        public FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
    }

    // The point query of the weather: the field plus the providers other mods register, merged. The highest priority owns the wind
    // (the field counts as priority 0); the level is the largest; visibility and the sun's transmission multiply (the optical depths
    // add). A read allocates nothing. Calls from a worker thread are allowed when the field and every source say ThreadSafe.
    public static class WeatherHub
    {
        private static readonly object Gate = new object();
        private static IWeatherSource[] sources = new IWeatherSource[0];
        private static IWeatherField field = NullField.Instance;
        private static long changes;

        // The field that is the base of every sample. Assigning null puts the NullField back.
        public static IWeatherField Field
        {
            get { return Volatile.Read(ref field); }
            set
            {
                Volatile.Write(ref field, value ?? NullField.Instance);
                Interlocked.Increment(ref changes);
            }
        }

        // The number of registered providers (not counting the field).
        public static int SourceCount
        {
            get { return Volatile.Read(ref sources).Length; }
        }

        // Changes whenever the output of Sample may have changed: the field's Version, a new field, a provider added or removed. A
        // provider's own changes are not seen here.
        public static long Version
        {
            get { return unchecked(Field.Version + (Interlocked.Read(ref changes) << 40)); }
        }

        // Adds a provider; a provider already there is not added twice.
        public static void Register(IWeatherSource source)
        {
            if (source == null) throw new ArgumentNullException("source");
            lock (Gate)
            {
                IWeatherSource[] old = sources;
                if (Array.IndexOf(old, source) >= 0) return;
                var next = new IWeatherSource[old.Length + 1];
                int at = 0;
                // Highest priority first; equal priorities keep the order of registration.
                bool placed = false;
                for (int i = 0; i < old.Length; i++)
                {
                    if (!placed && source.Priority > old[i].Priority)
                    {
                        next[at++] = source;
                        placed = true;
                    }
                    next[at++] = old[i];
                }
                if (!placed) next[at] = source;
                Volatile.Write(ref sources, next);
                Interlocked.Increment(ref changes);
            }
        }

        // Removes a provider; true where it was registered.
        public static bool Unregister(IWeatherSource source)
        {
            lock (Gate)
            {
                IWeatherSource[] old = sources;
                int index = Array.IndexOf(old, source);
                if (index < 0) return false;
                var next = new IWeatherSource[old.Length - 1];
                Array.Copy(old, 0, next, 0, index);
                Array.Copy(old, index + 1, next, index, old.Length - index - 1);
                Volatile.Write(ref sources, next);
                Interlocked.Increment(ref changes);
                return true;
            }
        }

        // The merged weather at a place and time. False where neither the field nor a provider knew the place; the sample is then clear.
        public static bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            IWeatherSource[] list = Volatile.Read(ref sources);
            WeatherSample merged = WeatherSample.Clear(bodyIndex);
            bool any = false;
            bool haveOwner = false;
            int ownerPriority = int.MinValue;
            float visibilityFactor = 1f, clearSky = 1f;
            float bestLevel = -1f;
            bool haveTemperature = false;
            int temperaturePriority = int.MinValue;

            WeatherSample s;
            if (Field.Sample(bodyIndex, latDeg, lonDeg, altM, ut, out s))
            {
                any = true;
                Merge(ref merged, ref s, 0, ref haveOwner, ref ownerPriority, ref visibilityFactor, ref clearSky, ref bestLevel,
                    ref haveTemperature, ref temperaturePriority);
            }
            for (int i = 0; i < list.Length; i++)
            {
                IWeatherSource source = list[i];
                if (!source.TrySample(bodyIndex, latDeg, lonDeg, altM, ut, out s)) continue;
                any = true;
                Merge(ref merged, ref s, source.Priority, ref haveOwner, ref ownerPriority, ref visibilityFactor, ref clearSky, ref bestLevel,
                    ref haveTemperature, ref temperaturePriority);
            }
            if (any)
            {
                merged.VisibilityM = WeatherSample.ClearVisibilityM * visibilityFactor;
                merged.CloudCover01 = 1f - clearSky;
            }
            sample = merged;
            return any;
        }

        private static void Merge(ref WeatherSample merged, ref WeatherSample s, int priority, ref bool haveOwner, ref int ownerPriority,
            ref float visibilityFactor, ref float clearSky, ref float bestLevel, ref bool haveTemperature, ref int temperaturePriority)
        {
            if (!haveOwner || priority > ownerPriority)
            {
                haveOwner = true;
                ownerPriority = priority;
                merged.WindWorld = s.WindWorld;
                merged.GustFactor = s.GustFactor;
            }
            if (s.Level01 > bestLevel)
            {
                bestLevel = s.Level01;
                merged.Level01 = s.Level01;
                merged.Type = s.Type;
                merged.SnowFraction = s.SnowFraction;
                merged.SleetFraction = s.SleetFraction;
                merged.RainFraction = s.RainFraction;
            }
            float factor = s.VisibilityM / WeatherSample.ClearVisibilityM;
            visibilityFactor *= factor < 0f ? 0f : factor > 1f ? 1f : factor;
            float cover = s.CloudCover01 < 0f ? 0f : s.CloudCover01 > 1f ? 1f : s.CloudCover01;
            clearSky *= 1f - cover;
            if (s.OpticalDepthVertical > 0f) merged.OpticalDepthVertical += s.OpticalDepthVertical;
            if (!float.IsNaN(s.TemperatureK) && (!haveTemperature || priority > temperaturePriority))
            {
                haveTemperature = true;
                temperaturePriority = priority;
                merged.TemperatureK = s.TemperatureK;
            }
            if (s.Readiness > merged.Readiness) merged.Readiness = s.Readiness;
            if (s.Approximate) merged.Approximate = true;
        }
    }
}
