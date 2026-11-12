using System;
using System.Diagnostics;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Reads the inputs of the height maps from the weather field (IWeatherField, the one source of truth): the coverage, humidity and
    // surface-temperature maps at the maps' size, and the updraft of the cells sampled on a coarse grid at a height of the layer and
    // spread over the full size bilinearly. The updraft is the vertical component of the sample's wind WindWorld.z [VERIFY: the field
    // fills it with the cell's vertical wind at that altitude; a field that does not leaves the top term at zero].
    internal static class HeightFieldInputs
    {
        // All of it at once, for one altitude (the tests and the weather player; the game reads with HeightGather in slices or on a worker).
        internal static HeightInputs Read(IWeatherField field, int bodyIndex, double ut, int width, int height, double sampleAltitudeM,
                                          int updraftWidth = 64, int updraftHeight = 32)
        {
            var gather = new HeightGather(field, bodyIndex, ut, width, height, new[] { true }, new[] { sampleAltitudeM }, updraftWidth, updraftHeight);
            gather.Step(0.0);
            return gather.InputsOf(0);
        }
    }

    // The reading of the field for the layers of one build, in units small enough to spread over frames: the three maps once (they do not
    // depend on the altitude), then for each wanted layer the 64 x 32 updraft samples at its altitude, row by row, and their bilinear spread to
    // the map size. A field that says ThreadSafe can be read on the worker in one Step(0); any other is read on the main thread with a
    // budget. The values are those the one-shot reading would give.
    internal sealed class HeightGather
    {
        private readonly IWeatherField field;
        private readonly int bodyIndex, width, height, updraftWidth, updraftHeight;
        private readonly double ut;
        private readonly bool[] wanted;
        private readonly double[] altitudes;
        private readonly float[][] updrafts;
        private float[] coverage, humidity, temperature;
        private bool mapsDone;
        // The layer being read (the next wanted one at or after it), and where in it: 0 sampling, 1 spreading.
        private int layer = -1, phase;
        private WeatherMap coarse;
        private int row;
        private bool any;
        private float[] updraft;

        internal HeightGather(IWeatherField field, int bodyIndex, double ut, int width, int height, bool[] wanted, double[] altitudes,
                              int updraftWidth = 64, int updraftHeight = 32)
        {
            this.field = field;
            this.bodyIndex = bodyIndex;
            this.ut = ut;
            this.width = width;
            this.height = height;
            this.wanted = wanted;
            this.altitudes = altitudes;
            this.updraftWidth = updraftWidth;
            this.updraftHeight = updraftHeight;
            updrafts = new float[wanted.Length][];
            Done = field == null;
            if (!Done && !AnyWanted()) Done = true;
        }

        internal bool Done { get; private set; }

        private bool AnyWanted()
        {
            for (int i = 0; i < wanted.Length; i++)
                if (wanted[i]) return true;
            return false;
        }

        // Works until the reading is complete or the budget in ms is spent (checked between units); a budget of 0 or less runs to the end.
        // True when complete.
        internal bool Step(double budgetMs)
        {
            long start = Stopwatch.GetTimestamp();
            while (!Done)
            {
                if (budgetMs > 0.0 && (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency >= budgetMs) return false;
                Unit();
            }
            return true;
        }

        // The inputs of the layer at an index of the plan: the shared maps and its own updraft. Valid once Done.
        internal HeightInputs InputsOf(int index)
        {
            return new HeightInputs { Width = width, Height = height, Coverage = coverage, Humidity = humidity, TemperatureK = temperature, Updraft = updrafts[index] };
        }

        private void Unit()
        {
            if (!mapsDone)
            {
                coverage = Fetch(WeatherMapKind.Coverage);
                humidity = Fetch(WeatherMapKind.Humidity);
                temperature = Fetch(WeatherMapKind.SurfaceTemperature);
                mapsDone = true;
                return;
            }
            if (layer < 0 || (phase == 2))
            {
                if (!NextLayer())
                {
                    Done = true;
                    return;
                }
            }
            if (phase == 0)
            {
                SampleRow();
                return;
            }
            SpreadRows(8);
        }

        private bool NextLayer()
        {
            for (int i = layer + 1; i < wanted.Length; i++)
            {
                if (!wanted[i]) continue;
                layer = i;
                phase = 0;
                row = 0;
                any = false;
                updraft = null;
                coarse = new WeatherMap();
                coarse.Resize(updraftWidth, updraftHeight);
                return true;
            }
            layer = wanted.Length;
            return false;
        }

        private void SampleRow()
        {
            int y = row++;
            double lat = 90.0 - (y + 0.5) / updraftHeight * 180.0;
            for (int x = 0; x < updraftWidth; x++)
            {
                double lon = (x + 0.5) / updraftWidth * 360.0 - 180.0;
                WeatherSample s;
                if (field.Sample(bodyIndex, lat, lon, altitudes[layer], ut, out s) && !float.IsNaN(s.WindWorld.z))
                {
                    coarse.Values[y * updraftWidth + x] = s.WindWorld.z;
                    any = true;
                }
            }
            if (row < updraftHeight) return;
            if (any)
            {
                updraft = new float[width * height];
                phase = 1;
                row = 0;
            }
            else
            {
                updrafts[layer] = null;
                phase = 2;
            }
        }

        private void SpreadRows(int rows)
        {
            int end = Math.Min(height, row + rows);
            for (int y = row; y < end; y++)
            {
                double lat = 90.0 - (y + 0.5) / height * 180.0;
                for (int x = 0; x < width; x++)
                    updraft[y * width + x] = coarse.Sample(lat, (x + 0.5) / width * 360.0 - 180.0);
            }
            row = end;
            if (row < height) return;
            updrafts[layer] = updraft;
            updraft = null;
            coarse = null;
            phase = 2;
        }

        private float[] Fetch(WeatherMapKind kind)
        {
            var map = new WeatherMap();
            map.Resize(width, height);
            map.Kind = kind;
            return field.GetMap(bodyIndex, kind, ut, map) && map.Values.Length == width * height ? map.Values : null;
        }
    }
}
