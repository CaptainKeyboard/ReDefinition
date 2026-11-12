using System;
using System.IO;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The optional authored maps of the shape model (package T02): the flow map (P16) and the colour map (P15). Both are equirectangular
    // like the coverage maps (row 0 at the north pole, column 0 at longitude -180, wrap on the longitude, clamp on the latitude) and are read
    // by the layer shader at the same place as the coverage map. The loaders take a PNG; the builders write the bytes of a map from a
    // function of latitude, for the bodies whose flow or colour is zonal (the bands of a gas giant, the trade winds of a dust storm).
    //
    // The flow map holds the direction the noise moves in: red is the east share, green the north share (0.5 = still, 0 and 1 = the full
    // amplitude of the layer's flow). The colour map holds the tint the layer's cloud is multiplied by (white = none).
    internal static class NoiseMaps
    {
        // Loads a PNG as a map texture; null where the file is missing or does not decode.
        internal static Texture2D LoadPng(string path, string name)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), true))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            Setup(texture, name);
            return texture;
        }

        // A map texture from RGBA bytes (row 0 = north).
        internal static Texture2D FromBytes(int width, int height, byte[] rgba, string name)
        {
            if (rgba == null || rgba.Length != width * height * 4) throw new ArgumentException("rgba must hold width * height * 4 bytes.");
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            texture.SetPixelData(rgba, 0);
            texture.Apply(false);
            Setup(texture, name);
            return texture;
        }

        private static void Setup(Texture2D texture, string name)
        {
            texture.name = name;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapModeU = TextureWrapMode.Repeat;
            texture.wrapModeV = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave;
        }

        // The encoding of a flow vector component (-1 to 1) in a byte.
        internal static byte EncodeFlow(double v)
        {
            return (byte)Math.Min(255.0, Math.Max(0.0, Math.Round((v * 0.5 + 0.5) * 255.0)));
        }

        // The component a byte stands for.
        internal static double DecodeFlow(byte b)
        {
            return b / 255.0 * 2.0 - 1.0;
        }

        // The latitude in degrees of a row's centre of a map of `height` rows.
        internal static double RowLatitude(int row, int height)
        {
            return 90.0 - (row + 0.5) * 180.0 / height;
        }

        // A zonal flow map: the east and north share as functions of the latitude in degrees (-1 to 1 each).
        internal static byte[] ZonalFlow(int width, int height, Func<double, double> east, Func<double, double> north)
        {
            var bytes = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                double lat = RowLatitude(y, height);
                byte e = EncodeFlow(east(lat)), n = EncodeFlow(north(lat));
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    bytes[i] = e;
                    bytes[i + 1] = n;
                    bytes[i + 2] = 128;
                    bytes[i + 3] = 255;
                }
            }
            return bytes;
        }

        // A colour map of latitude bands: `colours` are evenly spaced from the north pole to the south pole and blended between.
        internal static byte[] BandColours(int width, int height, Color32[] colours)
        {
            if (colours == null || colours.Length == 0) throw new ArgumentException("at least one colour");
            var bytes = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                double x = (y + 0.5) / height * (colours.Length - 1);
                int i0 = Math.Min((int)x, colours.Length - 1), i1 = Math.Min(i0 + 1, colours.Length - 1);
                double f = x - i0;
                for (int c = 0; c < width; c++)
                {
                    int i = (y * width + c) * 4;
                    bytes[i] = (byte)Math.Round(colours[i0].r * (1 - f) + colours[i1].r * f);
                    bytes[i + 1] = (byte)Math.Round(colours[i0].g * (1 - f) + colours[i1].g * f);
                    bytes[i + 2] = (byte)Math.Round(colours[i0].b * (1 - f) + colours[i1].b * f);
                    bytes[i + 3] = 255;
                }
            }
            return bytes;
        }
    }
}
