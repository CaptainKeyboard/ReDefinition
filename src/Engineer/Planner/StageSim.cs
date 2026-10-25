using System;
using System.Collections.Generic;
using System.Linq;

namespace ReDefinition.Engineer
{
    // How a resource flows to what burns it (PartResourceDefinition.resourceFlowMode,
    // or a propellant's own): from its own part, from the parts reachable through
    // crossfeed, or from any part.
    internal enum SimFlow
    {
        // Own: from its own part only; Stack: through crossfeed and fuel lines; Vessel: from any part.
        Own,
        Stack,
        Vessel,
    }

    // One propellant an engine mode burns.
    internal sealed class SimPropellant
    {
        // The resource's name, its share of the mix in units (Propellant.ratio), and how it flows to the engine.
        public string Name;
        public double Ratio;
        public SimFlow Flow;
    }

    // One engine mode: its thrust in vacuum at its thrust limit, in kN, its Isp in
    // vacuum, the propellants it burns that have mass (not the air it takes in, not
    // electric charge, which KSP's simulation leaves out as well), its thrust at the
    // surface of each body with air, and whether it burns out once lit. An
    // air-breathing mode: its thrust standing at the launch body's sea level (set
    // from ThrustAtSurface for the body, AirLaunch.Plan), its Isp there, and the
    // Mach number it can push the vessel to (AirLaunch.MachLimit).
    internal sealed class SimEngine
    {
        // In kN and seconds; the propellants of mass; the thrust at each body's surface with air, by the body's name; whether
        // it burns out once lit; for an air-breathing mode, the Mach number it reaches.
        public double Thrust;
        public double Isp;
        public readonly List<SimPropellant> Propellants = new List<SimPropellant>();
        public readonly Dictionary<string, double> ThrustAtSurface = new Dictionary<string, double>();
        public bool Solid;
        public double Mach;
    }

    // One part as the planner's simulation takes it, by its index in the vessel's part list.
    internal sealed class SimPart
    {
        // The parent's index in the part tree, -1 for the root, and the children's indices.
        public int Parent = -1;
        public readonly List<int> Children = new List<int>();
        // Parts whose fuel lines end here.
        public readonly List<int> FuelLineSources = new List<int>();
        // Whether fuel crosses through it; the stage that decouples it, -1 for none; the stage that activates it
        // (Part.inverseStage); whether it is a launch clamp, which never flies.
        public bool Crossfeed = true;
        public int DecoupleStage = -1;
        public int ActivatesAt = -1;
        public bool Clamp;
        // In tonnes: the part without the resources that can flow, and per resource
        // the amount that can (a locked tank's counts to Fixed).
        public double Fixed;
        public readonly Dictionary<string, double> Amount = new Dictionary<string, double>();
        // The rocket mode KSP's simulation counts, and an air-breathing mode.
        public SimEngine Rocket;
        public SimEngine Air;
    }

    // The vessel's stages worked out as KSP's delta-v simulation does
    // (docs/development/planner.md), with one difference: engines that breathe air
    // do not burn in the stages. KSP gives them no thrust in vacuum and lets them
    // drain the liquid fuel they reach, which leaves the rockets sharing those tanks
    // nothing. Instead, where the route starts on a body with oxygen, the
    // air-breathing engines lit first burn as the vessel lifts off (`AirDeltaV`, the
    // part of the launch they fly), before any stage: a row of their own, numbered
    // one above the highest stage, taking the liquid fuel they reach at their Isp.
    // The stages then burn what is left, rocket modes only (a RAPIER's closed
    // cycle).
    //
    // The flow follows tools/craft_stages.py, which matches KSP's figures for the
    // stock crafts: a stage drops the parts it decouples, lights the rocket engines
    // activated by then that still reach every propellant, and burns until they
    // are dry or the engines the next stage drops are. A resource is drawn from its
    // own part, from the parts reachable through crossfeed and fuel lines, or from
    // any part, as its flow mode says, and there from the parts dropped soonest,
    // evenly. Worked out from event to event, each time a tank runs dry. Free of
    // Unity, tested.
    internal static class StageSim
    {
        // One stage, or the air-breathing engines' row, as the simulation works it out.
        internal sealed class Row
        {
            // The stage's number, one above the highest stage for the air-breathing row; its masses as it starts and ends, in
            // tonnes; its delta-v, its thrust in kN and its burn time in seconds; whether it is the air-breathing row; whether
            // only solid engines burn in it; and its thrust at each body's surface with air, by the body's name.
            public int Number;
            public double StartMass;
            public double EndMass;
            public double DeltaV;
            public double Thrust;
            public double BurnTime;
            public bool Air;
            public bool Solid;
            public readonly Dictionary<string, double> ThrustAtSurface = new Dictionary<string, double>();
        }

