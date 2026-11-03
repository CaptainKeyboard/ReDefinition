using System;
using System.Collections.Generic;
using System.Globalization;
using static ReDefinition.Engineer.PlanText;

namespace ReDefinition.Engineer
{
    // The planner's route and what the player sets on it: the stops, their bodies and altitudes, the stages kept or dropped on a step, the
    // refills, the split burns and the margin. Each command changes the plan and calls Changed.
    internal sealed partial class PlannerModel
    {
        // Whether the route has a stop at `index`.
        internal bool Shown(int index)
        {
            return Ready() && index < State.Stops.Count;
        }

        // The stops up to the first whose body is not picked yet: the route so far.
        internal List<RouteStop> PickedStops()
        {
            List<RouteStop> picked = new List<RouteStop>();
            foreach (RouteStop stop in State.Stops)
            {
                if (stop.Body < 0) break;
                picked.Add(stop);
            }
            return picked;
        }

        // Whether the stop at `index` has a body picked.
        internal bool HasBody(int index)
        {
            return Shown(index) && State.Stops[index].Body >= 0 && State.Stops[index].Body < Bodies.Bodies.Count;
        }

        // Whether a destination can be added at the end of the route: once every destination has its body.
        internal bool CanAddStop()
        {
            return Ready() && State.Stops.Count < HardStops && PickedStops().Count == State.Stops.Count;
        }

        // The stop's body's name, or "Destination" as a problem where none is picked.
        internal string BodyName(int index)
        {
            if (!Shown(index)) return "";
            int body = State.Stops[index].Body;
            return body >= 0 && body < Bodies.Bodies.Count ? Bodies.Bodies[body].Name : "<color=" + Problem + ">Destination</color>";
        }

        // The stop's kind; an orbit where there is no such stop.
        internal StopKind Kind(int index)
        {
            return Shown(index) ? State.Stops[index].Kind : StopKind.Orbit;
        }

        // Whether the stop's body has air.
        internal bool HasAtmosphere(int index)
        {
            return HasBody(index) && Bodies.Bodies[State.Stops[index].Body].HasAtmosphere;
        }

        // Whether arriving at the stop uses its body's air.
        internal bool UsesAtmosphere(int index)
        {
            return Shown(index) && State.Stops[index].UseAtmosphere && HasAtmosphere(index);
        }

        // The stop's altitude in whole kilometres, as the field shows it.
        internal string AltitudeKm(int index)
        {
            return Shown(index) ? (State.Stops[index].Altitude / 1000.0).ToString("0", CultureInfo.InvariantCulture) : "";
        }

        // "C  Kerbin orbit, 2,863 km".
        internal string StopText(int index)
        {
            RouteStop stop = State.Stops[index];
            if (!HasBody(index)) return Letter(index) + "  no destination yet";
            string text = Letter(index) + "  " + RouteSteps.Place(Bodies.Bodies, stop);
            if (stop.Kind == StopKind.Orbit) text += ", " + AltitudeKm(index) + " km";
            return text;
        }

        // Typed in kilometres, kept between the lowest safe orbit and the edge of the
        // body's sphere of influence.
        internal void SetAltitude(int index, string text)
        {
            double km;
            if (!HasBody(index) || !double.TryParse((text ?? "").Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out km)) return;
            RouteStop stop = State.Stops[index];
            double altitude = Math.Max(Bodies.Bodies[stop.Body].LowOrbitAltitude, Math.Min(km * 1000.0, Bodies.MaxAltitude(stop.Body)));
            if (Math.Abs(altitude - stop.Altitude) < 0.5) return;
            stop.Altitude = altitude;
            Changed();
        }

        // The bodies, each moon under its planet.
        internal List<Choice> BodyChoices(int index)
        {
            List<Choice> choices = new List<Choice>();
            if (!Shown(index)) return choices;
            foreach (int body in Bodies.Order)
            {
                int picked = body;
                int depth = 0;
                for (int at = Bodies.Bodies[body].Parent; at >= 0 && depth < 8; at = Bodies.Bodies[at].Parent) depth++;
                choices.Add(new Choice
                {
                    Label = Bodies.Bodies[body].Name, Current = body == State.Stops[index].Body, Indent = depth * 12f,
                    Pick = () => SetBody(index, picked),
                });
            }
            return choices;
        }

