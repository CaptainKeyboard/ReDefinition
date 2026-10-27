using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // An enum's names from tables made once (EnumNames): the same answers as Enum.Parse, Enum.TryParse and
    // ToString, for every value and for the names that are none.
    [TestClass]
    public class EnumNamesTests
    {
        // Each value's name is the one ToString gives, and parses back to the value in any letter case when
        // the case does not count.
        [TestMethod]
        public void EveryValueRoundTripsThroughItsName()
        {
            foreach (RaytracingMode mode in Enum.GetValues(typeof(RaytracingMode)))
            {
                Assert.AreEqual(mode.ToString(), EnumNames<RaytracingMode>.Name(mode));
                Assert.AreEqual(mode, EnumNames<RaytracingMode>.Parse(mode.ToString(), false));
                Assert.AreEqual(mode, EnumNames<RaytracingMode>.Parse(mode.ToString().ToUpperInvariant(), true));
                RaytracingMode parsed;
                Assert.IsTrue(EnumNames<RaytracingMode>.TryParse(mode.ToString(), out parsed));
                Assert.AreEqual(mode, parsed);
            }
        }

        // What the tables lack goes to the framework: the case counts for TryParse and for Parse without the
        // flag, a number is taken as Enum.Parse takes it, and a name that is none throws as before.
        [TestMethod]
        public void WhatTheTablesLackIsLeftToTheFramework()
        {
            RaytracingMode parsed;
            Assert.IsFalse(EnumNames<RaytracingMode>.TryParse("full", out parsed));
            Assert.IsTrue(EnumNames<RaytracingMode>.TryParse("2", out parsed));
            Assert.AreEqual((RaytracingMode)2, parsed);
            Assert.IsFalse(EnumNames<RaytracingMode>.TryParse(null, out parsed));
            Assert.IsFalse(EnumNames<RaytracingMode>.TryParse("Moon", out parsed));
            Assert.AreEqual((RaytracingMode)1, EnumNames<RaytracingMode>.Parse("1", true));
            Assert.AreEqual("42", EnumNames<RaytracingMode>.Name((RaytracingMode)42));
            try
            {
                EnumNames<RaytracingMode>.Parse("full", false);
                Assert.Fail("a name of the wrong case parsed");
            }
            catch (ArgumentException)
            {
            }
            try
            {
                EnumNames<RaytracingMode>.Parse("Moon", true);
                Assert.Fail("a name that is none parsed");
            }
            catch (ArgumentException)
            {
            }
            try
            {
                EnumNames<RaytracingMode>.Parse(null, true);
                Assert.Fail("null parsed");
            }
            catch (ArgumentNullException)
            {
            }
        }
    }
}
