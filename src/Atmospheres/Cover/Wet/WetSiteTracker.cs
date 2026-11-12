using System;

namespace ReDefinition.Atmospheres
{
    // The wetness of one site kept in step with the game's clock (package T08): a cursor in game time and the water state at the cursor.
    // A new site, a rewind or a gap longer than the lookback starts a catch-up from the beginning of the lookback; the catch-up and every
    // later advance read the weather of the site through a source, a limited number of steps per call so that one frame never does the
    // whole replay. Pure maths, no engine type: the renderer owns the source and the frame loop, the tests own both of them here.
    internal sealed class WetSiteTracker
    {
        internal WetState State;
        // The game time the state is valid at; NaN before the first start.
        internal double CursorUt = double.NaN;
        // True from a start until the cursor has reached the game time.
        internal bool CatchingUp;

        // Whether the clock asks for a new start: no start yet, the clock went back, or the gap is longer than the lookback.
        internal bool NeedsStart(double ut, double lookbackS)
        {
            return double.IsNaN(CursorUt) || ut < CursorUt - 1.0 || ut - CursorUt > lookbackS;
        }

        // Starts dry at the beginning of the lookback before ut.
        internal void Start(double ut, double lookbackS)
        {
            State = default(WetState);
            CursorUt = ut - lookbackS;
            CatchingUp = true;
        }

        internal void Forget()
        {
            State = default(WetState);
            CursorUt = double.NaN;
            CatchingUp = false;
        }

        // Moves the state towards ut by at most budget steps; returns the steps taken. During a catch-up a step is at most the history
        // step of the tuning; afterwards a step is taken once sampleEveryS of game time has gone by (and is at most the history step).
        internal int Advance(WetnessCatchUp.Source source, WetClass kind, ref WetnessTuning t, double ut, int budget, double sampleEveryS)
        {
            int steps = 0;
            double historyS = t.HistoryStepH * 3600.0;
            while (CursorUt < ut && steps < budget)
            {
                double span = ut - CursorUt;
                if (!CatchingUp && span < sampleEveryS) break;
                double stepS = Math.Min(historyS, span);
                WetInput w = source(CursorUt + 0.5 * stepS);
                WetnessModel.Step(ref State, ref w, kind, ref t, (float)stepS);
                CursorUt += stepS;
                steps++;
            }
            if (CatchingUp && ut - CursorUt < 1.0) CatchingUp = false;
            return steps;
        }
    }
}
