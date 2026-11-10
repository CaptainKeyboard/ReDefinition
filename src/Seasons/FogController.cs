using UnityEngine;

namespace ReDefinition.Seasons
{
    // The bookkeeping of Unity's global fog for a storm: the fog as it was before the storm, the fog the storm wrote last, and the
    // decision about putting it back. Like AmbientTracker, the saved fog goes back only while the fog is still the one written; if
    // another mod set it since, it is theirs now and is left alone. Free of RenderSettings (StormEffect reads and writes them), so
    // that it is tested outside the game.
    internal sealed class FogController
    {
        // Densities and colour channels closer than this are the same value (floats come back from the engine as they were written).
        internal const float Same = 1e-6f;

        private bool saved;
        private bool savedFog;
        private FogMode savedMode;
        private float savedDensity;
        private Color savedColour;

        private bool written;
        private float writtenDensity;
        private Color writtenColour;

        private bool outsideChange;

        // The density the storm wrote last.
        internal float WrittenDensity
        {
            get { return writtenDensity; }
        }

        internal bool Saved
        {
            get { return saved; }
        }

        internal bool SavedFog
        {
            get { return savedFog; }
        }

        internal FogMode SavedMode
        {
            get { return savedMode; }
        }

        internal float SavedDensity
        {
            get { return savedDensity; }
        }

        internal Color SavedColour
        {
            get { return savedColour; }
        }

        // Remembers the fog as it is now, once per storm; a second call keeps the first.
        internal void Save(bool fog, FogMode mode, float density, Color colour)
        {
            if (saved) return;
            saved = true;
            savedFog = fog;
            savedMode = mode;
            savedDensity = density;
            savedColour = colour;
            written = false;
            outsideChange = false;
        }

        // Whether the density found is not the one written last, for the first time in this storm (the caller logs it once).
        internal bool FoundOutsideChange(float currentDensity)
        {
            if (!written || outsideChange || Mathf.Abs(currentDensity - writtenDensity) <= Same) return false;
            outsideChange = true;
            return true;
        }

        // Records what the storm wrote this frame.
        internal void Wrote(float density, Color colour)
        {
            written = true;
            writtenDensity = density;
            writtenColour = colour;
        }

        // Ends the storm's hold on the fog. True when the saved fog is to be written back: the fog now is still the one written (fog on,
        // exponential squared, the density and colour written). False when nothing was saved or written, or someone else changed it.
        internal bool Restore(bool fog, FogMode mode, float density, Color colour)
        {
            bool ours = saved && written && fog && mode == FogMode.ExponentialSquared
                        && Mathf.Abs(density - writtenDensity) <= Same
                        && Mathf.Abs(colour.r - writtenColour.r) <= Same && Mathf.Abs(colour.g - writtenColour.g) <= Same
                        && Mathf.Abs(colour.b - writtenColour.b) <= Same;
            saved = false;
            written = false;
            return ours;
        }
    }
}
