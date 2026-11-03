using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // The decisions about Parallax's scatters that need no game: which scatter is grass, how far the rays reach
    // from its culling point, and which start of the farthest level of detail counts.
    [TestClass]
    public class ParallaxScatterReaderTests
    {
        // A scatter named grass is grass whatever its case; one without the word, or without a name, is not.
        [TestMethod]
        public void GrassIsFoundByItsName()
        {
            Assert.IsTrue(ParallaxScatterReader.IsGrass("Grass"));
            Assert.IsTrue(ParallaxScatterReader.IsGrass("TallGRASS"));
            Assert.IsFalse(ParallaxScatterReader.IsGrass("Rocks"));
            Assert.IsFalse(ParallaxScatterReader.IsGrass(null));
        }

        // The grass note reads the main grass only: tall and dry grass are left out.
        [TestMethod]
        public void TheMainGrassIsNeitherTallNorDry()
        {
            Assert.IsTrue(ParallaxScatterReader.IsMainGrass("Grass"));
            Assert.IsTrue(ParallaxScatterReader.IsMainGrass("KerbinGrassMeadow"));
            Assert.IsFalse(ParallaxScatterReader.IsMainGrass("TallGrass"));
            Assert.IsFalse(ParallaxScatterReader.IsMainGrass("dryGrass"));
            Assert.IsFalse(ParallaxScatterReader.IsMainGrass("Boulders"));
            Assert.IsFalse(ParallaxScatterReader.IsMainGrass(null));
        }

        // A tenth over the range and 20 m: 100 m reaches 130 m, and 0 still reaches 20 m.
        [TestMethod]
        public void TheReachIsATenthOverTheRangeAndTwentyMetres()
        {
            Assert.AreEqual(130f, ParallaxScatterReader.ReachRadius(100f), 0.001f);
            Assert.AreEqual(20f, ParallaxScatterReader.ReachRadius(0f), 0.001f);
        }

        // The lowest start counts; a start of 0 or less is none, and the first start is taken whole.
        [TestMethod]
        public void TheLowestStartOfTheFarthestLevelCounts()
        {
            Assert.AreEqual(300f, ParallaxScatterReader.NearestLodStart(0f, 300f));
            Assert.AreEqual(200f, ParallaxScatterReader.NearestLodStart(300f, 200f));
            Assert.AreEqual(200f, ParallaxScatterReader.NearestLodStart(200f, 300f));
            Assert.AreEqual(200f, ParallaxScatterReader.NearestLodStart(200f, 0f));
            Assert.AreEqual(0f, ParallaxScatterReader.NearestLodStart(0f, -5f));
        }
    }
}
