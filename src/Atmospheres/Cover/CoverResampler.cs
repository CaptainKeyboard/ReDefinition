using System;
using System.Threading;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Keeps the CoverMap of each body in step with the field's surface layer. The main thread copies the field's SnowCover map (a
    // 256 x 128 float array, 128 KB) when the field's version changed and the last copy is at least ResampleSeconds old, or at once
    // after a jump of the game time; a worker resamples it to the R16 map of 1024 x 512 and publishes the frame. The decisions are
    // static methods. Package D34, brief item 2.
    internal static class CoverResampler
    {
        // Real seconds between two resamples of a body, and the jump in game seconds that makes the next one immediate.
        internal const float ResampleSeconds = 30f;
        internal const double JumpSeconds = 1800.0;

        private sealed class Job
        {
            internal CoverBodyState State;
            internal float[] Values;
            internal int Width, Height;
            internal long SourceVersion;
            internal double Ut;
            internal float MaxDepthM;
            internal volatile bool Done;
            internal volatile string Error;
            internal long Milliseconds;
        }

        private sealed class Book
        {
            internal float LastAt = -1000f;
            internal double LastUt = double.NaN;
            internal long LastFieldVersion = long.MinValue;
            internal Job Running;
            internal readonly WeatherMap Fetch = new WeatherMap();
            internal bool NoMapLogged;
        }

        private static readonly System.Collections.Generic.Dictionary<int, Book> Books = new System.Collections.Generic.Dictionary<int, Book>();

        // The milliseconds of the last resample on the worker, the number of frames published and the failures, for the log and the probe.
        internal static long LastMilliseconds { get; private set; }
        internal static int Published { get; private set; }
        internal static int Failures { get; private set; }

        // Whether a body is resampled now: nothing running, and the map is new enough or the time jumped or the field changed and the
        // interval has passed. fieldChanged is the field's version against the last copy's.
        internal static bool Due(bool running, bool first, bool fieldChanged, double secondsSinceLast, double utJump)
        {
            if (running) return false;
            if (first) return true;
            if (Math.Abs(utJump) >= JumpSeconds) return true;
            return fieldChanged && secondsSinceLast >= ResampleSeconds;
        }

        // One tick for a body on the main thread: collects a finished job, starts a new one when due. now is the real time in seconds.
        internal static void Tick(CoverBodyState state, float now, double ut)
        {
            Book book;
            if (!Books.TryGetValue(state.Index, out book)) Books[state.Index] = book = new Book();
            Job job = book.Running;
            if (job != null)
            {
                if (!job.Done) return;
                book.Running = null;
                if (job.Error != null)
                {
                    Failures++;
                    AtmospheresLog.WarnOnce("the cover map of " + state.Body.name + " could not be resampled (" + job.Error + ")");
                    return;
                }
                LastMilliseconds = job.Milliseconds;
                Published++;
            }
            IWeatherField field = WeatherHub.Field;
            if (field == null) return;
            bool first = state.Map.SourceVersion < 0;
            long version = field.Version;
            double jump = double.IsNaN(book.LastUt) ? 0 : ut - book.LastUt;
            if (!Due(false, first, version != book.LastFieldVersion, now - book.LastAt, jump)) return;
            if (!field.GetMap(state.Index, WeatherMapKind.SnowCover, ut, book.Fetch))
            {
                // The field has no map for the body yet (its state is still being built): ask again in a second.
                if (!book.NoMapLogged)
                {
                    book.NoMapLogged = true;
                    AtmospheresLog.Say("cover: the field has no surface map for " + state.Body.name + " yet (" + field.Readiness + "); asking again.");
                }
                book.LastAt = now - ResampleSeconds + 1f;
                return;
            }
            book.NoMapLogged = false;
            book.LastAt = now;
            book.LastUt = ut;
            book.LastFieldVersion = version;
            var work = new Job
            {
                State = state,
                Width = book.Fetch.Width,
                Height = book.Fetch.Height,
                SourceVersion = book.Fetch.Version,
                Ut = ut,
                MaxDepthM = state.Def.MaxDepthM,
                Values = (float[])book.Fetch.Values.Clone()
            };
            book.Running = work;
            ThreadPool.QueueUserWorkItem(delegate { Run(work); });
        }

        private static void Run(Job job)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                CoverMap.Frame frame = job.State.Map.Resample(job.Values, job.Width, job.Height, job.MaxDepthM, job.SourceVersion, job.Ut);
                job.State.Map.Publish(frame);
            }
            catch (Exception e)
            {
                job.Error = e.GetType().Name + " " + e.Message;
            }
            finally
            {
                job.Milliseconds = watch.ElapsedMilliseconds;
                job.Done = true;
            }
        }

        internal static void Reset()
        {
            Books.Clear();
        }
    }
}
