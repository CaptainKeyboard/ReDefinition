using System;
using System.Collections;
using ReDefinition.Proxy;
using UnityEngine;

namespace ReDefinition.Probe
{
    // The small waits and lookups the probes share: frames to wait, the scene camera, the end of the screen
    // recording and the quit that ends a run. Each wait is a coroutine a probe yields to, so a probe may take up to
    // a frame more than the same loop written out, which the measurements tolerate.
    internal static class ProbeSteps
    {
        // The name of KSP's flight camera that draws the scene.
        internal const string SceneCameraName = "Camera 00";

        // Waits the given number of frames.
        internal static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        // The flight's scene camera: the last of the cameras so named, null where there is none.
        internal static Camera SceneCamera()
        {
            Camera scene = null;
            foreach (Camera c in Camera.allCameras)
                if (c.name == SceneCameraName) scene = c;
            return scene;
        }

        // Waits until the proxy's screen recording is neither recording nor saving, seconds at most. everyFrame, where
        // given, runs in each frame of the wait.
        internal static IEnumerator RecordingSaved(float seconds, Action everyFrame = null)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                string state = ScreenRecordingBridge.State();
                if (!state.StartsWith("recording", StringComparison.Ordinal)
                    && !state.StartsWith("saving", StringComparison.Ordinal)) break;
                if (everyFrame != null) everyFrame();
                yield return null;
            }
        }

        // Waits the seconds and quits KSP.
        internal static IEnumerator QuitAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            Application.Quit();
        }
    }
}
