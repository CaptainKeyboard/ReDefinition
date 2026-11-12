using System;

namespace ReDefinition.Atmospheres
{
    // The cloud shadow on the CPU (package T04): the optical depth of the own layers along any ray, the sun's transmission at a place
    // (the same number for the ground, a vessel and a kerbal, since everything lit by the sun takes the mask), and the vertical optical
    // depth over a place. It reads the field the layer shader draws, the maps and the type table of ICloudLayerSource, without the
    // shader's noise and erosion (the noise package's detail is smaller than the penumbra), so the shadow and the picture agree on where
    // a cloud is and how thick. Pure (no Unity): the tests and the weather player link it.
    //
    // The shadow shader walks the same spans with the same midpoint rule (steps per span in ShadowFrame.StepsPerSpan).
    internal sealed class CloudShadowField
    {
        private readonly ICloudLayerSource source;
        private readonly LayerTypeTable[] tables;

        // The steps of the midpoint rule in each span of a layer, and the samples across the sun's disc.
        internal int StepsPerSpan = 16;
        internal int DiscSamples = 1;

        internal CloudShadowField(ICloudLayerSource source, LayerTypeTable[] tables)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (tables == null || tables.Length < source.LayerCount) throw new ArgumentException("One table per layer.", "tables");
            this.source = source;
            this.tables = tables;
        }

        internal ICloudLayerSource Source { get { return source; } }

        // The optical depth along the ray o + t d for t from 0 to tMax, all layers, each scaled by its ShadowStrength. No distance fade:
        // the fade belongs to the camera's picture, not to the sun.
        internal double PathOpticalDepth(double ox, double oy, double oz, double dx, double dy, double dz, double tMax, bool withStrength = true)
        {
            double tau = 0.0;
            for (int i = 0; i < source.LayerCount; i++)
            {
                CloudLayerDef layer = source.LayerAt(i);
                if (withStrength && layer.ShadowStrength <= 0f) continue;
                tau += LayerOpticalDepth(i, ox, oy, oz, dx, dy, dz, tMax) * (withStrength ? layer.ShadowStrength : 1f);
            }
            return tau;
        }

        internal double LayerOpticalDepth(int layerIndex, double ox, double oy, double oz, double dx, double dy, double dz, double tMax)
        {
            CloudLayerDef layer = source.LayerAt(layerIndex);
            float tightInner, tightOuter;
            layer.TightAltitudes(out tightInner, out tightOuter);
            LayerSpan a, b;
            int count = LayerGeometry.ShellSpans(ox, oy, oz, dx, dy, dz, source.BodyRadiusM, tightInner, tightOuter, tMax, out a, out b);
            double tau = 0.0;
            LayerTypeTable table = tables[layerIndex];
            for (int s = 0; s < count; s++)
            {
                LayerSpan span = s == 0 ? a : b;
                double dt = span.Length / StepsPerSpan;
                for (int k = 0; k < StepsPerSpan; k++)
                {
                    double t = span.T0 + dt * (k + 0.5);
                    double x = ox + dx * t, y = oy + dy * t, z = oz + dz * t;
                    double lat, lon;
                    LayerGeometry.LatLon(x, y, z, out lat, out lon);
                    double altitude = LayerGeometry.AltitudeOf(x, y, z, source.BodyRadiusM);
                    tau += HeightDensity.Extinction(source, table, layerIndex, altitude, lat, lon) * dt;
                }
            }
            return tau;
        }

        // The distance along the ray at which it meets the body's sea-level sphere (a sun below the horizon of the receiver), else
        // double.MaxValue: the sun's ray is cut there because no cloud beyond the planet lights anything.
        private double SunRayEnd(double ox, double oy, double oz, double dx, double dy, double dz)
        {
            if (!source.HasSurface) return double.MaxValue;
            double t0, t1;
            if (LayerGeometry.HitSphere(ox, oy, oz, dx, dy, dz, source.BodyRadiusM, out t0, out t1) && t0 > 0.0) return t0;
            return double.MaxValue;
        }

