using System;
using System.Collections.Generic;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;
using ReDefinition.Tests.Atmospheres.T09;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // The solo probe's game without the game (ISoloRig): the viewer rig's model plus the solo control (the real CloudSoloPlan writes the overrides), a model of what the pass draws
    // under them (the layer mask, the cover at the vessel, the share of the frame each picture's cloud fills) and the pictures as numbers. A fault is a flag that makes one thing the
    // probe judges go wrong, so that the probe has to fail with the check that names it.
    internal sealed class FakeSoloRig : FakeViewerRig, ISoloRig
    {
        // The faults: another layer stays drawn, the cell is not in view, the layer fills the whole frame, the sun runs on, the forced rain stays, the pass draws every layer, two
        // baselines differ.
        internal bool OtherLayerDrawn, NothingInView, FillsFrame, SunDrifts, RainStaysOn, MaskNotSet, NoisyBaseline, TypeMissing;

        private static readonly CloudLayerDef[] Defs = { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };

        private struct Frame
        {
            internal double Share, Span;
        }

        private readonly Dictionary<string, Frame> frames = new Dictionary<string, Frame>();
        private SoloSelection selection = new SoloSelection { Layer = -1, Type = -1 };
        private double aheadKm = CloudViewer.SoloCellDefaultKm;

        public IList<SoloEntry> SoloEntries
        {
            get
            {
                var list = new List<SoloEntry>();
                for (int l = 0; l < Defs.Length; l++)
                    for (int t = 0; t < Defs[l].Types.Count; t++)
                    {
                        string name = Defs[l].Types[t].Name;
                        if (TypeMissing && name == "Cumulus mediocris") continue;
                        list.Add(new SoloEntry { Layer = l, Type = t, LayerName = Defs[l].Name, Group = Defs[l].Group ?? "", TypeName = name, Cell = CloudSoloPlan.IsCellType(Defs[l], t) });
                    }
                return list;
            }
        }

        public SoloSelection Solo { get { return selection; } }

        public void SetSolo(int layer, int type)
        {
            selection = new SoloSelection { Layer = layer, Type = type, Strength = CloudSoloPlan.DefaultStrength(Defs[layer], type), Cell = CloudSoloPlan.IsCellType(Defs[layer], type), CellKm = CloudSoloPlan.DefaultCellKm };
            Apply();
        }

        public void SetSoloStrength(float strength)
        {
            if (!selection.On) return;
            selection.Strength = Math.Max(0f, Math.Min(1f, strength));
            Apply();
        }

        public void SetSoloCellAhead(double km)
        {
            aheadKm = km;
            if (selection.On && selection.Cell) Apply();
        }

        private void Apply()
        {
            foreach (KeyValuePair<string, float> pair in CloudSoloPlan.Overrides(Defs, selection))
            {
                Note(pair.Key);
                if (float.IsNaN(pair.Value)) Options.ClearOverride(pair.Key);
                else Options.SetOverride(pair.Key, pair.Value);
            }
            if (OtherLayerDrawn && selection.On)
            {
                string other = Defs[(selection.Layer + 1) % Defs.Length].Name;
                Note(CloudLookTuning.TuneMapOverride + other);
                Options.SetOverride(CloudLookTuning.TuneMapOverride + other, 0.3f);
            }
            if (!RainStaysOn) Options.SetSetting(AtmospheresSettings.Keys.ForceLevel, selection.On ? 0.0 : openSettings[AtmospheresSettings.Keys.ForceLevel]);
        }

        public int PassLayerMask
        {
            get
            {
                if (MaskNotSet) return 0;
                int n = (int)Math.Round(Options.Effective(CloudLookTuning.TuneSoloLayer, 0f));
                return n >= 1 ? 1 << (n - 1) : 0;
            }
        }

        public double FieldOfViewDeg { get { return 60.0; } }

        public override double SunElevationDeg { get { return base.SunElevationDeg + (SunDrifts ? RealS * 0.05 : 0.0); } }

        // A cell's map is empty at the vessel (it stands km ahead); a uniform field is the forced value.
        protected override float CoverTarget(int layer, float target)
        {
            return Options.Effective(CloudLookTuning.TuneCellKm, 0f) > 0f && layer == (int)Math.Round(Options.Effective(CloudLookTuning.TuneSoloLayer, 0f)) - 1 ? 0f : target;
        }

        // What the frame shows: the layers the mask lets through at their forced map values (a cell: the share its tower and anvil fill at the distance it stands at).
        protected override void OnPicture(string name)
        {
            int mask = PassLayerMask;
            double share = 0.0, span = 0.0;
            for (int l = 0; l < Defs.Length; l++)
            {
                if (mask != 0 && (mask & (1 << l)) == 0) continue;
                float value = Options.Effective(CloudLookTuning.TuneMapOverride + Defs[l].Name, 0.4f);
                if (value <= 0f) continue;
                if (Options.Effective(CloudLookTuning.TuneCellKm, 0f) > 0f)
                {
                    bool near = aheadKm <= 30.0;
                    share += NothingInView ? 0.0 : near ? 0.5 : 0.25;
                    span = Math.Max(span, NothingInView ? 0.0 : near ? 0.7 : 0.45);
                }
                else
                {
                    share += 0.9 * value;
                    span = Math.Max(span, 0.9);
                }
            }
            if (FillsFrame && share > 0.0) share = 1.0;
            frames[name] = new Frame { Share = Math.Min(1.0, share), Span = Math.Min(1.0, span) };
        }

        public bool MeasurePictures(string picture, string baseline, out MaskReading reading)
        {
            reading = default(MaskReading);
            Frame p, b;
            if (!frames.TryGetValue(picture, out p) || !frames.TryGetValue(baseline, out b)) return false;
            double noise = NoisyBaseline && picture != baseline ? 0.05 : 0.0;
            const int w = 1707, h = 960;
            int counted = (int)(w * h * 0.9);
            double share = Math.Max(0.0, p.Share - b.Share) + noise;
            reading = new MaskReading { Width = w, Height = h, Counted = counted, Masked = (int)(share * counted), Share = share, RowSpan = Math.Max(0.0, p.Span - b.Span), RowLow = 0.1, RowHigh = 0.1 + Math.Max(0.0, p.Span - b.Span), ColumnSpan = p.Span };
            return true;
        }
    }
}
