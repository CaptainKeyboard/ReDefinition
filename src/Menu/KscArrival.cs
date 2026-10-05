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
    // same height, the picture fades and the camera comes down to KSP's own view.
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
                testFolder = System.IO.Path.Combine(System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "ReDefinitionCaptures"),
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
                if (t >= 1f) break;
                if (t > 0.5f && t - Time.unscaledDeltaTime / Seconds <= 0.5f) KscArrival.TestShot("1-menu-half");
                yield return null;
            }

            yield return new WaitForEndOfFrame();
            Texture2D picture = ScreenCapture.CaptureScreenshotAsTexture();
            KscArrival.Overlay = KscArrivalOverlay.Show(picture);
            foreach (Canvas canvas in hidden)
                if (canvas != null) canvas.enabled = true;
            yield return null;
            KscArrival.LoadSpaceCentre();
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

        private IEnumerator Start()
        {
            KscArrivalOverlay overlay = KscArrival.Overlay;
            if (overlay == null) yield break;
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
            Debug.Log(Log.Tag + " Menu to the space centre: the descent from " + (startHeight / 1000f).ToString("0", CultureInfo.InvariantCulture)
                      + " km to KSP's view " + endDistance.ToString("0", CultureInfo.InvariantCulture) + " m from the pad.");

            float started = Time.unscaledTime;
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
                if (t >= 1f) break;
                foreach (float at in new[] { 0.1f, 0.6f, 1.5f, 2.5f, 3.5f, 4.5f })
                    if (elapsed >= at && elapsed - Time.unscaledDeltaTime < at)
                        KscArrival.TestShot("2-descent-" + at.ToString("0.0", CultureInfo.InvariantCulture));
                if (elapsed > 0.3f && (Input.anyKeyDown || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))
                {
                    skipped = true;
                    break;
                }
                yield return null;
            }
            view.localPosition = endLocalPosition;
            view.localRotation = endLocalRotation;
            control.enabled = true;
            overlay.Close();
            Debug.Log(Log.Tag + " Menu to the space centre: " + (skipped ? "skipped" : "down") + ".");
            if (KscArrival.Test)
            {
                yield return new WaitForSeconds(1f);
                KscArrival.TestShot("3-ksp-view");
                yield return new WaitForSeconds(1f);
                Application.Quit();
            }
        }
    }
}
