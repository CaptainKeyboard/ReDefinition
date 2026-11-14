using System;
using System.Collections.Generic;
using System.Linq;

namespace ReDefinition.Atmospheres
{
    // The data form of the own cloud system's layers for one body (package T10): what the LAYER sub-nodes of CLOUDS say, before any map
    // or renderer exists. A spec is plain data; Build turns it into the CloudLayerDef the layer stack and the pass read. The values in the
    // shipped files come from real cloud base and top ranges and extinction coefficients (the header of each file names
    // them). Example:
    //
    //   CLOUDS
    //   {
    //       LAYER
    //       {
    //           name = Kerbin-cirrus
    //           innerAltitudeM = 9000
    //           outerAltitudeM = 11000
    //           sigmaPerM = 0.0002
    //           kind = Cheap
    //           coverage = 0.3
    //           TYPE
    //           {
    //               name = Cirrus
    //               baseShare = 0
    //               topShare = 1
    //               erosion = 0.6
    //               PROFILE
    //               {
    //                   key0 = 0 0
    //                   key1 = 0.4 0.8
    //                   key2 = 0.6 0.8
    //                   key3 = 1 0
    //               }
    //           }
    //       }
    //   }
    //
    // A layer with a group is one of several that never show at once (a weather deck of rain and one of snow): the pass draws one member of
    // each group, chosen by the weather (CloudLayerSet.Select). Layers without a group are all candidates.

    // One cloud type of a layer: heights as shares of the layer's thickness, a vertical density profile over the type's own height, and the
    // erosion the detail noise gets.
    internal sealed class CloudTypeSpec
    {
        // The soft water content's per-type numbers (E40 soft density; cfg keys baseGain, riseShare and densityScale): the gain of the density at the type's base, the share of
        // its height over which it rises to 1, and a factor on the type's density (1: none). Baked into the type's tabulated profile.
        internal float BaseGain { get; set; } = 0.3f;
        internal float RiseShare { get; set; } = 0.5f;
        internal float DensityScale { get; set; } = 1f;

        internal CloudTypeSpec(string name, float baseShare, float topShare, float erosion, Curve profile)
        {
            Name = name ?? "";
            BaseShare = baseShare;
            TopShare = topShare;
            Erosion = erosion;
            Profile = profile;
        }

        internal string Name { get; }
        internal float BaseShare { get; }
        internal float TopShare { get; }
        internal float Erosion { get; }
        internal Curve Profile { get; }

        internal CloudTypeDef Build()
        {
            return new CloudTypeDef(Name, BaseShare, TopShare, Profile, Erosion) { BaseGain = BaseGain, RiseShare = RiseShare, DensityScale = DensityScale };
        }
    }

    // One layer of a body's set.
    internal sealed class CloudLayerSpec
    {
        internal CloudLayerSpec(string name, float innerAltitudeM, float outerAltitudeM, float sigmaPerM, LayerKind kind, string group, float coverage,
            float edgeSoftness, float fadeNearM, float fadeFarM, float shadowStrength, float noiseAmount, float noiseScaleM, IEnumerable<CloudTypeSpec> types)
        {
            Name = name ?? "";
            InnerAltitudeM = innerAltitudeM;
            OuterAltitudeM = outerAltitudeM;
            SigmaPerM = sigmaPerM;
            Kind = kind;
            Group = group ?? "";
            Coverage = coverage;
            EdgeSoftness = edgeSoftness;
            FadeNearM = fadeNearM;
            FadeFarM = fadeFarM;
            ShadowStrength = shadowStrength;
            NoiseAmount = noiseAmount;
            NoiseScaleM = noiseScaleM;
            Types = SchemaList.Of(types);
        }

        internal string Name { get; }
        internal float InnerAltitudeM { get; }
        internal float OuterAltitudeM { get; }
        // The extinction at full density, per m.
        internal float SigmaPerM { get; }
        internal LayerKind Kind { get; }
        // The name of the group of layers that never show together, or empty.
        internal string Group { get; }
        internal float Coverage { get; }
        internal float EdgeSoftness { get; }
        internal float FadeNearM { get; }
        internal float FadeFarM { get; }
        internal float ShadowStrength { get; }
        internal float NoiseAmount { get; }
        internal float NoiseScaleM { get; }
        // The type value of the layer where no precipitation falls (cfg key fairType, 0 to 1 over the type table; -1: the precipitation level itself, so the first type).
        // With it the type value is fairType + (1 - fairType) * precipitation (CloudLookTuning.TypeValue).
        internal float FairType { get; set; } = -1f;
        // The soft water content's per-layer numbers (E40 soft density; cfg keys softnessScale and waterPower): the factor on the owner's softness (1: the setting itself) and the
        // exponent of the water content in the density. The starting values are [UNKNOWN]: they are judged on pictures.
        internal float SoftnessScale { get; set; } = 1f;
        internal float WaterPower { get; set; } = 2f;
        // What may draw the layer (cfg key deckRule = 1 and the fog keys, WeatherDeckRule); null: the layer is drawn wherever its coverage map passes, with the type from the precipitation.
        internal WeatherDeckRule Deck { get; set; }
        internal IReadOnlyList<CloudTypeSpec> Types { get; }

