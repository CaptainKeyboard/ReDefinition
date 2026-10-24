using System;
using System.Collections.Generic;
using KSP.UI.Screens;
using ReDefinition.Core;
using ReDefinition.Engineer;
using UnityEngine;

namespace ReDefinition.Dashboard
{
    // What the dashboard's VAB and SPH page shows and does: the Engineer, the vessel as built --
    // its parts, mass, cost and size against what the buildings allow in a career, its stages and
    // their delta-v, TWR, Isp, thrust and burn time in the situation KSP's delta-v app has, its
    // resources and crew --, read from KSP's own delta-v simulation as the staging stack shows it;
    // and the planner (PlannerView.Dashboard.cs), its plan, its lists and its changes. Read in the
    // editors only.
    internal sealed class EditorTelemetry
    {
        // The vessel read again at most this often: KSP's simulation runs by itself as it changes.
        private const float ShipEvery = 0.5f;
        private float shipRead = -1e9f;
        private Json.Raw ship;

        // The list a button folds out, as the dashboard last asked for it, with a number that
        // changes with every request, so that the same list asked again goes out again.
        private int choicesAsked;
        private object choices;

        private static bool InEditor
        {
            get { return HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null; }
        }

        internal object Ship()
        {
            if (!InEditor) return null;
            float now = Time.realtimeSinceStartup;
            if (ship == null || now - shipRead >= ShipEvery)
            {
                shipRead = now;
                ship = new Json.Raw(Json.Value(ReadShip()));
            }
            return ship;
        }

        // Written as JSON once for each snapshot the planner makes, not at every update.
        private object planShot;
        private Json.Raw plan;

        internal object Plan()
        {
            PlannerView planner = Planner();
            if (planner == null) return null;
            Dictionary<string, object> shot = planner.Snapshot();
            if (shot != planShot || plan == null)
            {
                planShot = shot;
                plan = new Json.Raw(Json.Value(shot));
            }
            return plan;
        }

        internal object Choices()
        {
            return InEditor ? choices : null;
        }

        private static PlannerView Planner()
        {
            if (!InEditor || EditorEngineer.Instance == null) return null;
            return EditorEngineer.Instance.PlannerForDashboard();
        }

        // e.plan[change, numbers...]: a change of the plan (PlannerView.Change).
        internal static void Change(string[] args)
        {
            PlannerView planner = Planner();
            if (planner == null || args.Length == 0) return;
            string[] rest = new string[args.Length - 1];
            Array.Copy(args, 1, rest, 0, rest.Length);
            if (!planner.Change(args[0], rest)) LogLine("no planner change " + args[0]);
        }

        // e.choices[list, a, b]: the list a planner button folds out, answered in e.choiceList.
        internal void AskChoices(string[] args)
        {
            PlannerView planner = Planner();
            if (planner == null || args.Length == 0) return;
            int a = Int(args, 1), b = Int(args, 2);
            var entries = new List<object>();
            foreach (Choice choice in planner.ChoicesFor(args[0], a, b))
                entries.Add(new Dictionary<string, object>
                {
                    { "label", choice.Label }, { "tooltip", choice.Tooltip }, { "current", choice.Current }, { "indent", (double)choice.Indent },
                });
            choicesAsked++;
            choices = new Dictionary<string, object>
            {
                { "asked", choicesAsked }, { "list", args[0] }, { "a", a }, { "b", b }, { "entries", entries },
            };
        }

        // e.pick[list, a, b, index, label]: that choice of the list, where it still reads the
        // label (URI-encoded, as a label may hold commas and brackets).
        internal static void Pick(string[] args)
        {
            PlannerView planner = Planner();
            if (planner == null || args.Length < 5) return;
            string label = Uri.UnescapeDataString(string.Join(",", args, 4, args.Length - 4));
            if (!planner.Pick(args[0], Int(args, 1), Int(args, 2), Int(args, 3), label))
                LogLine("the planner's list " + args[0] + " has changed; nothing picked");
        }

        private static int Int(string[] args, int at)
        {
            int value;
            return at < args.Length && int.TryParse(args[at], out value) ? value : -1;
        }

        // ------------------------------------------------------------ Engineer

