using System;
using System.Collections.Generic;
using System.Text;

namespace ReDefinition.Raytracing
{
    // The parity of the rays' scene with the picture Unity drew, by the primary check
    // (RT_SIGNAL_PRIMARY_CHECK in Raytracing.hlsl). Per pixel it holds the distance of the surface the
    // camera's ray met, the distance the depth buffer gives, and the instance met plus one.
    // It finds where the rays see geometry the raster does not draw, and where they miss geometry it does.
    // It stays free of Unity, so the tests can run it.
    internal static class RtParity
    {
        // What a pixel shows. Match: both give the same surface. InFront: the rays' surface lies before the
        // raster's. Behind: it lies behind the raster's. Missing: the rays met nothing where the raster
        // drew a surface. OverSky: the rays met a surface where the raster drew sky. Sky: neither.
        internal enum Kind { Match, InFront, Behind, Missing, OverSky, Sky }

        // The kinds, as many.
        internal const int Kinds = 6;

        // A frame's counts, by kind, and by the instance the rays met for the three kinds that name one.
        internal sealed class Result
        {
            public int Width;
            public int Height;
            public readonly int[] Count = new int[Kinds];
            public readonly Dictionary<int, int[]> PerInstance = new Dictionary<int, int[]>();
            // By the same instances: the sum of how far the rays' surface lies behind the picture's (before it,
            // negative) in metres, and the greatest distance apart, over the pixels that disagree.
            public readonly Dictionary<int, float[]> Apart = new Dictionary<int, float[]>();
        }

        // How far apart the two distances may be: 5 cm and half a per cent, and for the ground, which
        // Parallax displaces off its mesh, 2 m and 3 per cent as the shader allows it (RtCommon.hlsli, CandidateCounts).
        internal static float Tolerance(float expected, bool terrain)
        {
            return terrain ? expected * 0.03f + 2.0f : expected * 0.005f + 0.05f;
        }

        // The kind of a pixel from its three values; `terrain` is whether the instance met is the ground.
        internal static Kind Classify(float hit, float expected, bool terrain)
        {
            bool surface = expected > 0f;
            if (!surface) return hit > 0f ? Kind.OverSky : Kind.Sky;
            if (hit <= 0f) return Kind.Missing;
            float tolerance = Tolerance(expected, terrain);
            if (hit < expected - tolerance) return Kind.InFront;
            if (hit > expected + tolerance) return Kind.Behind;
            return Kind.Match;
        }

        // The counts of a frame. `data` holds four floats a pixel, the output of the primary check.
        // They are the distance met (0 for none), the distance the depth gives (0 for sky), the instance plus
        // one, and 1. `isTerrain` says whether an instance (the id the rays gave, from 0) is the ground.
        internal static Result Analyse(float[] data, int width, int height, Func<int, bool> isTerrain)
        {
            var result = new Result { Width = width, Height = height };
            int pixels = width * height;
            for (int i = 0; i < pixels; i++)
            {
                float hit = data[i * 4], expected = data[i * 4 + 1];
                int instance = (int)data[i * 4 + 2] - 1;
                Kind kind = Classify(hit, expected, instance >= 0 && isTerrain(instance));
                result.Count[(int)kind]++;
                if (instance < 0 || kind == Kind.Match || kind == Kind.Sky || kind == Kind.Missing) continue;
                int[] per;
                float[] apart;
                if (!result.PerInstance.TryGetValue(instance, out per))
                {
                    result.PerInstance[instance] = per = new int[Kinds];
                    result.Apart[instance] = new float[2];
                }
                apart = result.Apart[instance];
                per[(int)kind]++;
                if (kind != Kind.OverSky)
                {
                    apart[0] += hit - expected;
                    apart[1] = Math.Max(apart[1], Math.Abs(hit - expected));
                }
            }
            return result;
        }

        // How the surfaces look to the rays against the G-buffer, per instance. Over the pixels where both give
        // the same surface it holds the count and how many have the rays' albedo a third off in luminance.
        // It holds the sums of the two luminances and of the two smoothnesses too.
        internal sealed class LookResult
        {
            public readonly Dictionary<int, double[]> PerInstance = new Dictionary<int, double[]>();
        }

