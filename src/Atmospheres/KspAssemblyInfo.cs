// Loaded after ReDefinition.Common in any folder order, and not without it: KSP's AssemblyLoader sorts by this declaration and leaves
// an assembly unloaded, with a warning naming the missing dependency, where it is not met.
[assembly: KSPAssemblyDependency("ReDefinition.Common", 0, 1)]

// The main mod is required (ReDefinition.Api): KSP leaves these atmospheres unloaded, with a warning naming it, where ReDefinition is
// missing. AtmospheresStartup checks the Api's version and members besides (HostGate).
[assembly: KSPAssemblyDependency("ReDefinition", 0, 1)]
