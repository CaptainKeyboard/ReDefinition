using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Window;

namespace ReDefinition.Tests
{
    // The diagnostics window's options as data (DebugOption, DebugOptionSet, DebugPins): what
    // the search and the "Changed" filter let through, what the reset puts back, which rows are
    // greyed out, and the pinned ids and frame times the strip keeps.
    [TestClass]
    public class DebugOptionsTests
    {
        // The values the options below read and set; writes counts the jitter's setter and the action's runs.
        private int jitter = 1;
        private int exposure = 1;
        private int mask;
        private int shadow = 1;
        private int upscaler;
        private int hook;
        private bool hasMasks;
        private int writes;

        // A set of the options a diagnostics page has: a main switch, switches and choices with defaults and
        // reasons, one shown on a second page, and an action, all over the fields above.
        private DebugOptionSet Build()
        {
            DebugOptionSet set = new DebugOptionSet();

            DebugOption main = new DebugOption("upscaler", DebugCategory.Overview, "Upscaler", DebugKind.Switch, DebugPersistence.Saved)
            {
                Resettable = false, Get = () => upscaler, Set = v => upscaler = v,
            };
            set.Add(main);

            DebugOption jitterOption = new DebugOption("jitter", DebugCategory.UpscalerInputs, "Jitter", DebugKind.Switch, DebugPersistence.Saved)
            {
                Group = "Motion", Default = 1, Get = () => jitter, Set = v => { jitter = v; writes++; },
                Keywords = new[] { "TAA" },
            };
            set.Add(jitterOption);

            DebugOption exposureOption = new DebugOption("autoExposure", DebugCategory.UpscalerInputs, "Auto exposure",
                                                         DebugKind.Switch, DebugPersistence.Saved)
            {
                Default = 1, Get = () => exposure, Set = v => exposure = v,
                // DLSS measures its exposure itself: blocked while on, usable once away from the default.
                Blocked = () => hasMasks ? null : "DLSS measures its exposure itself",
            };
            set.Add(exposureOption);

            DebugOption maskOption = new DebugOption("reactiveMask", DebugCategory.UpscalerInputs, "Reactive mask",
                                                     DebugKind.Choice, DebugPersistence.Saved)
            {
                Choices = new[] { "Off", "Renderers", "FSR's estimate" }, Get = () => mask, Set = v => mask = v,
                Blocked = () => hasMasks ? null : "needs FSR 3 or AMD",
            };
            set.Add(maskOption);

            DebugOption shadowOption = new DebugOption("vesselShadow", DebugCategory.UpscalerInputs, "Vessel shadow in frame generation",
                                                       DebugKind.Choice, DebugPersistence.SessionOnly)
            {
                Choices = new[] { "Moved", "Kept in place" }, Default = 1, Get = () => shadow, Set = v => shadow = v,
                Also = DebugCategory.FrameGeneration,
            };
            set.Add(shadowOption);

            DebugOption hookOption = new DebugOption("hookProbe", DebugCategory.Capture, "Hook probe, into the log",
                                                     DebugKind.Switch, DebugPersistence.SessionOnly)
            {
                Get = () => hook, Set = v => hook = v,
            };
            set.Add(hookOption);

            set.Add(new DebugOption("write", DebugCategory.Capture, "Write diagnostics to log", DebugKind.Action, DebugPersistence.SessionOnly)
            {
                ActionText = "Write", Run = () => writes += 100,
            });
            return set;
        }

        // The ids of the options, in their order.
        private static List<string> Ids(IEnumerable<DebugOption> options)
        {
            List<string> ids = new List<string>();
            foreach (DebugOption option in options) ids.Add(option.Id);
            return ids;
        }

        // A search finds a part of a label in any letter case, and nothing where no label has it.
        [TestMethod]
        public void ASearchFindsALabelPartInAnyCase()
        {
            DebugOptionSet set = Build();
            CollectionAssert.AreEqual(new[] { "jitter" }, Ids(set.Filter("JIT", false)));
            CollectionAssert.AreEqual(new[] { "autoExposure" }, Ids(set.Filter("exposure", false)));
            Assert.AreEqual(0, set.Filter("nothing like it", false).Count);
        }

        // A search also finds an option by its keywords, its group, its choices and its page's title.
        [TestMethod]
        public void ASearchAlsoFindsKeywordsGroupsChoicesAndPages()
        {
            DebugOptionSet set = Build();
            CollectionAssert.AreEqual(new[] { "jitter" }, Ids(set.Filter("taa", false)), "keyword");
            CollectionAssert.AreEqual(new[] { "jitter" }, Ids(set.Filter("motion", false)), "group");
            CollectionAssert.AreEqual(new[] { "reactiveMask" }, Ids(set.Filter("estimate", false)), "choice");
            CollectionAssert.AreEqual(new[] { "hookProbe", "write" }, Ids(set.Filter("capture", false)), "page");
        }

