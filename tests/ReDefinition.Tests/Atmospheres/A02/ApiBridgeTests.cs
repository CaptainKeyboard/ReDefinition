using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Tests.Atmospheres.A02
{
    // The reflection bridge to ReDefinition.Api: members found by name, absent or differently shaped members count as not there, and a
    // registration reaches the host and leaves it again.
    [TestClass]
    public class ApiBridgeTests
    {
        // A host that has the after-upscaling hook, the overlay hook and the history reset, but no depth hook; its motion-vector hook
        // has the wrong signature.
        public static class FakeHooks
        {
            public static readonly List<Delegate> Added = new List<Delegate>();
            public static readonly List<Delegate> OverlayAdded = new List<Delegate>();
            public static bool ThrowOnRegister;

            public static void RegisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> handler)
            {
                if (ThrowOnRegister) throw new InvalidOperationException("refused");
                Added.Add(handler);
            }

            public static void UnregisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> handler)
            {
                Added.Remove(handler);
            }

            public static void RegisterOverlay(Action<CommandBuffer, Camera> handler)
            {
                OverlayAdded.Add(handler);
            }

            public static void UnregisterOverlay(Action<CommandBuffer, Camera> handler)
            {
                OverlayAdded.Remove(handler);
            }

            public static void RegisterMotionVectors(Action<CommandBuffer, Camera> wrongShape)
            {
            }

            public static void UnregisterMotionVectors(Action<CommandBuffer, Camera> wrongShape)
            {
            }
        }

        public static class FakeFrame
        {
            public static string LastReason;

            public static void RequestHistoryReset(string reason)
            {
                LastReason = reason;
            }
        }

        // A host that also has the depth hook.
        public static class FakeDepthHooks
        {
            public static readonly List<Delegate> Added = new List<Delegate>();

            public static void RegisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> handler)
            {
            }

            public static void UnregisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> handler)
            {
            }

            public static void RegisterAfterUpscalingWithDepth(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
            {
                Added.Add(handler);
            }

            public static void UnregisterAfterUpscalingWithDepth(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
            {
                Added.Remove(handler);
            }
        }

        private static readonly List<string> Warnings = new List<string>();

        [TestInitialize]
        public void Reset()
        {
            FakeHooks.Added.Clear();
            FakeHooks.OverlayAdded.Clear();
            FakeHooks.ThrowOnRegister = false;
            FakeFrame.LastReason = null;
            FakeDepthHooks.Added.Clear();
            Warnings.Clear();
        }

        private static ApiBridge Bridge(Type hooks, Type frame)
        {
            return new ApiBridge(hooks, frame, Warnings.Add);
        }

        [TestMethod]
        public void AnAbsentHostAnswersFalseAndDoesNotThrow()
        {
            ApiBridge bridge = Bridge(null, null);
            Assert.IsFalse(bridge.HostAvailable);
            Assert.IsFalse(bridge.DepthAvailable);
            Assert.IsFalse(bridge.MotionVectorsAvailable);
            Assert.IsFalse(bridge.OverlayAvailable);
            Assert.IsFalse(bridge.HistoryResetAvailable);
            Assert.IsFalse(bridge.Register("t", (c, i, d, cam) => { }, false));
            Assert.IsFalse(bridge.RegisterOverlay("t", (c, cam) => { }));
            bridge.RequestHistoryReset("nothing happens");
            Assert.AreEqual(0, bridge.RegistrationCount);
        }

        [TestMethod]
        public void AMemberAbsentInTheHostIsNotAnError()
        {
            ApiBridge bridge = Bridge(typeof(FakeHooks), typeof(FakeFrame));
            Assert.IsTrue(bridge.HostAvailable);
            Assert.IsFalse(bridge.DepthAvailable, "the fake host has no depth hook");
            Assert.IsFalse(bridge.MotionVectorsAvailable, "a hook with another signature counts as absent");
            Assert.IsTrue(bridge.OverlayAvailable);
            Assert.IsTrue(bridge.HistoryResetAvailable);
        }

        [TestMethod]
        public void ADrawThatWantsDepthIsRefusedWhereTheHostHasNone()
        {
            ApiBridge bridge = Bridge(typeof(FakeHooks), typeof(FakeFrame));
            Assert.IsFalse(bridge.Register("needs depth", (c, i, d, cam) => { }, true));
            Assert.AreEqual(0, FakeHooks.Added.Count);
        }

        [TestMethod]
        public void ADrawWithoutDepthReachesTheHostAndLeavesIt()
        {
            ApiBridge bridge = Bridge(typeof(FakeHooks), typeof(FakeFrame));
            RenderTexture seenDepth = null;
            bool called = false;
            PostUpscaleDraw draw = (commands, image, depth, camera) => { called = true; seenDepth = depth; };
            Assert.IsTrue(bridge.Register("t", draw, false));
            Assert.IsTrue(bridge.Register("t", draw, false), "registering twice is harmless");
            Assert.AreEqual(1, FakeHooks.Added.Count);
            ((Action<CommandBuffer, RenderTexture, Camera>)FakeHooks.Added[0])(null, null, null);
            Assert.IsTrue(called);
            Assert.IsNull(seenDepth, "the plain hook hands the draw no depth");
            Assert.IsTrue(bridge.Unregister(draw));
            Assert.AreEqual(0, FakeHooks.Added.Count);
            Assert.IsFalse(bridge.Unregister(draw));
        }

        [TestMethod]
        public void ADrawThatWantsDepthGoesToTheDepthHookWhereThereIsOne()
        {
            ApiBridge bridge = Bridge(typeof(FakeDepthHooks), null);
            Assert.IsTrue(bridge.DepthAvailable);
            bool called = false;
            Assert.IsTrue(bridge.Register("t", (c, i, d, cam) => { called = true; }, true));
            Assert.AreEqual(1, FakeDepthHooks.Added.Count);
            ((Action<CommandBuffer, RenderTexture, RenderTexture, Camera>)FakeDepthHooks.Added[0])(null, null, null, null);
            Assert.IsTrue(called);
        }

        [TestMethod]
        public void AHostThatRefusesIsReportedAndLeavesNoRegistration()
        {
            ApiBridge bridge = Bridge(typeof(FakeHooks), typeof(FakeFrame));
            FakeHooks.ThrowOnRegister = true;
            Assert.IsFalse(bridge.Register("t", (c, i, d, cam) => { }, false));
            Assert.AreEqual(0, bridge.RegistrationCount);
            Assert.AreEqual(1, Warnings.Count);
        }

        [TestMethod]
        public void OverlayAndHistoryResetReachTheHost()
        {
            ApiBridge bridge = Bridge(typeof(FakeHooks), typeof(FakeFrame));
            Action<CommandBuffer, Camera> handler = (c, cam) => { };
            Assert.IsTrue(bridge.RegisterOverlay("t", handler));
            Assert.AreEqual(1, FakeHooks.OverlayAdded.Count);
            bridge.RequestHistoryReset("weather cut");
            Assert.AreEqual("weather cut", FakeFrame.LastReason);
            bridge.UnregisterAll();
            Assert.AreEqual(0, FakeHooks.OverlayAdded.Count);
            Assert.AreEqual(0, bridge.RegistrationCount);
        }

        [TestMethod]
        public void TheGameBridgeResolvesTheMainModWithoutThrowing()
        {
            // The test project references the main mod, so its Api types are loaded; the depth hook is added by a later package.
            ApiBridge bridge = new ApiBridge();
            Type hooks = typeof(ReDefinition.Api.Hooks);
            bool hasDepth = hooks.GetMethod("RegisterAfterUpscalingWithDepth") != null;
            Assert.IsTrue(bridge.HostAvailable);
            Assert.AreEqual(hasDepth, bridge.DepthAvailable);
            Assert.IsTrue(bridge.MotionVectorsAvailable);
            Assert.IsTrue(bridge.OverlayAvailable);
            Assert.IsTrue(bridge.HistoryResetAvailable);
        }

        [TestMethod]
        public void TheNullCompositorStillAnswersFalse()
        {
            IPostUpscaleCompositor none = new NullPostUpscaleCompositor();
            Assert.IsFalse(none.HostAvailable);
            Assert.IsFalse(none.Register("t", (c, i, d, cam) => { }, false));
        }
    }
}
