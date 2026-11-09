using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Planner.Autopilot.Background
{
    // The routes in the save file. What is written carries a version; a route of another version, or one with a figure that cannot be an orbit, is left out
    // with a line in the log, and not applied to a vessel.
    internal static class RoutePersistence
    {
        private const int Version = 1;

        internal static List<RouteEntry> Load(ConfigNode node)
        {
            var entries = new List<RouteEntry>();
            foreach (ConfigNode route in node.GetNodes("ROUTE"))
            {
                RouteEntry entry = null;
                try
                {
                    entry = ReadEntry(route);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " Route: the route of " + (route.GetValue("name") ?? "") + " in the save could not be read and is left out: " + e.Message);
                }
                if (entry != null) entries.Add(entry);
            }
            return entries;
        }

        private static RouteEntry ReadEntry(ConfigNode route)
        {
            string name = route.GetValue("name") ?? "";
            if ((int)Read(route, "version", 0.0) != Version)
            {
                Debug.LogWarning(Log.Tag + " Route: the route of " + name + " in the save is of another version and is left out.");
                return null;
            }
            Guid vessel;
            if (!Guid.TryParse(route.GetValue("vessel"), out vessel))
            {
                Debug.LogWarning(Log.Tag + " Route: the route of " + name + " in the save names no vessel and is left out.");
                return null;
            }
            var entry = new RouteEntry
            {
                Vessel = vessel, Name = name, Key = route.GetValue("key") ?? "", Lowest = Read(route, "lowest"), Footprint = Read(route, "footprint"), Reached = (int)Read(route, "reached", -1.0),
                Down = route.HasValue("downHeight"), DownLatitude = Read(route, "downLatitude"), DownLongitude = Read(route, "downLongitude"), DownHeight = Read(route, "downHeight"),
                Wet = route.HasValue("downWet"), Left = true,
            };
            if (!IsFinite(entry.Lowest, entry.Footprint, entry.DownLatitude, entry.DownLongitude, entry.DownHeight))
            {
                Debug.LogWarning(Log.Tag + " Route: the route of " + name + " in the save has a figure that is not a number and is left out.");
                return null;
            }
            foreach (string propellant in route.GetValues("propellant")) entry.Propellants.Add(propellant);
            foreach (ConfigNode stepNode in route.GetNodes("STEP"))
            {
                BackgroundStep step = ReadStep(stepNode);
                if (step == null)
                {
                    // The steps after it were planned on the vessel having been through it.
                    Debug.LogWarning(Log.Tag + " Route: a step of " + name + " in the save is not a step that can be made, and the route is left out.");
                    return null;
                }
                entry.Steps.Add(step);
            }
            RouteCompiler.SortByTime(entry);
            return entry;
        }

        // The step in the node, or null where its figures cannot be the step of a vessel: a body that is not named, an orbit that is not one, a time that is not a time.
        internal static BackgroundStep ReadStep(ConfigNode node)
        {
            StepKind kind;
            if (!Enum.TryParse(node.GetValue("kind") ?? "", out kind) || !Enum.IsDefined(typeof(StepKind), kind)) return null;
            var step = new BackgroundStep
            {
                Kind = kind, Body = node.GetValue("body") ?? "", Why = node.GetValue("why") ?? "", UT = Read(node, "ut"), Inclination = Read(node, "inc"),
                Eccentricity = Read(node, "ecc"), SemiMajorAxis = Read(node, "sma"), Lan = Read(node, "lan"), ArgumentOfPeriapsis = Read(node, "arg"), MeanAnomaly = Read(node, "mna"),
                Epoch = Read(node, "epoch"), Keep = Read(node, "keep", 1.0), Reached = (int)Read(node, "reached", -1.0), GroundUT = Read(node, "ground"),
            };
            if (!IsFinite(step.UT) || step.UT <= 0.0 || !IsFinite(step.Keep) || step.Keep < 0.0 || step.Keep > 1.0) return null;
            if (kind != StepKind.Release)
            {
                if (step.Body.Length == 0 || step.SemiMajorAxis == 0.0 || step.Eccentricity < 0.0
                    || !IsFinite(step.Inclination, step.Eccentricity, step.SemiMajorAxis, step.Lan, step.ArgumentOfPeriapsis, step.MeanAnomaly, step.Epoch)) return null;
                // An ellipse has a positive semi-major axis, a hyperbola a negative one.
                if ((step.Eccentricity < 1.0) != (step.SemiMajorAxis > 0.0)) return null;
            }
            foreach (string kept in node.GetValues("kept"))
            {
                string[] pieces = kept.Split(' ');
                uint id;
                double share;
                if (pieces.Length == 2 && uint.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                    && double.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out share) && IsFinite(share) && share >= 0.0 && share <= 1.0) step.Keeps[id] = share;
            }
            foreach (ConfigNode cutNode in node.GetNodes("CUT"))
            {
                var cut = new PartsCut { Name = cutNode.GetValue("name") ?? "" };
                VesselType type;
                cut.Type = Enum.TryParse(cutNode.GetValue("type") ?? "", out type) ? type : VesselType.Debris;
                foreach (string part in cutNode.GetValues("part"))
                {
                    uint id;
                    if (uint.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) cut.Parts.Add(id);
                }
                step.Cuts.Add(cut);
            }
            if (kind == StepKind.Touchdown)
            {
                step.Site = new TouchdownSite
                {
                    Latitude = Read(node, "siteLatitude"), Longitude = Read(node, "siteLongitude"), Height = Read(node, "siteHeight"), Ground = Read(node, "siteGround"),
                    Tilt = Read(node, "siteTilt"), Score = Read(node, "siteScore"), Shift = Read(node, "siteShift"), Found = true,
                    Water = node.GetValue("siteWater") == "True", Rotation = KSPUtil.ParseQuaternion(node.GetValue("siteRotation") ?? "0,0,0,1"),
                };
                if (!IsFinite(step.GroundUT, step.Site.Latitude, step.Site.Longitude, step.Site.Height, step.Site.Ground, step.Site.Tilt, step.Site.Score, step.Site.Shift)) return null;
            }
            return step;
        }

        internal static void Save(IEnumerable<RouteEntry> entries, ConfigNode node)
        {
            foreach (RouteEntry entry in entries)
            {
                ConfigNode route = node.AddNode("ROUTE");
                route.AddValue("version", Version);
                route.AddValue("vessel", entry.Vessel.ToString());
                route.AddValue("name", entry.Name);
                route.AddValue("key", entry.Key);
                Write(route, "lowest", entry.Lowest);
                Write(route, "footprint", entry.Footprint);
                if (entry.Down)
                {
                    Write(route, "downLatitude", entry.DownLatitude);
                    Write(route, "downLongitude", entry.DownLongitude);
                    Write(route, "downHeight", entry.DownHeight);
                    if (entry.Wet) route.AddValue("downWet", true);
                }
                route.AddValue("reached", entry.Reached);
                foreach (string name in entry.Propellants) route.AddValue("propellant", name);
                foreach (BackgroundStep step in entry.Steps) WriteStep(route.AddNode("STEP"), step);
            }
        }

        internal static void WriteStep(ConfigNode node, BackgroundStep step)
        {
            node.AddValue("kind", step.Kind.ToString());
            node.AddValue("body", step.Body ?? "");
            node.AddValue("why", step.Why);
            Write(node, "ut", step.UT);
            Write(node, "inc", step.Inclination);
            Write(node, "ecc", step.Eccentricity);
            Write(node, "sma", step.SemiMajorAxis);
            Write(node, "lan", step.Lan);
            Write(node, "arg", step.ArgumentOfPeriapsis);
            Write(node, "mna", step.MeanAnomaly);
            Write(node, "epoch", step.Epoch);
            Write(node, "keep", step.Keep);
            node.AddValue("reached", step.Reached);
            foreach (KeyValuePair<uint, double> kept in step.Keeps)
                node.AddValue("kept", kept.Key.ToString(CultureInfo.InvariantCulture) + " " + kept.Value.ToString("R", CultureInfo.InvariantCulture));
            foreach (PartsCut cut in step.Cuts)
            {
                ConfigNode cutNode = node.AddNode("CUT");
                cutNode.AddValue("name", cut.Name);
                cutNode.AddValue("type", cut.Type.ToString());
                foreach (uint part in cut.Parts) cutNode.AddValue("part", part.ToString(CultureInfo.InvariantCulture));
            }
            if (step.Kind != StepKind.Touchdown) return;
            Write(node, "ground", step.GroundUT);
            Write(node, "siteLatitude", step.Site.Latitude);
            Write(node, "siteLongitude", step.Site.Longitude);
            Write(node, "siteHeight", step.Site.Height);
            Write(node, "siteGround", step.Site.Ground);
            Write(node, "siteTilt", step.Site.Tilt);
            Write(node, "siteScore", step.Site.Score);
            Write(node, "siteShift", step.Site.Shift);
            node.AddValue("siteWater", step.Site.Water);
            node.AddValue("siteRotation", KSPUtil.WriteQuaternion(step.Site.Rotation));
        }

        private static bool IsFinite(params double[] values)
        {
            foreach (double value in values)
                if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            return true;
        }

        private static double Read(ConfigNode node, string name, double fallback = 0.0)
        {
            double value;
            return double.TryParse(node.GetValue(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static void Write(ConfigNode node, string name, double value)
        {
            node.AddValue(name, value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
