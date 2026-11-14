using System;

namespace ReDefinition.Atmospheres
{
    // The frame's share of the step offset of the layer march (the shader's _DitherP.x). Every pixel keeps one spatial offset and every frame adds the same phase to it, so
    // that the history of the pass sees each pixel's offsets spread over the grid step: the golden-ratio sequence (Roberts 2018, the R1 sequence). E40 L4 clean: the other
    // sequences (van der Corput, shuffled strata, white, golden per block) and the gradient noise's per-frame shift (layers.ditherPhase, layers.ditherShift) are gone; the
    // weave measurement gave no case that separated from the default (E40-weave-march-result.md).
    internal static class StepPhase
    {
        private const double GoldenRatio = 0.6180339887498949;

        // The phase of a frame, in [0, 1).
        internal static double Of(int frame)
        {
            if (frame < 0) frame = 0;
            double x = frame * GoldenRatio;
            return x - Math.Floor(x);
        }
    }
}
