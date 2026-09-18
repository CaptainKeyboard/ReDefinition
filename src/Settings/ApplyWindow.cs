namespace ReDefinition.Settings
{
    // When a change to a setting takes effect. Declared per setting, so a menu
    // can say "takes effect at the next scene" instead of leaving the player to
    // wonder -- Community Shaders does the same with its restart-required
    // fields (Feature::GetRestartRequiredFields).
    internal enum ApplyWindow
    {
        // Not declared. The default, so that a setting nobody has checked does
        // not claim to take effect at once.
        Unspecified,

        // On the running objects, at once.
        Live,

        // When the next scene builds its cameras -- or at once, where the
        // scene the setting needs is already loaded.
        NextScene,

        // Only at the next start of the game.
        Restart,
    }
}
