using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The host of the layers drawn on a planet in the map view and the tracking station (IOrbitOverlayLayer, registered in
    // OrbitOverlayLayers: the surface cover at order 10, the cloud shell at 40, D35's ice between). It walks the registered layers in
    // order, asks each whether it has anything for the body, and draws it inside its own try/catch, so that one broken layer does not
    // stop the others. DrawRound decides how many bodies are drawn and how often: the focused body at every call, the others one by
    // one in turn (orbitBodies Visible and All). The orbit painter's tick (OrbitSnow) calls DrawRound and gives the textures of the
    // bodies that are not drawn any longer back.
    internal static class OrbitOverlayHost
    {
        // The number of layers drawn by the last call, and the number whose Draw threw in the whole session.
        internal static int LastDrawn { get; private set; }
        internal static int Failures { get; private set; }
        // The number of bodies drawn by the last round, the total of rounds, and the index of the other body drawn last.
        internal static int LastBodies { get; private set; }
        internal static int Rounds { get; private set; }
        private static int rotation;

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

        // The indices (into candidates) of the bodies one round draws: the focused body first (Focused mode: only it), then one other
        // candidate in turn for Visible (the candidates that isVisible accepts) and All. focusIndex is the index in candidates, or -1.
        // rotationStart is the host's turn counter. Pure.
        internal static List<int> Pick(int candidateCount, int focusIndex, OrbitBodiesMode mode, Func<int, bool> isVisible, int rotationStart)
        {
            var picked = new List<int>(2);
            if (focusIndex >= 0 && focusIndex < candidateCount) picked.Add(focusIndex);
            if (mode == OrbitBodiesMode.Focused || candidateCount == 0) return picked;
            var others = new List<int>();
            for (int i = 0; i < candidateCount; i++)
            {
                if (i == focusIndex) continue;
                if (mode == OrbitBodiesMode.Visible && (isVisible == null || !isVisible(i))) continue;
                others.Add(i);
            }
            if (others.Count > 0) picked.Add(others[((rotationStart % others.Count) + others.Count) % others.Count]);
            return picked;
        }

        // One round: draws the picked bodies (the focused one with focused = true) and returns their indices in candidates (the layers
        // that hold textures for the others may let them go). candidates are the bodies with a scaled body.
        internal static List<int> DrawRound(IList<CelestialBody> candidates, CelestialBody focus, OrbitBodiesMode mode, Func<CelestialBody, bool> isVisible, double ut)
        {
            int focusIndex = -1;
            for (int i = 0; i < candidates.Count; i++)
                if (ReferenceEquals(candidates[i], focus)) focusIndex = i;
            List<int> picked = Pick(candidates.Count, focusIndex, mode, i => isVisible != null && isVisible(candidates[i]), rotation++);
            int drawn = 0;
            foreach (int i in picked)
            {
                Draw(candidates[i], i == focusIndex, ut);
                drawn++;
            }
            LastBodies = drawn;
            Rounds++;
            return picked;
        }
    }
}
