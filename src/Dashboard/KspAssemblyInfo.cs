// Loaded after ReDefinition.Common in any folder order, and not without it: KSP's AssemblyLoader sorts by this declaration and leaves
// an assembly unloaded, with a warning naming the missing dependency, where it is not met.
[assembly: KSPAssemblyDependency("ReDefinition.Common", 0, 1)]
