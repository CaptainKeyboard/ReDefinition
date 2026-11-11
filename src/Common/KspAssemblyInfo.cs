// KSP's AssemblyLoader sorts the assemblies it finds by their KSPAssemblyDependency declarations and loads them in that order, whatever
// the order of their folders is. The bundled mods (ReDefinition, Planner, Dashboard, Atmospheres) each declare a dependency on this
// name; without it, the one whose folder sorts before Common's was loaded first and failed on its reference to this DLL.
// KSPAssembly lives in Assembly-CSharp, which this project already references; the attribute names no game type beyond that.
[assembly: KSPAssembly("ReDefinition.Common", 0, 1)]
