using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The screen as the game drew it in one frame, as bytes (row 0 at the bottom, as Unity gives them).
    internal sealed class ProbeScreen
    {
        internal int Width, Height, Frame;
        internal Color32[] Pixels;
        internal byte[] Png;
    }

    // The mean colour of the ground in a picture: the lower left of the frame (the part of the frame the K-S2 far poses show lawn in and no building or vessel), as fractions of the
    // picture so that every size is measured at the same place.
    internal static class GroundRegion
    {
        // From the left edge to XMax of the width, from YMin to YMax of the height counted from the bottom.
        internal const double XMax = 0.273, YMin = 0.014, YMax = 0.181;

        internal static void Mean(Color32[] pixels, int width, int height, out double r, out double g, out double b)
        {
            r = g = b = double.NaN;
            if (pixels == null || width <= 0 || height <= 0 || pixels.Length < width * height) return;
            int x1 = Math.Max(1, (int)(width * XMax)), y0 = (int)(height * YMin), y1 = Math.Max(y0 + 1, (int)(height * YMax));
            double sr = 0, sg = 0, sb = 0;
            long n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < x1; x++)
                {
                    Color32 c = pixels[y * width + x];
                    sr += c.r;
                    sg += c.g;
                    sb += c.b;
                    n++;
                }
            if (n == 0) return;
            r = sr / n;
            g = sg / n;
            b = sb / n;
        }

        // How far the ground leans to green over grey, as tools/vanilla_ground.py measures it (its 'low_green'): the lower-left box of the picture (x 0 to 33 percent, the bottom 15
        // percent), the mean of G - (R + B) / 2 in 0 to 255 units. Through the space centre the KSC ground reads about +11 there, straight from the main menu about +4.
        internal const double GreenXMax = 0.33, GreenYMax = 0.15;

        internal static double GreenShare(Color32[] pixels, int width, int height)
        {
            if (pixels == null || width <= 0 || height <= 0 || pixels.Length < width * height) return double.NaN;
            int x1 = Math.Max(1, (int)(width * GreenXMax)), y1 = Math.Max(1, (int)(height * GreenYMax));
            double sum = 0;
            long n = 0;
            for (int y = 0; y < y1; y++)
                for (int x = 0; x < x1; x++)
                {
                    Color32 c = pixels[y * width + x];
                    sum += c.g - 0.5 * (c.r + c.b);
                    n++;
                }
            return n == 0 ? double.NaN : sum / n;
        }

        // The largest difference of a channel between two means (NaN where either is not there).
        internal static double MaxDifference(double r1, double g1, double b1, double r2, double g2, double b2)
        {
            if (double.IsNaN(r1) || double.IsNaN(r2)) return double.NaN;
            return Math.Max(Math.Abs(r1 - r2), Math.Max(Math.Abs(g1 - g2), Math.Abs(b1 - b2)));
        }
    }

    // What the capture A/B probe asks of the running game besides the viewer: a picture of the screen at the end of the frame a condition holds in, the camera put where a pose says
    // with the vessel left where it stands (as tools/ProbeLite does), and a description of every camera. A test replaces it.
    internal interface IProbeHost
    {
        // Calls done with the screen at the end of the first frame in which when() holds (when is asked at the end of each frame, after every Update and LateUpdate).
        // With png the picture is encoded as well (an encode of 2560x1440 stalls frames: only the last picture of a series asks for it).
        void Grab(Func<bool> when, Action<ProbeScreen> done, bool png);
        // The game's interface (stage stack, clock, readouts) away or back, as the viewer's capture does; HideUi says whether it was showing and is now hidden by this call.
        bool HideUi();
        void ShowUi();
        bool UiShowing { get; }
        // The flight camera is put every frame at the pose; the vessel is not moved. Heading and elevation in degrees, the camera's height over the PQS terrain and its distance
        // behind the vessel's spot.
        void HoldCamera(double headingDeg, double elevationDeg, double distanceM, double cameraAglM);
        void ReleaseCamera();
        bool CameraHeld { get; }
        // One line per camera: name, depth, culling mask, target texture, enabled.
        string DescribeCameras();
    }

    // The game's host: a component that lives through the flight, ticking at the end of each frame and in each LateUpdate.
    internal sealed class GameProbeHost : MonoBehaviour, IProbeHost
    {
        private sealed class Pending
        {
            internal Func<bool> When;
            internal Action<ProbeScreen> Done;
            internal bool Png;
        }

        private static GameProbeHost instance;
        private readonly List<Pending> pending = new List<Pending>();
        private bool held;
        private double heading, elevation, distance, cameraAgl, siteLat = double.NaN, siteLon = double.NaN;

        internal static GameProbeHost Get()
        {
            if (instance != null) return instance;
            var go = new GameObject("ReDefinitionProbeHost");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<GameProbeHost>();
            return instance;
        }

        private void Start()
        {
            StartCoroutine(EndOfFrames());
        }

        public bool CameraHeld
        {
            get { return held; }
        }

        public void Grab(Func<bool> when, Action<ProbeScreen> done, bool png)
        {
            pending.Add(new Pending { When = when, Done = done, Png = png });
        }

        private bool hiddenByUs;

        public bool UiShowing
        {
            get { return KSP.UI.UIMasterController.Instance != null && KSP.UI.UIMasterController.Instance.IsUIShowing; }
        }

        public bool HideUi()
        {
            KSP.UI.UIMasterController ui = KSP.UI.UIMasterController.Instance;
            if (ui == null || !ui.IsUIShowing) return false;
            ui.HideUI();
            hiddenByUs = true;
            return true;
        }

        public void ShowUi()
        {
            if (!hiddenByUs) return;
            hiddenByUs = false;
            if (KSP.UI.UIMasterController.Instance != null) KSP.UI.UIMasterController.Instance.ShowUI();
        }

        private IEnumerator EndOfFrames()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    Pending p = pending[i];
                    bool hold;
                    try { hold = p.When(); }
                    catch (Exception) { hold = false; }
                    if (!hold) continue;
                    pending.RemoveAt(i);
                    Texture2D tex = null;
                    try
                    {
                        tex = ScreenCapture.CaptureScreenshotAsTexture();
                        p.Done(new ProbeScreen { Width = tex.width, Height = tex.height, Frame = Time.frameCount, Pixels = tex.GetPixels32(), Png = p.Png ? tex.EncodeToPNG() : null });
                    }
                    catch (Exception e)
                    {
                        AtmospheresLog.Failed("probe host", "the screen could not be read", e);
                        p.Done(null);
                    }
                    finally
                    {
                        if (tex != null) Destroy(tex);
                    }
                }
            }
        }

        public void HoldCamera(double headingDeg, double elevationDeg, double distanceM, double cameraAglM)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || FlightCamera.fetch == null) return;
            if (!held)
            {
                siteLat = v.latitude;
                siteLon = v.longitude;
                FlightCamera.fetch.DeactivateUpdate();
            }
            heading = headingDeg;
            elevation = elevationDeg;
            distance = distanceM;
            cameraAgl = cameraAglM;
            held = true;
        }

        public void ReleaseCamera()
        {
            if (!held) return;
            held = false;
            if (FlightCamera.fetch != null) FlightCamera.fetch.ActivateUpdate();
        }

        private void LateUpdate()
        {
            if (!held || FlightCamera.fetch == null) return;
            try
            {
                CelestialBody body = FlightGlobals.currentMainBody;
                if (body == null || double.IsNaN(siteLat)) return;
                double h = heading * Math.PI / 180.0, el = elevation * Math.PI / 180.0;
                Vector3d up0, north0, east0;
                Frame(body, siteLat, siteLon, out up0, out north0, out east0);
                Vector3d site = body.GetWorldSurfacePosition(siteLat, siteLon, body.TerrainAltitude(siteLat, siteLon, true));
                Vector3d horizontal = north0 * Math.Cos(h) + east0 * Math.Sin(h);
                Vector3d approx = site - horizontal * (distance * Math.Cos(el));
                double lat, lon, alt;
                body.GetLatLonAlt(approx, out lat, out lon, out alt);
                Vector3d position = body.GetWorldSurfacePosition(lat, lon, body.TerrainAltitude(lat, lon, true) + cameraAgl);
                Vector3d up, north, east;
                Frame(body, lat, lon, out up, out north, out east);
                Vector3d forward = (north * Math.Cos(h) + east * Math.Sin(h)) * Math.Cos(el) + up * Math.Sin(el);
                Transform view = FlightCamera.fetch.transform;
                view.position = (Vector3)position;
                view.rotation = Quaternion.LookRotation((Vector3)forward, (Vector3)up);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("probe host", "the camera could not be put at the pose", e);
                held = false;
            }
        }

        // The local frame at a place: up from the body's centre, north along the meridian, east.
        private static void Frame(CelestialBody body, double lat, double lon, out Vector3d up, out Vector3d north, out Vector3d east)
        {
            Vector3d here = body.GetWorldSurfacePosition(lat, lon, 0.0);
            Vector3d further = body.GetWorldSurfacePosition(lat + 0.5, lon, 0.0);
            up = (here - body.position).normalized;
            Vector3d along = further - here;
            north = (along - up * Vector3d.Dot(along, up)).normalized;
            east = Vector3d.Cross(up, north);
        }

        public string DescribeCameras()
        {
            var text = new StringBuilder();
            try
            {
                Camera[] all = Camera.allCameras;
                Array.Sort(all, (a, b) => a.depth.CompareTo(b.depth));
                foreach (Camera c in all)
                {
                    if (text.Length > 0) text.Append("; ");
                    text.Append(c.name).Append(" depth ").Append(c.depth.ToString("0.#")).Append(" mask 0x").Append(c.cullingMask.ToString("x")).Append(c.targetTexture != null ? " texture " + c.targetTexture.width + "x" + c.targetTexture.height : " screen");
                }
            }
            catch (Exception e)
            {
                text.Append("cameras could not be listed (" + e.GetType().Name + ")");
            }
            return text.ToString();
        }
    }
}
