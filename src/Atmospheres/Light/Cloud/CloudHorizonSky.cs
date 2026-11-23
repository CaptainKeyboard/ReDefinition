using System;

namespace ReDefinition.Atmospheres
{
    // The colour a far cloud fades into: the radiance of the air along the cloud's own view ray, worked out from the body's air (E40 H1 aerial perspective).
    //
    // A cloud at the end of the air shows what the air in front of it in-scatters, which is what the sky shows along that ray. The earlier law gave it by a power law of the
    // Rayleigh coefficients and a body gain fitted to one sky; this integrates the single scattering along a horizontal ray of the camera through the exponential air (Rayleigh per
    // channel, Mie with Scatterer's phase function, the sun's transmittance with the planet's shadow) and adds the higher orders as Hillaire's isotropic closure (EGSR 2020, section 5.5:
    // the second order's mean radiance over the sphere divided by one minus the mean transfer, as a source proportional to the scattering). Nothing is fitted: the inputs are the
    // body's air (Scatterer's atmosphere node: m_betaR, BETA_MSca, HR, HM, m_mieG, averageGroundReflectance; the body's data otherwise), the camera's height, the sun's elevation and the
    // ray's azimuth from the sun.
    //
    // The result is radiance per unit of the sun's irradiance (1/sr). The sky Scatterer draws shows it through its own display law (skyExposure, the sun intensity 100 and the curve of
    // its hdr function), which Display reproduces; the pass then takes the colour as a display value (AerialPerspectivePlan.Inscatter decodes it through the output stage), so that a cloud that has
    // faded completely is the colour of the sky next to it.
    //
    // Near: the same medium's colour over an infinitesimal path (the local source over the extinction per channel), which the layer shader blends into Far by the blue extinction.
    internal static class CloudHorizonSky
    {
        internal struct Rgb
        {
            public double R, G, B;

            public Rgb(double r, double g, double b)
            {
                R = r; G = g; B = b;
            }

            public double this[int i]
            {
                get { return i == 0 ? R : i == 1 ? G : B; }
                set { if (i == 0) R = value; else if (i == 1) G = value; else B = value; }
            }
        }

        // The air in the units of the model: per metre, metres.
        internal struct Medium
        {
            public Rgb Rayleigh;                // scattering = extinction, per m, at sea level
            public double MieScattering, MieExtinction;
            public double RayleighHeightM, MieHeightM, MieG, GroundAlbedo, PlanetRadiusM, TopRadiusM;
            public bool Valid;
        }

        // The result for one ray: the colour at the end of the air and the colour of a short path, both radiance per unit of the sun's irradiance.
        internal struct Horizon
        {
            public Rgb Far, Near;
        }

        // Scatterer's display law of the sky (CommonAtmosphere.cginc: _Sun_Intensity 100 and hdr(L, exposure)); the sky sphere's colour is the table's radiance times its sun colour (1) times these.
        internal const double SunIntensity = 100.0, HdrScale = 0.38317, HdrKnee = 1.413;
        // Scatterer's default and the stock configs' sky exposure (configPoints.skyExposure), used for an air that has none.
        internal const double DefaultSkyExposure = 0.2;
        // The Mie phase function's default asymmetry and the albedo of the ground when the air gives none (Hillaire's Mie g 0.8; Scatterer's averageGroundReflectance of the stock configs).
        internal const double DefaultMieG = 0.8, DefaultGroundAlbedo = 0.1;
        // The samples of the view ray (spaced by the 2.2 power of the fraction), the anchors the higher orders are worked out at (every third sample), the directions of the sphere and the steps of
        // each of their rays, and the steps of a sun ray.
        private const int ViewSamples = 24, AnchorStride = 3, SphereDirections = 16, SphereSteps = 10, SunSteps = 10;
        private const double ViewSpacing = 2.2, MinimumSceneBlue = 0.02;

