using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres
{
    // The own sky probe's pacing (PRUEFPROTOKOLL 365): a part of a face every few frames instead of a whole face every second frame.
    [TestClass]
    public class OwnCloudSkyProbeTests
    {
        [TestMethod]
        public void ATurnDrawsEveryColumnOfEveryFaceOnceAndTakesTurnFrames()
        {
            const int n = 128;
            var drawn = new int[6, n];
            int face = 0, part = 0, steps = 0, frames = 0;
            for (int frame = 0; frame < OwnCloudSkyProbe.Pacing.TurnFrames; frame++)
            {
                frames++;
                if (!OwnCloudSkyProbe.Pacing.DrawsAt(frame)) continue;
                int x0, width;
                OwnCloudSkyProbe.Pacing.PartColumns(part, n, out x0, out width);
                for (int x = x0; x < x0 + width; x++) drawn[face, x]++;
                OwnCloudSkyProbe.Pacing.Next(ref face, ref part);
                steps++;
            }
            Assert.AreEqual(OwnCloudSkyProbe.Pacing.TurnFrames, frames);
            Assert.AreEqual(6 * OwnCloudSkyProbe.Pacing.PartsPerFace, steps);
            Assert.AreEqual(0, face, "the turn ends where it began");
            Assert.AreEqual(0, part);
            for (int f = 0; f < 6; f++)
                for (int x = 0; x < n; x++) Assert.AreEqual(1, drawn[f, x], "face " + f + ", column " + x);
        }

        [TestMethod]
        public void ThePartsCoverAFaceOfAnOddWidthWithoutAGapOrAnOverlap()
        {
            foreach (int n in new[] { 1, 3, 7, 64, 129 })
            {
                var drawn = new int[n];
                for (int part = 0; part < OwnCloudSkyProbe.Pacing.PartsPerFace; part++)
                {
                    int x0, width;
                    OwnCloudSkyProbe.Pacing.PartColumns(part, n, out x0, out width);
                    Assert.IsTrue(x0 >= 0 && width >= 1 && x0 + width <= n, "width " + n + ", part " + part + ": " + x0 + " + " + width);
                    for (int x = x0; x < x0 + width; x++) drawn[x]++;
                }
                // A face narrower than its parts is drawn by more than one of them (a column twice is harmless: copied and marched anew); none is left out.
                for (int x = 0; x < n; x++) Assert.IsTrue(drawn[x] >= 1 && (n < OwnCloudSkyProbe.Pacing.PartsPerFace || drawn[x] == 1), "width " + n + ", column " + x + ": " + drawn[x]);
            }
        }

        [TestMethod]
        public void TheWorkOfAFrameIsAQuarterOfAWholeFaceEverySecondFrame()
        {
            // Counted over ten turns from the pacing's own answers: the columns drawn a frame, against a whole face on every second frame (0.5 of a face a frame).
            const int n = 128;
            int frames = 10 * OwnCloudSkyProbe.Pacing.TurnFrames, face = 0, part = 0, draws = 0;
            long columns = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                if (!OwnCloudSkyProbe.Pacing.DrawsAt(frame)) continue;
                int x0, width;
                OwnCloudSkyProbe.Pacing.PartColumns(part, n, out x0, out width);
                columns += width;
                OwnCloudSkyProbe.Pacing.Next(ref face, ref part);
                draws++;
            }
            Assert.AreEqual(frames / 4, draws, "one command buffer in four frames (one in two before)");
            double facesAFrame = (double)columns / n / frames;
            Assert.AreEqual(0.125, facesAFrame, 1e-9);
            Assert.AreEqual(0.25, facesAFrame / 0.5, 1e-9);
        }
    }
}
