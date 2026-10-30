using System;
using System.Collections.Generic;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Core;
using ReDefinition.Hud.Panel;
using ReDefinition.Hud.Readouts;
using UnityEngine;

namespace ReDefinition.Hud
{
    // What the round gauge reads for each mode, from the vessel and KSP's delta-v
    // simulation, and the log of what it shows.
    public partial class RoundGauge
    {
        // The mode for the situation: a burn while a node is planned in orbit or
        // on the way to a target, else the stage in orbit, the closing speed on
        // the way to a target.
        private static GaugeMode ForSituation(ReadoutFrame frame)
        {
            if (GuidanceBoard.Current(frame.Vessel) && GuidanceBoard.HasBar)
            {
                // The landing is a manoeuvre: the countdown to its burn, as for any. Once the guidance asks for thrust, or the burn is due, the bar.
                ManeuverNode brake = Node(frame.Vessel);
                bool throttleAsked = GuidanceBoard.HasThrottle && GuidanceBoard.Throttle > 0.02;
                if (brake != null && !throttleAsked && UntilBurn(brake, frame) > 0.0) return GaugeMode.Burn;
                return GaugeMode.Guidance;
            }
            switch (FlightProfiles.Held(frame.Vessel))
            {
                case PanelSituation.Landing: return GaugeMode.Impact;
                case PanelSituation.Docking: return GaugeMode.Closing;
                case PanelSituation.Air: return GaugeMode.Attack;
                case PanelSituation.Rendezvous: return Node(frame.Vessel) != null ? GaugeMode.Burn : GaugeMode.Closing;
                case PanelSituation.Orbit: return Node(frame.Vessel) != null ? GaugeMode.Burn : GaugeMode.Stage;
                default: return GaugeMode.Stage;
            }
        }

        // What the gauge shows in that mode; null where there is nothing to show.
        private static GaugeReading Read(GaugeMode mode, ReadoutFrame frame)
        {
            switch (mode)
            {
                case GaugeMode.Stage: return StageReading(frame);
                case GaugeMode.Burn: return BurnReading(frame);
                case GaugeMode.Impact: return ImpactReading(frame);
                case GaugeMode.Closing: return ClosingReading(frame);
                case GaugeMode.Attack: return AttackReading(frame);
                case GaugeMode.Guidance: return GuidanceReading(frame);
                default: return null;
            }
        }

        // The bar of the navigation: the arc stands over the mark in the middle of the scale where the throttle is to go up, and under it where
        // it is to go down. In a descent the arc is the speed against the speed it should be, in a climb the throttle asked for against the one given.
        private static GaugeReading GuidanceReading(ReadoutFrame frame)
        {
            if (!GuidanceBoard.Current(frame.Vessel) || !GuidanceBoard.HasBar) return null;
            return new GaugeReading { Text = GuidanceBoard.BarText, Fill = GuidanceBoard.BarFill, Mark = 0.5, Level = GuidanceBoard.BarLevel };
        }

        // The stage as KSP's delta-v simulation has it (DeltaVStageInfo:
        // stageBurnTime, enginesActiveInStage). Each engine comes with the propellants
        // it can reach (Part.GetConnectedResourceTotals, each propellant's flow
        // mode), by mass, and its full fuel flow at its thrust limit
        // (ModuleEngines.maxFuelFlow, thrustPercentage).
        private static readonly List<EngineFuel> stageEngines = new List<EngineFuel>();
        private static double stageBurn;

        // The stage's burn time, the arc on the propellant left to the engine it is timed by; null where it burns nothing.
        private static GaugeReading StageReading(ReadoutFrame frame)
        {
            stageEngines.Clear();
            stageBurn = 0.0;
            Vessel vessel = frame.Vessel;
            DeltaVStageInfo stage = vessel.VesselDeltaV != null ? vessel.VesselDeltaV.GetStage(vessel.currentStage) : null;
            if (stage == null || stage.stageBurnTime <= 0.0 || stage.enginesActiveInStage == null) return null;
            foreach (DeltaVEngineInfo info in stage.enginesActiveInStage)
            {
                ModuleEngines engine = info != null ? info.engine : null;
                if (engine == null || engine.part == null || engine.propellants == null) continue;
                EngineFuel fuel = new EngineFuel
                {
                    Name = engine.part.partInfo != null ? engine.part.partInfo.name : engine.part.name,
                    Flow = engine.maxFuelFlow * engine.thrustPercentage * 0.01,
                };
                foreach (Propellant propellant in engine.propellants)
                {
                    PartResourceDefinition definition = PartResourceLibrary.Instance.GetDefinition(propellant.id);
                    if (definition == null || definition.density <= 0.0 || propellant.name == "IntakeAir" || propellant.name == "IntakeAtm")
                        continue;
                    double amount, most;
                    engine.part.GetConnectedResourceTotals(propellant.id, propellant.GetFlowMode(), out amount, out most);
                    fuel.Left += amount * definition.density;
                    fuel.Full += most * definition.density;
                }
                stageEngines.Add(fuel);
            }
            stageBurn = stage.stageBurnTime;
            return GaugeReadings.Stage(stageEngines, stageBurn);
        }