        // The medium of an air and the radius of its planet. The top is where Scatterer cuts the air off (AtmoPreprocessor.CalculateRt: the density has fallen to 1e-7 per km of the coefficient).
        internal static Medium MediumOf(CloudBridgeAir.Air air, double planetRadiusM)
        {
            var m = new Medium();
            if (!air.Valid || !(planetRadiusM > 0.0)) return m;
            m.Rayleigh = new Rgb(air.RayleighPerM.x, air.RayleighPerM.y, air.RayleighPerM.z);
            m.MieExtinction = air.MiePerM;
            m.MieScattering = air.MiePerM * CloudBridgeAir.ScattererMieExtinctionDivisor;
            m.RayleighHeightM = air.RayleighHeightM;
            m.MieHeightM = air.MieHeightM;
            m.MieG = air.HasSky ? Math.Max(0.0, Math.Min(air.MieG, 0.99)) : DefaultMieG;
            m.GroundAlbedo = air.HasSky ? Math.Max(0.0, Math.Min(air.GroundAlbedo, 1.0)) : DefaultGroundAlbedo;
            m.PlanetRadiusM = planetRadiusM;
            double rayleighPerKm = 1000.0 * Math.Sqrt(air.RayleighPerM.x * air.RayleighPerM.x + air.RayleighPerM.y * air.RayleighPerM.y + air.RayleighPerM.z * air.RayleighPerM.z);
            double miePerKm = 1000.0 * air.MiePerM * CloudBridgeAir.ScattererMieExtinctionDivisor * Math.Sqrt(3.0);
            double top = Math.Max(-air.RayleighHeightM * Math.Log(1e-7 / Math.Max(rayleighPerKm, 1e-12)), -air.MieHeightM * Math.Log(1e-7 / Math.Max(miePerKm, 1e-12)));
            m.TopRadiusM = planetRadiusM + Math.Max(top, 8.0 * Math.Max(air.RayleighHeightM, air.MieHeightM) * 0.5);
            m.Valid = m.MieScattering > 0.0 && m.RayleighHeightM > 0.0 && m.MieHeightM > 0.0 && m.TopRadiusM > planetRadiusM;
            return m;
        }

        // The elevation (radians from the camera's horizontal) of the ray along which far cloud of a layer at layerAltitudeM stands: the layer's own sphere seen from the camera. A camera at
        // or below the layer sees its far end on the horizontal; a camera above it sees the far end below the horizontal by the angle of the tangent to that sphere.
        internal static double LayerElevation(double cameraHeightM, double layerAltitudeM, double planetRadiusM)
        {
            if (cameraHeightM <= layerAltitudeM) return 0.0;
            double ratio = (planetRadiusM + layerAltitudeM) / (planetRadiusM + cameraHeightM);
            return -Math.Acos(Math.Max(0.0, Math.Min(1.0, ratio)));
        }

        // The two angles the model takes for a light: its elevation above the camera's horizon and the azimuth of the camera's view from it (0 looking at it, pi with it behind the camera).
        // A view straight up or down has no azimuth; it is taken as the side view (pi / 2), the mean of the phase.
        internal static void ViewAngles(UnityEngine.Vector3 up, UnityEngine.Vector3 forward, UnityEngine.Vector3 toLight, out double elevation, out double azimuth)
        {
            up = up.normalized; toLight = toLight.normalized;
            elevation = Math.Asin(Math.Max(-1.0, Math.Min(1.0, UnityEngine.Vector3.Dot(up, toLight))));
            UnityEngine.Vector3 f = forward - up * UnityEngine.Vector3.Dot(forward, up), l = toLight - up * UnityEngine.Vector3.Dot(toLight, up);
            if (f.sqrMagnitude < 1e-6f) { azimuth = 0.5 * Math.PI; return; }
            if (l.sqrMagnitude < 1e-8f) { azimuth = 0.0; return; }
            azimuth = Math.Acos(Math.Max(-1.0, Math.Min(1.0, UnityEngine.Vector3.Dot(f.normalized, l.normalized))));
        }

        // Scatterer's display value of a radiance per irradiance (per channel).
        internal static Rgb Display(Rgb radiance, double skyExposure)
        {
            return new Rgb(DisplayOf(radiance.R, skyExposure), DisplayOf(radiance.G, skyExposure), DisplayOf(radiance.B, skyExposure));
        }

