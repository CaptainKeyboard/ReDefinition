using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // A saved vessel split in two without the vessel being loaded: the parts that go, and the parts that stay. It works on the vessel's saved node,
    // which is what KSP loads a vessel from. Each part refers to the others by their place in the node: its parent, its symmetric parts, the parts on
    // its attachment nodes. The places are numbered again for each vessel that comes out, and a link to a part that is gone points to none.
    internal static class VesselSplit
    {
        // The parts of the node named by `away` (their saved persistent ids) taken out: the vessel that stays, and a vessel for each group of them that
        // holds together. Null where none of the parts is there, or all are.
        internal static ConfigNode Split(ConfigNode vessel, ICollection<uint> away, out List<ConfigNode> gone)
        {
            gone = new List<ConfigNode>();
            ConfigNode[] parts = vessel.GetNodes("PART");
            var leaving = new HashSet<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                uint id;
                if (uint.TryParse(parts[i].GetValue("persistentId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && away.Contains(id)) leaving.Add(i);
            }
            int root = Int(vessel.GetValue("root"), 0);
            var staying = new List<int>();
            for (int i = 0; i < parts.Length; i++)
                if (!leaving.Contains(i)) staying.Add(i);
            if (leaving.Count == 0 || staying.Count == 0) return null;
            ConfigNode kept = Make(vessel, parts, staying, staying.Contains(root) ? root : staying[0]);
            // The leaving parts fall into groups that hold together by the links between them, and each group is a vessel.
            var seen = new HashSet<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (!leaving.Contains(i) || seen.Contains(i)) continue;
                var group = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(i);
                seen.Add(i);
                while (queue.Count > 0)
                {
                    int part = queue.Dequeue();
                    group.Add(part);
                    foreach (int other in Links(parts, part))
                        if (leaving.Contains(other) && seen.Add(other)) queue.Enqueue(other);
                }
                group.Sort();
                // The part of the group nearest the root is the lowest one: parts are saved after their parents.
                gone.Add(Make(vessel, parts, group, group[0]));
            }
            return kept;
        }

        // The parts a part is linked to: its parent, the parts on its attachment nodes and its surface attachment.
        private static IEnumerable<int> Links(ConfigNode[] parts, int part)
        {
            ConfigNode node = parts[part];
            int parent = Int(node.GetValue("parent"), -1);
            if (parent >= 0 && parent != part) yield return parent;
            foreach (string value in node.GetValues("attN")) { int index = IndexOf(value); if (index >= 0) yield return index; }
            int surface = IndexOf(node.GetValue("srfN"));
            if (surface >= 0) yield return surface;
            for (int other = 0; other < parts.Length; other++)
                if (other != part && Int(parts[other].GetValue("parent"), -1) == part) yield return other;
        }

        // The vessel of the chosen parts, with `top` as its root: a copy of the vessel's node with only these parts, numbered again.
        private static ConfigNode Make(ConfigNode vessel, ConfigNode[] parts, List<int> chosen, int top)
        {
            ConfigNode result = vessel.CreateCopy();
            result.RemoveNodes("PART");
            // The root first: a vessel is loaded from the first part, and a part after its parent.
            chosen.Remove(top);
            chosen.Insert(0, top);
            var number = new Dictionary<int, int>();
            for (int i = 0; i < chosen.Count; i++) number[chosen[i]] = i;
            Vector3 topPosition = ReadVector(parts[top].GetValue("position"));
            Quaternion topRotation = ReadRotation(parts[top].GetValue("rotation"));
            Quaternion inverse = UnityEngine.Quaternion.Inverse(topRotation);
            bool moved = Int(vessel.GetValue("root"), 0) != top;
            foreach (int index in chosen)
            {
                ConfigNode part = parts[index].CreateCopy();
                int parent = Int(part.GetValue("parent"), 0);
                part.SetValue("parent", (index == top ? 0 : (number.ContainsKey(parent) ? number[parent] : 0)).ToString(CultureInfo.InvariantCulture));
                string[] symmetric = part.GetValues("sym");
                part.RemoveValues("sym");
                foreach (string value in symmetric)
                {
                    int other = Int(value, -1);
                    if (number.ContainsKey(other)) part.AddValue("sym", number[other].ToString(CultureInfo.InvariantCulture));
                }
                RenumberNode(part, "srfN", number);
                RenumberNode(part, "attN", number);
                FreePorts(part, parts, chosen);
                if (moved)
                {
                    // Positions are saved from the vessel's root: from the new root now.
                    part.SetValue("position", KSPUtil.WriteVector(inverse * (ReadVector(part.GetValue("position")) - topPosition)));
                    part.SetValue("rotation", KSPUtil.WriteQuaternion(inverse * ReadRotation(part.GetValue("rotation"))));
                }
                result.AddNode(part);
            }
            result.SetValue("root", "0");
            return result;
        }

        // A docking port whose partner is not among the chosen parts is ready to dock again: it is not docked to a part that is in another vessel now.
        private static void FreePorts(ConfigNode part, ConfigNode[] parts, List<int> chosen)
        {
            foreach (ConfigNode module in part.GetNodes("MODULE"))
            {
                if (module.GetValue("name") != "ModuleDockingNode") continue;
                string state = module.GetValue("state") ?? "Ready";
                string partner = module.GetValue("dockUId");
                if (state == "Ready" || partner == null || partner == "0") continue;
                bool kept = false;
                foreach (int index in chosen)
                    if (parts[index].GetValue("persistentId") == partner || parts[index].GetValue("uid") == partner || parts[index].GetValue("cid") == partner) kept = true;
                if (kept) continue;
                module.SetValue("state", "Ready");
                module.SetValue("dockUId", "0");
                module.SetValue("dockNodeIdx", "0");
            }
        }

        // A link saved as "id, index" with the index in the new numbering, or -1 where the part is gone.
        private static void RenumberNode(ConfigNode part, string key, Dictionary<int, int> number)
        {
            string[] values = part.GetValues(key);
            part.RemoveValues(key);
            foreach (string value in values)
            {
                string[] pieces = value.Split(',');
                if (pieces.Length < 2) { part.AddValue(key, value); continue; }
                int index = Int(pieces[1], -1);
                pieces[1] = " " + (number.ContainsKey(index) ? number[index] : -1).ToString(CultureInfo.InvariantCulture);
                part.AddValue(key, string.Join(",", pieces));
            }
        }

        // The index in a link saved as "id, index" or "id, index, mesh", or -1.
        private static int IndexOf(string link)
        {
            if (link == null) return -1;
            string[] pieces = link.Split(',');
            return pieces.Length >= 2 ? Int(pieces[1], -1) : -1;
        }

        private static int Int(string text, int fallback)
        {
            int value;
            return int.TryParse(text == null ? null : text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static Vector3 ReadVector(string text)
        {
            return text == null ? Vector3.zero : KSPUtil.ParseVector3(text);
        }

        private static Quaternion ReadRotation(string text)
        {
            return text == null ? UnityEngine.Quaternion.identity : KSPUtil.ParseQuaternion(text);
        }
    }
}
