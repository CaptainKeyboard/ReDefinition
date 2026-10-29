using System;

namespace ReDefinition.Orbits
{
    // Motion on a conic round one body, by universal variables: one set of formulas for ellipses, parabolas and hyperbolas
    // (Bate, Mueller and White; Curtis, algorithm 3.4). The step is bracketed and bisected, so that it holds for any time and any orbit.
    internal static class Kepler
    {
        // Stumpff's functions C(z) and S(z).
        internal static double C(double z)
        {
            if (z > 1e-8) return (1.0 - Math.Cos(Math.Sqrt(z))) / z;
            if (z < -1e-8) return (Math.Cosh(Math.Sqrt(-z)) - 1.0) / -z;
            return 0.5 - z / 24.0 + z * z / 720.0;
        }

        internal static double S(double z)
        {
            if (z > 1e-8)
            {
                double root = Math.Sqrt(z);
                return (root - Math.Sin(root)) / (root * root * root);
            }
            if (z < -1e-8)
            {
                double root = Math.Sqrt(-z);
                return (Math.Sinh(root) - root) / (root * root * root);
            }
            return 1.0 / 6.0 - z / 120.0 + z * z / 5040.0;
        }

        // The state `dt` seconds after `start`, forward or back, round a body of gravitational parameter `mu`.
        internal static State Propagate(State start, double dt, double mu)
        {
            if (dt == 0.0) return start;
            double r0 = start.R.Length;
            double vr0 = V3.Dot(start.R, start.V) / r0;
            double alpha = 2.0 / r0 - V3.Dot(start.V, start.V) / mu;
            double root = Math.Sqrt(mu);
            double chi = Solve(r0, vr0, alpha, root, dt);
            double z = alpha * chi * chi;
            double f = 1.0 - chi * chi / r0 * C(z);
            double g = dt - chi * chi * chi / root * S(z);
            V3 r = start.R * f + start.V * g;
            double rr = r.Length;
            double fdot = root * chi / (rr * r0) * (z * S(z) - 1.0);
            double gdot = 1.0 - chi * chi / rr * C(z);
            return new State(r, start.R * fdot + start.V * gdot);
        }

        // The universal anomaly for the time: the root of the universal Kepler equation, which rises with it everywhere.
        private static double Solve(double r0, double vr0, double alpha, double root, double dt)
        {
            Func<double, double> equation = chi =>
            {
                double z = alpha * chi * chi;
                return r0 * vr0 / root * chi * chi * C(z) + (1.0 - alpha * r0) * chi * chi * chi * S(z) + r0 * chi - root * dt;
            };
            double low, high;
            double guess = root * Math.Abs(alpha) * dt;
            if (guess == 0.0) guess = root * dt / r0;
            if (equation(guess) < 0.0)
            {
                low = guess;
                high = Math.Abs(guess) + 1.0;
                while (equation(high) < 0.0) high *= 2.0;
            }
            else
            {
                high = guess;
                low = -(Math.Abs(guess) + 1.0);
                while (equation(low) > 0.0) low *= 2.0;
            }
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (low + high);
                if (equation(mid) < 0.0) low = mid;
                else high = mid;
                if (Math.Abs(high - low) <= 1e-13 * Math.Max(1.0, Math.Abs(mid))) break;
            }
            return 0.5 * (low + high);
        }
    }
}
