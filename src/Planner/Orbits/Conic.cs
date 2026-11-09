using System;

namespace ReDefinition.Orbits
{
    // An orbit round one body, read off a state: its shape, its plane, where the body is on it, and the times to places on it.
    internal sealed class Conic
    {
        public readonly double Mu;
        public readonly State State;
        // The angular momentum, the eccentricity vector (zero on a circle) and the eccentricity.
        public readonly V3 H, EVec;
        public readonly double E;
        // The semi-latus rectum, and the semi-major axis (negative on a hyperbola).
        public readonly double P, A;
        // The true anomaly now, measured from the periapsis; from the position itself on a circle.
        public readonly double TrueAnomaly;
        // The direction the periapsis lies in, and the direction a quarter turn on in the plane.
        public readonly V3 PeriapsisDirection, QuarterDirection;

        public Conic(State state, double mu)
        {
            State = state;
            Mu = mu;
            double r = state.R.Length;
            H = V3.Cross(state.R, state.V);
            EVec = V3.Cross(state.V, H) / mu - state.R / r;
            E = EVec.Length;
            P = V3.Dot(H, H) / mu;
            A = P / (1.0 - E * E);
            PeriapsisDirection = E > 1e-9 ? EVec / E : state.R / r;
            QuarterDirection = V3.Cross(H.Unit, PeriapsisDirection);
            double cos = V3.Dot(PeriapsisDirection, state.R) / r;
            double sin = V3.Dot(QuarterDirection, state.R) / r;
            TrueAnomaly = Math.Atan2(sin, cos);
        }

        public bool Closed
        {
            get { return E < 1.0; }
        }

        // The time for a whole turn; infinite where the orbit is open.
        public double Period
        {
            get { return Closed ? 2.0 * Math.PI * Math.Sqrt(A * A * A / Mu) : double.PositiveInfinity; }
        }

        // The radius at a true anomaly.
        public double RadiusAt(double trueAnomaly)
        {
            return P / (1.0 + E * Math.Cos(trueAnomaly));
        }

        // The time until the body reaches `target`, a true anomaly in radians. On a closed orbit the next time, within one period; on an
        // open orbit NaN where the place is behind it or not on the orbit.
        public double TimeToTrueAnomaly(double target)
        {
            if (Closed)
            {
                double now = MeanAnomaly(TrueAnomaly), then = MeanAnomaly(target);
                double n = Math.Sqrt(Mu / (A * A * A));
                double dt = (then - now) / n;
                double period = Period;
                dt %= period;
                if (dt < 0.0) dt += period;
                return dt;
            }
            double limit = Math.Acos(-1.0 / E);
            if (Math.Abs(NormalAngle(target)) >= limit) return double.NaN;
            double nh = Math.Sqrt(Mu / (-A * -A * -A));
            double dt2 = (HyperbolicMean(NormalAngle(target)) - HyperbolicMean(NormalAngle(TrueAnomaly))) / nh;
            return dt2 < 0.0 ? double.NaN : dt2;
        }

        public double TimeToPeriapsis
        {
            get { return TimeToTrueAnomaly(0.0); }
        }

        public double TimeToApoapsis
        {
            get { return TimeToTrueAnomaly(Math.PI); }
        }

        // The true anomaly of a direction in the orbit's plane, from the periapsis.
        public double TrueAnomalyOf(V3 direction)
        {
            return Math.Atan2(V3.Dot(QuarterDirection, direction), V3.Dot(PeriapsisDirection, direction));
        }

        private double MeanAnomaly(double trueAnomaly)
        {
            double e = Math.Atan2(Math.Sqrt(1.0 - E * E) * Math.Sin(trueAnomaly), E + Math.Cos(trueAnomaly));
            double mean = e - E * Math.Sin(e);
            return mean < 0.0 ? mean + 2.0 * Math.PI : mean;
        }

        private double HyperbolicMean(double trueAnomaly)
        {
            double t = Math.Sqrt((E - 1.0) / (E + 1.0)) * Math.Tan(trueAnomaly / 2.0);
            double f = Math.Log((1.0 + t) / (1.0 - t));
            return E * Math.Sinh(f) - f;
        }

        private static double NormalAngle(double angle)
        {
            while (angle > Math.PI) angle -= 2.0 * Math.PI;
            while (angle <= -Math.PI) angle += 2.0 * Math.PI;
            return angle;
        }
    }
}