        private static Dictionary<string, object> ReadShip()
        {
            EditorLogic editor = EditorLogic.fetch;
            ShipConstruct construct = editor.ship;
            bool vab = EditorDriver.editorFacility == EditorFacility.VAB;
            var result = new Dictionary<string, object>
            {
                { "facility", vab ? "VAB" : "SPH" },
                { "name", construct != null ? construct.shipName : null },
                { "stockOff", EditorReadout.StockOff },
            };
            if (construct == null || construct.parts == null || construct.parts.Count == 0)
            {
                result["parts"] = 0;
                return result;
            }

            float dryMass, fuelMass, dryCost, fuelCost;
            double mass = construct.GetShipMass(out dryMass, out fuelMass, ShipConstruction.ShipManifest);
            double cost = construct.GetShipCosts(out dryCost, out fuelCost);
            Vector3 size = ShipConstruction.CalculateCraftSize(construct);
            result["parts"] = construct.parts.Count;
            result["mass"] = mass;
            result["dryMass"] = (double)dryMass;
            result["fuelMass"] = (double)fuelMass;
            result["cost"] = cost;
            result["dryCost"] = (double)dryCost;
            result["fuelCost"] = (double)fuelCost;
            result["size"] = new[] { (double)size.x, (double)size.y, (double)size.z };
            result["limits"] = Limits(vab);

            DeltaVAppValues app = DeltaVGlobals.DeltaVAppValues;
            DeltaVSituationOptions situation = app != null ? app.situation : DeltaVSituationOptions.Vaccum;
            result["situation"] = new Dictionary<string, object>
            {
                { "body", app != null && app.body != null ? app.body.bodyName : null },
                { "mode", situation == DeltaVSituationOptions.SeaLevel ? "Sea level" : situation == DeltaVSituationOptions.Altitude ? "Altitude" : "Vacuum" },
                { "altitude", app != null && situation == DeltaVSituationOptions.Altitude ? (object)app.altitude : null },
            };
            result["stages"] = Stages(construct, situation);
            result["resources"] = Resources(construct);

            int seats = 0;
            foreach (Part part in construct.parts) if (part != null) seats += part.CrewCapacity;
            var crew = new List<object>();
            VesselCrewManifest manifest = ShipConstruction.ShipManifest;
            if (manifest != null)
                foreach (ProtoCrewMember kerbal in manifest.GetAllCrew(false))
                    if (kerbal != null) crew.Add(kerbal.name);
            result["seats"] = seats;
            result["crew"] = crew;
            return result;
        }

        // What the buildings allow at their levels, as KSP checks it at launch: parts by the
        // VAB's or the SPH's level, mass and size by the launch pad's or the runway's; in a
        // career only, where buildings have levels.
        private static object Limits(bool vab)
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.CAREER || GameVariables.Instance == null) return null;
            float editor = ScenarioUpgradeableFacilities.GetFacilityLevel(vab ? SpaceCenterFacility.VehicleAssemblyBuilding : SpaceCenterFacility.SpaceplaneHangar);
            float site = ScenarioUpgradeableFacilities.GetFacilityLevel(vab ? SpaceCenterFacility.LaunchPad : SpaceCenterFacility.Runway);
            int parts = GameVariables.Instance.GetPartCountLimit(editor, vab);
            float mass = GameVariables.Instance.GetCraftMassLimit(site, vab);
            Vector3 size = GameVariables.Instance.GetCraftSizeLimit(site, vab);
            return new Dictionary<string, object>
            {
                { "parts", parts == int.MaxValue ? null : (object)parts },
                { "mass", float.IsInfinity(mass) || mass >= float.MaxValue ? null : (object)(double)mass },
                { "size", float.IsInfinity(size.x) || size.x >= float.MaxValue ? null : (object)new[] { (double)size.x, (double)size.y, (double)size.z } },
            };
        }

        // KSP's stages, the one that fires first first, with the figures of the situation chosen;
        // the TWR also as the stage burns out (its thrust against its end mass).
        private static List<object> Stages(ShipConstruct construct, DeltaVSituationOptions situation)
        {
            var list = new List<object>();
            VesselDeltaV deltaV = construct.vesselDeltaV;
            if (deltaV == null || deltaV.OperatingStageInfo == null) return list;
            var infos = new List<DeltaVStageInfo>(deltaV.OperatingStageInfo);
            infos.RemoveAll(info => info == null);
            infos.Sort((a, b) => b.stage.CompareTo(a.stage));
            foreach (DeltaVStageInfo info in infos)
            {
                float start = info.GetSituationTWR(situation);
                double end = start > 0f && info.endMass > 0f ? start * info.startMass / info.endMass : 0.0;
                list.Add(new Dictionary<string, object>
                {
                    { "stage", info.stage }, { "dv", (double)info.GetSituationDeltaV(situation) }, { "dvVacuum", (double)info.deltaVinVac },
                    { "twr", (double)start }, { "twrEnd", end }, { "isp", info.GetSituationISP(situation) },
                    { "thrust", (double)info.GetSituationThrust(situation) }, { "burn", info.stageBurnTime },
                    { "startMass", (double)info.startMass }, { "endMass", (double)info.endMass },
                });
            }
            return list;
        }

        // The resources in it, each summed over the parts, as KSP names them.
        private static List<object> Resources(ShipConstruct construct)
        {
            var sums = new Dictionary<string, double[]>();
            var order = new List<string>();
            foreach (Part part in construct.parts)
            {
                if (part == null) continue;
                foreach (PartResource resource in part.Resources)
                {
                    double[] sum;
                    if (!sums.TryGetValue(resource.resourceName, out sum))
                    {
                        sums[resource.resourceName] = sum = new double[2];
                        order.Add(resource.resourceName);
                    }
                    sum[0] += resource.amount;
                    sum[1] += resource.maxAmount;
                }
            }
            var list = new List<object>();
            foreach (string name in order)
            {
                PartResourceDefinition definition = PartResourceLibrary.Instance != null ? PartResourceLibrary.Instance.GetDefinition(name) : null;
                list.Add(new Dictionary<string, object>
                {
                    { "name", definition != null && !string.IsNullOrEmpty(definition.displayName) ? definition.displayName : name },
                    { "amount", sums[name][0] }, { "max", sums[name][1] },
                });
            }
            return list;
        }

        private static void LogLine(string message)
        {
            Debug.Log(Log.Tag + " Dashboard: " + message);
        }
    }
}
