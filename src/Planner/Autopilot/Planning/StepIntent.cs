namespace ReDefinition.Planner.Autopilot.Planning
{
    // What the next step of a route is, as far as it follows from the facts of the vessel and the stop it goes to, before any node is worked out.
    internal enum StepIntent
    {
        // A launch into the orbit of the stop; and a hop from the surface to the surface, which is not planned.
        Ascent,
        HopNotPlanned,
        // A drive over the ground to the place of the stop: the vessel stands on the ground with wheels, and the place is farther than the drive's reach.
        Drive,
        // The landing on the body the vessel is round. It is made straight in from the approach, with the air slowing the vessel at the periapsis,
        // by a capture first, by the deorbit burn the player flies, or by the autopilot's own landing.
        LandStraightIn,
        LandByAir,
        LandByCapture,
        LandByDeorbit,
        Land,
        // The orbit round the body the vessel is round. It is a meeting with a vessel, a new circular orbit, or the end of a plan at a pass through the air.
        // It is also a pass through the air from a closed orbit, the air slowing a vessel on an open one, or its capture.
        Meet,
        ChangeOrbit,
        AirPassEndsPlan,
        AirPassFromOrbit,
        AirOnApproach,
        Capture,
        // The way to another body: on the way out already, the burn that leaves the body, no way as nodes, the transfer, the correction of the course.
        Leaving,
        Eject,
        NoWay,
        Transfer,
        CorrectCourse,
    }

    // What decides the intent: facts of the vessel's orbit and of the stop the vessel goes to, worked out by the caller from the game.
    internal struct StepFacts
    {
        // The stop is a surface, and is on the body the vessel is round.
        public bool StopSurface;
        public bool SameBody;
        // The vessel stands on the ground or the pad, flies in the air, or climbs on a suborbital arc that has not yet reached the orbit asked for.
        public bool OnGround;
        public bool CanDrive;
        public bool Flying;
        public bool Climbing;
        // The orbit is open; it is an escape from its body.
        public bool Open;
        public bool Escaping;
        // The stop is to be reached straight in, or by the air.
        public bool Direct;
        public bool Aerobrake;
        // The player flies the descent, and the periapsis is over the ground.
        public bool ForPlayer;
        public bool PeriapsisOverGround;
        // The stop is met with a vessel that is there to be met, and the orbit is wider than the one asked for.
        public bool HasMeetTarget;
        public bool ApoapsisAboveOrbit;
        // The plan is made ahead of the vessel and does not simulate passes through the air.
        public bool PlanEndsAtPass;
        // The body the stop is on can be left for: the vessel's body is not the sun, and the stop is its parent or a sibling; or the stop's parent is it.
        public bool EjectPossible;
        public bool IsChildOfBody;
        // The vessel's orbit has an encounter with the stop's body ahead, and the arrival is to use the air.
        public bool HasEncounter;
        public bool AirOnApproach;

        // The intent: the order of the questions is the order of the route's logic.
        internal StepIntent Intend()
        {
            bool landing = StopSurface && SameBody && !OnGround;
            if (!landing && (OnGround || Flying || Climbing)) return SameBody && StopSurface ? (CanDrive ? StepIntent.Drive : StepIntent.HopNotPlanned) : StepIntent.Ascent;
            if (StopSurface && SameBody)
            {
                if (Open) return Direct ? StepIntent.LandStraightIn : Aerobrake ? StepIntent.LandByAir : StepIntent.LandByCapture;
                return ForPlayer && PeriapsisOverGround ? StepIntent.LandByDeorbit : StepIntent.Land;
            }
            if (SameBody)
            {
                if (Open) return Aerobrake ? StepIntent.AirOnApproach : StepIntent.Capture;
                if (HasMeetTarget) return StepIntent.Meet;
                if (!Aerobrake || !ApoapsisAboveOrbit) return StepIntent.ChangeOrbit;
                return PlanEndsAtPass ? StepIntent.AirPassEndsPlan : StepIntent.AirPassFromOrbit;
            }
            if (Open && Escaping) return StepIntent.Leaving;
            if (EjectPossible) return StepIntent.Eject;
            if (!IsChildOfBody) return StepIntent.NoWay;
            return HasEncounter ? StepIntent.CorrectCourse : StepIntent.Transfer;
        }
    }
}
