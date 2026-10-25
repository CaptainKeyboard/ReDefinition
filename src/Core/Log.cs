namespace ReDefinition.Core
{
    // The tag every line ReDefinition writes into KSP.log begins with.
    internal static class Log
    {
        // Written in front of each line, so a grep of KSP.log finds ReDefinition's lines.
        public const string Tag = "[ReDefinition]";
    }
}
