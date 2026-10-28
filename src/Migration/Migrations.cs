using System.Collections.Generic;

namespace ReDefinition.Migration
{
    // Every change to a player's settings since the first release with a migration, and what a player's file does about it. There
    // are two kinds of file: ReDefinition's own settings.cfg, and bundled.cfg, which keeps the values of the other mods' settings that
    // ReDefinition holds for the player (keyed "<mod>.<setting>", as the registrations name them).
    //
    // A step belongs to the release that brings the change. Until that release is out its steps stand under Unreleased, and the
    // surface check (tests/ReDefinition.Tests/SurfaceTests.cs) holds them against the surface of the last release: what differs
    // must be explained by a step, or the build fails. A player never has a release that was not out, so a setting renamed twice
    // before a release needs one step, from the name of the last release to the name now.
    //
    // A release moves its steps into Released (tools/promote_release.ps1 does it between the marker comments). A step has an id that
    // never changes: a file lists the ids it has been through, and a step runs on a file once.
    internal static class Migrations
    {
        // The steps of releases that are out, oldest first.
        private static readonly SettingsStep[] Released =
        {
            // released-steps-end
        };

        // The steps for the release after 0.1.3.
        private static readonly SettingsStep[] Unreleased =
        {
            // unreleased-steps-begin
            // The DLSS model row named two choices and now names DLSS's own choice and the presets. High was preset L and Fast was
            // preset M; High was the standard and DLSS's own choice is now.
            new ConvertedSetting("dlss-model-presets", "dlssModel",
                new Dictionary<string, string> { { "High", "Default" }, { "Fast", "M" } },
                "The DLSS model row now offers DLSS's own choice and the presets K, M and L. High became Default, and Fast became M."),
            // unreleased-steps-end
        };

        // The same for bundled.cfg.
        private static readonly SettingsStep[] ReleasedBundled =
        {
            // released-bundled-end
        };

        private static readonly SettingsStep[] UnreleasedBundled =
        {
            // unreleased-bundled-begin
            // unreleased-bundled-end
        };

        // All steps for settings.cfg, in the order they run.
        internal static IEnumerable<SettingsStep> All
        {
            get
            {
                foreach (SettingsStep step in Released) yield return step;
                foreach (SettingsStep step in Unreleased) yield return step;
            }
        }

        // Only the steps of the release to come.
        internal static IEnumerable<SettingsStep> ToCome
        {
            get { return Unreleased; }
        }

        // All steps for bundled.cfg, in the order they run, and those of the release to come.
        internal static IEnumerable<SettingsStep> Bundled
        {
            get
            {
                foreach (SettingsStep step in ReleasedBundled) yield return step;
                foreach (SettingsStep step in UnreleasedBundled) yield return step;
            }
        }

        internal static IEnumerable<SettingsStep> BundledToCome
        {
            get { return UnreleasedBundled; }
        }
    }
}
