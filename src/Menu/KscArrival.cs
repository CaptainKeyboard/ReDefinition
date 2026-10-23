using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Menu
{
    // The way from the main menu to the space centre as one flight: the menu's camera
    // closes in on Kerbin over the space centre's place; while KSP loads -- the game
    // stands still for seconds there and draws nothing (measured 2026-10-05: 2.5 s and
    // 1.6 s) -- the last picture of it stays on the screen in place of KSP's black
    // loading screen; in the space centre the camera starts over the same place at the
    // same height, the picture fades and the camera comes down to KSP's own view. On the
    // way in the menu's sun turns to stand over the space centre as the save's does, so
    // that a save at night is flown into the night side (MenuSun).
    // A click or a key ends the descent there. OwnSettings.MenuKscZoom switches it.
    internal static class KscArrival
    {
        // The space centre's place on Kerbin (KSP's launch pad).
        internal const double Latitude = -0.0972;
        internal const double Longitude = -74.5577;
        // How high over the place the menu's camera ends and the space centre's starts,
        // in Kerbin's radii.
        internal const float Height = 0.35f;

        private static bool installed;
        private static bool passing;
        // The picture left from the menu, waiting for the space centre.
        internal static KscArrivalOverlay Overlay;
        // -redefinitionKscArrivalTest: from the menu into the probe's save by itself, with
        // screenshots on the way into ReDefinitionCaptures, then quit.
        internal static readonly bool Test = Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionKscArrivalTest") >= 0;
        private static string testFolder;

        internal static void TestShot(string name)
        {
            if (!Test) return;
            if (testFolder == null)
            {
                testFolder = System.IO.Path.Combine(Core.CaptureFolder.Root,
                                                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-ksc-arrival");
                System.IO.Directory.CreateDirectory(testFolder);
            }
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(testFolder, name + ".png"));
        }

        internal static bool Enabled()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            return addon == null || addon.Live.MenuKscZoom;
        }

        internal static void Install()
        {
            if (installed) return;
            installed = true;
            HarmonyHooks.Safely("ksc-arrival", "The way from the main menu to the space centre stays KSP's", () =>
                HarmonyHooks.Install("ReDefinition.KscArrival", typeof(KscArrival), new List<HarmonyHook>
                {
                    new HarmonyHook(typeof(HighLogic).GetMethod("LoadScene", new[] { typeof(GameScenes) }), nameof(LoadScenePrefix),
                                    null, null),
                }));
        }

        // The menu's load of the space centre waits for the camera's way in.
        private static bool LoadScenePrefix(GameScenes scene)
        {
            if (passing || scene != GameScenes.SPACECENTER || HighLogic.LoadedScene != GameScenes.MAINMENU || !Enabled())
                return true;
            KscArrivalMenu menu = KscArrivalMenu.Instance;
            return menu == null || !menu.Begin();
        }

        internal static void LoadSpaceCentre()
        {
            passing = true;
            try
            {
                HighLogic.LoadScene(GameScenes.SPACECENTER);
            }
            finally
            {
                passing = false;
            }
        }

        // The direction from Kerbin's centre to the space centre in the body's own frame,
        // and its north there.
        internal static bool LocalFrame(CelestialBody body, out Vector3 up, out Vector3 north)
        {
            up = north = Vector3.zero;
            if (body == null) return false;
            Vector3d centre = body.position;
            Vector3d here = body.GetWorldSurfacePosition(Latitude, Longitude, 0.0) - centre;
            Vector3d further = body.GetWorldSurfacePosition(Latitude + 0.5, Longitude, 0.0) - centre;
            Vector3 worldUp = ((Vector3)here).normalized;
            Vector3 worldNorth = Vector3.ProjectOnPlane((Vector3)(further - here), worldUp).normalized;
            up = body.transform.InverseTransformDirection(worldUp);
            north = body.transform.InverseTransformDirection(worldNorth);
            return north.sqrMagnitude > 0.5f;
        }

        // The sun's hour angle over the space centre, in degrees: 0 at noon, -90 at sunrise,
        // +90 at sunset, from the sun's direction there in the local up and north.
        internal static float HourAngle(Vector3 sunDirection, Vector3 up, Vector3 north)
        {
            Vector3 east = Vector3.Cross(up, north);
            return Mathf.Atan2(-Vector3.Dot(sunDirection, east), Vector3.Dot(sunDirection, up)) * Mathf.Rad2Deg;
        }

        // The direction of the sun at that hour angle, in the local up and north.
        internal static Vector3 SunAt(float hourAngle, Vector3 up, Vector3 north)
        {
            float h = hourAngle * Mathf.Deg2Rad;
            return Mathf.Cos(h) * up - Mathf.Sin(h) * Vector3.Cross(up, north);
        }

        // The sun's hour angle over the space centre now, as the bodies stand.
        internal static bool HourAngleNow(CelestialBody body, out float hourAngle)
        {
            hourAngle = 0f;
            CelestialBody sun = body != null ? body.referenceBody : null;
            Vector3 up, north;
            if (sun == null || sun == body || !LocalFrame(body, out up, out north)) return false;
            Vector3d here = body.GetWorldSurfacePosition(Latitude, Longitude, 0.0);
            hourAngle = HourAngle(((Vector3)(sun.position - here)).normalized, body.transform.TransformDirection(up),
                                  body.transform.TransformDirection(north));
            return true;
        }

        // The sun's hour angle over the space centre at the save's time: now's, turned on by
        // the solar day's share of the time between (the body's turn less its way round the
        // sun). Null where there is no save or no sun. Checked in the space centre against
        // the sun there (the descent's log line).
        internal static float? SaveHourAngle()
        {
            Game game = HighLogic.CurrentGame;
            CelestialBody body = Home();
            float now;
            if (game == null || game.flightState == null || body == null || body.orbit == null || !HourAngleNow(body, out now))
                return null;
            double between = game.flightState.universalTime - Planetarium.GetUniversalTime();
            double perSecond = 360.0 / body.rotationPeriod - 360.0 / body.orbit.period;
            double angle = (now + between * perSecond) % 360.0;
            if (angle > 180.0) angle -= 360.0;
            if (angle < -180.0) angle += 360.0;
            return (float)angle;
        }

        // The hour angle the menu showed, for the space centre's check.
        internal static float? PlannedHourAngle;

        // The menu camera's vertical field of view at the end of the way in: the space
        // centre's camera starts with it, so that the planet stands as large on the screen.
        internal static float MenuFieldOfView;

        // How much of the screen's height the planet's disc fills, seen from `distance`
        // off its centre: its angular radius against half the field of view.
        internal static float ScreenShare(float radius, float distance, float fieldOfView)
        {
            float angle = Mathf.Asin(Mathf.Clamp01(radius / Mathf.Max(distance, radius)));
            return Mathf.Tan(angle) / Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
        }

        // In the test, the way down frame by frame: a picture each tenth of a second and
        // the planet's share of the screen, to see the whole movement.
        private static float nextFrameShot;
        private static readonly bool NoShots = Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionKscArrivalNoShots") >= 0;

        // Each part's frame times, into the log at its end: how smooth the way was.
        private static int frames;
        private static float frameSum, frameMost;

        internal static void FrameTime()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            frames++;
            frameSum += ms;
            frameMost = Mathf.Max(frameMost, ms);
            if (ms > 100f)
                Debug.Log(Log.Tag + " Menu to the space centre: a frame of " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms.");
        }

        internal static void FrameTimes(string part)
        {
            if (frames > 0)
                Debug.Log(Log.Tag + " Menu to the space centre: " + part + ": " + frames + " frames, "
                          + (frameSum / frames).ToString("0.0", CultureInfo.InvariantCulture) + " ms on average, the longest "
                          + frameMost.ToString("0.0", CultureInfo.InvariantCulture) + " ms.");
            frames = 0;
            frameSum = frameMost = 0f;
        }

        internal static void TestFrame(string part, float elapsed, float share)
        {
            if (!Test || NoShots) return;
            if (elapsed < nextFrameShot) return;
            nextFrameShot = elapsed + 0.1f;
            string name = part + "-" + elapsed.ToString("00.00", CultureInfo.InvariantCulture);
            TestShot(name);
            Debug.Log(Log.Tag + " Menu to the space centre: frame " + name + ", the planet " + share.ToString("0.000", CultureInfo.InvariantCulture)
                      + " of the screen's height.");
        }

        internal static void TestFramesFrom(float elapsed)
        {
            nextFrameShot = elapsed;
        }

        internal static CelestialBody Home()
        {
            if (FlightGlobals.Bodies != null)
                foreach (CelestialBody body in FlightGlobals.Bodies)
                    if (body != null && body.isHomeWorld) return body;
            if (PSystemManager.Instance != null && PSystemManager.Instance.localBodies != null)
                foreach (CelestialBody body in PSystemManager.Instance.localBodies)
                    if (body != null && body.isHomeWorld) return body;
            return null;
        }

        internal static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // The temporal antialiasing of the other mods started afresh, each frame of the way
        // down and once at its end: to them the camera's flight is a cut in every frame.
        // Scatterer's TAA, left with it, turned the picture to NaN for good, shown black by
        // TUFX's post-processing -- in the space centre after the way down, with ReDefinition's
        // upscaler off (which takes that TAA off while it runs), and not after KSP's own load
        // (KscArrivalCheck, 2026-10-23); it keeps its history when switched off and on, and
        // makes it anew only on a reset (TemporalAntiAliasing.ResetHistory, CheckHistory;
        // Scatterer 0.908, decompiled). TUFX's PostProcessing takes the same
        // (PostProcessLayer.ResetHistory).
        private static readonly Dictionary<Type, System.Reflection.MethodInfo> resets =
            new Dictionary<Type, System.Reflection.MethodInfo>();
        private static bool resetWarned;

        internal static void ResetTemporalHistories()
        {
            try
            {
                foreach (Camera camera in Camera.allCameras)
                {
                    if (camera == null) continue;
                    foreach (Component component in camera.GetComponents<Component>())
                    {
                        if (component == null) continue;
                        Type type = component.GetType();
                        System.Reflection.MethodInfo reset;
                        if (!resets.TryGetValue(type, out reset))
                        {
                            reset = type.FullName == "Scatterer.TemporalAntiAliasing"
                                    || type.FullName == "UnityEngine.Rendering.PostProcessing.PostProcessLayer"
                                ? type.GetMethod("ResetHistory", TypeLookup.Any, null, Type.EmptyTypes, null)
                                : null;
                            resets[type] = reset;
                        }
                        if (reset != null) reset.Invoke(component, null);
                    }
                }
            }
            catch (Exception e)
            {
                if (resetWarned) return;
                resetWarned = true;
                CompatibilityLog.Warn("ksc-arrival-taa", "The other mods' temporal antialiasing could not be started afresh"
                                                         + " after the way down (" + CompatibilityLog.Reason(e) + ").");
            }
        }
    }

    // The menu's half: the camera's way in, the picture, then KSP's load.
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    internal sealed class KscArrivalMenu : MonoBehaviour
    {
        internal static KscArrivalMenu Instance;
        private const float Seconds = 2.2f;
        private bool running;

        private void Awake()
        {
            Instance = this;
            KscArrival.Install();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private IEnumerator Start()
        {
            if (!KscArrival.Test) yield break;
            yield return new WaitForSeconds(4f);
            Game game = GamePersistence.LoadGame("persistent", Probe.SceneProbe.SaveFolder, true, false);
            if (game == null) yield break;
            HighLogic.SaveFolder = Probe.SceneProbe.SaveFolder;
            HighLogic.CurrentGame = game;
            game.startScene = GameScenes.SPACECENTER;
            game.Start();
        }

        // True where the way in starts; KSP's load comes at its end.
        internal bool Begin()
        {
            if (running) return true;
            try
            {
                MainMenuEnvLogic env = FindObjectOfType<MainMenuEnvLogic>();
                Transform kerbin = null;
                float radius = 0f;
                if (env != null && env.areas != null)
                    foreach (GameObject area in env.areas)
                    {
                        if (area == null || !area.activeInHierarchy) continue;
                        foreach (MeshFilter filter in area.GetComponentsInChildren<MeshFilter>())
                            if (filter.name == "Kerbin" && filter.sharedMesh != null)
                            {
                                kerbin = filter.transform;
                                radius = filter.sharedMesh.bounds.extents.y * kerbin.lossyScale.y;
                            }
                    }
                Vector3 up, north;
                if (env == null || env.landscapeCamera == null || kerbin == null
                    || !KscArrival.LocalFrame(KscArrival.Home(), out up, out north))
                {
                    Debug.Log(Log.Tag + " Menu to the space centre: no menu planet or no Kerbin found; KSP's own load.");
                    return false;
                }
                running = true;
                // The menu's own camera chase stands still from here: it would pull the camera
                // back to its stage as soon as the way in stops placing it (measured
                // 2026-10-05: the planet shrank again after the way in, the player's "zooms
                // out again").
                env.enabled = false;
                StartCoroutine(WayIn(env.landscapeCamera, kerbin, radius, up, north));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Menu to the space centre: " + e.Message + "; KSP's own load.");
                return false;
            }
        }

        private IEnumerator WayIn(Camera camera, Transform kerbin, float radius, Vector3 localUp, Vector3 localNorth)
        {
            var sun = new MenuSun();
            float? hourAngle = sun.Sun != null ? KscArrival.SaveHourAngle() : null;
            KscArrival.PlannedHourAngle = hourAngle;
            if (hourAngle.HasValue)
                Debug.Log(Log.Tag + " Menu to the space centre: the save's sun over the space centre at "
                          + hourAngle.Value.ToString("0", CultureInfo.InvariantCulture) + " degrees from noon, the menu's at "
                          + KscArrival.HourAngle(-sun.Sun.transform.forward, kerbin.TransformDirection(localUp), kerbin.TransformDirection(localNorth))
                              .ToString("0", CultureInfo.InvariantCulture) + " ('" + sun.Sun.name + "').");
            List<Canvas> hidden = new List<Canvas>();
            foreach (Canvas canvas in FindObjectsOfType<Canvas>())
                if (canvas.enabled && canvas.isRootCanvas)
                {
                    canvas.enabled = false;
                    hidden.Add(canvas);
                }

            Transform view = camera.transform;
            Vector3 startOffset = view.position - kerbin.position;
            Quaternion startRotation = view.rotation;
            float startDistance = startOffset.magnitude;
            float endDistance = radius * (1f + KscArrival.Height);
            Vector3 startScreen = camera.WorldToViewportPoint(kerbin.position + kerbin.TransformDirection(localUp) * radius);
            Debug.Log(Log.Tag + " Menu to the space centre: the space centre's place at screen ("
                      + startScreen.x.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                      + startScreen.y.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                      + (startScreen.z > 0f ? "ahead" : "behind") + "), facing the camera "
                      + Vector3.Dot(kerbin.TransformDirection(localUp), startOffset.normalized).ToString("0.00", CultureInfo.InvariantCulture)
                      + "; the camera from " + (startDistance / radius).ToString("0.00", CultureInfo.InvariantCulture) + " to "
                      + (endDistance / radius).ToString("0.00", CultureInfo.InvariantCulture) + " radii.");

            float started = Time.unscaledTime;
            KscArrival.TestFramesFrom(0f);
            while (true)
            {
                float t = (Time.unscaledTime - started) / Seconds;
                float e = KscArrival.Ease(t);
                Vector3 up = kerbin.TransformDirection(localUp);
                Vector3 north = kerbin.TransformDirection(localNorth);
                Vector3 direction = Vector3.Slerp(startOffset.normalized, up, e);
                float distance = Mathf.Lerp(startDistance, endDistance, e);
                view.position = kerbin.position + direction * distance;
                Quaternion end = Quaternion.LookRotation(-up, north);
                view.rotation = Quaternion.Slerp(startRotation, end, e);
                if (hourAngle.HasValue) sun.Turn(KscArrival.SunAt(hourAngle.Value, up, north), e);
                if (t >= 1f) break;
                KscArrival.TestFrame("1-menu", Time.unscaledTime - started, KscArrival.ScreenShare(radius, distance, camera.fieldOfView));
                yield return null;
                KscArrival.FrameTime();
            }
            KscArrival.FrameTimes("the way in");

            if (hourAngle.HasValue)
                Debug.Log(Log.Tag + " Menu to the space centre: the menu's sun at the end "
                          + KscArrival.HourAngle(-sun.Sun.transform.forward, kerbin.TransformDirection(localUp), kerbin.TransformDirection(localNorth))
                              .ToString("0", CultureInfo.InvariantCulture) + " degrees from noon.");
            KscArrival.MenuFieldOfView = camera.fieldOfView;
            yield return new WaitForEndOfFrame();
            KscArrival.TestShot("1-menu-end");
            Texture2D picture = ScreenCapture.CaptureScreenshotAsTexture();
            KscArrival.Overlay = KscArrivalOverlay.Show(picture);
            foreach (Canvas canvas in hidden)
                if (canvas != null) canvas.enabled = true;
            yield return null;
            KscArrival.LoadSpaceCentre();
        }
    }

    // The menu's sun turned to an hour angle over the space centre. Every directional
    // light of the menu turns with it. Parallax's main menu sets the direction of
    // PlanetLight, FillLight and RenderSettings.sun in each Update (ScaledMainMenu.Update),
    // so the lights are turned each frame after that, from where they stand then; a light
    // nobody set again since the last turn turns from where it stood before it, so that
    // it does not turn on and on.
    internal sealed class MenuSun
    {
        private readonly List<Light> lights = new List<Light>();
        private readonly Dictionary<Light, Quaternion> before = new Dictionary<Light, Quaternion>();
        private readonly Dictionary<Light, Quaternion> written = new Dictionary<Light, Quaternion>();
        internal readonly Light Sun;

        internal MenuSun()
        {
            foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                if (light.type == LightType.Directional && light.isActiveAndEnabled)
                {
                    lights.Add(light);
                    // The sun as Scatterer and Parallax take it in the menu: PlanetLight; in
                    // KSP's own Mun scene "Directional light", not the brighter BackLight
                    // that only rims Kerbin (MenuFireworks.Rank).
                    if (Sun == null || MenuFireworks.Rank(light) > MenuFireworks.Rank(Sun)
                        || (MenuFireworks.Rank(light) == MenuFireworks.Rank(Sun) && light.name.Contains("PlanetLight")
                            && !Sun.name.Contains("PlanetLight")))
                        Sun = light;
                }
        }

        // The lights turned so that the sun shines from `towards`, `share` of the way from
        // where it stands now.
        internal void Turn(Vector3 towards, float share)
        {
            if (Sun == null) return;
            foreach (Light light in lights)
            {
                if (light == null) continue;
                Quaternion last;
                if (!written.TryGetValue(light, out last) || light.transform.rotation != last)
                    before[light] = light.transform.rotation;
            }
            Vector3 from = -(before[Sun] * Vector3.forward);
            Quaternion turn = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(from, towards), share);
            foreach (Light light in lights)
            {
                if (light == null) continue;
                light.transform.rotation = turn * before[light];
                written[light] = light.transform.rotation;
            }
        }
    }

    // The picture over everything until the space centre's camera takes over.
    internal sealed class KscArrivalOverlay : MonoBehaviour
    {
        private RawImage image;
        private Texture2D picture;

        internal static KscArrivalOverlay Show(Texture2D picture)
        {
            var root = new GameObject("ReDefinition menu to space centre");
            DontDestroyOnLoad(root);
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var child = new GameObject("Picture", typeof(RectTransform));
            child.transform.SetParent(root.transform, false);
            var rect = (RectTransform)child.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            KscArrivalOverlay overlay = root.AddComponent<KscArrivalOverlay>();
            overlay.picture = picture;
            overlay.image = child.AddComponent<RawImage>();
            overlay.image.texture = picture;
            overlay.image.raycastTarget = false;
            return overlay;
        }

        internal void Fade(float alpha)
        {
            if (image != null) image.color = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
        }

        internal void Close()
        {
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (picture != null) Destroy(picture);
            if (KscArrival.Overlay == this) KscArrival.Overlay = null;
        }
    }

    // The space centre's half: from over the place down to KSP's own view.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    internal sealed class KscArrivalDescent : MonoBehaviour
    {
        private const float Seconds = 5f;
        private const float FadeSeconds = 0.8f;
        // The picture held whole this long first, the camera still: the space centre's
        // first frames are not yet its picture (measured 2026-10-05 under the picture:
        // rings at 0.05 s, black at 0.4 s, the planet from 0.7 s).
        private const float HoldSeconds = 1f;
        private bool interfaceHidden;

        private void OnDestroy()
        {
            // Left early, the interface comes back all the same, and the picture goes: it
            // outlives the scene, and the next scene would start under it.
            if (interfaceHidden) GameEvents.onShowUI.Fire();
            if (KscArrival.Overlay != null) KscArrival.Overlay.Close();
        }

        private IEnumerator Start()
        {
            KscArrivalOverlay overlay = KscArrival.Overlay;
            if (overlay == null)
            {
                // In the test without the way in (MenuKscZoom off): the same check after KSP's
                // own load, to compare.
                if (!KscArrival.Test) yield break;
                yield return new WaitForSeconds(8f);
                KscArrival.TestShot("3-ksp-view");
                yield return KscArrivalCheck.Run();
                Application.Quit();
                yield break;
            }
            SpaceCenterCamera2 control = null;
            for (int i = 0; i < 300 && control == null; i++)
            {
                control = FindObjectOfType<SpaceCenterCamera2>();
                if (control == null) yield return null;
            }
            // Two frames for KSP to set its camera where it starts.
            yield return null;
            yield return null;
            CelestialBody body = KscArrival.Home();
            Vector3 localUp, localNorth;
            if (control == null || FlightCamera.fetch == null || !KscArrival.LocalFrame(body, out localUp, out localNorth))
            {
                Debug.Log(Log.Tag + " Menu to the space centre: no space centre camera; the picture goes.");
                overlay.Close();
                yield break;
            }

            Transform view = FlightCamera.fetch.transform;
            Vector3 endPosition = view.position;
            Quaternion endRotation = view.rotation;
            Vector3 endLocalPosition = view.localPosition;
            Quaternion endLocalRotation = view.localRotation;
            Vector3 place = (Vector3)body.GetWorldSurfacePosition(KscArrival.Latitude, KscArrival.Longitude, 0.0);
            Vector3 up = body.transform.TransformDirection(localUp);
            Vector3 north = body.transform.TransformDirection(localNorth);
            float startHeight = (float)body.Radius * KscArrival.Height;
            Quaternion startRotation = Quaternion.LookRotation(-up, north);
            Vector3 endOffset = endPosition - place;
            float endDistance = endOffset.magnitude;
            control.enabled = false;
            float actual;
            if (KscArrival.PlannedHourAngle.HasValue && KscArrival.HourAngleNow(body, out actual))
                Debug.Log(Log.Tag + " Menu to the space centre: the sun over the space centre at " + actual.ToString("0", CultureInfo.InvariantCulture)
                          + " degrees from noon; the menu showed it at " + KscArrival.PlannedHourAngle.Value.ToString("0", CultureInfo.InvariantCulture) + ".");
            Debug.Log(Log.Tag + " Menu to the space centre: the descent from " + (startHeight / 1000f).ToString("0", CultureInfo.InvariantCulture)
                      + " km to KSP's view " + endDistance.ToString("0", CultureInfo.InvariantCulture) + " m from the pad.");

            // The menu's field of view first, KSP's at the end.
            float endFieldOfView = FlightCamera.fetch.mainCamera != null ? FlightCamera.fetch.mainCamera.fieldOfView : 60f;
            float startFieldOfView = KscArrival.MenuFieldOfView > 1f ? KscArrival.MenuFieldOfView : endFieldOfView;
            FlightCamera.fetch.SetFoV(startFieldOfView);
            float planetRadius = (float)body.Radius;
            // KSP's interface away on the way down, as with F2, and back at the end.
            GameEvents.onHideUI.Fire();
            interfaceHidden = true;
            float held = Time.unscaledTime;
            KscArrival.TestFramesFrom(0f);
            view.position = place + up * startHeight;
            view.rotation = startRotation;
            while (Time.unscaledTime - held < HoldSeconds)
            {
                view.position = place + up * startHeight;
                view.rotation = startRotation;
                float waited = Time.unscaledTime - held;
                FlightCamera.fetch.SetFoV(startFieldOfView);
                KscArrival.ResetTemporalHistories();
                KscArrival.TestFrame("2-hold", waited, KscArrival.ScreenShare(planetRadius, planetRadius + startHeight, startFieldOfView));
                if (waited > 0f) KscArrival.FrameTime();
                yield return null;
            }

            KscArrival.FrameTimes("the held picture");
            float started = Time.unscaledTime;
            KscArrival.TestFramesFrom(0f);
            bool skipped = false;
            while (true)
            {
                float elapsed = Time.unscaledTime - started;
                overlay.Fade(1f - elapsed / FadeSeconds);
                float t = elapsed / Seconds;
                // Height falls evenly in its logarithm: as fast in the last metres as in
                // the first hundred kilometres, seen from the camera.
                float e = KscArrival.Ease(t);
                float distance = Mathf.Exp(Mathf.Lerp(Mathf.Log(startHeight), Mathf.Log(Mathf.Max(endDistance, 1f)), e));
                Vector3 direction = Vector3.Slerp(up, endOffset / Mathf.Max(endDistance, 1f), e);
                view.position = place + direction * distance;
                view.rotation = Quaternion.Slerp(startRotation, endRotation, e);
                float fieldOfView = Mathf.Lerp(startFieldOfView, endFieldOfView, e);
                FlightCamera.fetch.SetFoV(fieldOfView);
                KscArrival.ResetTemporalHistories();
                KscArrival.TestFrame("3-down", elapsed, KscArrival.ScreenShare(planetRadius, (float)((Vector3d)view.position - body.position).magnitude, fieldOfView));
                if (elapsed > 0f) KscArrival.FrameTime();
                if (t >= 1f) break;
                if (elapsed > 0.3f && (Input.anyKeyDown || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))
                {
                    skipped = true;
                    break;
                }
                yield return null;
            }
            KscArrival.FrameTimes("the way down");
            view.localPosition = endLocalPosition;
            view.localRotation = endLocalRotation;
            FlightCamera.fetch.SetFoV(endFieldOfView);
            KscArrival.ResetTemporalHistories();
            control.enabled = true;
            overlay.Close();
            GameEvents.onShowUI.Fire();
            interfaceHidden = false;
            Debug.Log(Log.Tag + " Menu to the space centre: " + (skipped ? "skipped" : "down") + ".");
            // Once more after KSP's own camera has taken its place again.
            yield return null;
            KscArrival.ResetTemporalHistories();
            if (KscArrival.Test)
            {
                yield return new WaitForSeconds(1f);
                KscArrival.TestShot("3-ksp-view");
                yield return KscArrivalCheck.Run();
                Application.Quit();
            }
        }
    }

    // In the test, at the end in the space centre: where each camera draws and how bright
    // that is, the rig's state, and the view with the upscaler switched the other way and
    // back, each into the log and a screenshot.
    internal static class KscArrivalCheck
    {
        // The stages of a camera's frame each sampled: where in it the picture goes.
        private static readonly UnityEngine.Rendering.CameraEvent[] Stages =
        {
            UnityEngine.Rendering.CameraEvent.AfterGBuffer, UnityEngine.Rendering.CameraEvent.AfterLighting,
            UnityEngine.Rendering.CameraEvent.AfterSkybox, UnityEngine.Rendering.CameraEvent.AfterForwardAlpha,
            UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, UnityEngine.Rendering.CameraEvent.AfterImageEffects,
            UnityEngine.Rendering.CameraEvent.AfterEverything,
        };

        private static IEnumerator SampleStages(string when)
        {
            var samples = new List<KeyValuePair<string, RenderTexture>>();
            var attached = new List<KeyValuePair<Camera, KeyValuePair<UnityEngine.Rendering.CameraEvent, UnityEngine.Rendering.CommandBuffer>>>();
            foreach (Camera camera in Camera.allCameras)
            {
                if (camera.name != "Camera 00" && camera.name != "Camera ScaledSpace" && camera.name != "GalaxyCamera") continue;
                foreach (UnityEngine.Rendering.CameraEvent stage in Stages)
                {
                    var small = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGBFloat);
                    var buffer = new UnityEngine.Rendering.CommandBuffer { name = "ReDefinition arrival check" };
                    buffer.Blit(UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget, small);
                    camera.AddCommandBuffer(stage, buffer);
                    samples.Add(new KeyValuePair<string, RenderTexture>(camera.name + " " + stage, small));
                    attached.Add(new KeyValuePair<Camera, KeyValuePair<UnityEngine.Rendering.CameraEvent, UnityEngine.Rendering.CommandBuffer>>(
                        camera, new KeyValuePair<UnityEngine.Rendering.CameraEvent, UnityEngine.Rendering.CommandBuffer>(stage, buffer)));
                }
            }
            for (int i = 0; i < 5; i++) yield return null;
            foreach (var a in attached)
                if (a.Key != null) a.Key.RemoveCommandBuffer(a.Value.Key, a.Value.Value);
            var text = new System.Text.StringBuilder(Log.Tag + " Menu to the space centre: stages " + when + ":");
            foreach (var s in samples)
            {
                text.Append("; ").Append(s.Key).Append(' ').Append(Mean(s.Value));
                s.Value.Release();
                UnityEngine.Object.Destroy(s.Value);
            }
            foreach (var a in attached) a.Value.Value.Release();
            Debug.Log(text.ToString());
        }

        internal static IEnumerator Run()
        {
            yield return new WaitForSeconds(1f);
            Report("as arrived");
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null) yield break;
            yield return SampleStages("as arrived");
            bool enabled = addon.Live.Enabled;
            foreach (bool now in new[] { !enabled, enabled })
            {
                OwnSettings before = addon.Live.Clone();
                OwnSettings after = before.Clone();
                after.Enabled = now;
                addon.Apply(before, after);
                yield return new WaitForSeconds(3f);
                string name = "4-upscaler-" + (now ? "on" : "off");
                KscArrival.TestShot(name);
                yield return new WaitForSeconds(0.5f);
                Report(name);
                yield return SampleStages(name);
            }
        }

        private static void Report(string when)
        {
            var text = new System.Text.StringBuilder(Log.Tag + " Menu to the space centre: check " + when + ":");
            Upscaler.UpscalerRig rig = Upscaler.UpscalerRig.Current;
            text.Append(rig == null ? " no rig" : " rig " + (rig.PassThrough ? "passing through" : "upscaling"));
            foreach (Camera camera in Camera.allCameras)
            {
                RenderTexture target = camera.targetTexture;
                text.Append("; '").Append(camera.name).Append("' depth ")
                    .Append(camera.depth.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append(" into ").Append(target == null ? "the screen" : target.name + " " + target.width + "x" + target.height)
                    .Append(target != null ? ", mean " + Mean(target) : "");
            }
            Debug.Log(text.ToString());
        }

        // The texture's mean red and green, rendered down to 4 by 4 in float and read back,
        // and how many of the 16 texels are not a number.
        private static string Mean(RenderTexture texture)
        {
            RenderTexture small = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGBFloat);
            RenderTexture before = RenderTexture.active;
            var read = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
            try
            {
                Graphics.Blit(texture, small);
                RenderTexture.active = small;
                read.ReadPixels(new Rect(0, 0, 4, 4), 0, 0, false);
                float r = 0f, g = 0f;
                int nan = 0;
                foreach (Color c in read.GetPixels())
                {
                    if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsInfinity(c.r) || float.IsInfinity(c.g))
                    {
                        nan++;
                        continue;
                    }
                    r += c.r;
                    g += c.g;
                }
                int n = Mathf.Max(16 - nan, 1);
                return (r / n).ToString("0.000", CultureInfo.InvariantCulture) + "/" + (g / n).ToString("0.000", CultureInfo.InvariantCulture)
                       + (nan > 0 ? " NaN " + nan : "");
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(small);
                UnityEngine.Object.Destroy(read);
            }
        }
    }
}
