using System.Collections.Generic;
using System.Text;

namespace ReDefinition.Upscaler
{
    // One other mod's filter that HostStack switches off while the upscaler runs, remembers, and hands back.
    internal interface IHostFilter
    {
        // The filter's name in the notes of a step that failed.
        string Label { get; }

        // Switches the filter off and remembers what it found. Appends what it did to the report, which is null when
        // nobody reads it.
        void Apply(StringBuilder report);

        // Switches off again what came back since, once a second while HostStack is applied.
        void Reassert();

        // Hands back what Apply changed where it still holds what was set, and appends what it did to the report.
        void Restore(StringBuilder report);

        // Appends the rows that show the filter's state to the report the diagnostics window reads.
        void AddLines(List<HostStack.Line> lines);
    }
}
