namespace ReDefinition
{
    // Which frames reset the temporal history, and for whom (SharedFrame).
    //
    // A reason decided when the frame begins -- a camera cut found before its first
    // scene camera, or a mod's request made before -- resets the upscaler and frame
    // generation and reaches every mod in the same frame. One found while the frame
    // renders resets the upscaler and frame generation at the frame's end, and
    // reaches the mods, whose effects in this frame already rendered, in the next:
    // there without a second reset of the upscaler.
    internal sealed class HistoryResets
    {
        private string requested;
        private string carried;

        // A mod's cut; the first reason until the reset is decided.
        internal void Request(string reason)
        {
            if (requested == null) requested = string.IsNullOrEmpty(reason) ? "a mod" : reason;
        }

        // When the frame begins, with the camera cut found then, or null.
        internal void Begin(string cut, out string forMods, out string forRig)
        {
            string request = Take();
            forRig = cut ?? request;
            forMods = forRig ?? carried;
            carried = null;
        }

        // At the frame's end, with the camera cut found then, or null: the reason the
        // upscaler resets for in addition to Begin's, which the next frame's Begin
        // hands to the mods.
        internal string Late(string cut)
        {
            string request = Take();
            string reason = cut ?? request;
            if (reason != null) carried = reason;
            return reason;
        }

        private string Take()
        {
            string reason = requested;
            requested = null;
            return reason;
        }
    }
}