        // The stop goes to `body`, at its low orbit; without air, the way down does not use it.
        private void SetBody(int index, int body)
        {
            if (!Shown(index) || body < 0 || body >= Bodies.Bodies.Count) return;
            RouteStop stop = State.Stops[index];
            stop.Body = body;
            stop.Altitude = Bodies.Bodies[body].LowOrbitAltitude;
            if (!Bodies.Bodies[body].HasAtmosphere) stop.UseAtmosphere = false;
            AutoChutes(index);
            Changed();
        }

        // The stop becomes a surface or an orbit; an orbit with no altitude takes the body's low orbit.
        internal void SetKind(int index, StopKind kind)
        {
            if (!Shown(index) || State.Stops[index].Kind == kind) return;
            State.Stops[index].Kind = kind;
            if (kind == StopKind.Orbit && State.Stops[index].Altitude <= 0.0 && HasBody(index))
                State.Stops[index].Altitude = Bodies.Bodies[State.Stops[index].Body].LowOrbitAltitude;
            AutoChutes(index);
            Changed();
        }

        // One AltitudeStep up (1) or down (-1), kept between the low orbit and the edge of the sphere of influence.
        internal void StepAltitude(int index, int direction)
        {
            if (!HasBody(index)) return;
            RouteStop stop = State.Stops[index];
            double lowest = Bodies.Bodies[stop.Body].LowOrbitAltitude;
            double altitude = stop.Altitude + direction * AltitudeStep;
            stop.Altitude = Math.Max(lowest, Math.Min(altitude, Bodies.MaxAltitude(stop.Body)));
            Changed();
        }

        // The last destination, a surface with air: home on the parachutes, where
        // what lands there has any, the capsule's where there is one.
        private void AutoChutes(int index)
        {
            if (!HasBody(index)) return;
            RouteStop stop = State.Stops[index];
            if (index != State.Stops.Count - 1 || index == 0 || stop.Kind != StopKind.Surface
                || !Bodies.Bodies[stop.Body].HasAtmosphere) return;
            if (Sections.CapsuleCarries(part => part.Parachute)) stop.UseAtmosphere = true;
        }

        // Arriving at the stop uses its body's air, or no longer does.
        internal void ToggleAtmosphere(int index)
        {
            if (!Shown(index) || !HasAtmosphere(index)) return;
            State.Stops[index].UseAtmosphere = !State.Stops[index].UseAtmosphere;
            Changed();
        }

        // A new stop where the route ends: the same body, in its low orbit.
        internal void AddStop()
        {
            if (!Ready() || State.Stops.Count >= HardStops) return;
            RouteStop last = State.Stops[State.Stops.Count - 1];
            if (last.Body < 0) return;
            State.Stops.Add(new RouteStop
            {
                Body = last.Body, Kind = StopKind.Orbit, Altitude = Bodies.Bodies[last.Body].LowOrbitAltitude,
            });
            Changed();
        }

        // Takes the stop out of the route, keeping two at least.
        internal void RemoveStop(int index)
        {
            if (!Shown(index) || State.Stops.Count <= 2) return;
            // The list of bodies is for a stop that may be another one now.
            CloseLists();
            State.Stops.RemoveAt(index);
            State.RemoveStop(index);
            if (StopsReshaped != null) StopsReshaped();
            Changed();
        }

        // ------------------------------------------------------------- stages

        // The stages the player keeps from a step, or drops after it (StagePlan, kept
        // on the stop the step leads to, by role).
        private StagePlan PlanOf(int step)
        {
            return step >= 0 && step < Steps.Count ? State.Stops[Steps[step].Goal].Stages[(int)Steps[step].Role] : null;
        }

        // Whether the player saved the stage on the step.
        internal bool Saved(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            return plan != null && plan.Save.Contains(number);
        }

        // Whether the player drops the stage after the step.
        internal bool DropsAfter(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            return plan != null && plan.DropAfter.Contains(number);
        }

        // Saves the stage on the step, or no longer does.
        internal void ToggleSaved(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            if (plan == null || number < 0) return;
            if (!plan.Save.Remove(number)) plan.Save.Add(number);
            Changed();
        }

        // Drops the stage after the step, or no longer does.
        internal void ToggleDropAfter(int step, int number)
        {
            StagePlan plan = PlanOf(step);
            if (plan == null || number < 0) return;
            if (!plan.DropAfter.Remove(number)) plan.DropAfter.Add(number);
            Changed();
        }