        // The rows worked out, what each part burns in them, and where rocket engines ran dry.
        internal sealed class Result
        {
            // In the order they burn: the air-breathing row first, then the stages from the highest number down.
            public readonly List<Row> Rows = new List<Row>();
            // Per part, by row number: the mass of its resources as the row starts and
            // as it ends, where it burns any.
            public readonly List<Dictionary<int, double[]>> Fuel = new List<Dictionary<int, double[]>>();
            // The row of the air-breathing engines; null for none.
            public Row AirRow;
            // Each time rocket engines run dry in the stages, in order.
            public readonly List<Dry> Dry = new List<Dry>();
        }

        // Rocket engines running dry: the propellants they burn, and what is left
        // then of each resource that can flow, in units: in the parts still attached,
        // and what the parts dropped by then held as they went.
        internal sealed class Dry
        {
            // The propellants of the engines run dry; per resource, what the attached parts still hold and what the dropped
            // parts held.
            public readonly HashSet<string> Propellants = new HashSet<string>();
            public readonly Dictionary<string, double> Left = new Dictionary<string, double>();
            public readonly Dictionary<string, double> Dropped = new Dictionary<string, double>();
        }

        // Standard gravity, for the exhaust speed from Isp; the longest a stage is burnt, in seconds, and the most events
        // worked out per stage, so that a burn that never ends stops; and the amount, in units, that counts as none.
        private const double G0 = StageTable.StandardGravity;
        private const double MaxTime = 100000.0;
        private const int MaxEvents = 5000;
        private const double Empty = 1e-9;

        // The fastest air-breathing engine lit first, by the Mach number it reaches;
        // 0 for none. The engines lit first: those activated with the first stage
        // that lights any engine, as AirPhase takes them.
        internal static double LaunchMach(IList<SimPart> parts)
        {
            int first = int.MinValue;
            foreach (SimPart part in parts)
                if (!part.Clamp && (part.Rocket != null || part.Air != null)) first = Math.Max(first, part.ActivatesAt);
            double mach = 0.0;
            foreach (SimPart part in parts)
                if (!part.Clamp && part.Air != null && part.ActivatesAt >= first) mach = Math.Max(mach, part.Air.Mach);
            return mach;
        }

