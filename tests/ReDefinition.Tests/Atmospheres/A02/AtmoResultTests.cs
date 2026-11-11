using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.A02
{
    // The result lines tools/atmo_series.py reads.
    [TestClass]
    public class AtmoResultTests
    {
        private readonly List<string> lines = new List<string>();

        [TestInitialize]
        public void Capture()
        {
            lines.Clear();
            AtmoResult.Sink = lines.Add;
        }

        [TestCleanup]
        public void Release()
        {
            AtmoResult.Sink = line => UnityEngine.Debug.Log(line);
        }

        [TestMethod]
        public void ANumberIsWrittenAsTheDocumentedLine()
        {
            AtmoResult.Emit("K-pre", "p1_mfi", 1.0);
            Assert.AreEqual("ATMORESULT {\"series\":\"K-pre\",\"metric\":\"p1_mfi\",\"value\":1.0}", lines[0]);
        }

        [TestMethod]
        public void AFractionKeepsItsDigitsAndTheCultureDoesNotMatter()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            try
            {
                AtmoResult.Emit("K-pre", "m", 0.125);
                AtmoResult.Emit("K-pre", "n", -1234.5);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
            StringAssert.Contains(lines[0], "\"value\":0.125}");
            StringAssert.Contains(lines[1], "\"value\":-1234.5}");
        }

        [TestMethod]
        public void AVariantTravelsWithTheLine()
        {
            AtmoResult.Emit("K-W3b", "light_sigma", 2.5, "fog");
            Assert.AreEqual("ATMORESULT {\"series\":\"K-W3b\",\"metric\":\"light_sigma\",\"value\":2.5,\"variant\":\"fog\"}", lines[0]);
        }

        [TestMethod]
        public void ABooleanIsOneOrZeroAndATextIsQuoted()
        {
            AtmoResult.Emit("K-pre", "ok", true);
            AtmoResult.Emit("K-pre", "bad", false);
            AtmoResult.EmitText("K-pre", "who", "say \"hi\"\\\n");
            StringAssert.Contains(lines[0], "\"value\":1}");
            StringAssert.Contains(lines[1], "\"value\":0}");
            StringAssert.Contains(lines[2], "\"value\":\"say \\\"hi\\\"\\\\\\n\"}");
        }

        [TestMethod]
        public void NaNAndInfinityBecomeStringsBecauseJsonHasNone()
        {
            AtmoResult.Emit("K-pre", "a", double.NaN);
            AtmoResult.Emit("K-pre", "b", double.PositiveInfinity);
            StringAssert.Contains(lines[0], "\"value\":\"NaN\"}");
            StringAssert.Contains(lines[1], "\"value\":\"Infinity\"}");
        }
    }
}