        // ------------------------------------------------------------- refills

        // The rest's stages that hold fuel as the step ends, for its refill.
        private List<int> Tankers(int leg, StepRole role)
        {
            int index = Slot(leg, role);
            return index >= 0 && Budget.Tankers[index] != null ? Budget.Tankers[index] : new List<int>();
        }

        // "from S3", "from the rest": where a refill comes from first; in orange a stage
        // that holds nothing then, so that the rest gives it in KSP's order.
        internal string TankerText(int leg, StepRole role)
        {
            if (!Shown(leg + 1)) return "";
            int stage = State.Stops[leg + 1].RefuelFrom[(int)role];
            if (stage < 0) return "<color=" + Good + ">from the rest</color>";
            if (!Tankers(leg, role).Contains(stage)) return "<color=" + Warning + ">" + StageLabel(stage) + " is empty</color>";
            return "<b><u><color=" + Good + ">from S" + stage + "</color></u></b>";
        }

        // The list a refill's first source is picked from: the rest in KSP's order, then each of the rest's stages with fuel.
        internal List<Choice> TankerChoices(int leg, StepRole role)
        {
            if (!Shown(leg + 1)) return new List<Choice>();
            int[] from = State.Stops[leg + 1].RefuelFrom;
            List<Choice> choices = new List<Choice>
            {
                new Choice
                {
                    Label = "Rest, in staging order", Tooltip = "Refuel from the rest of the vessel's stages in the order KSP fires them.",
                    Current = from[(int)role] < 0, Pick = () => SetTanker(leg, role, -1),
                },
            };
            foreach (int tanker in Tankers(leg, role))
            {
                int number = tanker;
                choices.Add(new Choice
                {
                    Label = StageLabel(number), Tooltip = "Refill from this stage first, then from the others.",
                    Current = from[(int)role] == number, Pick = () => SetTanker(leg, role, number),
                });
            }
            return choices;
        }

        // The refill on the leg's step in that role comes first from `stage`, -1 for the rest in KSP's order.
        private void SetTanker(int leg, StepRole role, int stage)
        {
            if (!Shown(leg + 1) || State.Stops[leg + 1].RefuelFrom[(int)role] == stage) return;
            State.Stops[leg + 1].RefuelFrom[(int)role] = stage;
            Changed();
        }

        // A section refilled from the rest when it docks at the end of the step.
        internal bool Refuels(int leg, StepRole role)
        {
            return Shown(leg + 1) && State.Stops[leg + 1].Refuel[(int)role];
        }

        // The section docking at the end of the step is refilled from the rest, or no longer is.
        internal void ToggleRefuel(int leg, StepRole role)
        {
            if (!Shown(leg + 1)) return;
            bool[] refuel = State.Stops[leg + 1].Refuel;
            refuel[(int)role] = !refuel[(int)role];
            Changed();
        }

        // ------------------------------------------------------------- the way across and the way down

        // Whether the way across to the stop is two steps, the burn that leaves and the one that arrives.
        internal bool SplitsBurns(int index)
        {
            return Shown(index) && State.Stops[index].SplitBurns;
        }

        // Splits the way across to the stop into two steps, or joins them.
        internal void SetSplitBurns(int index, bool split)
        {
            if (!Shown(index) || State.Stops[index].SplitBurns == split) return;
            State.Stops[index].SplitBurns = split;
            Changed();
        }

        // Whether the way down to the stop goes into an orbit first rather than straight into the air.
        internal bool OrbitFirst(int index)
        {
            return Shown(index) && State.Stops[index].OrbitFirst;
        }

        // Has the way down to the stop go into an orbit first, or straight into the air.
        internal void SetOrbitFirst(int index, bool orbit)
        {
            if (!Shown(index) || State.Stops[index].OrbitFirst == orbit) return;
            State.Stops[index].OrbitFirst = orbit;
            Changed();
        }

        // The margin, from the slider's per cent.
        internal void SetMargin(float percent)
        {
            double margin = Math.Round(percent) / 100.0;
            if (!Ready() || Math.Abs(margin - State.Margin) < 1e-6) return;
            State.Margin = margin;
            Changed();
        }
    }
}