        // An empty, blank or missing search lets every option through.
        [TestMethod]
        public void AnEmptyOrBlankSearchLetsEverythingThrough()
        {
            DebugOptionSet set = Build();
            Assert.AreEqual(set.All.Count, set.Filter("", false).Count);
            Assert.AreEqual(set.All.Count, set.Filter("   ", false).Count);
            Assert.AreEqual(set.All.Count, set.Filter(null, false).Count);
        }

        // The Changed filter keeps only the options away from their default, and together with a search both
        // have to hold.
        [TestMethod]
        public void TheChangedFilterKeepsOnlyOptionsAwayFromTheirDefault()
        {
            DebugOptionSet set = Build();
            Assert.AreEqual(0, set.Filter(null, true).Count);

            jitter = 0;
            shadow = 0;
            hook = 1;
            CollectionAssert.AreEqual(new[] { "jitter", "vesselShadow", "hookProbe" }, Ids(set.Filter(null, true)));
            // Together with the search: both have to hold.
            CollectionAssert.AreEqual(new[] { "vesselShadow" }, Ids(set.Filter("shadow", true)));
        }

        // The main switches and the actions never count as changed: they are no diagnostics to put back.
        [TestMethod]
        public void TheMainSwitchesAndActionsAreNeverChanged()
        {
            DebugOptionSet set = Build();
            upscaler = 1;
            Assert.IsFalse(set.ById("upscaler").Changed);
            Assert.IsFalse(set.ById("write").Changed);
            Assert.AreEqual(0, set.Filter(null, true).Count);
        }

        // A page holds its own options and those shown on it too, and its count follows the search.
        [TestMethod]
        public void APageHoldsItsOwnOptionsAndThoseShownThereToo()
        {
            DebugOptionSet set = Build();
            CollectionAssert.AreEqual(new[] { "jitter", "autoExposure", "reactiveMask", "vesselShadow" },
                                      Ids(set.Of(DebugCategory.UpscalerInputs)));
            CollectionAssert.AreEqual(new[] { "vesselShadow" }, Ids(set.Of(DebugCategory.FrameGeneration)));
            Assert.AreEqual(0, set.Of(DebugCategory.Previews).Count);
            Assert.AreEqual(1, set.Count(DebugCategory.FrameGeneration, null, false));
            Assert.AreEqual(0, set.Count(DebugCategory.FrameGeneration, "jitter", false));
        }

        // An option shown on a second page is found by a search once.
        [TestMethod]
        public void AnOptionShownOnTwoPagesIsInTheResultsOnce()
        {
            DebugOptionSet set = Build();
            List<string> found = Ids(set.Filter("vessel", false));
            CollectionAssert.AreEqual(new[] { "vesselShadow" }, found);
        }

        // The reset puts back only the options away from their default, each through its own setter, leaves the
        // main switches alone, and says how many it put back.
        [TestMethod]
        public void TheResetPutsBackOnlyWhatChangedThroughItsSetter()
        {
            DebugOptionSet set = Build();
            jitter = 0;
            mask = 2;
            upscaler = 1;
            writes = 0;

            Assert.AreEqual(2, set.Reset());
            Assert.AreEqual(1, jitter);
            Assert.AreEqual(0, mask);
            Assert.AreEqual(1, upscaler, "the main switches are the player's settings");
            Assert.AreEqual(1, writes, "through the option's own setter, and only the changed one");
            Assert.AreEqual(0, set.Reset());
        }

        // A row that does not apply is greyed out with its reason, and usable again once it applies.
        [TestMethod]
        public void ARowThatDoesNotApplyIsGreyedOutWithItsReason()
        {
            DebugOptionSet set = Build();
            hasMasks = false;
            Assert.AreEqual("needs FSR 3 or AMD", set.ById("reactiveMask").Unavailable());
            Assert.AreEqual("DLSS measures its exposure itself", set.ById("autoExposure").Unavailable());
            Assert.IsNull(set.ById("jitter").Unavailable());

            hasMasks = true;
            Assert.IsNull(set.ById("reactiveMask").Unavailable());
            Assert.IsNull(set.ById("autoExposure").Unavailable());
        }

        // A row greyed out by its reason stays usable while away from its default, so it can be put back, unless
        // the option says it cannot be.
        [TestMethod]
        public void ARowAwayFromItsDefaultStaysUsableSoItCanBePutBack()
        {
            DebugOptionSet set = Build();
            hasMasks = false;
            mask = 1;
            exposure = 0;
            Assert.IsNull(set.ById("reactiveMask").Unavailable());
            Assert.IsNull(set.ById("autoExposure").Unavailable());

            // Unless the option says it cannot be put back.
            DebugOption fixedOption = set.ById("reactiveMask");
            fixedOption.PutBack = false;
            Assert.AreEqual("needs FSR 3 or AMD", fixedOption.Unavailable());
        }

