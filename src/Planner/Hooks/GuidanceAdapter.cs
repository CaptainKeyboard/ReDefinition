using ReDefinition.Core;
using ReDefinition.Planner.Autopilot.Guidance;
using UnityEngine;

namespace ReDefinition.Planner
{
    // The guidance board and the guide node as the host reads them. Every call reads the live state; nothing is kept here.
    internal sealed class GuidanceAdapter : GuidanceHook
    {
        public override bool Current(Vessel vessel) { return GuidanceBoard.Current(vessel); }
        public override string Line { get { return GuidanceBoard.Line; } }
        public override Vector3d Direction { get { return GuidanceBoard.Direction; } }
        public override bool HasDirection { get { return GuidanceBoard.HasDirection; } }
        public override double Throttle { get { return GuidanceBoard.Throttle; } }
        public override bool HasThrottle { get { return GuidanceBoard.HasThrottle; } }
        public override bool HasBar { get { return GuidanceBoard.HasBar; } }
        public override double Arc { get { return GuidanceBoard.Arc; } }
        public override string NumberText { get { return GuidanceBoard.NumberText; } }
        public override int Level { get { return GuidanceBoard.Level; } }
        public override double MarkAt { get { return GuidanceBoard.MarkAt; } }
        public override bool Done { get { return GuidanceBoard.Done; } }
        public override double OnTarget { get { return GuidanceBoard.OnTarget; } }
        public override double FullColour { get { return GuidanceBoard.FullColour; } }
        public override bool HoldsNode(Vessel vessel) { return GuideNode.Holds(vessel); }
        public override bool IsGuideNode(ManeuverNode node) { return GuideNode.Is(node); }
    }
}
