using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Scales one ambient value that other code writes as well. The value is read, scaled and written back. At the next read, a value
    // that is not the one written is a new baseline of its owner (KSP rewrites the ambient light as the vessel moves). Restoring puts
    // the baseline back only while the value is still the one written, so a newer value of someone else stays.
    // Free of RenderSettings, so that it is tested outside the game.
    internal sealed class AmbientTracker
    {
        // Values closer than this are the same value (floats come back from the engine as they were written).
        private const float Same = 1e-5f;

        private Color baseline;
        private Color written;
        private bool active;

        // How often the value was found changed by someone else while the tracker was active.
        internal int ExternalWrites { get; private set; }

        internal bool Active
        {
            get { return active; }
        }

        internal Color Baseline
        {
            get { return baseline; }
        }

        // The value written last.
        internal Color Written
        {
            get { return written; }
        }

        // The value to write for the current one and a factor (alpha stays). The current value is the baseline unless it is the one
        // written the last time.
        internal Color Scale(Color current, float factor)
        {
            if (!active)
            {
                baseline = current;
                active = true;
            }
            else if (!Near(current, written))
            {
                baseline = current;
                ExternalWrites++;
            }
            written = new Color(baseline.r * factor, baseline.g * factor, baseline.b * factor, baseline.a);
            return written;
        }

        // Ends the scaling. True with the colour to write back when the value is still the one written; false when it is not (someone
        // else owns it now) or nothing was scaled.
        internal bool Restore(Color current, out Color restore)
        {
            restore = current;
            if (!active) return false;
            active = false;
            if (!Near(current, written)) return false;
            restore = baseline;
            return true;
        }

        private static bool Near(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) <= Same && Mathf.Abs(a.g - b.g) <= Same && Mathf.Abs(a.b - b.b) <= Same && Mathf.Abs(a.a - b.a) <= Same;
        }
    }
}
