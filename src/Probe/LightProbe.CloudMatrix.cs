using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Probe
{
    // The moon's light per pixel on EVE's clouds against the raymarching's own moonlight on them, in a matrix
    // (-redefinitionProbeCloudLight). The moon stands 25, 45 and 65 degrees high, and the camera looks towards it, across and
    // away, each at night with clouds in the sky. In each cell the light per pixel is read at
    // several scales of the optical depth (EveCloudRelight.TauScale), and the scale that equals the two lights is found.
    // Where it is the same in all cells, the shader's model of the raymarching holds; where it differs, the log says how.
    internal static partial class LightProbe
    {
        // The moon's heights over the horizon the cells are looked for at, in degrees, and how far from one the moon may stand.
        private static readonly float[] MatrixHeights = { 25f, 45f, 65f };
        private const float MatrixHeightTolerance = 6f;
        // The angles from the moon's direction the camera looks along, in degrees, the camera's pitch and its distance.
        private static readonly float[] MatrixLooks = { 0f, 90f, 180f };
        private const float MatrixPitch = -8f, MatrixDistance = 60f;
        // The scales of the optical depth tried, and the least mean opacity a view needs to count.
        private static readonly float[] MatrixScales = { 0.25f, 0.5f, 1f, 2f, 4f };
        private const float MatrixLeastOpacity = 0.02f;
        // Seconds of game time looked through at most.
        private const double MatrixLongest = 6.0 * 21600.0 * 3.0;

        // The cells, the time looked for each by setting it on; the scales that equal the two lights are collected and their
        // median set as EveCloudRelight.TauScale.
        private static IEnumerator CloudMatrix(string folder, CelestialBody sun, CelestialBody mun)
        {
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (camera == null || sun == null || mun == null) yield break;
            var done = new bool[MatrixHeights.Length];
            var scales = new List<float>();
            double started = Planetarium.GetUniversalTime();
            float directBefore = EveCloudRelight.DirectGain, tauBefore = EveCloudRelight.TauScale;
            EveCloudRelight.DirectGain = 1f;
            Debug.Log(Log.Tag + " Probe: light: the raymarching's shader and keywords: " + EveCloudMoonlight.RaymarchKeywords() + ".");
            Debug.Log(Log.Tag + " Probe: light: clouds matrix, " + MatrixHeights.Length + " heights of the moon, " + MatrixLooks.Length + " looks each.");
            while (Planetarium.GetUniversalTime() - started < MatrixLongest && Array.IndexOf(done, false) >= 0)
            {
                int cell = -1;
                float height = Elevation(mun);
                if (Elevation(sun) < -25f)
                    for (int i = 0; i < MatrixHeights.Length; i++)
                        if (!done[i] && Mathf.Abs(height - MatrixHeights[i]) < MatrixHeightTolerance)
                            cell = i;
                if (cell >= 0 && Cloudy())
                {
                    done[cell] = true;
                    for (int look = 0; look < MatrixLooks.Length; look++)
                        yield return MatrixView(camera, sun, mun, MatrixHeights[cell], MatrixLooks[look], scales);
                }
                Planetarium.SetUniversalTime(Planetarium.GetUniversalTime() + 120.0);
                yield return ProbeSteps.Frames(2);
            }
            scales.Sort();
            float median = scales.Count > 0 ? scales[scales.Count / 2] : tauBefore;
            Debug.Log(Log.Tag + " Probe: light: clouds matrix: " + scales.Count + " views, the scales that equal the lights from "
                      + (scales.Count > 0 ? scales[0] : 0f).ToString("0.000", CultureInfo.InvariantCulture) + " to "
                      + (scales.Count > 0 ? scales[scales.Count - 1] : 0f).ToString("0.000", CultureInfo.InvariantCulture) + ", median "
                      + median.ToString("0.000", CultureInfo.InvariantCulture) + " (before " + tauBefore.ToString("0.000", CultureInfo.InvariantCulture)
                      + ").");
            EveCloudRelight.TauScale = median;
            EveCloudRelight.DirectGain = directBefore;
            EveCloudRelight.DebugMode = 0f;
            EveCloudRelight.ProbeAlways = false;
            EveCloudMoonlight.ProbeHandoff = -1f;
            EveCloudMoonlight.ProbeShare = -1f;
        }

        // One view of a cell. The camera looks along the look, and the light per pixel is read at each scale. EVE's clouds in
        // the sun's light and in the moon's follow. The scale that equals the two lights (log-log between the scales around
        // gain 1) goes into `scales`.
        private static IEnumerator MatrixView(Camera camera, CelestialBody sun, CelestialBody mun, float target, float look, List<float> scales)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            Vector3 up = ((Vector3)(vessel.CoMD - vessel.mainBody.position)).normalized;
            Vector3 flat = Vector3.ProjectOnPlane((Vector3)(mun.position - vessel.CoMD), up).normalized;
            flat = Quaternion.AngleAxis(look, up) * flat;
            yield return LookAlong(flat, MatrixPitch, MatrixDistance, "the matrix look " + look.ToString("0", CultureInfo.InvariantCulture));
            EveCloudRelight.ProbeAlways = true;
            EveCloudMoonlight.ProbeHandoff = 0f;
            EveCloudMoonlight.ProbeShare = 1f;
            EveCloudRelight.DebugMode = 7f;
            Color[] opacityMean = new Color[3];
            EveCloudRelight.TauScale = 1f;
            yield return MeanClouds(camera, opacityMean);
            float opacity = opacityMean[1].g;
            if (opacityMean[2].r < 1f || opacity < MatrixLeastOpacity)
            {
                Debug.Log(Log.Tag + " Probe: light: clouds matrix: the moon " + Elevation(mun).ToString("0", CultureInfo.InvariantCulture) + " degrees up, look "
                          + look.ToString("0", CultureInfo.InvariantCulture) + ": too few clouds in view (opacity "
                          + opacity.ToString("0.0000", CultureInfo.InvariantCulture) + ").");
                yield break;
            }
            EveCloudRelight.DebugMode = 2f;
            var direct = new Color[MatrixScales.Length];
            Color inSun = Color.black;
            for (int i = 0; i < MatrixScales.Length; i++)
            {
                EveCloudRelight.TauScale = MatrixScales[i];
                Color[] mean = new Color[3];
                yield return MeanCloudsFast(camera, mean);
                direct[i] = mean[1];
                inSun = mean[0];
            }
            // The sky's light the moon gives alone, which does not depend on the scale.
            EveCloudRelight.DebugMode = 3f;
            EveCloudRelight.TauScale = 1f;
            Color[] skyMean = new Color[3];
            yield return MeanCloudsFast(camera, skyMean);
            EveCloudRelight.DebugMode = 8f;
            Color[] depths = new Color[3];
            EveCloudRelight.TauScale = 1f;
            yield return MeanCloudsFast(camera, depths);
            EveCloudRelight.DebugMode = 0f;
            EveCloudMoonlight.ProbeHandoff = 1f;
            EveCloudMoonlight.ProbeShare = 0f;
            Color[] raymarched = new Color[3];
            yield return MeanClouds(camera, raymarched);
            Color wanted = raymarched[0] - inSun;
            var gains = new float[MatrixScales.Length];
            string table = "";
            for (int i = 0; i < MatrixScales.Length; i++)
            {
                Color d = direct[i] + skyMean[1];
                double dd = d.r * d.r + d.g * d.g + d.b * d.b;
                double dw = d.r * wanted.r + d.g * wanted.g + d.b * wanted.b;
                gains[i] = dd > 1e-20 ? (float)(dw / dd) : 0f;
                table += (i > 0 ? ", " : "") + MatrixScales[i].ToString("0.00", CultureInfo.InvariantCulture) + ": " + gains[i].ToString("0.000", CultureInfo.InvariantCulture);
            }
            float scale = ScaleForGainOne(gains);
            if (scale > 0f) scales.Add(scale);
            Color atOne = direct[2] + skyMean[1];
            Debug.Log(Log.Tag + " Probe: light: clouds matrix: the moon " + Elevation(mun).ToString("0", CultureInfo.InvariantCulture) + " degrees up (cell "
                      + target.ToString("0", CultureInfo.InvariantCulture) + "), look " + look.ToString("0", CultureInfo.InvariantCulture) + " degrees from it, opacity "
                      + opacity.ToString("0.000", CultureInfo.InvariantCulture) + ", our opacity along the rays "
                      + (depths[1].r / opacity).ToString("0.00", CultureInfo.InvariantCulture) + ", the steps' mean light " + (depths[1].g / opacity).ToString("0.000", CultureInfo.InvariantCulture)
                      + ", the optical depth at the mean point " + (depths[1].b / opacity).ToString("0.00", CultureInfo.InvariantCulture) + "; the raymarching's moonlight " + Rgb(wanted)
                      + ", per pixel at scale 1 " + Rgb(atOne) + "; the gain that equals them at each scale of the optical depth (" + table
                      + "); the scale for gain 1: " + (scale > 0f ? scale.ToString("0.000", CultureInfo.InvariantCulture) : "none") + ".");
        }

        // The scale at which the gains reach 1, log-log between the two scales around it; 0 where they do not (the gains
        // rise with the scale).
        private static float ScaleForGainOne(float[] gains)
        {
            for (int i = 0; i + 1 < MatrixScales.Length; i++)
            {
                float low = gains[i], high = gains[i + 1];
                if (low <= 0f || high <= 0f || (low - 1f) * (high - 1f) > 0f) continue;
                float t = Mathf.Abs(Mathf.Log(high) - Mathf.Log(low)) < 1e-6f ? 0f : -Mathf.Log(low) / (Mathf.Log(high) - Mathf.Log(low));
                return Mathf.Exp(Mathf.Lerp(Mathf.Log(MatrixScales[i]), Mathf.Log(MatrixScales[i + 1]), t));
            }
            return 0f;
        }

        // MeanClouds without the wait for EVE's history to settle: for a change of the per-pixel light alone.
        private static IEnumerator MeanCloudsFast(Camera camera, Color[] result)
        {
            yield return ProbeSteps.Frames(3);
            Color eve = Color.black, relit = Color.black;
            int frames = 0;
            for (int i = 0; i < 4; i++)
            {
                yield return new WaitForEndOfFrame();
                Color e, r;
                if (EveCloudRelight.MeanColour(camera, out e, out r))
                {
                    eve += e;
                    relit += r;
                    frames++;
                }
                yield return null;
            }
            result[0] = frames > 0 ? eve / frames : Color.black;
            result[1] = frames > 0 ? relit / frames : Color.black;
            result[2] = new Color(frames, 0f, 0f, 0f);
        }
    }
}
