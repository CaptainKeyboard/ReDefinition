using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Dashboard;

namespace ReDefinition.Tests
{
    // The dashboard's turn to a target attitude (FlyByWire.Inputs), flown on a rigid body whose input
    // on each axis changes its turning speed about that axis by the vessel's alpha.
    [TestClass]
    public class FlyByWireTests
    {
        private const double Dt = 0.02, Degree = Math.PI / 180;

        // The target 60 degrees away with its top rolled 90 degrees further: the nose goes the
        // shortest way there while the vessel rolls, not off to the side.
        [TestMethod]
        public void TheNoseGoesStraightWhileTheVesselRolls()
        {
            foreach (double rollAlpha in new[] { 0.5, 2.0 })
            {
                Vector3d nose = new Vector3d(0, 0, 1), top = new Vector3d(1, 0, 0), right = new Vector3d(0, -1, 0);
                Vector3d aim = Turned(nose, new Vector3d(1, 0, 0), 60 * Degree);
                Vector3d aimTop = Turned(Turned(top, new Vector3d(1, 0, 0), 60 * Degree), aim, 90 * Degree);
                Vector3d plane = Vector3d.Cross(nose, aim).normalized;
                double off = 0;
                Fly(ref nose, ref right, ref top, aim, aimTop, new Vector3d(0.5, rollAlpha, 0.5), 40, () =>
                    off = Math.Max(off, Math.Abs(Math.Asin(Vector3d.Dot(nose, plane)))));
                Assert.IsTrue(off < 0.5 * Degree, "off the way by " + off / Degree + " degrees, roll alpha " + rollAlpha);
                Assert.IsTrue(Vector3d.Angle(nose, aim) < 0.5, "nose " + Vector3d.Angle(nose, aim) + " degrees from the aim");
                Assert.IsTrue(Vector3d.Angle(top, aimTop) < 1, "top " + Vector3d.Angle(top, aimTop) + " degrees from the aim's");
            }
        }

        // The target straight behind: the vessel takes a way round and keeps it.
        [TestMethod]
        public void StraightBehindItTurnsRound()
        {
            Vector3d nose = new Vector3d(0, 0, 1), top = new Vector3d(1, 0, 0), right = new Vector3d(0, -1, 0);
            Vector3d aim = new Vector3d(0, 0, -1), aimTop = new Vector3d(1, 0, 0);
            Fly(ref nose, ref right, ref top, aim, aimTop, new Vector3d(0.5, 0.5, 0.5), 60, () => { });
            Assert.IsTrue(Vector3d.Angle(nose, aim) < 0.5, "nose " + Vector3d.Angle(nose, aim) + " degrees from the aim");
            Assert.IsTrue(Vector3d.Angle(top, aimTop) < 1, "top " + Vector3d.Angle(top, aimTop) + " degrees from the aim's");
        }

        private static void Fly(ref Vector3d nose, ref Vector3d right, ref Vector3d top, Vector3d aim, Vector3d aimTop, Vector3d alpha, double seconds,
                                Action watch)
        {
            FlyByWire.Turn turn = new FlyByWire.Turn();
            Vector3d spin = Vector3d.zero;
            for (double t = 0; t < seconds; t += Dt)
            {
                Vector3d input = FlyByWire.Inputs(turn, nose, right, top, aim, aimTop, alpha, Dt);
                spin += (Vector3d.Cross(nose, top) * (input.x * alpha.x) + Vector3d.Cross(nose, right) * (input.y * alpha.z)
                         + Vector3d.Cross(top, right) * (input.z * alpha.y)) * Dt;
                double angle = spin.magnitude * Dt;
                if (angle > 0)
                {
                    Vector3d axis = spin.normalized;
                    nose = Turned(nose, axis, angle);
                    right = Turned(right, axis, angle);
                    top = Turned(top, axis, angle);
                }
                watch();
            }
        }

        // `v` turned by `angle` about `axis` (a unit vector), a point of it moving at axis x v.
        private static Vector3d Turned(Vector3d v, Vector3d axis, double angle)
        {
            return v * Math.Cos(angle) + Vector3d.Cross(axis, v) * Math.Sin(angle) + axis * (Vector3d.Dot(axis, v) * (1 - Math.Cos(angle)));
        }
    }
}
