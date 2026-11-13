#if UNITY_2019_4_OR_NEWER
// A scene of the weather player (package T04, -mode scene -scene cloud-shadow -out <folder>); linked into the player by
// tests/WeatherPlayer/linked/T04.txt and compiled out of the test project (the symbol is defined by Unity only).
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The cloud shadow on the GPU against the analytic expectations, with the mod's own CloudShadowRenderer and CloudShadowRig (the forward
    // path: the colour multiply at AfterForwardOpaque; the deferred mask pass 2 differs from it only in where the product goes). The planet is a 600 km body
    // with its centre 600 km below the origin and its map frame turned so that up is world +y, east +x and north +z. The cloud is a slab 20 m
    // thick at 2000 m whose coverage map is 1 west of longitude 0 and 0 east of it (a hard edge: the threshold of the bilinear map), or 1
    // everywhere. The sun stands in the east. A camera looks straight down; the picture without the buffer is the lit ground, the picture with
    // it is the shadowed ground, and their ratio per pixel is the shadow factor (the lighting cancels).
    //
    // "shadow_buffer_acts" gates the rest: the diagnostic output 1 of the forward pass is a constant 0.5 and must halve the picture. The
    // ratio of the picture read back with and without the buffer is the factor; the 8-bit or half-float target quantises it, which is why the
    // darkness metric floors the expected value.
    //
    // Captures (the 'ours' column of tools/weather_metrics/parity_run.py table, one folder per check id with meta.json) go to <out>/ours/, the
    // pictures (natural and aid: the factor in false colour) to <out>/.
    [WeatherScene("cloud-shadow")]
    public sealed partial class CloudShadowScene : WeatherSceneBase
    {
        private const double R = 600000.0;
        // A map of 16384 by 2 texels: a texel is 230 m of longitude, so that the edge of the bilinear threshold is placed to under a metre
        // (the weights of the filter have 8 bits); the cloud does not change with the latitude.
        private const int MapW = 16384, MapH = 2;
        private const float SlabBase = 1990f, SlabTop = 2010f, SlabCentre = 2000f;

        private CloudShadowRenderer renderer;
        private LayerMapStore maps;
        private CloudShadowRig rig;
        private CloudShadowFrame frame { get { return rig.Frame; } }
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
            // The mod's own rig puts the forward buffer on the camera in Camera.onPreCull and takes it off in Camera.onPostRender.
            rig = new CloudShadowRig(renderer, c => c == Cam ? CloudShadowRole.Scene : CloudShadowRole.None, () => Sun);
            rig.Start();
            NaturalMode(false);
            // The sea reaches 25 km: the shadow edge of the 20 degree view falls at 5.5 km and its view reaches 9.6 km.
            GameObject sea = GameObject.Find("Sea");
            if (sea != null) sea.transform.localScale = new Vector3(5000f, 1f, 5000f);
            // The vehicle and the small object are large enough to cover several pixels from the camera's height.
            Vehicle.transform.localScale = new Vector3(12f, 10f, 12f);
            maps = new LayerMapStore();
            maps.Ensure(MapW, MapH);
            Directory.CreateDirectory(Output);
            oursFolder = Path.Combine(Output, "ours");
            Directory.CreateDirectory(oursFolder);
            bool finite = true;
            // -shadownoise 1: the shape noise in the shadow, the shader against its CPU twin (CloudShadowNoiseScene.cs).
            if (WeatherPass.Argument("-shadownoise") == "1")
            {
                yield return RunShapeNoise();
                yield break;
            }
            // -shadowsteps 1: the step-policy comparison of the shader with its CPU twin (CloudShadowStepsScene.cs) instead of the analytic checks.
            if (WeatherPass.Argument("-shadowsteps") == "1")
            {
                yield return RunSteps();
                yield break;
            }

            // ---- does the buffer act at all: the diagnostic output 1 is a constant 0.5 ----
            SetCloud(1f, false);
            SetSun(45.0);
            Pose(new CameraPose(new Vector3(-300f, 1500f, 0f), 0f, 90f));
            frame.Debug = 1;
            yield return Capture();
            double[] dx, dr;
            RowRatio(out dx, out dr);
            double dm = 0.0;
            for (int i = dr.Length / 4; i < dr.Length * 3 / 4; i++) dm += dr[i];
            dm /= dr.Length / 2;
            Sink.Info("debug1.mean_ratio", dm);
            bool acts = Math.Abs(dm - 0.5) < 0.05;
            Sink.Flag("shadow_buffer_acts", acts);
            frame.Debug = 0;

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
                foreach (double v in ratio) { if (double.IsNaN(v)) continue; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
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
                if (e < 60.0)
                {
                    // The same view as a player sees it: the product's setting (the cloud's scattered light in the mask, the ambient kept).
                    NaturalMode(true);
                    yield return Capture();
                    SavePictures("edge" + (int)e + "-natural");
                    NaturalMode(false);
                }
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
                // The relative error, with the expected value floored at 1e-4: the target has 16 bits (1.5e-5 a step), so a factor below
                // that is a black pixel whatever its exact value (tau 20 is 5e-13).
                double err = Math.Abs(mean - want) / Math.Max(want, 1e-4);
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
            small.transform.localScale = new Vector3(12f, 12f, 12f);
            small.transform.position = new Vector3(900f, TerrainHeight(900f, 60f) + 6f, 60f);
            UnityEngine.Object.Destroy(small.GetComponent<Collider>());
            yield return Capture();
            Vector3 vehicleTop = Vehicle.transform.position + Vector3.up * 10f;
            double seaFactor = Patch(new Vector3(-300f, 0f, 0f), 6), land = Patch(new Vector3(300f, TerrainHeight(300f, 0f), 0f), 6);
            double terrain = Patch(new Vector3(850f, TerrainHeight(850f, 0f), 0f), 3);
            double vessel = Patch(vehicleTop, 1), obj = Patch(small.transform.position + Vector3.up * 6f, 1);
            double want1 = Math.Exp(-1.0 / Math.Sin(Math.PI / 4.0));
            Sink.Info("receiver.sea", seaFactor);
            Sink.Info("receiver.land", land);
            Sink.Info("receiver.terrain_at_vessel", terrain);
            Sink.Info("receiver.vessel", vessel);
            Sink.Info("receiver.small_object", obj);
            Sink.Info("receiver.expected", want1);
            double spread = (Math.Max(Math.Max(terrain, vessel), obj) - Math.Min(Math.Min(terrain, vessel), obj)) / ((terrain + vessel + obj) / 3.0);
            // What this measures: the forward colour multiply with the depth reconstructed on surfaces at different heights and normals, the
            // player's sea being a plane and not Scatterer's ocean. The deferred mask and the ocean hook are measured in the game (K-T1).
            // A ratio of 1 everywhere also gives a spread of 0: the first two metrics are meaningful only with shadow_buffer_acts == 1.
            Sink.AtMost("forward.receivers_spread", acts ? spread : 1.0, 0.03);
            Sink.AtMost("forward.sea_land", acts ? Math.Abs(seaFactor - land) / land : 1.0, 0.05);
            WriteCapture("R11-shadow-receivers", "{\"factors\": {\"terrain\": " + N(terrain) + ", \"vessel\": " + N(vessel) + ", \"small_object\": " + N(obj) + "}}");
            WriteCapture("R13-ocean-land", "{\"water\": " + N(seaFactor) + ", \"land\": " + N(land) + "}");
            SavePictures("receivers");

            // ---- the vehicle and the ground in the product's setting: lit by the sky under the shadow, not black ----
            NaturalMode(true);
            yield return Capture();
            SavePictures("receivers-natural");
            double vesselNatural = Patch(vehicleTop, 1);
            Sink.Info("natural.vessel_tau1", vesselNatural);
            NaturalMode(false);

            // ---- R12 on the GPU: the mask with the diffuse floor under a wide cloud against the fitted total transmission (the player's
            // measurement of the R12 'ours' column; the reference is the photon walk of the harness) ----
            frame.DiffuseShare = 1f;
            var r12 = new System.Collections.Generic.List<double>();
            double worstTotal = 0.0;
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
                double want = CloudShadowMath.TotalTransmission(tau / Math.Sin(Math.PI / 4.0), tau);
                double err = Math.Abs(mean - want) / want;
                worstTotal = Math.Max(worstTotal, err);
                r12.Add(mean);
                Sink.Info("total.tau" + tau.ToString(CultureInfo.InvariantCulture), mean);
                Sink.Info("total_expected.tau" + tau.ToString(CultureInfo.InvariantCulture), want);
                Sink.AtMost("R12-gpu-total.tau" + tau.ToString(CultureInfo.InvariantCulture), err, 0.05);
            }
            Sink.Info("R12-gpu-total.worst", worstTotal);
            WriteCapture("R12-shadow-vs-tau", "{\"taus\": " + List(taus) + ", \"factors\": " + List(r12.ToArray()) + "}");

            // ---- the ground under a dense cloud (tau 20): the colour keeps the ambient share, the picture is not black ----
            NaturalMode(true);
            SetCloud(20f, false);
            SetSun(45.0);
            Pose(new CameraPose(new Vector3(-300f, 1500f, 0f), 0f, 90f));
            yield return Capture();
            var landPoint = new Vector3(300f, TerrainHeight(300f, 0f), 0f);
            yield return MeasureShare(landPoint, 6);
            double maskDense = CloudShadowMath.TotalTransmission(20.0 / Math.Sin(Math.PI / 4.0), 20.0);
            double wantDense = ExpectedNatural(maskDense);
            double gotDense = Patch(landPoint, 6);
            Sink.Info("forward.share_measured", measuredShare);
            Sink.Info("forward.share_model", ModelShare(45.0));
            // The land patch slopes about 4 degrees away from the sun and reflects some sky, which the flat-ground formula leaves out: 12 %.
            Sink.AtMost("forward.share_model_vs_measured", Math.Abs(ModelShare(45.0) - measuredShare) / measuredShare, 0.12);
            Sink.Info("forward.dense_ratio", gotDense);
            Sink.Info("forward.dense_expected_measured_share", wantDense);
            // The forward path applies 1 - share (1 - mask) with the model's share: that is what it must reproduce exactly. How far the model's
            // share is from what a material really gives (Unity's Standard shader weights the sun's term by the Disney diffuse factor, which
            // the ambient does not get) is the line forward.share_model_vs_measured above and forward.dense_vs_measured below.
            double wantModel = 1.0 - ModelShare(45.0) * (1.0 - maskDense);
            Sink.Info("forward.dense_expected_model_share", wantModel);
            Sink.AtMost("forward.dense_ambient_kept", acts ? Math.Abs(gotDense - wantModel) / wantModel : 1.0, 0.04);
            Sink.Info("forward.dense_vs_measured", Math.Abs(gotDense - wantDense) / wantDense);
            Sink.AtLeast("forward.dense_not_black", gotDense, 0.3);
            SavePictures("dense-tau20-natural");

            // ---- the deferred path: the same cloud through the sun's shadow mask (needs a shadow-casting sun) ----
            // The sun's screen-space mask exists only inside the shadow distance (the camera is 1500 m up), so the distance is set wide.
            Cam.renderingPath = RenderingPath.DeferredShading;
            QualitySettings.shadows = ShadowQuality.HardOnly;
            QualitySettings.shadowDistance = 5000f;
            QualitySettings.shadowCascades = 2;
            Sun.shadows = LightShadows.Hard;
            rig.Enabled = false;
            yield return Frames(6);
            bool deferredOn = Cam.actualRenderingPath == RenderingPath.DeferredShading;
            Sink.Flag("deferred_active", deferredOn);
            if (deferredOn)
            {
                yield return Capture();
                yield return MeasureShare(landPoint, 6);
                double wantMask = ExpectedNatural(maskDense);
                double gotMask = Patch(landPoint, 6);
                Sink.Info("deferred.share_measured", measuredShare);
                Sink.Info("deferred.dense_ratio", gotMask);
                Sink.Info("deferred.dense_expected", wantMask);
                // A ratio of 1 means the sun's mask was never multiplied (no mask event fired): reported apart from the value check.
                Sink.Flag("deferred_mask_ran", Math.Abs(gotMask - 1.0) > 0.05);
                Sink.AtMost("deferred.dense_ambient_kept", Math.Abs(gotMask - wantMask) / wantMask, 0.10);
                // The two paths differ by the forward path's share model (see above): a line, not a limit.
                Sink.Info("deferred.against_forward", Math.Abs(gotMask - gotDense) / gotDense);
                SavePictures("dense-tau20-deferred");
            }
            Cam.renderingPath = RenderingPath.Forward;
            Sun.shadows = LightShadows.None;
            QualitySettings.shadowDistance = 150f;
            QualitySettings.shadows = ShadowQuality.Disable;
            NaturalMode(false);

            Sink.Flag("shadow_finite", finite);
            rig.Stop();
            renderer.Dispose();
        }

        // The slab of vertical optical depth tau, everywhere or west of longitude 0 only.
        private void SetCloud(float tau, bool westOnly, float heightReachM = 0f)
        {
            var source = new LayerStackSource(0, R, true);
            currentSource = source;
            var def = new CloudLayerDef("slab", 0, SlabBase, SlabTop, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, tau / (SlabTop - SlabBase))
            {
                Coverage = 0.5f,
                EdgeSoftness = 1e-4f,
                // The height variation's reach widens the layer's tight interval (and with it the shadow's step count under the scaled policy).
                HeightDownM = heightReachM,
                HeightUpM = heightReachM
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
            frame.PlanetCenter = new Vector3(0f, (float)-R, 0f);
            frame.RadiusUnits = R;
            frame.MetresPerUnit = 1f;
            frame.WorldToMap = toMap;
            frame.TowardsSun = toSun;
            frame.StepsPerSpan = 12;
            frame.DiscTaps = 8;
            frame.Floor = 0f;
        }

        // The analytic checks measure the pure direct beam through the whole colour (no diffuse floor, no ambient share); the natural
        // pictures and the ambient checks run the product's setting: the diffuse floor in the mask and the sun's share of the colour read from
        // the scene's light.
        private void NaturalMode(bool on)
        {
            frame.DiffuseShare = on ? 1f : 0f;
            rig.AutoForwardShare = on;
            if (!on) frame.ForwardDirectShare = 1f;
        }

        // The share of the sun in the colour the camera really reads at a world point (flat-ish land): the lit picture against the picture with
        // the sun switched off (ambient only), measured, so that the expected ratio of the natural checks does not repeat the formula of the rig.
        private double measuredShare;
        private IEnumerator MeasureShare(Vector3 world, int halfPixels)
        {
            bool was = rig.Enabled;
            rig.Enabled = false;
            yield return Frames(3);
            yield return new WaitForEndOfFrame();
            float[] withSun = Grab();
            float intensity = Sun.intensity;
            Sun.intensity = 0f;
            yield return Frames(3);
            yield return new WaitForEndOfFrame();
            float[] ambientOnly = Grab();
            Sun.intensity = intensity;
            rig.Enabled = was;
            Vector3 s = Cam.WorldToScreenPoint(world);
            int cx = Mathf.RoundToInt(s.x), cy = Mathf.RoundToInt(s.y);
            double a = 0.0, b = 0.0;
            for (int y = cy - halfPixels; y <= cy + halfPixels; y++)
                for (int x = cx - halfPixels; x <= cx + halfPixels; x++)
                {
                    int i = y * Target.width + x;
                    if (x < 0 || y < 0 || x >= Target.width || y >= Target.height) continue;
                    a += withSun[i];
                    b += ambientOnly[i];
                }
            measuredShare = a > 1e-9 ? 1.0 - b / a : 1.0;
        }

        // The ratio the product's setting must give on that surface for a mask value: the colour keeps its ambient share.
        private double ExpectedNatural(double mask)
        {
            return 1.0 - measuredShare * (1.0 - mask);
        }

        // The share the rig's model (CloudShadowMath.DirectShare of the sun and ambient settings) gives, for the same point of view.
        private double ModelShare(double elevationDeg)
        {
            double lum = Sun.intensity * (0.2126 * Sun.color.r + 0.7152 * Sun.color.g + 0.0722 * Sun.color.b);
            Color a = RenderSettings.ambientLight;
            return CloudShadowMath.DirectShare(lum, Math.Sin(elevationDeg * Math.PI / 180.0), 0.2126 * a.r + 0.7152 * a.g + 0.0722 * a.b);
        }

        // Renders the lit ground (no buffer) and the shadowed ground (the renderer's forward buffer on the camera) from the current pose.
        private IEnumerator Capture()
        {
            rig.Enabled = false;
            yield return Frames(4);
            yield return new WaitForEndOfFrame();
            lit = Grab();
            rig.Enabled = true;
            yield return Frames(4);
            yield return new WaitForEndOfFrame();
            shadowed = Grab();
            shadowedColour = grabbedColour;
        }

        private Color[] grabbedColour, shadowedColour;

        private float[] Grab()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            var tex = new Texture2D(Target.width, Target.height, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            Color[] px = tex.GetPixels();
            grabbedColour = px;
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
            foreach (double v in y) { if (double.IsNaN(v)) continue; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            return Math.Abs(Crossing(x, y, lo + 0.9 * (hi - lo)) - Crossing(x, y, lo + 0.1 * (hi - lo)));
        }

        // The natural picture (the shadowed ground) and the aid picture (the factor in false colour: white 1, red 0.5, black 0).
        private void SavePictures(string label)
        {
            int w = Target.width, h = Target.height;
            var natural = new Texture2D(w, h, TextureFormat.RGB24, false);
            var aid = new Texture2D(w, h, TextureFormat.RGB24, false);
            // The natural picture is shown at a fixed gain per capture: the mean of the lit picture goes to 0.5 (the player's fake light is dim).
            double meanLit = 0.0;
            for (int i = 0; i < lit.Length; i++) meanLit += lit[i];
            meanLit /= Math.Max(1, lit.Length);
            float gain = (float)Mathf.Clamp(0.5f / Mathf.Max((float)meanLit, 1e-3f), 1f, 8f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    Color c = shadowedColour[i];
                    natural.SetPixel(x, y, new Color(Mathf.Clamp01(c.r * gain), Mathf.Clamp01(c.g * gain), Mathf.Clamp01(c.b * gain)));
                    aid.SetPixel(x, y, AidColour(Ratio(x, y)));
                }
            // The scale of the aid picture: the bottom strip runs from a factor of 0.001 (left) to 1 (right); ticks at 0.001, 0.01, 0.1 and 1.
            for (int x = 0; x < w / 2; x++)
            {
                float v = x / (float)(w / 2 - 1);
                bool tick = Mathf.Abs(v * 3f - Mathf.Round(v * 3f)) < 0.004f;
                for (int y = 0; y < 24; y++)
                    aid.SetPixel(x, y, tick ? Color.cyan : AidColour(Mathf.Pow(10f, (v - 1f) * 3f)));
            }
            File.WriteAllBytes(Path.Combine(Output, "shadow-" + label + ".png"), natural.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(Output, "shadow-" + label + "-aid.png"), aid.EncodeToPNG());
            UnityEngine.Object.Destroy(natural);
            UnityEngine.Object.Destroy(aid);
        }

        // The shadow factor in false colour on a log scale (white 1, red 0.03, black 0.001 and less); NaN (no lit reference) is blue.
        private static Color AidColour(float ratio)
        {
            if (float.IsNaN(ratio)) return Color.blue;
            float v = Mathf.Clamp01(1f + Mathf.Log10(Mathf.Max(ratio, 1e-3f)) / 3f);
            return v > 0.5f ? Color.Lerp(Color.red, Color.white, (v - 0.5f) * 2f) : Color.Lerp(Color.black, Color.red, v * 2f);
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
