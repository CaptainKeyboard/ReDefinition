namespace ReDefinition.Atmospheres.Viewer
{
    // What the viewer registers for itself through the same mechanism a cloud package uses (CloudKeySource): the empty preset slots that the packages fill by registering a preset of
    // the same name (see CloudKeyRegistrations.Preset). Nothing here changes a default.
    internal static class ViewerBuiltIns
    {
        internal const string NewModelSlot = "New Model 96 Steps";

        [CloudKeySource]
        internal static void Register(CloudKeyRegistrations registrations)
        {
            registrations.Preset(NewModelSlot, "an empty slot: the package of the new cloud model fills it with its keys and a march of 96 steps", null);
        }
    }
}