        // The luminance of a linear colour.
        private static double Luminance(float r, float g, float b)
        {
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        // `check` is the primary check's output, `rays` the rays' albedo with the smoothness their material
        // means in its fourth float, and `raster` the G-buffer's albedo in the rays' colour space with its smoothness.
        // Each holds four floats a pixel.
        internal static LookResult Looks(float[] check, float[] rays, float[] raster, int width, int height)
        {
            var result = new LookResult();
            for (int i = 0; i < width * height; i++)
            {
                float hit = check[i * 4], expected = check[i * 4 + 1];
                int instance = (int)check[i * 4 + 2] - 1;
                if (instance < 0 || expected <= 0f || Classify(hit, expected, false) != Kind.Match) continue;
                double lr = Luminance(rays[i * 4], rays[i * 4 + 1], rays[i * 4 + 2]);
                double lg = Luminance(raster[i * 4], raster[i * 4 + 1], raster[i * 4 + 2]);
                double[] per;
                if (!result.PerInstance.TryGetValue(instance, out per))
                    result.PerInstance[instance] = per = new double[6];
                per[0]++;
                if (lr > lg * 4.0 / 3.0 || lr < lg * 0.75) per[1]++;
                per[2] += lr;
                per[3] += lg;
                per[4] += rays[i * 4 + 3];
                per[5] += raster[i * 4 + 3];
            }
            return result;
        }

        // The `top` instances whose albedo the rays take most pixels wrong, with the mean luminances and smoothnesses.
        internal static string LookReport(LookResult result, Func<int, string> label, int top)
        {
            var worst = new List<KeyValuePair<int, double[]>>(result.PerInstance);
            worst.Sort((a, b) => b.Value[1].CompareTo(a.Value[1]));
            var text = new StringBuilder("Raytracing parity of the surfaces' look (rays against the G-buffer):");
            for (int i = 0; i < worst.Count && i < top; i++)
            {
                double[] per = worst[i].Value;
                text.Append("\n  ").Append(label(worst[i].Key)).Append(": ").Append(per[0]).Append(" pixels, albedo off by a third or more in ")
                    .Append(per[1]).Append(", luminance ").Append((per[2] / per[0]).ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" against ").Append((per[3] / per[0]).ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(", smoothness ").Append((per[4] / per[0]).ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" against ").Append((per[5] / per[0]).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        // The picture of a frame as RGBA bytes, rows from the bottom as the texture holds them.
        // Green is a match, red where the rays are in front, blue behind, magenta where they miss,
        // and yellow over sky. The sky is dark.
        internal static byte[] Image(float[] data, int width, int height, Func<int, bool> isTerrain)
        {
            var image = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                float hit = data[i * 4], expected = data[i * 4 + 1];
                int instance = (int)data[i * 4 + 2] - 1;
                byte r = 0, g = 0, b = 0;
                switch (Classify(hit, expected, instance >= 0 && isTerrain(instance)))
                {
                    case Kind.Match: g = 120; break;
                    case Kind.InFront: r = 255; break;
                    case Kind.Behind: b = 255; break;
                    case Kind.Missing: r = 255; b = 255; break;
                    case Kind.OverSky: r = 255; g = 220; break;
                    default: r = g = b = 20; break;
                }
                image[i * 4] = r;
                image[i * 4 + 1] = g;
                image[i * 4 + 2] = b;
                image[i * 4 + 3] = 255;
            }
            return image;
        }

        // The report of a frame: the shares of the kinds among the pixels, then the `top` instances that make
        // most of the pixels that disagree, named by `label`.
        internal static string Report(Result result, Func<int, string> label, int top)
        {
            int pixels = Math.Max(1, result.Width * result.Height);
            var text = new StringBuilder();
            text.Append("Raytracing parity ").Append(result.Width).Append('x').Append(result.Height).Append(": match ")
                .Append(Share(result.Count[(int)Kind.Match], pixels)).Append(", sky ").Append(Share(result.Count[(int)Kind.Sky], pixels))
                .Append(", rays in front ").Append(Share(result.Count[(int)Kind.InFront], pixels))
                .Append(", rays behind ").Append(Share(result.Count[(int)Kind.Behind], pixels))
                .Append(", rays miss ").Append(Share(result.Count[(int)Kind.Missing], pixels))
                .Append(", rays over sky ").Append(Share(result.Count[(int)Kind.OverSky], pixels)).Append('.');
            var worst = new List<KeyValuePair<int, int[]>>(result.PerInstance);
            worst.Sort((a, b) => Disagreeing(b.Value).CompareTo(Disagreeing(a.Value)));
            for (int i = 0; i < worst.Count && i < top; i++)
            {
                int[] per = worst[i].Value;
                float[] apart = result.Apart[worst[i].Key];
                int counted = Math.Max(1, per[(int)Kind.InFront] + per[(int)Kind.Behind]);
                text.Append("\n  ").Append(label(worst[i].Key)).Append(": in front ").Append(per[(int)Kind.InFront])
                    .Append(", behind ").Append(per[(int)Kind.Behind]).Append(", over sky ").Append(per[(int)Kind.OverSky])
                    .Append(" pixels, behind by ").Append((apart[0] / counted).ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" m on average, ").Append(apart[1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(" m at most");
            }
            return text.ToString();
        }

        // The pixels of an instance that disagree with the picture.
        private static int Disagreeing(int[] per)
        {
            return per[(int)Kind.InFront] + per[(int)Kind.Behind] + per[(int)Kind.OverSky];
        }

        // A count as a share of the pixels, in per cent with one decimal.
        private static string Share(int count, int pixels)
        {
            return (count * 100.0 / pixels).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " %";
        }
    }
}
