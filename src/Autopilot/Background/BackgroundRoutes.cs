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
    // The autopilot's route flown while the player is away from the vessel, without a simulation. The vessel stays a real vessel on its orbit, in
    // the space centre, the tracking station and in another flight. The plan the player had is kept as events, each at its time: the orbit the
    // vessel is on after a burn, and the share of its propellant that burn leaves. When the clock reaches an event the vessel's orbit is set to
    // that one and its tanks are emptied by that share, as KSP does for a vessel on rails. KSP itself carries the vessel from one body's sphere
    // of influence into the next. A vessel the player takes into flight has its events dropped: the autopilot flies on from where it is.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public sealed class BackgroundRoutes : ScenarioModule
    {
        internal static BackgroundRoutes Instance;

        private readonly List<RouteEntry> entries = new List<RouteEntry>();
        // The routes handed over and not yet taken in by a scenario: the scenario of a scene is destroyed as the next scene is asked for.
        private static readonly List<RouteEntry> Staged = new List<RouteEntry>();
        private readonly SettleService settle = new SettleService();
        private readonly RouteMessages messages = new RouteMessages();

        public override void OnAwake()
        {
            Instance = this;
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
        }

        private void OnDestroy()
        {
            GameEvents.onVesselWillDestroy.Remove(OnVesselWillDestroy);
            if (Instance == this) Instance = null;
        }

        // A vessel that the game destroys while it has steps left: the time, and the step it did not get to.
        private void OnVesselWillDestroy(Vessel vessel)
        {
            RouteEntry entry = entries.Find(e => e.Vessel == vessel.id);
            if (entry == null || entry.Steps.Count == 0) return;
            Debug.LogWarning(Log.Tag + " Route: " + entry.Name + " was destroyed at UT " + Planetarium.GetUniversalTime().ToString("0") + " with " + entry.Steps.Count
                             + " step(s) left, the next at UT " + entry.Steps[0].UT.ToString("0") + " (" + entry.Steps[0].Why + "), " + vessel.situation + " round " + vessel.mainBody.bodyName
                             + " at " + vessel.altitude.ToString("0") + " m.");
        }

        // The plan the vessel flies without the player: the route from where the vessel is, with the passes through the air flown in the simulation.
        internal static RoutePlan PlanFor(Vessel vessel, IList<Destination> route)
        {
            return RouteSimulation.Run(vessel, route, PlanOptions.Background);
        }

        // Keeps the plan's orbits for the vessel as steps, where the autopilot is to go on without the player. The plan's first leg is the orbit the vessel
        // is on; each later leg starts with a burn. Null where it was kept; else why not.
        // With `late` the scene that follows is already asked for: the module of this one is going, and the route waits staged for the next scene's.
        internal static string Hand(Vessel vessel, RoutePlan plan, IList<Destination> route = null, IVesselFigures figures = null, bool late = false)
        {
            bool climb = plan != null && plan.Steps.Count > 0 && plan.Steps[0].Kind == NodePlanner.StepKind.Ascent && route != null;
            bool lets = plan != null && plan.Steps.Exists(step => step.Kind == NodePlanner.StepKind.Release);
            bool meets = plan != null && plan.Steps.Exists(step => step.Kind == NodePlanner.StepKind.Rendezvous);
            if (plan == null || (plan.Legs.Count < 2 && !climb && !lets && !meets)) return "The plan has no burn to fly.";
            RouteEntry entry = RouteCompiler.Build(vessel, plan, route, figures);
            if (entry.Problem != null) return entry.Problem;
            if (entry.Steps.Count == 0) return "The plan has no orbit to go on to.";
            Staged.RemoveAll(old => old.Vessel == vessel.id);
            entry.Late = late;
            Staged.Add(entry);
            EnsureScenario();
            if (Instance == null) return null;
            if (!late) Instance.Adopt(false);
            // The game takes the scenarios as they are now into the scene that follows.
            if (HighLogic.CurrentGame != null) HighLogic.CurrentGame.scenarios = ScenarioRunner.GetUpdatedProtoModules();
            return null;
        }

        // Whether a route is kept for the vessel that is still to be flown.
        internal static bool Holds(Vessel vessel, IList<Destination> route)
        {
            string key = RoutePlan.SignatureOf(route);
            double now = Planetarium.GetUniversalTime();
            foreach (RouteEntry entry in Staged)
                if (Fits(entry, vessel, key, now)) return true;
            if (Instance != null)
                foreach (RouteEntry entry in Instance.entries)
                    if (Fits(entry, vessel, key, now) && !entry.Left) return true;
            return false;
        }

        // The route kept for the vessel is that of its route and of this moment: the time stands still while the pause menu is open.
        private static bool Fits(RouteEntry entry, Vessel vessel, string key, double now)
        {
            return entry.Vessel == vessel.id && entry.Steps.Count > 0 && entry.Key == key && Math.Abs(entry.HandedUT - now) < 1.0;
        }

        // The UT of the last step kept for the vessel, zero where there is none.
        internal static double LastStep(Vessel vessel)
        {
            double last = 0.0;
            foreach (RouteEntry entry in Staged)
                if (entry.Vessel == vessel.id && entry.Steps.Count > 0) last = Math.Max(last, entry.Steps[entry.Steps.Count - 1].UT);
            if (Instance != null)
                foreach (RouteEntry entry in Instance.entries)
                    if (entry.Vessel == vessel.id && entry.Steps.Count > 0) last = Math.Max(last, entry.Steps[entry.Steps.Count - 1].UT);
            return last;
        }

        // The vessel flies on by itself no longer: its route is dropped, where the player turned the autopilot off or went on flying.
        internal static void Cancel(Vessel vessel)
        {
            Staged.RemoveAll(old => old.Vessel == vessel.id);
            if (Instance != null) Instance.entries.RemoveAll(old => old.Vessel == vessel.id);
        }

        // A game made before the scenario was there gets it now, and the scenario runner keeps it for the scenes after this one. A game that has it,
        // with its module destroyed as the scene is left, takes the staged routes into the module of the next scene.
        private static void EnsureScenario()
        {
            if (Instance != null || ScenarioRunner.Instance == null || HighLogic.CurrentGame == null) return;
            if (HighLogic.CurrentGame.scenarios.Exists(module => module.moduleName == typeof(BackgroundRoutes).Name)) return;
            ScenarioModule added = ScenarioRunner.Instance.AddModule(typeof(BackgroundRoutes).Name);
            if (added == null) return;
            added.targetScenes = new List<GameScenes> { GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION };
            ScenarioRunner.GetUpdatedProtoModules().Add(new ProtoScenarioModule(added));
        }

        // The staged routes join this scenario's; a route handed over later for the same vessel is the newer.
        private void Adopt(bool loading)
        {
            for (int i = Staged.Count - 1; i >= 0; i--)
            {
                RouteEntry staged = Staged[i];
                if (staged.Late && !loading) continue;
                // A route handed over late belongs to the game the player left, at the moment he left it; the game that is loaded now is another where the time
                // differs.
                if (staged.Late && HighLogic.CurrentGame != null && HighLogic.CurrentGame.UniversalTime > 0.0 && Math.Abs(HighLogic.CurrentGame.UniversalTime - staged.HandedUT) > 5.0)
                {
                    Debug.LogWarning(Log.Tag + " Route: the route of " + staged.Name + " was handed over at UT " + staged.HandedUT.ToString("0") + " and the game loaded is at UT "
                                     + HighLogic.CurrentGame.UniversalTime.ToString("0") + "; it is dropped.");
                    Staged.RemoveAt(i);
                    continue;
                }
                staged.Late = false;
                entries.RemoveAll(old => old.Vessel == staged.Vessel);
                entries.Add(staged);
                Staged.RemoveAt(i);
            }
        }

        // Every frame: the steps whose time has come are made, in order, for each vessel that is not loaded.
        private void Update()
        {
            if (Staged.Count > 0) Adopt(false);
            messages.Tell(entries);
            if (entries.Count == 0 || FlightGlobals.fetch == null || FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count == 0 || HighLogic.CurrentGame == null) return;
            double now = Planetarium.GetUniversalTime();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                RouteEntry entry = entries[i];
                Vessel vessel = FlightGlobals.Vessels.Find(v => v.id == entry.Vessel);
                if (vessel == null || vessel.protoVessel == null)
                {
                    entries.RemoveAt(i);
                    continue;
                }
                if (!vessel.loaded) entry.Left = true;
                // A route kept when the pause menu opened, and the player went on flying: it is stale.
                if (vessel.loaded && !entry.Left && !FlightDriver.Pause && now > entry.HandedUT + 5.0)
                {
                    entries.RemoveAt(i);
                    continue;
                }
                // In the player's hands: the autopilot flies it from where it is.
                if (entry.Down && (!vessel.loaded || vessel.packed)) StepExecutor.StandOrbit(vessel, entry);
                if (vessel.loaded && entry.Left && vessel == FlightGlobals.ActiveVessel && !(entry.Down && vessel.packed))
                {
                    Debug.Log(Log.Tag + " Route: " + entry.Name + " is loaded; the background route ends here, with " + (entry.Reached + 1) + " stop(s) reached.");
                    if (entry.Down) settle.Begin(vessel.id);
                    RouteState state = RouteState.Of(vessel);
                    if (state != null && entry.Reached >= 0)
                        state.Progress = new RouteProgress { Key = entry.Key, Reached = Math.Max(entry.Reached, state.Progress.Key == entry.Key ? state.Progress.Reached : -1) };
                    entries.RemoveAt(i);
                    continue;
                }
                while (!vessel.loaded && entry.Steps.Count > 0 && entry.Steps[0].UT <= now)
                {
                    RouteStep step = entry.Steps[0];
                    entry.Steps.RemoveAt(0);
                    if (Make(vessel, entry, step)) entry.Reached = step.Reached;
                }
            }
        }

        // A vessel set down is first looked at on the ground it will stand on, and then let down on it at the speed of a landing.
        private void FixedUpdate()
        {
            settle.Tick(entries);
        }

        // A step made: the vessel is changed as the step says. A step that cannot be made is dropped with the reason, so that it is not tried again every frame.
        private static bool Make(Vessel vessel, RouteEntry entry, RouteStep step)
        {
            try
            {
                return StepExecutor.Make(vessel, entry, step);
            }
            catch (Exception e)
            {
                Debug.LogError(Log.Tag + " Route: " + entry.Name + " could not make its step at UT " + step.UT.ToString("0") + " (" + step.Why + "): " + e);
                return false;
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            entries.Clear();
            entries.AddRange(RoutePersistence.Load(node));
            Debug.Log(Log.Tag + " Route: the scenario is loaded in the " + HighLogic.LoadedScene + " with " + entries.Count + " route(s) saved in it, " + Staged.Count + " staged.");
            Adopt(true);
        }

        public override void OnSave(ConfigNode node)
        {
            RoutePersistence.Save(entries, node);
        }
    }
}
