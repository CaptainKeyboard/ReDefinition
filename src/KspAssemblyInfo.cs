// Harmony is a requirement: the section in KSP's own settings dialog
// (KspSettingsSection), the close buttons on the bundled mods' windows
// (ModWindowClose), the hooks behaviours put on their mods (HarmonyHooks),
// Scatterer's godrays fix (ScattererCompatibility), EVE's cloud jitter
// (EveCloudMotion) and TUFX's split around the upscaler (TufxPostProcessing) are
// Harmony patches.
//
// Declared the way Deferred and Kopernicus declare it, version 0.0: KSP's
// AssemblyLoader leaves an assembly unloaded whose dependency is not met, and
// HarmonyKSP's 0Harmony carries no KSPAssembly attribute to compare a version
// against.
[assembly: KSPAssemblyDependency("0Harmony", 0, 0)]

// For mods that reference ReDefinition.dll and declare
// [KSPAssemblyDependency("ReDefinition", 0, 1)]: KSP loads them after it, and not
// without it (ReDefinition.Api).
[assembly: KSPAssembly("ReDefinition", 0, 1)]
