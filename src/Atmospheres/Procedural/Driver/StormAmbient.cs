using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Lowers the scene's ambient light in a storm: RenderSettings.ambientLight in the flat mode, the three colours in the trilight
    // mode, the ambient intensity in the skybox mode. Scatterer's DisableAmbientLight sets the flat colour to black in OnPreRender of
    // the scaled-space camera and back in OnPostRender. Deferred's RefreshLegacyAmbient copies it to a shader global in OnPreRender.
    // So the values are written in Camera.onPreCull, once per frame, after every Update and LateUpdate and before the cameras render.
    // A value that someone else wrote is the new baseline (AmbientTracker). Everything is put back when the storm ends.
    // The factor is 1 - 0.6 * stormSkyDarkening * intensity^0.8. Who writes the ambient light in KSP itself was not found in the
    // reference sources, so the log counts how often the baseline changed under us.
    internal sealed class StormAmbient
    {
        private readonly AmbientTracker flat = new AmbientTracker();
        private readonly AmbientTracker sky = new AmbientTracker();
        private readonly AmbientTracker equator = new AmbientTracker();
        private readonly AmbientTracker ground = new AmbientTracker();
        private readonly AmbientTracker intensity = new AmbientTracker();
        private AmbientMode modeScaled = AmbientMode.Flat;
        private bool scaling;
        private int frame = -1;

        internal bool Scaling
        {
            get { return scaling; }
        }

        internal int ExternalWrites
        {
            get { return flat.ExternalWrites + sky.ExternalWrites + equator.ExternalWrites + ground.ExternalWrites + intensity.ExternalWrites; }
        }

        // The ambient light as the camera renders it now, for the log.
        internal static string Describe()
        {
            AmbientMode mode = RenderSettings.ambientMode;
            if (mode == AmbientMode.Flat) return "flat " + Text(RenderSettings.ambientLight);
            if (mode == AmbientMode.Trilight)
                return "trilight " + Text(RenderSettings.ambientSkyColor) + " / " + Text(RenderSettings.ambientEquatorColor) + " / " + Text(RenderSettings.ambientGroundColor);
            return mode + " intensity " + RenderSettings.ambientIntensity.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // The baseline and the value written last, for the log.
        internal string DescribeScaling()
        {
            if (!scaling) return "not scaling";
            if (modeScaled == AmbientMode.Flat) return "flat baseline " + Text(flat.Baseline) + " written " + Text(flat.Written);
            if (modeScaled == AmbientMode.Trilight) return "trilight baseline " + Text(sky.Baseline) + " written " + Text(sky.Written);
            return modeScaled + " baseline " + intensity.Baseline.r.ToString("0.000", CultureInfo.InvariantCulture)
                   + " written " + intensity.Written.r.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static string Text(Color c)
        {
            return c.r.ToString("0.000", CultureInfo.InvariantCulture) + "," + c.g.ToString("0.000", CultureInfo.InvariantCulture) + ","
                   + c.b.ToString("0.000", CultureInfo.InvariantCulture);
        }

        // Writes the ambient values scaled by the factor; once per frame.
        internal void Apply(float factor)
        {
            if (frame == Time.frameCount) return;
            frame = Time.frameCount;
            AmbientMode mode = RenderSettings.ambientMode;
            if (scaling && mode != modeScaled) Restore();
            modeScaled = mode;
            scaling = true;
            if (mode == AmbientMode.Flat)
            {
                RenderSettings.ambientLight = flat.Scale(RenderSettings.ambientLight, factor);
            }
            else if (mode == AmbientMode.Trilight)
            {
                RenderSettings.ambientSkyColor = sky.Scale(RenderSettings.ambientSkyColor, factor);
                RenderSettings.ambientEquatorColor = equator.Scale(RenderSettings.ambientEquatorColor, factor);
                RenderSettings.ambientGroundColor = ground.Scale(RenderSettings.ambientGroundColor, factor);
            }
            else
            {
                Color scaled = intensity.Scale(new Color(RenderSettings.ambientIntensity, 0f, 0f, 1f), factor);
                RenderSettings.ambientIntensity = scaled.r;
            }
        }

        // Puts back what was scaled, where it is still the value written.
        internal void Restore()
        {
            if (!scaling) return;
            scaling = false;
            Color restore;
            if (modeScaled == AmbientMode.Flat)
            {
                if (flat.Restore(RenderSettings.ambientLight, out restore)) RenderSettings.ambientLight = restore;
            }
            else if (modeScaled == AmbientMode.Trilight)
            {
                if (sky.Restore(RenderSettings.ambientSkyColor, out restore)) RenderSettings.ambientSkyColor = restore;
                if (equator.Restore(RenderSettings.ambientEquatorColor, out restore)) RenderSettings.ambientEquatorColor = restore;
                if (ground.Restore(RenderSettings.ambientGroundColor, out restore)) RenderSettings.ambientGroundColor = restore;
            }
            else
            {
                if (intensity.Restore(new Color(RenderSettings.ambientIntensity, 0f, 0f, 1f), out restore)) RenderSettings.ambientIntensity = restore.r;
            }
            frame = -1;
        }
    }
}
