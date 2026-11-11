using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What a cloud type of an EVE layer is for, by the role lists of the body's CLOUDS node (CloudRules).
    internal enum CloudRole
    {
        // A type that plays no role of ours (EVE's own weather decks).
        None = 0,
        // The thin snow stratus of flurries and light snow.
        SnowLight = 1,
        // The grey deck of moderate snow.
        SnowStratus = 2,
        // The storm type with the low dark base: heavy snow and blizzard.
        Storm = 3,
        // The type of a dust deck (Duna).
        Dust = 4
    }

    // The cloud types of one layer as the type map sees them: how many there are and which index plays which role. EVE reads the
    // type map's value t (0 to 1) as the index t x (Count - 1), blends the two neighbouring types by the fraction and takes the base
    // and top of the deck from the blend when interpolateCloudHeights is on (CloudsRaymarchedVolume.BakeCurvesTexture). A value is
    // therefore the index divided by Count - 1, and two roles that are not neighbours in the layer's list blend through the types
    // between them: the layer template (E41) lists SnowLight, SnowStratus and the storm type next to each other, in that order.
    internal sealed class TypeSlots
    {
        private readonly int[] indexOfRole = { -1, -1, -1, -1, -1 };

        internal TypeSlots(int count)
        {
            Count = Math.Max(count, 0);
        }

        // The number of cloud types of the layer.
        internal int Count { get; }

        // The index of the type that plays the role, or -1.
        internal int IndexOf(CloudRole role)
        {
            return indexOfRole[(int)role];
        }

        // Gives the role to the type at that index (the first type with a role keeps it).
        internal void Assign(CloudRole role, int index)
        {
            if (role == CloudRole.None || index < 0 || index >= Count) return;
            if (indexOfRole[(int)role] < 0) indexOfRole[(int)role] = index;
        }

        internal bool Has(CloudRole role)
        {
            return IndexOf(role) >= 0;
        }

        // The type map value of the type at an index.
        internal float ValueOfIndex(int index)
        {
            return Count <= 1 ? 0f : Math.Min(Math.Max(index, 0), Count - 1) / (float)(Count - 1);
        }

    }

    // The level of the precipitation to the value of the type map, pure (CONTRACT.md, "Cloud types per level"):
    //   Flurries, Light  SnowLight   the thin stratus, 600 to 1500 m
    //   Moderate         SnowStratus 400 to 1800 m
    //   Heavy, Blizzard  Storm       300 to 2300 m, the low dark base
    // The value moves continuously with the level between the anchors, so the deck thickens without a step: SnowLight up to
    // LightAnchor, SnowStratus at ModerateAnchor, Storm from HeavyAnchor on. A role the layer lacks is passed over to the nearest one
    // it has; a layer with none of the three has value 0 everywhere. A dust body (Duna) uses the Dust type at every level.
    internal static class CloudTypeEncoding
    {
        // The levels at which a type is fully in force: inside the Light band, in the middle of Moderate, early in Heavy.
        internal const float LightAnchor = 0.20f, ModerateAnchor = 0.48f, HeavyAnchor = 0.66f;

        // The number of entries of the lookup the painter pass reads.
        internal const int LutSize = 256;

        // The role that dominates at a level (the anchor nearest below the blend's middle).
        internal static CloudRole RoleAt(float level01, bool dust)
        {
            if (dust) return CloudRole.Dust;
            if (level01 < ModerateAnchor - (ModerateAnchor - LightAnchor) * 0.5f) return CloudRole.SnowLight;
            if (level01 < HeavyAnchor - (HeavyAnchor - ModerateAnchor) * 0.5f) return CloudRole.SnowStratus;
            return CloudRole.Storm;
        }

        // The role of each band: the band's representative level (the middle of the band, the top for Blizzard) mapped through RoleAt.
        internal static CloudRole RoleOfBand(PrecipBand band, bool dust)
        {
            return RoleAt(RepresentativeLevel(band), dust);
        }

        // A level inside a band that stands for it.
        internal static float RepresentativeLevel(PrecipBand band)
        {
            switch (band)
            {
                case PrecipBand.None: return 0f;
                case PrecipBand.Flurries: return 0.5f * (PrecipScale.FlurriesFrom + PrecipScale.LightFrom);
                case PrecipBand.Light: return 0.5f * (PrecipScale.LightFrom + PrecipScale.ModerateFrom);
                case PrecipBand.Moderate: return 0.5f * (PrecipScale.ModerateFrom + PrecipScale.HeavyFrom);
                case PrecipBand.Heavy: return 0.5f * (PrecipScale.HeavyFrom + PrecipScale.BlizzardFrom);
                default: return 0.9f;
            }
        }

        // The type map value at a level: piecewise linear in the level through the values of the roles the layer has.
        internal static float Value(TypeSlots slots, float level01, bool dust)
        {
            if (slots == null || slots.Count == 0) return 0f;
            if (dust)
            {
                return slots.Has(CloudRole.Dust) ? slots.ValueOfIndex(slots.IndexOf(CloudRole.Dust)) : 0f;
            }
            float level = level01 < 0f ? 0f : level01 > 1f ? 1f : level01;
            // The anchors the layer has, in the order of the levels.
            float a0 = 0f, a1 = 0f, a2 = 0f;
            bool h0 = Anchor(slots, CloudRole.SnowLight, out a0);
            bool h1 = Anchor(slots, CloudRole.SnowStratus, out a1);
            bool h2 = Anchor(slots, CloudRole.Storm, out a2);
            if (!h0 && !h1 && !h2) return 0f;
            // A missing anchor takes the value of the nearest one that exists.
            if (!h0) a0 = h1 ? a1 : a2;
            if (!h2) a2 = h1 ? a1 : a0;
            if (!h1) a1 = 0.5f * (a0 + a2);
            if (level <= LightAnchor) return a0;
            if (level < ModerateAnchor) return Mix(a0, a1, (level - LightAnchor) / (ModerateAnchor - LightAnchor));
            if (level < HeavyAnchor) return Mix(a1, a2, (level - ModerateAnchor) / (HeavyAnchor - ModerateAnchor));
            return a2;
        }

        private static bool Anchor(TypeSlots slots, CloudRole role, out float value)
        {
            int index = slots.IndexOf(role);
            value = index >= 0 ? slots.ValueOfIndex(index) : 0f;
            return index >= 0;
        }

        // Smoothstep blend: no kink where the level crosses an anchor.
        private static float Mix(float a, float b, float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            t = t * t * (3f - 2f * t);
            return a + (b - a) * t;
        }

        // The lookup of the painter pass: entry i is the value (0 to 255) for the level i / (LutSize - 1).
        internal static byte[] BuildLut(TypeSlots slots, bool dust)
        {
            var lut = new byte[LutSize];
            for (int i = 0; i < LutSize; i++)
            {
                float v = Value(slots, i / (float)(LutSize - 1), dust);
                lut[i] = (byte)Math.Min(255, Math.Max(0, (int)Math.Round(v * 255f)));
            }
            return lut;
        }

        // The thickness of the deck at a type map value, in m: the base and top blend like the types do when interpolateCloudHeights is
        // on (CloudsRaymarchedVolume.cs:908-909), so the level thickens the deck through the type value alone.
        internal static float DeckThicknessM(float[] minAltitudeM, float[] maxAltitudeM, float typeValue)
        {
            int n = minAltitudeM == null || maxAltitudeM == null ? 0 : Math.Min(minAltitudeM.Length, maxAltitudeM.Length);
            if (n == 0) return 0f;
            if (n == 1) return maxAltitudeM[0] - minAltitudeM[0];
            float index = Math.Min(Math.Max(typeValue, 0f), 1f) * (n - 1);
            int current = (int)index;
            int next = Math.Min(current + 1, n - 1);
            float frac = index - current;
            float min = minAltitudeM[current] + (minAltitudeM[next] - minAltitudeM[current]) * frac;
            float max = maxAltitudeM[current] + (maxAltitudeM[next] - maxAltitudeM[current]) * frac;
            return max - min;
        }
    }
}
