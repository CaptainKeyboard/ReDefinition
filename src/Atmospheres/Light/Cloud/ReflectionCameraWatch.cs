using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Measurement (master plan 46.1 part 4, PRUEFPROTOKOLL 313): which cameras render KSP's reflection probe, so that the own clouds can be drawn into its faces.
    // probe.logReflectionCameras 1 logs, at most every 5 s, every camera of type Reflection seen in Camera.onPreCull: name, size, target, path, mask, position.
    // 0 (the default): nothing is hooked.
    internal static class ReflectionCameraWatch
    {
        internal const string TuneLog = "probe.logReflectionCameras";
        private static bool hooked;
        private static float nextLog;
        private static readonly Dictionary<string, int> seen = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> detail = new Dictionary<string, string>();

        // From the own clouds' frame: hooks or unhooks by the key.
        internal static void Sync()
        {
            bool want = Tuning.Get(TuneLog, 0f) > 0.5f;
            if (want == hooked) return;
            hooked = want;
            if (want) Camera.onPreCull += OnPreCull;
            else Camera.onPreCull -= OnPreCull;
        }

        private static void OnPreCull(Camera cam)
        {
            if (cam == null || cam.cameraType != CameraType.Reflection) return;
            string key = cam.name;
            int n;
            seen.TryGetValue(key, out n);
            seen[key] = n + 1;
            if (!detail.ContainsKey(key))
            {
                RenderTexture t = cam.targetTexture;
                detail[key] = cam.pixelWidth + "x" + cam.pixelHeight + ", target " + (t == null ? "none" : t.width + "x" + t.height + " " + t.dimension + " " + t.format)
                              + ", path " + cam.actualRenderingPath + ", mask 0x" + cam.cullingMask.ToString("X") + ", fov " + cam.fieldOfView.ToString("0") + ", at "
                              + cam.transform.position.ToString("F0") + ", clear " + cam.clearFlags;
            }
            if (Time.unscaledTime < nextLog) return;
            nextLog = Time.unscaledTime + 5f;
            var b = new System.Text.StringBuilder("reflection cameras, calls since the last line:");
            foreach (KeyValuePair<string, int> kv in seen) b.Append(" [").Append(kv.Key).Append(" x").Append(kv.Value).Append(": ").Append(detail[kv.Key]).Append(']');
            AtmospheresLog.Say(b.ToString());
            seen.Clear();
        }
    }
}
