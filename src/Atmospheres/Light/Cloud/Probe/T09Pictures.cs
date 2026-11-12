using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // The pictures of series K-T2: per view and variant one natural picture (what the player sees) and, for the own pass, one aid picture, so that
    // the owner sees the look, the cloud against the terrain, the noise, and TVC and the own pass side by side. The series collects the folder
    // (tests/series/K-T2.json: "collect") into .local/work/series-pictures/K-T2.
    // The aid picture is the layer shader's own aid 1 (optical depth as a heat map over the scene, LayerFrame.AidMode). The own pass has no
    // contribution mask: atmoDebug contrib is read by the shaders of the bolt, droplet, frost, lamp and wet effects only, not by the layer shader.
    // TVC has no aid view (EVE's shader has no debug mode we own); off and TVC give the natural picture only.
    // Names: t09-<start>-<view>-<variant>-<WxH>-natural.png and t09-<start>-<view>-<variant>-<WxH>-aid-opticaldepth.png, with "-half" before ".png"
    // for the copy scaled down by two. Full size is stored once for the key views (ground, inside, above) of off, plain and tvc; everything else
    // is a half-size copy, so that a start stays at a few tens of megabytes.
    internal static class T09Pictures
    {
        internal const string Natural = "natural";
        internal const string AidKind = "aid-opticaldepth";

        // Real seconds the read of one picture may take.
        internal const double ReadWaitS = 10.0;
        // Consecutive faults after which the start stops taking pictures (the measurement goes on).
        internal const int FaultLimit = 4;

        internal static string Name(string start, string view, string variant, int width, int height, string kind, bool half)
        {
            return string.Format(CultureInfo.InvariantCulture, "t09-{0}-{1}-{2}-{3}x{4}-{5}{6}", Clean(start ?? "nostart"), Clean(view), Clean(variant), width, height, kind, half ? "-half" : "");
        }

        // Only the key views of the baseline, the plain pass and TVC are kept at full size.
        internal static bool FullSize(string view, string variant)
        {
            bool keyVariant = variant == "off" || variant == "plain" || variant == "tvc";
            bool keyView = view == "ground" || view == "inside" || view == "above";
            return keyVariant && keyView;
        }

        // Off and TVC have no aid picture of the own pass.
        internal static bool HasAid(string variant)
        {
            return variant != "off" && variant != "tvc";
        }

        // Frames the aid view is shown before the read: the reconstruction needs its history (4 x 4 blocks: 16 frames) to fill the picture.
        internal static int SettleFrames(string variant)
        {
            return variant == "recon4" || variant == "edge" ? 16 : 6;
        }

        private static string Clean(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_') chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
