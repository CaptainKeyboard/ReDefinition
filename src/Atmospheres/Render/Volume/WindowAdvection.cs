using System;
using AtmoSim.Core;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The wind and the vertical motion of the local 3D window (package E43). The window the field delivers (WeatherWindow: cloud water, updraft, temperature
    // on a coarse grid) is a pure function of the clock, so a step "advected with the wind of its height" is a different lookup of the same pure function, not a
    // stored state that could drift away from the field:
    //   - the stratiform deck of every column is looked up where the wind of its height has carried it (the layer turns rigidly against the field's patterns by
    //     HeightWind.LayerRate x UT, so layers of different heights slide against each other and never shear into stripes);
    //     (the layers that carry rain and cells, below 0.2 of the tropopause, move with the patterns and are not offset: HeightWind.LayerDecoupleWeight);
    //   - towers grow in the first quarter of the life of a cell and sink at its end: the top rises with the updraft, the window's updraft is that rise;
    //   - a tower leans downwind with the shear aloft (bounded by half a cell radius): its body is the weighted sum of the cells found at the point, half-way and fully
    //     upwind, so it is one connected body and a different strongest cell fades in and out; the anvil spreads at the top of the fully shifted lookup;
    //   - the consistency rule: beneath a tall cell the deck is thinned, the anvil lies above it; a column never carries an updraft through an unthinned deck.
    // No feedback into the field. The cost of a refill is 1024 columns of at most three samples each. Allocation free.
    internal static class WindowAdvection
    {
        // Refills the rows firstRow .. firstRow + rowCount - 1 (north) of the window's cloud water and updraft with layered columns (GeneratorField.FillWindow
        // calls it for the whole window). The window's temperature is the field's.
        internal static void Refill(WeatherWindow w, int firstRow, int rowCount, WeatherGenerator g, LayerOptions o)
        {
            double a = g.Body.RadiusM;
            double centreLat = Math.Max(-89.9, Math.Min(89.9, w.CentreLatDeg)) * (Math.PI / 180.0);
            double centreLon = w.CentreLonDeg * (Math.PI / 180.0);
            double cosC = Math.Max(0.02, DMath.Cos(centreLat));
            double dxy = w.SpacingHorizontalM, dz = w.SpacingVerticalM;
            int nx = w.Nx, ny = w.Ny, nz = w.Nz, stride = nx * ny;
            int last = Math.Min(ny, firstRow + rowCount);
            for (int j = Math.Max(0, firstRow); j < last; j++)
            {
                double north = (j - 0.5 * (ny - 1)) * dxy;
                double lat = Math.Max(-1.5707, Math.Min(1.5707, centreLat + north / a));
                for (int i = 0; i < nx; i++)
                {
                    double east = (i - 0.5 * (nx - 1)) * dxy;
                    double lon = DMath.WrapPi(centreLon + east / (a * cosC));
                    int i0 = j * nx + i;
                    LayerCoupling.Column(g, lat, lon, w.Ut, w.OriginAltM, dz, nz, w.CloudWater, i0, stride, w.Updraft, o);
                    for (int k = 0; k < nz; k++)
                    {
                        int index = i0 + k * stride;
                        w.CloudWater[index] *= GeneratorField.WindowGramsPerDensity;
                        if (w.Updraft[index] < 0f) w.Updraft[index] = 0f;
                    }
                }
            }
        }
    }
}
