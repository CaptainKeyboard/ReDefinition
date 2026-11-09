using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Core;
using ReDefinition.Landing;
using ReDefinition.Orbits;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ReDefinition.Autopilot.Planning
{
    // The search for the time of the deorbit burn, made so that the game goes on while it runs. The descents of the times tried are independent of one
    // another. Each is a simulation that touches nothing of the game once it is set up, so they run on the thread pool, the way MechJeb's landing
    // prediction runs its own (MechJebModuleLandingPredictions, GPL-3.0). The game's thread sets up a few of them in each frame, within a few
    // milliseconds, and takes the results as they come. The search is an iterator that waits for them between its steps, as Trajectories spreads its
    // computation over the frames (Trajectory.ComputeTrajectoryIncrement, GPL-3.0-or-later).
    internal sealed class DeorbitSearch
    {
        // How many times the time is tried over the first stretch, and how often it is bettered, until the descent comes down within the tolerance.
        private const int Samples = 36;
        private const int Refinements = 9;
        // How far from the vessel the burn may be: over this many orbits, and not sooner than this many seconds.
        private const double Orbits = 1.5;
        private const double Soon = 60.0;
        // The periapsis the burn leaves, as a share of the body's radius, as the untargeted deorbit has it.
        private const double Periapsis = 0.9;
        // How long the game's thread spends on the search in a frame, in ms, and how long a search that is waited for may take at most, in s.
        private const double SliceMilliseconds = 4.0;
        private const double GiveUpSeconds = 180.0;

        // The best burn found so far.
        internal struct Best
        {
            // The delay of the burn from the state it was searched from, how far from the place it comes down, when it lands, and the burn.
            public double Delay, Miss, Impact;
            public V3 Burn;
            // The UT of the state the delay counts from, and the plane turn the state follows, if any.
            public double Now;
            public bool Turned;
            public Burn Turn;

            internal static Best None
            {
                get { return new Best { Delay = double.NaN, Miss = double.PositiveInfinity }; }
            }
        }

        // One descent, on its way on the thread pool.
        private sealed class Flight
        {
            public double Delay;
            public V3 Burn;
            public ReentrySimulation Simulation;
            public ReentrySimulation.Result Result;
            public Exception Error;
            private volatile bool finished;

            public bool Finished
            {
                get { return finished; }
            }

            public void Run(object unused)
            {
                try
                {
                    Result = Simulation.RunSimulation();
                }
                catch (Exception e)
                {
                    Error = e;
                }
                finally
                {
                    finished = true;
                }
            }
        }

        private readonly Vessel vessel;
        private readonly CelestialBody body;
        private readonly Orbit orbit;
        private readonly double latitude, longitude, start, terrain, tolerance;
        private readonly IEnumerator<bool> steps;
        private readonly Stopwatch slice = new Stopwatch();
        private readonly Stopwatch whole = Stopwatch.StartNew();
        private bool cancelled;
        // The best burn of the line being searched, the burns tried, and why one could not be simulated.
        private Best line;
        private int tries;
        private string failure;
        private double slowest;

        // Whether the search is over, and then either the best burn found or why there is none.
        internal bool Done { get; private set; }
        internal bool Found { get; private set; }
        internal Best Choice { get; private set; }
        internal string Failure { get; private set; }

        internal DeorbitSearch(Frame frame, double latitude, double longitude)
        {
            vessel = frame.Vessel;
            body = frame.Body;
            orbit = frame.Orbit;
            start = frame.Now;
            this.latitude = latitude;
            this.longitude = longitude;
            tolerance = DeorbitAim.Tolerance(body);
            terrain = Math.Max(0.0, body.TerrainAltitude(latitude, longitude, true));
            steps = Run().GetEnumerator();
        }

        internal string Progress
        {
            get { return "Searching the deorbit burn that lands on the marker: " + tries + " burns tried."; }
        }

        // A search that is not wanted any more. The descents on their way end by themselves and are left to the collector.
        internal void Cancel()
        {
            cancelled = true;
            Done = true;
            Found = false;
            if (Failure == null) Failure = "The search was stopped.";
        }

        // One slice of the work, on the game's thread: sets up descents and takes the results that are in, for a few milliseconds.
        internal void Advance()
        {
            if (Done) return;
            slice.Reset();
            slice.Start();
            try
            {
                while (!Done && slice.Elapsed.TotalMilliseconds < SliceMilliseconds)
                {
                    if (!steps.MoveNext())
                    {
                        Finish();
                        return;
                    }
                    // False: waiting for the thread pool, so nothing more is to be done in this slice.
                    if (!steps.Current) return;
                }
            }
            catch (Exception e)
            {
                Debug.LogError(Log.Tag + " Navigation: the deorbit search failed: " + e);
                Failure = "The search failed: " + e.Message;
                Done = true;
            }
            slowest = Math.Max(slowest, slice.Elapsed.TotalMilliseconds);
        }

        // The search carried on until it is done, for a plan that has to answer at once.
        internal void Complete()
        {
            while (!Done)
            {
                Advance();
                if (Done) break;
                if (whole.Elapsed.TotalSeconds > GiveUpSeconds)
                {
                    Failure = "The search took too long.";
                    Cancel();
                    break;
                }
                Thread.Sleep(1);
            }
        }

        private void Finish()
        {
            Done = true;
            if (double.IsNaN(Choice.Delay))
            {
                Failure = "No burn was found whose descent lands. " + (failure ?? "None of the " + tries + " burns tried comes down.");
                return;
            }
            Found = true;
            Debug.Log(Log.Tag + " Navigation: the deorbit searched " + tries + " burns in " + whole.Elapsed.TotalSeconds.ToString("0.0") + " s, at most " + slowest.ToString("0.0")
                      + " ms of the game's thread in a frame; the best is in " + Choice.Delay.ToString("0") + " s, " + Choice.Burn.Length.ToString("0") + " m/s, and comes down "
                      + Choice.Miss.ToString("0") + " m from the marker.");
        }

        // A place off the orbit's plane is brought under it first, and the deorbit is searched from the orbit that burn leaves. The turn depends on when
        // the vessel is over the place, which the search finds; so the turn is worked out again for that time, once.
        private IEnumerable<bool> Run()
        {
            double mu = body.gravParameter;
            State origin = KspOrbits.StateAt(orbit, start);
            double arrival = double.NaN;
            Best best = Best.None;
            for (int pass = 0; pass < 2 && !cancelled; pass++)
            {
                Burn turn;
                bool turned = PlaneChange.Plan(body, origin, latitude, longitude, tolerance, arrival, out turn) == PlaneTurn.Needed;
                // Without a turn the second search would be the first again.
                if (pass == 1 && !turned && !best.Turned) break;
                State here = origin;
                double now = start;
                if (turned)
                {
                    State at = Kepler.Propagate(origin, turn.Delay, mu);
                    here = new State(at.R, at.V + turn.DeltaV);
                    now = start + turn.Delay;
                }
                foreach (bool ready in Line(here, mu, now))
                    yield return ready;
                if (cancelled) yield break;
                Best found = line;
                if (double.IsNaN(found.Delay)) break;
                found.Now = now;
                found.Turned = turned;
                found.Turn = turn;
                if (pass == 0 || found.Miss < best.Miss) best = found;
                if (best.Miss <= tolerance) break;
                arrival = found.Impact - start;
            }
            Choice = best;
        }

        // The best time along the line of burns from a state, in `line`. The times over the first stretch are tried at even steps, all at once. The interval
        // round the best is then narrowed by golden section, since the miss falls and rises about it.
        private IEnumerable<bool> Line(State here, double mu, double now)
        {
            double period = new Conic(here, mu).Period;
            double step = Orbits * period / Samples;
            line = Best.None;
            double low = Soon, high = Soon;
            var flights = new List<Flight>();
            for (int i = 0; i <= Samples; i++)
            {
                flights.Add(Launch(here, mu, now, Soon + i * step));
                if (slice.Elapsed.TotalMilliseconds >= SliceMilliseconds) yield return true;
            }
            foreach (bool waiting in Await(flights))
                yield return waiting;
            if (cancelled) yield break;
            for (int i = 0; i <= Samples; i++)
            {
                double before = line.Miss;
                Take(flights[i], Soon + i * step);
                if (line.Miss < before)
                {
                    low = Math.Max(Soon, Soon + i * step - step);
                    high = Soon + i * step + step;
                }
            }
            if (double.IsNaN(line.Delay)) yield break;
            const double golden = 0.6180339887;
            double a = low, b = high;
            double c = b - golden * (b - a), d = a + golden * (b - a);
            Flight flightC = Launch(here, mu, now, c), flightD = Launch(here, mu, now, d);
            foreach (bool waiting in Await(new List<Flight> { flightC, flightD }))
                yield return waiting;
            if (cancelled) yield break;
            double missC = Take(flightC, c), missD = Take(flightD, d);
            for (int i = 0; i < Refinements && line.Miss > tolerance; i++)
            {
                double asked;
                bool newIsC = missC < missD;
                if (newIsC)
                {
                    b = d;
                    d = c;
                    missD = missC;
                    c = b - golden * (b - a);
                    asked = c;
                }
                else
                {
                    a = c;
                    c = d;
                    missC = missD;
                    d = a + golden * (b - a);
                    asked = d;
                }
                Flight next = Launch(here, mu, now, asked);
                foreach (bool waiting in Await(new List<Flight> { next }))
                    yield return waiting;
                if (cancelled) yield break;
                double miss = Take(next, asked);
                if (newIsC) missC = miss;
                else missD = miss;
            }
        }

        // Waits, one frame at a time, for the descents to be done.
        private IEnumerable<bool> Await(List<Flight> flights)
        {
            while (!cancelled)
            {
                bool all = true;
                foreach (Flight flight in flights)
                    if (flight != null && !flight.Finished) all = false;
                if (all) yield break;
                yield return false;
            }
        }

        // The descent after the burn at `delay` set up and sent to the thread pool; null where there is no burn or the descent cannot be set up.
        private Flight Launch(State here, double mu, double now, double delay)
        {
            State leaving = Kepler.Propagate(here, delay, mu);
            V3 burn;
            if (!DeorbitAim.Retrograde(leaving, mu, body.Radius * Periapsis, out burn)) return null;
            try
            {
                V3 velocity = leaving.V + burn;
                double ut = now + delay;
                // The orbit maths works in the world's axes; the game's orbit in its own, which has the second and third swapped.
                var begin = new Orbit();
                begin.UpdateFromStateVectors(new Vector3d(leaving.R.X, leaving.R.Z, leaving.R.Y), new Vector3d(velocity.X, velocity.Z, velocity.Y), body, ut);
                double thrust = Engines.Acceleration(vessel);
                double gravity = body.GeeASL * 9.81;
                double decelEnd = LandingPrediction.BrakeEnd(vessel, terrain);
                IDescentSpeedPolicy policy = LandingPrediction.Policy(vessel, terrain, decelEnd, gravity, thrust);
                // The curves and the vessel are copies for this descent alone, as the thread that runs it must not share them.
                ReentrySimulation.SimCurves curves = ReentrySimulation.SimCurves.Borrow(body);
                SimulatedVessel simulated = SimulatedVessel.Borrow(vessel, curves, ut, -1);
                var flight = new Flight
                {
                    Delay = delay,
                    Burn = burn,
                    Simulation = ReentrySimulation.Borrow(begin, ut, simulated, curves, policy, decelEnd, thrust, 3.0, terrain, false, 0.2, Time.fixedDeltaTime, 1.0, false),
                };
                ThreadPool.QueueUserWorkItem(flight.Run);
                return flight;
            }
            catch (Exception e)
            {
                failure = "The descent could not be set up: " + e.Message;
                return null;
            }
        }

        // The result of a descent, on the game's thread: how far from the place it comes down, infinity where it does not land. The best is kept.
        private double Take(Flight flight, double delay)
        {
            if (flight == null) return double.PositiveInfinity;
            double miss = double.PositiveInfinity;
            try
            {
                if (flight.Error != null)
                {
                    failure = "The descent could not be simulated: " + flight.Error.Message;
                }
                else if (flight.Result != null && flight.Result.Outcome == ReentrySimulation.Outcome.LANDED)
                {
                    miss = DeorbitAim.Distance(body.Radius, latitude, longitude, flight.Result.EndPosition.Latitude, flight.Result.EndPosition.Longitude);
                    tries++;
                    if (miss < line.Miss)
                    {
                        line.Miss = miss;
                        line.Delay = delay;
                        line.Burn = flight.Burn;
                        line.Impact = flight.Result.EndUT;
                    }
                }
            }
            finally
            {
                flight.Simulation.Release();
                if (flight.Result != null) flight.Result.Release();
            }
            return miss;
        }
    }
}
