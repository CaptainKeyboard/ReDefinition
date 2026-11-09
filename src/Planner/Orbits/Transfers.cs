using System;

namespace ReDefinition.Orbits
{
    // A transfer between two bodies on orbits round one body: when to leave, how long it takes, the burn that leaves and the change of
    // velocity at the arrival that would match the target's.
    internal struct Transfer
    {
        public double Delay, FlightTime;
        public V3 DeparturePosition, Departure, Arrival;

        public double Total(bool withArrival)
        {
            return Departure.Length + (withArrival ? Arrival.Length : 0.0);
        }
    }

    // The search for a transfer between two orbits. For each time to leave and each time of flight, Lambert gives the orbit, and the cheapest is kept.
    // A grid finds the neighbourhood of the best, and a finer grid is laid over it several times.
    internal static class Transfers
    {
        // The cheapest transfer from `ship` to `target`, both states at one time round a body of parameter `mu`. The ship leaves within `maxDelay`
        // seconds, the flight takes between `minFlight` and `maxFlight`. With `withArrival` the cost counts the change of velocity at the target too
        // (a rendezvous); without it the cost is the leaving burn (an intercept, where a capture burn follows). False where none is found.
        internal static bool Cheapest(State ship, State target, double mu, double maxDelay, double minFlight, double maxFlight,
                                      bool withArrival, out Transfer best, double minDelay = 0.0)
        {
            best = new Transfer();
            V3 normal = V3.Cross(ship.R, ship.V);
            double bestCost = double.PositiveInfinity;
            double delayLow = minDelay, delayHigh = Math.Max(maxDelay, minDelay), flightLow = minFlight, flightHigh = maxFlight;
            int steps = 120;
            for (int pass = 0; pass < 7; pass++)
            {
                double delayStep = (delayHigh - delayLow) / steps, flightStep = (flightHigh - flightLow) / steps;
                for (int i = 0; i <= steps; i++)
                {
                    double delay = delayLow + i * delayStep;
                    State leaving = Kepler.Propagate(ship, delay, mu);
                    State targetThen = Kepler.Propagate(target, delay, mu);
                    for (int j = 0; j <= steps; j++)
                    {
                        double flight = flightLow + j * flightStep;
                        State arriving = Kepler.Propagate(targetThen, flight, mu);
                        V3 v1, v2;
                        if (!Lambert.Solve(leaving.R, arriving.R, flight, mu, true, normal, out v1, out v2)) continue;
                        var candidate = new Transfer
                        {
                            Delay = delay, FlightTime = flight, DeparturePosition = leaving.R,
                            Departure = v1 - leaving.V, Arrival = arriving.V - v2,
                        };
                        double cost = candidate.Total(withArrival);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            best = candidate;
                        }
                    }
                }
                if (double.IsInfinity(bestCost)) return false;
                delayLow = Math.Max(minDelay, best.Delay - 2.0 * delayStep);
                delayHigh = Math.Min(Math.Max(maxDelay, minDelay), best.Delay + 2.0 * delayStep);
                flightLow = Math.Max(minFlight, best.FlightTime - 2.0 * flightStep);
                flightHigh = Math.Min(maxFlight, best.FlightTime + 2.0 * flightStep);
                steps = 20;
            }
            return !double.IsInfinity(bestCost);
        }
    }
}
