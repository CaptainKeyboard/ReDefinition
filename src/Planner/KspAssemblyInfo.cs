// For ReDefinition.dll, which declares [KSPAssemblyDependency("ReDefinition.Planner", 0, 1)]: KSP loads it after the planner, and leaves
// it unloaded, with a line in KSP.log, where the planner's folder is not there. ReDefinition names the planner's types directly, so without
// the declaration it would start and fail in every place it meets one of them.
[assembly: KSPAssembly("ReDefinition.Planner", 0, 1)]