        // `density`: per resource, in tonnes per unit. `airDeltaV`: what the
        // air-breathing engines lit first fly of the launch, 0 for none. `launchBody`:
        // the body's name, for their thrust there.
        internal static Result Run(IList<SimPart> parts, IDictionary<string, double> density, double airDeltaV, string launchBody)
        {
            Result result = new Result();
            Dictionary<string, double>[] amount = new Dictionary<string, double>[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                amount[i] = new Dictionary<string, double>(parts[i].Amount);
                result.Fuel.Add(new Dictionary<int, double[]>());
            }
            State state = new State(parts, density, amount);

            HashSet<int> attached = new HashSet<int>();
            for (int i = 0; i < parts.Count; i++)
                if (!parts[i].Clamp) attached.Add(i);
            int top = 0;
            foreach (SimPart part in parts) top = Math.Max(top, part.ActivatesAt);

            if (airDeltaV > 0.0) result.AirRow = AirPhase(state, attached, top, airDeltaV, launchBody, result);

            for (int stage = top; stage >= 0; stage--)
            {
                foreach (int i in new List<int>(attached))
                    if (!PartFacts.AttachedIn(parts[i].DecoupleStage, stage)) attached.Remove(i);
                Row row = new Row { Number = stage };
                List<int> active = new List<int>();
                foreach (int i in attached)
                    if (parts[i].Rocket != null && parts[i].ActivatesAt >= stage) active.Add(i);
                active.Sort();
                row.StartMass = state.Mass(attached);
                double[] before = state.FuelOf(attached);
                List<int> lit = active.FindAll(i => state.Feeds(attached, i, parts[i].Rocket));
                List<int> watched = active.FindAll(i => parts[i].DecoupleStage == stage - 1);
                row.Solid = lit.Count > 0;
                foreach (int i in lit)
                {
                    row.Solid &= parts[i].Rocket.Solid;
                    row.Thrust += parts[i].Rocket.Thrust;
                    foreach (KeyValuePair<string, double> body in parts[i].Rocket.ThrustAtSurface)
                    {
                        double sum;
                        row.ThrustAtSurface.TryGetValue(body.Key, out sum);
                        row.ThrustAtSurface[body.Key] = sum + body.Value;
                    }
                }
                double mass = row.StartMass, time = 0.0;
                for (int events = 0; lit.Count > 0 && time < MaxTime && events < MaxEvents; events++)
                {
                    List<Draw> draws = new List<Draw>();
                    double thrust = 0.0, flow = 0.0;
                    foreach (int i in lit)
                    {
                        SimEngine engine = parts[i].Rocket;
                        thrust += engine.Thrust;
                        flow += engine.Thrust / (engine.Isp * G0);
                        state.Demand(attached, i, engine, 1.0, draws);
                    }
                    double step = Math.Min(state.TimeToDry(draws), MaxTime - time);
                    double burnt = state.Take(draws, step);
                    if (burnt > 0.0 && mass - burnt > 0.0) row.DeltaV += thrust / flow * Math.Log(mass / (mass - burnt));
                    mass -= burnt;
                    time += step;
                    List<int> still = lit.FindAll(i => state.Feeds(attached, i, parts[i].Rocket));
                    if (still.Count < lit.Count) result.Dry.Add(state.DryOf(lit.FindAll(i => !still.Contains(i)), attached));
                    lit = still;
                    if (watched.Count > 0 && !watched.Exists(i => lit.Contains(i))) break;
                }
                row.EndMass = state.Mass(attached);
                row.BurnTime = time;
                if (row.DeltaV <= 0.0) row.Solid = false;
                Record(result, attached, before, state.FuelOf(attached), stage);
                result.Rows.Add(row);
            }
            return result;
        }

        // The air-breathing engines lit first, on whatever is attached as the vessel
        // starts, burning at their Isp until they have flown `airDeltaV` or run dry.
        private static Row AirPhase(State state, HashSet<int> attached, int top, double airDeltaV, string launchBody, Result result)
        {
            IList<SimPart> parts = state.Parts;
            int first = int.MinValue;
            foreach (int i in attached)
                if (parts[i].Rocket != null || parts[i].Air != null) first = Math.Max(first, parts[i].ActivatesAt);
            List<int> lit = new List<int>();
            foreach (int i in attached)
                if (parts[i].Air != null && parts[i].ActivatesAt >= first && state.Feeds(attached, i, parts[i].Air)) lit.Add(i);
            if (lit.Count == 0) return null;
            lit.Sort();

            Row row = new Row { Number = top + 1, Air = true };
            double thrust = 0.0, flow = 0.0;
            foreach (int i in lit)
            {
                SimEngine engine = parts[i].Air;
                thrust += engine.Thrust;
                if (engine.Isp > 0.0) flow += engine.Thrust / (engine.Isp * G0);
            }
            row.Thrust = thrust;
            if (launchBody != null) row.ThrustAtSurface[launchBody] = thrust;
            row.StartMass = state.Mass(attached);
            if (flow <= 0.0) return null;
            double exhaust = thrust / flow;
            // The propellant the launch's share takes at that exhaust speed.
            double wanted = row.StartMass * (1.0 - Math.Exp(-airDeltaV / exhaust));
            double[] before = state.FuelOf(attached);
            double mass = row.StartMass, burnt = 0.0;
            for (int events = 0; burnt < wanted - Empty && lit.Count > 0 && events < MaxEvents; events++)
            {
                List<Draw> draws = new List<Draw>();
                foreach (int i in lit) state.Demand(attached, i, parts[i].Air, 1.0, draws);
                double rate = 0.0;
                foreach (Draw draw in draws) rate += draw.Rate * state.Density(draw.Resource);
                if (rate <= 0.0) break;
                double step = Math.Min(state.TimeToDry(draws), (wanted - burnt) / rate);
                burnt += state.Take(draws, step);
                row.BurnTime += step;
                lit = lit.FindAll(i => state.Feeds(attached, i, parts[i].Air));
            }
            row.EndMass = state.Mass(attached);
            row.DeltaV = row.EndMass > 0.0 && row.StartMass > row.EndMass ? exhaust * Math.Log(row.StartMass / row.EndMass) : 0.0;
            Record(result, attached, before, state.FuelOf(attached), row.Number);
            result.Rows.Add(row);
            return row;
        }

