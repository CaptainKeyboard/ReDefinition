using System;
using System.Collections;
using System.Collections.Generic;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // Checks the lamp scatter's own draws (LampPass and its shader, the same classes the mod uses) against what the maths says, without
    // the game. A warm spot lamp (20 degree half angle, 120 m) shines along +z in a fake world; the camera stands 12 m to its side and
    // 15 m in front, looking across the beam. Reference expectation, written before the first run:
    //   * at sigma 0 nothing is added (the frame equals the frame without the lamp) and the recorded buffer is empty;
    //   * the beam's luminance (the frame with the lamp minus the frame without, over the beam's region) rises with sigma over the
    //     ladder 0.002 ... 0.05 and each step is above the one before;
    //   * the mean over the region agrees with the CPU march of LampMath.BeamRadiance over the same pixels within 10 %
    //     (the shader marches 10 jittered steps, the reference 128);
    //   * the lamp's colour stays warm: red above blue in the added light;
    //   * a wall between the camera and the beam hides the beam behind it (the depth copy clips the march): at most 5 % of the open
    //     beam's luminance is left behind the wall;
    //   * a sigma at the camera of 0.08 against 0 dims the beam (the cloud between the camera and the lamp), at most 80 %;
    //   * the glints leave the mean (within 15 %) and raise the spread of the luma at least 1.5 times;
    //   * the Host composite (after the frame, with a copy of the scene depth) of a beam half hidden by an off-centre occluder gives the
    //     Scene composite's mean within 5 % and a picture that correlates with it at 0.98;
    //   * atmoDebug contrib: the mask is blue and only where the beam is (red and green added: 0).
    //
    //   -mode scene -scene lamp-check
    [WeatherScene("lamp-check")]
    internal sealed class LampCheckScene : LampSceneBase
    {
        // The pass colour's scale: a lamp of intensity 2 times the gain 6.
        private const float Scale = 12f;
        private GameObject wall;
        private RenderTexture hostDepth;

        protected override void BuildContent()
        {
            MakePass();
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = new Color(0.12f, 0.14f, 0.18f);
            hostDepth = new RenderTexture(Width, Height, 0, RenderTextureFormat.RFloat) { filterMode = FilterMode.Point };
            hostDepth.Create();
            PoseAt(12f);
        }

        // The camera x metres to the side of the lamp's axis and 15 m in front, looking across (towards -x).
        private void PoseAt(float side)
        {
            Cam.transform.position = LampOrigin + new Vector3(side, 0f, 15f);
            Cam.transform.rotation = Quaternion.Euler(0f, -90f, 0f);
        }

        private LampDraw[] Beam(float sigma, float sigmaCamera, float glint)
        {
            return new[] { Lamp(LampOrigin, Vector3.forward, sigma, sigmaCamera, glint, Scale) };
        }

        // The region of the screen the beam fills: between the lamp's axis points 8 m and 32 m ahead of it, 120 pixels above and below
        // the screen's middle.
        private ImageStats.Rect2 BeamRegion()
        {
            Vector3 a = Cam.WorldToScreenPoint(LampOrigin + new Vector3(0f, 0f, 8f));
            Vector3 b = Cam.WorldToScreenPoint(LampOrigin + new Vector3(0f, 0f, 32f));
            int x0 = Mathf.Clamp((int)Mathf.Min(a.x, b.x), 0, Width - 1), x1 = Mathf.Clamp((int)Mathf.Max(a.x, b.x), 1, Width);
            int mid = Height / 2;
            return new ImageStats.Rect2(x0, mid - 120, x1, mid + 120);
        }

        // The mean luma the CPU march gives over the pixels of a region (every 8th pixel each way), for white light times Scale.
        private double Reference(ImageStats.Rect2 r, float sigma, float sigmaCamera)
        {
            float half = HalfAngleDeg * Mathf.Deg2Rad;
            float cosOuter = Mathf.Cos(half);
            double sum = 0;
            int n = 0;
            Vector3 o = Cam.transform.position;
            var origin = new LampVec(o.x, o.y, o.z);
            var pos = new LampVec(LampOrigin.x, LampOrigin.y, LampOrigin.z);
            var axis = new LampVec(0f, 0f, 1f);
            for (int y = r.Y0; y < r.Y1; y += 8)
            {
                for (int x = r.X0; x < r.X1; x += 8)
                {
                    Ray ray = Cam.ScreenPointToRay(new Vector3(x + 0.5f, y + 0.5f, 0f));
                    var dir = new LampVec(ray.direction.x, ray.direction.y, ray.direction.z);
                    float radiance = LampMath.BeamRadiance(origin, dir, 1e5f, pos, axis, LampRange, cosOuter, sigma, sigmaCamera, ref Tuning, 128, 0.5f);
                    // The pass colour is warm (1, 0.82, 0.55); its luma is the weight below.
                    sum += radiance;
                    n++;
                }
            }
            double warmLuma = 0.2126 * 1.0 + 0.7152 * 0.82 + 0.0722 * 0.55;
            return n == 0 ? 0 : sum / n * Scale * warmLuma;
        }

        private IEnumerator Capture(LampDraw[] draws, int count, float weight, Action<RgbFrame> done)
        {
            Attach(draws, count, weight);
            yield return Frames(2);
            yield return GrabRgb(done);
        }

        public override IEnumerator Run()
        {
            yield return Frames(6);
            ImageStats.Rect2 region = BeamRegion();
            Sink.Info("region.pixels", region.Area);

            // --- The frame without the lamp, and the zero cost at sigma 0.
            RgbFrame none = null;
            yield return Capture(null, 0, 1f, f => none = f);
            double baseLuma = none.Mean(region, 3);

            var emptyBuffer = new CommandBuffer { name = "empty" };
            LampFrame frame = MakeFrame(1f);
            LampDraw[] zeroSigma = Beam(0f, 0f, 0f);
            Pass.Record(emptyBuffer, zeroSigma, 1, ref frame, null);
            Sink.Flag("zero.empty_buffer_at_sigma_0", emptyBuffer.sizeInBytes == 0);
            emptyBuffer.Release();
            RgbFrame atZero = null;
            yield return Capture(zeroSigma, 1, 1f, f => atZero = f);
            Sink.AtMost("zero.delta_at_sigma_0", Math.Abs(atZero.Mean(region, 3) - baseLuma), 1e-6);

            // --- The ladder of sigma.
            float[] ladder = { 0.002f, 0.005f, 0.01f, 0.02f, 0.03f, 0.05f };
            var deltas = new List<double>();
            double reference0 = 0, measured0 = 0;
            RgbFrame at002 = null;
            for (int i = 0; i < ladder.Length; i++)
            {
                RgbFrame lit = null;
                yield return Capture(Beam(ladder[i], ladder[i], 0f), 1, 1f, f => lit = f);
                double delta = lit.Mean(region, 3) - baseLuma;
                deltas.Add(delta);
                Sink.Info("beam.delta_sigma_" + ladder[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), delta);
                if (i == 3)
                {
                    measured0 = delta;
                    reference0 = Reference(region, ladder[i], ladder[i]);
                    at002 = lit;
                }
            }
            // Diagnostics: the depth copy the Scene draws clip against, at the middle of the picture, the top and the bottom.
            yield return new WaitForEndOfFrame();
            if (Pass.DepthCopy != null)
            {
                Sink.Info("diag.depth_copy_middle", ReadRed(Pass.DepthCopy, Width / 2, Height / 2));
                Sink.Info("diag.depth_copy_top", ReadRed(Pass.DepthCopy, Width / 2, Height - 4));
                Sink.Info("diag.depth_copy_bottom", ReadRed(Pass.DepthCopy, Width / 2, 4));
            }
            Texture globalDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
            if (globalDepth != null)
            {
                Graphics.Blit(globalDepth, hostDepth);
                Sink.Info("diag.global_depth_middle", ReadRed(hostDepth, Width / 2, Height / 2));
                Sink.Info("diag.global_depth_top", ReadRed(hostDepth, Width / 2, Height - 4));
                Sink.Info("diag.global_depth_bottom", ReadRed(hostDepth, Width / 2, 4));
            }
            Sink.Info("diag.reversed_z", SystemInfo.usesReversedZBuffer ? 1 : 0);
            bool rising = true;
            for (int i = 0; i < deltas.Count; i++)
            {
                if (!(deltas[i] > (i == 0 ? 1e-5 : deltas[i - 1]))) rising = false;
            }
            Sink.Flag("beam.rises_with_sigma", rising);
            Sink.Info("beam.reference_sigma_0.02", reference0);
            Sink.AtMost("beam.cpu_deviation_sigma_0.02", Math.Abs(measured0 - reference0) / Math.Max(reference0, 1e-9), 0.10);

            // --- The colour: warm light added to a grey-blue backdrop.
            double dr = at002.Mean(region, 0) - none.Mean(region, 0), db = at002.Mean(region, 2) - none.Mean(region, 2);
            Sink.Info("beam.added_red", dr);
            Sink.Info("beam.added_blue", db);
            Sink.Flag("beam.stays_warm", dr > db * 1.2 && db > 0);

            // --- Camera in a cloud: the sigma at the camera dims the beam.
            RgbFrame thick = null;
            yield return Capture(Beam(0.02f, 0.08f, 0f), 1, 1f, f => thick = f);
            double dimmed = (thick.Mean(region, 3) - baseLuma) / Math.Max(measured0, 1e-9);
            Sink.AtMost("cloud.camera_sigma_dim_ratio", dimmed, 0.80);
            Sink.AtLeast("cloud.camera_sigma_dim_floor", dimmed, 0.0);

            // --- The glints.
            RgbFrame glint = null;
            yield return Capture(Beam(0.02f, 0.02f, 0.6f), 1, 1f, f => glint = f);
            // The spread of the added light around its local mean (41 x 41 box): the backdrop's edges (the frames are differences to the
            // frame without the lamp) and the beam's own gradient are left out, the flakes are not.
            double spreadBase = RgbFrame.Difference(at002, none).HighFrequency(region, 20);
            double spreadGlint = RgbFrame.Difference(glint, none).HighFrequency(region, 20);
            double meanRatio = (glint.Mean(region, 3) - baseLuma) / Math.Max(measured0, 1e-9);
            Sink.Info("glint.spread_smooth", spreadBase);
            Sink.Info("glint.spread_glint", spreadGlint);
            Sink.AtLeast("glint.spread_ratio", spreadGlint / Math.Max(spreadBase, 1e-9), 1.5);
            Sink.AtLeast("glint.mean_ratio_low", meanRatio, 0.85);
            Sink.AtMost("glint.mean_ratio_high", meanRatio, 1.15);

            // --- The contribution mask: blue, and only where the beam is.
            Shader.SetGlobalFloat("_AtmoDebug", 1f);
            RgbFrame mask = null;
            yield return Capture(Beam(0.02f, 0.02f, 0f), 1, 1f, f => mask = f);
            Shader.SetGlobalFloat("_AtmoDebug", 0f);
            var corner = new ImageStats.Rect2(0, 0, 100, 100);
            Sink.AtLeast("contrib.blue_in_beam", mask.Mean(region, 2) - none.Mean(region, 2), 0.01);
            Sink.AtMost("contrib.red_in_beam", Math.Abs(mask.Mean(region, 0) - none.Mean(region, 0)), 0.002);
            Sink.AtMost("contrib.green_in_beam", Math.Abs(mask.Mean(region, 1) - none.Mean(region, 1)), 0.002);
            Sink.AtMost("contrib.blue_outside_beam", Math.Abs(mask.Mean(corner, 2) - none.Mean(corner, 2)), 0.002);

            // --- A wall between the camera and the beam. The camera steps back to 14 m, the wall stands at 11.5 m from the axis, 2.5 m
            // ahead of the camera, and fills the whole view (the rays reach z <= 17.6 m before it, where the cone's radius is 6.4 m, so
            // no beam is in front of it); the lamp is on in both frames.
            PoseAt(14f);
            yield return Frames(2);
            ImageStats.Rect2 wallRegion = BeamRegion();
            RgbFrame openNone = null, openLit = null;
            yield return Capture(null, 0, 1f, f => openNone = f);
            yield return Capture(Beam(0.02f, 0.02f, 0f), 1, 1f, f => openLit = f);
            double openDelta = openLit.Mean(wallRegion, 3) - openNone.Mean(wallRegion, 3);
            wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = LampOrigin + new Vector3(11.5f, 0f, 25f);
            wall.transform.localScale = new Vector3(0.2f, 40f, 60f);
            wall.GetComponent<Renderer>().sharedMaterial = new Material(Resources.Load<Material>("WeatherStandard")) { color = new Color(0.4f, 0.4f, 0.4f) };
            UnityEngine.Object.Destroy(wall.GetComponent<Collider>());
            yield return Frames(2);
            RgbFrame wallNone = null, wallLit = null;
            yield return Capture(null, 0, 1f, f => wallNone = f);
            yield return Capture(Beam(0.02f, 0.02f, 0f), 1, 1f, f => wallLit = f);
            double wallDelta = wallLit.Mean(wallRegion, 3) - wallNone.Mean(wallRegion, 3);
            Sink.Info("wall.open_delta", openDelta);
            Sink.Info("wall.walled_delta", wallDelta);
            Sink.AtLeast("wall.open_delta_present", openDelta, 0.005);
            Sink.AtMost("wall.hidden_share", Math.Abs(wallDelta) / Math.Max(openDelta, 1e-9), 0.05);
            UnityEngine.Object.Destroy(wall);
            Detach();

            // --- The Host composite against the Scene composite, at the first pose again, with an occluder that is off-centre and
            // higher than the lamp (y from -1 to +3 m, z from 19 to 29 m, 6 m from the axis): a depth that is missing, flipped or
            // read at the wrong place shows in the picture, not only in its mean.
            PoseAt(12f);
            GameObject occluder = GameObject.CreatePrimitive(PrimitiveType.Cube);
            occluder.transform.position = LampOrigin + new Vector3(6f, 1f, 24f);
            occluder.transform.localScale = new Vector3(0.3f, 4f, 10f);
            occluder.GetComponent<Renderer>().sharedMaterial = new Material(Resources.Load<Material>("WeatherStandard")) { color = new Color(0.4f, 0.4f, 0.4f) };
            UnityEngine.Object.Destroy(occluder.GetComponent<Collider>());
            yield return Frames(3);
            ImageStats.Rect2 r2 = BeamRegion();
            RgbFrame sceneNone = null, sceneLit = null;
            yield return Capture(null, 0, 1f, f => sceneNone = f);
            yield return Capture(Beam(0.02f, 0.02f, 0f), 1, 1f, f => sceneLit = f);
            RgbFrame sceneDiff = RgbFrame.Difference(sceneLit, sceneNone);
            double sceneDelta = sceneDiff.Mean(r2, 3);
            Detach();
            yield return Frames(2);
            yield return new WaitForEndOfFrame();
            RgbFrame before = RgbFrame.Read(Target);
            Texture sceneDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
            Sink.Flag("host.depth_present", sceneDepth != null);
            if (sceneDepth != null) Graphics.Blit(sceneDepth, hostDepth);
            var cmd = new CommandBuffer { name = "lamp host" };
            LampFrame hostFrame = MakeFrame(1f);
            cmd.SetRenderTarget(Target);
            cmd.SetViewProjectionMatrices(Cam.worldToCameraMatrix, GL.GetGPUProjectionMatrix(Cam.projectionMatrix, true));
            Pass.Record(cmd, Beam(0.02f, 0.02f, 0f), 1, ref hostFrame, sceneDepth != null ? hostDepth : null);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            RgbFrame after = RgbFrame.Read(Target);
            RgbFrame hostDiff = RgbFrame.Difference(after, before);
            double hostDelta = hostDiff.Mean(r2, 3);
            Sink.Info("host.scene_delta", sceneDelta);
            Sink.Info("host.host_delta", hostDelta);
            Sink.AtMost("host.parity_dev", Math.Abs(hostDelta - sceneDelta) / Math.Max(sceneDelta, 1e-9), 0.05);
            Sink.AtLeast("host.pixel_correlation", RgbFrame.Correlation(sceneDiff, hostDiff, r2), 0.98);
            // The occluder takes light away: the occluded beam is at most 90 % of the open beam of the sigma ladder (same pose, sigma).
            Sink.AtMost("host.occluder_effect", sceneDelta / Math.Max(measured0, 1e-9), 0.90);
            UnityEngine.Object.Destroy(occluder);
        }
    }
}
