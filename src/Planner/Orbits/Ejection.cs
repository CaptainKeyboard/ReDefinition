using System;

namespace ReDefinition.Orbits
{
    // Leaving a body: the velocity at a place that carries a vessel out of the body's gravity with a given velocity at infinity, and the cheapest
    // place on the orbit it leaves from to burn it.
    internal static class Ejection
    {
        // The velocity at `position` (relative to the body, parameter `mu`) of the hyperbola whose velocity at infinity is `excess`. The hyperbola
        // lies in the plane of the position and the excess; the true anomaly of the place follows from the shape that fits the place's radius and the
        // excess speed, in closed form. The orbit turns about the plane's normal, from the place towards the excess.
        internal static V3 VelocityFor(V3 position, V3 excess, double mu, V3 fallbackNormal)
        {
            double radius = position.Length;
            double speedAtInfinity = excess.Length;
            V3 radial = position / radius;
            V3 direction = excess / speedAtInfinity;
            V3 normal = V3.Cross(radial, direction);
            if (normal.Length < 1e-9) normal = fallbackNormal;
            normal = normal.Unit;
            V3 along = V3.Cross(normal, radial);
            double phi = Math.Atan2(V3.Dot(direction, along), V3.Dot(direction, radial));
            if (phi < 0.0) phi += 2.0 * Math.PI;
            double kappa = mu / (speedAtInfinity * speedAtInfinity * radius);
            double sin = Math.Sin(phi), cos = Math.Cos(phi);
            double s = (sin + Math.Sqrt(sin * sin + 4.0 * kappa * (1.0 - cos))) / (2.0 * kappa);
            double e = Math.Sqrt(1.0 + s * s);
            double p = mu * s * s / (speedAtInfinity * speedAtInfinity);
            double nu = Math.Acos(-1.0 / e) - phi;
            double h = Math.Sqrt(mu * p);
            return radial * (mu * e * Math.Sin(nu) / h) + along * (h / radius);
        }

        // The cheapest burn on a closed orbit within `window` seconds from the state, that gives the velocity at infinity `excess`: the places
        // of the orbit are tried, then the best is narrowed down. The burn's delay and its change of velocity.
        internal static Burn Cheapest(State ship, double mu, V3 excess, double windowStart, double window)
        {
            V3 normal = V3.Cross(ship.R, ship.V);
            double bestCost = double.PositiveInfinity;
            double bestDelay = windowStart;
            V3 bestDelta = new V3(0.0, 0.0, 0.0);
            double low = windowStart, high = windowStart + window;
            for (int pass = 0; pass < 5; pass++)
            {
                int steps = pass == 0 ? 720 : 40;
                double step = (high - low) / steps;
                for (int i = 0; i <= steps; i++)
                {
                    double delay = low + i * step;
                    State there = Kepler.Propagate(ship, delay, mu);
                    V3 delta = VelocityFor(there.R, excess, mu, normal) - there.V;
                    double cost = delta.Length;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestDelay = delay;
                        bestDelta = delta;
                    }
                }
                low = Math.Max(windowStart, bestDelay - 2.0 * step);
                high = Math.Min(windowStart + window, bestDelay + 2.0 * step);
            }
            return new Burn(bestDelay, bestDelta);
        }

        // The time a vessel on a hyperbola leaves a sphere of radius `soi` round the body: from the state, by bisection on the radius. Infinite where
        // the state is on a closed orbit that stays inside.
        internal static double TimeToLeave(State state, double mu, double soi)
        {
            double high = 3600.0;
            while (Kepler.Propagate(state, high, mu).R.Length < soi)
            {
                high *= 2.0;
                if (high > 1e9) return double.PositiveInfinity;
            }
            double low = 0.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (low + high);
                if (Kepler.Propagate(state, mid, mu).R.Length < soi) low = mid;
                else high = mid;
            }
            return 0.5 * (low + high);
        }
    }
}
