using System.Collections.Generic;
using System;

namespace ReDefinition.Engineer
{
    // A body as the planner needs it, read from the game (PlannerBodies), so that
    // the planner works for any system Kopernicus loads. Free of Unity.
    internal sealed class PlannerBody
    {
        // As the player reads it, and as the game knows it whatever the
        // language (CelestialBody.bodyName): plans keep the second.
        public string Name;
        public string Id;
        // Its parent's index in the list, -1 for the star.
        public int Parent = -1;
        public double Mu;
        public double Radius;
        public double RotationPeriod;
        public double AtmosphereDepth;
        // At sea level, in atmospheres.
        public double SurfacePressure;
        public double SemiMajorAxis;
        // Degrees, against the parent's reference plane.
        public double Inclination;
        public double SphereOfInfluence = double.PositiveInfinity;
        // Above the atmosphere or the highest ground.
        public double LowOrbitAltitude;

        public bool HasAtmosphere
        {
            get { return AtmosphereDepth > 0.0; }
        }
    }

    // Entry is no destination the player picks: the vessel on its way down into a
    // body's air, with no orbit, where the rest of it is left before a lander
    // lands with parachutes (RouteSteps).
    internal enum StopKind
    {
        Surface,
        Orbit,
        Entry,
    }

    // Who flies a step (RouteSteps): the whole vessel, the lander alone while the
    // rest waits, or the rest without the lander, which stays behind for good.
    internal enum Vehicle
    {
        Ship,
        Lander,
        Rest,
    }

    // One point of the route: a body, on its surface or in a circular orbit.
    internal sealed class RouteStop
    {
        public int Body;
        public StopKind Kind = StopKind.Orbit;
        // Above the surface, for an orbit.
        public double Altitude;
        // Arriving here uses the body's atmosphere: aerobraking into the orbit, or
        // parachutes and heat shield for a landing.
        public bool UseAtmosphere;
        // For each step on the way here (by StepRole, RouteSteps): who flies it, as a
        // Vehicle, -1 where the planner works it out; and whether the lander is
        // refilled from the rest's tanks when it docks at the step's end.
        public int[] By = { -1, -1, -1 };
        public bool[] Refuel = new bool[3];
        // On the way to a landing on parachutes: into an orbit first, rather than
        // straight into the air.
        public bool OrbitFirst;
        // A payload stays here (StageBudget).
        public bool LeavesPayload;

        public RouteStop Copy()
        {
            RouteStop copy = (RouteStop)MemberwiseClone();
            copy.By = (int[])By.Clone();
            copy.Refuel = (bool[])Refuel.Clone();
            return copy;
        }
    }

    internal sealed class LegLine
    {
        public string Name;
        public double DeltaV;
    }

    // What going from one stop to the next costs, burn by burn.
    internal sealed class Leg
    {
        public int From;
        public int To;
        public readonly List<LegLine> Lines = new List<LegLine>();

        public double DeltaV
        {
            get
            {
                double total = 0.0;
                foreach (LegLine line in Lines) total += line.DeltaV;
                return total;
            }
        }

        internal void Add(string name, double deltaV)
        {
            if (deltaV > 0.5) Lines.Add(new LegLine { Name = name, DeltaV = deltaV });
        }
    }

    // The delta-v of a route, from the bodies' own gravity, size, rotation,
    // atmosphere and orbits (docs/development/planner.md). Transfers are Hohmann
    // transfers between circular orbits, with patched conics across spheres of
    // influence: a burn from a low orbit leaves with the excess speed the transfer
    // around the common parent needs, and one at the far end captures from the
    // excess speed it arrives with. Launches and landings are estimates. Free of
    // Unity, tested.
    internal static class RoutePlanner
    {
        // Launch and landing: 7 % over the low orbit's speed for the turn and the
        // gravity lost on the way up, less what the ground's rotation gives, and
        // in an atmosphere what its drag and the steeper climb take:
        // sqrt(g * depth) grown with the air's density. Fitted to the stock
        // system's known figures: Kerbin 3,400 m/s, Duna 1,450, Eve 8,000,
        // Laythe 2,900, the Mun 580, Tylo 2,270. Each is within 4 % (PlannerTests).
        private const double TurnFactor = 1.07;
        private const double VacuumAllowance = 10.0;
        private const double AtmosphereLoss = 1.33;
        private const double DenseAtmosphereLoss = 0.0964;
        // Below this pressure a parachute landing still needs a powered end.
        private const double ThinAtmosphere = 0.2;
        private const double ThinLandingShare = 0.3;
        // What is left after aerobraking: raising the periapsis out of the air.
        private const double AerobrakeShare = 0.03;
        // Where an aerobraking pass dips to, as a share of the atmosphere's depth.
        private const double AerobrakeDepth = 0.5;

        internal static List<Leg> Legs(IList<PlannerBody> bodies, IList<RouteStop> stops, bool planeChanges)
        {
            List<Leg> legs = new List<Leg>();
            for (int i = 0; i + 1 < stops.Count; i++) legs.Add(LegOf(bodies, stops[i], stops[i + 1], i, planeChanges));
            return legs;
        }

