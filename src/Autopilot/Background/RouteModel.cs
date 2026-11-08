using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Landing;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // What a step is: the vessel is on an orbit from then, parts are let go and the orbit stays the vessel's own, or the vessel is set down on the ground.
    internal enum StepKind
    {
        Orbit,
        Release,
        Touchdown,
    }

    // One thing that happens at a time: the vessel is on this orbit from then, with this share of its propellant kept.
    internal sealed class BackgroundStep
    {
        public double UT;
        public string Body;
        public double Inclination, Eccentricity, SemiMajorAxis, Lan, ArgumentOfPeriapsis, MeanAnomaly, Epoch;
        public double Keep = 1.0;
        public string Why = "";
        // The last stop of the route the vessel has reached once the step is made.
        public int Reached = -1;
        public StepKind Kind = StepKind.Orbit;
        // A touchdown has the orbit of the descent, and GroundUT the time the descent meets the ground. The step is made long before that, so that
        // no frame of the fastest time warp carries the vessel into the ground, where the game would destroy it.
        public double GroundUT;
        public TouchdownSite Site;
        // The share each part keeps of its propellant, by its persistent id, where the burn is shared over the vessel's stages.
        public readonly Dictionary<uint, double> Keeps = new Dictionary<uint, double>();
        // The parts let go in the step, in groups that become vessels.
        public readonly List<PartsCut> Cuts = new List<PartsCut>();
    }

    // Parts let go of the vessel: by their persistent ids, with the name and the kind of the vessel they make.
    internal sealed class PartsCut
    {
        public string Name = "";
        public VesselType Type = VesselType.Debris;
        public readonly List<uint> Parts = new List<uint>();
    }

    // A vessel's route: its steps in the order of their times, and the propellants its engines burn.
    internal sealed class RouteEntry
    {
        public Guid Vessel;
        public string Name = "";
        public readonly List<string> Propellants = new List<string>();
        public readonly List<BackgroundStep> Steps = new List<BackgroundStep>();
        // Set once the vessel was seen out of the flight: before that it is the vessel the route is handed over from, still loaded.
        public bool Left;
        // The route the steps were planned for (RoutePlan.Signature) and the last stop reached by the steps made so far.
        public string Key = "";
        public int Reached = -1;
        // How far the vessel reaches below its reference point when it stands upright, in m: the height its reference point is set over the ground.
        public double Lowest;
        // How far it reaches to the side of its reference point.
        public double Footprint;
        // Where the vessel stands once it is set down: latitude, longitude and the height of its reference point. Its orbit is kept to this until it is loaded.
        public bool Down;
        public double DownLatitude, DownLongitude, DownHeight;
        // The ground under the vessel was measured with rays, before it was unpacked.
        public bool Surveyed;
        // The vessel is set down on the water, where there is no ground to measure.
        public bool Wet;
        // Why the route cannot be flown in the background, where it cannot.
        public string Problem;
        // Handed over when the next scene was already asked for: only the module of that scene takes it in.
        public bool Late;
        // The UT the route was kept at: a route is the one kept for its moment only.
        public double HandedUT;
    }
}
