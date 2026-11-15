using System;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // The cloud viewer in the flight scene: the keys of the Keys tab (KEY blocks cloudViewer, cloudViewerFlip and cloudViewerCapture of
    // src/Settings/Atmospheres/Atmospheres.cfg, all without a default), the viewer's frame, and the end of the session before the scene is saved and left.
    // The window also opens from the atmospheres' settings window (its Cloud Viewer button).
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class CloudViewerAddon : MonoBehaviour
    {
        internal const string ToggleKey = "atmospheres.cloudViewer", FlipKey = "atmospheres.cloudViewerFlip", CaptureKey = "atmospheres.cloudViewerCapture";

        private ForecastKey toggle, flip, capture;
        private bool started;

        // The addon of this flight scene runs (the main mod is there): without it nothing ticks the viewer.
        internal static bool Running { get; private set; }

        private void Start()
        {
            if (!HostGate.Open) return;
            Running = true;
            toggle = new ForecastKey(ToggleKey);
            flip = new ForecastKey(FlipKey);
            capture = new ForecastKey(CaptureKey);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            started = true;
        }

        private void OnDestroy()
        {
            if (!started) return;
            Running = false;
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            End();
        }

        // The scene is saved when it is left: the viewer's time and place are put back first.
        private void OnSceneLoadRequested(GameScenes scene)
        {
            End();
        }

        private static void End()
        {
            try
            {
                CloudViewerWindow.Instance.Close(true);
                CloudViewer.Instance.EndScene();
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("cloud viewer", "the viewer could not be ended with the scene", e);
            }
        }

        private void Update()
        {
            if (!started) return;
            try
            {
                if (toggle.Pressed()) CloudViewerWindow.Instance.Toggle();
                if (CloudViewer.Instance.Active)
                {
                    if (flip.Pressed()) CloudViewer.Instance.FlipAB();
                    if (capture.Pressed()) CloudViewer.Instance.RequestCapture();
                }
                CloudViewer.Instance.Tick();
                CloudViewerWindow.Instance.Tick();
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("cloud viewer", "the viewer's frame failed; the viewer is closed", e);
                End();
            }
        }
    }
}