        // For each attached part whose resources lost mass, that mass before and after, under the row's number.
        private static void Record(Result result, HashSet<int> attached, double[] before, double[] after, int number)
        {
            foreach (int i in attached)
                if (before[i] - after[i] > 1e-6) result.Fuel[i][number] = new[] { before[i], after[i] };
        }

        // One resource drawn from one part, in units per second.
        private sealed class Draw
        {
            // The part's index, the resource's name, and the units drawn per second.
            public int Part;
            public string Resource;
            public double Rate;
        }

        // What each part holds as the simulation goes on, and how resources flow to the engines.
        private sealed class State
        {
            // The parts; per resource, its density in tonnes per unit; per part, the units of each resource it holds now.
            internal readonly IList<SimPart> Parts;
            private readonly IDictionary<string, double> density;
            private readonly Dictionary<string, double>[] amount;

            // Over the parts, holding `amount`, which it changes as the engines burn.
            internal State(IList<SimPart> parts, IDictionary<string, double> density, Dictionary<string, double>[] amount)
            {
                Parts = parts;
                this.density = density;
                this.amount = amount;
            }

            // Tonnes per unit; 0 for a resource of no mass or not known.
            internal double Density(string resource)
            {
                double value;
                return density.TryGetValue(resource, out value) ? value : 0.0;
            }

            // The units of the resource the part holds now.
            private double Held(int part, string resource)
            {
                double value;
                return amount[part].TryGetValue(resource, out value) ? value : 0.0;
            }

            // What the attached parts weigh now, in tonnes: their fixed mass and their resources.
            internal double Mass(HashSet<int> attached)
            {
                double total = 0.0;
                foreach (int i in attached)
                {
                    total += Parts[i].Fixed;
                    foreach (KeyValuePair<string, double> held in amount[i]) total += held.Value * Density(held.Key);
                }
                return total;
            }

            // Per part, the mass of its resources now, in tonnes; 0 for parts not attached.
            internal double[] FuelOf(HashSet<int> attached)
            {
                double[] fuel = new double[Parts.Count];
                foreach (int i in attached)
                    foreach (KeyValuePair<string, double> held in amount[i]) fuel[i] += held.Value * Density(held.Key);
                return fuel;
            }

            // What is recorded as the engines run dry: their propellants, and what is left of each resource, attached and dropped.
            internal Dry DryOf(List<int> engines, HashSet<int> attached)
            {
                Dry dry = new Dry();
                foreach (int i in engines)
                    foreach (SimPropellant propellant in Parts[i].Rocket.Propellants) dry.Propellants.Add(propellant.Name);
                for (int i = 0; i < Parts.Count; i++)
                {
                    if (Parts[i].Clamp) continue;
                    Dictionary<string, double> into = attached.Contains(i) ? dry.Left : dry.Dropped;
                    foreach (KeyValuePair<string, double> held in amount[i])
                    {
                        double sum;
                        into.TryGetValue(held.Key, out sum);
                        into[held.Key] = sum + held.Value;
                    }
                }
                return dry;
            }

            // Every propellant reaches the engine.
            internal bool Feeds(HashSet<int> attached, int part, SimEngine engine)
            {
                if (engine.Propellants.Count == 0) return false;
                foreach (SimPropellant propellant in engine.Propellants)
                    if (Sources(attached, part, propellant).Count == 0) return false;
                return true;
            }

