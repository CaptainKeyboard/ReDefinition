using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The measurements that draw something of their own and read the frame: P10 and P11 (an opaque disc at queue 2504 over the ocean),
    // P7 (fog), P8 (a lit shader the game does not know), P12 (ambient factor). Every one compares states of the same scene a few frames
    // apart: off, on, and off again as the control.
    internal sealed partial class AtmoRecon
    {
        private const int SettleFrames = 25;

        // The pixel region (fractions of the frame) and the per-state luma lists the tests share.
        private Vector3 ViewportOf(Camera camera, Vector3 world)
        {
            return camera.WorldToViewportPoint(world);
        }

        // ---------------------------------------------------------------- P10 / P11: the disc over the ocean

        private IEnumerable<ProbeWait> DiscOverOcean()
        {
            string tag = scattererPresent ? "P11" : "P10";
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            foreach (ProbeWait w in WaitFor(() => vessel.Splashed, 60)) yield return w;
            Emit(tag + ".pod_splashed", waited);
            Emit(tag + ".ocean_overlay_is_scatterer", scattererPresent);
            if (!waited) yield break;

            int[] distances = { 100, 1000, 10000 };
            string[] names = { "50m", "500m", "5km" };
            for (int i = 0; i < distances.Length; i++)
            {
                double d = distances[i];
                PlaceCamera((float)d, 30f, 90f);
                yield return ProbeWait.Seconds(3);
                Camera camera = FlightCamera.fetch.mainCamera;
                Emit(tag + ".camera_height_" + names[i], body.GetAltitude(camera.transform.position));
                Vector3d up = (vessel.CoMD - body.position).normalized;
                Vector3 right = Vector3.ProjectOnPlane(camera.transform.right, (Vector3)up).normalized;
                Vector3d offset = (Vector3d)right * (0.3 * d + 15.0);
                var disc = new ReconDisc(body, vessel, offset, 0.25 * d + 12.0, 0.3, 2504, 15);
                Action follow = disc.Update;
                host.PerFrame.Add(follow);
                Vector3 centre = ViewportOf(camera, (Vector3)disc.Centre());
                bool inView = centre.z > 0f && centre.x > 0.08f && centre.x < 0.92f && centre.y > 0.08f && centre.y < 0.92f;
                Emit(tag + ".region_in_view_" + names[i], inView);
                if (!inView)
                {
                    host.PerFrame.Remove(follow);
                    disc.Destroy();
                    continue;
                }

                disc.Active = false;
                yield return ProbeWait.Frames(SettleFrames);
                foreach (ProbeWait w in Capture()) yield return w;
                List<float> off1 = lastFrame != null ? lastFrame.Lumas(centre.x, centre.y, 0.06, 0.06) : null;
                disc.Active = true;
                yield return ProbeWait.Frames(SettleFrames);
                foreach (ProbeWait w in Capture()) yield return w;
                List<float> on1 = lastFrame != null ? lastFrame.Lumas(centre.x, centre.y, 0.06, 0.06) : null;
                yield return ProbeWait.Frames(5);
                foreach (ProbeWait w in Capture()) yield return w;
                List<float> on2 = lastFrame != null ? lastFrame.Lumas(centre.x, centre.y, 0.06, 0.06) : null;
                disc.Active = false;
                yield return ProbeWait.Frames(SettleFrames);
                foreach (ProbeWait w in Capture()) yield return w;
                List<float> off2 = lastFrame != null ? lastFrame.Lumas(centre.x, centre.y, 0.06, 0.06) : null;
                host.PerFrame.Remove(follow);
                disc.Destroy();
                if (off1 == null || on1 == null || on2 == null || off2 == null)
                {
                    EmitText(tag + ".error", "a frame could not be read at " + names[i]);
                    continue;
                }

                on1.AddRange(on2);
                float medianOff = ReconMath.Median(off1), medianOn = ReconMath.Median(on1);
                float middle = 0.5f * (medianOff + medianOn);
                bool brighter = medianOn > medianOff;
                Emit(tag + ".disc_fraction_" + names[i], ShareBeyond(on1, middle, brighter));
                Emit(tag + ".control_fraction_" + names[i], ShareBeyond(off2, middle, brighter));
                Emit(tag + ".luma_off_" + names[i], medianOff);
                Emit(tag + ".luma_on_" + names[i], medianOn);
                Emit(tag + ".luma_ratio_" + names[i], medianOn / Math.Max(1e-4f, medianOff));
                Say(tag + ": " + names[i] + " off " + medianOff.ToString("0.###") + " on " + medianOn.ToString("0.###") + " share "
                    + ShareBeyond(on1, middle, brighter).ToString("0.##") + " control " + ShareBeyond(off2, middle, brighter).ToString("0.##"));
            }
            PlaceCamera(60f, 8f, 90f);
        }

        private static double ShareBeyond(List<float> values, float threshold, bool above)
        {
            int n = 0;
            foreach (float v in values)
            {
                if (above ? v >= threshold : v <= threshold) n++;
            }
            return values.Count == 0 ? 0 : n / (double)values.Count;
        }

        // ---------------------------------------------------------------- P7: fog

        private IEnumerable<ProbeWait> FogFixture()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            Shader lit = FirstShader("Legacy Shaders/Diffuse", "Diffuse", "Standard", "KSP/Diffuse");
            if (lit == null)
            {
                EmitText("P7.error", "no lit shader found for the fog targets");
                yield break;
            }
            PlaceCamera(60f, 0f, 90f);
            yield return ProbeWait.Seconds(3);
            Camera camera = FlightCamera.fetch.mainCamera;
            Vector3d up = (vessel.CoMD - body.position).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, (Vector3)up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(camera.transform.right, (Vector3)up).normalized;
            Vector3d cameraAt = camera.transform.position;
            const double farDistance = 2500.0, referenceDistance = 12.0;
            Vector3d farOffset = cameraAt + (Vector3d)forward * farDistance + up * 150.0 - vessel.CoMD;
            Vector3d referenceOffset = cameraAt + (Vector3d)forward * referenceDistance + (Vector3d)right * 3.0 + up * 1.0 - vessel.CoMD;
            var material = new Material(lit);
            material.color = Color.white;
            var far = new ReconBall(vessel, farOffset, 120f, material, 15);
            var reference = new ReconBall(vessel, referenceOffset, 1.2f, material, 0);
            Action follow = () => { far.Update(); reference.Update(); };
            host.PerFrame.Add(follow);

            bool fogWas = RenderSettings.fog;
            FogMode modeWas = RenderSettings.fogMode;
            float densityWas = RenderSettings.fogDensity;
            Color colourWas = RenderSettings.fogColor;
            const float density = 3.33e-4f;
            Color fogColour = new Color(0.5f, 0.5f, 0.5f, 1f);
            double eyeDepth = Vector3.Dot(far.Position - camera.transform.position, camera.transform.forward);
            double predicted = ReconMath.FogShare(3, density, 0, 0, eyeDepth);
            Vector3 farView = ViewportOf(camera, far.Position), referenceView = ViewportOf(camera, reference.Position);
            bool inView = farView.z > 0 && referenceView.z > 0 && farView.x > 0.05f && farView.x < 0.95f && referenceView.x > 0.02f && referenceView.x < 0.98f;
            Emit("P7.targets_in_view", inView);
            Emit("P7.eye_depth_m", eyeDepth);
            Emit("P7.share_predicted", predicted);
            if (inView)
            {
                // off, on, on, off.
                RenderSettings.fog = false;
                yield return ProbeWait.Frames(SettleFrames);
                foreach (ProbeWait w in Capture()) yield return w;
                Color farOff1 = Region(farView), refOff1 = Region(referenceView);
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogDensity = density;
                RenderSettings.fogColor = fogColour;
                yield return ProbeWait.Frames(SettleFrames);
                foreach (ProbeWait w in Capture()) yield return w;
                Color farOn = Region(farView), refOn = Region(referenceView);
                RenderSettings.fog = false;
                yield return ProbeWait.Frames(SettleFrames);
                foreach (ProbeWait w in Capture()) yield return w;
                Color farOff2 = Region(farView), refOff2 = Region(referenceView);

                // Fog blends in the encoded (gamma) colour space of the game.
                double measured = ReconMath.MeasuredFogShare(Enc(farOff1), Enc(farOn), Enc(refOff1), Enc(refOn), 0.5);
                double control = ReconMath.MeasuredFogShare(Enc(farOff1), Enc(farOff2), Enc(refOff1), Enc(refOff2), 0.5);
                Emit("P7.share_measured", measured);
                Emit("P7.share_control", Math.Abs(control));
                Emit("P7.share_error_rel", predicted > 0 ? Math.Abs(measured - predicted) / predicted : double.NaN);
                Emit("P7.far_green_off", Enc(farOff1));
                Emit("P7.far_green_on", Enc(farOn));
                Emit("P7.ref_green_ratio", Enc(refOn) / Math.Max(1e-6, Enc(refOff1)));
                Say("P7: predicted " + predicted.ToString("0.###") + " measured " + measured.ToString("0.###") + " control " + control.ToString("0.###"));
            }
            RenderSettings.fog = fogWas;
            RenderSettings.fogMode = modeWas;
            RenderSettings.fogDensity = densityWas;
            RenderSettings.fogColor = colourWas;
            host.PerFrame.Remove(follow);
            far.Destroy();
            reference.Destroy();
            UnityEngine.Object.Destroy(material);
        }

        private Color Region(Vector3 view)
        {
            return lastFrame != null ? lastFrame.Mean(view.x, view.y, 0.02, 0.02) : Color.black;
        }

        private static double Enc(Color linear)
        {
            return Mathf.LinearToGammaSpace(linear.g);
        }

        private static Shader FirstShader(params string[] names)
        {
            foreach (string name in names)
            {
                Shader s = Shader.Find(name);
                if (s != null) return s;
            }
            return null;
        }

        // ---------------------------------------------------------------- P8: a shader the game does not know

        private IEnumerable<ProbeWait> ForeignShader()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            Shader stock = FirstShader("KSP/Diffuse", "KSP/Bumped Specular", "KSP/Specular");
            Shader foreign = FirstShader("Legacy Shaders/Diffuse", "Diffuse", "Standard");
            EmitText("P8.stock_shader", stock != null ? stock.name : "none");
            EmitText("P8.foreign_shader", foreign != null ? foreign.name : "none");
            Emit("P8.shaders_found", stock != null && foreign != null);
            if (stock == null || foreign == null) yield break;

            PlaceCamera(60f, 0f, 90f);
            yield return ProbeWait.Seconds(3);
            Camera camera = FlightCamera.fetch.mainCamera;
            Vector3d up = (vessel.CoMD - body.position).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, (Vector3)up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(camera.transform.right, (Vector3)up).normalized;
            Vector3d centre = camera.transform.position;
            Vector3d leftOffset = centre + (Vector3d)forward * 25.0 - (Vector3d)right * 4.0 + up * 3.0 - vessel.CoMD;
            Vector3d rightOffset = centre + (Vector3d)forward * 25.0 + (Vector3d)right * 4.0 + up * 3.0 - vessel.CoMD;
            var stockMaterial = new Material(stock) { color = Color.white };
            var foreignMaterial = new Material(foreign) { color = Color.white };
            var a = new ReconBall(vessel, leftOffset, 2.5f, stockMaterial, 0);
            var b = new ReconBall(vessel, rightOffset, 2.5f, foreignMaterial, 0);
            Action follow = () => { a.Update(); b.Update(); };
            host.PerFrame.Add(follow);
            Vector3 viewA = ViewportOf(camera, a.Position), viewB = ViewportOf(camera, b.Position);
            double stockSum = 0, foreignSum = 0;
            int shots = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                yield return ProbeWait.Frames(SettleFrames);
                foreach (ProbeWait w in Capture()) yield return w;
                if (lastFrame != null)
                {
                    Color atA = lastFrame.Mean(viewA.x, viewA.y, 0.02, 0.02), atB = lastFrame.Mean(viewB.x, viewB.y, 0.02, 0.02);
                    stockSum += pass == 0 ? ReconMath.Luma(atA.r, atA.g, atA.b) : ReconMath.Luma(atB.r, atB.g, atB.b);
                    foreignSum += pass == 0 ? ReconMath.Luma(atB.r, atB.g, atB.b) : ReconMath.Luma(atA.r, atA.g, atA.b);
                    shots++;
                }
                // The second pass swaps the two materials, so that the position in the light does not decide.
                Material swap = a.Material;
                a.Material = b.Material;
                b.Material = swap;
            }
            host.PerFrame.Remove(follow);
            a.Destroy();
            b.Destroy();
            UnityEngine.Object.Destroy(stockMaterial);
            UnityEngine.Object.Destroy(foreignMaterial);
            if (shots == 0)
            {
                EmitText("P8.error", "no frame could be read");
                yield break;
            }
            double stockLuma = stockSum / shots, foreignLuma = foreignSum / shots;
            Emit("P8.stock_luma", stockLuma);
            Emit("P8.foreign_luma", foreignLuma);
            Emit("P8.luma_ratio", foreignLuma / Math.Max(1e-5, stockLuma));
            Say("P8: stock " + stockLuma.ToString("0.###") + " foreign " + foreignLuma.ToString("0.###"));
        }

        // ---------------------------------------------------------------- P12: ambient factor

        private IEnumerable<ProbeWait> AmbientFactors()
        {
            PlaceCamera(60f, 8f, 90f);
            yield return ProbeWait.Seconds(3);
            float intensityWas = RenderSettings.ambientIntensity;
            var lumas = new List<double>();
            float[] factors = { 1f, 0.7f, 0.4f };
            Action force = null;
            float current = 1f;
            force = () => { RenderSettings.ambientIntensity = current; };
            host.PerFrame.Add(force);
            foreach (float factor in factors)
            {
                current = factor;
                yield return ProbeWait.Frames(SettleFrames + 20);
                foreach (ProbeWait w in Capture()) yield return w;
                double mean = lastFrame != null ? ReconMath.Mean(lastFrame.Lumas(0.5, 0.5, 1.0, 1.0)) : double.NaN;
                lumas.Add(mean);
                Emit("P12.frame_luma_factor_" + factor.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), mean);
            }
            host.PerFrame.Remove(force);
            RenderSettings.ambientIntensity = intensityWas;
            // The factors run 1, 0.7, 0.4: a falling luminance is the monotone case.
            var reversed = new List<double>(lumas);
            reversed.Reverse();
            bool monotone = !reversed.Exists(double.IsNaN) && ReconMath.IsMonotoneIncreasing(reversed, 1e-4);
            double spread = lumas.Count == 3 ? Math.Abs(lumas[0] - lumas[2]) / Math.Max(1e-5, lumas[0]) : 0;
            Emit("P12.monotone", monotone);
            Emit("P12.spread_rel", spread);
        }
    }
}
