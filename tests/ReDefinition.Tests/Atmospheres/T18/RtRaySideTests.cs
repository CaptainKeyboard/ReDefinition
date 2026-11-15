using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Proxy;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // The ray side of the own layers (package T18): the seam's layout against the raytracing contract, the header row, the packet's cloud block, the
    // seam's freshness, and a float port of RtCommon.hlsli's OwnCloudTau / OwnCloudLight reading the whole texture as the sampler does, against the
    // atmospheres' law (RtCloudRayMath). The port mirrors the shader line by line; a change of one needs the other.
    [TestClass]
    public class RtRaySideTests
    {
        [TestMethod]
        public void TheSeamsLayoutIsTheRaytracingContracts()
        {
            // RtNative's constants are held to RtContract.h by RtContractTests; the seam's are held to RtNative here.
            Assert.AreEqual(RtNative.CloudsModeEve, RayCloudLayout.ModeEve);
            Assert.AreEqual(RtNative.CloudsModeOwn, RayCloudLayout.ModeOwn);
            Assert.AreEqual(RtNative.OwnCloudsHeaderRows, RayCloudLayout.HeaderRows);
            Assert.AreEqual(RtNative.OwnCloudsGlobalTexels, RayCloudLayout.GlobalTexels);
            Assert.AreEqual(RtNative.OwnCloudsLayerTexels, RayCloudLayout.LayerTexels);
            Assert.AreEqual(RtCloudRayMath.MaxTaps, RayCloudLayout.DefaultTaps);
            // The header of the most layers fits the narrowest pack.
            Assert.IsTrue(RtCloudPackLayout.HeaderTexels(RtCloudPackLayout.MaxLayers) <= 64);
        }

        [TestMethod]
        public void TheHeaderRowHoldsTheBodyAndEachLayersNumbers()
        {
            var source = new LatLonSource(PackScenes.Kerbin)
                .Add("low", 1500, 3000, 4.0, PackScenes.Broken(6, 4))
                .Add("high", 6000, 7000, 2.0, PackScenes.Broken(3, 2));
            source.LayerAt(1).ShadowStrength = 0.6f;
            source.LayerAt(1).MapShiftDeg = 12.5f;
            source.LayerAt(0).FadeFarM = 50000f;
            source.LayerAt(0).FadeNearM = 20000f;
            RtCloudPack pack = PackScenes.Bake(source, source.Tables(), 128);
            var header = new float[128 * 2];
            RtCloudPackLayout.WriteHeader(pack, header);
            Assert.AreEqual((float)PackScenes.Kerbin, header[0]);
            Assert.AreEqual(1f, header[1]);
            for (int i = 0; i < 2; i++)
            {
                RtCloudLayerEntry e = pack.Entries[i];
                int k = (RayCloudLayout.GlobalTexels + i * RayCloudLayout.LayerTexels) * 2;
                Assert.AreEqual((float)e.InnerRadiusM, header[k], "inner " + i);
                Assert.AreEqual((float)e.OuterRadiusM, header[k + 1], "outer " + i);
                Assert.AreEqual(e.ShadowStrength, header[k + 2]);
                Assert.AreEqual(e.Coverage, header[k + 3]);
                Assert.AreEqual(e.EdgeSoftness, header[k + 4]);
                Assert.AreEqual(e.ClearSkyK, header[k + 5]);
                Assert.AreEqual(e.WaterPower, header[k + 6]);
                Assert.AreEqual(e.MapShiftDeg, header[k + 7]);
            }
            int f0 = RayCloudLayout.GlobalTexels * 2 + 8, f1 = (RayCloudLayout.GlobalTexels + RayCloudLayout.LayerTexels) * 2 + 8;
            Assert.AreEqual(pack.Entries[0].FadeNearM, header[f0]);
            Assert.AreEqual(pack.Entries[0].FadeFarM, header[f0 + 1]);
            Assert.AreEqual(0f, header[f1 + 1], "no fade is written as far 0");
            Assert.AreEqual(0.6f, header[(RayCloudLayout.GlobalTexels + RayCloudLayout.LayerTexels) * 2 + 2]);
            Assert.AreEqual(12.5f, header[(RayCloudLayout.GlobalTexels + RayCloudLayout.LayerTexels) * 2 + 7]);
        }

        [TestMethod]
        public void TheShaderPortAgreesWithTheLawForLightAndView()
        {
            var source = new LatLonSource(PackScenes.Kerbin)
                .Add("low", 1500, 3000, 6.0, PackScenes.Noise(3u, 12))
                .Add("mid", 4000, 5500, 3.0, PackScenes.Noise(7u, 6))
                .Add("high", 7000, 8000, 1.5, PackScenes.Broken(5, 3));
            source.LayerAt(2).ShadowStrength = 0.5f;
            source.LayerAt(1).MapShiftDeg = 33f;
            RtCloudPack pack = PackScenes.Bake(source, source.Tables(), 256);
            var gpu = new ShaderPort(pack);
            var random = new System.Random(5);
            double worstLight = 0.0, worstView = 0.0;
            int lit = 0, seen = 0;
            for (int n = 0; n < 400; n++)
            {
                double lat = -60.0 + 120.0 * random.NextDouble(), lon = -180.0 + 360.0 * random.NextDouble();
                double altitude = n % 4 == 0 ? 3500.0 : 50.0;
                double ox, oy, oz, sx, sy, sz;
                source.Position(lat, lon, altitude, out ox, out oy, out oz);
                LatLonSource.SunTowards(lat, lon, 8.0 + 80.0 * random.NextDouble(), 360.0 * random.NextDouble(), out sx, out sy, out sz);
                double law = RtCloudRayMath.LightTransmission(pack, ox, oy, oz, sx, sy, sz);
                double port = gpu.Light(ox, oy, oz, sx, sy, sz);
                if (law < 0.98) lit++;
                worstLight = Math.Max(worstLight, Math.Abs(law - port));
                double view = RtCloudRayMath.ViewOpacity(pack, ox, oy, oz, sx, sy, sz, double.MaxValue);
                double viewPort = gpu.ViewOpacity(ox, oy, oz, sx, sy, sz);
                if (view > 0.02) seen++;
                worstView = Math.Max(worstView, Math.Abs(view - viewPort));
            }
            Assert.IsTrue(lit > 100 && seen > 100, "the rays must meet cloud: lit " + lit + ", seen " + seen);
            // Float against double, and the sampler's 8-bit filter weights (emulated below as exact) aside: the same numbers.
            Assert.IsTrue(worstLight < 0.01, "light transmission differs by up to " + worstLight.ToString("0.0000"));
            Assert.IsTrue(worstView < 0.01, "view opacity differs by up to " + worstView.ToString("0.0000"));
        }

        [TestMethod]
        public void TheBlockTakesTheCameraRelativeWorldToTheMapFrame()
        {
            // A map frame turned against the world, as a body's rotation turns it.
            Matrix4x4 rotation = Matrix4x4.identity;
            float c = Mathf.Cos(0.7f), s = Mathf.Sin(0.7f);
            rotation.SetRow(0, new Vector4(c, 0f, -s, 0f));
            rotation.SetRow(1, new Vector4(0f, 1f, 0f, 0f));
            rotation.SetRow(2, new Vector4(s, 0f, c, 0f));
            var view = new RayCloudView { WorldToMap = rotation, InnerRadiusM = 601500f, OuterRadiusM = 608000f, LayerCount = 3, Width = 512, SliceHeight = 256, Taps = 8 };
            var centre = new Vector3(1000f, -600000f, 250f);
            var camera = new Vector3(20f, 75f, -40f);
            var block = new float[24];
            RtOwnCloudBlock.Write(block, view, centre, camera);
            var world = new Vector3(310f, 1500f, -900f);
            Vector3 relative = world - camera;
            Vector3 expected = rotation.MultiplyVector(world - centre);
            for (int row = 0; row < 3; row++)
            {
                float q = block[row * 4] * relative.x + block[row * 4 + 1] * relative.y + block[row * 4 + 2] * relative.z + block[row * 4 + 3];
                Assert.AreEqual(expected[row], q, 0.05f, "row " + row);
            }
            Assert.AreEqual(608000f, block[12]);
            Assert.AreEqual(601500f, block[13]);
            Assert.AreEqual(3f, block[14]);
            Assert.AreEqual((float)RayCloudLayout.ModeOwn, block[15]);
            Assert.AreEqual(512f, block[16]);
            Assert.AreEqual(256f, block[17]);
            Assert.AreEqual(1f, block[18]);
            Assert.AreEqual(8f, block[19]);
            Assert.AreEqual(1f, block[23]);
        }

        [TestMethod]
        public void TheSeamIsFreshOnlyForItsBodyAndTheLastFrames()
        {
            RayCloudView v = RayCloudSeam.View;
            try
            {
                RayCloudSeam.Clear();
                Assert.IsFalse(RayCloudSeam.Fresh(100, 1), "cleared");
                v.Texture = new IntPtr(1234);
                v.LayerCount = 2;
                v.BodyIndex = 1;
                v.Frame = 100;
                Assert.IsTrue(RayCloudSeam.Fresh(100, 1));
                Assert.IsTrue(RayCloudSeam.Fresh(100 + RayCloudSeam.FreshFrames, 1));
                Assert.IsFalse(RayCloudSeam.Fresh(101 + RayCloudSeam.FreshFrames, 1), "stale");
                Assert.IsFalse(RayCloudSeam.Fresh(100, 2), "another body");
                RayCloudSeam.Ask(500);
                Assert.IsTrue(RayCloudSeam.Asked(500 + RayCloudSeam.AskTimeoutFrames));
                Assert.IsFalse(RayCloudSeam.Asked(501 + RayCloudSeam.AskTimeoutFrames), "the rays stopped asking");
                Assert.IsFalse(RayCloudSeam.Asked(499), "a frame before the ask (a new game start)");
            }
            finally
            {
                RayCloudSeam.Clear();
                RayCloudSeam.Ask(-1000);
            }
        }

        // RtCommon.hlsli's own-cloud functions in float, reading the texture as RtOwnClouds uploads it (the slices, then the header row) through a
        // bilinear sampler with wrapping, as linearWrap reads it. Map frame = world (the block's rows the identity), the camera at the body's centre.
        private sealed class ShaderPort
        {
            private const float Pi = 3.14159265f, TapSpacing = 400f;
            private readonly float[] tex;
            private readonly int width, rows, slice, layers;

            internal ShaderPort(RtCloudPack pack)
            {
                width = pack.Width;
                slice = pack.Height;
                layers = pack.LayerCount;
                rows = slice * layers + RayCloudLayout.HeaderRows;
                tex = new float[width * rows * 4];
                Array.Copy(pack.Data, tex, pack.Data.Length);
                var header = new float[width * RtCloudPackLayout.HeaderChannels];
                RtCloudPackLayout.WriteHeader(pack, header);
                // The header row's numbers are two a texel in r and g; the texture holds four.
                for (int i = 0; i < width; i++)
                {
                    tex[pack.Data.Length + i * 4] = header[i * 2];
                    tex[pack.Data.Length + i * 4 + 1] = header[i * 2 + 1];
                }
            }

            private float Load(int x, int y, int c) { return tex[(y * width + x) * 4 + c]; }

            private void Header(int texel, out float a, out float b)
            {
                int y = slice * layers;
                a = Load(texel, y, 0);
                b = Load(texel, y, 1);
            }

            private void Sample(float u, float v, out float r, out float g, out float bw, out float aw)
            {
                float x = u * width - 0.5f, y = v * rows - 0.5f;
                int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                float fx = x - x0, fy = y - y0;
                int xa = Wrap(x0, width), xb = Wrap(x0 + 1, width), ya = Wrap(y0, rows), yb = Wrap(y0 + 1, rows);
                r = Mix(Load(xa, ya, 0), Load(xb, ya, 0), Load(xa, yb, 0), Load(xb, yb, 0), fx, fy);
                g = Mix(Load(xa, ya, 1), Load(xb, ya, 1), Load(xa, yb, 1), Load(xb, yb, 1), fx, fy);
                bw = Mix(Load(xa, ya, 2), Load(xb, ya, 2), Load(xa, yb, 2), Load(xb, yb, 2), fx, fy);
                aw = Mix(Load(xa, ya, 3), Load(xb, ya, 3), Load(xa, yb, 3), Load(xb, yb, 3), fx, fy);
            }

            private static float Mix(float a, float b, float c, float d, float fx, float fy)
            {
                return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
            }

            private static int Wrap(int i, int n) { int m = i % n; return m < 0 ? m + n : m; }

            private static float Sq(float v) { return v * v; }

            private static bool Sphere(float ox, float oy, float oz, float dx, float dy, float dz, float radius, out float t0, out float t1)
            {
                float distance = (float)Math.Sqrt(ox * ox + oy * oy + oz * oz);
                float b = ox * dx + oy * dy + oz * dz;
                float h = b * b - (distance - radius) * (distance + radius);
                t0 = t1 = 0f;
                if (h < 0f) return false;
                h = (float)Math.Sqrt(h);
                t0 = -b - h;
                t1 = -b + h;
                return true;
            }

            private float ColumnTau(int layer, float qx, float qy, float qz, float shift, float coverage, float softness, float clearK, float power, out float baseF, out float topF)
            {
                float r = Math.Max((float)Math.Sqrt(qx * qx + qy * qy + qz * qz), 1f);
                float lat = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, qz / r))) * (180f / Pi);
                float lon = (float)Math.Atan2(qy, qx) * (180f / Pi) - shift;
                float y = Math.Max(0.5f, Math.Min(slice - 0.5f, (90f - lat) * slice / 180f));
                float u = (lon + 180f) / 360f, v = (layer * slice + y) / (layers * slice + RayCloudLayout.HeaderRows);
                float mr, mg, mb, ma;
                Sample(u, v, out mr, out mg, out mb, out ma);
                float map = clearK > 0f ? Math.Min(mr, 2f * mr - clearK) : mr;
                float share = Math.Max(0f, Math.Min(1f, (map + coverage - 1f) / Math.Max(softness, 1e-4f) + 0.5f));
                share = share > 0f ? (power == 1f ? share : (float)Math.Pow(share, power)) : 0f;
                baseF = 0f;
                topF = 1f;
                if (mg > 1e-6f)
                {
                    baseF = Math.Max(0f, Math.Min(1f, mb / mg));
                    topF = Math.Max(baseF, Math.Min(1f, ma / mg));
                }
                return share * mg;
            }

            private static float BandShare(float f0, float f1, float baseF, float topF)
            {
                float lo = Math.Min(f0, f1), hi = Math.Max(f0, f1), width = hi - lo;
                if (width < 1e-6f) return lo >= baseF && lo <= topF ? 1f : 0f;
                float overlap = Math.Min(hi, topF) - Math.Max(lo, baseF);
                return overlap <= 0f ? 0f : overlap / width;
            }

            private float Tau(float ox, float oy, float oz, float dx, float dy, float dz, float tMax, bool light)
            {
                float body, surface;
                Header(0, out body, out surface);
                float outer = 0f;
                for (int l = 0; l < layers; l++)
                {
                    float a, b;
                    Header(RayCloudLayout.GlobalTexels + l * RayCloudLayout.LayerTexels, out a, out b);
                    outer = Math.Max(outer, b);
                }
                float t0, t1;
                if (!Sphere(ox, oy, oz, dx, dy, dz, outer, out t0, out t1) || t1 <= 0f) return 0f;
                float total = 0f;
                for (int layer = 0; layer < layers; layer++)
                {
                    int b0 = RayCloudLayout.GlobalTexels + layer * RayCloudLayout.LayerTexels;
                    float inner, outerR, strength, coverage, soft, clearK, power, shift, near, far;
                    Header(b0, out inner, out outerR);
                    Header(b0 + 1, out strength, out coverage);
                    Header(b0 + 2, out soft, out clearK);
                    Header(b0 + 3, out power, out shift);
                    Header(b0 + 4, out near, out far);
                    float thickness = outerR - inner;
                    if (thickness <= 0f || (light && strength <= 0f)) continue;
                    float o0, o1;
                    if (!Sphere(ox, oy, oz, dx, dy, dz, outerR, out o0, out o1)) continue;
                    float lo = Math.Max(o0, 0f), hi = Math.Min(o1, tMax);
                    if (hi <= lo) continue;
                    var spans = new float[4];
                    int count = 0;
                    float i0, i1;
                    if (inner <= body || !Sphere(ox, oy, oz, dx, dy, dz, inner, out i0, out i1))
                    {
                        spans[0] = lo; spans[1] = hi; count = 1;
                    }
                    else
                    {
                        float aEnd = Math.Min(i0, hi);
                        if (aEnd > lo) { spans[count * 2] = lo; spans[count * 2 + 1] = aEnd; count++; }
                        float bStart = Math.Max(i1, lo);
                        if (hi > bStart) { spans[count * 2] = bStart; spans[count * 2 + 1] = hi; count++; }
                    }
                    float tau = 0f;
                    for (int s = 0; s < count; s++)
                    {
                        float span = spans[s * 2 + 1] - spans[s * 2];
                        int n = Math.Max(1, Math.Min(RayCloudLayout.DefaultTaps, (int)Math.Ceiling(span / TapSpacing)));
                        float dt = span / n;
                        for (int k = 0; k < n; k++)
                        {
                            float ta = spans[s * 2] + dt * k, t = ta + dt * 0.5f, tb = ta + dt;
                            float baseF, topF;
                            float column = ColumnTau(layer, ox + dx * t, oy + dy * t, oz + dz * t, shift, coverage, soft, clearK, power, out baseF, out topF);
                            if (column <= 0f) continue;
                            float f0 = ((float)Math.Sqrt(Sq(ox + dx * ta) + Sq(oy + dy * ta) + Sq(oz + dz * ta)) - inner) / thickness;
                            float f1 = ((float)Math.Sqrt(Sq(ox + dx * tb) + Sq(oy + dy * tb) + Sq(oz + dz * tb)) - inner) / thickness;
                            float inside = BandShare(f0, f1, baseF, topF);
                            if (inside <= 0f) continue;
                            tau += column * dt * inside / (Math.Max(0.002f, topF - baseF) * thickness);
                        }
                    }
                    total += light ? tau * strength : tau;
                }
                return total;
            }

            internal double Light(double ox, double oy, double oz, double dx, double dy, double dz)
            {
                float body, surface;
                Header(0, out body, out surface);
                float tMax = 1e30f, g0, g1;
                if (surface > 0.5f && Sphere((float)ox, (float)oy, (float)oz, (float)dx, (float)dy, (float)dz, body, out g0, out g1) && g0 > 0f) tMax = g0;
                float tau = Tau((float)ox, (float)oy, (float)oz, (float)dx, (float)dy, (float)dz, tMax, true);
                return tau <= 0f ? 1.0 : Math.Exp(-tau);
            }

            internal double ViewOpacity(double ox, double oy, double oz, double dx, double dy, double dz)
            {
                float tau = Tau((float)ox, (float)oy, (float)oz, (float)dx, (float)dy, (float)dz, 1e30f, false);
                return tau <= 0f ? 0.0 : 1.0 - Math.Exp(-tau);
            }
        }
    }
}
