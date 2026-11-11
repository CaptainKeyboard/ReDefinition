using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The measurements that draw something of their own and read the frame: P10 and P11 (an opaque disc at queue 2504 over the ocean),
    // P7 (fog), P8 (a lit shader the game does not know), P12 (ambient factor). Every one compares states of the same scene a few frames
    // apart: off, on, and off again as the control. The fog, shader and ambient tests need daylight and a frame that is not saturated:
    // they say so in a row and stop when the sun is low.
    internal sealed partial class AtmoRecon
    {
        private const int SettleFrames = 25;

        private static Vector3 ViewportOf(Camera camera, Vector3 world)
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
            Emit(tag + ".ocean_renderers", CountOceanRenderers());
            Emit(tag + ".ocean_renderers_scatterer", CountOceanRenderers("Scatterer/"));
            Emit(tag + ".disc_shader_found", Shader.Find("Hidden/Internal-Colored") != null);
            if (!waited) yield break;

            int[] distances = { 100, 1000, 10000 };
            string[] names = { "50m", "500m", "5km" };
            for (int i = 0; i < distances.Length; i++)
            {
                // Lifted 3 m the disc is above any wave: a miss there is a failure of queue or depth. Lifted 0.3 m it is the product's
                // likely height and the waves may cross it: that row says how much.
                foreach (ProbeWait w in DiscStates(tag + ".", names[i], distances[i], 3.0)) yield return w;
                foreach (ProbeWait w in DiscStates(tag + ".low_", names[i], distances[i], 0.3)) yield return w;
            }
            PlaceCamera(60f, 8f, 90f);
        }

        // Active renderers whose shader name contains "Ocean"; with a prefix, only those whose shader name starts with it ("Scatterer/").
        private static int CountOceanRenderers(string prefix = null)
        {
            int n = 0;
            foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                Material m = r.sharedMaterial;
                if (m != null && m.shader != null && r.enabled && r.gameObject.activeInHierarchy
                    && m.shader.name.IndexOf("Ocean", StringComparison.OrdinalIgnoreCase) >= 0
                    && (prefix == null || m.shader.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) n++;
            }
            return n;
        }

        // One distance, one lift: the camera 30 degrees over the horizon at that distance from the pod, a disc beside the pod, the region
        // in its middle read with the disc off, on, on and off.
        private IEnumerable<ProbeWait> DiscStates(string prefix, string name, double distance, double lift)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;
            PlaceCamera((float)distance, 30f, 90f);
            yield return ProbeWait.Seconds(3);
            Camera camera = FlightCamera.fetch.mainCamera;
            if (lift > 1.0) Emit(prefix + "camera_height_" + name, body.GetAltitude(camera.transform.position));
            Vector3d up = (vessel.CoMD - body.position).normalized;
            Vector3 right = Vector3.ProjectOnPlane(camera.transform.right, (Vector3)up).normalized;
            Vector3d offset = (Vector3d)right * (0.3 * distance + 15.0);
            var disc = new ReconDisc(body, vessel, offset, 0.25 * distance + 12.0, lift, 2504, 15);
            Action follow = disc.Update;
            host.PerFrame.Add(follow);
            Vector3 centre = ViewportOf(camera, (Vector3)disc.Centre());
            bool inView = centre.z > 0f && centre.x > 0.08f && centre.x < 0.92f && centre.y > 0.08f && centre.y < 0.92f;
            Emit(prefix + "region_in_view_" + name, inView);
            if (!inView)
            {
                host.PerFrame.Remove(follow);
                disc.Destroy();
                yield break;
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
                EmitText(prefix + "error", "a frame could not be read at " + name);
                yield break;
            }

            on1.AddRange(on2);
            float medianOff = ReconMath.Median(off1), medianOn = ReconMath.Median(on1);
            float middle = 0.5f * (medianOff + medianOn);
            bool brighter = medianOn > medianOff;
            double share = ShareBeyond(on1, middle, brighter), control = ShareBeyond(off2, middle, brighter);
            Emit(prefix + "disc_fraction_" + name, share);
            Emit(prefix + "control_fraction_" + name, control);
            Emit(prefix + "luma_off_" + name, medianOff);
            Emit(prefix + "luma_on_" + name, medianOn);
            Emit(prefix + "luma_ratio_" + name, medianOn / Math.Max(1e-4f, medianOff));
            Say(prefix + name + " lift " + Num(lift) + " off " + medianOff.ToString("0.###") + " on " + medianOn.ToString("0.###") + " share "
                + share.ToString("0.##") + " control " + control.ToString("0.##"));
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
            if (!Daylight)
            {
                EmitText("P7.error", "the sun is " + Num(sunElevation) + " degrees over the pod: no daylight for the fog test");
                yield break;
            }
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
            // A grey albedo so that the sunlit ball stays below the top of an 8-bit target.
            var material = new Material(lit);
            material.color = new Color(0.25f, 0.25f, 0.25f, 1f);
            var far = new ReconBall(vessel, farOffset, 120f, material, 15);
            var reference = new ReconBall(vessel, referenceOffset, 1.2f, material, 0);
            Action follow = () => { far.Update(); reference.Update(); };
            host.PerFrame.Add(follow);

            bool fogWas = RenderSettings.fog;
            FogMode modeWas = RenderSettings.fogMode;
            float densityWas = RenderSettings.fogDensity;
            Color colourWas = RenderSettings.fogColor;
            const float density = 3.33e-4f, fogValue = 0.6f;
            Color fogColour = new Color(fogValue, fogValue, fogValue, 1f);
            double eyeDepth = Vector3.Dot(far.Position - camera.transform.position, camera.transform.forward);
            double predicted = ReconMath.FogShare(3, density, 0, 0, eyeDepth);
            Vector3 farView = ViewportOf(camera, far.Position), referenceView = ViewportOf(camera, reference.Position);
            bool inView = farView.z > 0 && referenceView.z > 0 && farView.x > 0.05f && farView.x < 0.95f && referenceView.x > 0.02f && referenceView.x < 0.98f;
            Emit("P7.targets_in_view", inView);
            Emit("P7.eye_depth_m", eyeDepth);
            Emit("P7.share_predicted", predicted);
            if (inView)
            {
                // off, on, off.
                RenderSettings.fog = false;
                yield return ProbeWait.Frames(SettleFrames);
                foreach (ProbeWait w in Capture()) yield return w;
                Color farOff1 = Region(farView), refOff1 = Region(referenceView);
                float peakOff = lastFrame != null ? lastFrame.Peak : float.NaN;
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
                double measured = ReconMath.MeasuredFogShare(Enc(farOff1), Enc(farOn), Enc(refOff1), Enc(refOn), fogValue);
                double control = ReconMath.MeasuredFogShare(Enc(farOff1), Enc(farOff2), Enc(refOff1), Enc(refOff2), fogValue);
                Emit("P7.share_measured", measured);
                Emit("P7.share_control", Math.Abs(control));
                Emit("P7.share_error_rel", predicted > 0 ? Math.Abs(measured - predicted) / predicted : double.NaN);
                Emit("P7.far_green_off", Enc(farOff1));
                Emit("P7.far_green_on", Enc(farOn));
                Emit("P7.ref_green_off", Enc(refOff1));
                Emit("P7.ref_green_ratio", Enc(refOn) / Math.Max(1e-6, Enc(refOff1)));
                Emit("P7.frame_peak", peakOff);
                Say("P7: predicted " + predicted.ToString("0.###") + " measured " + measured.ToString("0.###") + " control " + control.ToString("0.###")
                    + ", far " + Enc(farOff1).ToString("0.###") + " -> " + Enc(farOn).ToString("0.###") + ", peak " + peakOff.ToString("0.##"));
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

        // Two cases: two balls in the air (stock KSP/Diffuse and Unity's Legacy Diffuse, swapped between two shots), and the pod's own
        // renderers dressed in each shader with the pod's texture. The ratio of the foreign to the stock luminance is the row; the one
        // furthest from 1 counts. Terrain is not tried: the camera cannot be aimed at land reliably, and the shader path is the same.
        // Two foreign shaders: a surface shader with a deferred pass (the row), and a lit shader without one, which the game draws in its
        // forward step after the lighting (the ratio is informative: what a shell shader without a deferred pass would look like).
        private IEnumerable<ProbeWait> ForeignShader()
        {
            Shader stock = FirstShader("KSP/Diffuse", "KSP/Bumped Specular", "KSP/Specular");
            Shader foreign = FirstShader("Legacy Shaders/Diffuse", "Diffuse", "Standard");
            Shader forwardOnly = FirstShader("Legacy Shaders/VertexLit", "Mobile/VertexLit", "VertexLit");
            EmitText("P8.stock_shader", stock != null ? stock.name : "none");
            EmitText("P8.foreign_shader", foreign != null ? foreign.name : "none");
            EmitText("P8.forward_only_shader", forwardOnly != null ? forwardOnly.name : "none");
            Emit("P8.shaders_found", stock != null && foreign != null);
            if (stock == null || foreign == null) yield break;
            if (!Daylight)
            {
                EmitText("P8.error", "the sun is " + Num(sunElevation) + " degrees over the pod: no daylight for the shader test");
                yield break;
            }
            foreach (ProbeWait w in ShaderCase(stock, foreign, "")) yield return w;
            if (forwardOnly != null)
            {
                foreach (ProbeWait w in ShaderCase(stock, forwardOnly, "forward_")) yield return w;
            }
        }

        private IEnumerable<ProbeWait> ShaderCase(Shader stock, Shader foreign, string key)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            CelestialBody body = vessel.mainBody;

            // The balls.
            PlaceCamera(60f, 0f, 90f);
            yield return ProbeWait.Seconds(3);
            Camera camera = FlightCamera.fetch.mainCamera;
            Vector3d up = (vessel.CoMD - body.position).normalized;
            Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, (Vector3)up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(camera.transform.right, (Vector3)up).normalized;
            Vector3d centre = camera.transform.position;
            Vector3d leftOffset = centre + (Vector3d)forward * 25.0 - (Vector3d)right * 4.0 + up * 3.0 - vessel.CoMD;
            Vector3d rightOffset = centre + (Vector3d)forward * 25.0 + (Vector3d)right * 4.0 + up * 3.0 - vessel.CoMD;
            var grey = new Color(0.3f, 0.3f, 0.3f, 1f);
            var stockMaterial = new Material(stock) { color = grey };
            var foreignMaterial = new Material(foreign) { color = grey };
            var a = new ReconBall(vessel, leftOffset, 2.5f, stockMaterial, 0);
            var b = new ReconBall(vessel, rightOffset, 2.5f, foreignMaterial, 0);
            Action follow = () => { a.Update(); b.Update(); };
            host.PerFrame.Add(follow);
            Vector3 viewA = ViewportOf(camera, a.Position), viewB = ViewportOf(camera, b.Position);
            double stockSum = 0, foreignSum = 0, peak = 0;
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
                    peak = Math.Max(peak, lastFrame.Peak);
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
            double ballRatio = double.NaN;
            if (shots > 0)
            {
                double stockLuma = stockSum / shots, foreignLuma = foreignSum / shots;
                ballRatio = foreignLuma / Math.Max(1e-5, stockLuma);
                Emit("P8." + key + "ball_stock_luma", stockLuma);
                Emit("P8." + key + "ball_foreign_luma", foreignLuma);
                Emit("P8." + key + "ball_luma_ratio", ballRatio);
                Emit("P8." + key + "frame_peak", peak);
                Say("P8 " + key + "balls: stock " + stockLuma.ToString("0.###") + " other " + foreignLuma.ToString("0.###") + ", peak " + peak.ToString("0.##"));
            }

            // The part.
            double partRatio = double.NaN;
            Renderer[] renderers = vessel.rootPart != null ? (Renderer[])vessel.rootPart.GetComponentsInChildren<MeshRenderer>() : new Renderer[0];
            if (renderers.Length > 0)
            {
                PlaceCamera(14f, 15f, 90f);
                yield return ProbeWait.Seconds(3);
                var originals = new Material[renderers.Length][];
                Texture texture = null;
                for (int i = 0; i < renderers.Length; i++)
                {
                    originals[i] = renderers[i].sharedMaterials;
                    foreach (Material m in originals[i])
                    {
                        if (texture == null && m != null && m.HasProperty("_MainTex")) texture = m.mainTexture;
                    }
                }
                double[] luma = new double[2];
                Shader[] shaders = { stock, foreign };
                for (int state = 0; state < 2; state++)
                {
                    var dressed = new Material(shaders[state]) { color = Color.white };
                    if (texture != null) dressed.mainTexture = texture;
                    foreach (Renderer r in renderers)
                    {
                        var mats = new Material[r.sharedMaterials.Length];
                        for (int k = 0; k < mats.Length; k++) mats[k] = dressed;
                        r.sharedMaterials = mats;
                    }
                    yield return ProbeWait.Frames(SettleFrames);
                    foreach (ProbeWait w in Capture()) yield return w;
                    if (lastFrame != null)
                    {
                        Color c = lastFrame.Mean(0.5, 0.5, 0.04, 0.04);
                        luma[state] = ReconMath.Luma(c.r, c.g, c.b);
                    }
                    UnityEngine.Object.Destroy(dressed);
                }
                for (int i = 0; i < renderers.Length; i++) renderers[i].sharedMaterials = originals[i];
                partRatio = luma[0] > 1e-5 ? luma[1] / luma[0] : double.NaN;
                Emit("P8." + key + "part_stock_luma", luma[0]);
                Emit("P8." + key + "part_foreign_luma", luma[1]);
                Emit("P8." + key + "part_luma_ratio", partRatio);
                Say("P8 " + key + "pod: stock " + luma[0].ToString("0.###") + " other " + luma[1].ToString("0.###"));
            }

            // The row is the case furthest from 1; where one case could not be read, the other.
            double worst = double.NaN;
            foreach (double ratio in new[] { ballRatio, partRatio })
            {
                if (double.IsNaN(ratio)) continue;
                if (double.IsNaN(worst) || Math.Abs(ratio - 1.0) > Math.Abs(worst - 1.0)) worst = ratio;
            }
            if (!double.IsNaN(worst)) Emit("P8." + key + "luma_ratio", worst);
            else EmitText("P8." + key + "error", "no case could be read");
        }

        // ---------------------------------------------------------------- P12: ambient factor

        private IEnumerable<ProbeWait> AmbientFactors()
        {
            if (!Daylight)
            {
                EmitText("P12.error", "the sun is " + Num(sunElevation) + " degrees over the pod: no daylight for the ambient test");
                yield break;
            }
            PlaceCamera(60f, 8f, 90f);
            yield return ProbeWait.Seconds(3);
            float intensityWas = RenderSettings.ambientIntensity;
            var lumas = new List<double>();
            float[] factors = { 1f, 0.7f, 0.4f };
            float current = 1f;
            Action force = () => { RenderSettings.ambientIntensity = current; };
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
            // The factors run 1, 0.7, 0.4: a luminance that rises with the factor is the monotone case, and it must rise by a margin.
            var reversed = new List<double>(lumas);
            reversed.Reverse();
            bool monotone = !reversed.Exists(double.IsNaN) && ReconMath.IsMonotoneIncreasing(reversed, -0.002);
            double spread = lumas.Count == 3 ? Math.Abs(lumas[0] - lumas[2]) / Math.Max(1e-5, lumas[0]) : 0;
            Emit("P12.monotone", monotone);
            Emit("P12.spread_rel", spread);
        }
    }
}
