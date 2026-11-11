using System;

namespace AtmoSim.Core
{
    // Maps and the local 3D window of the generator, both by point evaluation (nothing is stored in the generator).
    public static class SubGrid
    {
        // An equirectangular map (row 0 north, column 0 at longitude -180, texel centres): one Sample per texel.
        // fields are filled when not null: coverage, precipitation rate (mm/h), level01, temperature, wind speed, cell strength.
        public static void Map(WeatherGenerator g, double ut, int nx, int ny, float[] coverage, float[] precipMmH, float[] level01, float[] tempK, float[] windMs, float[] cell)
        {
            for (int j = 0; j < ny; j++)
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / ny) * DMath.Pi / 180.0;
                for (int i = 0; i < nx; i++)
                {
                    double lon = (-180.0 + (i + 0.5) * 360.0 / nx) * DMath.Pi / 180.0;
                    Weather w;
                    g.Sample(lat, lon, 0.0, ut, out w);
                    int k = j * nx + i;
                    if (coverage != null) coverage[k] = (float)w.Coverage;
                    if (precipMmH != null) precipMmH[k] = (float)w.PrecipMmH;
                    if (level01 != null) level01[k] = (float)g.Level01(w.PrecipMmH);
                    if (tempK != null) tempK[k] = (float)w.TempK;
                    if (windMs != null) windMs[k] = (float)w.WindSpeed;
                    if (cell != null) cell[k] = (float)w.CellStrength;
                }
            }
        }

        // The local 3D window: nx x ny x nz cloud density points around a centre; spacing in metres; z from altitude zBase.
        public static void Window(WeatherGenerator g, double centreLat, double centreLon, double ut, int nx, int ny, int nz, double dxyM, double dzM, double zBaseM, float[] density)
        {
            double a = g.Body.RadiusM;
            double cosC = DMath.Cos(centreLat);
            // one point evaluation per column at the base altitude; the vertical profile of the column gives the levels
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    double east = (i - 0.5 * (nx - 1)) * dxyM, north = (j - 0.5 * (ny - 1)) * dxyM;
                    double lat = centreLat + north / a;
                    double lon = centreLon + east / (a * cosC);
                    Weather w;
                    g.Sample(lat, lon, zBaseM, ut, out w);
                    for (int k = 0; k < nz; k++) density[(k * ny + j) * nx + i] = (float)w.DensityAt(zBaseM + k * dzM);
                }
        }
    }
}
