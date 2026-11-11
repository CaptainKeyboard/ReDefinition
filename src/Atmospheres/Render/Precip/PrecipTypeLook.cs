using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Which procedural picture a type is drawn with (a cell of the sheet PrecipSheets paints).
    internal enum PrecipSheetKind
    {
        // A soft round flake with a faint lacy edge.
        Flake = 0,
        // A thin streak, bright in the middle, for rain and sleet.
        Streak = 1,
        // A small hard grain, for dust, sand and ash.
        Grain = 2,
        // A round droplet with a short tail, for methane.
        Droplet = 3
    }

    // How one kind of precipitation looks, apart from how much of it falls: colour, size, speed, stretch, splash and wind coupling.
    // The numbers are tuning starts, read through Tuning.Get("precip.<type>.<field>") by PrecipLookParams, so the series changes them
    // without a build.
    internal struct PrecipTypeLook
    {
        // Linear colour at full light.
        internal float R, G, B;
        // The sheet cell.
        internal PrecipSheetKind Sheet;
        // The factor on the layers' sizes.
        internal float SizeScale;
        // The speed the particles fall at, in m/s; 0 takes the speed of the view (the level's fall factor, for snow).
        internal float FallMs;
        // The streak length as a multiple of the size per m/s of speed (0 = round).
        internal float Stretch;
        // The share of drops that splash on the ground (0 = none).
        internal float Splash;
        // How much of the wind's horizontal speed the particle takes (1 = all). Grit is carried along the wind, a falling drop less.
        internal float WindCoupling;
        // The factor on the opacity.
        internal float Opacity;
        // The share of the horizontal motion that is a swirl across the wind (dust and sand drift sideways in puffs).
        internal float Tangential;
        // The sway of the particle across its fall, in metres (a flake wanders, a drop does not).
        internal float Sway;
    }

    // The table of looks per type, complete for every PrecipType but None.
    internal static class PrecipTypeTable
    {
        private static readonly PrecipTypeLook[] Table = Build();

        // The look of a type; None and a value outside the enum give the snow look (the renderer draws nothing for None).
        internal static PrecipTypeLook Get(PrecipType type)
        {
            int i = (int)type;
            return i > 0 && i < Table.Length ? Table[i] : Table[(int)PrecipType.Snow];
        }

        // The types that have a row.
        internal static PrecipType[] Types
        {
            get
            {
                var all = (PrecipType[])Enum.GetValues(typeof(PrecipType));
                var list = new System.Collections.Generic.List<PrecipType>();
                foreach (PrecipType t in all) if (t != PrecipType.None) list.Add(t);
                return list.ToArray();
            }
        }

        // The grey a type's colour is judged against by the luma-sign sanity metric.
        internal const float MidGrey = 0.45f;

        // The sign of the shift in luminance the type makes on a mid-grey ground: + for bright particles, - for dark ones.
        internal static int LumaSign(PrecipType type)
        {
            PrecipTypeLook look = Get(type);
            float luma = 0.2126f * look.R + 0.7152f * look.G + 0.0722f * look.B;
            return luma >= MidGrey ? 1 : -1;
        }

        private static PrecipTypeLook[] Build()
        {
            var t = new PrecipTypeLook[(int)PrecipType.Methane + 1];
            t[(int)PrecipType.Snow] = new PrecipTypeLook
            {
                R = 0.95f, G = 0.97f, B = 1.00f, Sheet = PrecipSheetKind.Flake, SizeScale = 1f, FallMs = 0f, Stretch = 0.02f, Splash = 0f,
                WindCoupling = 1f, Opacity = 1f, Tangential = 0f, Sway = 0.35f
            };
            t[(int)PrecipType.Rain] = new PrecipTypeLook
            {
                R = 0.78f, G = 0.82f, B = 0.88f, Sheet = PrecipSheetKind.Streak, SizeScale = 0.45f, FallMs = 8.5f, Stretch = 0.9f, Splash = 0.6f,
                WindCoupling = 0.55f, Opacity = 0.55f, Tangential = 0f, Sway = 0f
            };
            t[(int)PrecipType.Sleet] = new PrecipTypeLook
            {
                R = 0.86f, G = 0.90f, B = 0.95f, Sheet = PrecipSheetKind.Streak, SizeScale = 0.6f, FallMs = 3.8f, Stretch = 0.35f, Splash = 0.25f,
                WindCoupling = 0.8f, Opacity = 0.75f, Tangential = 0f, Sway = 0.1f
            };
            t[(int)PrecipType.FreezingRain] = new PrecipTypeLook
            {
                R = 0.80f, G = 0.86f, B = 0.94f, Sheet = PrecipSheetKind.Streak, SizeScale = 0.5f, FallMs = 7.0f, Stretch = 0.7f, Splash = 0.1f,
                WindCoupling = 0.6f, Opacity = 0.6f, Tangential = 0f, Sway = 0f
            };
            t[(int)PrecipType.Dust] = new PrecipTypeLook
            {
                R = 0.64f, G = 0.55f, B = 0.45f, Sheet = PrecipSheetKind.Grain, SizeScale = 0.8f, FallMs = 0.35f, Stretch = 0.08f, Splash = 0f,
                WindCoupling = 1f, Opacity = 0.7f, Tangential = 0.5f, Sway = 0.2f
            };
            t[(int)PrecipType.Sand] = new PrecipTypeLook
            {
                R = 0.78f, G = 0.64f, B = 0.45f, Sheet = PrecipSheetKind.Grain, SizeScale = 0.7f, FallMs = 0.9f, Stretch = 0.12f, Splash = 0f,
                WindCoupling = 1f, Opacity = 0.85f, Tangential = 0.35f, Sway = 0.1f
            };
            t[(int)PrecipType.Ash] = new PrecipTypeLook
            {
                R = 0.24f, G = 0.23f, B = 0.23f, Sheet = PrecipSheetKind.Flake, SizeScale = 1.1f, FallMs = 0.7f, Stretch = 0.03f, Splash = 0f,
                WindCoupling = 1f, Opacity = 0.9f, Tangential = 0.1f, Sway = 0.3f
            };
            t[(int)PrecipType.Methane] = new PrecipTypeLook
            {
                R = 0.78f, G = 0.58f, B = 0.34f, Sheet = PrecipSheetKind.Droplet, SizeScale = 0.9f, FallMs = 2.6f, Stretch = 0.25f, Splash = 0.3f,
                WindCoupling = 0.7f, Opacity = 0.65f, Tangential = 0f, Sway = 0.05f
            };
            return t;
        }
    }
}
