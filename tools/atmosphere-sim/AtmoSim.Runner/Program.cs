using System;

namespace AtmoSim.Runner
{
    // Command line of the offline proof of the weather generator.
    //   smoke <body>                                  health print of one body
    //   cost [out.json] [covermap]                    criterion 6 (run under the exclusive lock for the signed numbers)
    //   determinism [out.json]                        criteria 7 and 8
    //   tracker <body> <days> <seed> <frameHours> <out.json> [wavesOff]   criteria 1, 2, 12 detectors and trackers
    //   evaluate <root>                               applies the header thresholds to the files in <root>
    public static class Program
    {
        public static int Main(string[] args)
        {
            // option --recon: real grids and the fitted climatology of the B09 dumps for Kerbin, Duna, Laythe and Eve (and the Kerbin variants)
            var rest = new System.Collections.Generic.List<string>();
            foreach (string a in args) { if (a == "--recon") Study.ReconDir = ReconData.FindDumpDir(); else rest.Add(a); }
            if (rest.Count != args.Length) { args = rest.ToArray(); if (Study.ReconDir == null) { Console.Error.WriteLine("--recon: no tests/data/recon/dumps found"); return 3; } }
            string cmd = args.Length > 0 ? args[0] : "smoke";
            if (cmd == "data") return Study.Data(args);
            if (cmd == "smoke") return Study.Smoke(args);
            if (cmd == "month") return Study.Month(args);
            if (cmd == "zonal") return Study.Zonal(args);
            if (cmd == "frontdebug") return Study.FrontDebug(args);
            if (cmd == "cost") return Timing.Cost(args);
            if (cmd == "determinism") return Timing.Determinism(args);
            if (cmd == "tracker") return Study.Tracker(args);
            if (cmd == "evaluate") return Evaluate.Run(args);
            if (cmd == "crit-tracker") return Criteria.Tracker(args);
            if (cmd == "crit-levels") return Criteria.Levels(args);
            if (cmd == "crit-fields") return Criteria.Fields(args);
            if (cmd == "crit-surface") return CriteriaSurface.Run(args);
            if (cmd == "crit-cells") return CriteriaCells.Run(args);
            Console.Error.WriteLine("unknown command " + cmd);
            return 2;
        }
    }
}
