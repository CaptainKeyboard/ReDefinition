using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // One vessel's plan: its route, with who flies to each destination and the
    // choices for the steps before it, the margin, whether plane changes count, the
    // stages pinned to legs and the lander. Kept per vessel name in
    // GameData/ReDefinition/PluginData/planner.cfg, so that a craft brings its
    // plan back when it is loaded into the editor again.
    internal sealed class PlannerState
    {
        public readonly List<RouteStop> Stops = new List<RouteStop>();
        public double Margin = 0.1;
        public bool PlaneChanges;
        // Stage number to the legs it is pinned to (StageBudget).
        public readonly Dictionary<int, HashSet<int>> Pins = new Dictionary<int, HashSet<int>>();

        internal bool IsPinned(int stage, int leg)
        {
            HashSet<int> legs;
            return Pins.TryGetValue(stage, out legs) && legs.Contains(leg);
        }

        internal void Pin(int stage, int leg, bool on)
        {
            HashSet<int> legs;
            if (!Pins.TryGetValue(stage, out legs))
            {
                if (!on) return;
                legs = new HashSet<int>();
                Pins[stage] = legs;
            }
            if (on) legs.Add(leg);
            else legs.Remove(leg);
            if (legs.Count == 0) Pins.Remove(stage);
        }

        // The legs after a removed stop move up by one. The leg into it and the
        // leg out of it become one leg: it keeps the pins of neither, since it
        // costs what neither did.
        internal void RemoveStop(int index)
        {
            Dictionary<int, HashSet<int>> pins = new Dictionary<int, HashSet<int>>();
            foreach (KeyValuePair<int, HashSet<int>> pair in Pins)
            {
                HashSet<int> legs = new HashSet<int>();
                foreach (int leg in pair.Value)
                {
                    if (leg == index || leg == index - 1) continue;
                    legs.Add(leg > index ? leg - 1 : leg);
                }
                if (legs.Count > 0) pins[pair.Key] = legs;
            }
            Pins.Clear();
            foreach (KeyValuePair<int, HashSet<int>> pair in pins) Pins[pair.Key] = pair.Value;
        }

        // The highest stage of the lander, -1 for none (StageBudget).
        public int Lander = -1;

        private const string FileName = "planner.cfg";
        private const int MaxPlans = 200;

        // From the home body's surface to its low orbit: where every mission
        // starts.
        internal static PlannerState Default(PlannerBodies bodies)
        {
            PlannerState state = new PlannerState();
            int home = bodies.Home;
            state.Stops.Add(new RouteStop { Body = home, Kind = StopKind.Surface });
            state.Stops.Add(new RouteStop
            {
                Body = home, Kind = StopKind.Orbit,
                Altitude = home < bodies.Bodies.Count ? bodies.Bodies[home].LowOrbitAltitude : 80000.0,
            });
            return state;
        }

        internal static PlannerState Load(string vessel, PlannerBodies bodies)
        {
            try
            {
                string path = PluginData.Path(FileName);
                if (!File.Exists(path)) return Default(bodies);
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return Default(bodies);
                foreach (ConfigNode plan in root.GetNodes("PLAN"))
                {
                    if (plan.GetValue("vessel") != Key(vessel)) continue;
                    PlannerState state = FromNode(plan, bodies);
                    return state.Stops.Count >= 2 ? state : Default(bodies);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The planner's plans could not be read: " + e.Message);
            }
            return Default(bodies);
        }

        internal void Save(string vessel, PlannerBodies bodies)
        {
            try
            {
                string path = PluginData.Path(FileName);
                ConfigNode root = File.Exists(path) ? ConfigNode.Load(path) ?? new ConfigNode() : new ConfigNode();
                // The plans saved last are at the end; the oldest go first.
                List<ConfigNode> others = new List<ConfigNode>();
                foreach (ConfigNode plan in root.GetNodes("PLAN"))
                    if (plan.GetValue("vessel") != Key(vessel)) others.Add(plan);
                ConfigNode kept = new ConfigNode();
                for (int i = Math.Max(0, others.Count - (MaxPlans - 1)); i < others.Count; i++) kept.AddNode(others[i]);
                kept.AddNode(ToNode(vessel, bodies));
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                kept.Save(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The planner's plan could not be saved: " + e.Message);
            }
        }

        // The vessel's name as the file keeps it: escaped, since a config file
        // reads "//" as the start of a comment.
        internal static string Key(string vessel)
        {
            return Uri.EscapeDataString(vessel ?? "");
        }

        internal static bool Exists(string vessel)
        {
            try
            {
                string path = PluginData.Path(FileName);
                if (!File.Exists(path)) return false;
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return false;
                foreach (ConfigNode plan in root.GetNodes("PLAN"))
                    if (plan.GetValue("vessel") == Key(vessel)) return true;
            }
            catch (Exception)
            {
            }
            return false;
        }

        private ConfigNode ToNode(string vessel, PlannerBodies bodies)
        {
            ConfigNode node = new ConfigNode("PLAN");
            node.AddValue("vessel", Key(vessel));
            node.AddValue("margin", Margin.ToString("0.00", CultureInfo.InvariantCulture));
            node.AddValue("planeChanges", PlaneChanges);
            node.AddValue("lander", Lander);
            foreach (RouteStop stop in Stops)
            {
                ConfigNode stopNode = node.AddNode("STOP");
                stopNode.AddValue("body", stop.Body >= 0 && stop.Body < bodies.Bodies.Count ? bodies.Bodies[stop.Body].Id : "");
                stopNode.AddValue("kind", stop.Kind);
                stopNode.AddValue("altitude", stop.Altitude.ToString("0", CultureInfo.InvariantCulture));
                stopNode.AddValue("useAtmosphere", stop.UseAtmosphere);
                stopNode.AddValue("vehicle", stop.Vehicle);
                stopNode.AddValue("landerStays", stop.LanderStays);
                stopNode.AddValue("refuel", stop.Refuel);
                stopNode.AddValue("orbitFirst", stop.OrbitFirst);
            }
            foreach (KeyValuePair<int, HashSet<int>> pair in Pins)
                foreach (int leg in pair.Value)
                {
                    ConfigNode pin = node.AddNode("PIN");
                    pin.AddValue("stage", pair.Key);
                    pin.AddValue("leg", leg);
                }
            return node;
        }

        private static PlannerState FromNode(ConfigNode node, PlannerBodies bodies)
        {
            PlannerState state = new PlannerState();
            double margin;
            if (double.TryParse(node.GetValue("margin"), NumberStyles.Float, CultureInfo.InvariantCulture, out margin))
                state.Margin = Math.Max(0.0, Math.Min(margin, 1.0));
            bool planes;
            if (bool.TryParse(node.GetValue("planeChanges"), out planes)) state.PlaneChanges = planes;
            int lander;
            if (int.TryParse(node.GetValue("lander"), out lander)) state.Lander = Math.Max(-1, lander);
            bool skipped = false;
            foreach (ConfigNode stopNode in node.GetNodes("STOP"))
            {
                int body = bodies.IndexOf(stopNode.GetValue("body"));
                // A body the loaded system does not have: the plan was made for
                // another one.
                if (body < 0)
                {
                    skipped = true;
                    continue;
                }
                RouteStop stop = new RouteStop { Body = body };
                string kind = stopNode.GetValue("kind") ?? "";
                if (Enum.IsDefined(typeof(StopKind), kind) && kind != StopKind.Entry.ToString())
                    stop.Kind = (StopKind)Enum.Parse(typeof(StopKind), kind);
                string vehicle = stopNode.GetValue("vehicle") ?? "";
                if (Enum.IsDefined(typeof(Vehicle), vehicle)) stop.Vehicle = (Vehicle)Enum.Parse(typeof(Vehicle), vehicle);
                double altitude;
                stop.Altitude = double.TryParse(stopNode.GetValue("altitude"), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out altitude)
                    ? altitude
                    : bodies.Bodies[body].LowOrbitAltitude;
                bool useAtmosphere;
                if (bool.TryParse(stopNode.GetValue("useAtmosphere"), out useAtmosphere)) stop.UseAtmosphere = useAtmosphere;
                bool flag;
                if (bool.TryParse(stopNode.GetValue("landerStays"), out flag)) stop.LanderStays = flag;
                if (bool.TryParse(stopNode.GetValue("refuel"), out flag)) stop.Refuel = flag;
                if (bool.TryParse(stopNode.GetValue("orbitFirst"), out flag)) stop.OrbitFirst = flag;
                state.Stops.Add(stop);
            }
            // With a stop gone, the legs are not the ones the stages were pinned to.
            if (skipped) return state;
            foreach (ConfigNode pin in node.GetNodes("PIN"))
            {
                int stage, leg;
                if (int.TryParse(pin.GetValue("stage"), out stage) && int.TryParse(pin.GetValue("leg"), out leg) && leg >= 0)
                    state.Pin(stage, leg, true);
            }
            return state;
        }
    }
}