        // The first manoeuvre node planned; null for none.
        private static ManeuverNode Node(Vessel vessel)
        {
            PatchedConicSolver solver = vessel != null ? vessel.patchedConicSolver : null;
            return solver != null && solver.maneuverNodes != null && solver.maneuverNodes.Count > 0 ? solver.maneuverNodes[0] : null;
        }

        // The next node. Before its burn, the time until the burn starts, half its
        // length before the node (the length by the rocket equation, at the
        // stage's thrust and Isp in vacuum). Once it is due, the delta-v left,
        // against what the node asked for as the burn started.
        private static ManeuverNode burnNode;
        private static double burnStart = double.NaN;

        // The seconds until the burn of the node starts, half its length before the node.
        private static double UntilBurn(ManeuverNode node, ReadoutFrame frame)
        {
            double length = BurnLength(frame.Vessel, node.GetBurnVector(frame.Orbit).magnitude);
            return double.IsNaN(length) ? node.UT - frame.Now : node.UT - frame.Now - length / 2.0;
        }

        // The countdown to the node's burn, then the delta-v left of it; null without a node.
        private static GaugeReading BurnReading(ReadoutFrame frame)
        {
            ManeuverNode node = Node(frame.Vessel);
            if (node == null) return null;
            double left = node.GetBurnVector(frame.Orbit).magnitude;
            if (node != burnNode)
            {
                burnNode = node;
                burnStart = double.NaN;
            }
            double length = BurnLength(frame.Vessel, left);
            double until = double.IsNaN(length) ? node.UT - frame.Now : node.UT - frame.Now - length / 2.0;
            if (until > 0.0)
            {
                burnStart = double.NaN;
                return GaugeReadings.Countdown(until);
            }
            if (double.IsNaN(burnStart)) burnStart = left;
            return GaugeReadings.Burning(left, burnStart);
        }

        // How long the stage takes for `deltaV` at its thrust and Isp in vacuum; NaN where it has none.
        private static double BurnLength(Vessel vessel, double deltaV)
        {
            DeltaVStageInfo stage = vessel.VesselDeltaV != null ? vessel.VesselDeltaV.GetStage(vessel.currentStage) : null;
            if (stage == null || stage.thrustVac <= 0f || stage.ispVac <= 0.0) return double.NaN;
            double exhaust = stage.ispVac * PhysicsGlobals.GravitationalAcceleration;
            double flow = stage.thrustVac / exhaust;
            return vessel.totalMass * (1.0 - Math.Exp(-deltaV / exhaust)) / flow;
        }

        // The time until the vessel's path meets the ground (Impact.Seconds), and
        // the countdown to the last moment to brake (ReadoutFrame.SuicideBurn).
        private static GaugeReading ImpactReading(ReadoutFrame frame)
        {
            double seconds = frame.ImpactSeconds;
            if (double.IsNaN(seconds)) return null;
            double altitude, countdown, deltaV;
            frame.SuicideBurn(out altitude, out countdown, out deltaV);
            return GaugeReadings.Impact(seconds, countdown);
        }

        // How fast the vessel closes on its target, its docking port where it is
        // one, along the line between them, and how far it is. It measures from the vessel's
        // own port to a target port (ReadoutFrame.DockingFrom), else from its
        // control point.
        private static GaugeReading ClosingReading(ReadoutFrame frame)
        {
            if (frame.Target == null) return null;
            ModuleDockingNode port = frame.TargetPort;
            Vector3d at = port != null && port.nodeTransform != null ? (Vector3d)port.nodeTransform.position : frame.TargetPosition;
            Vector3d line = at - (port != null ? frame.DockingFrom : (Vector3d)frame.Vessel.ReferenceTransform.position);
            double distance = line.magnitude;
            if (distance <= 0.0) return null;
            return GaugeReadings.Closing(Vector3d.Dot(frame.RelativeVelocity, line / distance), distance);
        }

        // The angle of attack against the wings' stall; null where the vessel moves under 1 m/s over the ground.
        private static GaugeReading AttackReading(ReadoutFrame frame)
        {
            double attack, sideslip;
            frame.AeroAngles(out attack, out sideslip);
            return double.IsNaN(attack) ? null : GaugeReadings.Attack(attack, Stall);
        }

        // ------------------------------------------------------------ watch

