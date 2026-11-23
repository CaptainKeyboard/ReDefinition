using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // What the layer set of a body is resolved against (E40 Q8a): the body's facts and rules and the layer templates of the GameDatabase.
    internal sealed class CloudLayerContext
    {
        internal BodyFacts Facts;
        internal GeneratorRules Generator;
        internal TemperatureRules Temperature;
        internal BodyStatus Status;
        // The REDEFINITION_CLOUD_TEMPLATE nodes; null or empty: there is no template.
        internal IReadOnlyList<ConfigNode> Templates;
        // The height scale (CloudHeightScale): on, and the viewer's factor; the parser sets both from the tuning keys, tests by hand.
        internal bool HeightScaleOn;
        internal double HeightFactor = 1.0;

        // The depth of the body's troposphere in m (the generator's rule); NaN where it is not known.
        internal double DepthM
        {
            get { return Generator == null || Facts == null ? double.NaN : Generator.TroposphereDepthM(Facts.AtmosphereDepthM); }
        }
    }

    // Which layer set a body draws (E40 Q8a). CLOUDS key layers: auto (default; the authored LAYER nodes where there are any, else the template), none (no own layers),
    // template (the template, each LAYER node of the body changes the template's layer of its name). Key layerTemplate names the template (Earthlike). The template's
    // LAYER nodes give their lengths in troposphere depths (innerDepths, outerDepths, noiseScaleDepths, lightCellWidthDepths) and say with needs = liquid | ice which
    // water phase they need; a layer whose phase the body's troposphere cannot hold is left out. The note says which set and why; nothing here throws.
    internal static class CloudLayerTemplate
    {
        internal const string NodeName = "REDEFINITION_CLOUD_TEMPLATE";
        internal const string DefaultName = "Earthlike";
        // The lapse rate in K/m where the body says none (the standard atmosphere's troposphere), and the heights the troposphere is sampled at.
        internal const double DefaultLapseKPerM = 0.0065;
        private const int TemperatureSamples = 33;
        // Liquid water clouds form between the homogeneous freezing point (40 K under the freezing point) and the boiling point (100 K over it).
        internal const double HomogeneousFreezingBelowK = 40.0, BoilingAboveK = 100.0;

        internal static List<CloudLayerSpec> Choose(BodyNodeReader clouds, CloudLayerContext ctx, out string note)
        {
            double metreScale, scaledDepthM;
            return Choose(clouds, ctx, out note, out metreScale, out scaledDepthM);
        }

        // metreScale: the factor on the metre lengths of the set (CloudHeightScale; 1 where the height scale is off, the troposphere not measured or the set names no reference).
        // scaledDepthM: the troposphere of the height scale (the measured one times the factor), NaN where the scale does not act.
        internal static List<CloudLayerSpec> Choose(BodyNodeReader clouds, CloudLayerContext ctx, out string note, out double metreScale, out double scaledDepthM)
        {
            scaledDepthM = double.NaN;
            string mode = clouds.String("layers", "auto").ToLowerInvariant();
            if (mode != "auto" && mode != "none" && mode != "template")
            {
                clouds.Warn("layers", "'" + mode + "' is not auto, none or template; auto is used");
                mode = "auto";
            }
            string name = clouds.String("layerTemplate", DefaultName);
            ConfigNode[] own = clouds.Nodes("LAYER");
            double depth = ctx == null ? double.NaN : ctx.DepthM;
            // The height scale (owner 2026-11-23): both keys are read always (an unread key is reported as a typo).
            double referenceM = clouds.Double("heightReferenceM", 0.0, 0.0, 1.0e6);
            float bodyFactor = clouds.Float("heightFactor", CloudHeightScale.FactorDefault, CloudHeightScale.FactorMin, CloudHeightScale.FactorMax);
            metreScale = 1.0;
            string scaleNote = "";
            if (ctx != null && ctx.HeightScaleOn)
            {
                double factor = CloudHeightScale.Clamp((float)(bodyFactor * ctx.HeightFactor));
                double t = CloudHeightScale.TroposphereM(ctx.Facts != null ? ctx.Facts.MeasuredTropopauseM : double.NaN, factor);
                if (t > 0)
                {
                    depth = t;
                    scaledDepthM = t;
                    metreScale = CloudHeightScale.MetreScale(t, referenceM);
                    scaleNote = ", height scale: measured troposphere x " + factor.ToString("0.##", CultureInfo.InvariantCulture) + " = " + (t * 0.001).ToString("0.##", CultureInfo.InvariantCulture)
                                + " km, metres x " + metreScale.ToString("0.###", CultureInfo.InvariantCulture);
                }
                else scaleNote = ", height scale: no measured troposphere, the layers as written";
            }
            List<CloudLayerSpec> chosen = ChooseSet(clouds, ctx, mode, name, own, depth, metreScale, out note);
            note += scaleNote;
            return chosen;
        }

        private static List<CloudLayerSpec> ChooseSet(BodyNodeReader clouds, CloudLayerContext ctx, string mode, string name, ConfigNode[] own, double depth, double metreScale, out string note)
        {
            var none = new List<CloudLayerSpec>();
            if (mode == "none")
            {
                if (own.Length > 0) clouds.Warn("layers", "layers = none: the " + own.Length + " LAYER nodes are ignored");
                note = "none (layers = none)";
                return none;
            }
            if (mode == "auto" && own.Length > 0)
            {
                List<CloudLayerSpec> authored = BodyRuleReaders.CloudLayers(clouds, depth, metreScale);
                note = "authored, " + authored.Count + " layer" + (authored.Count == 1 ? "" : "s");
                return authored;
            }
            if (ctx == null || ctx.Templates == null || ctx.Templates.Count == 0)
            {
                note = "none (no LAYER node and no layer template)";
                return none;
            }
            if (ctx.Status == null || !ctx.Status.WeatherRuns)
            {
                note = "none (" + (ctx.Status == null ? "no status" : ctx.Status.ToString()) + ")";
                return none;
            }
            if (!(depth > 0))
            {
                note = "none (the depth of the troposphere is not known)";
                return none;
            }
            ConfigNode template = Find(ctx.Templates, name);
            if (template == null)
            {
                clouds.Warn("layerTemplate", "there is no " + NodeName + " named " + name + "; the body draws no own layers");
                note = "none (no template " + name + ")";
                return none;
            }

            bool liquid, ice;
            string phases = Phases(ctx, depth, out liquid, out ice);
            var set = new ConfigNode("CLOUDS");
            var dropped = new List<string>();
            foreach (ConfigNode layer in template.GetNodes("LAYER"))
            {
                ConfigNode copy = layer.CreateCopy();
                string needs = (copy.GetValue("needs") ?? "").Trim().ToLowerInvariant();
                Remove(copy, "needs");
                if (needs.Length > 0 && needs != "liquid" && needs != "ice")
                    clouds.Warn("layerTemplate", "the template " + name + " says needs = " + needs + " (liquid or ice) for " + copy.GetValue("name") + "; the layer is kept");
                if ((needs == "liquid" && !liquid) || (needs == "ice" && !ice))
                {
                    dropped.Add(copy.GetValue("name") ?? "?");
                    continue;
                }
                set.AddNode(copy);
            }
            if (mode == "template")
            {
                foreach (ConfigNode change in own) Merge(set, change, clouds);
            }
            List<CloudLayerSpec> layers = BodyRuleReaders.CloudLayers(clouds.Wrap(set, "template[" + name + "]"), depth, metreScale);
            string km = (depth * 0.001).ToString("0.##", CultureInfo.InvariantCulture);
            string left = dropped.Count == 0 ? "" : ", without " + string.Join(", ", dropped.ToArray());
            if (layers.Count == 0)
            {
                note = "none (template " + name + ": " + phases + ")";
                return layers;
            }
            note = "template " + name + ", " + layers.Count + " layer" + (layers.Count == 1 ? "" : "s") + ", troposphere " + km + " km, " + phases + left
                + (mode == "template" ? ", changed by " + own.Length + " LAYER node" + (own.Length == 1 ? "" : "s") : "");
            return layers;
        }

        private static ConfigNode Find(IReadOnlyList<ConfigNode> templates, string name)
        {
            ConfigNode found = null;
            foreach (ConfigNode t in templates)
            {
                string n = t == null ? null : t.GetValue("name");
                if (n != null && string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase)) found = t;
            }
            return found;
        }

        // Which water phases the body's troposphere holds: liquid where a height lies between the homogeneous freezing point and the boiling point, ice where one lies below
        // the freezing point (273.15 K + the body's waterCelsius). The temperature is the body's target curve, else the surface temperature with the lapse rate; without any
        // temperature water is assumed. The text says what was found.
        internal static string Phases(CloudLayerContext ctx, double depthM, out bool liquid, out bool ice)
        {
            double freeze = 273.15 + (ctx.Temperature != null ? ctx.Temperature.WaterCelsius : 0.0);
            double low = freeze - HomogeneousFreezingBelowK, boil = freeze + BoilingAboveK;
            Curve curve = ctx.Temperature != null && ctx.Temperature.Target != null ? ctx.Temperature.Target : ctx.Facts.TemperatureTarget;
            double surface = ctx.Generator != null && ctx.Generator.SurfaceMeanK > 0 ? ctx.Generator.SurfaceMeanK : ctx.Facts.SeaLevelTemperatureK;
            double lapse = ctx.Generator != null && ctx.Generator.LapseKPerM > 0 ? ctx.Generator.LapseKPerM : DefaultLapseKPerM;
            if (curve == null && !(surface > 0))
            {
                liquid = ice = true;
                return "no temperature known: water assumed";
            }
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            liquid = ice = false;
            for (int i = 0; i < TemperatureSamples; i++)
            {
                double h = depthM * i / (TemperatureSamples - 1);
                double t = curve != null ? curve.Evaluate((float)h) : surface - lapse * h;
                min = Math.Min(min, t);
                max = Math.Max(max, t);
                if (t >= low && t <= boil) liquid = true;
                if (t < freeze) ice = true;
            }
            string range = min.ToString("0", CultureInfo.InvariantCulture) + " to " + max.ToString("0", CultureInfo.InvariantCulture) + " K";
            if (!liquid && !ice) return "no water cloud forms: " + range + ", warmer than water's boiling point at every height";
            if (!liquid) return "ice only: " + range;
            if (!ice) return "no ice: " + range;
            return range;
        }

        // One LAYER node of the body on the template's set: its keys replace the template layer's, its sub-nodes replace the template layer's sub-node of the same kind and
        // name in place (a TYPE by name, MESO whole), enabled = false drops the layer or the sub-node; a layer of a new name is added.
        private static void Merge(ConfigNode set, ConfigNode change, BodyNodeReader clouds)
        {
            string name = change.GetValue("name");
            if (string.IsNullOrEmpty(name))
            {
                clouds.Warn("LAYER", "a LAYER node without a name cannot change the template; it is ignored");
                return;
            }
            ConfigNode target = Named(set.GetNodes("LAYER"), name);
            if (Disabled(change))
            {
                if (target != null) Replace(set, target, null);
                return;
            }
            if (target == null)
            {
                ConfigNode added = change.CreateCopy();
                Remove(added, "enabled");
                set.AddNode(added);
                return;
            }
            foreach (ConfigNode.Value v in change.values)
            {
                if (v.name == "name" || v.name == "enabled") continue;
                target.SetValue(v.name, v.value, true);
            }
            foreach (ConfigNode child in change.nodes)
            {
                string childName = child.GetValue("name");
                ConfigNode[] same = target.GetNodes(child.name);
                ConfigNode old = childName == null ? (same.Length > 0 ? same[0] : null) : Named(same, childName);
                ConfigNode copy = null;
                if (!Disabled(child))
                {
                    copy = child.CreateCopy();
                    Remove(copy, "enabled");
                }
                if (old != null) Replace(target, old, copy);
                else if (copy != null) target.AddNode(copy);
            }
        }

        private static ConfigNode Named(ConfigNode[] nodes, string name)
        {
            foreach (ConfigNode n in nodes)
            {
                string v = n.GetValue("name");
                if (v != null && v.Trim() == name.Trim()) return n;
            }
            return null;
        }

        private static bool Disabled(ConfigNode node)
        {
            string v = node.GetValue("enabled");
            return v != null && v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        // Replaces one sub-node of parent by another (null: removes it), keeping the order of the others (the order of the TYPE nodes is the type axis).
        private static void Replace(ConfigNode parent, ConfigNode old, ConfigNode replacement)
        {
            var children = new List<ConfigNode>();
            foreach (ConfigNode c in parent.nodes) children.Add(c);
            parent.ClearNodes();
            foreach (ConfigNode c in children)
            {
                if (ReferenceEquals(c, old))
                {
                    if (replacement != null) parent.AddNode(replacement);
                }
                else parent.AddNode(c);
            }
        }

        private static void Remove(ConfigNode node, string key)
        {
            while (node.HasValue(key)) node.RemoveValue(key);
        }
    }
}
