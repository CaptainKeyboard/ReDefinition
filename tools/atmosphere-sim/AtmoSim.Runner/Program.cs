using System;
using System.Diagnostics;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Command line of the offline proof. Phase 1 only needs "smoke": it runs a body for some days and prints health
    // numbers (NaN, maximum wind, step time, hash). The criteria runs of phase 2 are further commands.
    public static class Program
    {
        public static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0] : "smoke";
            if (cmd == "smoke") return Smoke(args);
            Console.Error.WriteLine("unknown command " + cmd);
            return 2;
        }

        public static BodyParams ByName(string n)
        {
            switch (n)
            {
                case "kerbin": return BodyParams.KerbinLike();
                case "duna": return BodyParams.DunaLike();
                case "eve": return BodyParams.EveLike();
                case "laythe": return BodyParams.LaytheLike();
                case "slow": return BodyParams.SlowRotator();
                case "noocean": return BodyParams.NoOcean();
                case "cold": return BodyParams.ColdWorld();
                case "earth": return BodyParams.EarthLike();
            }
            throw new ArgumentException("body " + n);
        }

        private static int Smoke(string[] args)
        {
            string body = args.Length > 1 ? args[1] : "kerbin";
            double days = args.Length > 2 ? double.Parse(args[2]) : 20;
            int nlon = args.Length > 3 ? int.Parse(args[3]) : 128;
            int nlat = nlon / 2, levels = args.Length > 4 ? int.Parse(args[4]) : 16;
            var bp = ByName(body);
            var cfg = new SimConfig { NLon = nlon, NLat = nlat, Levels = levels, Seed = 7 };
            var sim = new Atmosphere(bp, cfg);
            Console.WriteLine("body " + body + " grid " + nlon + "x" + nlat + "x" + levels + " Ld(km) " + (sim.DeformationRadiusM / 1000.0).ToString("F0") + " tauRad(d) " + (sim.RadiativeTauS / 86400.0).ToString("F1"));
            long steps = (long)(days * bp.RotationPeriodS / cfg.StepSeconds);
            var sw = Stopwatch.StartNew();
            for (long s = 0; s < steps; s++)
            {
                sim.Step();
                if (s % Math.Max(1, steps / 10) == 0 || s == steps - 1)
                {
                    double pmax = 0, pmean = 0, tmin = 1e9, tmax = 0, cwp = 0;
                    for (int c = 0; c < sim.NLon * sim.NLat; c++) { pmax = Math.Max(pmax, sim.PrecipMmH[c]); pmean += sim.PrecipMmH[c]; cwp += sim.CloudPath[c]; tmin = Math.Min(tmin, sim.T3[c]); tmax = Math.Max(tmax, sim.T3[c]); }
                    pmean /= sim.NLon * sim.NLat; cwp /= sim.NLon * sim.NLat;
                    Console.WriteLine("step " + (s + 1) + " day " + (sim.Ut / bp.RotationPeriodS).ToString("F1") + " maxwind " + sim.MaxWind.ToString("F1") + " T " + tmin.ToString("F0") + ".." + tmax.ToString("F0")
                        + " P(mm/h) mean " + pmean.ToString("F3") + " max " + pmax.ToString("F2") + " cwp " + cwp.ToString("F3") + " ledger " + sim.WaterLedgerResidual.ToString("E1") + " ms/step " + (sw.Elapsed.TotalMilliseconds / (s + 1)).ToString("F1"));
                    if (double.IsNaN(sim.MaxWind) || sim.MaxWind > 400) { Console.WriteLine("BLOWUP"); return 1; }
                }
            }
            Console.WriteLine("hash " + sim.StateHash().ToString("X16"));
            return 0;
        }
    }
}