        internal static double DisplayOf(double radiance, double skyExposure)
        {
            double l = Math.Max(radiance, 0.0) * SunIntensity * skyExposure;
            return l < HdrKnee ? Math.Pow(l * HdrScale, 1.0 / 2.2) : 1.0 - Math.Exp(-l);
        }

        // The state a frame's evaluation keeps from the one before: the higher orders at the anchors depend on the camera's height, the ray's elevation and the sun's elevation, not on the azimuth,
        // so a camera that turns on the spot does not pay for them again.
        internal sealed class Evaluator
        {
            private readonly Rgb[] psi = new Rgb[ViewSamples / AnchorStride + 2];
            private bool have;
            private Medium lastMedium;
            private double lastHeight, lastElevation, lastSun;
            // How far the inputs may move before the higher orders are worked out again: the camera's height as a share of the air's scale height (4 percent: the density moves by as much),
            // the angles in radians (a quarter of a degree).
            internal const double HeightTolerance = 0.04, AngleTolerance = 0.0044;
            internal int Recomputed;

            internal Horizon Evaluate(Medium m, double cameraHeightM, double elevationRad, double sunElevationRad, double azimuthRad)
            {
                bool moved = !have || lastMedium.RayleighHeightM != m.RayleighHeightM || lastMedium.Rayleigh.B != m.Rayleigh.B || lastMedium.MieG != m.MieG || lastMedium.PlanetRadiusM != m.PlanetRadiusM
                             || Math.Abs(cameraHeightM - lastHeight) > HeightTolerance * m.RayleighHeightM || Math.Abs(elevationRad - lastElevation) > AngleTolerance || Math.Abs(sunElevationRad - lastSun) > AngleTolerance;
                if (moved)
                {
                    lastMedium = m; lastHeight = cameraHeightM; lastElevation = elevationRad; lastSun = sunElevationRad;
                    have = true;
                    Recomputed++;
                }
                return CloudHorizonSky.Evaluate(m, lastHeight, lastElevation, lastSun, azimuthRad, psi, scratch, !moved);
            }

            private readonly Scratch scratch = new Scratch();
        }

        // The arrays of one evaluation, kept by an Evaluator so that a frame allocates nothing.
        private sealed class Scratch
        {
            public readonly double[] Edges = new double[ViewSamples + 1], SampleX = new double[ViewSamples], SampleY = new double[ViewSamples], SampleZ = new double[ViewSamples], Segment = new double[ViewSamples];
            public readonly Rgb[] Transmittance = new Rgb[ViewSamples], Sun = new Rgb[ViewSamples];
        }

        // One ray, no cache: the sky's radiance along the view ray of the camera at cameraHeightM, elevation elevationRad above its horizontal, azimuthRad from the sun's azimuth, for a sun at
        // sunElevationRad above the horizon.
        internal static Horizon Evaluate(Medium m, double cameraHeightM, double elevationRad, double sunElevationRad, double azimuthRad)
        {
            return Evaluate(m, cameraHeightM, elevationRad, sunElevationRad, azimuthRad, new Rgb[ViewSamples / AnchorStride + 2], new Scratch(), false);
        }

