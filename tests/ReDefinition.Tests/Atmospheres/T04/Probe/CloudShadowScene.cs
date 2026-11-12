#if UNITY_2019_4_OR_NEWER
// A scene of the weather player (package T04, -mode scene -scene cloud-shadow -out <folder>); linked into the player by
// tests/WeatherPlayer/linked/T04.txt and compiled out of the test project (the symbol is defined by Unity only).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The cloud shadow on the GPU against the analytic expectations, with the mod's own CloudShadowRenderer and CloudShadowRig (forward path:
    // the colour multiply at AfterForwardOpaque; the deferred mask pass 2 differs from it only in where the product goes). The planet is a 600 km
    // body with its centre 600 km below the origin and its map frame turned so that up is world +y, east +x and north +z. The cloud is a slab
    // 20 m thick at 2000 m whose coverage map is 1 west of longitude 0 and 0 east of it (a hard edge: the threshold of the bilinear map), or 1
    // everywhere. The sun stands in the east. A camera looks straight down; the picture with the rig off is the lit ground, the picture with it on
    // is the shadowed ground, and their ratio per pixel is the shadow factor (the lighting cancels).
    //
    // Captures (the 'ours' column of tools/weather_metrics/parity_run.py table, one folder per check id with meta.json) go to <out>/ours/, the
    // pictures (natural and aid: the factor in false colour) to <out>/.
    [WeatherScene("cloud-shadow")]
    public sealed class CloudShadowScene : WeatherSceneBase
    {
        private const double R = 600000.0;
        private const int MapW = 2048, MapH = 1024;
        private const float SlabBase = 1990f, SlabTop = 2010f, SlabCentre = 2000f;

        private CloudShadowRenderer renderer;
        private CloudShadowRig rig;
        private LayerMapStore maps;
        private float[] lit, shadowed;
        private string oursFolder;

        public override IEnumerator Run()
        {
            Shader shader = Shader.Find(CloudShadowRenderer.ShaderName);
            Sink.Flag("shadow_shader", shader != null);
            if (shader == null) yield break;
            renderer = new CloudShadowRenderer();
            bool ok = renderer.Init(shader);
            Sink.Flag("shadow_compiled", ok);
            if (!ok) yield break;
            maps = new LayerMapStore();
            maps.Ensure(MapW, MapH);
            rig = new CloudShadowRig(renderer, c => c == Cam ? CloudShadowRole.Scene : CloudShadowRole.None, () => Sun);
            rig.Start();
            Directory.CreateDirectory(Output);
            oursFolder = Path.Combine(Output, "ours");
            Directory.CreateDirectory(oursFolder);
            bool finite = true;

            // ---- the shadow of a hard edge: displacement and penumbra, at 20, 45 and 70 degrees ----
            double[] elevations = { 20.0, 45.0, 70.0 };
            double worstShift = 0.0;
            foreach (double e in elevations)
            {
                double expected = SlabCentre / Math.Tan(e * Math.PI / 180.0);
                float height = e < 30.0 ? 4000f : 1500f;
                SetCloud(8f, true);
                SetSun(e);
                Pose(new CameraPose(new Vector3((float)-expected, height, 0f), 0f, 90f));
                yield return Capture();
                double[] xs, ratio;
                RowRatio(out xs, out ratio);
                foreach (double v in ratio) if (double.IsNaN(v) || double.IsInfinity(v)) finite = false;
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (double v in ratio) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                double x50 = Crossing(xs, ratio, 0.5 * (lo + hi));
                double shift = -x50;
                double err = Math.Abs(shift - expected) / expected;
                worstShift = Math.Max(worstShift, err);
                Sink.Info("shift_m.e" + (int)e, shift);
                Sink.Info("shift_expected_m.e" + (int)e, expected);
                Sink.AtMost("R11-shadow-displacement.e" + (int)e, err, 0.01);
                if (e == 45.0)
                {
                    double w1090 = Width1090(xs, ratio);
                    double full = CloudShadowMath.PenumbraWidth(SlabCentre, e * Math.PI / 180.0);
                    double pixel = xs[1] - xs[0];
                    Sink.Info("penumbra_10_90_m", w1090);
                    Sink.Info("penumbra_expected_10_90_m", 0.687 * full);
                    Sink.Info("pixel_m", pixel);
                    // The error beyond the two pixels' slack, as a share of the expected width.
                    Sink.AtMost("R11-shadow-penumbra", Math.Max(0.0, Math.Abs(w1090 - 0.687 * full) - 2.0 * pixel) / (0.687 * full), 0.25);
                    WriteCapture("R11-shadow-displacement", "{\"shift_m\": " + N(shift) + ", \"height_m\": 2000, \"elevation_deg\": 45, \"x_m\": " + List(xs) + ", \"profile\": " + List(ratio) + ", \"pixel_m\": " + N(pixel) + "}");
                    WriteCapture("R11-shadow-penumbra", "{\"x_m\": " + List(xs) + ", \"profile\": " + List(ratio) + ", \"pixel_m\": " + N(pixel) + ", \"height_m\": 2000, \"elevation_deg\": 45}");
                    SavePictures("edge45");
                }
                else if (e == 20.0) SavePictures("edge20");
                else SavePictures("edge70");
            }
            Sink.Info("R11-shadow-displacement.worst", worstShift);

            // ---- the darkness law: the factor under a wide cloud against exp(-tau of the sun path), four optical depths, sun at 45 degrees ----
            double[] taus = { 0.1, 1.0, 5.0, 20.0 };
            double worstDark = 0.0;
            foreach (double tau in taus)
            {
                SetCloud((float)tau, false);
                SetSun(45.0);
                Pose(new CameraPose(new Vector3(-300f, 1500f, 0f), 0f, 90f));
                yield return Capture();
                double[] xs, ratio;
                RowRatio(out xs, out ratio);
                double mean = 0.0;
                int n = 0;
                for (int i = ratio.Length / 4; i < ratio.Length * 3 / 4; i++) { mean += ratio[i]; n++; }
                mean /= n;
                double want = Math.Exp(-tau / Math.Sin(Math.PI / 4.0));
                double err = Math.Abs(mean - want) / want;
                worstDark = Math.Max(worstDark, err);
                Sink.Info("factor.tau" + tau.ToString(CultureInfo.InvariantCulture), mean);
                Sink.Info("factor_expected.tau" + tau.ToString(CultureInfo.InvariantCulture), want);
                Sink.AtMost("R11-shadow-darkness.tau" + tau.ToString(CultureInfo.InvariantCulture), err, 0.10);
                if (tau == 5.0) SavePictures("wide-tau5");
            }
            Sink.Info("R11-shadow-darkness.worst", worstDark);

            // ---- receivers: sea, land, the vehicle and a small object in one wide shadow (tau 1), the land and the sea within 5 %, all within 3 % ----
            SetCloud(1f, false);
            SetSun(45.0);
            Pose(new CameraPose(new Vector3(500f, 1500f, 0f), 0f, 90f));
            var small = GameObject.CreatePrimitive(PrimitiveType.Cube);
            small.name = "Small object";
            small.transform.position = new Vector3(900f, TerrainHeight(900f, 60f) + 2.5f, 60f);
            small.transform.localScale = new Vector3(5f, 5f, 5f);
            UnityEngine.Object.Destroy(small.GetComponent<Collider>());
            yield return Capture();
            Vector3 vehicleTop = Vehicle.transform.position + Vector3.up * 10f;
            double sea = Patch(new Vector3(-300f, 0f, 0f), 6), land = Patch(new Vector3(300f, TerrainHeight(300f, 0f), 0f), 6);
            double terrain = Patch(new Vector3(840f, TerrainHeight(840f, 0f), 0f), 3);
            double vessel = Patch(vehicleTop, 2), obj = Patch(small.transform.position + Vector3.up * 2.5f, 1);
            double want1 = Math.Exp(-1.0 / Math.Sin(Math.PI / 4.0));
            Sink.Info("receiver.sea", sea);
            Sink.Info("receiver.land", land);
            Sink.Info("receiver.terrain_at_vessel", terrain);
            Sink.Info("receiver.vessel", vessel);
            Sink.Info("receiver.small_object", obj);
            Sink.Info("receiver.expected", want1);
            double spread = (Math.Max(Math.Max(terrain, vessel), obj) - Math.Min(Math.Min(terrain, vessel), obj)) / ((terrain + vessel + obj) / 3.0);
            Sink.AtMost("R11-shadow-receivers", spread, 0.03);
            Sink.AtMost("R13-ocean-land", Math.Abs(sea - land) / land, 0.05);
            WriteCapture("R11-shadow-receivers", "{\"factors\": {\"terrain\": " + N(terrain) + ", \"vessel\": " + N(vessel) + ", \"small_object\": " + N(obj) + "}}");
            WriteCapture("R13-ocean-land", "{\"water\": " + N(sea) + ", \"land\": " + N(land) + "}");
            SavePictures("receivers");

            Sink.Flag("shadow_finite", finite);
            rig.Stop();
            renderer.Dispose();
        }

        // The slab of vertical optical depth tau, everywhere or west of longitude 0 only.
        private void SetCloud(float tau, bool westOnly)
        {
            var source = new LayerStackSource(0, R, true);
            var def = new CloudLayerDef("slab", 0, SlabBase, SlabTop, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, tau / (SlabTop - SlabBase))
            {
                Coverage = 0.5f,
                EdgeSoftness = 1e-4f
            };
            def.CoverageMapHandle = 0;
            def.TypeMapHandle = 0;
            source.Add(def, null, null);
            var cov = new byte[MapW * MapH];
            for (int y = 0; y < MapH; y++)
                for (int x = 0; x < MapW; x++)
                    cov[y * MapW + x] = (byte)(!westOnly || x < MapW / 2 ? 255 : 0);
            maps.SetTargetBytes(0, cov, new byte[MapW * MapH]);
            maps.Step(0f, MapSlew.DefaultRate, true);
            renderer.Upload(source, maps);
        }

        private void SetSun(double elevationDeg)
        {
            double e = elevationDeg * Math.PI / 180.0;
            var toSun = new Vector3((float)Math.Cos(e), (float)Math.Sin(e), 0f);
            Sun.transform.rotation = Quaternion.LookRotation(-toSun);
            Matrix4x4 toMap = Matrix4x4.zero;
            toMap.SetRow(0, new Vector4(0f, 1f, 0f, 0f));
            toMap.SetRow(1, new Vector4(1f, 0f, 0f, 0f));
            toMap.SetRow(2, new Vector4(0f, 0f, 1f, 0f));
            toMap.SetRow(3, new Vector4(0f, 0f, 0f, 1f));
            CloudShadowFrame f = rig.Frame;
            f.PlanetCenter = new Vector3(0f, (float)-R, 0f);
            f.RadiusUnits = R;
            f.MetresPerUnit = 1f;
            f.WorldToMap = toMap;
            f.TowardsSun = toSun;
            f.StepsPerSpan = 12;
            f.DiscTaps = 8;
            f.Floor = 0f;
        }

        // Renders the lit ground (rig off) and the shadowed ground (rig on) from the current pose.
        private IEnumerator Capture()
        {
            rig.Enabled = false;
            yield return Frames(4);
            lit = Grab();
            rig.Enabled = true;
            yield return Frames(4);
            shadowed = Grab();
        }

        private float[] Grab()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            var tex = new Texture2D(Target.width, Target.height, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            Color[] px = tex.GetPixels();
            UnityEngine.Object.Destroy(tex);
            var l = new float[px.Length];
            for (int i = 0; i < px.Length; i++) l[i] = (px[i].r + px[i].g + px[i].b) / 3f;
            return l;
        }

        private float Ratio(int x, int y)
        {
            int i = y * Target.width + x;
            return lit[i] > 1e-6f ? shadowed[i] / lit[i] : float.NaN;
        }

        // The ratio along the screen's middle row with the world x of each pixel on the plane y = 0 (the sea).
        private void RowRatio(out double[] xs, out double[] ratio)
        {
            int w = Target.width, y = Target.height / 2;
            xs = new double[w];
            ratio = new double[w];
            var plane = new Plane(Vector3.up, Vector3.zero);
            for (int x = 0; x < w; x++)
            {
                Ray ray = Cam.ScreenPointToRay(new Vector3(x + 0.5f, y + 0.5f, 0f));
                float t;
                plane.Raycast(ray, out t);
                xs[x] = ray.GetPoint(t).x;
                ratio[x] = Ratio(x, y);
            }
        }

        // The mean ratio in a square of pixels around the screen position of a world point.
        private double Patch(Vector3 world, int halfPixels)
        {
            Vector3 s = Cam.WorldToScreenPoint(world);
            int cx = Mathf.RoundToInt(s.x), cy = Mathf.RoundToInt(s.y);
            double sum = 0.0;
            int n = 0;
            for (int y = cy - halfPixels; y <= cy + halfPixels; y++)
                for (int x = cx - halfPixels; x <= cx + halfPixels; x++)
                {
                    if (x < 0 || y < 0 || x >= Target.width || y >= Target.height) continue;
                    float r = Ratio(x, y);
                    if (float.IsNaN(r)) continue;
                    sum += r;
                    n++;
                }
            return n > 0 ? sum / n : double.NaN;
        }

        private static double Crossing(double[] x, double[] y, double level)
        {
            for (int i = 1; i < x.Length; i++)
                if ((y[i - 1] - level) * (y[i] - level) <= 0.0 && y[i] != y[i - 1])
                    return x[i - 1] + (x[i] - x[i - 1]) * (level - y[i - 1]) / (y[i] - y[i - 1]);
            return double.NaN;
        }

        private static double Width1090(double[] x, double[] y)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double v in y) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            return Math.Abs(Crossing(x, y, lo + 0.9 * (hi - lo)) - Crossing(x, y, lo + 0.1 * (hi - lo)));
        }

        // The natural picture (the shadowed ground) and the aid picture (the factor in false colour: white 1, red 0.5, black 0).
        private void SavePictures(string label)
        {
            int w = Target.width, h = Target.height;
            var natural = new Texture2D(w, h, TextureFormat.RGB24, false);
            var aid = new Texture2D(w, h, TextureFormat.RGB24, false);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float v = Mathf.Clamp01(shadowed[i]);
                    natural.SetPixel(x, y, new Color(v, v, v));
                    float r = Mathf.Clamp01(Ratio(x, y));
                    aid.SetPixel(x, y, r > 0.5f ? Color.Lerp(Color.red, Color.white, (r - 0.5f) * 2f) : Color.Lerp(Color.black, Color.red, r * 2f));
                }
            File.WriteAllBytes(Path.Combine(Output, "shadow-" + label + ".png"), natural.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(Output, "shadow-" + label + "-aid.png"), aid.EncodeToPNG());
            UnityEngine.Object.Destroy(natural);
            UnityEngine.Object.Destroy(aid);
        }

        private void WriteCapture(string id, string json)
        {
            string dir = Path.Combine(oursFolder, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "meta.json"), json);
        }

        private static string N(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string List(double[] v)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < v.Length; i++) sb.Append(i > 0 ? ", " : "").Append(double.IsNaN(v[i]) ? "null" : N(v[i]));
            return sb.Append(']').ToString();
        }
    }
}
#endif
