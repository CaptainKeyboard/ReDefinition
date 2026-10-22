using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeViews "pitch,distance,heading;...": the flight camera put at each pose in turn -- pitch in
    // degrees (positive looks down), distance in metres, heading in degrees from the one it started with --
    // and a shot of each with the raytracing in each mode of -redefinitionProbeViewModes "Off,Full" (the
    // mode the settings hold where none is given), as <shot>-v<i>-<mode>.png. For how a surface looks from
    // near and far, steep and flat: the grass against the ground beside it.
    //
    // -redefinitionProbeNoGloss: Parallax's materials without their shine -- specular, Fresnel rim and
    // environment reflection set to nothing, and so the raytracing's shine for them too, which it reads from
    // the same values (RtMaterialIntent) -- set again every two seconds, since Parallax makes its scatter's
    // materials as the scatter loads.
    internal static class ViewProbe
    {
        internal const string ViewsArgument = "-redefinitionProbeViews";
        private static readonly int SpecularIntensity = Shader.PropertyToID("_SpecularIntensity");
        private static readonly int EnvironmentMapFactor = Shader.PropertyToID("_EnvironmentMapFactor");
        private static readonly int FresnelColor = Shader.PropertyToID("_FresnelColor");

        internal static bool NoGloss
        {
            get { return Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeNoGloss") >= 0; }
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

        internal static IEnumerator KeepGlossOff()
        {
            while (true)
            {
                int changed = TakeGlossOff();
                if (changed > 0) Debug.Log(Log.Tag + " Probe: no gloss: " + changed + " of Parallax's materials without shine.");
                yield return new WaitForSecondsRealtime(2f);
            }
        }

        public static IEnumerator Run(string folder, string shot, string views)
        {
            if (FlightCamera.fetch == null || ReDefinitionAddon.Instance == null) yield break;
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
                    if (equals > 0) Settings.ProxyIni.Set(pair.Substring(0, equals).Trim(), pair.Substring(equals + 1).Trim());
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
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder, name));
                    Debug.Log(Log.Tag + " Probe: views: " + name + " at pitch " + pitch + ", distance " + distance
                              + " m, heading " + heading + (NoGloss ? ", without Parallax's shine" : "") + ".");
                    yield return null;
                }
            }
        }

        private static bool Number(string text, out float value)
        {
            return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
