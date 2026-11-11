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
            if (cmd == "maps")
            {
                var bp0 = ByName(args[1]); int nl0 = int.Parse(args[2]);
                var s0 = new Atmosphere(bp0, new SimConfig { NLon = nl0, NLat = nl0 / 2, Levels = 12, Seed = ulong.Parse(args[3]) });
                double mn = 9, mx = -9, land = 0; foreach (double o in s0.OceanFrac) { mn = Math.Min(mn, o); mx = Math.Max(mx, o); if (o < 0.5) land++; }
                Console.WriteLine("ocean min " + mn + " max " + mx + " land share " + land / s0.OceanFrac.Length);
                return 0;
            }
            if (cmd == "nanscan")
            {
                // One-parameter mutations of the Kerbin-like body: which one makes the first step NaN.
                var muts = new System.Collections.Generic.Dictionary<string, Action<BodyParams>> {
                    { "diurnal25", b => b.DiurnalLandAmpK = 25 }, { "diurnal10", b => b.DiurnalLandAmpK = 10 }, { "obliq0.44", b => b.ObliquityRad = 0.44 }, { "obliq0.05", b => b.ObliquityRad = 0.05 }, { "ecc0.05", b => b.Eccentricity = 0.05 },
                    { "ps6000", b => b.SurfacePressurePa = 6000 }, { "g2.94", b => b.GravityMs2 = 2.94 }, { "dusty", b => b.Dusty = true }, { "ocean0", b => b.OceanFractionTarget = 0 }, { "ocean0.3", b => b.OceanFractionTarget = 0.3 },
                    { "rot65517", b => b.RotationPeriodS = 65517 }, { "rot864000", b => b.RotationPeriodS = 864000 }, { "year17e6", b => b.YearS = 17315400 }, { "radius320", b => b.RadiusM = 320000 }, { "flux590", b => b.SolarFluxWm2 = 590 },
                    { "tmean215", b => b.SurfaceMeanK = 215 }, { "lapse0.0045", b => b.LapseKPerM = 0.0045 }, { "trop20000", b => b.TropopauseM = 20000 } };
                foreach (var kv in muts)
                {
                    var bpM = BodyParams.KerbinLike(); kv.Value(bpM);
                    var sM = new Atmosphere(bpM, new SimConfig { NLon = 64, NLat = 32, Levels = 12, Seed = 1 });
                    sM.Step(); sM.Step();
                    bool bad = false; foreach (double v in sM.Psi1Grid) if (double.IsNaN(v) || double.IsInfinity(v)) { bad = true; break; }
                    Console.WriteLine(kv.Key + " -> " + (bad ? "NaN" : "ok"));
                }
                return 0;
            }
            if (cmd == "nanscan2")
            {
                // Leave-one-out on the Duna-like body: reset one parameter to the Kerbin-like value and see whether the first steps stay finite.
                var muts = new System.Collections.Generic.Dictionary<string, Action<BodyParams>> {
                    { "radius", b => b.RadiusM = 600000 }, { "rotation", b => b.RotationPeriodS = 21600 }, { "year", b => b.YearS = 9203545 }, { "obliquity", b => b.ObliquityRad = 0.4084 }, { "ecc", b => b.Eccentricity = BodyParams.KerbinLike().Eccentricity },
                    { "flux", b => b.SolarFluxWm2 = BodyParams.KerbinLike().SolarFluxWm2 }, { "ps", b => b.SurfacePressurePa = BodyParams.KerbinLike().SurfacePressurePa }, { "g", b => b.GravityMs2 = BodyParams.KerbinLike().GravityMs2 },
                    { "tmean", b => b.SurfaceMeanK = BodyParams.KerbinLike().SurfaceMeanK }, { "lapse", b => b.LapseKPerM = BodyParams.KerbinLike().LapseKPerM }, { "tropopause", b => b.TropopauseM = BodyParams.KerbinLike().TropopauseM },
                    { "ocean", b => b.OceanFractionTarget = 0.7 }, { "dusty", b => b.Dusty = false }, { "diurnal", b => b.DiurnalLandAmpK = BodyParams.KerbinLike().DiurnalLandAmpK } };
                foreach (var kv in muts)
                {
                    var bpM = BodyParams.DunaLike(); kv.Value(bpM);
                    var sM = new Atmosphere(bpM, new SimConfig { NLon = 64, NLat = 32, Levels = 12, Seed = 1 });
                    sM.Step(); sM.Step();
                    bool bad = false; foreach (double v in sM.Psi1Grid) if (double.IsNaN(v) || double.IsInfinity(v)) { bad = true; break; }
                    Console.WriteLine("duna with " + kv.Key + " = Kerbin value -> " + (bad ? "NaN" : "ok"));
                }
                return 0;
            }
            if (cmd == "nan")
            {
                // Which public array of a body turns NaN first, after initialisation and after each of the first steps.
                var sN = new Atmosphere(ByName(args[1]), new SimConfig { NLon = 64, NLat = 32, Levels = 12, Seed = 1 });
                for (int st = 0; st <= 3; st++)
                {
                    var bad = new System.Collections.Generic.List<string>();
                    foreach (var kv in new System.Collections.Generic.Dictionary<string, double[]> { { "Ts", sN.Ts }, { "T3", sN.T3 }, { "Qv", sN.Qv }, { "Qc", sN.Qc }, { "U3", sN.U3 }, { "Psi1", sN.Psi1Grid }, { "Psi2", sN.Psi2Grid }, { "Delta", sN.DeltaGrid }, { "Omega3", sN.Omega3 }, { "Precip", sN.PrecipMmH }, { "Wind10", sN.Wind10 }, { "Ice", sN.IceH }, { "Soil", sN.SoilMm } })
                        foreach (double v in kv.Value) if (double.IsNaN(v) || double.IsInfinity(v)) { bad.Add(kv.Key); break; }
                    Console.WriteLine("after " + st + " steps: NaN in " + string.Join(",", bad) + "  Ld " + sN.DeformationRadiusM + " radTau " + sN.RadiativeTauS);
                    sN.Step();
                }
                return 0;
            }
            if (cmd == "cost") return Timing.Cost(args);
            if (cmd == "determinism") return Timing.Determinism(args);
            if (cmd == "forecast") return Timing.Forecast(args);
            if (cmd == "run") return Study.Run(args);
            if (cmd == "epoch") return EpochStudy.Run(args);
            if (cmd == "evaluate") return Evaluate.Run(args);
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
