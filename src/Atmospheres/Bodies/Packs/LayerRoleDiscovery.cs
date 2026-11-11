using System;
using System.Collections.Generic;
using System.Text;

namespace ReDefinition.Atmospheres
{
    // What the discovery found on one body: the layers it takes, in the order of their altitude, the reason for every other layer, and
    // the line for the log. Built by LayerRoleDiscovery.
    internal sealed class LayerDiscovery
    {
        internal LayerDiscovery(string body, IReadOnlyList<LayerDecision> decisions, int cap)
        {
            Body = body ?? "";
            Decisions = decisions;
            Cap = cap;
            var taken = new List<PackLayerInfo>();
            foreach (LayerDecision d in decisions)
            {
                if (d.Taken) taken.Add(d.Layer);
            }
            Taken = taken;
        }

        internal string Body { get; }
        internal IReadOnlyList<LayerDecision> Decisions { get; }
        // The layers we drive.
        internal IReadOnlyList<PackLayerInfo> Taken { get; }
        // The cap that applied (0: none).
        internal int Cap { get; }

        // True where no layer qualifies: the clouds are then our own (EffectiveSource).
        internal bool None
        {
            get { return Taken.Count == 0; }
        }

        // The roles that at least one taken layer plays.
        internal LayerRoles Roles
        {
            get
            {
                LayerRoles all = LayerRoles.None;
                foreach (LayerDecision d in Decisions)
                {
                    if (d.Taken) all |= d.Roles;
                }
                return all;
            }
        }

        // cloudSource when EVE has no layer for the body: Eve and Both fall back to Ours, the other values stand.
        internal CloudSourceKind EffectiveSource(CloudSourceKind requested)
        {
            if (!None) return requested;
            return requested == CloudSourceKind.Eve || requested == CloudSourceKind.Both ? CloudSourceKind.Ours : requested;
        }

        // The status a body is listed with when EVE is its renderer and nothing qualifies: Full falls to CoverOnly (the ground cover
        // runs, the decks are not EVE's); any other status stands.
        internal BodyStatus StatusFor(BodyStatus status, CloudSourceKind requested)
        {
            if (status == null) return null;
            bool needsEve = requested == CloudSourceKind.Eve || requested == CloudSourceKind.Both;
            return status.Kind == BodyStatusKind.Full && needsEve && None ? BodyStatus.CoverOnly : status;
        }

        // The memory the maps of the layers we drive hold, in MB: per layer two render textures and two readable copies of the
        // map (coverage and type), one byte per texel (MapPainter: R8). TVC's own noise, curve and light textures are not counted.
        internal double MapMegabytes(int width, int height)
        {
            return Taken.Count * 4.0 * width * (double)height / (1024.0 * 1024.0);
        }

        // One line for the log: the body, the layers taken with their roles, the layers left with the reason.
        internal string Summary()
        {
            var text = new StringBuilder();
            text.Append("layers on ").Append(Body).Append(": ").Append(Taken.Count).Append(" taken");
            if (Cap > 0) text.Append(" (cap ").Append(Cap).Append(')');
            foreach (LayerDecision d in Decisions)
            {
                text.Append(d.Taken ? "; + " : "; - ").Append(d.Layer.Name).Append(" [").Append(RoleText(d.Roles)).Append(']');
                if (!d.Taken) text.Append(' ').Append(d.Skip);
            }
            return text.ToString();
        }

        internal static string RoleText(LayerRoles roles)
        {
            return roles == LayerRoles.None ? "no role" : roles.ToString().Replace(", ", "+");
        }
    }

    // Finds the layers of a body that play a role for us, by the cloud type names and the body's role lists (CLOUDS: snowTypeNames,
    // stratusTypeNames, dustTypeNames). Pure. Every qualifying layer is taken, never one assumed; a body with the same role in two packs
    // (two packs that both put layers on the home planet) gets both, up to the cap. A layer qualifies with a coverage map, a type map, a cloud type of a
    // role and not being an fx only layer. discover = Off takes none; discover = Explicit, or a non-empty weatherLayers list, takes the
    // named layers (also those without a role: the owner of the node asked for them).
    internal static class LayerRoleDiscovery
    {
        internal static LayerDiscovery Discover(IEnumerable<PackLayerInfo> layers, string body, CloudRules rules, int cap)
        {
            CloudRules r = rules ?? CloudRules.Default;
            var onBody = new List<PackLayerInfo>();
            var decisions = new List<LayerDecision>();
            if (layers != null)
            {
                foreach (PackLayerInfo layer in layers)
                {
                    if (layer == null) continue;
                    if (!string.Equals(layer.Body, body, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    onBody.Add(layer);
                }
            }
            onBody.Sort((a, b) =>
            {
                int byAltitude = a.AltitudeM.CompareTo(b.AltitudeM);
                return byAltitude != 0 ? byAltitude : string.CompareOrdinal(a.Name, b.Name);
            });
            bool named = r.Discover == CloudDiscovery.Explicit || r.WeatherLayers.Count > 0;
            int taken = 0;
            foreach (PackLayerInfo layer in onBody)
            {
                LayerRoles roles = RolesOf(layer, r);
                SkipReason skip = SkipReason.None;
                if (r.Discover == CloudDiscovery.Off) skip = SkipReason.DiscoveryOff;
                else if (layer.FxOnly) skip = SkipReason.FxOnly;
                else if (!layer.HasCoverageMap || !layer.HasTypeMap) skip = SkipReason.NoMaps;
                else if (named && !IsNamed(r, layer.Name)) skip = SkipReason.NotNamed;
                else if (!named && roles == LayerRoles.None) skip = SkipReason.NoRole;
                if (skip == SkipReason.None)
                {
                    if (cap > 0 && taken >= cap) skip = SkipReason.OverCap;
                    else taken++;
                }
                decisions.Add(new LayerDecision(layer, roles, skip));
            }
            return new LayerDiscovery(body, decisions, cap);
        }

        // The roles a layer plays: one flag for every role list that one of its cloud types is in (the storm kind by name).
        internal static LayerRoles RolesOf(PackLayerInfo layer, CloudRules rules)
        {
            LayerRoles roles = LayerRoles.None;
            if (layer == null) return roles;
            foreach (string type in layer.TypeNames)
            {
                if (Contains(rules.StratusTypeNames, type)) roles |= LayerRoles.Stratus;
                else if (Contains(rules.SnowTypeNames, type)) roles |= LayerRoles.Snow;
                else if (Contains(rules.DustTypeNames, type)) roles |= LayerRoles.Dust;
                else if (TypeRoles.IsStormName(type)) roles |= LayerRoles.Storm;
            }
            return roles;
        }

        private static bool IsNamed(CloudRules rules, string name)
        {
            // Layer names are matched exactly, as the scan of the game does (a pack's own names differ in case from layer to layer).
            for (int i = 0; i < rules.WeatherLayers.Count; i++)
            {
                if (string.Equals(rules.WeatherLayers[i], name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool Contains(IReadOnlyList<string> list, string name)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
