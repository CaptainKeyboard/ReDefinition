using System;
using System.Threading;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    internal enum WindowJobState
    {
        // Nothing asked, or the last result was taken.
        Idle,
        // The worker is filling the back buffer.
        Running,
        // The back buffer is complete and waits to be taken.
        Ready,
        // The fill returned false (no coverage, a guard tripped); taking it resets the job.
        Failed
    }

    // A field that fills its 3D window on a worker (package C25/E43). The layered window costs 7 ms offline and about 20 ms in the game's
    // runtime, too much for the frame that asks for it; the volume pass asks, polls and takes the finished window. The window is the one
    // FillWindow would have made at the time of the ask (the settings are read by BeginWindow, on the caller's thread).
    internal interface IWindowJobField
    {
        WindowJobState WindowState { get; }
        // Starts a fill of the back buffer; false while a fill runs or its result is not taken yet, and where the field cannot fill a
        // window for the body.
        bool BeginWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut);
        // Ready: spare now holds the finished window and the job owns the window that was spare; Failed: nothing changed. Both reset the job.
        // Idle and Running change nothing.
        WindowJobState TakeWindow(ref WeatherWindow spare);
    }

    // The worker and the double buffer of IWindowJobField. One fill at a time; the buffers swap by reference on the taker's thread, so
    // the taker never sees a window that is being written and the worker never writes one the taker holds.
    internal sealed class WindowJob
    {
        private WeatherWindow back = new WeatherWindow();
        private int state;

        internal WindowJobState State
        {
            get { return (WindowJobState)Volatile.Read(ref state); }
        }

        // Runs fill(back) on a pool thread; false where a job is already in the air.
        internal bool Begin(Func<WeatherWindow, bool> fill)
        {
            if (fill == null) throw new ArgumentNullException("fill");
            if (Interlocked.CompareExchange(ref state, (int)WindowJobState.Running, (int)WindowJobState.Idle) != (int)WindowJobState.Idle) return false;
            WeatherWindow target = back;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok;
                try
                {
                    ok = fill(target);
                }
                catch (Exception e)
                {
                    AtmospheresLog.WarnOnce("window fill failed on the worker (" + e.GetType().Name + ": " + e.Message + ")");
                    ok = false;
                }
                Volatile.Write(ref state, (int)(ok ? WindowJobState.Ready : WindowJobState.Failed));
            });
            return true;
        }

        internal WindowJobState Take(ref WeatherWindow spare)
        {
            WindowJobState now = State;
            if (now == WindowJobState.Ready)
            {
                WeatherWindow finished = back;
                back = spare ?? new WeatherWindow();
                spare = finished;
                Volatile.Write(ref state, (int)WindowJobState.Idle);
            }
            else if (now == WindowJobState.Failed)
            {
                Volatile.Write(ref state, (int)WindowJobState.Idle);
            }
            return now;
        }
    }
}
