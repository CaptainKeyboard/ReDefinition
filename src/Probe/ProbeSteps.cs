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

        // The line a probe writes when its flow threw an exception, which the scripts that run the probes end on.
        internal const string FailedLine = "Probe: failed";

        // Runs a flow so that an exception in it, or in a flow it runs inside it, is written to the log as a line the scripts end on, at once.
        // An exception in a coroutine ends it with no sign but the log's exception, and a probe waiting for its closing line would wait for ever.
        internal static IEnumerator Safe(IEnumerator flow, string name)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!flow.MoveNext()) yield break;
                    current = flow.Current;
                }
                catch (Exception e)
                {
                    Debug.LogError(Core.Log.Tag + " " + FailedLine + ": " + name + ": " + e);
                    yield break;
                }
                IEnumerator nested = current as IEnumerator;
                yield return nested != null ? Safe(nested, name) : current;
            }
        }

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
