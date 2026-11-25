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
    }
}
