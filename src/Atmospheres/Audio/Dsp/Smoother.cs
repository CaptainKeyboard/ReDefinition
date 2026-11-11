using System;

namespace ReDefinition.Atmospheres.Audio
{
    // A one-pole smoother stepped once per sample, so the result does not depend on the block size. The first value it is given
    // is taken at once (no slide from zero).
    public struct Smoother
    {
        private double value;
        private double coeff;
        private bool primed;

        // tauSeconds is the time to reach 63 % of a step.
        public void Init(double sampleRate, double tauSeconds)
        {
            coeff = tauSeconds <= 0.0 ? 1.0 : 1.0 - Math.Exp(-1.0 / (tauSeconds * sampleRate));
            primed = false;
            value = 0.0;
        }

        public double Value { get { return value; } }

        // Takes the target as the value at once.
        public void Snap(double target)
        {
            value = target;
            primed = true;
        }

        public double Next(double target)
        {
            if (!primed)
            {
                value = target;
                primed = true;
                return value;
            }
            double d = target - value;
            if (d < 1e-12 && d > -1e-12) value = target;
            else value += d * coeff;
            return value;
        }
    }
}
