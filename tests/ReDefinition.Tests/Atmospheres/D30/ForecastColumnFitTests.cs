using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // K-W3a: 13 of 253 labels of the forecast window wrapped. The font cannot be measured outside the game, so the columns are held to
    // a model of the skin's font: 6.5 pixels per character, 7.2 for the bold header. The model is a lower bound that the K-W3a numbers
    // did not contradict (the old columns broke it); the probe names the labels that wrap in the game.
    [TestClass]
    public class ForecastColumnFitTests
    {
        private const float Regular = 6.5f, Bold = 7.2f;

        [TestMethod]
        public void TheColumnsFitTheirRowAndTheirHeaders()
        {
            float sum = 0f;
            foreach (float w in ForecastView.ColumnWidths) sum += w;
            Assert.IsTrue(sum <= 510f, "a row is 510 pixels wide, the columns sum to " + sum);
            Assert.AreEqual(ForecastView.Columns.Length, ForecastView.ColumnWidths.Length);
            for (int c = 0; c < ForecastView.Columns.Length; c++)
                Assert.IsTrue(ForecastView.Columns[c].Length * Bold <= ForecastView.ColumnWidths[c], "header " + ForecastView.Columns[c] + " in " + ForecastView.ColumnWidths[c]);
        }

        [TestMethod]
        public void TheLongestCellOfEveryColumnFitsItsWidth()
        {
            var longest = new string[ForecastView.Columns.Length];
            Action<int, string> take = (col, text) =>
            {
                if (longest[col] == null || text.Length > longest[col].Length) longest[col] = text;
            };
            take(0, ForecastFormat.Lead(48 * 3600.0));
            foreach (PrecipBand band in Enum.GetValues(typeof(PrecipBand))) take(1, ForecastFormat.LevelName(band));
            foreach (PrecipType type in Enum.GetValues(typeof(PrecipType))) take(2, LaunchConditions.TypeName(type));
            take(3, ForecastFormat.Visibility(9500f));
            take(3, ForecastFormat.Visibility(20000f));
            foreach (float dir in new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f }) take(4, ForecastFormat.Wind(120f, dir));
            take(5, ForecastFormat.Gust(120f));
            take(6, ForecastFormat.Temperature(273.15f - 120f));
            take(6, ForecastFormat.Temperature(273.15f + 450f));
            for (int c = 0; c < longest.Length; c++)
                Assert.IsTrue(longest[c].Length * Regular <= ForecastView.ColumnWidths[c],
                    "column " + ForecastView.Columns[c] + ": '" + longest[c] + "' needs " + longest[c].Length * Regular + " of " + ForecastView.ColumnWidths[c]);
        }

        [TestMethod]
        public void TheOldWidthsFailTheSameModel()
        {
            // The old widths fail the same model, which is the check that the test could have caught the finding.
            float[] old = { 56f, 100f, 62f, 66f, 96f, 52f, 70f };
            bool oldFits = true;
            for (int c = 0; c < old.Length; c++)
            {
                if (ForecastView.Columns[c].Length * Bold > old[c]) oldFits = false;
            }
            Assert.IsFalse(oldFits, "the model flags the old header 'Field Temp' or 'Visibility' in 70 and 66 pixels");
        }
    }
}
