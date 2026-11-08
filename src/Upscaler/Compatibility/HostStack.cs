using System.Collections.Generic;
using System.Text;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Reads and configures the temporal and spatial filters of other mods.
    //
    // An upscaler is itself a temporal filter. Another one in front of it
    // averages an already time-averaged image a second time, while both jitter
    // the same projection matrix; with TUFX's Default-Flight profile and
    // Scatterer both running TAA the image smears.
    //
    // Switching Scatterer's TAA off is not enough on its own: three of its
    // effects outlive the component, and two of them the scene:
    //
    //   The replacement motion vector shader. Scatterer installs it globally for
    //       BuiltinShaderType.MotionVectors and never reverts it, so it survives
    //       a scene change and a config change alike -- only a restart clears it.
    //       It then keeps reading globals nobody feeds any more.
    //   TAA_UseFloatingOriginCameraMotion and TAA_PreviousFrameTransform. Set via
    //       Shader.SetGlobal, so likewise process-wide, and frozen at their last
    //       value once the component stops updating them.
    //   motionVectorGenerationMode per vessel renderer. Scatterer sets them all
    //       to ForceNoMotion on load and corrects that every frame; disabled, they
    //       freeze there and stop writing object motion while still moving in
    //       the image, and smear.
    //
    // All three are cleaned up here, so taking Scatterer's TAA in the middle of a
    // session needs no restart (docs/development/flow-frame.md).
    //
    // Everything goes through reflection: ReDefinition loads without TUFX and
    // Scatterer.
    internal static class HostStack
    {
        // One row of the report the diagnostics window shows: a mod's filter and its state.
        public struct Line
        {
            // What is reported on, and its state as text.
            public string Label;
            public string Value;
            public bool Ok;        // is this state suitable for FSR?
            public bool Present;   // false: mod not installed at all

            // A report row that is fine as it is.
            internal static Line Info(string label, string value)
            {
                return new Line { Label = label, Value = value, Ok = true, Present = true };
            }

            // A report row for a mod that is not installed.
            internal static Line Missing(string label)
            {
                return new Line { Label = label, Value = "not installed", Ok = true, Present = false };
            }
        }

        // The report is cached: FindObjectsOfType walks the whole scene, and OnGUI
        // runs several times per frame.
        // A snapshot also keeps the number of controls the same between the Layout
        // and Repaint events, as IMGUI requires.
        private const float RefreshInterval = 0.5f;

        // The seconds between the passes that switch off again what other mods brought back.
        internal const float ReassertInterval = 1.0f;

        // Scatterer's TAA and what it leaves behind, Scatterer's and Deferred's SMAA, Kerbal Frame Generator and
        // TUFX's antialiasing, each with the state it needs to hand back what it changed.
        internal static readonly ScattererTaaControl ScattererTaa = new ScattererTaaControl();
        internal static readonly SmaaControl Smaa = new SmaaControl();
        internal static readonly KfgControl Kfg = new KfgControl();
        internal static readonly TufxAaControl Tufx = new TufxAaControl();

        // The second antialiasing on one camera, kept off while ReDefinition neither upscales nor generates frames.
        internal static readonly DuplicateAaControl Duplicates = new DuplicateAaControl();

        // When Reassert next runs, in unscaled time.
        private static float nextReassert;

        // Whether Apply has configured the other mods' filters and Restore has not handed them back yet.
        public static bool Applied { get; private set; }

        // The report of the last Apply or Restore, for the diagnostics window.
        public static string LastMessage { get; private set; }

        // The rows of the last Build, when the next one is due and whether there was one at all.
        private static readonly List<Line> cachedLines = new List<Line>();
        private static float nextRefresh;
        private static bool everBuilt;

        // The cached report rows, as Refresh last built them.
        public static List<Line> Lines { get { return cachedLines; } }

        // Rebuilds the report rows, at most every RefreshInterval unless forced.
        public static void Refresh(bool force)
        {
            if (!force && everBuilt && Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshInterval;
            everBuilt = true;

            cachedLines.Clear();
            Build(cachedLines);
        }

        // Reads the state of TUFX, Scatterer's TAA and SMAA, Deferred's SMAA, Kerbal Frame Generator, the active vessel's
        // motion vector modes and the motion vector shader into report rows.
        private static void Build(List<Line> lines)
        {
            Tufx.AddLines(lines);
            ScattererTaa.AddLines(lines);
            Smaa.AddLines(lines);
            Kfg.AddLines(lines);
            ScattererTaa.AddMotionLines(lines);
        }

        // Switches off every filter of another mod that runs in front of the upscaler, and what Scatterer's TAA leaves
        // behind; remembers what it found for Restore. Returns the report.
        public static string Apply()
        {
            StringBuilder report = new StringBuilder();

            ScattererTaa.Apply(report);

            // Each mod on its own, and Applied either way: a step that throws (another
            // build of the mod) costs that mod's change, and Restore still hands back
            // what the others changed.
            Step(report, Smaa.Label, () => Smaa.Apply(report));
            Step(report, Kfg.Label, () => Kfg.Apply(report));
            Step(report, Tufx.Label, () => Tufx.Apply(report));

            Applied = true;
            LastMessage = report.ToString();
            Debug.Log(Log.Tag + " Host stack configured: " + LastMessage);
            Refresh(true);
            return LastMessage;
        }

        // Runs one mod's part of Apply or Restore; an exception costs that part only and is noted in the report.
        internal static void Step(StringBuilder report, string what, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                report.Append(what).Append(" left as it is (").Append(CompatibilityLog.Reason(e)).Append("). ");
            }
        }

        // Hands back everything Apply changed, where it still holds what was set here. Returns the report.
        public static string Restore()
        {
            StringBuilder report = new StringBuilder();
            Step(report, ScattererTaa.Label, () => ScattererTaa.Restore(report));
            Step(report, Tufx.Label, () => Tufx.Restore(report));
            Step(report, Smaa.Label, () => Smaa.Restore(report));
            Step(report, Kfg.Label, () => Kfg.Restore(report));

            Applied = false;
            LastMessage = report.ToString();
            Debug.Log(Log.Tag + " Host stack restored: " + LastMessage);
            Refresh(true);
            return LastMessage;
        }

        // At once rather than at the next interval: TUFX has just applied a
        // profile whose antialiasing is not taken yet (TufxBehaviour).
        internal static void ReassertNow()
        {
            nextReassert = 0f;
            Reassert();
        }

        // Switches off again what the mods brought back since, once a second while applied. Scatterer rebuilds its
        // TAA components in Init on every scene change, TUFX creates new PostProcessLayers, and a stored backup would
        // point at a destroyed object.
        public static void Reassert()
        {
            if (!Applied) return;
            if (Time.unscaledTime < nextReassert) return;
            nextReassert = Time.unscaledTime + ReassertInterval;

            ScattererTaa.Reassert();
            Smaa.Reassert();
            Kfg.Reassert();
            Tufx.Reassert();
        }

        // Every frame from the add-on: the second antialiasing on one camera taken while wanted, given back otherwise.
        internal static void SyncDuplicates(bool wanted, bool now)
        {
            Duplicates.Sync(wanted, now);
        }
    }
}