            // The engine's draw at full thrust times `share`, split evenly over its
            // sources: its mass flow over its propellants by ratio and density.
            internal void Demand(HashSet<int> attached, int part, SimEngine engine, double share, List<Draw> draws)
            {
                if (engine.Isp <= 0.0) return;
                double flow = engine.Thrust * share / (engine.Isp * G0);
                double total = 0.0;
                foreach (SimPropellant propellant in engine.Propellants) total += propellant.Ratio * Density(propellant.Name);
                if (total <= 0.0) return;
                foreach (SimPropellant propellant in engine.Propellants)
                {
                    if (Density(propellant.Name) <= 0.0) continue;
                    List<int> pool = Sources(attached, part, propellant);
                    if (pool.Count == 0) continue;
                    double units = flow * propellant.Ratio / total / pool.Count;
                    foreach (int source in pool)
                    {
                        Draw draw = draws.Find(d => d.Part == source && d.Resource == propellant.Name);
                        if (draw == null) draws.Add(draw = new Draw { Part = source, Resource = propellant.Name });
                        draw.Rate += units;
                    }
                }
            }

            // The seconds until the first of the drawn parts runs dry of its resource; 0 where nothing is drawn.
            internal double TimeToDry(List<Draw> draws)
            {
                double time = double.MaxValue;
                foreach (Draw draw in draws)
                    if (draw.Rate > 0.0) time = Math.Min(time, Held(draw.Part, draw.Resource) / draw.Rate);
                return time == double.MaxValue ? 0.0 : time;
            }

            // Burns `seconds` of the draws; the mass burnt, in tonnes.
            internal double Take(List<Draw> draws, double seconds)
            {
                double burnt = 0.0;
                foreach (Draw draw in draws)
                {
                    double held = Held(draw.Part, draw.Resource);
                    double take = Math.Min(held, draw.Rate * seconds);
                    amount[draw.Part][draw.Resource] = held - take < Empty ? 0.0 : held - take;
                    burnt += take * Density(draw.Resource);
                }
                return burnt;
            }

            // Where the propellant comes from: of the parts it can flow from that hold
            // any, those dropped soonest.
            internal List<int> Sources(HashSet<int> attached, int part, SimPropellant propellant)
            {
                List<int> pool = new List<int>();
                if (propellant.Flow == SimFlow.Own) pool.Add(part);
                else if (propellant.Flow == SimFlow.Stack) pool.AddRange(Reachable(attached, part));
                else pool.AddRange(attached);
                pool = pool.FindAll(i => Held(i, propellant.Name) > Empty);
                if (pool.Count == 0) return pool;
                int top = int.MinValue;
                foreach (int i in pool) top = Math.Max(top, Parts[i].DecoupleStage);
                return pool.FindAll(i => Parts[i].DecoupleStage == top);
            }

            // The part itself and every attached part reached from it through parts
            // that let fuel cross, along the part tree and the fuel lines.
            private List<int> Reachable(HashSet<int> attached, int start)
            {
                HashSet<int> seen = new HashSet<int> { start };
                Stack<int> todo = new Stack<int>();
                todo.Push(start);
                while (todo.Count > 0)
                {
                    int at = todo.Pop();
                    SimPart part = Parts[at];
                    if (at != start && !part.Crossfeed) continue;
                    List<int> near = new List<int>(part.Children);
                    if (part.Parent >= 0) near.Add(part.Parent);
                    near.AddRange(part.FuelLineSources);
                    foreach (int other in near)
                    {
                        if (other < 0 || other >= Parts.Count || seen.Contains(other) || Parts[other].Clamp) continue;
                        seen.Add(other);
                        todo.Push(other);
                    }
                }
                List<int> reached = new List<int>();
                foreach (int i in seen)
                    if (attached.Contains(i)) reached.Add(i);
                return reached;
            }
        }
    }

    // The air-breathing engines' part of the launch (docs/development/planner.md),
    // and the planner's stages with them. Free of Unity, tested.
    internal static class AirLaunch
    {
        // The pressure, as a share of the body's at sea level, the speed of sound is
        // taken at (PlannerBody.SpeedOfSound): about where jets are left behind for
        // rockets, some 20 km up on Kerbin.
        internal const double HighAir = 0.05;
        // How far and in what steps the thrust curve is searched, and the peak's Mach below which an engine is held to Mach 1.
        private const double LastMach = 10.0;
        private const double MachStep = 0.01;
        private const double TransonicPeak = 1.5;

