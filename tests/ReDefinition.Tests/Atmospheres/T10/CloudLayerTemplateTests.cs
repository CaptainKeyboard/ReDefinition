using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.B13;
using ReDefinition.Tests.Atmospheres.C25;
using ReDefinition.Tests.Atmospheres.E41;
using ReDefinition.Tests.Atmospheres.T09;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // E40 Q8a: a body without an authored layer set draws the layer template (Bodies/Clouds-Template.cfg) sized to its troposphere; an authored set always wins.
    [TestClass]
    public class CloudLayerTemplateTests
    {
        // The lengths of a layer, in m in the parsed spec and in troposphere depths in the template (and what follows from them).
        private static readonly HashSet<string> Lengths = new HashSet<string>(StringComparer.Ordinal) { "InnerAltitudeM", "OuterAltitudeM", "NoiseScaleM", "LightCellWidth", "PeakOpticalDepth" };
        private const double LengthTolerance = 5e-4;

        internal static List<ConfigNode> Templates()
        {
            string file = Path.Combine(E41Support.Root(), "GameData", "ReDefinition", "Atmospheres", "Bodies", "Clouds-Template.cfg");
            return ConfigNode.Load(file).GetNodes(CloudLayerTemplate.NodeName).ToList();
        }

        private static BodyFacts KerbinFacts()
        {
            return E41Support.StockBodies().Single(s => s.Facts.Name == "Kerbin").Facts;
        }

        private static BodyParseResult Run(BodyFacts facts, string nodeText = null)
        {
            return BodyConfigParser.Parse(facts, nodeText == null ? null : BodyTestSupport.Parse(nodeText), null, 7, Templates());
        }

        // Every field of two specs (properties and fields, through the types, curves, MESO, CLIMATE and the deck rule); a length within a relative tolerance, the rest exactly.
        private static void Compare(object a, object b, string path, double lengthTolerance, List<string> diffs, Dictionary<string, double> residuals, string member = "")
        {
            if (a == null || b == null)
            {
                if (!(a == null && b == null)) diffs.Add(path + ": one is null");
                return;
            }
            Type t = a.GetType();
            if (t != b.GetType())
            {
                diffs.Add(path + ": " + t.Name + " against " + b.GetType().Name);
                return;
            }
            if (t == typeof(float) || t == typeof(double))
            {
                double x = Convert.ToDouble(a), y = Convert.ToDouble(b);
                if (Lengths.Contains(member))
                {
                    double r = Math.Abs(x - y) / Math.Max(1e-9, Math.Abs(y));
                    if (x == y) r = 0.0;
                    double old;
                    if (!residuals.TryGetValue(member, out old) || r > old) residuals[member] = r;
                    if (r > lengthTolerance) diffs.Add(path + ": " + x + " against " + y);
                }
                else if (!(x == y || (double.IsNaN(x) && double.IsNaN(y)))) diffs.Add(path + ": " + x + " against " + y);
                return;
            }
            if (t.IsPrimitive || t.IsEnum || t == typeof(string))
            {
                if (!a.Equals(b)) diffs.Add(path + ": " + a + " against " + b);
                return;
            }
            if (a is Curve)
            {
                Curve ca = (Curve)a, cb = (Curve)b;
                if (ca.Count != cb.Count) diffs.Add(path + ": " + ca.Count + " keys against " + cb.Count);
                else for (int i = 0; i < ca.Count; i++)
                        if (ca.KeyAt(i) != cb.KeyAt(i) || ca.ValueAt(i) != cb.ValueAt(i)) diffs.Add(path + "[" + i + "]");
                return;
            }
            if (a is IEnumerable)
            {
                List<object> la = ((IEnumerable)a).Cast<object>().ToList(), lb = ((IEnumerable)b).Cast<object>().ToList();
                if (la.Count != lb.Count) diffs.Add(path + ": " + la.Count + " items against " + lb.Count);
                else for (int i = 0; i < la.Count; i++) Compare(la[i], lb[i], path + "[" + i + "]", lengthTolerance, diffs, residuals);
                return;
            }
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (PropertyInfo p in t.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0 || p.GetGetMethod(true) == null) continue;
                if (p.Name == "Name" && t == typeof(CloudLayerSpec)) continue;
                Compare(p.GetValue(a), p.GetValue(b), path + "." + p.Name, lengthTolerance, diffs, residuals, p.Name);
            }
            foreach (FieldInfo f in t.GetFields(flags))
            {
                if (f.Name.Contains("<")) continue;
                Compare(f.GetValue(a), f.GetValue(b), path + "." + f.Name, lengthTolerance, diffs, residuals, f.Name);
            }
        }

        [TestMethod]
        public void TheTemplateReadsWithoutAWarningAndEveryKeyIsKnown()
        {
            // Kerbin's own node without the patch of Clouds-Kerbin.cfg: no LAYER node, so the template.
            BodyParseResult r = BodyConfigParser.Parse(KerbinFacts(), E41Support.BodyNode("Kerbin"), null, 7, Templates());
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            IReadOnlyList<CloudLayerSpec> layers = r.Climate.Clouds.Layers;
            CollectionAssert.AreEqual(new[] { "weather-rain", "weather-snow", "cumulus", "cumulonimbus", "cirrus" }, layers.Select(l => l.Name).ToArray());
            Assert.AreEqual(4, CloudLayerSet.MostAtOnce(layers));
            StringAssert.StartsWith(r.Climate.Clouds.LayerSet, "template Earthlike, 5 layers, troposphere 10.5 km");
            StringAssert.Contains(r.Summary, "clouds template Earthlike");
            // The layers build and select as Kerbin's do.
            foreach (CloudLayerSpec spec in CloudLayerSet.Select(layers, CloudLayerDef.MaxLayers)) Assert.IsNotNull(spec.Build(1));
        }

        [TestMethod]
        public void TheTemplateAtKerbinsTroposphereIsKerbinsAuthoredSet()
        {
            IReadOnlyList<CloudLayerSpec> template = BodyConfigParser.Parse(KerbinFacts(), E41Support.BodyNode("Kerbin"), null, 7, Templates()).Climate.Clouds.Layers;
            IReadOnlyList<CloudLayerSpec> kerbin = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers;
            Assert.AreEqual(kerbin.Count, template.Count);
            var diffs = new List<string>();
            var residuals = new Dictionary<string, double>();
            for (int i = 0; i < kerbin.Count; i++)
            {
                Assert.AreEqual(kerbin[i].Name, "Kerbin-" + template[i].Name);
                Compare(template[i], kerbin[i], template[i].Name, LengthTolerance, diffs, residuals);
            }
            Console.WriteLine("largest relative residual per length: " + string.Join(", ", residuals.Select(kv => kv.Key + " " + kv.Value.ToString("0.00000"))));
            Assert.AreEqual(0, diffs.Count, string.Join("; ", diffs));
        }

        [TestMethod]
        public void KerbinKeepsItsAuthoredSetWithTheTemplateThere()
        {
            int added;
            ConfigNode node = CloudLayerSchemaTests.PatchedKerbin(out added);
            BodyParseResult with = BodyConfigParser.Parse(KerbinFacts(), node, null, 7, Templates());
            BodyParseResult without = CloudLayerSchemaTests.ParseKerbin();
            Assert.AreEqual(0, with.Warnings.Count, string.Join("; ", with.Warnings));
            Assert.AreEqual("authored, 5 layers", with.Climate.Clouds.LayerSet);
            Assert.AreEqual("authored, 5 layers", without.Climate.Clouds.LayerSet);
            var diffs = new List<string>();
            Compare(with.Climate.Clouds.Layers, without.Climate.Clouds.Layers, "Kerbin", 0.0, diffs, new Dictionary<string, double>());
            Assert.AreEqual(0, diffs.Count, string.Join("; ", diffs));
            CollectionAssert.AreEqual(without.Climate.Clouds.Layers.Select(l => l.Name).ToArray(), with.Climate.Clouds.Layers.Select(l => l.Name).ToArray());
        }

        [TestMethod]
        public void ABodyWithoutANodeGetsTheTemplateSizedToItsTroposphere()
        {
            // A planet of 60 km atmosphere: a troposphere of 9 km.
            BodyParseResult r = Run(BodyTestSupport.Planet("Plain", BodyTestSupport.HomeYear));
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            Assert.AreEqual(5, r.Climate.Clouds.Layers.Count);
            CloudLayerSpec cumulus = r.Climate.Clouds.Layers.Single(l => l.Name == "cumulus");
            Assert.AreEqual(0.1619 * 9000.0, cumulus.InnerAltitudeM, 0.01);
            Assert.AreEqual(0.7048 * 9000.0, cumulus.OuterAltitudeM, 0.01);
            Assert.AreEqual(0.02381 * 9000.0, cumulus.NoiseScaleM, 0.01);
            Assert.AreEqual(0.1905 * 9000.0, cumulus.LightCellWidth, 0.01);
            StringAssert.Contains(r.Climate.Clouds.LayerSet, "troposphere 9 km");
            // GENERATOR tropopauseM sets the depth, as it does for the generator.
            BodyParseResult deep = Run(BodyTestSupport.Planet("Deep", BodyTestSupport.HomeYear), "REDEFINITION_BODY{name = Deep;GENERATOR{tropopauseM = 15000}}");
            Assert.AreEqual(0.2 * 15000.0, deep.Climate.Clouds.Layers.Single(l => l.Name == "weather-rain").OuterAltitudeM, 0.01);
        }

        [TestMethod]
        public void TheSecondBodyGetsTheSizesOfTheLaws()
        {
            // The ocean moon of P1 (500 km, atmosphere 50 km: troposphere 7.5 km) against Kerbin's set scaled by 7.5 / 10.5 (heights and noise; TypeMixFixture).
            BodyFacts f = FieldFixture.SecondFacts();
            BodyParseResult r = Run(f);
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            CloudLayerDef[] law = TypeMixFixture.ShippedLayersScaled(7.5 / 10.5);
            string[] names = { "weather-rain", "cumulus", "cumulonimbus", "cirrus" };
            IReadOnlyList<CloudLayerSpec> kerbin = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers;
            for (int i = 0; i < names.Length; i++)
            {
                CloudLayerDef ours = r.Climate.Clouds.Layers.Single(l => l.Name == names[i]).Build(1);
                Assert.AreEqual(law[i].InnerAltitudeM, ours.InnerAltitudeM, 1e-3 * Math.Max(1.0, law[i].OuterAltitudeM), names[i]);
                Assert.AreEqual(law[i].OuterAltitudeM, ours.OuterAltitudeM, 1e-3 * law[i].OuterAltitudeM, names[i]);
                Assert.AreEqual(law[i].NoiseScaleM, ours.NoiseScaleM, 1e-3 * law[i].NoiseScaleM, names[i]);
                // The cell width of the sun's diffusion is a length too: it follows the depth.
                float kerbinWidth = kerbin.Single(l => l.Name == "Kerbin-" + names[i]).LightCellWidth;
                Assert.AreEqual(kerbinWidth * 7.5 / 10.5, ours.LightCellWidth, 1e-3 * Math.Max(1f, kerbinWidth), names[i]);
                // What is not a length is the same.
                Assert.AreEqual(law[i].SigmaPerM, ours.SigmaPerM, names[i]);
                Assert.AreEqual(law[i].Types.Count, ours.Types.Count, names[i]);
            }
        }

        [TestMethod]
        public void ABodyWithoutAtmosphereOrSurfaceGetsNoLayersAndNoException()
        {
            foreach (string name in new[] { "Mun", "Jool", "Sun" })
            {
                BodyParseResult r = Run(E41Support.StockBodies().Single(s => s.Facts.Name == name).Facts);
                Assert.AreEqual(0, r.Climate.Clouds.Layers.Count, name);
                StringAssert.StartsWith(r.Climate.Clouds.LayerSet, "none (Off(", name);
                Assert.IsFalse(r.Warnings.Any(w => w.Contains("exception") || w.Contains("could not be read")), string.Join("; ", r.Warnings));
            }
            // A body the game reports with an atmosphere but no depth: no troposphere, no layers.
            BodyFacts flat = BodyTestSupport.Planet("Flat", BodyTestSupport.HomeYear);
            flat.AtmosphereDepthM = 0.0;
            BodyParseResult none = Run(flat, "REDEFINITION_BODY{name = Flat;acceptOutsideEnvelope = true}");
            Assert.AreEqual(0, none.Climate.Clouds.Layers.Count);
        }

        [TestMethod]
        public void TheWaterPhasesOfTheTroposphereDecideWhichLayersExist()
        {
            // Hot: 700 K at the ground, 641 K at the tropopause: no water cloud anywhere.
            BodyFacts hot = BodyTestSupport.Planet("Hot", BodyTestSupport.HomeYear);
            hot.SeaLevelTemperatureK = 700.0;
            BodyParseResult h = Run(hot);
            Assert.AreEqual(0, h.Climate.Clouds.Layers.Count);
            StringAssert.Contains(h.Climate.Clouds.LayerSet, "no water cloud forms");
            // Cold: 200 K at the ground, below the homogeneous freezing point everywhere: only the ice layers.
            BodyFacts cold = BodyTestSupport.Planet("Cold", BodyTestSupport.HomeYear);
            cold.SeaLevelTemperatureK = 200.0;
            BodyParseResult c = Run(cold);
            CollectionAssert.AreEqual(new[] { "weather-snow", "cirrus" }, c.Climate.Clouds.Layers.Select(l => l.Name).ToArray());
            StringAssert.Contains(c.Climate.Clouds.LayerSet, "ice only");
            // Warm: 330 K at the ground, 271.5 K at the tropopause: liquid and (just) ice.
            BodyFacts warm = BodyTestSupport.Planet("Warm", BodyTestSupport.HomeYear);
            warm.SeaLevelTemperatureK = 330.0;
            Assert.AreEqual(5, Run(warm).Climate.Clouds.Layers.Count);
            // The body's freezing point moves the bands (waterCelsius): water freezing at 100 C makes the hot body's tropopause 641 K still too hot.
            Assert.AreEqual(0, Run(hot, "REDEFINITION_BODY{name = Hot;TEMPERATURE{waterCelsius = 100}}").Climate.Clouds.Layers.Count);
            // The temperature curve goes before the surface temperature: a curve that is 280 K at every height holds liquid but no ice.
            BodyFacts curved = BodyTestSupport.Planet("Curved", BodyTestSupport.HomeYear);
            curved.SeaLevelTemperatureK = 700.0;
            curved.TemperatureTarget = new Curve(new[] { 0f, 60000f }, new[] { 280f, 280f });
            CollectionAssert.AreEqual(new[] { "weather-rain", "cumulus", "cumulonimbus" }, Run(curved).Climate.Clouds.Layers.Select(l => l.Name).ToArray());
            // No temperature at all: water assumed.
            BodyFacts unknown = BodyTestSupport.Planet("Unknown", BodyTestSupport.HomeYear);
            unknown.SeaLevelTemperatureK = double.NaN;
            BodyParseResult u = Run(unknown);
            Assert.AreEqual(5, u.Climate.Clouds.Layers.Count);
            StringAssert.Contains(u.Climate.Clouds.LayerSet, "water assumed");
        }

        [TestMethod]
        public void ABodyCanOptOutOrChangeSingleLayersOfTheTemplate()
        {
            BodyFacts f = BodyTestSupport.Planet("Test", BodyTestSupport.HomeYear);
            BodyParseResult off = Run(f, "REDEFINITION_BODY{name = Test;CLOUDS{layers = none}}");
            Assert.AreEqual(0, off.Climate.Clouds.Layers.Count);
            Assert.AreEqual("none (layers = none)", off.Climate.Clouds.LayerSet);

            string changes = "REDEFINITION_BODY{name = Test;CLOUDS{layers = template;"
                + "LAYER{name = cirrus;enabled = false}"
                + "LAYER{name = cumulus;sigmaPerM = 0.03;outerDepths = 0.6;"
                + "TYPE{name = Altocumulus;enabled = false}"
                + "TYPE{name = Stratocumulus;baseShare = 0;topShare = 0.2;erosion = 0.9;PROFILE{key0 = 0 1}}}"
                + "LAYER{name = haze;innerDepths = 1.2;outerDepths = 1.4;kind = Cheap;TYPE{name = Haze;baseShare = 0;topShare = 1;PROFILE{key0 = 0 1}}}}}";
            BodyParseResult r = Run(f, changes);
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            CollectionAssert.AreEqual(new[] { "weather-rain", "weather-snow", "cumulus", "cumulonimbus", "haze" }, r.Climate.Clouds.Layers.Select(l => l.Name).ToArray());
            CloudLayerSpec cumulus = r.Climate.Clouds.Layers.Single(l => l.Name == "cumulus");
            Assert.AreEqual(0.03f, cumulus.SigmaPerM, 1e-6f);
            Assert.AreEqual(0.6 * 9000.0, cumulus.OuterAltitudeM, 0.01);
            Assert.AreEqual(0.1619 * 9000.0, cumulus.InnerAltitudeM, 0.01, "the keys the body does not name stay the template's");
            // The replaced type keeps its place on the type axis; the dropped one is gone.
            CollectionAssert.AreEqual(new[] { "Stratocumulus", "Cumulus humilis", "Cumulus mediocris", "Cumulus congestus" }, cumulus.Types.Select(t => t.Name).ToArray());
            Assert.AreEqual(0.9f, cumulus.Types[0].Erosion, 1e-6f);
            Assert.IsNull(cumulus.Types[0].Climate, "a TYPE is replaced whole");
            StringAssert.Contains(r.Climate.Clouds.LayerSet, "changed by 3 LAYER nodes");
            // The template file itself is not changed by a body's changes.
            Assert.AreEqual(5, Run(f).Climate.Clouds.Layers.Count);
        }

        [TestMethod]
        public void AWrongLayersValueOrAMissingTemplateIsAWarningNeverAnException()
        {
            BodyFacts f = BodyTestSupport.Planet("Test", BodyTestSupport.HomeYear);
            BodyParseResult wrong = Run(f, "REDEFINITION_BODY{name = Test;CLOUDS{layers = maybe}}");
            Assert.AreEqual(5, wrong.Climate.Clouds.Layers.Count, "auto");
            Assert.IsTrue(wrong.Warnings.Any(w => w.Contains("layers")), string.Join("; ", wrong.Warnings));
            BodyParseResult missing = Run(f, "REDEFINITION_BODY{name = Test;CLOUDS{layerTemplate = Venuslike}}");
            Assert.AreEqual(0, missing.Climate.Clouds.Layers.Count);
            Assert.AreEqual("none (no template Venuslike)", missing.Climate.Clouds.LayerSet);
            Assert.IsTrue(missing.Warnings.Any(w => w.Contains("Venuslike")), string.Join("; ", missing.Warnings));
            // Depth keys in an authored set: the parser knows the body's troposphere from its facts, with or without a template.
            BodyParseResult noDepth = BodyConfigParser.Parse(f, BodyTestSupport.Parse("REDEFINITION_BODY{name = Test;CLOUDS{LAYER{name = A;innerDepths = 0.1;outerDepths = 0.2;"
                + "TYPE{name = T;baseShare = 0;topShare = 1;PROFILE{key0 = 0 1}}}}}"), null, 7);
            Assert.AreEqual(1, noDepth.Climate.Clouds.Layers.Count, "the parser knows the depth from the facts even without a template");
            Assert.AreEqual(0.2 * 9000.0, noDepth.Climate.Clouds.Layers[0].OuterAltitudeM, 0.01);
            // Where the depth is not known, depth keys are a warning and the layer is dropped.
            BodyFacts flat = BodyTestSupport.Planet("Flat", BodyTestSupport.HomeYear);
            flat.AtmosphereDepthM = 0.0;
            BodyParseResult unknown = BodyConfigParser.Parse(flat, BodyTestSupport.Parse("REDEFINITION_BODY{name = Flat;CLOUDS{LAYER{name = A;innerDepths = 0.1;outerDepths = 0.2;"
                + "TYPE{name = T;baseShare = 0;topShare = 1;PROFILE{key0 = 0 1}}}}}"), null, 7);
            Assert.AreEqual(0, unknown.Climate.Clouds.Layers.Count);
            Assert.IsTrue(unknown.Warnings.Any(w => w.Contains("innerDepths")), string.Join("; ", unknown.Warnings));
        }

        [TestMethod]
        public void TheProviderHandsTheTemplatesToTheParserAndLogsTheSetOncePerBody()
        {
            var lines = new List<string>();
            var provider = new BodyConfigProvider(() => new ConfigNode[0], () => new string[0], () => null, () => 7, Templates) { Say = lines.Add, Warn = (k, w) => lines.Add(w) };
            BodyClimate c = provider.Build(BodyTestSupport.Planet("Plain", BodyTestSupport.HomeYear), new List<string>());
            Assert.AreEqual(5, c.Clouds.Layers.Count);
            Assert.AreEqual(1, lines.Count(l => l.Contains("clouds template Earthlike, 5 layers")), string.Join("\n", lines));
            var bare = new BodyConfigProvider(() => new ConfigNode[0], () => new string[0], () => null, () => 7) { Say = s => { }, Warn = (k, w) => { } };
            Assert.AreEqual(0, bare.Build(BodyTestSupport.Planet("Plain", BodyTestSupport.HomeYear), new List<string>()).Clouds.Layers.Count, "no template: no layers");
        }
    }
}
