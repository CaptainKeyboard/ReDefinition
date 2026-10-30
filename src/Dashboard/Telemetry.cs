using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Engineer;
using UnityEngine;

namespace ReDefinition.Dashboard
{
    /// <summary>
    /// The keys a dashboard can read and the commands it can run. The key names and the bracket
    /// syntax (<c>f.setThrottle[0.5]</c>) follow Telemachus' API (https://github.com/TeaGuild/Telemachus-1,
    /// MIT), so dashboards written for it keep working; what is behind them is this mod's own code.
    /// Unlike Telemachus nothing here depends on an antenna part or on the vessel's power.
    /// </summary>
    internal sealed class Telemetry
    {
        // A key's value on the active vessel, from the arguments in its brackets.
        private delegate object Reader(Vessel v, string[] args);
        // A command run on the active vessel with the arguments in its brackets.
        private delegate void Action(Vessel v, string[] args);

        // The vessel's keys and commands by name, read and run in flight only, while the game is not paused.
        private readonly Dictionary<string, Reader> readers = new Dictionary<string, Reader>();
        private readonly Dictionary<string, Action> actions = new Dictionary<string, Action>();

        // Keys and commands with no vessel, read and run in every scene: the menu's, the game's, the Space
        // Center's and the editors' pages.
        private delegate object SceneReader(string[] args);
        // A command of that kind, run with the arguments in its brackets.
        private delegate void SceneAction(string[] args);
        // Those keys and commands by name, and the readers for each page's scene that keep state between reads.
        private readonly Dictionary<string, SceneReader> sceneReaders = new Dictionary<string, SceneReader>();
        private readonly Dictionary<string, SceneAction> sceneActions = new Dictionary<string, SceneAction>();
        private readonly MenuTelemetry menu = new MenuTelemetry();
        private readonly KscTelemetry ksc = new KscTelemetry();
        private readonly EditorTelemetry editor = new EditorTelemetry();
        private readonly FuelTelemetry fuel = new FuelTelemetry();

        // A key as the dashboard sends it, split once: with a page polling every frame, the same keys come back
        // for as long as it is open. Keys beyond the cap (a client may send any) are split each time.
        private sealed class Parsed
        {
            // The name before the brackets, the arguments inside them, and the step the value is rounded to (null for none).
            public string Name;
            public string[] Args;
            public Func<double, double> Step;
        }

        private const int ParsedCap = 1024;
        private readonly Dictionary<string, Parsed> parsed = new Dictionary<string, Parsed>();

        // Resource totals are summed over the whole vessel once per frame and then looked up per key.
        private int resourceFrame = -1;
        private readonly Dictionary<string, double[]> resources = new Dictionary<string, double[]>();

        // KSP's ten custom action groups, behind v.ag1Value .. v.ag10Value and f.ag1 .. f.ag10.
        private static readonly KSPActionGroup[] CustomGroups =
        {
            KSPActionGroup.Custom01, KSPActionGroup.Custom02, KSPActionGroup.Custom03, KSPActionGroup.Custom04,
            KSPActionGroup.Custom05, KSPActionGroup.Custom06, KSPActionGroup.Custom07, KSPActionGroup.Custom08,
            KSPActionGroup.Custom09, KSPActionGroup.Custom10,
        };

