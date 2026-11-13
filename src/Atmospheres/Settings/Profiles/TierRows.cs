using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The budget row of one quality tier: what a tier changes. A tier changes COST and never TASTE: frequency, strengths, colours and the
    // switches stay the player's. Part of the row is settings (QualityProfile writes them), part is budget numbers that the renderers read
    // through TierRows.For (particle cap, coverage and painter resolution, map update of the generator), which have no key because the
    // player has no reason to set them apart from the tier.
    //
    // Sources: the steps, resolutions and orbit sizes are the rows of seasons/08-performance.md section 5 and the brief D36 (volume
    // steps 16/20/28/36/48; light count 2/4/8; orbit map 2048, 4096 from High); the whole-system GPU budget by tier (Low 1.2, Medium 2.6,
    // High 3.6, Ultra 5.0 ms, master plan section 10) scales the particle cap from 20 000 billboards at High (brief C22): 6 700, 14 400,
    // 27 800. The generator's map width and update interval and the painter width are estimates [EST] for E42 to tune.
    internal sealed class TierRow
    {
        internal TierRow(QualityTier tier, int volumeSteps, float volumeResolutionAfterGate, int lightCount, SimGridChoice simGrid,
            int orbitMapWidth, int particleCap, float coverageResolution, int painterWidth, int mapWidth, float mapUpdateSeconds)
        {
            Tier = tier;
            VolumeSteps = volumeSteps;
            VolumeResolutionAfterGate = volumeResolutionAfterGate;
            LightCount = lightCount;
            SimGrid = simGrid;
            OrbitMapWidth = orbitMapWidth;
            ParticleCap = particleCap;
            CoverageResolution = coverageResolution;
            PainterWidth = painterWidth;
            MapWidth = mapWidth;
            MapUpdateSeconds = mapUpdateSeconds;
        }

        internal QualityTier Tier { get; }
        // Key volumeSteps: the march steps of the volume pass.
        internal int VolumeSteps { get; }
        // Key volumeResolution once the half-resolution look gate has passed (VolumeResolutionGate); full size before.
        internal float VolumeResolutionAfterGate { get; }
        // Key lightCount: the lamps that scatter light in fog and precipitation.
        internal int LightCount { get; }
        // Key simGrid: the size of the simulation grid against the body's own.
        internal SimGridChoice SimGrid { get; }
        // Key orbitMapWidth: the width of the weather map in the orbit and map view.
        internal int OrbitMapWidth { get; }
        // The most precipitation billboards drawn; negative for no limit (Max, measured).
        internal int ParticleCap { get; }
        // The factor on the resolution of the surface cover map.
        internal float CoverageResolution { get; }
        // The width in pixels of the textures the map painter hands to EVE (the height is half).
        internal int PainterWidth { get; }
        // The width of the generator's global maps (the height is half) and how often they are made anew, in seconds.
        internal int MapWidth { get; }
        internal float MapUpdateSeconds { get; }

        // The volume resolution this row asks for under the gate: full size until the gate has passed.
        internal float VolumeResolution(bool gatePassed)
        {
            return gatePassed ? VolumeResolutionAfterGate : 1f;
        }
    }

    // The table of the tiers.
    internal static class TierRows
    {
        internal const int NoLimit = -1;

        private static readonly TierRow Low = new TierRow(QualityTier.Low, 16, 0.25f, 2, SimGridChoice.Coarse, 1024, 6700, 0.5f, 1024, 256, 8f);
        private static readonly TierRow Medium = new TierRow(QualityTier.Medium, 20, 0.5f, 4, SimGridChoice.Normal, 2048, 14400, 0.5f, 2048, 512, 4f);
        private static readonly TierRow High = new TierRow(QualityTier.High, 28, 1f, 8, SimGridChoice.Normal, 4096, 20000, 1f, 2048, 512, 3f);
        private static readonly TierRow Ultra = new TierRow(QualityTier.Ultra, 36, 1f, 8, SimGridChoice.Fine, 4096, 27800, 1f, 4096, 1024, 2f);
        // Max has no limit: the most the keys take, and the particle cap off. Its cost is measured, not budgeted.
        private static readonly TierRow Max = new TierRow(QualityTier.Max, 48, 1f, 8, SimGridChoice.Fine, 4096, NoLimit, 1f, 4096, 1024, 1f);

        // The tiers with a row, lowest first.
        internal static readonly QualityTier[] All = { QualityTier.Low, QualityTier.Medium, QualityTier.High, QualityTier.Ultra, QualityTier.Max };

        // The row of a tier; Auto is read as Medium (the tier a card of the A4500 class gets; QualityProfile resolves Auto before it asks).
        internal static TierRow For(QualityTier tier)
        {
            switch (tier)
            {
                case QualityTier.Low: return Low;
                case QualityTier.High: return High;
                case QualityTier.Ultra: return Ultra;
                case QualityTier.Max: return Max;
                default: return Medium;
            }
        }

        // The settings a tier sets, each with the profile row it belongs to (the key table's ProfileRow of that key).
        internal static readonly string[] SetKeys =
        {
            AtmospheresSettings.Keys.VolumeSteps,
            AtmospheresSettings.Keys.VolumeResolution,
            AtmospheresSettings.Keys.LightCount,
            AtmospheresSettings.Keys.SimGrid,
            AtmospheresSettings.Keys.OrbitMapWidth
        };

        // The keys that carry a profile row in the key table (the group they belong to) but that no tier sets, each with the reason: they
        // are the player's choice of a feature, of gameplay or of taste. A key with a row is in SetKeys or here.
        internal static readonly Dictionary<string, string> NotSet = new Dictionary<string, string>
        {
            { AtmospheresSettings.Keys.QualityTier, "the choice of the tier itself" },
            { AtmospheresSettings.Keys.QualityGovernor, "a switch of the player's; its rule is E42's" },
            { AtmospheresSettings.Keys.CloudSource, "which renderer draws the clouds, not how large" },
            { AtmospheresSettings.Keys.WeatherCompose, "where the veil and flakes are composed, not how large" },
            { AtmospheresSettings.Keys.EveLayerCap, "a limit on EVE's own layers, which are EVE's cost" },
            { AtmospheresSettings.Keys.VolumePass, "a switch of the player's" },
            { AtmospheresSettings.Keys.VolumeCompose, "where the volume is composed, not how large" },
            { AtmospheresSettings.Keys.WindSource, "gameplay: whose wind is used" },
            { AtmospheresSettings.Keys.WindInjection, "gameplay: how the wind reaches a vehicle" },
            { AtmospheresSettings.Keys.WindForces, "gameplay switch" },
            { AtmospheresSettings.Keys.WindStrength, "gameplay and taste" },
            { AtmospheresSettings.Keys.GenEnabled, "a switch of the player's" },
            { AtmospheresSettings.Keys.SimLevels, "0 asks for the body's own; the grid choice carries the size" },
            { AtmospheresSettings.Keys.SimSliceMs, "a hitch limit of the main thread, not a size" },
            { AtmospheresSettings.Keys.PrecipDensity, "taste: how dense the precipitation looks; the cap is TierRow.ParticleCap" },
            { AtmospheresSettings.Keys.StormCullDistance, "a look switch of the player's" },
            { AtmospheresSettings.Keys.LightInMedium, "a switch of the player's" },
            { AtmospheresSettings.Keys.Aurora, "which aurora is drawn, a choice of look" },
            { AtmospheresSettings.Keys.CameraFrost, "a switch of the player's" },
            { AtmospheresSettings.Keys.WeatherAudio, "a switch of the player's" },
            { AtmospheresSettings.Keys.GroundCover, "a switch of the player's" },
            { AtmospheresSettings.Keys.VehicleCover, "a choice of the player's" },
            { AtmospheresSettings.Keys.Tracks, "a switch of the player's" },
            { AtmospheresSettings.Keys.SeaIce, "a choice of the player's" },
            { AtmospheresSettings.Keys.IceDrawDistanceKm, "a distance the owner has not given a tier row" },
            { AtmospheresSettings.Keys.ForecastEnabled, "a switch of the player's" },
            { AtmospheresSettings.Keys.ForecastHorizonHours, "a length of time the player wants, not a size" },
            { AtmospheresSettings.Keys.LayerCap, "the tier mapping of the own clouds is not settled and waits for the owner's agreement to lower any default" },
            { AtmospheresSettings.Keys.LayerStepQuality, "the tier mapping of the own clouds is not settled and waits for the owner's agreement to lower any default" },
            { AtmospheresSettings.Keys.CloudNoiseTier, "the tier mapping of the own clouds is not settled and waits for the owner's agreement to lower any default" },
            { AtmospheresSettings.Keys.CloudLightQuality, "the tier mapping of the own clouds is not settled and waits for the owner's agreement to lower any default" },
            { AtmospheresSettings.Keys.CloudShadowQuality, "the tier mapping of the own clouds is not settled and waits for the owner's agreement to lower any default" },
            { AtmospheresSettings.Keys.CloudResolution, "a level above 1 lowers the cloud picture's resolution; a tier sets it only after the owner agrees to a lower default" },
            { AtmospheresSettings.Keys.CloudRefineBisect, "a cheaper form of the entry refinement that gives up some of its gain; a tier sets it only after the owner agrees to the trade" },
            { AtmospheresSettings.Keys.CloudRefineEdgeMask, "switches the edge-adaptive cloud picture on, which lowers the resolution of the flat faces; a tier sets it only after the owner agrees to a lower default" },
            { AtmospheresSettings.Keys.CloudSkipEmpty, "starts no ray in tiles the maps prove empty; the bound is conservative but its saving and error are measured, and a tier sets it only after the owner agrees" },
            { AtmospheresSettings.Keys.CloudSkipClosed, "drops the layers above a deck proved closed for a tile; the early-out of the march already does most of this, and a tier sets it only after the owner agrees" },
            { AtmospheresSettings.Keys.CloudLayerDistanceCut, "thins and drops layers the air leaves below a visibility weight, which needs the aerial perspective; a tier sets it only after the owner agrees to the trade" },
            { AtmospheresSettings.Keys.CloudStepGrowth, "coarsens the far part of long spans, a quality reduction; a tier sets it only after the owner agrees to a lower default" },
            { AtmospheresSettings.Keys.CloudTapLod, "repeats the sun march less often with distance, a quality reduction; a tier sets it only after the owner agrees to a lower default" },
            { AtmospheresSettings.Keys.CloudDetailLod, "drops the noise detail where a pixel cannot resolve it and switches the far shape mean on; a tier sets it only after the owner agrees" },
            { AtmospheresSettings.Keys.CloudLayerCapDistance, "thins the layers beyond the nearest few from a distance, a quality reduction; a tier sets it only after the owner agrees to a lower default" }
        };

        // Keys a tier sets whose entry in the key table names no profile row (orbitMapWidth has none: the table has no Orbit row). A
        // request to the owner of the table is in the report of D36.
        internal static readonly string[] SetWithoutRow = { AtmospheresSettings.Keys.OrbitMapWidth };
    }

    // Whether the half-resolution look gate of the volume pass has passed. Until it has, every tier draws the volume at full size
    // (volumeResolution 1.0, the key's default). C21 measured the half-size structure error at 0.9 to 1.0 and left the decision to E40's
    // look judgement in the full stack, so the gate is closed [VERIFY after E40].
    internal static class VolumeResolutionGate
    {
        internal static bool Passed;
    }
}
