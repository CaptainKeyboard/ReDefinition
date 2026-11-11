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
            string cmd = args.Length > 0 ? args[0] : "smoke";
            if (cmd == "smoke") return Study.Smoke(args);
            if (cmd == "month") return Study.Month(args);
            if (cmd == "zonal") return Study.Zonal(args);
            if (cmd == "frontdebug") return Study.FrontDebug(args);
            if (cmd == "cost") return Timing.Cost(args);
            if (cmd == "determinism") return Timing.Determinism(args);
            if (cmd == "tracker") return Study.Tracker(args);
            if (cmd == "evaluate") return Evaluate.Run(args);
            Console.Error.WriteLine("unknown command " + cmd);
            return 2;
        }
    }
}
