using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the atmospheres remember of one ground quad: the colours its vertices had before the snow, and the latitude, height and noise
    // the snow cover of each vertex follows from. With these a quad that is already built is painted again for another phase without
    // being built again. Free of the quad type, so that it can be tested outside the game.
    internal sealed class QuadSnow
    {
        // The vertex colours before the snow, and the colours with it, as the mesh keeps them.
        internal readonly Color32[] Original;
        internal readonly Color32[] Painted;

        // Per vertex: latitude in degrees (north positive), height above sea level in metres, noise from -1 to 1.
        private readonly float[] latitude;
        private readonly float[] height;
        private readonly float[] noise;

        // The season phase the quad was painted for last; NaN before the first time.
        internal double PaintPhase = double.NaN;

        // The mean snow amount of the quad when the scatters of Parallax on it were last made; negative if never known.
        internal float ScatterBasis = -1f;

        // The mean snow amount of the quad at its last Paint.
        internal float LastMean;

        internal QuadSnow(int vertexCount)
        {
            Original = new Color32[vertexCount];
            Painted = new Color32[vertexCount];
            latitude = new float[vertexCount];
            height = new float[vertexCount];
            noise = new float[vertexCount];
        }

        internal int Length
        {
            get { return Original.Length; }
        }

        // The inputs of a vertex. A vertex outside the quad is ignored.
        internal void Set(int index, Color32 original, double latitudeDegrees, double heightMetres, double noiseValue)
        {
            if (index < 0 || index >= Original.Length) return;
            Original[index] = original;
            latitude[index] = (float)latitudeDegrees;
            height[index] = (float)heightMetres;
            noise[index] = (float)noiseValue;
        }

        // The snow amount of a point for a phase: the cover of the mask times the strength.
        internal static float Amount(double latitudeDegrees, double heightMetres, double phase, double noiseValue, float strength, double winterLine)
        {
            return SnowPaint.Amount(SnowMask.Coverage(latitudeDegrees, heightMetres, phase, noiseValue, winterLine), strength);
        }

        // Fills Painted for that phase from the originals; returns the mean snow amount of the quad.
        internal float Paint(double phase, float strength, double winterLine)
        {
            double sum = 0;
            for (int i = 0; i < Original.Length; i++)
            {
                float amount = Amount(latitude[i], height[i], phase, noise[i], strength, winterLine);
                Painted[i] = SnowPaint.Blend(Original[i], amount);
                sum += amount;
            }
            PaintPhase = phase;
            LastMean = Original.Length == 0 ? 0f : (float)(sum / Original.Length);
            return LastMean;
        }

        // The mean snow amount of the quad for a phase, without painting.
        internal float MeanAmount(double phase, float strength, double winterLine)
        {
            double sum = 0;
            for (int i = 0; i < Original.Length; i++) sum += Amount(latitude[i], height[i], phase, noise[i], strength, winterLine);
            return Original.Length == 0 ? 0f : (float)(sum / Original.Length);
        }

        // Whether the quad was painted for a phase that differs from that one by more than the tolerance (the year wraps).
        internal bool NeedsPaint(double phase, double tolerance)
        {
            if (double.IsNaN(PaintPhase)) return true;
            return SeasonClock.PhaseDistance(PaintPhase, phase) > tolerance;
        }
    }
}
