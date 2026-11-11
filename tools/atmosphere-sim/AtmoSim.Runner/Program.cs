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
            if (cmd == "series") return Study.Series(args);
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
            if (cmd == "diag-levels") return Diagnostics.Levels(args);
            if (cmd == "diag-micro") return Diagnostics.Micro(args);
            if (cmd == "diag-climate") return Diagnostics.Climate(args);
            if (cmd == "diag-surfacehash") return Diagnostics.SurfaceHash(args);
            if (cmd == "diag-ground") return Diagnostics2.Ground(args);
            if (cmd == "diag-salinity") return Diagnostics2.Salinity(args);
            if (cmd == "diag-seam") return Diagnostics2.Seam(args);
            if (cmd == "diag-snow") return Diagnostics2.Snow(args);
            if (cmd == "diag-wet") return Diagnostics2.Wet(args);
            if (cmd == "surfmap") return SurfaceMaps.Run(args);
            if (cmd == "diag-cellsites") return Diagnostics2.CellSites(args);
            if (cmd == "diag-cellfar") return Diagnostics2.CellFar(args);
            if (cmd == "diag-jump") return Diagnostics2.Jump(args);
            if (cmd == "diag-slots") return Diagnostics2.Slots(args);
            if (cmd == "diag-contin") return Diagnostics2.Contin(args);
            if (cmd == "frames") return Round2Maps.Frames(args);
            if (cmd == "diag-alloc") return Round2Maps.Alloc(args);
            if (cmd == "diag-ice") return Round2Maps.Ice(args);
            if (cmd == "diag-icesite") return Round2Maps.IceSite(args);
            if (cmd == "diag-noise") return Round2Maps.Noise(args);
            if (cmd == "diag-edge") return Round2Maps.Edge(args);
            if (cmd == "diag-clim") return Round2Maps.Clim(args);
            if (cmd == "diag-itcz") return Round2Maps.Itcz(args);
            Console.Error.WriteLine("unknown command " + cmd);
            return 2;
        }
    }
}
