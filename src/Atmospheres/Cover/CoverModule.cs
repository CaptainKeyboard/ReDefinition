using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Runs the cover's maps: each body someone asked for lately (the ground repainter, the orbit overlay, the vehicles, the active body)
    // has its CoverMap kept up to date by CoverResampler. Also drops the tracks that lie in the future of the game time (a revert).
    // Does nothing while the master switch or ground cover is off, so the game is vanilla.
    [AtmospheresModule(66)]
    internal sealed class CoverModule : IAtmospheresModule
    {
        // A body counts as wanted this many real seconds after the last request.
        internal const float WantedSeconds = 10f;

        private static readonly Dictionary<int, float> Requests = new Dictionary<int, float>();
        private static readonly List<int> Scratch = new List<int>();

        internal static bool Enabled
        {
            get { return AtmospheresSettings.Enabled && AtmospheresSettings.GroundCover; }
        }

        // Asks for the body's map to be kept up to date (the call is cheap; call it every frame it is needed).
        internal static void Request(CelestialBody body)
        {
            if (body == null) return;
            Requests[body.flightGlobalsIndex] = Time.realtimeSinceStartup;
        }

        public void Init()
        {
            Requests.Clear();
            CoverResampler.Reset();
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            Request(newBody);
        }

        public void Tick(float dt)
        {
            if (!Enabled) return;
            double ut;
            if (!CoverClock.TryUt(out ut)) return;
            Request(BodyWatcher.ActiveBody);
            List<CelestialBody> bodies = GameGlobals.Bodies;
            if (bodies == null) return;
            float now = Time.realtimeSinceStartup;
            Scratch.Clear();
            foreach (KeyValuePair<int, float> pair in Requests)
                if (now - pair.Value > WantedSeconds) Scratch.Add(pair.Key);
            for (int i = 0; i < Scratch.Count; i++) Requests.Remove(Scratch[i]);
            foreach (KeyValuePair<int, float> pair in Requests)
            {
                if (pair.Key < 0 || pair.Key >= bodies.Count) continue;
                CoverBodyState state = CoverBodies.Of(bodies[pair.Key]);
                if (state == null) continue;
                CoverResampler.Tick(state, now, ut);
            }
        }

        public void Shutdown()
        {
            Requests.Clear();
        }
    }
}