        internal static Leg LegOf(IList<PlannerBody> bodies, RouteStop from, RouteStop to, int index, bool planeChanges)
        {
            Leg leg = new Leg { From = index, To = index + 1 };
            PlannerBody start = bodies[from.Body];
            PlannerBody end = bodies[to.Body];

            double departRadius = OrbitRadius(start, from);
            if (from.Kind == StopKind.Surface) leg.Add("Launch to " + Km(start.LowOrbitAltitude) + " orbit", Launch(start));

            double arriveRadius = OrbitRadius(end, to);

            // On the way down into the air: the landing is all that is left.
            if (from.Kind == StopKind.Entry && from.Body == to.Body)
            {
                if (to.Kind == StopKind.Surface) leg.Add("Landing", Landing(end, to.UseAtmosphere));
                return leg;
            }
            if (to.Kind == StopKind.Entry && from.Body == to.Body)
            {
                // From an orbit down to a periapsis in the air.
                double deorbit, unused;
                Hohmann(start.Mu, departRadius, end.Radius + end.AtmosphereDepth * AerobrakeDepth, out deorbit, out unused);
                leg.Add("Deorbit into the air", deorbit);
                return leg;
            }

            if (from.Body == to.Body)
            {
                if (from.Kind == StopKind.Surface && to.Kind == StopKind.Surface)
                {
                    leg.Add("Landing", Landing(end, to.UseAtmosphere));
                    return leg;
                }
                double first, second;
                if (to.Kind == StopKind.Surface && to.UseAtmosphere && end.HasAtmosphere)
                {
                    // Down from the orbit into the air, then on the parachutes.
                    Hohmann(start.Mu, departRadius, end.Radius + end.AtmosphereDepth * AerobrakeDepth, out first, out second);
                    leg.Add("Deorbit into the air", first);
                    leg.Add("Landing", Landing(end, true));
                    return leg;
                }
                if (to.Kind == StopKind.Orbit && to.UseAtmosphere && end.HasAtmosphere && arriveRadius < departRadius)
                {
                    // Down into the upper air, then out of it at the new orbit.
                    Hohmann(start.Mu, departRadius, end.Radius + end.AtmosphereDepth * AerobrakeDepth, out first, out second);
                    leg.Add("Aerobraking, then circularise", first + Circular(end.Mu, arriveRadius) * AerobrakeShare);
                    return leg;
                }
                Hohmann(start.Mu, departRadius, arriveRadius, out first, out second);
                leg.Add("Raise or lower the orbit", first + second);
                if (to.Kind == StopKind.Surface) leg.Add("Landing", Landing(end, to.UseAtmosphere));
                return leg;
            }

            List<int> up = Chain(bodies, from.Body);
            List<int> down = Chain(bodies, to.Body);
            int common = CommonAncestor(up, down);
            if (common < 0)
            {
                leg.Add("No route between these bodies", 0.0);
                return leg;
            }

            // Around the common parent: from the start side's orbit to the end
            // side's: a moon's orbit, or the low orbit where a body is the parent.
            int startChild = ChildOn(up, common);
            int endChild = ChildOn(down, common);
            PlannerBody parent = bodies[common];
            double inner = startChild < 0 ? departRadius : bodies[startChild].SemiMajorAxis;
            double outer = endChild < 0 ? arriveRadius : bodies[endChild].SemiMajorAxis;
            double transferA = (inner + outer) / 2.0;
            double departSpeed = VisViva(parent.Mu, inner, transferA);
            double arriveSpeed = VisViva(parent.Mu, outer, transferA);

            if (startChild < 0)
                leg.Add("Transfer burn", Math.Abs(departSpeed - Circular(parent.Mu, inner)));
            else
                leg.Add("Transfer burn", Leave(bodies, up, startChild, from.Body, departRadius,
                    Math.Abs(departSpeed - Circular(parent.Mu, inner))));

            if (to.Kind == StopKind.Entry)
            {
                // Straight into the air: nothing to capture.
            }
            else if (endChild < 0)
            {
                double capture = Math.Abs(Circular(parent.Mu, outer) - arriveSpeed);
                leg.Add(to.UseAtmosphere && end.HasAtmosphere ? "Aerobraking, then circularise" : "Capture into orbit",
                    to.UseAtmosphere && end.HasAtmosphere ? Circular(parent.Mu, outer) * AerobrakeShare : capture);
            }
            else
            {
                double excess = Math.Abs(Circular(parent.Mu, outer) - arriveSpeed);
                double capture = Leave(bodies, down, endChild, to.Body, arriveRadius, excess);
                leg.Add(to.UseAtmosphere && end.HasAtmosphere ? "Aerobraking, then circularise" : "Capture into orbit",
                    to.UseAtmosphere && end.HasAtmosphere ? Circular(end.Mu, arriveRadius) * AerobrakeShare : capture);
            }

            // Between two children of the common parent, the tilt between their
            // orbits; between a body and its own moon, the moon's tilt against
            // the low orbit over the body's equator.
            if (planeChanges && (startChild >= 0 || endChild >= 0))
            {
                double startTilt = startChild >= 0 ? bodies[startChild].Inclination : 0.0;
                double endTilt = endChild >= 0 ? bodies[endChild].Inclination : 0.0;
                double tilt = Math.Abs(startTilt - endTilt) * Math.PI / 180.0;
                double slowest = Math.Min(departSpeed, arriveSpeed);
                leg.Add("Plane change", 2.0 * slowest * Math.Sin(tilt / 2.0));
            }

            if (to.Kind == StopKind.Surface) leg.Add("Landing", Landing(end, to.UseAtmosphere));
            return leg;
        }