        // reuse: the higher orders in store are those of this height, elevation and sun (kept from an evaluation within the tolerances); otherwise they are worked out into it.
        private static Horizon Evaluate(Medium m, double cameraHeightM, double elevationRad, double sunElevationRad, double azimuthRad, Rgb[] store, Scratch scratch, bool reuse)
        {
            var result = new Horizon();
            if (!m.Valid) return result;
            double rg = m.PlanetRadiusM;
            double height = Math.Max(1.0, Math.Min(cameraHeightM, 0.9 * (m.TopRadiusM - rg)));
            // the ray never goes below the ground: the lowest elevation is the tangent of the planet
            double dip = Math.Acos(Math.Min(1.0, rg / (rg + height)));
            double el = Math.Max(elevationRad, -dip + 1e-5);
            double ce = Math.Cos(el), se = Math.Sin(el), ca = Math.Cos(azimuthRad), sa = Math.Sin(azimuthRad);
            double dx = ce * ca, dy = ce * sa, dz = se;
            double sunC = Math.Cos(sunElevationRad), sunS = Math.Sin(sunElevationRad);
            double cz = rg + height;
            double mu = dx * sunC + dz * sunS;
            double length = ExitDistance(0.0, 0.0, cz, dx, dy, dz, m.TopRadiusM);
            double pr = Rayleigh(mu), pm = MiePhase(mu, m.MieG);

            // the samples along the ray: their positions, the optical depths from the camera (Rayleigh and Mie columns) and the sun's transmittance and shadow at each
            double[] edges = scratch.Edges, sampleX = scratch.SampleX, sampleY = scratch.SampleY, sampleZ = scratch.SampleZ, segment = scratch.Segment;
            Rgb[] transmittance = scratch.Transmittance;
            for (int i = 0; i <= ViewSamples; i++) edges[i] = length * Math.Pow((double)i / ViewSamples, ViewSpacing);
            double colR = 0.0, colM = 0.0;
            double fr0 = DensityR(m, HeightAt(m, 0.0, 0.0, cz, dx, dy, dz, 0.0)), fm0 = DensityM(m, HeightAt(m, 0.0, 0.0, cz, dx, dy, dz, 0.0));
            var single = new Rgb();
            for (int i = 0; i < ViewSamples; i++)
            {
                double s0 = edges[i], s1 = edges[i + 1], mid = 0.5 * (s0 + s1), ds = s1 - s0;
                double f1R = DensityR(m, HeightAt(m, 0.0, 0.0, cz, dx, dy, dz, mid)), f1M = DensityM(m, HeightAt(m, 0.0, 0.0, cz, dx, dy, dz, mid));
                double f2R = DensityR(m, HeightAt(m, 0.0, 0.0, cz, dx, dy, dz, s1)), f2M = DensityM(m, HeightAt(m, 0.0, 0.0, cz, dx, dy, dz, s1));
                double h = ds * 0.5;
                // the column at the middle of the segment (quadratic through the three nodes) and at its far edge (Simpson)
                double midR = colR + h * (5.0 * fr0 + 8.0 * f1R - f2R) / 12.0, midM = colM + h * (5.0 * fm0 + 8.0 * f1M - f2M) / 12.0;
                for (int c = 0; c < 3; c++) transmittance[i][c] = Math.Exp(-(m.Rayleigh[c] * midR + m.MieExtinction * midM));
                colR += ds * (fr0 + 4.0 * f1R + f2R) / 6.0; colM += ds * (fm0 + 4.0 * f1M + f2M) / 6.0;
                fr0 = f2R; fm0 = f2M;
                sampleX[i] = mid * dx; sampleY[i] = mid * dy; sampleZ[i] = cz + mid * dz;
                segment[i] = ds;
            }
            double nearR = DensityR(m, height), nearM = DensityM(m, height);

            // the sun at each sample, and the higher orders at the anchors
            Rgb[] sun = scratch.Sun;
            for (int i = 0; i < ViewSamples; i++)
                if (!SunTransmittance(m, sampleX[i], sampleY[i], sampleZ[i], sunC, sunS, out sun[i])) sun[i] = new Rgb();
            int anchors = (ViewSamples + AnchorStride - 1) / AnchorStride;
            int centre = AnchorStride / 2;
            if (!reuse)
            {
                for (int a = 0; a < anchors; a++)
                {
                    int i = Math.Min(ViewSamples - 1, a * AnchorStride + centre);
                    // at the sample's height straight above the camera: the sun's elevation there is the camera's, which keeps the orders independent of the view's azimuth (a sample 50 km away sees
                    // the sun a few degrees lower or higher by the planet's curve; the orders are a fifth of the radiance, and the difference is under 1 percent of the sky at a sun above 40 degrees
                    // and 3 percent of it at 10 degrees)
                    store[a] = transmittance[i].B < MinimumSceneBlue ? new Rgb() : HigherOrders(m, 0.0, 0.0, rg + HeightAt(m, 0.0, 0.0, cz, dx, dy, dz, 0.5 * (edges[i] + edges[i + 1])), sunC, sunS);
                }
                store[anchors] = HigherOrders(m, 0.0, 0.0, cz, sunC, sunS);
            }

            for (int i = 0; i < ViewSamples; i++)
            {
                double hs = HeightAt(m, 0.0, 0.0, cz, dx, dy, dz, 0.5 * (edges[i] + edges[i + 1]));
                double dr = DensityR(m, hs), dm = DensityM(m, hs);
                // the higher orders at this sample: the two nearest anchors, by sample index
                double pos = (i - centre) / (double)AnchorStride;
                int a0 = (int)Math.Max(0.0, Math.Min(anchors - 1.0, Math.Floor(pos))), a1 = Math.Min(anchors - 1, a0 + 1);
                double t = Math.Max(0.0, Math.Min(1.0, pos - a0));
                for (int c = 0; c < 3; c++)
                {
                    double sca = m.Rayleigh[c] * dr + m.MieScattering * dm;
                    double ms = store[a0][c] * (1.0 - t) + store[a1][c] * t;
                    single[c] += transmittance[i][c] * (sun[i][c] * (m.Rayleigh[c] * dr * pr + m.MieScattering * dm * pm) + sca * ms) * segment[i];
                }
            }
            result.Far = single;

            // the colour of a short path at the camera: the local source over the extinction
            Rgb camSun;
            bool camLit = SunTransmittance(m, 0.0, 0.0, cz, sunC, sunS, out camSun);
            if (!camLit) camSun = new Rgb();
            for (int c = 0; c < 3; c++)
            {
                double ext = m.Rayleigh[c] * nearR + m.MieExtinction * nearM;
                double sca = m.Rayleigh[c] * nearR + m.MieScattering * nearM;
                double source = camSun[c] * (m.Rayleigh[c] * nearR * pr + m.MieScattering * nearM * pm) + sca * store[anchors][c];
                result.Near[c] = ext > 0.0 ? source / ext : 0.0;
            }
            return result;
        }

