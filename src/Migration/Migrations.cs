using System.Collections.Generic;

namespace ReDefinition.Migration
{
    // Every change to ReDefinition's settings since the first release with a migration, and what a player's file does about it.
    //
    // A step belongs to the release that brings the change. Until that release is out its steps stand under Unreleased, and the
    // surface check (tests/ReDefinition.Tests/SurfaceTests.cs) holds them against the surface of the last release: what differs
    // must be explained by a step, or the build fails. A player never has a release that was not out, so a setting renamed twice
    // before a release needs one step, from the name of the last release to the name now. A release moves its steps into Released.
    //
    // A step has an id that never changes: a file lists the ids it has been through, and a step runs on a file once.
    internal static class Migrations
    {
        // The steps of releases that are out, oldest first.
        private static readonly SettingsStep[] Released = new SettingsStep[0];

        // The steps for the release after 0.1.3.
        private static readonly SettingsStep[] Unreleased =
        {
            // The DLSS model row named two choices and now names DLSS's own choice and the presets. High was preset L and Fast was
            // preset M; High was the standard and DLSS's own choice is now.
            new ConvertedSetting("dlss-model-presets", "dlssModel",
                new Dictionary<string, string> { { "High", "Default" }, { "Fast", "M" } },
                "The DLSS model row now offers DLSS's own choice and the presets K, M and L. High became Default, and Fast became M."),
        };

        // All steps, in the order they run.
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
    }
}