        // The vertical optical depth through the layer's thickest type at full density and full coverage: sigma times the height of the type.
        internal float PeakOpticalDepth
        {
            get
            {
                float best = 0f;
                float thickness = OuterAltitudeM - InnerAltitudeM;
                for (int i = 0; i < Types.Count; i++)
                {
                    float peak = 0f;
                    for (int k = 0; k < Types[i].Profile.Count; k++) peak = Math.Max(peak, Types[i].Profile.ValueAt(k));
                    best = Math.Max(best, SigmaPerM * (Types[i].TopShare - Types[i].BaseShare) * thickness * peak);
                }
                return best;
            }
        }

        // The layer the pass reads, for the body with that index. The maps are the caller's (CoverageMapHandle and TypeMapHandle stay -1).
        internal CloudLayerDef Build(int bodyIndex)
        {
            var types = new List<CloudTypeDef>();
            for (int i = 0; i < Types.Count; i++) types.Add(Types[i].Build());
            return new CloudLayerDef(Name, bodyIndex, InnerAltitudeM, OuterAltitudeM, types, SigmaPerM, Kind)
            {
                Coverage = Coverage,
                Group = Group,
                EdgeSoftness = EdgeSoftness,
                FadeNearM = FadeNearM,
                FadeFarM = FadeFarM,
                ShadowStrength = ShadowStrength,
                NoiseAmount = NoiseAmount,
                NoiseScaleM = NoiseScaleM,
                FairType = FairType,
                Deck = Deck,
                SoftnessScale = SoftnessScale,
                WaterPower = WaterPower
            };
        }
    }

    // Chooses the layers a pass draws out of a body's set: one member of each group, then at most `cap` layers, the lowest first (the high
    // layers are the first to go: the sky below stays complete). Pure.
    internal static class CloudLayerSet
    {
        // pick: the member of a group to draw, by group name (the weather decides; null or an index past the members counts from the start).
        internal static List<CloudLayerSpec> Select(IReadOnlyList<CloudLayerSpec> specs, int cap, Func<string, int> pick = null)
        {
            var chosen = new List<CloudLayerSpec>();
            var seenGroups = new HashSet<string>(StringComparer.Ordinal);
            if (specs != null)
            {
                foreach (CloudLayerSpec spec in specs)
                {
                    if (spec.Group.Length == 0)
                    {
                        chosen.Add(spec);
                        continue;
                    }
                    if (!seenGroups.Add(spec.Group)) continue;
                    var members = new List<CloudLayerSpec>();
                    foreach (CloudLayerSpec other in specs)
                    {
                        if (string.Equals(other.Group, spec.Group, StringComparison.Ordinal)) members.Add(other);
                    }
                    int index = pick == null ? 0 : pick(spec.Group);
                    chosen.Add(members[index < 0 || index >= members.Count ? 0 : index]);
                }
            }
            // Lowest inner altitude first; the same inner altitude by the lower outer altitude, then in the order of the file (OrderBy is stable).
            chosen = chosen.OrderBy(s => s.InnerAltitudeM).ThenBy(s => s.OuterAltitudeM).ToList();
            int limit = Math.Max(0, Math.Min(cap, CloudLayerDef.MaxLayers));
            if (chosen.Count > limit) chosen.RemoveRange(limit, chosen.Count - limit);
            return chosen;
        }

        // The most layers of a set that can show at once before the cap: the layers without a group plus one for each group.
        internal static int MostAtOnce(IReadOnlyList<CloudLayerSpec> specs)
        {
            var groups = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            if (specs == null) return 0;
            foreach (CloudLayerSpec spec in specs)
            {
                if (spec.Group.Length == 0 || groups.Add(spec.Group)) count++;
            }
            return count;
        }
    }
}
