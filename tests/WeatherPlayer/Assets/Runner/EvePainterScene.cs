using System;
using System.Collections;
using System.Diagnostics;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The cloud-map painter on the GPU (package C23, src/Atmospheres/Eve): five test fields are painted by the pass shader and read
    // back, and the maps are compared with the CPU twin (MapPaintModel), which the CPU mirror also runs. Then the cost of the passes,
    // the slew step of the GPU against the twin, and the own shell drawn on a sphere against the map it shows.
    //
    //   -mode scene -scene eve-painter
    //
    // Limits (brief C23): painter <= 0.3 ms per update; GPU map against CPU map correlation >= 0.99 for 5 fields; the shell against the
    // map >= 0.95 at <= 0.3 ms.
    [WeatherScene("eve-painter")]
    public sealed class EvePainterScene : WeatherSceneBase
    {
        private const int FieldW = 256, FieldH = 128;

        protected override void BuildContent()
        {
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = Color.black;
            Cam.cullingMask = 1 << 31;
            Cam.fieldOfView = 50f;
        }

        private static WeatherMap MakeField(int kind)
        {
            var map = new WeatherMap();
            map.Resize(FieldW, FieldH);
            for (int j = 0; j < FieldH; j++)
            {
                double lat = 90.0 - (j + 0.5) / FieldH * 180.0;
                for (int i = 0; i < FieldW; i++)
                {
                    double lon = (i + 0.5) / FieldW * 360.0 - 180.0;
                    double v;
                    switch (kind)
                    {
                        case 0: v = Bump(lat, lon, 10, 20, 25); break;
                        case 1: v = Bump(lat, lon, -40, -170, 30); break;
                        case 2: v = 0.5 + 0.5 * Math.Sin(lon * Math.PI / 45.0) * Math.Cos(lat * Math.PI / 180.0); break;
                        case 3: v = Math.Max(Bump(lat, lon, 55, 100, 18), Bump(lat, lon, -20, -60, 22)); break;
                        default: v = lat > 60 ? 0.9 : 0.1; break;
                    }
                    map.Values[j * FieldW + i] = (float)Math.Max(0.0, Math.Min(1.0, v));
                }
            }
            return map;
        }

        private static double Bump(double lat, double lon, double lat0, double lon0, double radiusDeg)
        {
            double dl = lon - lon0;
            if (dl > 180) dl -= 360;
            if (dl < -180) dl += 360;
            double c = Math.Cos(lat0 * Math.PI / 180.0);
            double dlat = lat - lat0;
            double d = Math.Sqrt(dlat * dlat + dl * dl * c * c);
            return d >= radiusDeg ? 0.0 : 0.5 + 0.5 * Math.Cos(Math.PI * d / radiusDeg);
        }

        // The level map of a field: the same shape scaled so that part of it is at Moderate or more.
        private static WeatherMap LevelOf(WeatherMap coverage)
        {
            var level = new WeatherMap();
            level.Resize(coverage.Width, coverage.Height);
            for (int i = 0; i < level.Values.Length; i++) level.Values[i] = Math.Min(1f, coverage.Values[i] * 0.9f);
            return level;
        }

        private static TypeSlots Slots()
        {
            var slots = new TypeSlots(5);
            slots.Assign(CloudRole.SnowLight, 1);
            slots.Assign(CloudRole.SnowStratus, 2);
            slots.Assign(CloudRole.Storm, 3);
            return slots;
        }

        public override IEnumerator Run()
        {
            Shader paint = Shader.Find("ReDefinition/AtmospheresMapPaint");
            Shader shellShader = Shader.Find("ReDefinition/AtmospheresMapPaintShell");
            Sink.Flag("eve_painter_shaders_found", paint != null && shellShader != null);
            if (paint == null || shellShader == null) yield break;
            yield return Frames(2);

            var painter = new MapPainter(paint);
            byte[] lut = CloudTypeEncoding.BuildLut(Slots(), false);
            painter.SetLut(lut);
            float rate = MapSlew.DefaultRate;

            // 1. The GPU map against the CPU twin, five fields, each with its own layout.
            double worstCoverage = 1.0, worstType = 1.0;
            for (int k = 0; k < 5; k++)
            {
                WeatherMap coverage = MakeField(k);
                WeatherMap level = LevelOf(coverage);
                double shift = new[] { 0.0, 63.0, -120.0, 15.5, 179.0 }[k];
                bool mirrored = k % 2 == 1;
                painter.Upload(coverage, level);
                PaintSet set = painter.SetFor(shift, mirrored);
                painter.Paint(set, MapSlew.DefaultIntervalS, rate, true);
                yield return Frames(1);
                yield return new WaitForEndOfFrame();
                byte[] gpuCoverage = MapPainter.ReadBackRed(set.Coverage);
                byte[] gpuType = MapPainter.ReadBackRed(set.Type);
                var twinCoverage = new MapPaintModel(painter.Width, painter.Height);
                var twinType = new MapPaintModel(painter.Width, painter.Height, lut);
                twinCoverage.Snap(coverage.Values, coverage.Width, coverage.Height, shift, mirrored);
                twinType.Snap(level.Values, level.Width, level.Height, shift, mirrored);
                double cc = EveMapLayout.Correlation(gpuCoverage, twinCoverage.Current);
                double ct = EveMapLayout.Correlation(gpuType, twinType.Current);
                worstCoverage = Math.Min(worstCoverage, cc);
                worstType = Math.Min(worstType, ct);
                Sink.AtLeast("gpu_cpu_corr_coverage_field" + k, cc, 0.99);
                Sink.AtLeast("gpu_cpu_corr_type_field" + k, ct, 0.99);
            }
            Sink.AtLeast("gpu_cpu_corr_coverage_min", worstCoverage, 0.99);
            Sink.AtLeast("gpu_cpu_corr_type_min", worstType, 0.99);

            // 2. One limited step: from field 0 toward field 1 by dt, the GPU against the twin (levels).
            {
                WeatherMap a = MakeField(0), b = MakeField(1);
                painter.Upload(a, LevelOf(a));
                PaintSet set = painter.SetFor(0.0, false);
                painter.Paint(set, MapSlew.DefaultIntervalS, rate, true);
                var twin = new MapPaintModel(painter.Width, painter.Height);
                twin.Snap(a.Values, a.Width, a.Height, 0.0, false);
                painter.Upload(b, LevelOf(b));
                painter.Paint(set, MapSlew.DefaultIntervalS, rate, false);
                twin.Update(b.Values, b.Width, b.Height, 0.0, false, MapSlew.DefaultIntervalS, rate);
                yield return Frames(1);
                yield return new WaitForEndOfFrame();
                byte[] gpu = MapPainter.ReadBackRed(set.Coverage);
                int worst = 0;
                for (int i = 0; i < gpu.Length; i++) worst = Math.Max(worst, Math.Abs(gpu[i] - twin.Current[i]));
                Sink.AtMost("slew_step_gpu_vs_twin_levels", worst, 2);
                double stepLevels = MapSlew.StepLevels(rate, MapSlew.DefaultIntervalS);
                Sink.Info("slew_step_levels_limit", stepLevels);
            }

            // 3. The cost of a paint (both targets, two copies and two passes): N paints, then a one-texel read that waits for the GPU.
            {
                WeatherMap a = MakeField(3);
                painter.Upload(a, LevelOf(a));
                PaintSet set = painter.SetFor(0.0, false);
                painter.Paint(set, MapSlew.DefaultIntervalS, rate, true);
                SyncRead(set.Coverage);
                const int N = 200;
                var watch = Stopwatch.StartNew();
                double mainThread = 0;
                for (int i = 0; i < N; i++) mainThread += painter.Paint(set, MapSlew.DefaultIntervalS, rate, false);
                SyncRead(set.Coverage);
                watch.Stop();
                Sink.AtMost("painter_ms_per_update", watch.Elapsed.TotalMilliseconds / N, 0.3);
                Sink.AtMost("painter_main_thread_ms_per_update", mainThread / N, 1.0);
                // The orbit picture: pass 2.
                painter.PaintOrbit(MapPainter.OrbitShell, 0.0, false, null, 1f);
                SyncRead(painter.Orbit(MapPainter.OrbitShell));
                var orbitWatch = Stopwatch.StartNew();
                for (int i = 0; i < N; i++) painter.PaintOrbit(MapPainter.OrbitShell, 0.0, false, null, 1f);
                SyncRead(painter.Orbit(MapPainter.OrbitShell));
                orbitWatch.Stop();
                Sink.Info("orbit_picture_ms", orbitWatch.Elapsed.TotalMilliseconds / N);
            }

            // 4. The own shell: a sphere that draws the orbit picture, against the map it shows.
            WeatherMap field = MakeField(0);
            WeatherMap levels = LevelOf(field);
            painter.Upload(field, levels);
            painter.PaintOrbit(MapPainter.OrbitShell, 0.0, false, null, 1f);
            yield return Frames(1);
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            UnityEngine.Object.Destroy(sphere.GetComponent<Collider>());
            sphere.layer = 31;
            sphere.transform.position = Vector3.zero;
            sphere.transform.localScale = Vector3.one * 2f;
            var shellMaterial = new Material(shellShader) { renderQueue = 3000 };
            shellMaterial.SetTexture("_Picture", painter.Orbit(MapPainter.OrbitShell));
            shellMaterial.SetVector("_SunDir", new Vector4(0f, 0f, 1f, 0f));
            shellMaterial.SetFloat("_Opacity", 1f);
            sphere.GetComponent<Renderer>().sharedMaterial = shellMaterial;
            Cam.transform.position = new Vector3(0f, 0f, 3f);
            Cam.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            yield return Frames(3);
            yield return new WaitForEndOfFrame();
            LumaFrame frame = LumaFrame.Read(Target);
            double correlation = ShellCorrelation(frame, field, levels);
            Sink.AtLeast("shell_vs_map_correlation", correlation, 0.95);

            // The shell's cost: N extra renders of the camera with and without the sphere.
            Cam.targetTexture = Target;
            SyncRead(Target);
            const int R = 60;
            var withShell = Stopwatch.StartNew();
            for (int i = 0; i < R; i++) Cam.Render();
            SyncRead(Target);
            withShell.Stop();
            sphere.SetActive(false);
            var without = Stopwatch.StartNew();
            for (int i = 0; i < R; i++) Cam.Render();
            SyncRead(Target);
            without.Stop();
            Sink.AtMost("shell_ms", Math.Max(0.0, (withShell.Elapsed.TotalMilliseconds - without.Elapsed.TotalMilliseconds) / R), 0.3);

            painter.Dispose();
        }

        // Waits for the GPU: reads one texel of a render texture.
        private static void SyncRead(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var one = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            one.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            UnityEngine.Object.Destroy(one);
            RenderTexture.active = previous;
        }

        // The rendered luma against what the map says at the point of the sphere each pixel shows, over the pixels where the sun is
        // within 60 degrees of the normal (the shell's light is full there).
        private double ShellCorrelation(LumaFrame frame, WeatherMap coverage, WeatherMap level)
        {
            float fov = Cam.fieldOfView * Mathf.Deg2Rad;
            float tanHalf = Mathf.Tan(fov * 0.5f);
            float aspect = frame.Width / (float)frame.Height;
            var rendered = new System.Collections.Generic.List<double>();
            var expected = new System.Collections.Generic.List<double>();
            Vector3 origin = new Vector3(0f, 0f, 3f);
            for (int y = 0; y < frame.Height; y += 2)
            {
                for (int x = 0; x < frame.Width; x += 2)
                {
                    float px = ((x + 0.5f) / frame.Width * 2f - 1f) * tanHalf * aspect;
                    float py = ((y + 0.5f) / frame.Height * 2f - 1f) * tanHalf;
                    // The camera looks down -z with +x to its left (a yaw of 180 degrees): the pixel's direction in the world.
                    Vector3 dir = new Vector3(-px, py, -1f).normalized;
                    float b = Vector3.Dot(origin, dir);
                    float c = Vector3.Dot(origin, origin) - 1f;
                    float disc = b * b - c;
                    if (disc <= 0f) continue;
                    float t = -b - Mathf.Sqrt(disc);
                    if (t <= 0f) continue;
                    Vector3 n = (origin + dir * t).normalized;
                    if (n.z < 0.5f) continue;
                    double u = 0.5 + Math.Atan2(n.x, n.z) / (2 * Math.PI);
                    double v = Math.Acos(Mathf.Clamp(n.y, -1f, 1f)) / Math.PI;
                    float cov = EveMapLayout.SampleBilinear(coverage.Values, coverage.Width, coverage.Height, u, v);
                    float lvl = EveMapLayout.SampleBilinear(level.Values, level.Width, level.Height, u, v);
                    double grey = OrbitPicture.GreyAt(lvl);
                    double alpha = cov * OrbitPicture.OpacityAt(lvl);
                    expected.Add(grey * alpha);
                    rendered.Add(frame.Y[y * frame.Width + x]);
                }
            }
            if (expected.Count < 1000) return double.NaN;
            double me = 0, mr = 0;
            for (int i = 0; i < expected.Count; i++) { me += expected[i]; mr += rendered[i]; }
            me /= expected.Count; mr /= expected.Count;
            double see = 0, srr = 0, ser = 0;
            for (int i = 0; i < expected.Count; i++)
            {
                double de = expected[i] - me, dr = rendered[i] - mr;
                see += de * de; srr += dr * dr; ser += de * dr;
            }
            if (see <= 0 || srr <= 0) return 0.0;
            return ser / Math.Sqrt(see * srr);
        }
    }
}
