using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The Engineer's memos, which must give the answer the work gives: the same curve's Mach limit.
    [TestClass]
    public class EngineerCachesTests
    {
        // A jet's thrust over Mach: rises to a peak at Mach 2 and falls to nothing by Mach 5.
        private static double Jet(double mach)
        {
            return mach <= 2.0 ? mach * 50.0 : Math.Max(0.0, 100.0 - (mach - 2.0) * 100.0 / 3.0);
        }

        // A curve with the same numbers is not searched again and gets the limit the search gave; another curve is searched.
        [TestMethod]
        public void TheMachLimitOfACurveIsSearchedOnce()
        {
            int evaluations = 0;
            Func<double, double> counted = mach => { evaluations++; return Jet(mach); };
            float[] numbers = { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 2f, 100f, 1f, 1f, 0f, 0f, 0f };
            double expected = AirLaunch.MachLimit(Jet);

            Assert.AreEqual(expected, AirLaunch.MachLimit(numbers, counted));
            int first = evaluations;
            Assert.IsTrue(first > 1000, "the first call searches");

            Assert.AreEqual(expected, AirLaunch.MachLimit((float[])numbers.Clone(), counted));
            Assert.AreEqual(first, evaluations, "the same numbers are not searched again");

            float[] other = (float[])numbers.Clone();
            other[9] = 90f;
            AirLaunch.MachLimit(other, counted);
            Assert.IsTrue(evaluations > first, "other numbers are");
        }
    }
}