        // Registers every key and command by name, grouped by the page and the part of the game they belong to.
        public Telemetry()
        {
            // The game, in every scene: the scene's name (GameScenes), the versions, KSP's progress
            // while it loads, the saves; g.load[name] (URI-encoded) and g.quit from the main menu.
            sceneReaders["g.scene"] = a => MenuTelemetry.Scene;
            sceneReaders["g.versions"] = a => MenuTelemetry.Versions();
            sceneReaders["g.loading"] = a => MenuTelemetry.Loading();
            sceneReaders["g.saves"] = a => menu.Saves();
            sceneActions["g.load"] = a => menu.Load(a);
            sceneActions["g.quit"] = a => MenuTelemetry.Quit();

            // A game in progress, in every scene but the menus: its mode, funds, science, reputation,
            // UT, date and calendar; time warp at the Space Center, in the Tracking Station and in
            // flight; g.quicksave and g.mainMenu at the Space Center and in the Tracking Station.
            sceneReaders["g.mode"] = a => GameTelemetry.Mode();
            sceneReaders["g.funds"] = a => GameTelemetry.Funds();
            sceneReaders["g.science"] = a => GameTelemetry.Science();
            sceneReaders["g.reputation"] = a => GameTelemetry.Reputation();
            sceneReaders["g.ut"] = a => GameTelemetry.Ut();
            sceneReaders["g.date"] = a => GameTelemetry.Date();
            sceneReaders["g.calendar"] = a => GameTelemetry.Calendar();
            sceneReaders["t.currentRate"] = a => GameTelemetry.WarpRate();
            sceneReaders["t.currentRateIndex"] = a => GameTelemetry.WarpRateIndex();
            sceneActions["t.timeWarp"] = a => GameTelemetry.SetWarp(a);
            sceneActions["g.quicksave"] = a => GameTelemetry.QuickSave();
            sceneActions["g.mainMenu"] = a => GameTelemetry.MainMenu();

            // The Space Center: its buildings, the vessels to fly, the next alarms, the active
            // contracts and the crew; k.enter[facility], k.fly[vessel id], k.warpTo[alarm id].
            sceneReaders["k.buildings"] = a => ksc.Buildings();
            sceneReaders["k.vessels"] = a => ksc.Vessels();
            sceneReaders["k.alarms"] = a => ksc.Alarms();
            sceneReaders["k.contracts"] = a => ksc.Contracts();
            sceneReaders["k.crew"] = a => ksc.Crew();
            sceneActions["k.enter"] = a => KscTelemetry.Enter(a);
            sceneActions["k.fly"] = a => KscTelemetry.Fly(a);
            sceneActions["k.warpTo"] = a => KscTelemetry.WarpTo(a);

            // The VAB and the SPH: the Engineer (e.ship), the planner's plan (e.plan) and the list
            // one of its buttons folds out (e.choiceList, asked with e.choices[list,a,b]);
            // e.change[name,numbers...] and e.pick[list,a,b,index,label] change the plan.
            sceneReaders["e.ship"] = a => editor.Ship();
            sceneReaders["e.plan"] = a => editor.Plan();
            sceneReaders["e.choiceList"] = a => editor.Choices();
            sceneActions["e.change"] = a => EditorTelemetry.Change(a);
            sceneActions["e.choices"] = a => editor.AskChoices(a);
            sceneActions["e.pick"] = a => EditorTelemetry.Pick(a);

            // Vessel
            readers["v.name"] = (v, a) => v.vesselName;
            readers["v.body"] = (v, a) => v.mainBody != null ? v.mainBody.bodyName : null;
            readers["v.situationString"] = (v, a) => v.SituationString;
            readers["v.altitude"] = (v, a) => v.altitude;
            readers["v.heightFromTerrain"] = (v, a) => (double)v.heightFromTerrain;
            readers["v.surfaceVelocity"] = (v, a) => v.srf_velocity.magnitude;
            readers["v.surfaceSpeed"] = (v, a) => v.horizontalSrfSpeed;
            readers["v.verticalSpeed"] = (v, a) => v.verticalSpeed;
            readers["v.orbitalVelocity"] = (v, a) => v.obt_velocity.magnitude;
            readers["v.geeForce"] = (v, a) => v.geeForce;
            readers["v.missionTime"] = (v, a) => v.missionTime;
            readers["v.currentStage"] = (v, a) => v.currentStage;
            readers["v.isControllable"] = (v, a) => v.IsControllable;

            // Orbit
            readers["o.ApA"] = (v, a) => v.orbit.ApA;
            readers["o.PeA"] = (v, a) => v.orbit.PeA;
            readers["o.timeToAp"] = (v, a) => v.orbit.timeToAp;
            readers["o.timeToPe"] = (v, a) => v.orbit.timeToPe;
            readers["o.eccentricity"] = (v, a) => v.orbit.eccentricity;
            readers["o.inclination"] = (v, a) => v.orbit.inclination;
            readers["o.period"] = (v, a) => v.orbit.period;

            // Controls: state
            readers["f.throttle"] = (v, a) => (double)FlightInputHandler.state.mainThrottle;
            readers["f.sasEnabled"] = (v, a) => v.Autopilot.Enabled;
            readers["f.sasMode"] = (v, a) => v.Autopilot.Mode.ToString();
            readers["v.sasValue"] = (v, a) => v.ActionGroups[KSPActionGroup.SAS];
            readers["v.rcsValue"] = (v, a) => v.ActionGroups[KSPActionGroup.RCS];
            readers["v.lightValue"] = (v, a) => v.ActionGroups[KSPActionGroup.Light];
            readers["v.gearValue"] = (v, a) => v.ActionGroups[KSPActionGroup.Gear];
            readers["v.brakeValue"] = (v, a) => v.ActionGroups[KSPActionGroup.Brakes];
            readers["v.abortValue"] = (v, a) => v.ActionGroups[KSPActionGroup.Abort];
            for (int i = 0; i < CustomGroups.Length; i++)
            {
                KSPActionGroup g = CustomGroups[i];
                readers["v.ag" + (i + 1) + "Value"] = (v, a) => v.ActionGroups[g];
            }

            // Attitude for a navball, as unit vectors in the vessel's local north/east/up frame:
            // [north, east, up]. The dashboard turns them into the ball.
            readers["n.nose"] = (v, a) => Local(v, v.ReferenceTransform.up);
            readers["n.right"] = (v, a) => Local(v, v.ReferenceTransform.right);
            readers["n.top"] = (v, a) => Local(v, -v.ReferenceTransform.forward);
            readers["n.speedMode"] = (v, a) => FlightGlobals.speedDisplayMode.ToString();
            // The first maneuver node: its burn direction (for the navball), the time to it and the
            // delta-v still to burn; null without a node.
            readers["n.maneuver"] = (v, a) => { ManeuverNode n = FirstNode(v); return n == null ? null : Local(v, n.GetBurnVector(v.orbit).normalized); };
            readers["o.nodeTime"] = (v, a) => { ManeuverNode n = FirstNode(v); return n == null ? null : (object)(n.UT - Planetarium.GetUniversalTime()); };
            readers["o.nodeDv"] = (v, a) => { ManeuverNode n = FirstNode(v); return n == null ? null : (object)n.GetBurnVector(v.orbit).magnitude; };
            readers["o.nodeDvTotal"] = (v, a) => { ManeuverNode n = FirstNode(v); return n == null ? null : (object)n.DeltaV.magnitude; };
            // How long the rest of the burn takes at full thrust, and in how many seconds to start it so
            // that the lead share of it lies before the node (as KSP's own maneuver readout does).
            readers["o.nodeBurnTime"] = (v, a) => { ManeuverNode n = FirstNode(v); return n == null ? null : BurnTime(v, n.GetBurnVector(v.orbit).magnitude); };
            readers["o.nodeStartIn"] = (v, a) =>
            {
                ManeuverNode n = FirstNode(v);
                if (n == null) return null;
                object lead = BurnTime(v, n.GetBurnVector(v.orbit).magnitude * nodeLead);
                return lead == null ? null : (object)(n.UT - Planetarium.GetUniversalTime() - (double)lead);
            };
            readers["o.nodeLead"] = (v, a) => nodeLead * 100;
            readers["n.speed"] = (v, a) => SpeedShown(v);
            // The navigation: where it asks the nose to point (given as n.maneuver is), the throttle it asks for, the bar of the round gauge with its
            // text and level, and the line that says what to do. All are null while it has nothing for this vessel. g.route is what the route is
            // doing, with g.navigation and g.autopilot for the two switches.
            readers["n.guide"] = (v, a) => GuidanceBoard.Current(v) && GuidanceBoard.HasDirection ? Local(v, GuidanceBoard.Direction) : null;
            readers["g.line"] = (v, a) => GuidanceBoard.Current(v) ? GuidanceBoard.Line : null;
            readers["g.throttle"] = (v, a) => GuidanceBoard.Current(v) && GuidanceBoard.HasThrottle ? (object)GuidanceBoard.Throttle : null;
            readers["g.barFill"] = (v, a) => GuidanceBoard.Current(v) && GuidanceBoard.HasBar ? (object)GuidanceBoard.BarFill : null;
            readers["g.barText"] = (v, a) => GuidanceBoard.Current(v) && GuidanceBoard.HasBar ? GuidanceBoard.BarText : null;
            readers["g.barLevel"] = (v, a) => GuidanceBoard.Current(v) && GuidanceBoard.HasBar ? (object)GuidanceBoard.BarLevel : null;
            readers["g.route"] = (v, a) => RouteDriver.Instance != null && RouteDriver.Instance.Status.Length > 0 ? RouteDriver.Instance.Status : null;
            readers["g.navigation"] = (v, a) => RouteState.Of(v) != null && RouteState.Of(v).Navigation;
            readers["g.autopilot"] = (v, a) => RouteState.Of(v) != null && RouteState.Of(v).Autopilot;
            readers["n.steerTarget"] = (v, a) => FlyByWire.Target;       // where a navball drag turns the vessel, or null
            readers["n.steerTargetTop"] = (v, a) => FlyByWire.TargetTop; // where its top will point there, or null
            readers["n.steerAltitude"] = (v, a) => FlyByWire.TargetAltitude; // the turn is done by this altitude, or null
            readers["n.steerSCurve"] = (v, a) => FlyByWire.SCurve;          // the turn spread as an S-curve, else linearly
            readers["n.steerCurve"] = (v, a) => FlyByWire.CurveName;        // "linear", "s" or "auto"
            readers["n.steerArmed"] = (v, a) => FlyByWire.Armed;            // play: steering to the target; pause: only holding it
            readers["n.prograde"] = (v, a) => Local(v, Prograde(v));
            readers["n.normal"] = (v, a) =>
            {
                Vector3d u, n, e;
                FrameThisFrame(v, out n, out e, out u);
                return Local(v, Vector3d.Cross(v.obt_velocity.normalized, u).normalized);
            };
            readers["n.radial"] = (v, a) =>
            {
                Vector3d p = v.obt_velocity.normalized;
                Vector3d u, n, e;
                FrameThisFrame(v, out n, out e, out u);
                return Local(v, (u - Vector3d.Dot(u, p) * p).normalized);
            };

            // Time warp: the rate and its index are the game's (GameTelemetry)
            readers["t.physics"] = (v, a) => TimeWarp.WarpMode == TimeWarp.Modes.LOW;

            // Views and the game
            readers["m.mapIsEnabled"] = (v, a) => MapView.MapIsEnabled;
            readers["v.precisionControlValue"] = (v, a) => FlightInputHandler.fetch.precisionMode;
            readers["c.mode"] = (v, a) => FlightCamera.fetch != null ? FlightCamera.fetch.mode.ToString() : null;

            // Resources: r.resource[name], r.resourceMax[name] over the whole vessel; r.engineDry[name], the
            // totals of it at which engines burning it run dry (FuelTelemetry); r.engineEmpty[name], an engine
            // lit that burns it is empty
            readers["r.resource"] = (v, a) => Resource(v, a, 0);
            readers["r.resourceMax"] = (v, a) => Resource(v, a, 1);
            readers["r.engineDry"] = (v, a) => fuel.Dry(v, a);
            readers["r.engineEmpty"] = (v, a) => fuel.Empty(v, a);

            // Controls: commands
            actions["f.stage"] = (v, a) => StageManager.ActivateNextStage();
            actions["f.setThrottle"] = (v, a) => FlightInputHandler.state.mainThrottle = Mathf.Clamp01(Float(a, 0));
            actions["f.throttleZero"] = (v, a) => FlightInputHandler.state.mainThrottle = 0f;
            actions["f.throttleFull"] = (v, a) => FlightInputHandler.state.mainThrottle = 1f;
            actions["f.throttleUp"] = (v, a) => FlightInputHandler.state.mainThrottle = Mathf.Clamp01(FlightInputHandler.state.mainThrottle + 0.1f);
            actions["f.throttleDown"] = (v, a) => FlightInputHandler.state.mainThrottle = Mathf.Clamp01(FlightInputHandler.state.mainThrottle - 0.1f);
            actions["f.sas"] = (v, a) => Group(v, KSPActionGroup.SAS, a);
            actions["f.rcs"] = (v, a) => Group(v, KSPActionGroup.RCS, a);
            actions["f.light"] = (v, a) => Group(v, KSPActionGroup.Light, a);
            actions["f.gear"] = (v, a) => Group(v, KSPActionGroup.Gear, a);
            actions["f.brake"] = (v, a) => Group(v, KSPActionGroup.Brakes, a);
            actions["f.abort"] = (v, a) => Group(v, KSPActionGroup.Abort, a);
            for (int i = 0; i < CustomGroups.Length; i++)
            {
                KSPActionGroup g = CustomGroups[i];
                actions["f.ag" + (i + 1)] = (v, a) => Group(v, g, a);
            }
            actions["f.setSASMode"] = (v, a) =>
            {
                var mode = (VesselAutopilot.AutopilotMode)Enum.Parse(typeof(VesselAutopilot.AutopilotMode), a[0], true);
                v.Autopilot.SetMode(mode);
            };
            // Fly by wire: v.fbw[pitch,yaw,roll], each -1 to 1, pitch up positive; repeated by the
            // dashboard while a finger is down, released by v.fbwOff or by running out.
            actions["v.fbw"] = (v, a) => FlyByWire.Set(Float(a, 0), Float(a, 1), Float(a, 2));
            actions["v.fbwOff"] = (v, a) => FlyByWire.Release();
            // Target attitude from dragging the navball: v.steerTo[north,east,up,topNorth,topEast,topUp],
            // the nose and the top, repeated while the finger is down; v.steerLetGo when it lifts (the
            // target stays until the player steers). v.steerRoll[1 or -1], repeated while a roll button
            // is held, turns the target about its axis.
            actions["v.steerTo"] = (v, a) => FlyByWire.SteerTo(
                new Vector3d(Float(a, 0), Float(a, 1), Float(a, 2)), new Vector3d(Float(a, 3), Float(a, 4), Float(a, 5)));
            actions["v.steerRoll"] = (v, a) => FlyByWire.SteerRoll(Float(a, 0));
            actions["v.steerLetGo"] = (v, a) => FlyByWire.SteerLetGo();
            // v.steerAltitude[metres]: the turn to the next target is spread over the way to this
            // altitude above sea level; v.steerSCurve[1 or 0]: as an S-curve or linearly;
            // v.steerReset: drops the target and the altitude.
            actions["v.steerAltitude"] = (v, a) => FlyByWire.SetAltitude(Float(a, 0));
            actions["v.steerSCurve"] = (v, a) => FlyByWire.SetSCurve(Float(a, 0) != 0);
            actions["v.steerCurve"] = (v, a) => FlyByWire.SetCurve(a.Length > 0 ? a[0] : "s"); // v.steerCurve[linear|s|auto]
            actions["v.steerReset"] = (v, a) => FlyByWire.Reset();
            actions["v.steerArm"] = (v, a) => FlyByWire.SetArmed(Float(a, 0) != 0); // v.steerArm[1] play, [0] pause
            // Switches between on-rails and physics warp, from 1x.
            actions["t.physicsToggle"] = (v, a) =>
            {
                TimeWarp.SetRate(0, true);
                TimeWarp.fetch.Mode = TimeWarp.WarpMode == TimeWarp.Modes.LOW ? TimeWarp.Modes.HIGH : TimeWarp.Modes.LOW;
            };
            actions["m.toggleMap"] = (v, a) =>
            {
                if (MapView.MapIsEnabled) MapView.ExitMapView();
                else MapView.EnterMapView();
            };
            actions["c.nextMode"] = (v, a) => { if (FlightCamera.fetch != null) FlightCamera.fetch.SetNextMode(); };
            actions["f.precisionControl"] = (v, a) => FlightInputHandler.fetch.precisionMode = !FlightInputHandler.fetch.precisionMode;
            // A maneuver node at the next apoapsis that makes the orbit circular there.
            actions["o.circularizeAp"] = (v, a) => CircularizeAtApoapsis(v);
            // o.nodeLead[percent]: the share of the burn to lie before the node.
            actions["o.nodeLead"] = (v, a) => nodeLead = Math.Max(0, Math.Min(100, Float(a, 0))) / 100.0;
            // Quicksave as F5 does, where the game allows saving now.
            actions["f.quicksave"] = (v, a) =>
            {
                ClearToSaveStatus status = FlightGlobals.ClearToSave();
                if (status != ClearToSaveStatus.CLEAR)
                {
                    ScreenMessages.PostScreenMessage("Cannot quicksave now: " + status, 3f, ScreenMessageStyle.UPPER_CENTER);
                    return;
                }
                GamePersistence.SaveGame("quicksave", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                ScreenMessages.PostScreenMessage("Quicksaving...", 2f, ScreenMessageStyle.UPPER_CENTER);
            };
        }

        /// <summary>0 flight scene ok, 1 paused, 5 not in the flight scene (Telemachus' p.paused values).</summary>
        public static int PauseState()
        {
            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null) return 5;
            if (FlightDriver.Pause) return 1;
            return 0;
        }

