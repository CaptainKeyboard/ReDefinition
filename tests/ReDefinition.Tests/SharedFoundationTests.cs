using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // The shared foundation for mods (docs/development/shared-foundation.md): the
    // history reset's rules, the hooks' guarding, the Direct3D 12 packets as the proxy
    // lays them out, and the wrapper mods copy against the interface.
    [TestClass]
    public class SharedFoundationTests
    {
        [TestMethod]
        public void ACutBeforeTheFrameResetsEveryone()
        {
            HistoryResets resets = new HistoryResets();
            string forMods, forRig;
            resets.Begin("camera target now 'probe'", out forMods, out forRig);
            Assert.AreEqual("camera target now 'probe'", forMods);
            Assert.AreEqual("camera target now 'probe'", forRig);

            resets.Begin(null, out forMods, out forRig);
            Assert.IsNull(forMods);
            Assert.IsNull(forRig);
        }

        [TestMethod]
        public void AModsRequestBeforeTheFrameResetsEveryoneOnce()
        {
            HistoryResets resets = new HistoryResets();
            resets.Request("Hullcam switched cameras");
            resets.Request("a second reason");
            string forMods, forRig;
            resets.Begin(null, out forMods, out forRig);
            Assert.AreEqual("Hullcam switched cameras", forMods);
            Assert.AreEqual("Hullcam switched cameras", forRig);

            resets.Begin(null, out forMods, out forRig);
            Assert.IsNull(forMods);
        }

        [TestMethod]
        public void ACutAndARequestInOneFrameAreOneReset()
        {
            HistoryResets resets = new HistoryResets();
            resets.Request("a mod");
            string forMods, forRig;
            resets.Begin("camera parent now 'probe'", out forMods, out forRig);
            Assert.AreEqual("camera parent now 'probe'", forRig);
            Assert.IsNull(resets.Late(null), "the request went with the cut");

            resets.Begin(null, out forMods, out forRig);
            Assert.IsNull(forMods);
            Assert.IsNull(forRig);
        }

        [TestMethod]
        public void AResetFoundWhileTheFrameRendersReachesTheModsInTheNextFrameOnly()
        {
            HistoryResets resets = new HistoryResets();
            string forMods, forRig;
            resets.Begin(null, out forMods, out forRig);
            resets.Request(null);
            Assert.AreEqual("a mod", resets.Late(null));

            resets.Begin(null, out forMods, out forRig);
            Assert.AreEqual("a mod", forMods);
            Assert.IsNull(forRig, "the upscaler reset for it in the frame before");

            resets.Begin(null, out forMods, out forRig);
            Assert.IsNull(forMods);
        }

        [TestMethod]
        public void AHandlerThatThrowsIsRemovedAndTheOthersRun()
        {
            List<string> reports = new List<string>();
            HookList<Action<int>> hooks = new HookList<Action<int>>("probes", reports.Add);
            int calls = 0;
            Action<int> good = value => calls += value;
            Action<int> bad = value => { throw new InvalidOperationException("probe"); };
            hooks.Add(bad);
            hooks.Add(good);
            hooks.Add(good);

            hooks.Invoke(handler => handler(1));
            hooks.Invoke(handler => handler(1));

            Assert.AreEqual(2, calls, "the good handler, added once, ran in both frames");
            Assert.AreEqual(1, hooks.Count);
            Assert.AreEqual(1, reports.Count);
            StringAssert.Contains(reports[0], "probes");
        }

        [TestMethod]
        public void TheDirect3D12PacketsAreLaidOutAsTheProxyReadsThem()
        {
            Assert.AreEqual(88, Marshal.SizeOf(typeof(D3d12Bridge.CreatePacket)));
            Assert.AreEqual(424, Marshal.SizeOf(typeof(D3d12Bridge.DispatchPacket)));
            Assert.AreEqual(40, Marshal.OffsetOf(typeof(D3d12Bridge.DispatchPacket), "Read").ToInt32());
            Assert.AreEqual(168, Marshal.OffsetOf(typeof(D3d12Bridge.DispatchPacket), "Constants").ToInt32());
            Assert.AreEqual(24, Marshal.SizeOf(typeof(D3d12Bridge.HandlePacket)));
        }

        [TestMethod]
        public void TheWrapperFindsEveryMemberOfTheInterface()
        {
            Assert.IsNotNull(typeof(Api.ApiInfo).Assembly);
            IList<string> missing = YourMod.ReDefinitionApi.MissingMembers();
            Assert.AreEqual(0, missing.Count, "missing: " + string.Join(", ", missing));
            Assert.AreEqual(Api.ApiInfo.Version, YourMod.ReDefinitionApi.Version);
            Assert.IsTrue(YourMod.ReDefinitionApi.Installed);
        }

        [TestMethod]
        public void TheWrapperReturnsTheInterfacesValues()
        {
            Assert.AreEqual(Api.Frame.UpscalerActive, YourMod.ReDefinitionApi.UpscalerActive);
            Assert.AreEqual(Api.Frame.RenderSize, YourMod.ReDefinitionApi.RenderSize);
            Assert.AreEqual(Api.Frame.Jitter, YourMod.ReDefinitionApi.Jitter);
            Assert.AreEqual(Api.Profiles.Current, YourMod.ReDefinitionApi.Profile);
            string status;
            Assert.AreEqual(-1, YourMod.ReDefinitionApi.ComputePassState(12345, out status), "the out parameter binds");
            Assert.IsNotNull(status);
        }

        [TestMethod]
        public void ReadingTheFrameThroughTheWrapperAllocatesNothing()
        {
            // Bound before measuring.
            Assert.IsTrue(YourMod.ReDefinitionApi.Installed);
            AppDomain.MonitoringIsEnabled = true;
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            float sum = 0f;
            for (int i = 0; i < 100000; i++)
            {
                sum += YourMod.ReDefinitionApi.Jitter.x + YourMod.ReDefinitionApi.RenderSize.x;
                if (YourMod.ReDefinitionApi.HistoryReset || YourMod.ReDefinitionApi.UpscalerActive) sum += 1f;
            }
            long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
            // Boxing each value would be over two megabytes; the allocation counter moves in steps of
            // its allocation contexts.
            Assert.IsTrue(allocated < 64 * 1024, allocated + " bytes allocated over 400000 reads (" + sum + ")");
        }
    }
}
