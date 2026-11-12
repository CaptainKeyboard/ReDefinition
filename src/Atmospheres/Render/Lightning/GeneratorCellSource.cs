using AtmoSim.Core;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The scan's source over the weather generator of one body: the convective cell strength, the precipitation level on the body's bands,
    // the height of the cell's top and the cloud cover at a place. A cell lights up only where it rains: the strength is scaled by the share
    // of the fall that is rain or sleet (a snow or dust fall has no flashes), so the schedule needs no second rule for the type.
    internal sealed class GeneratorCellSource : ICellSource
    {
        private readonly GeneratorState state;

        internal GeneratorCellSource(GeneratorState state)
        {
            this.state = state;
        }

        public bool Sample(double latRad, double lonRad, double ut, out CellSample sample)
        {
            sample = new CellSample();
            if (state == null || state.Broken) return false;
            Weather w;
            state.Generator.Sample(latRad, lonRad, 0.0, ut, out w);
            if (!GeneratorState.Finite(ref w)) return false;
            double sum = w.SnowFrac + w.SleetFrac + w.RainFrac;
            double warm = sum > 0.0 ? (w.RainFrac + 0.5 * w.SleetFrac) / sum : 0.0;
            if (w.Type == PrecipKind.Dust || w.Type == PrecipKind.None) warm = 0.0;
            sample.Strength = (float)(w.CellStrength * LightningSchedule.SmoothStep(0.2, 0.7, warm));
            sample.Level01 = PrecipScale.LevelFromRate((float)w.PrecipMmH, state.Bands);
            sample.TopM = (float)w.CellTopM;
            sample.Coverage = (float)w.Coverage;
            return true;
        }
    }
}