        // An action is greyed out by its reason alone, having no default to be away from.
        [TestMethod]
        public void AnActionIsBlockedByItsReasonAlone()
        {
            DebugOption action = new DebugOption("record", DebugCategory.Capture, "Record", DebugKind.Action, DebugPersistence.SessionOnly)
            {
                Blocked = () => "a recording is about to start",
            };
            Assert.AreEqual("a recording is about to start", action.Unavailable());
        }

        // A dynamic label reads its text at the moment it is shown, so a count-down can change it.
        [TestMethod]
        public void ADynamicLabelReadsNow()
        {
            string text = "Record the screen";
            DebugOption option = new DebugOption("record", DebugCategory.Capture, "Record", DebugKind.Action, DebugPersistence.SessionOnly)
            {
                DynamicLabel = () => text,
            };
            Assert.AreEqual("Record the screen", option.CurrentLabel);
            text = "Recording in 3 s";
            Assert.AreEqual("Recording in 3 s", option.CurrentLabel);
        }

        // A second option with an id already in the set is refused.
        [TestMethod]
        public void TwoOptionsWithTheSameIdAreRefused()
        {
            DebugOptionSet set = Build();
            Assert.ThrowsException<System.ArgumentException>(
                () => set.Add(new DebugOption("jitter", DebugCategory.Capture, "Again", DebugKind.Switch, DebugPersistence.Saved)));
        }

        // Every page of the window has a title of its own.
        [TestMethod]
        public void EveryPageHasATitleAndTheTitlesDiffer()
        {
            HashSet<string> titles = new HashSet<string>();
            foreach (DebugCategory category in System.Enum.GetValues(typeof(DebugCategory)))
            {
                string title = DebugOptionSet.Title(category);
                Assert.IsFalse(string.IsNullOrEmpty(title));
                Assert.IsTrue(titles.Add(title), title + " names two pages");
            }
        }

        // Pins keep the order they were made in, load only known ids, each once, toggle on and off, and read
        // back as the text they are saved as.
        [TestMethod]
        public void PinsKeepTheirOrderAndLoadOnlyKnownIds()
        {
            DebugOptionSet set = Build();
            DebugPins pins = new DebugPins();
            pins.Load("hookProbe, nonsense,jitter,jitter,,", set);
            CollectionAssert.AreEqual(new[] { "hookProbe", "jitter" }, new List<string>(pins.Ids));
            Assert.AreEqual("hookProbe,jitter", pins.Text);

            pins.Toggle("reactiveMask");
            pins.Toggle("hookProbe");
            Assert.AreEqual("jitter,reactiveMask", pins.Text);
            Assert.IsTrue(pins.Has("jitter"));
            Assert.IsFalse(pins.Has("hookProbe"));
            CollectionAssert.AreEqual(new[] { "jitter", "reactiveMask" }, Ids(pins.Options(set)));

            DebugPins again = new DebugPins();
            again.Load(pins.Text, set);
            Assert.AreEqual(pins.Text, again.Text);
            again.Load("", set);
            Assert.AreEqual(0, again.Ids.Count);
        }

        // The ring keeps only its newest frame times, oldest first, with their maximum; an index past them throws.
        [TestMethod]
        public void TheFrameTimeRingKeepsTheNewestAndOrdersThemOldestFirst()
        {
            FrameTimeRing ring = new FrameTimeRing(4);
            Assert.AreEqual(0, ring.Count);
            Assert.AreEqual(0f, ring.Percentile(0.5f));
            for (int i = 1; i <= 3; i++) ring.Add(i);
            Assert.AreEqual(3, ring.Count);
            Assert.AreEqual(1f, ring[0]);
            Assert.AreEqual(3f, ring[2]);

            ring.Add(4f);
            ring.Add(5f);
            ring.Add(6f);
            Assert.AreEqual(4, ring.Count);
            Assert.AreEqual(3f, ring[0]);
            Assert.AreEqual(6f, ring[3]);
            Assert.AreEqual(6f, ring.Max);
            Assert.ThrowsException<System.ArgumentOutOfRangeException>(() => ring[4].ToString());
        }

        // A percentile is the frame time below which that share of the frames lies; shares beyond 0 and 1 give
        // the longest and the shortest.
        [TestMethod]
        public void ThePercentilesOfTheFrameTimes()
        {
            FrameTimeRing ring = new FrameTimeRing(100);
            for (int i = 1; i <= 100; i++) ring.Add(i);
            Assert.AreEqual(51f, ring.Percentile(0.5f), 1f);
            Assert.AreEqual(95f, ring.Percentile(0.95f), 1f);
            Assert.AreEqual(100f, ring.Percentile(2f));
            Assert.AreEqual(1f, ring.Percentile(-1f));
        }
    }
}
