using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T05
{
    // A render target of the layer pass that is dropped (a size change, the end) is released AND destroyed. The engine's calls cannot run
    // outside the game, so the counts come from fakes standing in for them; the folder is read for any other way of dropping a target.
    [TestClass]
    public class RenderTargetsTests
    {
        private Action<RenderTexture> savedRelease;
        private Action<UnityEngine.Object> savedDestroy;
        private readonly List<string> calls = new List<string>();

        [TestInitialize]
        public void Fake()
        {
            savedRelease = RenderTargets.Release;
            savedDestroy = RenderTargets.Destroy;
            calls.Clear();
            RenderTargets.Release = rt => calls.Add("release " + Id(rt));
            RenderTargets.Destroy = o => calls.Add("destroy " + Id(o));
        }

        [TestCleanup]
        public void Restore()
        {
            RenderTargets.Release = savedRelease;
            RenderTargets.Destroy = savedDestroy;
        }

        // An object of the engine's class without the native object behind it (its constructor needs the engine).
        private static RenderTexture Target()
        {
            return (RenderTexture)FormatterServices.GetUninitializedObject(typeof(RenderTexture));
        }

        private static int Id(object o)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        [TestMethod]
        public void ATargetIsReleasedThenDestroyedOnce()
        {
            RenderTexture rt = Target();
            RenderTargets.Dispose(rt);
            CollectionAssert.AreEqual(new[] { "release " + Id(rt), "destroy " + Id(rt) }, calls);
        }

        [TestMethod]
        public void NullIsSkipped()
        {
            RenderTargets.Dispose(null);
            Assert.AreEqual(0, calls.Count);
        }

        [TestMethod]
        public void EverySizeChangeDropsEveryOldTargetWithNoLeak()
        {
            // The resize path of the reconstructor: for each change of size the three old targets go through Dispose.
            var live = new HashSet<int>();
            RenderTargets.Release = rt => { };
            RenderTargets.Destroy = o => live.Remove(Id(o));
            RenderTexture latest = null, spare = null, low = null;
            for (int change = 0; change < 25; change++)
            {
                RenderTargets.Dispose(latest);
                RenderTargets.Dispose(spare);
                RenderTargets.Dispose(low);
                latest = Target();
                spare = Target();
                low = Target();
                live.Add(Id(latest));
                live.Add(Id(spare));
                live.Add(Id(low));
            }
            Assert.AreEqual(3, live.Count, "one native object per target, not one per size change");
        }

        [TestMethod]
        public void NoCodeOfTheLayerPassReleasesATargetWithoutDestroyingIt()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string folder = Path.Combine(root, "src", "Atmospheres", "Render", "Layers");
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                if (file.EndsWith("RenderTargets.cs")) continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    // A command buffer is released without a destroy; a render target is destroyed in the same or the next line.
                    if (Regex.IsMatch(lines[i], @"\.Release\(\)") && !Regex.IsMatch(lines[i], @"(?i)commands?\.Release")
                        && !lines[i].Contains("Destroy") && !(i + 1 < lines.Length && lines[i + 1].Contains("Destroy")))
                        offenders.Add(Path.GetFileName(file) + ":" + (i + 1) + " " + lines[i].Trim());
                }
            }
            Assert.AreEqual(0, offenders.Count, "A render target released without a destroy (use RenderTargets.Dispose):\n" + string.Join("\n", offenders));
        }
    }
}
