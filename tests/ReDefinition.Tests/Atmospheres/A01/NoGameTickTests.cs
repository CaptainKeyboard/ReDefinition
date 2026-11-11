using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.A01
{
    // The modules run in every scene, also where the game's objects are absent: the main menu and the frames around a scene load have no
    // FlightGlobals object, so its ActiveVessel, Bodies, VesselsLoaded and GetHomeBody throw a NullReferenceException (K-W3b: the camera
    // frost threw five times in the menu and switched itself off for the session; BodyWatcher threw 31 times per start). GameGlobals is
    // the seam: these tests play the absent game.
    //
    // The test host has no Unity engine, so a method that calls a Unity native cannot be compiled there (SecurityException "ECall methods
    // must be packaged into a system module"): the body rule, the globals and the stand-down are tested as their own pure parts, the
    // module ticks as far as they run without a native call, and a source scan keeps the unguarded statics out of the code.
    [TestClass]
    public class NoGameTickTests
    {
        [TestInitialize]
        public void NoGame()
        {
            GameGlobals.SetPresence(() => false);
        }

        [TestCleanup]
        public void BackToTheGame()
        {
            GameGlobals.SetPresence(null);
        }

        [TestMethod]
        public void TheGlobalsAreNullWhenTheGameIsAbsent()
        {
            Assert.IsFalse(GameGlobals.Present);
            Assert.IsNull(GameGlobals.ActiveVessel);
            Assert.IsNull(GameGlobals.Bodies);
            Assert.IsNull(GameGlobals.VesselsLoaded);
            Assert.IsNull(GameGlobals.HomeBody);
            Assert.IsNull(GameGlobals.Sun);
        }

        [TestMethod]
        public void TheBodyWatcherSeesNoBodyInTheMenu()
        {
            Assert.IsNull(BodyWatcher.SeenBody());
        }

        // A streak of failing frames stands the path down for a while and it comes back; it never ends for the session.
        [TestMethod]
        public void AFailureStreakStandsDownAndResumes()
        {
            var stand = new StandDown(5, 5f);
            for (int i = 0; i < 4; i++) Assert.IsFalse(stand.Failed(), "failure " + i);
            Assert.IsFalse(stand.Paused);
            Assert.IsTrue(stand.Failed(), "the fifth starts the pause");
            Assert.IsTrue(stand.Paused);
            Assert.AreEqual(0, stand.Failures, "the count starts again");
            stand.Elapse(2f);
            Assert.IsTrue(stand.Paused, "2 s of 5 s");
            stand.Elapse(3.5f);
            Assert.IsFalse(stand.Paused, "back after the pause");
            for (int i = 0; i < 4; i++) stand.Failed();
            stand.Succeeded();
            for (int i = 0; i < 4; i++) Assert.IsFalse(stand.Failed(), "a working frame clears the streak");
            Assert.IsTrue(stand.Failed(), "and a new streak of five pauses again");
        }

        // Every module of the assembly: OnBodyChanged, Tick and Shutdown with the game absent throw no NullReferenceException. Any other
        // exception is a Unity native the test host cannot call.
        [TestMethod]
        public void NoModuleThrowsANullReferenceWhenTheGameIsAbsent()
        {
            var problems = new List<string>();
            int ticked = 0;
            foreach (Type type in ModuleRegistry.Discover(typeof(ModuleRegistry).Assembly))
            {
                IAtmospheresModule m;
                try
                {
                    m = (IAtmospheresModule)Activator.CreateInstance(type, true);
                }
                catch (Exception)
                {
                    // A module that needs the game to be made is out of reach here.
                    continue;
                }
                ticked++;
                Probe(problems, type, "OnBodyChanged", () => m.OnBodyChanged(null, null));
                for (int i = 0; i < 3; i++) Probe(problems, type, "Tick", () => m.Tick(1f / 30f));
                Probe(problems, type, "Shutdown", () => m.Shutdown());
            }
            Assert.IsTrue(ticked > 5, "modules reached: " + ticked);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        private static void Probe(List<string> problems, Type type, string what, Action call)
        {
            try
            {
                call();
            }
            catch (NullReferenceException e)
            {
                problems.Add(type.Name + "." + what + ": " + e.StackTrace.Split('\n')[0].Trim());
            }
            catch (Exception)
            {
                // A game call the test host cannot make; only a null reference is the defect here.
            }
        }

        // FlightGlobals.ActiveVessel, Bodies, VesselsLoaded and GetHomeBody throw while the FlightGlobals object does not exist, and a null
        // test on the result does not help (the getter throws). The code reads them through GameGlobals, which answers null.
        [TestMethod]
        public void TheAtmospheresReadTheFlightGlobalsOnlyThroughGameGlobals()
        {
            string src = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Atmospheres");
            var unguarded = new Regex(@"FlightGlobals\s*\.\s*(ActiveVessel|Bodies|VesselsLoaded|GetHomeBody)\b");
            var found = new List<string>();
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string rel = file.Substring(src.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("obj/") || rel.Contains("/obj/") || rel.StartsWith("Testing/") || rel.Contains("/Probe/")) continue;
                if (rel == "Infrastructure/Core/GameGlobals.cs") continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int comment = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) line = line.Substring(0, comment);
                    // The ones that test FlightGlobals.fetch first are guarded.
                    if (unguarded.IsMatch(line) && !line.Contains("FlightGlobals.fetch")) found.Add(rel + ":" + (i + 1) + " " + lines[i].Trim());
                }
            }
            Assert.AreEqual(0, found.Count, "Use GameGlobals instead:\n" + string.Join("\n", found));
        }
    }
}