        // The optical depth along the sun ray from a receiver at (ox, oy, oz) (planet-centred, map frame), the sun towards (sx, sy, sz).
        internal double SunPathOpticalDepth(double ox, double oy, double oz, double sx, double sy, double sz)
        {
            return PathOpticalDepth(ox, oy, oz, sx, sy, sz, SunRayEnd(ox, oy, oz, sx, sy, sz));
        }

        // The share of the sun's direct light that reaches the receiver: the mean of exp(-tau) over DiscSamples directions across the sun's
        // disc (one sample: the centre). rotation turns the pattern (a per-pixel angle in the shader).
        internal double DirectTransmission(double ox, double oy, double oz, double sx, double sy, double sz, double rotation = 0.0)
        {
            int n = Math.Max(1, DiscSamples);
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dx, dy, dz;
                CloudShadowMath.DiscDirection(sx, sy, sz, i, n, rotation, out dx, out dy, out dz);
                sum += CloudShadowMath.DirectTransmission(PathOpticalDepth(ox, oy, oz, dx, dy, dz, SunRayEnd(ox, oy, oz, dx, dy, dz)));
            }
            return sum / n;
        }

        // The vertical optical depth over a place: the clouds above the altitude (metres above sea level) on the local vertical. This is what
        // WeatherHooks.CloudOpticalDepth means.
        internal double VerticalOpticalDepth(double latDeg, double lonDeg, double altitudeM)
        {
            double la = latDeg * Math.PI / 180.0, lo = lonDeg * Math.PI / 180.0;
            double ux = Math.Cos(la) * Math.Cos(lo), uy = Math.Cos(la) * Math.Sin(lo), uz = Math.Sin(la);
            double r = source.BodyRadiusM + altitudeM;
            // The physical optical depth: the layers' shadow strength is a look setting of the shadow and does not scale it.
            return PathOpticalDepth(ux * r, uy * r, uz * r, ux, uy, uz, double.MaxValue, false);
        }
    }

    // The single definition behind WeatherHooks.CloudOpticalDepth for the own cloud system (cloudSource = Ours): the vertical optical depth
    // over a place of the body whose layers it was given. SunTransmission.From (Common) stays the one definition of how a vertical optical
    // depth becomes a transmission; SunTransmissionAt below is the exact value along the sun ray, which equals it for a plane-parallel
    // cloud and replaces its air-mass estimate where the cloud is not plane-parallel (a sun near the horizon, a cloud edge).
    internal static class OwnCloudOpticalDepth
    {
        private static CloudShadowField field;
        private static int bodyIndex = -1;
        private static readonly Func<double, double, double, double, float> reader = Read;

        // The field the hook reads, for the body the camera is at (T09 sets it with the layer stack and clears it when the system is off).
        internal static void Bind(CloudShadowField f)
        {
            field = f;
            bodyIndex = f == null ? -1 : f.Source.BodyIndex;
        }

        internal static bool Bound { get { return field != null; } }
        internal static int BoundBody { get { return bodyIndex; } }

        internal static Func<double, double, double, double, float> Reader { get { return reader; } }

        // latitude and longitude in degrees, altitude in metres above sea level, ut unused (the layers' maps are the current state).
        internal static float Read(double latDeg, double lonDeg, double altitudeM, double ut)
        {
            CloudShadowField f = field;
            if (f == null) return 0f;
            double tau = f.VerticalOpticalDepth(latDeg, lonDeg, altitudeM);
            return tau > 0.0 ? (float)tau : 0f;
        }

        // The transmission of the sun's direct light along the sun ray at a place, exact for the own layers.
        internal static float SunTransmissionAt(double latDeg, double lonDeg, double altitudeM, double sunX, double sunY, double sunZ)
        {
            CloudShadowField f = field;
            if (f == null) return 1f;
            double la = latDeg * Math.PI / 180.0, lo = lonDeg * Math.PI / 180.0;
            double r = f.Source.BodyRadiusM + altitudeM;
            return (float)f.DirectTransmission(Math.Cos(la) * Math.Cos(lo) * r, Math.Cos(la) * Math.Sin(lo) * r, Math.Sin(la) * r, sunX, sunY, sunZ);
        }
    }
}
