using System;
using System.IO;

namespace ReDefinition.Core
{
    // Where the probes, tests and dumps write their files: ReDefinitionCaptures beside
    // KSP_x64.exe, and in it a folder of their own, never the folder itself.
    internal static class CaptureFolder
    {
        // ReDefinitionCaptures beside KSP_x64.exe, the folder every capture folder lies in.
        internal static string Root
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "ReDefinitionCaptures"); }
        }

        // This session's folder once Run has named it; null until the first file asks for it.
        private static string run;

        // This KSP session's folder, <time>-run, made at the first file: the files that
        // belong to no measurement of their own folder go here.
        internal static string Run
        {
            get
            {
                if (run == null) run = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-run");
                Directory.CreateDirectory(run);
                return run;
            }
        }
    }
}