        private static double DensityR(Medium m, double h)
        {
            return Math.Exp(-h / m.RayleighHeightM);
        }

        private static double DensityM(Medium m, double h)
        {
            return Math.Exp(-h / m.MieHeightM);
        }

        private static double HeightAt(Medium m, double ox, double oy, double oz, double dx, double dy, double dz, double s)
        {
            double x = ox + s * dx, y = oy + s * dy, z = oz + s * dz;
            return Math.Max(0.0, Math.Sqrt(x * x + y * y + z * z) - m.PlanetRadiusM);
        }

        internal static double Rayleigh(double mu)
        {
            return 3.0 / (16.0 * Math.PI) * (1.0 + mu * mu);
        }

        // Scatterer's Mie phase function (CommonAtmosphere.cginc PhaseFunctionM).
        internal static double MiePhase(double mu, double g)
        {
            return 1.5 / (4.0 * Math.PI) * (1.0 - g * g) * Math.Pow(1.0 + g * g - 2.0 * g * mu, -1.5) * (1.0 + mu * mu) / (2.0 + g * g);
        }

        // The distance along the ray from the point (ox, oy, oz) to the sphere of the given radius about the origin (the far intersection).
        private static double ExitDistance(double ox, double oy, double oz, double dx, double dy, double dz, double radius)
        {
            double b = ox * dx + oy * dy + oz * dz, c = ox * ox + oy * oy + oz * oz - radius * radius;
            double disc = b * b - c;
            return disc < 0.0 ? 0.0 : -b + Math.Sqrt(disc);
        }

        // The sun's transmittance at a point (the optical depth to the top of the air along the sun's direction); false where the planet shadows the point.
        private static bool SunTransmittance(Medium m, double x, double y, double z, double sunC, double sunS, out Rgb t)
        {
            t = new Rgb();
            double b = x * sunC + z * sunS;
            double c = x * x + y * y + z * z - m.PlanetRadiusM * m.PlanetRadiusM;
            if (b < 0.0 && b * b - c > 0.0) return false;
            double length = ExitDistance(x, y, z, sunC, 0.0, sunS, m.TopRadiusM);
            double colR = 0.0, colM = 0.0;
            for (int k = 0; k < SunSteps; k++)
            {
                double u = (k + 0.5) / SunSteps, s = length * u * u, ds = length * 2.0 * u / SunSteps;
                double h = HeightAt(m, x, y, z, sunC, 0.0, sunS, s);
                colR += DensityR(m, h) * ds; colM += DensityM(m, h) * ds;
            }
            for (int k = 0; k < 3; k++) t[k] = Math.Exp(-(m.Rayleigh[k] * colR + m.MieExtinction * colM));
            return true;
        }

