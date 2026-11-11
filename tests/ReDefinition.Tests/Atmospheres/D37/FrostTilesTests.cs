using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.D37
{
    // The tile lists the shader uses to evaluate only the flakes and drops that touch a pixel's tile.
    [TestClass]
    public class FrostTilesTests
    {
        private const float Aspect = 16f / 9f;

        private static Vector4[] Tiles()
        {
            return new Vector4[FrostTiles.Count];
        }

        private static Vector4 At(Vector4[] tiles, float u, float v)
        {
            int i = Math.Min(FrostTiles.GridX - 1, (int)(u * FrostTiles.GridX)), j = Math.Min(FrostTiles.GridY - 1, (int)(v * FrostTiles.GridY));
            return tiles[j * FrostTiles.GridX + i];
        }

        [TestMethod]
        public void AFlakeMarksTheTilesItTouchesAndNoOthers()
        {
            var tiles = Tiles();
            var flakes = new[] { new Vector4(0.5f, 0.5f, 0.004f, 3.1f) };
            FrostTiles.Build(tiles, flakes, 1, new Vector4[0], 0, new Vector2(0, -1), Aspect);
            int marked = 0;
            foreach (Vector4 t in tiles) if (t.x != 0f || t.y != 0f || t.z != 0f) marked++;
            Assert.IsTrue(marked >= 1 && marked <= 4, "marked " + marked);
            Assert.AreEqual(1f, At(tiles, 0.5f, 0.5f).x);
            Assert.AreEqual(0f, At(tiles, 0.5f, 0.5f).y);
            Assert.AreEqual(0f, At(tiles, 0.1f, 0.1f).x);
        }

        [TestMethod]
        public void TheFlakeBitsSplitAtTwenty()
        {
            var tiles = Tiles();
            var flakes = new Vector4[40];
            for (int k = 0; k < 40; k++) flakes[k] = new Vector4(0.5f, 0.5f, 0.003f, 0.1f);
            FrostTiles.Build(tiles, flakes, 40, new Vector4[0], 0, new Vector2(0, -1), Aspect);
            Vector4 centre = At(tiles, 0.5f, 0.5f);
            Assert.AreEqual((float)((1 << 20) - 1), centre.x);
            Assert.AreEqual((float)((1 << 20) - 1), centre.y);
        }

        [TestMethod]
        public void ADropMarksItsHeadAndItsTrailAgainstTheGravity()
        {
            var tiles = Tiles();
            var drops = new[] { new Vector4(0.5f, 0.5f, 0.01f, 0.2f) };
            FrostTiles.Build(tiles, new Vector4[0], 0, drops, 1, new Vector2(0, -1), Aspect);
            Assert.AreEqual(1f, At(tiles, 0.5f, 0.5f).z, "head");
            Assert.AreEqual(1f, At(tiles, 0.5f, 0.65f).z, "trail 0.15 above the head");
            Assert.AreEqual(0f, At(tiles, 0.5f, 0.3f).z, "nothing below the head");
            Assert.AreEqual(0f, At(tiles, 0.9f, 0.5f).z, "nothing beside it");
            // Gravity to the right of the picture: the trail lies to the left.
            tiles = Tiles();
            FrostTiles.Build(tiles, new Vector4[0], 0, drops, 1, new Vector2(1, 0), Aspect);
            Assert.AreEqual(1f, At(tiles, 0.5f - 0.15f / Aspect, 0.5f).z, "trail 0.15 to the left");
            Assert.AreEqual(0f, At(tiles, 0.5f + 0.15f / Aspect, 0.5f).z, "nothing to the right");
        }

        [TestMethod]
        public void EveryPixelOfAShapeLiesInAMarkedTile()
        {
            // A drop in any direction: the oriented rectangle of the shader (along from -trail to 1.2 radii, across 0.55 radii for the
            // trail and 1 radius for the head) lies inside the marked tiles.
            var rng = new System.Random(3);
            for (int n = 0; n < 200; n++)
            {
                double angle = rng.NextDouble() * 2 * Math.PI;
                var g = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                var drop = new Vector4(0.1f + 0.8f * (float)rng.NextDouble(), 0.1f + 0.8f * (float)rng.NextDouble(), 0.006f + 0.01f * (float)rng.NextDouble(), 0.2f * (float)rng.NextDouble());
                var tiles = Tiles();
                FrostTiles.Build(tiles, new Vector4[0], 0, new[] { drop }, 1, g, Aspect);
                for (int m = 0; m < 60; m++)
                {
                    float along = -drop.w + (drop.w + 1.2f * drop.z) * (float)rng.NextDouble();
                    float across = (2f * (float)rng.NextDouble() - 1f) * drop.z;
                    float hx = g.x * along - g.y * across, hy = g.y * along + g.x * across;
                    float u = drop.x + hx / Aspect, v = drop.y + hy;
                    if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                    Assert.AreEqual(1f, At(tiles, u, v).z, "point at along " + along + " across " + across + " of drop " + n);
                }
            }
        }

        [TestMethod]
        public void ShapesOutsideThePictureAreIgnoredAndTheArrayIsCleared()
        {
            var tiles = Tiles();
            tiles[5] = new Vector4(7, 7, 7, 7);
            var flakes = new[] { new Vector4(-0.5f, 0.5f, 0.004f, 0.1f), new Vector4(0.5f, 1.6f, 0.004f, 0.1f) };
            FrostTiles.Build(tiles, flakes, 2, new Vector4[0], 0, new Vector2(0, -1), Aspect);
            foreach (Vector4 t in tiles) Assert.AreEqual(Vector4.zero, t);
            Assert.ThrowsException<ArgumentException>(() => FrostTiles.Build(new Vector4[3], flakes, 0, new Vector4[0], 0, new Vector2(0, -1), Aspect));
        }
    }
}
