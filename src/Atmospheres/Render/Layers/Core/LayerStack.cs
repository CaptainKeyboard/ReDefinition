using System;

namespace ReDefinition.Atmospheres
{
    // The stack of layers along one ray, pure: the spans of all layers sorted front to back, the transmittance product and the CPU
    // march that the cloud shadows, the light in the medium and the raytracing pack use to read the same field the shader draws
    // (P20 stacked layers, P25 inside, above, below). The shader marches the same spans in the same order.
    internal static class LayerStack
    {
        // Sorts spans[0..count) by their start, nearest first (a stable insertion sort: at most 2 spans per layer, 8 in all).
        internal static void SortByStart(LayerSpan[] spans, int count)
        {
            for (int i = 1; i < count; i++)
            {
                LayerSpan key = spans[i];
                int j = i - 1;
                while (j >= 0 && spans[j].T0 > key.T0)
                {
                    spans[j + 1] = spans[j];
                    j--;
                }
                spans[j + 1] = key;
            }
        }

        // The alpha of two stacked layers: 1 - (1 - a1)(1 - a2).
        internal static double CombinedAlpha(double a1, double a2)
        {
            return 1.0 - (1.0 - a1) * (1.0 - a2);
        }

        // Collects the spans of every layer along a ray and sorts them. spans must hold 2 per layer. Returns the count.
        internal static int Collect(ICloudLayerSource source, double ox, double oy, double oz, double dx, double dy, double dz, double tMax,
                                    LayerSpan[] spans)
        {
            int n = 0;
            for (int i = 0; i < source.LayerCount; i++)
            {
                CloudLayerDef layer = source.LayerAt(i);
                float tightInner, tightOuter;
                layer.TightAltitudes(out tightInner, out tightOuter);
                LayerSpan a, b;
                int c = LayerGeometry.ShellSpans(ox, oy, oz, dx, dy, dz, source.BodyRadiusM, tightInner, tightOuter, tMax, out a, out b);
                if (c > 0)
                {
                    a.Layer = i;
                    spans[n++] = a;
                }
                if (c > 1)
                {
                    b.Layer = i;
                    spans[n++] = b;
                }
            }
            SortByStart(spans, n);
            return n;
        }

        // The optical depth of one layer along the ray up to tMax, by the midpoint rule with stepsPerSpan steps in each span. The
        // shader's density without noise (the field the maps and the table give). Tables holds one LayerTypeTable per layer.
        internal static double OpticalDepth(ICloudLayerSource source, LayerTypeTable[] tables, int layerIndex, double ox, double oy, double oz,
                                            double dx, double dy, double dz, double tMax, int stepsPerSpan)
        {
            CloudLayerDef layer = source.LayerAt(layerIndex);
            float tightInner, tightOuter;
            layer.TightAltitudes(out tightInner, out tightOuter);
            LayerSpan a, b;
            int count = LayerGeometry.ShellSpans(ox, oy, oz, dx, dy, dz, source.BodyRadiusM, tightInner, tightOuter, tMax, out a, out b);
            double tau = 0.0;
            for (int s = 0; s < count; s++)
            {
                LayerSpan span = s == 0 ? a : b;
                double dt = span.Length / stepsPerSpan;
                for (int i = 0; i < stepsPerSpan; i++)
                {
                    double t = span.T0 + dt * (i + 0.5);
                    double x = ox + dx * t, y = oy + dy * t, z = oz + dz * t;
                    double lat, lon;
                    LayerGeometry.LatLon(x, y, z, out lat, out lon);
                    double altitude = LayerGeometry.AltitudeOf(x, y, z, source.BodyRadiusM);
                    tau += HeightDensity.Extinction(source, tables[layerIndex], layerIndex, altitude, lat, lon) * LayerDensity.DistanceFade(layer, t) * dt;
                }
            }
            return tau;
        }

        // The alpha of the whole stack along a ray: 1 - exp(-sum of the layers' optical depths) (the transmittances multiply).
        internal static double StackAlpha(ICloudLayerSource source, LayerTypeTable[] tables, double ox, double oy, double oz, double dx, double dy,
                                          double dz, double tMax, int stepsPerSpan)
        {
            double tau = 0.0;
            for (int i = 0; i < source.LayerCount; i++)
                tau += OpticalDepth(source, tables, i, ox, oy, oz, dx, dy, dz, tMax, stepsPerSpan);
            return 1.0 - Math.Exp(-tau);
        }
    }
}
