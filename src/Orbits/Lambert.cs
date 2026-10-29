using System;

namespace ReDefinition.Orbits
{
    // Lambert's problem: the orbit that takes a body from one position to another in a given time, for a transfer of less than one turn.
    // After Izzo, "Revisiting Lambert's problem" (2015): one variable x, the time of flight falls with it everywhere, so it is bisected.
    // It holds for a transfer of half a turn too, which is the Hohmann transfer's, and for either way round.
    internal static class Lambert
    {
        // The velocities at the start and at the end of the transfer. `planeNormal` says which way is prograde: counterclockwise about it. False where
        // there is none: no positive time, or the positions one place.
        internal static bool Solve(V3 r1, V3 r2, double tof, double mu, bool prograde, V3 planeNormal, out V3 v1, out V3 v2)
        {
            v1 = v2 = new V3(0.0, 0.0, 0.0);
            double m1 = r1.Length, m2 = r2.Length;
            double chord = (r2 - r1).Length;
            double s = 0.5 * (m1 + m2 + chord);
            if (tof <= 0.0 || chord < 1e-9 * s) return false;
            V3 ir1 = r1 / m1, ir2 = r2 / m2;
            V3 cross = V3.Cross(ir1, ir2);
            V3 axis = prograde ? planeNormal.Unit : -planeNormal.Unit;
            V3 normal = cross.Length > 1e-12 ? cross.Unit : axis;
            bool longWay = V3.Dot(normal, axis) < 0.0 && cross.Length > 1e-12;
            double lambda2 = Math.Max(0.0, 1.0 - chord / s);
            double lambda = Math.Sqrt(lambda2);
            V3 t1 = V3.Cross(normal, ir1), t2 = V3.Cross(normal, ir2);
            if (longWay)
            {
                lambda = -lambda;
                t1 = -t1;
                t2 = -t2;
            }
            double target = Math.Sqrt(2.0 * mu / (s * s * s)) * tof;

            double low = -1.0 + 1e-12, high = 1.0;
            while (TimeOfFlight(high, lambda) > target) high *= 2.0;
            if (high > 1e12) return false;
            for (int i = 0; i < 300; i++)
            {
                double mid = 0.5 * (low + high);
                if (TimeOfFlight(mid, lambda) > target) low = mid;
                else high = mid;
                if (high - low < 1e-15 * Math.Max(1.0, Math.Abs(mid))) break;
            }
            double x = 0.5 * (low + high);
            double gamma = Math.Sqrt(mu * s / 2.0);
            double rho = (m1 - m2) / chord;
            double sigma = Math.Sqrt(Math.Max(0.0, 1.0 - rho * rho));
            double y = Math.Sqrt(1.0 - lambda2 * (1.0 - x * x));
            double radial1 = gamma * ((lambda * y - x) - rho * (lambda * y + x)) / m1;
            double radial2 = -gamma * ((lambda * y - x) + rho * (lambda * y + x)) / m2;
            double tangent = gamma * sigma * (y + lambda * x);
            v1 = ir1 * radial1 + t1 * (tangent / m1);
            v2 = ir2 * radial2 + t2 * (tangent / m2);
            return !double.IsNaN(v1.X + v1.Y + v1.Z + v2.X + v2.Y + v2.Z);
        }

        // The time of flight, in Izzo's units, for the variable x and the shape lambda; it falls as x rises from -1.
        private static double TimeOfFlight(double x, double lambda)
        {
            double lambda2 = lambda * lambda;
            if (Math.Abs(x - 1.0) < 1e-9) return 2.0 / 3.0 * (1.0 - lambda2 * lambda);
            double a = 1.0 / (1.0 - x * x);
            if (x < 1.0)
            {
                double alpha = 2.0 * Math.Acos(Math.Max(-1.0, Math.Min(1.0, x)));
                double beta = 2.0 * Math.Asin(Math.Min(1.0, Math.Sqrt(lambda2 / a)));
                if (lambda < 0.0) beta = -beta;
                return a * Math.Sqrt(a) * ((alpha - Math.Sin(alpha)) - (beta - Math.Sin(beta))) / 2.0;
            }
            double hyperAlpha = 2.0 * Acosh(x);
            double hyperBeta = 2.0 * Asinh(Math.Sqrt(-lambda2 / a));
            if (lambda < 0.0) hyperBeta = -hyperBeta;
            return -a * Math.Sqrt(-a) * ((hyperBeta - Math.Sinh(hyperBeta)) - (hyperAlpha - Math.Sinh(hyperAlpha))) / 2.0;
        }

        private static double Acosh(double x)
        {
            return Math.Log(x + Math.Sqrt(x * x - 1.0));
        }

        private static double Asinh(double x)
        {
            return Math.Log(x + Math.Sqrt(x * x + 1.0));
        }
    }
}
