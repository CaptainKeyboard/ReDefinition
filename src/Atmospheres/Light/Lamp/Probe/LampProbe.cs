using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The probe of package D32 inside the integrated series K-W3b (start "lamp"): the lamp scatter against the running game. The
    // start needs the full mod set (EVE, Scatterer, Deferred, TUFX): RECON found the frame frozen with EVE present and Scatterer
    // absent, so no frame row means anything in that set (the row D32.scatterer_present is required).
    //
    // Steps and rows (all in tests/series/rows/D32.json):
    //   1. Lamps off. The craft's lights are switched off through KSPActionGroup.Light, and in forced snow nothing is drawn:
    //      D32.lights_off_candidates, D32.lights_off_draws (the control of the next step). Clear air with the lamps lit:
    //      D32.lit_candidates (at least one lit lamp), D32.clear_air_draws, D32.clear_air_buffers, D32.clear_air_sigma.
    //   2. Snow forced on a disc round the vessel, the lamps lit; the variants all / fog / snow / cloud / dust / none switch each
    //      medium source on its own and report its sigma at the first lamp. The cloud variant adds the test floor
    //      lamp.forceCloudDensity (0.8): the chain from a cloud density to a sigma to a drawn beam, with the camera inside it too.
    //      A real EVE cloud is not flown into: the craft stands on the ground or flies the series' own course, and a scripted climb
    //      to a layer is not reliable; D32.cloud_density_max reports what the real layers give round the vessel.
    //   3. Frame rows, where a frame source follows the scene (D32.frame_live, a white wall in front of the camera must move the
    //      luma): the camera is put to a side view of the first lamp's beam, the luma at eight points along the beam is read with the
    //      scatter on, off and off again (settings lightInMedium 1, 0, 0). D32.beam_delta_snow is on minus off, D32.beam_noise_snow the
    //      difference of the two off frames, D32.beam_control (variant none: no source, scatter on against off) is the artefact of
    //      the switch itself; D32.beam_signal_ratio is the beam over the larger of control and noise. The same in the cloud variant
    //      (D32.cloud_beam_delta). A white wall between camera and beam hides the points behind it: D32.wall_hidden_share.
    //   4. Under an upscaler (D32.upscaler_active), read from the screen (the picture after it, interface hidden; D32.smear_source_screen):
    //      the camera turns by 10 degrees at 20 degrees per second; the error of the frames
    //      after the turn against the converged frame, with the beam and without it, gives D32.smear_frames_dlss_20degps (frames until the
    //      beam's extra error is under a quarter of its own contribution) and D32.ghost_vs_baseline (the tail error ratio). The
    //      frame-generation duplicates are measured by the replay of the proxy's dump (tools/replay_fg.ps1, tools/replay_measure.py)
    //      and are not a probe row.
    //   5. IVA: with a crew the camera enters the IVA and no lamp of the own vessel is drawn (D32.iva_draws).
    //   6. The CPU milliseconds of the module's frame work and the depth copy's spread.
    // Whatever the probe changes (the lights, the camera, the wall) is put back in the finally block.
    [AtmospheresModule(986)]
    internal sealed class LampProbe : SeriesProbe
    {
        internal const string SeriesId = "K-W3b";
        private static readonly string[] Sources = { "fog", "precip", "cloud", "dust" };

        // The test floor of the cloud variant, the density of a thick EVE deck.
        private const float ForcedCloud = 0.8f;
        private const int PointCount = 8;
        private const double PointFrom = 0.15, PointTo = 0.75;
        private const double BoxSize = 0.012;
        private const double TurnDegreesPerSecond = 20.0;
        private const double TurnSeconds = 0.5;
        private const int TailFrames = 10;

        // The noise of the frame reading in luma: the smallest extra error that counts as a smear (the beam_noise_snow row's limit).
        private const double SmearNoiseFloor = 0.003;

        protected override string SeriesName
        {
            get { return SeriesId; }
        }

        protected override double MaxSeconds
        {
            get { return 15 * 60; }
        }

        protected override IList<string> DefaultVariants
        {
            get { return new[] { "all", "fog", "snow", "cloud", "dust", "none" }; }
        }

        protected override void ApplyVariant(string name)
        {
            string only = name == "snow" ? "precip" : name;
            foreach (string source in Sources)
            {
                bool on = name == "all" || source == only;
                ForceTuning("lamp.src." + source, on ? 1f : 0f);
            }
            ForceTuning("lamp.forceCloudDensity", name == "cloud" ? ForcedCloud : 0f);
        }

        protected override void RevertVariant(string name)
        {
            foreach (string source in Sources) ForceTuning("lamp.src." + source, 1f);
            ForceTuning("lamp.forceCloudDensity", 0f);
        }

        private static string Num(double v)
        {
            return v.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        // Whether an assembly whose name contains the fragment is loaded.
        private static bool Loaded(string fragment)
        {
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n != null && n.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ---- state put back at the end ----
        private Vessel lightsVessel;
        private bool lightsWere;
        private bool cameraSaved;
        private float savedHeading, savedPitch, savedDistance, savedMinDistance, savedMaxDistance;
        private bool uiHidden;
        private ReconHost host;
        private ReconBall wall;
        private Material wallMaterial;
        private Action wallFollow, turnFollow;
        private readonly List<Action> hooks = new List<Action>();
        private IWeatherField fieldBefore;
        private ForcedField snowField;

        // The level and temperature of the snow the probe forces: heavy enough for the beam, cold enough to stay snow by temperature.
        private const float SnowLevel = 0.8f, SnowTemperatureK = 255f;

        // Installs a ForcedField over a constant snow field as WeatherHub.Field; true when the field now answers with snow.
        private bool InstallSnow()
        {
            fieldBefore = WeatherHub.Field;
            var constant = new ConstantWeatherField { Level01 = SnowLevel, Type = PrecipType.Snow, TemperatureK = SnowTemperatureK };
            snowField = new ForcedField(constant) { Level01 = SnowLevel, Type = PrecipType.Snow };
            WeatherHub.Field = snowField;
            WeatherSample sample;
            return WeatherHub.Sample(0, 0.0, 0.0, 0.0, 0.0, out sample) && sample.Level01 >= SnowLevel * 0.99f && sample.Type == PrecipType.Snow;
        }

        private void Restore()
        {
            try
            {
                if (snowField != null)
                {
                    WeatherHub.Field = fieldBefore;
                    snowField.Detach();
                    snowField = null;
                }
                if (host != null)
                {
                    for (int i = 0; i < hooks.Count; i++) host.PerFrame.Remove(hooks[i]);
                    hooks.Clear();
                }
                RemoveWall();
                if (host != null)
                {
                    ReconHost.Release();
                    host = null;
                }
                if (uiHidden)
                {
                    GameEvents.onShowUI.Fire();
                    uiHidden = false;
                }
                // The flight camera first (the mode change resets it), then its own state.
                if (CameraManager.Instance != null && CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight)
                {
                    CameraManager.Instance.SetCameraFlight();
                }
                if (cameraSaved && FlightCamera.fetch != null)
                {
                    FlightCamera.fetch.minDistance = savedMinDistance;
                    FlightCamera.fetch.maxDistance = savedMaxDistance;
                    FlightCamera.fetch.camHdg = savedHeading;
                    FlightCamera.fetch.camPitch = savedPitch;
                    FlightCamera.fetch.SetDistanceImmediate(savedDistance);
                }
                if (lightsVessel != null && lightsVessel.ActionGroups != null)
                {
                    lightsVessel.ActionGroups.SetGroup(KSPActionGroup.Light, lightsWere);
                }
            }
            catch (Exception e)
            {
                LogLine("D32: the probe could not put everything back (" + e.GetType().Name + " " + e.Message + ")");
            }
            DestroyTestLamp();
            ClearForced();
        }

        private void RemoveWall()
        {
            if (wallFollow != null && host != null) host.PerFrame.Remove(wallFollow);
            wallFollow = null;
            if (wall != null) wall.Destroy();
            wall = null;
            if (wallMaterial != null) UnityEngine.Object.Destroy(wallMaterial);
            wallMaterial = null;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            try
            {
                IEnumerator<ProbeWait> inner = Run();
                while (inner.MoveNext()) yield return inner.Current;
            }
            finally
            {
                Restore();
            }
        }

        // ---- the probe's own lamp ----
        // What is representative: the module reads every Light under a part of a loaded vessel (LampLights), scores them and draws the lamp with the most horizontal axis
        // first (LampScatter side lamp). A spot light of this size and angle is what a stock spotlight part gives the module (a spot, a few tens of metres, wide cone) [VERIFY the
        // stock part's numbers]; the pad vessel's own lamps are down lights with a 3 m reach, which the module draws correctly but which show no beam from the side. The
        // test lamp is a child of the root part, lies horizontal along the local north, follows the light action group like a real lamp, and is destroyed in Restore.
        private const float TestLampRangeM = 60f, TestLampSpotAngleDeg = 60f, TestLampIntensity = 1.5f, TestLampLiftM = 1.5f;
        private GameObject testLampObject;
        private Light testLamp;

        private void CreateTestLamp(Vessel vessel)
        {
            Part root = vessel.rootPart;
            if (root == null) return;
            Vector3 up = ((Vector3)vessel.CoMD - (Vector3)vessel.mainBody.position).normalized;
            Vector3 forward = Vector3.ProjectOnPlane((Vector3)vessel.north, up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(Vector3.forward, up);
            forward.Normalize();
            testLampObject = new GameObject("ReDefinition-D32-probe-lamp");
            testLampObject.transform.SetParent(root.transform, true);
            testLampObject.transform.position = (Vector3)vessel.CoMD + up * TestLampLiftM;
            testLampObject.transform.rotation = Quaternion.LookRotation(forward, up);
            testLamp = testLampObject.AddComponent<Light>();
            testLamp.type = LightType.Spot;
            testLamp.range = TestLampRangeM;
            testLamp.spotAngle = TestLampSpotAngleDeg;
            testLamp.intensity = TestLampIntensity;
            testLamp.color = new Color(1f, 0.95f, 0.85f);
            testLamp.shadows = LightShadows.None;
            testLamp.enabled = false;
            LogLine("D32: the probe added a spot lamp of its own: range " + TestLampRangeM.ToString("0", CultureInfo.InvariantCulture) + " m, cone "
                    + TestLampSpotAngleDeg.ToString("0", CultureInfo.InvariantCulture) + " degrees, horizontal along the local north, "
                    + TestLampLiftM.ToString("0.0", CultureInfo.InvariantCulture) + " m above the centre of mass");
        }

        // The craft's lights and the probe's lamp together (the lamp follows the light action group like a real one).
        private void SetLights(Vessel vessel, bool on)
        {
            vessel.ActionGroups.SetGroup(KSPActionGroup.Light, on);
            if (testLamp != null) testLamp.enabled = on;
        }

        private void DestroyTestLamp()
        {
            if (testLampObject != null) UnityEngine.Object.Destroy(testLampObject);
            testLampObject = null;
            testLamp = null;
        }

        // ---- frame reading ----
        private ReconFrame frame;
        private double[] pointU, pointV;
        private Vector3[] pointWorld;
        private int pointsInView;

        private IEnumerable<ProbeWait> Capture()
        {
            host.Read();
            double end = Time.realtimeSinceStartup + 10.0;
            while (host.Reading && Time.realtimeSinceStartup < end) yield return ProbeWait.Frames(1);
            frame = host.Reading ? null : host.Frame;
        }

        // The luma of the frame round each beam point, in the order of the points; NaN where the frame is missing or the point is out
        // of view.
        private double[] ReadPoints()
        {
            var values = new double[PointCount];
            for (int i = 0; i < PointCount; i++)
            {
                if (frame == null || !LampProbeMath.InView(pointU[i], pointV[i], 1.0, 1.0, 0.03))
                {
                    values[i] = double.NaN;
                    continue;
                }
                values[i] = LampProbeMath.Mean(ToDoubles(frame.Lumas(pointU[i], pointV[i], BoxSize, BoxSize)));
            }
            return values;
        }

        private static List<double> ToDoubles(List<float> list)
        {
            var d = new List<double>(list.Count);
            for (int i = 0; i < list.Count; i++) d.Add(list[i]);
            return d;
        }

        // The mean over the points that are valid in every given array (and in the mask when it is not null).
        private static double MeanOver(double[] values, bool[] mask)
        {
            var list = new List<double>();
            for (int i = 0; i < values.Length; i++)
            {
                if (double.IsNaN(values[i])) continue;
                if (mask != null && !mask[i]) continue;
                list.Add(values[i]);
            }
            return LampProbeMath.Mean(list);
        }

        private static double[] Minus(double[] a, double[] b)
        {
            var d = new double[a.Length];
            for (int i = 0; i < a.Length; i++) d[i] = a[i] - b[i];
            return d;
        }

        // Puts the screen positions of the beam's points (fractions of the frame) into pointU and pointV for the camera as it is now.
        private void ProjectPoints(Camera cam)
        {
            pointsInView = 0;
            for (int i = 0; i < PointCount; i++)
            {
                Vector3 s = cam.WorldToScreenPoint(pointWorld[i]);
                pointU[i] = s.z > 0f ? s.x / cam.pixelWidth : -1.0;
                pointV[i] = s.z > 0f ? s.y / cam.pixelHeight : -1.0;
                if (LampProbeMath.InView(pointU[i], pointV[i], 1.0, 1.0, 0.03)) pointsInView++;
            }
        }

        // The beam's points in the world, from the first drawn lamp (valid while a lamp is drawn).
        private void CapturePoints(Vessel vessel)
        {
            pointWorld = new Vector3[PointCount];
            pointU = new double[PointCount];
            pointV = new double[PointCount];
            Vector3 lamp = LampScatter.SideLampPosition, axis = LampScatter.SideLampAxis.normalized;
            // The beam is seen as far as the visibility the medium gives (about 4 / sigma) and not to the lamp's whole range; a long
            // beam is also held to BeamReachCapM, so that the camera that sees all of it stays near the vessel.
            float range = Mathf.Min(Mathf.Min(LampScatter.SideLampRange, 4f / Mathf.Max(LampScatter.LastSigma.Total, 0.001f)), BeamReachCapM);
            for (int i = 0; i < PointCount; i++)
            {
                double fx, fy;
                LampProbeMath.SegmentPoint(0, 0, 1, 0, i, PointCount, PointFrom, PointTo, out fx, out fy);
                pointWorld[i] = lamp + axis * (range * (float)fx);
            }
            // The lamp as the probe aims at it, for the log: the premise of every frame row below is that the beam can be seen.
            Vector3 up = (lamp - (Vector3)vessel.mainBody.position).normalized;
            Vector3 fromCoM = lamp - (Vector3)vessel.CoMD;
            double elevation = LampProbeMath.ElevationDeg(axis.x, axis.y, axis.z, up.x, up.y, up.z);
            LogLine("D32: the lamp the probe aims at: " + fromCoM.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m from the vessel's centre of mass, axis "
                    + elevation.ToString("0", CultureInfo.InvariantCulture) + " degrees above the horizontal, range "
                    + LampScatter.SideLampRange.ToString("0.0", CultureInfo.InvariantCulture) + " m, half angle "
                    + (LampScatter.SideLampHalfAngleRad * Mathf.Rad2Deg).ToString("0", CultureInfo.InvariantCulture) + " degrees, beam points to "
                    + range.ToString("0.0", CultureInfo.InvariantCulture) + " m");
            lastElevation = elevation;
            lampIsProbeLamp = testLamp != null && (lamp - testLamp.transform.position).magnitude < 0.5f;
            Emit("D32.beam_axis_elevation_deg", elevation);
            Emit("D32.beam_lamp_is_probe_lamp", lampIsProbeLamp);
            Emit("D32.beam_reach_m", range);
        }

        // The elevation of the aimed-at lamp's axis and whether that lamp is the probe's own (set by CapturePoints).
        private double lastElevation = double.NaN;
        private bool lampIsProbeLamp;
        // A beam whose axis is steeper than this is seen end-on or not at all from the side: the frame rows are no measurement of a beam.
        private const double MaxAxisElevationDeg = 45.0;

        // The beam is held to this length for the points, in metres.
        private const float BeamReachCapM = 40f;
        // The camera placements tried, nearest first: distance from the vessel (m), pitch (degrees), and eight headings round it.
        private static readonly float[] CameraDistancesM = { 30f, 45f, 70f, 110f };
        private static readonly float[] CameraPitchesDeg = { 10f, 35f, -10f };
        private const int CameraHeadings = 8;
        // A beam that spans at least this share of the frame diagonal with every point in view is good enough to stop the search.
        private const double GoodSpan = 0.25;

        // Puts the camera where the whole beam is in view: tries distances from near to far, three pitches and eight headings, projects
        // the beam's points for each, and keeps the first placement that shows every point over a good span, else the one with the
        // longest span that shows at least PointCount - 1 points. When none does the premise of the frame rows is false: the probe fails
        // (Check) and says which placements were tried.
        private IEnumerable<ProbeWait> PlaceCamera()
        {
            FlightCamera fc = FlightCamera.fetch;
            Camera cam = fc.mainCamera;
            fc.maxDistance = Mathf.Max(fc.maxDistance, CameraDistancesM[CameraDistancesM.Length - 1] + 20f);
            fc.minDistance = Mathf.Min(fc.minDistance, 20f);
            var tried = new List<float[]>();
            var inViewList = new List<int>();
            var spanList = new List<double>();
            int pick = -1;
            int mostInView = 0;
            for (int d = 0; d < CameraDistancesM.Length && !(pick >= 0 && spanList[pick] >= GoodSpan); d++)
            {
                for (int p = 0; p < CameraPitchesDeg.Length && !(pick >= 0 && spanList[pick] >= GoodSpan); p++)
                {
                    for (int k = 0; k < CameraHeadings; k++)
                    {
                        float heading = (float)(k * 2.0 * Math.PI / CameraHeadings);
                        fc.SetDistanceImmediate(CameraDistancesM[d]);
                        fc.camPitch = CameraPitchesDeg[p] * Mathf.Deg2Rad;
                        fc.camHdg = heading;
                        yield return ProbeWait.Frames(3);
                        ProjectPoints(cam);
                        double span = Math.Sqrt(Math.Pow(pointU[0] - pointU[PointCount - 1], 2) + Math.Pow(pointV[0] - pointV[PointCount - 1], 2));
                        tried.Add(new[] { CameraDistancesM[d], CameraPitchesDeg[p], heading });
                        inViewList.Add(pointsInView);
                        spanList.Add(span);
                        mostInView = Math.Max(mostInView, pointsInView);
                        pick = LampProbeMath.ChoosePlacement(inViewList, spanList, PointCount, GoodSpan);
                        if (pick >= 0 && spanList[pick] >= GoodSpan) break;
                    }
                }
            }
            if (pick < 0) pick = LampProbeMath.ChoosePlacement(inViewList, spanList, PointCount - 1, GoodSpan);
            if (pick >= 0)
            {
                fc.SetDistanceImmediate(tried[pick][0]);
                fc.camPitch = tried[pick][1] * Mathf.Deg2Rad;
                fc.camHdg = tried[pick][2];
                yield return ProbeWait.Seconds(1);
                ProjectPoints(cam);
            }
            Emit("D32.beam_points_in_view", pointsInView);
            Emit("D32.beam_screen_span", pick >= 0 ? Math.Max(spanList[pick], 0.0) : 0.0);
            LogLine("D32: camera placements tried " + tried.Count + ", the most beam points in view in any " + mostInView + " of " + PointCount
                    + (pick >= 0 ? "; kept distance " + tried[pick][0].ToString("0", CultureInfo.InvariantCulture) + " m, pitch "
                                   + tried[pick][1].ToString("0", CultureInfo.InvariantCulture) + " degrees, heading "
                                   + (tried[pick][2] * Mathf.Rad2Deg).ToString("0", CultureInfo.InvariantCulture) + " degrees" : "; none shows the beam"));
        }

        // One sample: the per-point lumas of a frame read now.
        private double[] lastSample;

        private IEnumerable<ProbeWait> Sample()
        {
            foreach (ProbeWait w in Capture()) yield return w;
            lastSample = ReadPoints();
            lastPixels = frame != null ? frame.Pixels : null;
            lastPixelsW = frame != null ? frame.Width : 0;
            lastPixelsH = frame != null ? frame.Height : 0;
        }

        // The last pair of the on / off frames, for the picture sheet of the beam: the natural view with the scatter on and the
        // envelope of the two scatter-off frames (the heat map shows what the scatter adds beyond what moves by itself).
        private Color[] lastPixels, picOn;
        private int lastPixelsW, lastPixelsH, picW, picH;
        private float[] picEnvelope;

        // Saves the sheet of the last measured pair under the name (T1: the beam). A frame that could not be read gives no sheet.
        private void SaveBeamSheet(string name)
        {
            if (picOn != null && picEnvelope != null) SeriesPictures.SaveSheet("D32-beam-" + name, picOn, picEnvelope, picW, picH);
        }

        // Scatter on, off, off again, repeated: the beam's mean added luma and the difference of the two off frames.
        private double abDelta, abNoise;
        private double[] abOn, abOff;

        private IEnumerable<ProbeWait> MeasureOnOff(int pairs)
        {
            var on = new List<double[]>();
            var off = new List<double[]>();
            var off2 = new List<double[]>();
            for (int p = 0; p < pairs; p++)
            {
                SetSetting(AtmospheresSettings.Keys.LightInMedium, 1.0);
                yield return ProbeWait.Seconds(1.5);
                foreach (ProbeWait w in Sample()) yield return w;
                on.Add(lastSample);
                Color[] onPixels = lastPixels;
                int onW = lastPixelsW, onH = lastPixelsH;
                SetSetting(AtmospheresSettings.Keys.LightInMedium, 0.0);
                yield return ProbeWait.Seconds(1.0);
                foreach (ProbeWait w in Sample()) yield return w;
                off.Add(lastSample);
                float[] offLuma = lastPixels != null ? SeriesPictures.Luma(lastPixels) : null;
                yield return ProbeWait.Frames(20);
                foreach (ProbeWait w in Sample()) yield return w;
                off2.Add(lastSample);
                if (onPixels != null && offLuma != null && lastPixels != null && lastPixels.Length == onPixels.Length && offLuma.Length == onPixels.Length)
                {
                    float[] off2Luma = SeriesPictures.Luma(lastPixels);
                    picEnvelope = new float[offLuma.Length];
                    for (int i = 0; i < picEnvelope.Length; i++) picEnvelope[i] = Math.Max(offLuma[i], off2Luma[i]);
                    picOn = onPixels;
                    picW = onW;
                    picH = onH;
                }
            }
            SetSetting(AtmospheresSettings.Keys.LightInMedium, 1.0);
            abOn = AverageSamples(on);
            abOff = AverageSamples(off);
            var offB = AverageSamples(off2);
            abDelta = MeanOver(Minus(abOn, abOff), null);
            var noise = new double[PointCount];
            for (int i = 0; i < PointCount; i++) noise[i] = Math.Abs(abOff[i] - offB[i]);
            abNoise = MeanOver(noise, null);
        }

        private static double[] AverageSamples(List<double[]> samples)
        {
            var mean = new double[PointCount];
            for (int i = 0; i < PointCount; i++)
            {
                var list = new List<double>();
                for (int s = 0; s < samples.Count; s++) if (!double.IsNaN(samples[s][i])) list.Add(samples[s][i]);
                mean[i] = LampProbeMath.Mean(list);
            }
            return mean;
        }

        // Whether a white wall in front of the camera moves the luma at the frame's centre (target first, then the screen).
        private IEnumerable<ProbeWait> FrameLiveness(Vessel vessel, Action<bool> done)
        {
            Camera cam = FlightCamera.fetch.mainCamera;
            Shader flat = Shader.Find("Hidden/Internal-Colored");
            if (flat == null)
            {
                done(false);
                yield break;
            }
            var without = new double[2];
            var with = new double[2];
            for (int mode = 0; mode < 2; mode++)
            {
                host.UseScreen = mode == 1;
                foreach (ProbeWait w in Capture()) yield return w;
                without[mode] = frame != null ? LampProbeMath.Mean(ToDoubles(frame.Lumas(0.5, 0.5, 0.04, 0.04))) : double.NaN;
            }
            // The wall fields are also what Restore removes, so that an abort in this step leaves no ball in the scene.
            wallMaterial = ReconMesh.Flat(Color.white, 2000);
            wall = new ReconBall(vessel, (Vector3d)cam.transform.position + (Vector3d)cam.transform.forward * 4.0 - vessel.CoMD, 3f, wallMaterial, 0);
            wallFollow = wall.Update;
            host.PerFrame.Add(wallFollow);
            yield return ProbeWait.Frames(10);
            for (int mode = 0; mode < 2; mode++)
            {
                host.UseScreen = mode == 1;
                foreach (ProbeWait w in Capture()) yield return w;
                with[mode] = frame != null ? LampProbeMath.Mean(ToDoubles(frame.Lumas(0.5, 0.5, 0.04, 0.04))) : double.NaN;
            }
            RemoveWall();
            liveTarget = Math.Abs(with[0] - without[0]) > 0.03;
            liveScreen = Math.Abs(with[1] - without[1]) > 0.03;
            host.UseScreen = !liveTarget && liveScreen;
            done(liveTarget || liveScreen);
        }

        // Which frame sources followed the scene: the camera's target (before the upscaler) and the screen (after it).
        private bool liveTarget, liveScreen;

        // The wall: an opaque white ball near the camera, on the line to the middle of the beam, covering the points whose direction is
        // within 0.8 of its angular radius. Returns by mask which points are behind it.
        private bool[] PlaceWall(Vessel vessel, Camera cam)
        {
            Vector3 camPos = cam.transform.position;
            Vector3 middle = pointWorld[PointCount / 2];
            Vector3 toMiddle = (middle - camPos).normalized;
            const float distance = 3f, radius = 1.2f;
            Vector3 at = camPos + toMiddle * distance;
            // A mid grey, not white: a white wall saturates an 8-bit frame and a beam that shows through it could not raise it.
            wallMaterial = ReconMesh.Flat(new Color(0.25f, 0.25f, 0.25f), 2000);
            wall = new ReconBall(vessel, (Vector3d)at - vessel.CoMD, radius, wallMaterial, 0);
            wallFollow = wall.Update;
            host.PerFrame.Add(wallFollow);
            double halfAngle = Math.Atan2(radius, distance);
            var mask = new bool[PointCount];
            for (int i = 0; i < PointCount; i++)
            {
                double angle = Math.Acos(Mathf.Clamp(Vector3.Dot(toMiddle, (pointWorld[i] - camPos).normalized), -1f, 1f));
                mask[i] = angle < 0.8 * halfAngle;
            }
            return mask;
        }

        private static int Count(bool[] mask)
        {
            int n = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) n++;
            return n;
        }

        // The turn: the camera's heading advances by 20 degrees per second for half a second.
        private IEnumerable<ProbeWait> Turn(float startHeading)
        {
            FlightCamera fc = FlightCamera.fetch;
            fc.camHdg = startHeading;
            float turned = 0f, target = (float)(TurnDegreesPerSecond * TurnSeconds) * Mathf.Deg2Rad;
            turnFollow = () =>
            {
                if (turned >= target) return;
                float step = Mathf.Min((float)TurnDegreesPerSecond * Mathf.Deg2Rad * Time.unscaledDeltaTime, target - turned);
                fc.camHdg += step;
                turned += step;
            };
            host.PerFrame.Add(turnFollow);
            hooks.Add(turnFollow);
            while (turned < target) yield return ProbeWait.Frames(1);
            host.PerFrame.Remove(turnFollow);
            hooks.Remove(turnFollow);
        }

        // The errors of the TailFrames frames after a turn against the converged frame at the final heading, with the scatter as set.
        private double[] tailError;
        private double[] convergedPoints;
        private int tailStrideFrames;

        private IEnumerable<ProbeWait> TurnAndTail(float startHeading)
        {
            FlightCamera fc = FlightCamera.fetch;
            Camera cam = fc.mainCamera;
            fc.camHdg = startHeading;
            yield return ProbeWait.Frames(60);
            foreach (ProbeWait w in Turn(startHeading)) yield return w;
            // The beam's points on the screen at the final heading, which is also the heading of the converged frame.
            ProjectPoints(cam);
            var frames = new List<double[]>();
            int first = Time.frameCount;
            for (int i = 0; i < TailFrames; i++)
            {
                foreach (ProbeWait w in Sample()) yield return w;
                frames.Add(lastSample);
            }
            tailStrideFrames = Math.Max(1, (Time.frameCount - first) / TailFrames);
            yield return ProbeWait.Frames(120);
            foreach (ProbeWait w in Sample()) yield return w;
            convergedPoints = lastSample;
            tailError = new double[TailFrames];
            for (int i = 0; i < TailFrames; i++)
            {
                var d = new double[PointCount];
                for (int p = 0; p < PointCount; p++) d[p] = Math.Abs(frames[i][p] - convergedPoints[p]);
                tailError[i] = MeanOver(d, null);
            }
        }

        // ---- the scenario ----
        private IEnumerator<ProbeWait> Run()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("D32: no flight to measure in");
                yield break;
            }
            SetSetting(AtmospheresSettings.Keys.Enabled, 1.0);
            SetSetting(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            SetSetting(AtmospheresSettings.Keys.LightInMedium, 1.0);
            SetSetting(AtmospheresSettings.Keys.LightInMediumStrength, 1.0);
            SetSetting(AtmospheresSettings.Keys.LightCount, 4.0);
            Emit("D32.scatterer_present", Loaded("scatterer"));
            Emit("D32.eve_present", Loaded("EVEManager") || Loaded("EVE"));
            Emit("D32.deferred_present", Loaded("Deferred"));
            Emit("D32.tufx_present", Loaded("TUFX"));
            yield return ProbeWait.Until(() => LampScatter.Instance != null && LampScatter.KnownLights > 0, 150, "lamps on the vessels");
            Emit("D32.lamps_known", LampScatter.KnownLights);
            if (LampScatter.Instance == null || LampScatter.KnownLights == 0)
            {
                Fail("D32: no lamp was found (the series craft needs lights, and the module needs its shader)");
                yield break;
            }
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            FlightCamera fc = FlightCamera.fetch;
            if (fc != null)
            {
                cameraSaved = true;
                savedHeading = fc.camHdg;
                savedPitch = fc.camPitch;
                savedDistance = fc.Distance;
                savedMinDistance = fc.minDistance;
                savedMaxDistance = fc.maxDistance;
            }
            host = ReconHost.Get();

            // 1. The craft's lights off: no lit candidate, nothing drawn (also in snow, below).
            lightsVessel = vessel;
            lightsWere = vessel.ActionGroups[KSPActionGroup.Light];
            Emit("D32.lights_initially_on", lightsWere);
            // The pad vessel's own lamps point straight down (K-W3 repeat: axis -88 degrees, reach 3 m), so no beam is seen from the side. The probe adds one spot lamp of its own.
            CreateTestLamp(vessel);
            SetLights(vessel, false);
            yield return ProbeWait.Seconds(4);
            Emit("D32.lights_off_candidates", LampScatter.LitCandidates);

            // Lights on, clear air: lit candidates exist, nothing is drawn, nothing is attached.
            SetLights(vessel, true);
            yield return ProbeWait.Seconds(4);
            Emit("D32.lit_candidates", LampScatter.LitCandidates);
            Emit("D32.clear_air_draws", LampScatter.LastDrawCount);
            Emit("D32.clear_air_buffers", LampScatter.BuffersAttached);
            Emit("D32.clear_air_sigma", LampScatter.LastSigma.Total);

            // 2. Snow round the vessel. The lights go off first: snow without a lit lamp draws nothing.
            SetLights(vessel, false);
            // The game's field takes no forcing (nothing in the game installs one), so the probe installs a ForcedField: constant snow
            // at -18 C everywhere, level SnowLevel; Restore puts the game's own field back.
            bool forced = InstallSnow();
            Emit("D32.snow_forced", forced);
            if (forced)
            {
                yield return ProbeWait.Until(() => WeatherHooks.View.Active && WeatherHooks.View.Level > 0.25f, 90, "snow at the vessel");
            }
            else
            {
                Fail("D32: the snow field could not be installed");
            }
            Emit("D32.snow_level", WeatherHooks.View.Level);
            yield return ProbeWait.Seconds(4);
            Emit("D32.lights_off_draws", LampScatter.LastDrawCount);
            SetLights(vessel, true);
            yield return ProbeWait.Until(() => LampScatter.LitCandidates > 0, 20, "a lit lamp");

            bool live = false;
            bool liveChecked = false;
            bool geometry = false;
            double deltaSnow = double.NaN, noiseSnow = double.NaN, controlDelta = double.NaN;
            double[] openDelta = null;
            foreach (string name in new List<string>(Variants))
            {
                BeginVariant(name);
                // The slew of sigma and the fade of the lamps settle.
                yield return ProbeWait.Seconds(5);
                LampSigma s = LampScatter.LastSigma;
                if (name == "all" || name == "fog") Emit("D32.light_sigma_fog", s.Fog);
                if (name == "all" || name == "snow") Emit("D32.light_sigma_snow", s.Precip);
                if (name == "all" || name == "cloud") Emit("D32.light_sigma_cloud", s.Cloud);
                if (name == "all" || name == "dust") Emit("D32.light_sigma_dust", s.Dust);
                Emit("D32.light_sigma_total", s.Total);
                Emit("D32.variant_draws", LampScatter.LastDrawCount);
                if (name == "cloud")
                {
                    Emit("D32.cloud_forced_density", ForcedCloud);
                    Emit("D32.cloud_sigma_expected", LampMath.CloudSigma(ForcedCloud, LampTuning.Defaults().CloudSigmaFull));
                    Emit("D32.cloud_camera_sigma", LampScatter.LastSigmaCamera);
                }
                if (name == "all")
                {
                    Emit("D32.snow_draws", LampScatter.LastDrawCount);
                    Emit("D32.snow_buffers", LampScatter.BuffersAttached);
                    int before = LampScatter.DepthCopies;
                    yield return ProbeWait.Frames(30);
                    Emit("D32.snow_depth_copies", LampScatter.DepthCopies - before);
                    Emit("D32.mode_scene", LampScatter.ActiveMode == WeatherCompose.Scene);
                    // Only after a draw with a depth copy: before one the spread is -1 and says nothing about the depth source.
                    if (LampScatter.DepthCopies - before > 0 && LampScatter.LastDrawCount > 0)
                        Emit("D32.depth_copy_varies", LampScatter.Instance.ProbeDepthSpread());
                    Emit("D32.cpu_ms", LampScatter.CpuMs);
                    if (LampScatter.LastDrawCount > 0 && host != null)
                    {
                        CapturePoints(vessel);
                        foreach (ProbeWait w in PlaceCamera()) yield return w;
                        geometry = pointsInView >= PointCount - 1;
                        // The premise of every frame row: a camera sees the beam. Without it the probe fails and says so.
                        Check("D32.beam_geometry", geometry, pointsInView + " of " + PointCount + " beam points in view");
                        // The beam must be one that can be seen: a near-vertical axis (the pad vessel's down lights) or a lamp that is not the probe's own fails the premise.
                        Check("D32.beam_axis_horizontal", Math.Abs(lastElevation) <= MaxAxisElevationDeg, "axis elevation " + lastElevation.ToString("0", CultureInfo.InvariantCulture) + " degrees, limit " + MaxAxisElevationDeg.ToString("0", CultureInfo.InvariantCulture));
                        Check("D32.beam_lamp_is_probe_lamp", lampIsProbeLamp, "the drawn side lamp is " + (lampIsProbeLamp ? "the probe's own" : "another lamp"));
                    }
                    else
                    {
                        Check("D32.beam_geometry", false, "no lamp was drawn in the snow variant (draws " + LampScatter.LastDrawCount + ")");
                    }
                }
                if (geometry && !liveChecked)
                {
                    liveChecked = true;
                    foreach (ProbeWait w in FrameLiveness(vessel, v => live = v)) yield return w;
                    Emit("D32.frame_live", live);
                    Check("D32.frame_live", live, "a wall in front of the camera must move the luma of the frame");
                }
                if (geometry && live && (name == "all" || name == "none" || name == "cloud"))
                {
                    foreach (ProbeWait w in MeasureOnOff(3)) yield return w;
                    SaveBeamSheet(name);
                    if (name == "all")
                    {
                        deltaSnow = abDelta;
                        noiseSnow = abNoise;
                        openDelta = Minus(abOn, abOff);
                        Emit("D32.beam_delta_snow", deltaSnow);
                        Emit("D32.beam_noise_snow", noiseSnow);
                    }
                    else if (name == "none")
                    {
                        controlDelta = abDelta;
                        Emit("D32.beam_control", Math.Abs(controlDelta));
                    }
                    else
                    {
                        Emit("D32.cloud_beam_delta", abDelta);
                        Emit("D32.cloud_beam_noise", abNoise);
                    }
                    if (name == "all" && openDelta != null)
                    {
                        // The wall between the camera and the beam: the added luma behind it against the same points in the open.
                        bool[] mask = PlaceWall(vessel, FlightCamera.fetch.mainCamera);
                        Emit("D32.wall_points_covered", Count(mask));
                        yield return ProbeWait.Frames(10);
                        foreach (ProbeWait w in MeasureOnOff(2)) yield return w;
                        double behind = MeanOver(Minus(abOn, abOff), mask);
                        double open = MeanOver(openDelta, mask);
                        Emit("D32.wall_open_delta", open);
                        Emit("D32.wall_hidden_share", open > 0 ? Math.Max(0.0, behind) / open : 1.0);
                        RemoveWall();
                        yield return ProbeWait.Frames(10);
                    }
                }
                EndVariant();
            }
            if (!double.IsNaN(deltaSnow))
            {
                double control = double.IsNaN(controlDelta) ? 0.0 : controlDelta;
                Emit("D32.beam_signal_ratio", LampProbeMath.SignalRatio(deltaSnow, control, noiseSnow, 0.002));
            }

            // 4. Smear and ghost under an upscaler.
            UpscalerKind upscaler = WeatherHooks.Caps.Upscaler;
            Emit("D32.upscaler_active", upscaler != UpscalerKind.None);
            EmitText("D32.upscaler_name", upscaler.ToString());
            // The camera's target is the picture before the upscaler, so the temporal rows read the screen, the picture after it (with the
            // interface hidden, as RECON's screen source does). Without a live screen they have no value and fail as missing.
            bool screenSource = geometry && live && liveScreen;
            Emit("D32.smear_source_screen", screenSource);
            // With an upscaler on, the temporal rows need the screen; without a live screen they would be missing and the probe would
            // still print PASSED.
            if (upscaler != UpscalerKind.None) Check("D32.smear_source_screen", screenSource, "geometry " + geometry + ", frame live " + live + ", screen live " + liveScreen);
            if (screenSource && upscaler != UpscalerKind.None && fc != null)
            {
                host.UseScreen = true;
                GameEvents.onHideUI.Fire();
                uiHidden = true;
                yield return ProbeWait.Frames(10);
                ForceTuning("lamp.forceCloudDensity", 0f);
                float h0 = fc.camHdg;
                SetSetting(AtmospheresSettings.Keys.LightInMedium, 1.0);
                foreach (ProbeWait w in TurnAndTail(h0)) yield return w;
                double[] errorWith = tailError;
                double[] convOn = convergedPoints;
                int stride = tailStrideFrames;
                SetSetting(AtmospheresSettings.Keys.LightInMedium, 0.0);
                yield return ProbeWait.Seconds(1);
                foreach (ProbeWait w in TurnAndTail(h0)) yield return w;
                double[] errorWithout = tailError;
                double[] convOff = convergedPoints;
                SetSetting(AtmospheresSettings.Keys.LightInMedium, 1.0);
                double beam = MeanOver(Minus(convOn, convOff), null);
                Emit("D32.smear_beam_delta", beam);
                Emit("D32.smear_capture_stride", stride);
                Emit("D32.smear_frames_dlss_20degps", LampProbeMath.SmearFrames(errorWith, errorWithout, beam, 0.25, SmearNoiseFloor));
                var tailWith = new List<double>();
                var tailWithout = new List<double>();
                for (int i = 3; i < TailFrames; i++)
                {
                    tailWith.Add(errorWith[i]);
                    tailWithout.Add(errorWithout[i]);
                }
                double floor = Math.Max(0.002, 0.25 * Math.Abs(beam));
                Emit("D32.ghost_vs_baseline", (LampProbeMath.Mean(tailWith) + floor) / (LampProbeMath.Mean(tailWithout) + floor));
                fc.camHdg = h0;
                host.UseScreen = false;
                GameEvents.onShowUI.Fire();
                uiHidden = false;
            }

            // 5. The IVA: no lamp of the own vessel is drawn from inside it.
            bool iva = false;
            if (vessel.GetCrewCount() > 0 && CameraManager.Instance != null)
            {
                try
                {
                    iva = CameraManager.Instance.SetCameraIVA();
                }
                catch (Exception e)
                {
                    LogLine("D32: the IVA could not be entered (" + e.GetType().Name + " " + e.Message + ")");
                }
            }
            Emit("D32.iva_available", iva);
            if (iva)
            {
                yield return ProbeWait.Seconds(4);
                Emit("D32.iva_draws", LampScatter.LastDrawCount);
                Emit("D32.iva_candidates", LampScatter.LitCandidates);
                CameraManager.Instance.SetCameraFlight();
                yield return ProbeWait.Seconds(2);
            }

            // 6. The cloud density the lamp would read in EVE's layers: eight directions round the vessel, at the nearest layer's mid shell.
            float best = 0f;
            Vector3d up = (vessel.GetWorldPos3D() - body.position).normalized;
            Vector3d north = body.transform.up;
            Vector3d east = Vector3d.Cross(north, up).normalized;
            Emit("D32.eve_layers", LampScatter.EveLayers);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4.0;
                Vector3d dir = (up * Math.Cos(0.01 * (1 + i % 3)) + (east * Math.Cos(a) + north * Math.Sin(a)) * Math.Sin(0.01 * (1 + i % 3))).normalized;
                for (double h = 500.0; h <= 4000.0; h += 500.0)
                {
                    Vector3 at = (Vector3)(body.position + dir * (body.Radius + h));
                    float density = LampScatter.Instance.ProbeCloudDensity(at, body);
                    if (density > best) best = density;
                }
            }
            Emit("D32.cloud_density_max", best);
            Emit("D32.cloud_sigma_max", LampMath.CloudSigma(best, LampTuning.Defaults().CloudSigmaFull));
        }
    }
}