        // How fast an air-breathing engine takes the vessel, as a Mach number, from
        // its thrust over Mach (ModuleEngines.velCurve): where the thrust falls to
        // half its peak, past the peak, else where the curve ends. Drag rises steeply through Mach 1, which an
        // engine whose thrust peaks below Mach 1.5 does not push through: Mach 1 at
        // most there.
        internal static double MachLimit(Func<double, double> thrustOverMach)
        {
            double peak = 0.0, peakAt = 0.0;
            for (double mach = 0.0; mach <= LastMach; mach += MachStep)
            {
                double value = thrustOverMach(mach);
                if (value > peak)
                {
                    peak = value;
                    peakAt = mach;
                }
            }
            if (peak <= 0.0) return 0.0;
            // Where it never falls to half: where the curve ends, its last change.
            double limit = peakAt;
            double last = peak;
            for (double mach = peakAt + MachStep; mach <= LastMach; mach += MachStep)
            {
                double value = thrustOverMach(mach);
                if (value < peak / 2.0)
                {
                    limit = mach - MachStep * (peak / 2.0 - value) / Math.Max(last - value, 1e-9);
                    break;
                }
                if (Math.Abs(value - last) > 1e-6) limit = mach;
                last = value;
            }
            return peakAt < TransonicPeak ? Math.Min(limit, 1.0) : limit;
        }

        // The part of the launch into the body's low orbit (RoutePlanner.Launch) the
        // air-breathing engines fly: in the share of the orbital speed (less the
        // ground's own) they reach. 0 where the air holds no oxygen.
        internal static double Share(PlannerBody body, double mach)
        {
            if (body == null || !body.Oxygen || mach <= 0.0 || body.SpeedOfSound <= 0.0) return 0.0;
            double orbital = RoutePlanner.Circular(body.Mu, body.Radius + body.LowOrbitAltitude);
            double rotation = body.RotationPeriod > 0.0 ? 2.0 * Math.PI * body.Radius / body.RotationPeriod : 0.0;
            double reference = orbital - rotation;
            if (reference <= 0.0) return 0.0;
            return RoutePlanner.Launch(body) * Math.Min(1.0, mach * body.SpeedOfSound / reference);
        }

        // The stages the planner budgets with. KSP's (`rows`) where no engine breathes
        // air; otherwise StageSim's, with the air-breathing engines' row where the
        // route lifts off from `launch` (null where it does not start on a surface),
        // the parts' fuel per stage (PartFacts.StageFuel) rewritten to match.
        // `airRow`: its number, -1 for none.
        internal static List<StageRow> Plan(IList<StageRow> rows, IList<PartFacts> facts, IList<SimPart> sim,
                                            IDictionary<string, double> density, PlannerBody launch, string launchId, out int airRow)
        {
            airRow = -1;
            if (sim == null || density == null || !sim.Any(part => part.Air != null)) return new List<StageRow>(rows);
            foreach (SimPart part in sim)
            {
                double thrust;
                if (part.Air != null && launchId != null && part.Air.ThrustAtSurface.TryGetValue(launchId, out thrust)) part.Air.Thrust = thrust;
            }
            double share = Share(launch, StageSim.LaunchMach(sim));
            StageSim.Result result = StageSim.Run(sim, density, share, launchId);

            List<StageRow> planned = new List<StageRow>();
            foreach (StageSim.Row simRow in result.Rows)
            {
                StageRow ksp = null;
                foreach (StageRow row in rows)
                    if (row.Number == simRow.Number) ksp = row;
                StageRow planRow = ksp != null && !simRow.Air ? ksp.Copy() : new StageRow();
                planRow.Number = simRow.Number;
                planRow.StartMass = simRow.StartMass;
                planRow.EndMass = simRow.EndMass;
                planRow.DeltaV = planRow.DeltaVVacuum = simRow.DeltaV;
                planRow.Thrust = planRow.ThrustVacuum = simRow.Thrust;
                planRow.BurnTime = simRow.BurnTime;
                planRow.Solid = simRow.Solid;
                planRow.Air = simRow.Air;
                planRow.ThrustAtSurface = new Dictionary<string, double>(simRow.ThrustAtSurface);
                planned.Add(planRow);
                if (simRow.Air) airRow = simRow.Number;
            }
            for (int i = 0; i < facts.Count && i < result.Fuel.Count; i++)
            {
                facts[i].StageFuel.Clear();
                foreach (KeyValuePair<int, double[]> stage in result.Fuel[i]) facts[i].StageFuel[stage.Key] = stage.Value;
            }
            return planned;
        }
    }
}
