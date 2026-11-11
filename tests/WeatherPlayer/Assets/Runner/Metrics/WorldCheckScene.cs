using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The scene that checks the player's own measuring: it plants known things in the fake world and the metrics must find them. The
    // marker contrast at three distances, the opacity curve over five fog levels, the count of 25 planted particles, the camera paths
    // with the frame-indexed clock, and the timers. Its verdicts come from what was planted, not from a tuned number:
    //
    //   -mode scene -scene world-check
    [WeatherScene("world-check")]
    public sealed class WorldCheckScene : WeatherSceneBase
    {
        private const int Planted = 25;
        private readonly List<GameObject> planted = new List<GameObject>();

        private GameObject Sphere(Vector3 at, float radius)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.position = at;
            sphere.transform.localScale = Vector3.one * radius * 2f;
            sphere.GetComponent<Renderer>().sharedMaterial = new Material(Resources.Load<Material>("WeatherStandard")) { color = new Color(0.02f, 0.02f, 0.02f) };
            UnityEngine.Object.Destroy(sphere.GetComponent<Collider>());
            planted.Add(sphere);
            return sphere;
        }

        private void Clear()
        {
            foreach (GameObject g in planted) UnityEngine.Object.Destroy(g);
            planted.Clear();
        }

        // Waits for the end of the frame, then reads the target.
        private IEnumerator Grab(System.Action<LumaFrame> done)
        {
            yield return new WaitForEndOfFrame();
            done(LumaFrame.Read(Target));
        }

        public override IEnumerator Run()
        {
            var start = new CameraPose(new Vector3(2000f, 120f, -800f), 0f, 3f);
            Pose(start);
            yield return Frames(10);

            // The clear frame: the luma and the local contrast of the whole world.
            LumaFrame clear = null;
            yield return Grab(f => clear = f);
            ImageStats.Rect2 whole = ImageStats.Rect2.Whole(clear);
            double clearMean = ImageStats.MeanLuma(clear, whole);
            Sink.Info("luma_mean", clearMean);
            Sink.AtLeast("luma_mean_nonzero", clearMean, 0.05);
            Sink.Info("local_contrast", ImageStats.LocalContrast(clear, whole, 6));

            // Markers: three dark spheres of the same angular size at 200, 800 and 3200 m, in the sky above the horizon.
            Pose(new CameraPose(start.Position, 0f, -12f));
            float[] distances = { 200f, 800f, 3200f };
            float[] yaws = { -8f, 0f, 8f };
            var rects = new ImageStats.Rect2[3];
            for (int i = 0; i < 3; i++)
            {
                Vector3 direction = Quaternion.Euler(-12f, yaws[i], 0f) * Vector3.forward;
                Vector3 at = start.Position + direction * distances[i];
                Sphere(at, distances[i] * 0.012f);
            }
            yield return Frames(3);
            LumaFrame markers = null;
            yield return Grab(f => markers = f);
            for (int i = 0; i < 3; i++)
            {
                rects[i] = ImageStats.ScreenRect(Cam, planted[i].transform.position, planted[i].transform.localScale.x * 0.5f, markers.Width, markers.Height);
                Sink.AtLeast("marker_contrast_" + distances[i].ToString("0") + "m", ImageStats.MarkerContrast(markers, rects[i]), 0.6);
            }
            Clear();

            // Particles: 25 planted dark spheres on a 5 x 5 grid 300 m ahead; the count must be 25.
            Pose(new CameraPose(start.Position, 0f, -32f));
            for (int row = 0; row < 5; row++)
                for (int column = 0; column < 5; column++)
                {
                    Vector3 direction = Quaternion.Euler(-(10f + row * 5f), (column - 2) * 7f, 0f) * Vector3.forward;
                    Sphere(start.Position + direction * 300f, 2f);
                }
            yield return Frames(3);
            LumaFrame particles = null;
            yield return Grab(f => particles = f);
            Sink.Reference("particle_count", ImageStats.CountParticles(particles, 0.1f, 12, 8), Planted, 0.0, 0.5);
            Clear();

            // Opacity over five levels: the light on the ground is dimmed to 1, 0.8, 0.6, 0.4 and 0.2 of itself (the sun and the ambient),
            // so the curve rises with the level: by at least 0.03 a step, and to 0.8 +- 5 % at the last (opacity = 1 - dimming, as measured on the first run). The region is the
            // ground below the horizon; the clear mean is the same region of the clear frame.
            Pose(start);
            var ground = new ImageStats.Rect2(0, 0, Width, Height / 4);
            double clearGround = ImageStats.MeanLuma(clear, ground);
            float[] dimming = { 1f, 0.8f, 0.6f, 0.4f, 0.2f };
            var opacities = new List<double>();
            Color ambient = RenderSettings.ambientLight;
            float sun = Sun.intensity;
            for (int level = 0; level < dimming.Length; level++)
            {
                Sun.intensity = sun * dimming[level];
                RenderSettings.ambientLight = ambient * dimming[level];
                yield return Frames(3);
                LumaFrame frame = null;
                yield return Grab(f => frame = f);
                double opacity = ImageStats.Opacity(ImageStats.MeanLuma(frame, ground), clearGround);
                opacities.Add(opacity);
                Sink.Info("opacity_level_" + level, opacity);
            }
            Sun.intensity = sun;
            RenderSettings.ambientLight = ambient;
            Sink.Flag("opacity_monotonic", ImageStats.Monotonic(opacities, 0.03));
            Sink.Reference("opacity_level_4", opacities[4], 0.8, 0.05, 0.0);

            // The camera paths, one pose a frame, with the frame-indexed clock and the timers.
            var timers = new FrameTimers();
            bool gpu = timers.StartGpu();
            Bounds volume = new Bounds(new Vector3(2000f, 600f, 3000f), new Vector3(4000f, 800f, 4000f));
            foreach (string name in CameraPaths.Names)
            {
                List<CameraPose> path = CameraPaths.Make(name, start, Vehicle.transform.position, volume);
                int before = FrameIndex;
                timers.Clear();
                foreach (CameraPose pose in path)
                {
                    Pose(pose);
                    yield return Frames(1);
                    timers.Sample();
                }
                Sink.Flag("path_" + name + "_frames", FrameIndex - before == path.Count);
                if (name == "turn2" || name == "flyby100")
                {
                    Sink.Info("frame_ms_cpu_" + name, timers.CpuMean);
                    Sink.Info("frame_ms_p95_" + name, timers.CpuPercentile(0.95));
                    if (gpu) Sink.Info("frame_ms_gpu_" + name, timers.GpuMean);
                }
            }
            Sink.Flag("clock_is_frame_indexed", System.Math.Abs(ClockSeconds * 60.0 - FrameIndex) < 1e-9 && FrameIndex > 0);
            Sink.Flag("vehicle_lamps", VehicleLamps != null && VehicleLamps.Length == 2 && VehicleLamps[0].type == LightType.Spot);
            Sink.Flag("coast_and_sea", TerrainHeight(-500f, 0f) < SeaLevel && TerrainHeight(1500f, 0f) > SeaLevel);
        }
    }
}