        /// <summary>Reads a key; false when the key is unknown or has no value right now.</summary>
        public bool TryRead(string key, out object value)
        {
            value = null;
            if (key == "p.paused") { value = PauseState(); return true; }
            Parsed split = Parse(key);
            SceneReader sceneReader;
            if (sceneReaders.TryGetValue(split.Name, out sceneReader)) { value = sceneReader(split.Args); return true; }
            if (PauseState() != 0) return false;
            Reader reader;
            if (!readers.TryGetValue(split.Name, out reader)) return false;
            value = reader(FlightGlobals.ActiveVessel, split.Args);
            return true;
        }

        // The key split into name and arguments, from the memo where it was split before.
        private Parsed Parse(string key)
        {
            Parsed result;
            if (parsed.TryGetValue(key, out result)) return result;
            result = new Parsed { Step = StepOf(key) };
            Split(key, out result.Name, out result.Args);
            if (parsed.Count < ParsedCap) parsed[key] = result;
            return result;
        }

        // How finely a value is sent: no finer than the dashboard shows it, so a value that has not
        // changed on the screen is not sent again. Distances as the readouts show them (whole metres,
        // then 0.01 km, 0.01 Mm, 0.01 Gm), speeds to 0.1 m/s, times to 0.1 s, directions to 1e-4.
        private static readonly Dictionary<string, Func<double, double>> Steps = new Dictionary<string, Func<double, double>>();