        // The higher orders at a point: Psi = L2 / (1 - F), L2 the single-scattered radiance (per irradiance) averaged over the sphere, F the mean transfer of the scattering along the sphere's rays
        // (Hillaire, EGSR 2020, section 5.5). The source of the higher orders at a place is then the scattering coefficient times Psi.
        private static Rgb HigherOrders(Medium m, double x, double y, double z, double sunC, double sunS)
        {
            var l2 = new Rgb();
            var transfer = new Rgb();
            double golden = Math.PI * (3.0 - Math.Sqrt(5.0));
            for (int n = 0; n < SphereDirections; n++)
            {
                double dz = 1.0 - 2.0 * (n + 0.5) / SphereDirections, r = Math.Sqrt(Math.Max(0.0, 1.0 - dz * dz));
                double dx = r * Math.Cos(golden * n), dy = r * Math.Sin(golden * n);
                double mu = dx * sunC + dz * sunS;
                double pr = Rayleigh(mu), pm = MiePhase(mu, m.MieG);
                // the ray ends at the ground or at the top of the air
                double b = x * dx + y * dy + z * dz, c = x * x + y * y + z * z - m.PlanetRadiusM * m.PlanetRadiusM;
                double disc = b * b - c;
                bool ground = disc > 0.0 && -b - Math.Sqrt(disc) > 0.0;
                double length = ground ? -b - Math.Sqrt(disc) : ExitDistance(x, y, z, dx, dy, dz, m.TopRadiusM);
                double colR = 0.0, colM = 0.0;
                var radiance = new Rgb();
                var trans = new Rgb();
                for (int k = 0; k < SphereSteps; k++)
                {
                    double u = (k + 0.5) / SphereSteps, s = length * u * u, ds = length * 2.0 * u / SphereSteps;
                    double px = x + s * dx, py = y + s * dy, pz = z + s * dz;
                    double h = Math.Max(0.0, Math.Sqrt(px * px + py * py + pz * pz) - m.PlanetRadiusM);
                    double dr = DensityR(m, h), dm = DensityM(m, h);
                    colR += dr * ds * 0.5; colM += dm * ds * 0.5;
                    Rgb sun;
                    bool lit = SunTransmittance(m, px, py, pz, sunC, sunS, out sun);
                    for (int ch = 0; ch < 3; ch++)
                    {
                        double tc = Math.Exp(-(m.Rayleigh[ch] * colR + m.MieExtinction * colM));
                        double sca = m.Rayleigh[ch] * dr + m.MieScattering * dm;
                        if (lit) radiance[ch] += tc * sun[ch] * (m.Rayleigh[ch] * dr * pr + m.MieScattering * dm * pm) * ds;
                        trans[ch] += tc * sca * ds;
                    }
                    colR += dr * ds * 0.5; colM += dm * ds * 0.5;
                }
                if (ground)
                {
                    double gx = x + length * dx, gy = y + length * dy, gz = z + length * dz, gl = Math.Sqrt(gx * gx + gy * gy + gz * gz);
                    Rgb sun;
                    if (SunTransmittance(m, gx, gy, gz, sunC, sunS, out sun))
                    {
                        double cosSun = Math.Max(0.0, (gx * sunC + gz * sunS) / gl);
                        for (int ch = 0; ch < 3; ch++) radiance[ch] += Math.Exp(-(m.Rayleigh[ch] * colR + m.MieExtinction * colM)) * sun[ch] * cosSun * m.GroundAlbedo / Math.PI;
                    }
                }
                for (int ch = 0; ch < 3; ch++)
                {
                    l2[ch] += radiance[ch] / SphereDirections;
                    transfer[ch] += trans[ch] / SphereDirections;
                }
            }
            var psi = new Rgb();
            for (int ch = 0; ch < 3; ch++) psi[ch] = l2[ch] / (1.0 - Math.Min(transfer[ch], 0.95));
            return psi;
        }
    }
}