        // The burn in a low orbit of `body` that leaves it, and every parent up
        // to `top`, with `excess` over `top`'s own speed around the common
        // parent. The same burn, the other way round, captures on arrival.
        private static double Leave(IList<PlannerBody> bodies, List<int> chain, int top, int body, double radius, double excess)
        {
            int at = chain.IndexOf(top);
            double speed = excess;
            // From the child of the common parent down to the body: at each level
            // the craft must pass the next child's orbit with this much over it.
            for (int i = at; i > 0; i--)
            {
                PlannerBody level = bodies[chain[i]];
                PlannerBody below = bodies[chain[i - 1]];
                double needed = Math.Sqrt(speed * speed + 2.0 * level.Mu / below.SemiMajorAxis);
                speed = Math.Abs(needed - Circular(level.Mu, below.SemiMajorAxis));
            }
            PlannerBody own = bodies[body];
            return Math.Sqrt(speed * speed + 2.0 * own.Mu / radius) - Circular(own.Mu, radius);
        }

        internal static double Launch(PlannerBody body)
        {
            double radius = body.Radius + body.LowOrbitAltitude;
            double rotation = body.RotationPeriod > 0.0 ? 2.0 * Math.PI * body.Radius / body.RotationPeriod : 0.0;
            double deltaV = TurnFactor * Circular(body.Mu, radius) - rotation;
            if (body.HasAtmosphere)
            {
                double gravity = body.Mu / (body.Radius * body.Radius);
                deltaV += Math.Sqrt(gravity * body.AtmosphereDepth)
                          * (AtmosphereLoss + DenseAtmosphereLoss * body.SurfacePressure * body.SurfacePressure);
            }
            else
            {
                deltaV += VacuumAllowance;
            }
            return Math.Max(0.0, deltaV);
        }

        // Powered all the way where there is no air or none is used; with
        // parachutes and a heat shield only thin air leaves a powered end.
        internal static double Landing(PlannerBody body, bool useAtmosphere)
        {
            double radius = body.Radius + body.LowOrbitAltitude;
            double orbital = Circular(body.Mu, radius);
            if (useAtmosphere && body.HasAtmosphere)
                return orbital * ThinLandingShare * Math.Max(0.0, 1.0 - body.SurfacePressure / ThinAtmosphere);
            double rotation = body.RotationPeriod > 0.0 ? 2.0 * Math.PI * body.Radius / body.RotationPeriod : 0.0;
            return Math.Max(0.0, TurnFactor * orbital - rotation + VacuumAllowance);
        }

        internal static void Hohmann(double mu, double r1, double r2, out double first, out double second)
        {
            double a = (r1 + r2) / 2.0;
            first = Math.Abs(VisViva(mu, r1, a) - Circular(mu, r1));
            second = Math.Abs(Circular(mu, r2) - VisViva(mu, r2, a));
        }

        internal static double Circular(double mu, double radius)
        {
            return Math.Sqrt(mu / radius);
        }

        internal static double VisViva(double mu, double radius, double semiMajorAxis)
        {
            return Math.Sqrt(mu * (2.0 / radius - 1.0 / semiMajorAxis));
        }

        private static double OrbitRadius(PlannerBody body, RouteStop stop)
        {
            double altitude = stop.Kind != StopKind.Orbit ? body.LowOrbitAltitude : Math.Max(stop.Altitude, 0.0);
            return body.Radius + altitude;
        }

        // The body and its parents, the body first.
        internal static List<int> Chain(IList<PlannerBody> bodies, int body)
        {
            List<int> chain = new List<int>();
            for (int at = body; at >= 0 && chain.Count <= bodies.Count; at = bodies[at].Parent) chain.Add(at);
            return chain;
        }

        private static int CommonAncestor(List<int> up, List<int> down)
        {
            foreach (int body in up)
                if (down.Contains(body)) return body;
            return -1;
        }

        // The body on the chain directly below `ancestor`; -1 where the chain
        // starts at the ancestor.
        private static int ChildOn(List<int> chain, int ancestor)
        {
            int at = chain.IndexOf(ancestor);
            return at > 0 ? chain[at - 1] : -1;
        }

        internal static string Km(double metres)
        {
            return (metres / 1000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " km";
        }
    }
}
