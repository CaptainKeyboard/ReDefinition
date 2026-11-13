using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using AtmoSim.Core;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Turns what a body is (its climate rules and the game's facts about it, the terrain heights, the fit of its temperature curves) into
    // the parameter set of the weather generator (AtmoSim.Core.BodyParams). The generator itself is the source of tools/atmosphere-sim
    // compiled into this assembly as it is; this class is the only place that knows both sides. It starts from the generator's own
    // defaults and sets what the body states, so a lever that the generator gains keeps its default and needs no change here.
    internal static class GeneratorBodies
    {
        // The reference pressure of the body factor of the precipitation bands: sea level of the air the bands were signed for.
        internal const double ReferencePressurePa = 101325.0;
        // The share of the generator's mean day-night swing and others come from the game's temperature fit; these bound a body factor.
        internal const double MinPrecipFactor = 0.05, MaxPrecipFactor = 4.0;
        // The height grid of the generator is one degree (the B09 grid).
        internal const int GridWidth = GeneratorRaster.Width, GridHeight = GeneratorRaster.Height;

        // The seed of the core for a body: the body's seed (hash of its name and the player's genSeed) plus the node's seedOffset.
        internal static ulong SeedOf(BodyClimate c)
        {
            return unchecked((uint)(c.Seed + c.Generator.SeedOffset));
        }

        // Whether the body needs the terrain raster (the ocean source is the game's terrain).
        internal static bool NeedsHeights(BodyClimate c)
        {
            return c.Generator.OceanSource == GeneratorOceanSource.Terrain;
        }

        // The parameter set of a body. heights is the terrain raster (GridWidth x GridHeight, metres, row 0 north) or null where the rules do
        // not need it; fit is the fit of the game's temperature curves or null. Notes receive what was taken from where.
        internal static BodyParams Build(BodyClimate c, BodyFacts f, float[] heights, ThermalFit fit, List<string> notes)
        {
            GeneratorRules g = c.Generator;
            var p = new BodyParams();
            p.Name = c.Name;
            p.RadiusM = c.Radius > 0 ? c.Radius : f.RadiusM;

            double rotation = First(g.RotationPeriodS, f.DaySeconds);
            if (rotation > 0) p.RotationPeriodS = rotation;
            if (c.YearSeconds > 0) p.YearS = c.YearSeconds;

            // The tilt: the node's, else the body's own where it is tilted, else the artificial axis; none where the body has no seasons.
            double tilt = g.ObliquityDeg;
            if (double.IsNaN(tilt)) tilt = c.Seasons.TiltDegrees > BodyConfigParser.AxisTiltDetectDegrees ? c.Seasons.TiltDegrees : GeneratorRules.DefaultObliquityDeg;
            if (c.Seasons.Clock != SeasonClockKind.Orbit && double.IsNaN(g.ObliquityDeg)) tilt = 0.0;
            p.ObliquityRad = tilt * Math.PI / 180.0;

            double flux = First(g.SolarFluxWm2, f.SolarFluxWm2);
            if (flux > 0) p.SolarFluxWm2 = flux;
            double pressure = First(g.SurfacePressureKPa, f.SeaLevelPressureKPa) * 1000.0;
            if (pressure > 0) p.SurfacePressurePa = pressure;
            double gravity = First(g.GravityMs2, f.SurfaceGravityMs2);
            if (gravity > 0) p.GravityMs2 = gravity;
            double gas = First(g.GasConstant, f.GasConstant);
            if (gas > 0) p.GasConstant = gas;
            double cp = First(g.Cp, f.Cp);
            if (cp > 0) p.Cp = cp;

            // Temperature: the rules, else the fit of the game's curves, else the target curve the probe sampled, else the generator's default.
            double mean = First(g.SurfaceMeanK, fit != null ? fit.MeanK : double.NaN, f.SeaLevelTemperatureK);
            if (mean > 0) p.SurfaceMeanK = mean;
            if (fit != null)
            {
                p.ProfileK = (double[])fit.ProfileK.Clone();
                double shift = double.IsNaN(g.SurfaceMeanK) ? 0.0 : g.SurfaceMeanK - fit.MeanK;
                if (shift != 0.0)
                {
                    for (int k = 0; k < p.ProfileK.Length; k++) p.ProfileK[k] += shift;
                }
            }
            double lapse = First(g.LapseKPerM, fit != null ? fit.LapseKPerM : double.NaN, LapseOf(f.TemperatureTarget));
            if (lapse > 0) p.LapseKPerM = lapse;
            double diurnal = First(g.DiurnalLandAmpK, fit != null ? fit.DiurnalAmpK : double.NaN);
            if (diurnal >= 0) p.DiurnalLandAmpK = diurnal;
            double tropopause = First(g.TropopauseM, f.AtmosphereDepthM > 0 ? g.TropopauseDepthShare * f.AtmosphereDepthM : double.NaN);
            if (tropopause > 0) p.TropopauseM = tropopause;

            // The sea and the ice model.
            p.FreezeK0 = 273.15 + c.Temperature.WaterCelsius;

            // The change from snow to rain: the body's rule, the one the frame driver draws the sky with (PrecipTypeModel).
            float centreC, widthC;
            PrecipTypeModel.Parameters(c.Precip, out centreC, out widthC);
            p.PrecipCentreC = centreC;
            p.PrecipWidthC = widthC;
            if (!double.IsNaN(g.SalinityPsu)) p.SalinityPsu = g.SalinityPsu;
            if (!double.IsNaN(g.FreezeShiftKPerPsu)) p.FreezeShiftKPerPsu = g.FreezeShiftKPerPsu;
            if (!double.IsNaN(g.MixedLayerDepthM)) p.MixedLayerDepthM = g.MixedLayerDepthM;
            if (!double.IsNaN(g.SeaHeatCapacity)) p.SeaHeatCapacityPerM3 *= g.SeaHeatCapacity;

            // Dust and the body factor of the precipitation bands.
            p.Dusty = g.Dust == GeneratorDust.On || (g.Dust == GeneratorDust.Auto && DustType(c) != PrecipType.None);
            if (!double.IsNaN(g.DustWindThresholdMs)) p.DustWindThresholdMs = g.DustWindThresholdMs;
            p.TidallyLocked = f.TidallyLocked;
            double factor = g.PrecipFactor;
            if (double.IsNaN(factor) && pressure > 0) factor = Math.Sqrt(pressure / ReferencePressurePa);
            if (!double.IsNaN(factor)) p.PrecipBodyFactor = Math.Max(MinPrecipFactor, Math.Min(MaxPrecipFactor, factor));

            p.WavesEnabled = g.Waves;
            p.CellsEnabled = g.Cells;

            // The ocean and the moisture source.
            switch (g.OceanSource)
            {
                case GeneratorOceanSource.Terrain:
                    p.Grid = heights != null ? BuildGrid(heights, GridWidth, GridHeight, f.HasOcean, g.SeaLevelM) : null;
                    p.OceanFractionTarget = f.HasOcean ? p.OceanFractionTarget : 0.0;
                    break;
                case GeneratorOceanSource.Synthetic:
                    p.OceanFractionTarget = !double.IsNaN(g.OceanFraction) ? g.OceanFraction : f.HasOcean ? p.OceanFractionTarget : 0.0;
                    break;
                default:
                    p.OceanFractionTarget = 0.0;
                    p.Grid = BuildGrid(heights, GridWidth, GridHeight, false, g.SeaLevelM);
                    break;
            }
            ApplyLevers(p, g.Levers, notes);
            return p;
        }

        // The grid of the generator from a height raster: ocean where the body has an ocean and the surface lies below the sea level, the
        // terrain above it, the moisture source smoothed over MapGen.MoistBlurDeg. With heights null the grid is flat land (or sea).
        internal static HeightGrid BuildGrid(float[] heights, int width, int height, bool hasOcean, double seaLevelM)
        {
            var grid = new HeightGrid(width, height);
            for (int i = 0; i < grid.Ocean.Length; i++)
            {
                double h = heights != null && i < heights.Length ? heights[i] : 0.0;
                bool ocean = hasOcean && h < seaLevelM;
                grid.Ocean[i] = ocean ? 1.0 : 0.0;
                grid.TerrainM[i] = ocean ? 0.0 : Math.Max(0.0, h - seaLevelM);
            }
            grid.BuildMoist(MapGen.MoistBlurDeg);
            return grid;
        }

        // The type that falls on a dusty body: Sand where the body's covers or precipitation types name sand, Dust where they name dust,
        // None where neither.
        internal static PrecipType DustType(BodyClimate c)
        {
            PrecipType found = PrecipType.None;
            foreach (PrecipTypeRule rule in c.Precip.Types)
            {
                if (rule.Kind == PrecipType.Sand) return PrecipType.Sand;
                if (rule.Kind == PrecipType.Dust) found = PrecipType.Dust;
            }
            foreach (CoverRules cover in c.Covers)
            {
                if (cover.Kind == CoverKind.Sand) return PrecipType.Sand;
                if (cover.Kind == CoverKind.Dust && found == PrecipType.None) found = PrecipType.Dust;
            }
            return found;
        }

        // Sets fields of the parameter set by name: a number for a double or an int field, 0 or 1 for a boolean one. A name that is no field,
        // or a value that does not fit, is a note and changes nothing.
        internal static void ApplyLevers(BodyParams p, IReadOnlyDictionary<string, double> levers, List<string> notes)
        {
            if (levers == null) return;
            foreach (KeyValuePair<string, double> lever in levers)
            {
                FieldInfo field = typeof(BodyParams).GetField(lever.Key, BindingFlags.Public | BindingFlags.Instance);
                if (field == null || field.IsInitOnly)
                {
                    if (notes != null) notes.Add("LEVERS: '" + lever.Key + "' is not a field of the generator's parameters; it is ignored");
                    continue;
                }
                if (field.FieldType == typeof(double)) field.SetValue(p, lever.Value);
                else if (field.FieldType == typeof(int)) field.SetValue(p, (int)Math.Round(lever.Value));
                else if (field.FieldType == typeof(bool)) field.SetValue(p, lever.Value != 0.0);
                else if (notes != null) notes.Add("LEVERS: '" + lever.Key + "' is a " + field.FieldType.Name + " field; only numbers and switches can be set");
            }
        }

        // The lapse rate of a probed temperature curve between its first two points, K per metre; NaN where there is no usable pair.
        internal static double LapseOf(Curve target)
        {
            if (target == null || target.Count < 2) return double.NaN;
            double dz = target.KeyAt(1) - target.KeyAt(0);
            return dz > 0 ? (target.ValueAt(0) - target.ValueAt(1)) / dz : double.NaN;
        }

        // The first number that is not NaN; NaN where all are.
        private static double First(params double[] values)
        {
            foreach (double v in values)
            {
                if (!double.IsNaN(v) && !double.IsInfinity(v)) return v;
            }
            return double.NaN;
        }

        // A one-line description of the inputs, for the log.
        internal static string Describe(BodyParams p)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "radius {0:0} m, rotation {1:0} s, year {2:0} s, tilt {3:0.0} deg, pressure {4:0} Pa, gravity {5:0.00}, mean {6:0.0} K, lapse {7:0.0000} K/m, tropopause {8:0} m, precipitation factor {9:0.00}, profile {10}, grid {11}",
                p.RadiusM, p.RotationPeriodS, p.YearS, p.ObliquityRad * 180.0 / Math.PI, p.SurfacePressurePa, p.GravityMs2, p.SurfaceMeanK, p.LapseKPerM, p.TropopauseM,
                p.PrecipBodyFactor, p.ProfileK != null ? "game" : "model", p.Grid != null ? "terrain" : "synthetic");
        }
    }
}
