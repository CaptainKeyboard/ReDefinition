using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the cover remembers of one ground quad: the colours its vertices had before the cover, and the place, height, noise and slope
    // of each vertex. With these a quad that is already built is painted again for another state of the field without being built
    // again. Free of the quad type, so that it is tested outside the game. The record keeps the extra per-vertex data of the wind drift
    // (the normal's east and north parts) only when the drift is on.
    internal sealed class QuadCover
    {
        // The vertex colours before the cover, and the colours with it, as the mesh keeps them.
        internal readonly Color32[] Original;
        internal readonly Color32[] Painted;

        // Per vertex: latitude and longitude in degrees, height above sea level in metres, noise from -1 to 1, slope in degrees.
        private readonly float[] latitude;
        private readonly float[] longitude;
        private readonly float[] height;
        private readonly float[] noise;
        private readonly float[] slope;
        // The normal's east and north parts (-127..127 for -1..1); null until the drift asks for them.
        private sbyte[] normalEast;
        private sbyte[] normalNorth;

        // The state the quad was painted for last: the map's highest tile version under it, the settings stamp, the game time of the
        // wind pattern; the first paint has -1.
        internal int PaintedVersion = -1;
        internal int PaintedSettings = int.MinValue;
        internal double PaintedUt = double.NaN;

        // The mean cover of the quad when the scatters of Parallax on it were last made; negative if never known.
        internal float ScatterBasis = -1f;

        // The mean cover of the quad at its last Paint.
        internal float LastMean;

        // The bounding box of the quad in degrees (set while the vertices come in).
        internal float LatMin = 90f, LatMax = -90f, LonMin = 180f, LonMax = -180f;

        // Whether the slopes were filled from the mesh normals (they come after the build).
        internal bool HasSlope;

        // The body's state the quad belongs to.
        internal readonly CoverBodyState Body;

        internal QuadCover(CoverBodyState body, int vertexCount)
        {
            Body = body;
            Original = new Color32[vertexCount];
            Painted = new Color32[vertexCount];
            latitude = new float[vertexCount];
            longitude = new float[vertexCount];
            height = new float[vertexCount];
            noise = new float[vertexCount];
            slope = new float[vertexCount];
        }

        internal int Length
        {
            get { return Original.Length; }
        }

        internal bool HasNormals
        {
            get { return normalEast != null; }
        }

        // The inputs of a vertex. A vertex outside the quad is ignored.
        internal void Set(int index, Color32 original, double latitudeDegrees, double longitudeDegrees, double heightMetres, double noiseValue)
        {
            if (index < 0 || index >= Original.Length) return;
            Original[index] = original;
            latitude[index] = (float)latitudeDegrees;
            longitude[index] = (float)longitudeDegrees;
            height[index] = (float)heightMetres;
            noise[index] = (float)noiseValue;
            if (latitude[index] < LatMin) LatMin = latitude[index];
            if (latitude[index] > LatMax) LatMax = latitude[index];
            if (longitude[index] < LonMin) LonMin = longitude[index];
            if (longitude[index] > LonMax) LonMax = longitude[index];
        }

        // The slope of a vertex (degrees), and the normal's east and north parts where the drift is on (pass NaN to leave them).
        internal void SetSlope(int index, double slopeDegrees, double east, double north)
        {
            if (index < 0 || index >= Original.Length) return;
            slope[index] = double.IsNaN(slopeDegrees) ? 0f : (float)slopeDegrees;
            if (!double.IsNaN(east) && !double.IsNaN(north))
            {
                if (normalEast == null)
                {
                    normalEast = new sbyte[Original.Length];
                    normalNorth = new sbyte[Original.Length];
                }
                normalEast[index] = (sbyte)Math.Max(-127, Math.Min(127, Math.Round(east * 127.0)));
                normalNorth[index] = (sbyte)Math.Max(-127, Math.Min(127, Math.Round(north * 127.0)));
            }
        }

        internal double LatitudeOf(int i) { return latitude[i]; }
        internal double LongitudeOf(int i) { return longitude[i]; }
        internal double HeightOf(int i) { return height[i]; }
        internal double NoiseOf(int i) { return noise[i]; }
        internal double SlopeOf(int i) { return slope[i]; }
        internal double NormalEastOf(int i) { return normalEast == null ? 0.0 : normalEast[i] / 127.0; }
        internal double NormalNorthOf(int i) { return normalNorth == null ? 0.0 : normalNorth[i] / 127.0; }

        // The cover 0 to 1 at one vertex for the frame and the settings.
        internal float AmountAt(int i, CoverMap.Frame frame, CoverLook look, bool isWater)
        {
            double fieldDepthM = frame.Sample01(latitude[i], longitude[i]) * look.Def.MaxDepthM;
            double mask = CoverDepth.Mask(look.Def, latitude[i], height[i], slope[i], isWater);
            if (look.Drift > 0 && fieldDepthM > 0)
            {
                double ux, uy, uz;
                Geo.DirectionOf(latitude[i] * Math.PI / 180.0, longitude[i] * Math.PI / 180.0, out ux, out uy, out uz);
                double f = CoverWind.Factor(ux, uy, uz, look.WindEast, look.WindNorth, look.TravelEastM, look.TravelNorthM, look.Drift, look.Def.DriftStretch, look.Def.Dunes, look.RadiusM);
                double d = CoverDepth.Depth01(look.Def, fieldDepthM, mask, noise[i], look.EdgeNoise, look.Strength);
                return (float)CoverWind.Redistribute(d, f, look.Drift, NormalEastOf(i), NormalNorthOf(i), look.WindEast, look.WindNorth);
            }
            return (float)CoverDepth.Depth01(look.Def, fieldDepthM, mask, noise[i], look.EdgeNoise, look.Strength);
        }

        // Fills Painted for the frame from the originals; returns the mean cover of the quad.
        internal float Paint(CoverMap.Frame frame, CoverLook look, bool waterAtSeaLevel)
        {
            double sum = 0;
            for (int i = 0; i < Original.Length; i++)
            {
                float amount = AmountAt(i, frame, look, waterAtSeaLevel && height[i] <= 0.0);
                Painted[i] = CoverPaint.Blend(Original[i], look.Def, amount);
                sum += amount;
            }
            LastMean = Original.Length == 0 ? 0f : (float)(sum / Original.Length);
            return LastMean;
        }

        // The mean cover of the quad without painting.
        internal float MeanAmount(CoverMap.Frame frame, CoverLook look, bool waterAtSeaLevel)
        {
            double sum = 0;
            for (int i = 0; i < Original.Length; i++) sum += AmountAt(i, frame, look, waterAtSeaLevel && height[i] <= 0.0);
            return Original.Length == 0 ? 0f : (float)(sum / Original.Length);
        }

        // Whether the quad has to be painted again: never painted, the map changed under it, the settings changed, or the wind pattern
        // moved far enough in game time.
        internal bool NeedsPaint(CoverMap.Frame frame, int settingsStamp, double ut, double driftSeconds)
        {
            if (PaintedVersion < 0) return true;
            if (PaintedSettings != settingsStamp) return true;
            if (CoverMap.MaxVersion(frame.TileVersions, LatMin, LatMax, LonMin, LonMax) > PaintedVersion) return true;
            return driftSeconds > 0 && !double.IsNaN(PaintedUt) && Math.Abs(ut - PaintedUt) >= driftSeconds;
        }

        // Notes what the quad was painted for.
        internal void MarkPainted(CoverMap.Frame frame, int settingsStamp, double ut)
        {
            PaintedVersion = Math.Max(0, CoverMap.MaxVersion(frame.TileVersions, LatMin, LatMax, LonMin, LonMax));
            PaintedSettings = settingsStamp;
            PaintedUt = ut;
        }

        // The band of a mean cover (0 under 0.4, 1 under 0.8, 2 above): a Parallax scatter field is made anew only when the mean moves to
        // another band, so a storm that changes a large area does not respawn every field at once.
        internal static int Band(float mean)
        {
            return mean < 0.4f ? 0 : mean < 0.8f ? 1 : 2;
        }

        // Whether the scatters are made anew: never known, or the band changed. Dust makes no respawn; tracks never do.
        internal static bool RegenerateScatters(CoverKind kind, float basis, float mean)
        {
            if (kind == CoverKind.Dust) return false;
            if (basis < 0f) return true;
            return Band(basis) != Band(mean);
        }
    }

    // What a paint needs besides the quad: the layer, the strengths, the wind and the time. One instance for a pass.
    internal sealed class CoverLook
    {
        internal CoverDef Def;
        internal double Strength = 1.0;
        internal double EdgeNoise = 0.12;
        // The wind drift 0..1 (the layer's drift times the setting), the wind at the quad in m/s, the game time and the body radius.
        internal double Drift;
        internal double WindEast, WindNorth;
        // How far the pattern has been carried by the wind, in metres east and north (the body's CoverWindTravel).
        internal double TravelEastM, TravelNorthM;
        internal double Ut;
        internal double RadiusM = 600000.0;
    }
}
