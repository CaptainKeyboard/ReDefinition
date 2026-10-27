using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Proxy;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeViews "pitch,distance,heading;...": the flight camera is put at each pose in turn.
    // Pitch is in degrees (positive looks down), distance in metres, heading in degrees from the one it
    // started with. Each pose gets a shot with the raytracing in each mode of -redefinitionProbeViewModes
    // "Off,Full" (the mode the settings hold where none is given), as <shot>-v<i>-<mode>.png. The probe shows
    // how a surface looks from near and far, steep and flat: the grass against the ground beside it.
    // With -redefinitionProbeSurfaces, in each mode with the raytracing on, the surfaces of each view are
    // written too (<shot>-v<i>-<mode>-<name>.bin), at half the resolution, which tools/surface_sheet.py makes
    // into pictures and figures per region. With -redefinitionProbeViewOverlay, the surfaces' overlay
    // (RtCamera.SurfaceView) is written as <shot>-v<i>-<mode>-overlay.png.
    // With -redefinitionProbeViewTarget <part of a name>, the views are about the renderer so named instead of the vessel.
    //
    // -redefinitionProbeNoGloss gives Parallax's materials no shine. Specular, Fresnel rim and
    // environment reflection are set to nothing, and so is the raytracing's shine for them, which it reads from
    // the same values (RtMaterialIntent). They are set again every two seconds, since Parallax makes its
    // scatter's materials as the scatter loads.
    internal static class ViewProbe
    {
        // The switch with the poses, the surfaces written per view, and the Parallax properties that hold its shine.
        internal const string ViewsArgument = "-redefinitionProbeViews";
        private static readonly string[] SurfaceNames = { "gbuffer0", "gbuffer1", "gbuffer2", "surface", "surfacemode", "specular", "sun", "diffuse" };
        private static readonly int SpecularIntensity = Shader.PropertyToID("_SpecularIntensity");
        private static readonly int EnvironmentMapFactor = Shader.PropertyToID("_EnvironmentMapFactor");
        private static readonly int FresnelColor = Shader.PropertyToID("_FresnelColor");

        // Whether KSP was started to take Parallax's shine off.
        internal static bool NoGloss
        {
            get { return CommandLine.Has("-redefinitionProbeNoGloss"); }
        }

        // Parallax's shine off on every material of its shaders found now; how many.
        internal static int TakeGlossOff()
        {
            int changed = 0;
            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null || material.shader == null) continue;
                if (!material.shader.name.StartsWith("Custom/Parallax", StringComparison.Ordinal)) continue;
                bool any = false;
                if (material.HasProperty(SpecularIntensity) && material.GetFloat(SpecularIntensity) != 0f)
                {
                    material.SetFloat(SpecularIntensity, 0f);
                    any = true;
                }
                if (material.HasProperty(EnvironmentMapFactor) && material.GetFloat(EnvironmentMapFactor) != 0f)
                {
                    material.SetFloat(EnvironmentMapFactor, 0f);
                    any = true;
                }
                if (material.HasProperty(FresnelColor) && material.GetColor(FresnelColor) != Color.black)
                {
                    material.SetColor(FresnelColor, Color.black);
                    any = true;
                }
                if (any) changed++;
            }
            return changed;
        }

        // TakeGlossOff every two seconds for as long as the flight lasts, each change logged.
        internal static IEnumerator KeepGlossOff()
        {
            while (true)
            {
                int changed = TakeGlossOff();
                if (changed > 0) Debug.Log(Log.Tag + " Probe: no gloss: " + changed + " of Parallax's materials without shine.");
                yield return new WaitForSecondsRealtime(2f);
            }
        }

        // The views: the camera about the vessel or the named renderer, and for each raytracing mode and each set of
        // proxy keys every pose shot, with its timings, surfaces and overlay where the switches ask for them.
        public static IEnumerator Run(string folder, string shot, string views)
        {
            if (FlightCamera.fetch == null || ReDefinitionAddon.Instance == null) yield break;
            // -redefinitionProbeViewTarget <part of a name>: the views about the renderer so named nearest the
            // vessel (the space centre's flag) instead of the vessel.
            string targetName = SceneProbe.ArgumentAfter("-redefinitionProbeViewTarget");
            Transform middle = null;
            if (targetName != null && FlightGlobals.ActiveVessel != null)
            {
                Renderer nearest = null;
                float best = float.MaxValue;
                Vector3 from = FlightGlobals.ActiveVessel.transform.position;
                // A skinned one first: the space centre's waving flags are, a vessel's flag decal is not.
                bool skinned = false;
                foreach (SkinnedMeshRenderer renderer in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>())
                    if (renderer.enabled && renderer.name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0) skinned = true;
                foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (!renderer.enabled || renderer.name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (skinned && !(renderer is SkinnedMeshRenderer)) continue;
                    float distance = (renderer.bounds.center - from).sqrMagnitude;
                    if (distance < best)
                    {
                        best = distance;
                        nearest = renderer;
                    }
                }
                if (nearest != null)
                {
                    // About its middle, not its pivot (a flag's is at the foot of its pole).
                    middle = new GameObject("ReDefinition probe view target").transform;
                    middle.position = nearest.bounds.center;
                    middle.SetParent(nearest.transform, true);
                    FlightCamera.fetch.SetTargetTransform(middle);
                    yield return new WaitForSecondsRealtime(2f);
                }
                Debug.Log(Log.Tag + " Probe: views: about " + (nearest != null ? nearest.name + " (" + nearest.GetType().Name + "), "
                                                                 + Mathf.Sqrt(best).ToString("0") + " m from the vessel" : "nothing named " + targetName) + ".");
            }
            float startHeading = FlightCamera.fetch.camHdg;
            List<Raytracing.RaytracingMode> modes = new List<Raytracing.RaytracingMode>();
            string modeList = SceneProbe.ArgumentAfter("-redefinitionProbeViewModes");
            if (modeList != null)
                foreach (string name in modeList.Split(','))
                {
                    Raytracing.RaytracingMode mode;
                    if (Enum.TryParse(name.Trim(), true, out mode)) modes.Add(mode);
                }
            if (modes.Count == 0) modes.Add(ReDefinitionAddon.Instance.Current().Raytracing);
            // -redefinitionProbeViewIni "key=value,key=value;...": each set of the proxy's live keys in turn,
            // every view under each, as <shot>-v<i>-<mode>-ini<n>.png.
            string iniList = SceneProbe.ArgumentAfter("-redefinitionProbeViewIni");
            string[] iniSets = iniList != null ? iniList.Split(';') : new[] { "" };

            // Each mode once, every view in it: the raytracing set up once per mode.
            foreach (Raytracing.RaytracingMode mode in modes)
            for (int n = 0; n < iniSets.Length; n++)
            {
                foreach (string pair in iniSets[n].Split(','))
                {
                    int equals = pair.IndexOf('=');
                    if (equals > 0) ProxyIni.Set(pair.Substring(0, equals).Trim(), pair.Substring(equals + 1).Trim());
                }
                if (iniSets[n].Length > 0)
                {
                    Debug.Log(Log.Tag + " Probe: views: the proxy's ini set to " + iniSets[n] + ".");
                    yield return new WaitForSecondsRealtime(3f);
                }
                string suffix = iniSets[n].Length > 0 ? "-ini" + n : "";
                OwnSettings before = ReDefinitionAddon.Instance.Current();
                if (before.Raytracing != mode)
                {
                    OwnSettings after = before.Clone();
                    after.Raytracing = mode;
                    ReDefinitionAddon.Instance.Apply(before, after);
                    Debug.Log(Log.Tag + " Probe: views: the raytracing " + mode + ".");
                    yield return new WaitForSecondsRealtime(10f);
                }
                string[] poses = views.Split(';');
                for (int i = 0; i < poses.Length; i++)
                {
                    string[] parts = poses[i].Split(',');
                    float pitch, distance, heading;
                    if (parts.Length < 3 || !Number(parts[0], out pitch) || !Number(parts[1], out distance)
                        || !Number(parts[2], out heading))
                        continue;
                    FlightCamera.fetch.camPitch = pitch * Mathf.Deg2Rad;
                    FlightCamera.fetch.camHdg = startHeading + heading * Mathf.Deg2Rad;
                    FlightCamera.fetch.SetDistance(distance);
                    // The camera there, the raytracing's history and the upscaler's settled on the new view.
                    yield return new WaitForSecondsRealtime(4f);
                    yield return new WaitForEndOfFrame();
                    string name = shot + "-v" + i + "-" + mode + suffix + ".png";
                    // -redefinitionProbeParity: the primary check for this pose, its report in the log under the pose's name.
                    if (mode != Raytracing.RaytracingMode.Off && CommandLine.Has("-redefinitionProbeParity") && Raytracing.RtCamera.Current != null)
                    {
                        Raytracing.RtCamera.Current.ParityNow(shot + "-v" + i);
                        yield return new WaitForSecondsRealtime(1.5f);
                    }
                    // -redefinitionProbeViewTimings: the raytracing passes' GPU time and the frame time, each averaged
                    // over 6 s at the pose.
                    if (CommandLine.Has("-redefinitionProbeViewTimings"))
                        yield return Timings(name);
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder, name));
                    Debug.Log(Log.Tag + " Probe: views: " + name + " at pitch " + pitch + ", distance " + distance
                              + " m, heading " + heading + (NoGloss ? ", without Parallax's shine" : "") + ".");
                    yield return null;
                    // -redefinitionProbeSurfaces: with the raytracing on, the surfaces of the frame after at every
                    // second pixel (RtCamera.WriteSurfaceImages), for tools/surface_sheet.py.
                    if (mode != Raytracing.RaytracingMode.Off && CommandLine.Has("-redefinitionProbeSurfaces"))
                    {
                        yield return new WaitForEndOfFrame();
                        Raytracing.RtCamera traced = Raytracing.RtCamera.Current;
                        string prefix = name.Substring(0, name.Length - 4);
                        string written;
                        try
                        {
                            written = traced != null ? traced.WriteSurfaceImages(folder, prefix, 2, SurfaceNames) : "none, no raytraced camera";
                        }
                        catch (Exception e)
                        {
                            written = "failed: " + e.Message;
                        }
                        Debug.Log(Log.Tag + " Probe: views: surfaces of " + prefix + ": " + written + ".");
                    }
                    // -redefinitionProbeViewOverlay: with the raytracing on, the surfaces' overlay too
                    // (RtCamera.SurfaceView), as <shot>-v<i>-<mode>-overlay.png.
                    if (mode != Raytracing.RaytracingMode.Off && CommandLine.Has("-redefinitionProbeViewOverlay"))
                    {
                        bool shown = Raytracing.RtCamera.SurfaceView;
                        Raytracing.RtCamera.SurfaceView = true;
                        try
                        {
                            for (int frame = 0; frame < 3; frame++) yield return null;
                            yield return new WaitForEndOfFrame();
                            ScreenCapture.CaptureScreenshot(Path.Combine(folder, name.Substring(0, name.Length - 4) + "-overlay.png"));
                            yield return null;
                        }
                        finally
                        {
                            Raytracing.RtCamera.SurfaceView = shown;
                        }
                        yield return null;
                    }
                }
            }
            // The camera about the vessel again, for the probe's steps after the views.
            if (middle != null)
            {
                if (FlightGlobals.ActiveVessel != null) FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
                UnityEngine.Object.Destroy(middle.gameObject);
            }
        }

        // Six seconds of the raytracing passes' GPU times and the frame time at a pose, averaged, into the log.
        private static IEnumerator Timings(string name)
        {
            float[] passes = new float[8];
            float[] sum = new float[8];
            double frames = 0, seconds = 0;
            float until = Time.realtimeSinceStartup + 6f;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                RtNative.KspRtPassTimings(passes, passes.Length);
                for (int p = 0; p < passes.Length; p++) sum[p] += passes[p];
                frames++;
                seconds += Time.unscaledDeltaTime;
            }
            if (frames == 0) yield break;
            Func<int, string> ms = p => (sum[p] / frames).ToString("0.00", CultureInfo.InvariantCulture);
            float traced = 0f;
            for (int p = 0; p < 4; p++) traced += sum[p] / (float)frames;
            Debug.Log(Log.Tag + " Probe: views: timings " + name + ": frame " + (seconds / frames * 1000.0).ToString("0.00",
                          CultureInfo.InvariantCulture) + " ms; GPU ms sun " + ms(0) + ", occlusion " + ms(1) + ", diffuse "
                      + ms(2) + ", reflections " + ms(3) + ", denoiser " + ms(6) + "; traced "
                      + traced.ToString("0.00", CultureInfo.InvariantCulture) + ".");
        }

        // A number in the invariant culture; false where the text is none.
        private static bool Number(string text, out float value)
        {
            return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