        // Seconds between the log's lines of what the gauge shows.
        private const float TraceEvery = 10f;
        // How far the stage's arc may stand from where the digits put it, as a
        // share of the scale, before the log says so.
        private const double Tolerance = 0.05;
        // The mode and the time of the log's next line; the vessel and stage the arc is checked in, the arc per second of
        // burn time as that stage began, and whether it warned.
        private static GaugeMode traced = GaugeMode.Auto;
        private static float traceNext;
        private static readonly HeldVessel watchedVessel = new HeldVessel();
        private static int watchedStage = -1;
        private static double arcPerSecond = double.NaN;
        private static bool warned;

        // Writes what the gauge shows into the log every ten seconds and at each change
        // of figure. It also checks the stage's arc against its digits. Within a stage the
        // arc must stand at the same share per second of burn time as when the
        // stage began, or the two count different things. It gives one warning per stage,
        // with the engines as they were counted.
        private static void Watch(GaugeMode shown, GaugeReading reading, Vessel vessel)
        {
            float now = Time.unscaledTime;
            bool stage = shown == GaugeMode.Stage && reading.Text != null && stageBurn > 0.0;
            if (shown != traced || now >= traceNext)
            {
                traced = shown;
                traceNext = now + TraceEvery;
                Debug.Log(Log.Tag + " Round gauge: " + shown + " " + (reading.Text ?? "-") + ", arc " + reading.Fill.ToString("0.000")
                          + (reading.Level == 2 ? " red" : reading.Level == 1 ? " amber" : " white")
                          + (double.IsNaN(reading.Mark) ? "" : ", tick " + reading.Mark.ToString("0.000"))
                          + (stage ? "; stage " + vessel.currentStage + " of " + stageBurn.ToString("0.0") + " s, " + Engines() : "") + ".");
            }
            if (!stage) return;
            if (!watchedVessel.Is(vessel) || vessel.currentStage != watchedStage)
            {
                watchedVessel.Set(vessel);
                watchedStage = vessel.currentStage;
                arcPerSecond = double.NaN;
                warned = false;
            }
            if (double.IsNaN(arcPerSecond))
            {
                if (stageBurn > 1.0) arcPerSecond = reading.Fill / stageBurn;
                return;
            }
            double expected = arcPerSecond * stageBurn;
            if (warned || Math.Abs(reading.Fill - expected) <= Tolerance) return;
            warned = true;
            Debug.LogWarning(Log.Tag + " Round gauge check: in stage " + watchedStage + " the arc stands at " + reading.Fill.ToString("0.000")
                             + " where the digits, " + stageBurn.ToString("0.0") + " s, put it at " + expected.ToString("0.000")
                             + " (" + arcPerSecond.ToString("0.0000") + " of the arc per second as the stage began); throttle "
                             + (vessel.ctrlState != null ? vessel.ctrlState.mainThrottle.ToString("0.00") : "?") + "; " + Engines() + ".");
        }

        // The stage's engines as counted: each with its propellant left of full,
        // in tonnes, and its seconds at full thrust; the one the arc follows
        // marked.
        private static string Engines()
        {
            int timed = GaugeReadings.Timed(stageEngines, stageBurn);
            System.Text.StringBuilder text = new System.Text.StringBuilder("engines:");
            for (int i = 0; i < stageEngines.Count; i++)
                text.Append(i == 0 ? " " : ", ").Append(i == timed ? "*" : "").Append(stageEngines[i].Name).Append(' ')
                    .Append(stageEngines[i].Left.ToString("0.00")).Append('/').Append(stageEngines[i].Full.ToString("0.00"))
                    .Append(" t ").Append(stageEngines[i].Seconds.ToString("0.0")).Append(" s");
            return text.ToString();
        }

        // Where KSP's lift curve for wings peaks, in degrees (PhysicsGlobals'
        // "Default" lifting surface: lift over the sine of the angle of attack;
        // 30 in KSP's own Physics.cfg).
        private static double stall = double.NaN;

        // Where KSP's wings stall, in degrees: read once from the lift curve, 30 where it cannot be.
        internal static double Stall
        {
            get
            {
                if (!double.IsNaN(stall)) return stall;
                stall = 30.0;
                PhysicsGlobals.LiftingSurfaceCurve curve = PhysicsGlobals.GetLiftingSurfaceCurve("Default");
                if (curve != null && curve.liftCurve != null)
                {
                    float best = float.MinValue, at = 0f;
                    for (int i = 1; i <= 100; i++)
                    {
                        float lift = curve.liftCurve.Evaluate(i / 100f);
                        if (lift <= best) continue;
                        best = lift;
                        at = i / 100f;
                    }
                    if (at > 0f) stall = Math.Asin(at) * 180.0 / Math.PI;
                }
                return stall;
            }
        }
    }
}
