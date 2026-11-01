using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Autopilot.Flight;

namespace ReDefinition.Tests
{
    // A flight is a chain of coroutines; one that throws must end the flight with its reason and not leave it half run.
    [TestClass]
    public class SafeRunTests
    {
        private static IEnumerator Inner(List<string> log, bool throws)
        {
            try
            {
                log.Add("inner 1");
                yield return "inner wait";
                if (throws) throw new InvalidOperationException("broke");
                log.Add("inner 2");
            }
            finally
            {
                log.Add("inner done");
            }
        }

        private static IEnumerator Outer(List<string> log, bool throws)
        {
            log.Add("outer 1");
            yield return "outer wait";
            yield return Inner(log, throws);
            log.Add("outer 2");
        }

        private static List<object> Drain(IEnumerator guard)
        {
            var yielded = new List<object>();
            while (guard.MoveNext()) yielded.Add(guard.Current);
            return yielded;
        }

        [TestMethod]
        public void WhatTheCoroutinesYieldGoesOnInOrderAndTheWaitedForOnesRunInline()
        {
            var log = new List<string>();
            List<object> yielded = Drain(SafeRun.Guard(Outer(log, false), e => Assert.Fail()));
            CollectionAssert.AreEqual(new object[] { "outer wait", "inner wait" }, yielded);
            CollectionAssert.AreEqual(new[] { "outer 1", "inner 1", "inner 2", "inner done", "outer 2" }, log);
        }

        [TestMethod]
        public void AnExceptionInAWaitedForCoroutineIsReportedAndEndsTheChain()
        {
            var log = new List<string>();
            Exception caught = null;
            List<object> yielded = Drain(SafeRun.Guard(Outer(log, true), e => caught = e));
            Assert.IsNotNull(caught);
            Assert.AreEqual("broke", caught.Message);
            CollectionAssert.AreEqual(new object[] { "outer wait", "inner wait" }, yielded);
            CollectionAssert.Contains(log, "inner done");
            CollectionAssert.DoesNotContain(log, "outer 2");
        }

        [TestMethod]
        public void AChainThatIsStoppedFromOutsideLetsTheWaitedForCoroutinesEnd()
        {
            var log = new List<string>();
            IEnumerator guard = SafeRun.Guard(Outer(log, false), e => Assert.Fail());
            Assert.IsTrue(guard.MoveNext());
            Assert.IsTrue(guard.MoveNext());
            ((IDisposable)guard).Dispose();
            CollectionAssert.Contains(log, "inner done");
        }
    }
}
