using System;

namespace ReDefinition.Atmospheres
{
    // One cover layer of one body as the cover code reads it: plain numbers, no engine type, so that the depth maths, the orbit mask
    // and the series run outside the game. CoverDefs.From builds one from the body's CoverRules (cfg) and the colour of the ground.
    // Package D34 (surface cover). The depth itself is the weather field's: this class says only how a depth looks and where it may lie.
    internal sealed class CoverDef
    {
        internal CoverDef(string id, CoverKind kind, float red, float green, float blue, float roughness, float maxDepthM,
            double minLatDeg, double maxLatDeg, double minAltM, double maxAltM, double maxSlopeDeg, bool noWater,
            bool tracks, double tracksTauSeconds, float driftAmount, float driftStretch)
        {
            Id = id ?? "";
            Kind = kind;
            Red = red;
            Green = green;
            Blue = blue;
            Roughness = roughness;
            MaxDepthM = maxDepthM > 1e-4f ? maxDepthM : 1e-4f;
            MinLatDeg = minLatDeg;
            MaxLatDeg = maxLatDeg;
            MinAltM = minAltM;
            MaxAltM = maxAltM;
            MaxSlopeDeg = maxSlopeDeg;
            // Sand, dust and ash are deposits of a dry body: they never lie on water whatever the cfg says.
            NoWater = noWater || kind == CoverKind.Sand || kind == CoverKind.Dust || kind == CoverKind.Ash;
            Tracks = tracks;
            TracksTauSeconds = tracksTauSeconds > 1.0 ? tracksTauSeconds : 1.0;
            DriftAmount = driftAmount < 0f ? 0f : driftAmount > 1f ? 1f : driftAmount;
            DriftStretch = driftStretch < 1f ? 1f : driftStretch;
        }

        internal string Id { get; }
        internal CoverKind Kind { get; }
        // The colour of the cover (0 to 1 each), the roughness and the depth in metres at which the cover is full.
        internal float Red { get; }
        internal float Green { get; }
        internal float Blue { get; }
        internal float Roughness { get; }
        internal float MaxDepthM { get; }
        // The mask: where the cover may lie.
        internal double MinLatDeg { get; }
        internal double MaxLatDeg { get; }
        internal double MinAltM { get; }
        internal double MaxAltM { get; }
        internal double MaxSlopeDeg { get; }
        internal bool NoWater { get; }
        // Tracks of wheels and feet, and the time constant of their fade in seconds of game time.
        internal bool Tracks { get; }
        internal double TracksTauSeconds { get; }
        // The wind drift (0 off, 1 full) and how far its pattern stretches along the wind.
        internal float DriftAmount { get; }
        internal float DriftStretch { get; }

        // Whether the cover is a ridged pattern across the wind (dunes of sand and dust) rather than snow drifting along it.
        internal bool Dunes
        {
            get { return Kind == CoverKind.Sand || Kind == CoverKind.Dust; }
        }
    }
}