        // Fills Steps: each rounded key with the step it is sent in, which may depend on the value's size.
        static Telemetry()
        {
            Func<double, double> distance = d => { d = Math.Abs(d); return d < 1e4 ? 1 : d < 1e6 ? 10 : d < 1e9 ? 1e4 : 1e7; };
            Func<double, double> speed = d => Math.Abs(d) < 1e4 ? 0.1 : 10;
            Func<double, double> time = d => 0.1;
            foreach (var k in new[] { "v.altitude", "v.heightFromTerrain", "o.ApA", "o.PeA" }) Steps[k] = distance;
            foreach (var k in new[] { "v.surfaceVelocity", "v.surfaceSpeed", "v.verticalSpeed", "v.orbitalVelocity", "o.nodeDv", "o.nodeDvTotal", "n.speed" }) Steps[k] = speed;
            foreach (var k in new[] { "v.missionTime", "o.timeToAp", "o.timeToPe", "o.period", "o.nodeTime", "o.nodeBurnTime", "o.nodeStartIn" }) Steps[k] = time;
            Steps["v.geeForce"] = d => 0.01;
            Steps["f.throttle"] = d => 0.001;
            Steps["o.nodeLead"] = d => 1;
            // The game's numbers as the pages show them: whole funds and seconds, a tenth of science and reputation.
            Steps["g.funds"] = Steps["g.ut"] = d => 1;
            Steps["g.science"] = Steps["g.reputation"] = d => 0.1;
            // Resources show as whole percents of their maximum; four significant digits keep that.
            Steps["r.resource"] = Steps["r.resourceMax"] = d => d == 0 ? 1 : Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(d))) - 3);
        }

        /// <summary>The value rounded to what the dashboard shows of it; directions to 1e-4.</summary>
        public static object Round(string key, object value)
        {
            return Round(key, value, null);
        }

        /// <summary>The same for a key that TryRead has split before, which spares the second look-up of its name.</summary>
        public object RoundRead(string key, object value)
        {
            Parsed known;
            return Round(key, value, parsed.TryGetValue(key, out known) ? known : null);
        }

        // Rounds as Round does; `known`, where there is one, holds the key's step already.
        private static object Round(string key, object value, Parsed known)
        {
            var arr = value as double[];
            if (arr != null)
            {
                var r = new double[arr.Length];
                for (int i = 0; i < arr.Length; i++) r[i] = Math.Round(arr[i], 4);
                return r;
            }
            if (!(value is double)) return value;
            double d = (double)value;
            if (double.IsNaN(d) || double.IsInfinity(d)) return value;
            Func<double, double> step = known != null ? known.Step : StepOf(key);
            if (step == null) return value;
            double s = step(d);
            return Math.Round(d / s) * s;
        }

        // The step a key's value is rounded to, by its name before the brackets; null where it has none.
        private static Func<double, double> StepOf(string key)
        {
            int bracket = key.IndexOf('[');
            Func<double, double> step;
            return Steps.TryGetValue(bracket < 0 ? key : key.Substring(0, bracket), out step) ? step : null;
        }

        /// <summary>Runs a command on the active vessel, or one of every scene. Must be called on the main thread.</summary>
        public bool TryRun(string command)
        {
            string name; string[] args;
            Split(command, out name, out args);
            SceneAction sceneAction;
            if (sceneActions.TryGetValue(name, out sceneAction)) { sceneAction(args); return true; }
            if (PauseState() != 0) return false;
            Action action;
            if (!actions.TryGetValue(name, out action)) return false;
            action(FlightGlobals.ActiveVessel, args);
            return true;
        }

        // Whether the name is a key or a command here, read or run in any scene; arguments do not matter.
        public bool IsKnown(string key)
        {
            string name = Parse(key).Name;
            return key == "p.paused" || readers.ContainsKey(name) || actions.ContainsKey(name) || sceneReaders.ContainsKey(name)
                   || sceneActions.ContainsKey(name);
        }

        // r.resource[name] (index 0) and r.resourceMax[name] (index 1): the vessel's amount or capacity of that
        // resource, 0 where it carries none.
        private object Resource(Vessel v, string[] args, int index)
        {
            if (args.Length == 0) return null;
            if (resourceFrame != Time.frameCount)
            {
                resourceFrame = Time.frameCount;
                resources.Clear();
                foreach (Part p in v.parts)
                    for (int i = 0; i < p.Resources.Count; i++)
                    {
                        PartResource r = p.Resources[i];
                        double[] sums;
                        if (!resources.TryGetValue(r.resourceName, out sums)) resources[r.resourceName] = sums = new double[2];
                        sums[0] += r.amount;
                        sums[1] += r.maxAmount;
                    }
            }
            double[] total;
            return resources.TryGetValue(args[0], out total) ? total[index] : 0d;
        }

        // The share of a node's burn to lie before the node (o.nodeLead), half until the dashboard sets it.
        private static double nodeLead = 0.5;

        /// <summary>
        /// Seconds to burn dv at full thrust, stage by stage as KSP's delta-v readout sees them
        /// (rocket equation per stage), or null when the stages cannot burn it.
        /// </summary>
        private static object BurnTime(Vessel v, double dv)
        {
            VesselDeltaV vdv = v.VesselDeltaV;
            if (vdv == null || vdv.OperatingStageInfo == null) return null;
            var stages = new List<DeltaVStageInfo>(vdv.OperatingStageInfo);
            stages.Sort((x, y) => y.stage.CompareTo(x.stage)); // the stage that burns first, first
            double t = 0, left = dv;
            foreach (DeltaVStageInfo s in stages)
            {
                if (left <= 0) break;
                if (s.thrustActual <= 0 || s.ispActual <= 0 || s.deltaVActual <= 0) continue;
                double ve = s.ispActual * 9.80665;
                double use = Math.Min(left, s.deltaVActual);
                t += s.startMass * 1000 * ve / (s.thrustActual * 1000) * (1 - Math.Exp(-use / ve)); // t, kN
                left -= use;
            }
            return left > 0.5 ? null : (object)t;
        }

        // The speed the navball shows, by its speed mode.
        private static object SpeedShown(Vessel v)
        {
            switch (FlightGlobals.speedDisplayMode)
            {
                case FlightGlobals.SpeedDisplayModes.Orbit: return v.obt_velocity.magnitude;
                case FlightGlobals.SpeedDisplayModes.Target:
                    ITargetable target = FlightGlobals.fetch.VesselTarget;
                    return target != null ? (object)(v.obt_velocity - target.GetObtVelocity()).magnitude : null;
                default: return v.srf_velocity.magnitude;
            }
        }

        // The vessel's first maneuver node, null without one or before the solver exists.
        private static ManeuverNode FirstNode(Vessel v)
        {
            PatchedConicSolver solver = v.patchedConicSolver;
            return solver != null && solver.maneuverNodes.Count > 0 ? solver.maneuverNodes[0] : null;
        }

        /// <summary>
        /// Adds a maneuver node at the next apoapsis whose prograde burn makes the orbit circular
        /// there: the circular speed at that height, sqrt(mu / r), less the speed the vessel will have.
        /// </summary>
        private static void CircularizeAtApoapsis(Vessel v)
        {
            Orbit o = v.orbit;
            PatchedConicSolver solver = v.patchedConicSolver;
            if (solver == null)
            {
                ScreenMessages.PostScreenMessage("Maneuver nodes are not available yet.", 3f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            if (o.eccentricity >= 1 || double.IsNaN(o.timeToAp) || double.IsInfinity(o.timeToAp))
            {
                ScreenMessages.PostScreenMessage("No apoapsis: the orbit is not closed.", 3f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            double ut = Planetarium.GetUniversalTime() + o.timeToAp;
            double r = o.getRelativePositionAtUT(ut).magnitude;
            double speedThere = o.getOrbitalVelocityAtUT(ut).magnitude;
            double circular = Math.Sqrt(o.referenceBody.gravParameter / r);
            ManeuverNode node = solver.AddManeuverNode(ut);
            node.DeltaV = new Vector3d(0, 0, circular - speedThere); // radial, normal, prograde
            solver.UpdateFlightPlan();
        }

        /// <summary>The vessel's north, east and up in world space (all unit vectors).</summary>
        internal static void Frame(Vessel v, out Vector3d north, out Vector3d east, out Vector3d up)
        {
            Vector3d pos = v.CoMD;
            CelestialBody body = v.mainBody;
            up = (pos - body.position).normalized;
            Vector3d pole = body.position + (Vector3d)body.transform.up * body.Radius;
            north = Vector3d.Exclude(up, pole - pos).normalized;
            east = Vector3d.Cross(up, north).normalized;
            // Unity is left-handed; the surface's own motion under the body's rotation points east.
            // Near the poles that motion vanishes and the cross product has to be trusted.
            Vector3d spin = body.getRFrmVel(pos);
            if (spin.magnitude > 1.0 && Vector3d.Dot(east, spin) < 0) east = -east;
        }

        // The vessel and the frame (Time.frameCount) FrameThisFrame last worked out, and its three axes.
        private static Vessel frameVessel;
        private static int frameAt = -1;
        private static Vector3d frameNorth, frameEast, frameUp;

        // Frame, worked out once per rendered frame and vessel for the keys that all ask for it. Only the dashboard's
        // update reads it, after the frame's physics steps, so the vessel does not move between two reads.
        // FlyByWire steers inside the physics steps and calls Frame itself.
        private static void FrameThisFrame(Vessel v, out Vector3d north, out Vector3d east, out Vector3d up)
        {
            if (v != frameVessel || Time.frameCount != frameAt)
            {
                Frame(v, out frameNorth, out frameEast, out frameUp);
                frameVessel = v;
                frameAt = Time.frameCount;
            }
            north = frameNorth; east = frameEast; up = frameUp;
        }

        // A world direction in the vessel's north, east, up frame, as the navball keys send it.
        private static double[] Local(Vessel v, Vector3d world)
        {
            Vector3d up, north, east;
            FrameThisFrame(v, out north, out east, out up);
            return new[] { Vector3d.Dot(world, north), Vector3d.Dot(world, east), Vector3d.Dot(world, up) };
        }

        // The same for a single-precision direction, such as a transform's axis.
        private static double[] Local(Vessel v, Vector3 world)
        {
            return Local(v, (Vector3d)world);
        }

        /// <summary>The direction the navball's prograde marker shows, by the speed display mode.</summary>
        private static Vector3d Prograde(Vessel v)
        {
            switch (FlightGlobals.speedDisplayMode)
            {
                case FlightGlobals.SpeedDisplayModes.Orbit: return v.obt_velocity.normalized;
                case FlightGlobals.SpeedDisplayModes.Target:
                    ITargetable target = FlightGlobals.fetch.VesselTarget;
                    if (target != null) return (v.obt_velocity - target.GetObtVelocity()).normalized;
                    return v.obt_velocity.normalized;
                default: return v.srf_velocity.normalized;
            }
        }

        // Toggles an action group, or with an argument sets it to true or false.
        private static void Group(Vessel v, KSPActionGroup group, string[] args)
        {
            if (args.Length == 0) v.ActionGroups.ToggleGroup(group);
            else v.ActionGroups.SetGroup(group, bool.Parse(args[0]));
        }

        // The argument at i as a number with a decimal point; throws where it is missing or none, which fails
        // the command (DashboardServer.Run logs it).
        private static float Float(string[] args, int i)
        {
            return float.Parse(args[i], CultureInfo.InvariantCulture);
        }

        /// <summary>"f.setThrottle[0.5]" becomes ("f.setThrottle", ["0.5"]).</summary>
        private static void Split(string key, out string name, out string[] args)
        {
            int open = key.IndexOf('[');
            if (open < 0 || !key.EndsWith("]"))
            {
                name = key; args = new string[0];
                return;
            }
            name = key.Substring(0, open);
            string inner = key.Substring(open + 1, key.Length - open - 2);
            args = inner.Length == 0 ? new string[0] : inner.Split(',');
            for (int i = 0; i < args.Length; i++) args[i] = args[i].Trim();
        }
    }
}
