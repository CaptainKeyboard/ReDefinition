using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Ui;

namespace ReDefinition.Atmospheres.Viewer
{
    // The solo control of the viewer's Scene group (E40 SOLO): "All Layers" and one button per type of every layer of the stack, a strength slider and the cell. It draws
    // CloudViewer.SetSolo and friends and decides nothing; which button is marked is read from the keys in force, so a flip of A and B or a loaded preset moves the mark.
    internal sealed partial class CloudViewerWindow
    {
        private List<DialogGUIBase> SoloRows()
        {
            var rows = new List<DialogGUIBase>();
            IList<CloudLayerDef> defs = CloudViewer.StackDefs();
            rows.Add(new DialogGUILabel("Solo Layer / Type: one type alone over a clear sky", PageWidth));
            var buttons = new List<DialogGUIBase>
            {
                Tip(new DialogGUIButton(() => (viewer.SoloNow().On ? "" : "> ") + "All Layers", () => viewer.SetSolo(-1, -1), 134f, RowHeight, false), "Every Layer")
            };
            for (int l = 0; l < defs.Count; l++)
            {
                for (int t = 0; t < defs[l].Types.Count; t++)
                {
                    int layer = l, type = t;
                    string label = CloudSoloPlan.ShortLabel(defs[l], t);
                    buttons.Add(Tip(new DialogGUIButton(() => (IsSolo(layer, type) ? "> " : "") + label, () => viewer.SetSolo(layer, type), 134f, RowHeight, false),
                                    CultureInfo.InvariantCulture.TextInfo.ToTitleCase(defs[l].Types[t].Name ?? "")));
                }
            }
            for (int i = 0; i < buttons.Count; i += 4) rows.Add(Line(buttons.GetRange(i, Math.Min(4, buttons.Count - i)).ToArray()));

            var strength = new DialogGUISlider(() => viewer.SoloNow().On ? viewer.SoloNow().Strength : 0f, 0f, 1f, false, 250f, -1f, f =>
            {
                if (viewer.SoloNow().On && Math.Abs(f - viewer.SoloNow().Strength) > 1e-4f) viewer.SetSoloStrength(f);
            });
            rows.Add(Line(new DialogGUILabel("Strength", 80f), Tip(strength, "Cover Of The Field Or Peak Of The Cell"),
                          new DialogGUILabel(() => viewer.SoloNow().On ? viewer.SoloNow().Strength.ToString("0.00", CultureInfo.InvariantCulture) : "-", 110f)));
            var ahead = new DialogGUISlider(() => (float)viewer.SoloCellAheadKm, (float)CloudViewer.SoloCellMinKm, (float)CloudViewer.SoloCellMaxKm, false, 250f, -1f, f =>
            {
                if (Math.Abs(f - viewer.SoloCellAheadKm) > 0.05) viewer.SetSoloCellAhead(f);
            });
            rows.Add(Line(Tip(new DialogGUIToggle(() => viewer.SoloNow().On && viewer.SoloNow().Cell, "One Cell Ahead", on => viewer.SetSoloCell(on), 140f, RowHeight), "One Cell, Held Still"),
                          Tip(ahead, "Distance Of The Cell"), new DialogGUILabel(() => viewer.SoloCellAheadKm.ToString("0", CultureInfo.InvariantCulture) + " km", 80f)));
            return rows;
        }

        private bool IsSolo(int layer, int type)
        {
            SoloSelection s = viewer.SoloNow();
            return s.On && s.Layer == layer && s.Type == type;
        }
    }
}
