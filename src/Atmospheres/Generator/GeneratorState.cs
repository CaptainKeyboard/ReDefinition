using System;
using System.Reflection;
using System.Threading;
using AtmoSim.Core;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Everything the weather generator needs for ONE body, built once and then never changed: the parameter set, the generator made from
    // it (a pure function of position, altitude and time), the seed, the precipitation bands and the identity of all of it. The only
    // mutable members are the flag that a guard tripped (the body then answers with its climatology) and the bands with the player's
    // knobs applied, which are replaced as a whole.
    internal sealed class GeneratorState
    {
        // Rises when the algorithm's meaning changes in a way that its own output does not show (the output itself is part of the identity).
        internal const int AlgorithmVersion = 1;
        // The points at which the generator's own output enters the identity: an algorithm that gives other numbers is another definition.
        private const int FingerprintPoints = 16;

        private readonly float[] baseBands;
        private float[] bands;
        private int broken;

        private GeneratorState(int bodyIndex, string name, BodyParams parameters, ulong seed, float[] baseBands, PrecipType dustType)
        {
            BodyIndex = bodyIndex;
            Name = name ?? "";
            Parameters = parameters;
            Seed = seed;
            Generator = new WeatherGenerator(parameters, seed);
            this.baseBands = baseBands;
            bands = baseBands;
            DustType = dustType;
            DefinitionId = ComputeIdentity();
        }

        internal int BodyIndex { get; }
        internal string Name { get; }
        internal BodyParams Parameters { get; }
        internal WeatherGenerator Generator { get; }
        internal ulong Seed { get; }
        // The type that a dust flag of the generator means on this body (Dust or Sand).
        internal PrecipType DustType { get; }
        // A hash of the algorithm version, the seed, every input of the parameter set, the terrain and the output of the generator at fixed points.
        internal long DefinitionId { get; }

        // The five band rates in mm of water per hour that the body's levels use: the knobs of the player applied (PrecipRateToLevel).
        internal float[] Bands
        {
            get { return Volatile.Read(ref bands); }
        }

        internal float[] BaseBands
        {
            get { return baseBands; }
        }

        // Whether a guard tripped: the body answers with its climatology only.
        internal bool Broken
        {
            get { return Volatile.Read(ref broken) != 0; }
        }

        // Sets the flag; true for the call that set it (the one that logs).
        internal bool Trip()
        {
            return Interlocked.Exchange(ref broken, 1) == 0;
        }

        // Applies the player's knobs to the bands.
        internal void ApplyKnobs(float lightWeatherFrequency, float heavyShare)
        {
            var shifted = new float[baseBands.Length];
            PrecipRateToLevel.Shift(baseBands, lightWeatherFrequency, heavyShare, shifted);
            Volatile.Write(ref bands, shifted);
        }

        // The state of a body, or null with the reason in error where the parameter set cannot make a generator (a guard before the first
        // sample: an extreme parameter set freezes the body instead of failing the game).
        internal static GeneratorState Create(int bodyIndex, string name, BodyParams parameters, ulong seed, float[] statedBands, PrecipType dustType, out string error)
        {
            error = null;
            try
            {
                float[] baseBands;
                if (statedBands != null && statedBands.Length >= 5) baseBands = (float[])statedBands.Clone();
                else
                {
                    double[] derived = new BodyDerived(parameters).BandsMmH;
                    baseBands = new float[derived.Length];
                    for (int i = 0; i < derived.Length; i++) baseBands[i] = (float)derived[i];
                }
                var state = new GeneratorState(bodyIndex, name, parameters, seed, baseBands, dustType);
                string bad = state.Check();
                if (bad != null)
                {
                    error = bad;
                    return null;
                }
                return state;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        // The numbers the generator derives must be finite and positive where they are used as lengths and speeds.
        private string Check()
        {
            BodyDerived d = Generator.D;
            if (!IsGood(d.A) || !IsGood(d.SystemRadiusM) || !IsGood(d.CellLatticeM) || !IsGood(Parameters.YearS) || !IsGood(Parameters.RotationPeriodS)) return "a derived length is not positive and finite";
            if (double.IsNaN(d.JetSpeed0) || double.IsInfinity(d.JetSpeed0) || double.IsNaN(d.TempNorm) || double.IsInfinity(d.TempNorm)) return "a derived speed or temperature is not finite";
            Weather w;
            Generator.Sample(0.5, 0.5, 0.0, 1.0e6, out w);
            if (!Finite(ref w)) return "the generator gives a value that is not finite";
            return null;
        }

        private static bool IsGood(double v)
        {
            return v > 0 && !double.IsInfinity(v);
        }

        // Whether the values the field uses are finite.
        internal static bool Finite(ref Weather w)
        {
            return IsFinite(w.TempK) && IsFinite(w.SurfaceTempK) && IsFinite(w.Coverage) && IsFinite(w.PrecipMmH) && IsFinite(w.WindEast) && IsFinite(w.WindNorth)
                && IsFinite(w.GustFactor) && IsFinite(w.CloudWater) && IsFinite(w.Humidity) && IsFinite(w.CellStrength);
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private long ComputeIdentity()
        {
            ulong h = 0xCBF29CE484222325UL;
            h = Hash.Mix(h ^ AlgorithmVersion);
            h = Hash.Mix(h ^ Seed);
            // Every public field of the parameter set, in the order of its name, so that an added lever enters the identity by itself.
            FieldInfo[] fields = typeof(BodyParams).GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (FieldInfo field in fields)
            {
                object value = field.GetValue(Parameters);
                if (value is double) h = Hash.Fold(h, (double)value);
                else if (value is bool) h = Hash.Mix(h ^ ((bool)value ? 3UL : 2UL));
                else if (value is int) h = Hash.Mix(h ^ (ulong)(long)(int)value);
                else if (value is double[]) foreach (double v in (double[])value) h = Hash.Fold(h, v);
                else if (value == null) h = Hash.Mix(h ^ 5UL);
            }
            HeightGrid grid = Generator.Grid;
            h = Hash.Mix(h ^ (ulong)(grid.NLon * 100000L + grid.NLat));
            for (int i = 0; i < grid.Ocean.Length; i++)
            {
                h = Hash.Fold(h, grid.Ocean[i]);
                h = Hash.Fold(h, grid.TerrainM[i]);
            }
            // The generator's own output at fixed points: another algorithm is another definition.
            var probe = new Rng(Hash.Mix(Seed ^ 0x6A09E667F3BCC908UL));
            Weather w;
            for (int i = 0; i < FingerprintPoints; i++)
            {
                double lat = DMath.Asin(2.0 * probe.Uniform() - 1.0), lon = (2.0 * probe.Uniform() - 1.0) * Math.PI;
                Generator.Sample(lat, lon, 500.0 * i, 2.0e6 + 1.1e6 * i, out w);
                h = WeatherGenerator.HashOf(ref w, h);
            }
            return unchecked((long)h);
        }
    }
}
