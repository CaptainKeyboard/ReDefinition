using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Reads the inputs of the height maps from the weather field (IWeatherField, the one source of truth): the coverage, humidity and
    // surface-temperature maps at the maps' size, and the updraft of the cells sampled on a coarse grid at a height of the layer and
    // spread over the full size bilinearly. The updraft is the vertical component of the sample's wind WindWorld.z [VERIFY: the field
    // fills it with the cell's vertical wind at that altitude; a field that does not leaves the top term at zero].
    internal static class HeightFieldInputs
    {
        internal static HeightInputs Read(IWeatherField field, int bodyIndex, double ut, int width, int height, double sampleAltitudeM,
                                          int updraftWidth = 64, int updraftHeight = 32)
        {
            var input = new HeightInputs { Width = width, Height = height };
            input.Coverage = Fetch(field, bodyIndex, WeatherMapKind.Coverage, ut, width, height);
            input.Humidity = Fetch(field, bodyIndex, WeatherMapKind.Humidity, ut, width, height);
            input.TemperatureK = Fetch(field, bodyIndex, WeatherMapKind.SurfaceTemperature, ut, width, height);

            var coarse = new WeatherMap();
            coarse.Resize(updraftWidth, updraftHeight);
            bool any = false;
            for (int y = 0; y < updraftHeight; y++)
            {
                double lat = 90.0 - (y + 0.5) / updraftHeight * 180.0;
                for (int x = 0; x < updraftWidth; x++)
                {
                    double lon = (x + 0.5) / updraftWidth * 360.0 - 180.0;
                    WeatherSample s;
                    if (field.Sample(bodyIndex, lat, lon, sampleAltitudeM, ut, out s) && !float.IsNaN(s.WindWorld.z))
                    {
                        coarse.Values[y * updraftWidth + x] = s.WindWorld.z;
                        any = true;
                    }
                }
            }
            if (any)
            {
                input.Updraft = new float[width * height];
                for (int y = 0; y < height; y++)
                {
                    double lat = 90.0 - (y + 0.5) / height * 180.0;
                    for (int x = 0; x < width; x++)
                        input.Updraft[y * width + x] = coarse.Sample(lat, (x + 0.5) / width * 360.0 - 180.0);
                }
            }
            return input;
        }

        private static float[] Fetch(IWeatherField field, int bodyIndex, WeatherMapKind kind, double ut, int width, int height)
        {
            var map = new WeatherMap();
            map.Resize(width, height);
            map.Kind = kind;
            return field.GetMap(bodyIndex, kind, ut, map) && map.Values.Length == width * height ? map.Values : null;
        }
    }
}
