using System;

namespace ReDefinition.Core
{
    // The game's command line as ReDefinition reads it: the -redefinition switches that start a probe, a
    // profiler or a test scene, none of which a player gives. The line is read when asked, never cached.
    internal static class CommandLine
    {
        // Whether the switch is on the command line, spelled as given.
        internal static bool Has(string name)
        {
            return CommandLine.Has(name);
        }

        // The word after the switch; null where the switch is not given or is the last word.
        internal static string After(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }
    }
}
