using System;

namespace ReDefinition.Atmospheres
{
    // The host of the layers drawn on a planet in the map view and the tracking station (IOrbitOverlayLayer, registered in
    // OrbitOverlayLayers). A SKELETON: it walks the registered layers in order, asks each whether it has anything for the body, and
    // draws it inside its own try/catch, so that one broken layer does not stop the others. Package D34 takes the file over and
    // calls Draw from the orbit painter's tick (OrbitSnow today); it decides how many bodies are drawn and how often.
    internal static class OrbitOverlayHost
    {
        // The number of layers drawn by the last call, and the number whose Draw threw in the whole session.
        internal static int LastDrawn { get; private set; }
        internal static int Failures { get; private set; }

        // What happens when a layer throws: a warning in the log. A test replaces it.
        internal static Action<string, Exception> OnFailure = (name, e) =>
            AtmospheresLog.Failed("atmospheres-orbit-overlay", "orbit overlay layer " + name + " failed", e);

        // Draws every layer that wants the body, in order. Returns the number drawn.
        internal static int Draw(CelestialBody body, bool focused, double ut)
        {
            int drawn = 0;
            IOrbitOverlayLayer[] layers = OrbitOverlayLayers.All;
            for (int i = 0; i < layers.Length; i++)
            {
                IOrbitOverlayLayer layer = layers[i];
                try
                {
                    if (!layer.Wants(body)) continue;
                    layer.Draw(body, focused, ut);
                    drawn++;
                }
                catch (Exception e)
                {
                    Failures++;
                    OnFailure(layer.Name, e);
                }
            }
            LastDrawn = drawn;
            return drawn;
        }
    }
}
