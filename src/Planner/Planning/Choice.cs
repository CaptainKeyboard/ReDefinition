namespace ReDefinition.Planner.Planning
{
    // One entry of a Dropdown.
    internal sealed class Choice
    {
        // The entry's text, its tooltip, whether it is the current choice, and what picking it does.
        public string Label;
        public string Tooltip;
        public bool Current;
        public Callback Pick;
        // Room left of it, for a list with levels: a moon under its planet.
        public float Indent;
        // Told when the pointer comes onto the entry and leaves it, or the list closes.
        public System.Action<bool> Hover;
    }
}
