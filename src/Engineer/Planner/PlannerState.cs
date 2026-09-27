using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // One vessel's plan: its route, the margin, whether plane changes count, the
    // stages kept for a leg, the lander and who flies each leg. Kept per vessel name in
    // GameData/ReDefinition/PluginData/planner.cfg, so that a craft brings its
    // plan back when it is loaded into the editor again.
    internal sealed class PlannerState
    {
        public readonly List<RouteStop> Stops = new List<RouteStop>();
        public double Margin = 0.1;
        public bool PlaneChanges;
        public readonly Dictionary<int, int> Kept = new Dictionary<int, int>();
        // The highest stage of the lander, -1 for none (StageBudget).
        public int Lander = -1;
        public readonly Dictionary<int, LegMode> Modes = new Dictionary<int, LegMode>();

        internal List<LegMode> ModeList(int legs)
        {
            List<LegMode> list = new List<LegMode>();
            for (int leg = 0; leg < legs; leg++)
            {
                LegMode mode;
                list.Add(Modes.TryGetValue(leg, out mode) ? mode : LegMode.Whole);
            }
            return list;
        }

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
                Debug.LogWarning(Log.Tag + " The engineer view's plans could not be read: " + e.Message);
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
                Debug.LogWarning(Log.Tag + " The engineer view's plan could not be saved: " + e.Message);
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
            }
            foreach (KeyValuePair<int, int> pair in Kept)
            {
                ConfigNode keep = node.AddNode("KEEP");
                keep.AddValue("stage", pair.Key);
                keep.AddValue("leg", pair.Value);
            }
            foreach (KeyValuePair<int, LegMode> pair in Modes)
            {
                if (pair.Value == LegMode.Whole) continue;
                ConfigNode mode = node.AddNode("MODE");
                mode.AddValue("leg", pair.Key);
                mode.AddValue("mode", pair.Value);
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
                StopKind kind;
                if (Enum.IsDefined(typeof(StopKind), stopNode.GetValue("kind") ?? ""))
                {
                    kind = (StopKind)Enum.Parse(typeof(StopKind), stopNode.GetValue("kind"));
                    stop.Kind = kind;
                }
                double altitude;
                stop.Altitude = double.TryParse(stopNode.GetValue("altitude"), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out altitude)
                    ? altitude
                    : bodies.Bodies[body].LowOrbitAltitude;
                bool useAtmosphere;
                if (bool.TryParse(stopNode.GetValue("useAtmosphere"), out useAtmosphere)) stop.UseAtmosphere = useAtmosphere;
                state.Stops.Add(stop);
            }
            // With a stop gone, the legs are not the ones the stages were kept
            // for and the modes set for.
            if (skipped) return state;
            foreach (ConfigNode keep in node.GetNodes("KEEP"))
            {
                int stage, leg;
                if (int.TryParse(keep.GetValue("stage"), out stage) && int.TryParse(keep.GetValue("leg"), out leg))
                    state.Kept[stage] = leg;
            }
            foreach (ConfigNode modeNode in node.GetNodes("MODE"))
            {
                int leg;
                string mode = modeNode.GetValue("mode") ?? "";
                if (int.TryParse(modeNode.GetValue("leg"), out leg) && Enum.IsDefined(typeof(LegMode), mode))
                    state.Modes[leg] = (LegMode)Enum.Parse(typeof(LegMode), mode);
            }
            return state;
        }
    }
}
