using System;
using System.Collections.Generic;
using System.Text;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What a scenario waits for before it goes on: a number of frames, a time in seconds of real time, or a condition with a time limit
    // (a scenario ends on a condition, never on a timer alone). Created by the factory methods and yielded from SeriesProbe.Scenario.
    internal sealed class ProbeWait
    {
        internal enum Kind { Frames, Seconds, Until }

        internal readonly Kind What;
        internal readonly int FrameCount;
        internal readonly double SecondCount;
        internal readonly Func<bool> Condition;
        internal readonly string Description;

        private ProbeWait(Kind what, int frames, double seconds, Func<bool> condition, string description)
        {
            What = what;
            FrameCount = frames;
            SecondCount = seconds;
            Condition = condition;
            Description = description;
        }

        // The next frames.
        internal static ProbeWait Frames(int count)
        {
            return new ProbeWait(Kind.Frames, Math.Max(1, count), 0, null, "frames");
        }

        // That many seconds of real time.
        internal static ProbeWait Seconds(double seconds)
        {
            return new ProbeWait(Kind.Seconds, 0, Math.Max(0, seconds), null, "seconds");
        }

        // Until the condition holds; the probe fails when it does not within the limit.
        internal static ProbeWait Until(Func<bool> condition, double timeoutSeconds, string description)
        {
            if (condition == null) throw new ArgumentNullException("condition");
            return new ProbeWait(Kind.Until, 0, Math.Max(0, timeoutSeconds), condition, description ?? "condition");
        }
    }

    // The base of the probe of a series: a module that is active only with -atmoSeries <name> on the game's command line. Its scenario
    // is an iterator that yields ProbeWait values; the base steps it once per frame from the registry's Tick, collects the checks,
    // writes the result lines (AtmoResult) with the variant that is switched on, and ends with the line "Probe: PASSED" or
    // "Probe: FAILED (reasons)" itself, after which the game quits (-atmoKeep leaves it running). A package puts its probe in its own
    // folder, <Folder>/Probe/<Id>Probe.cs, as a class with [AtmospheresModule(order)] that derives from this one.
    //
    // Variants: one start of the game switches the variants of one item or feature one after another (-atmoVariants a,b,c, else
    // DefaultVariants); BeginVariant calls ApplyVariant, and every result line written until EndVariant carries the variant's name.
    // Forced state goes through the contract: ForceTuning (Tuning.SetOverride), ForcePrecipitation (ISurfaceForcing of the field),
    // SetSetting (AtmospheresSettings) and ForceUniversalTime.
    internal abstract class SeriesProbe : IAtmospheresModule
    {
        internal const string SeriesSwitch = "-atmoSeries";
        internal const string StartSwitch = "-atmoStart";
        internal const string VariantsSwitch = "-atmoVariants";
        internal const string KeepSwitch = "-atmoKeep";
        internal const string OnlySwitch = "-atmoProbe";
        internal const double QuitDelaySeconds = 3.0;

        // Where the probe's own lines go (the game's log); a test replaces it.
        internal static Action<string> LogLine = message => AtmospheresLog.Say(message);

        private enum State { Inactive, Running, Finished }

        private State state = State.Inactive;
        private IEnumerator<ProbeWait> scenario;
        private ProbeWait wait;
        private int framesLeft;
        private double secondsLeft;
        private double waited;
        private double elapsed;
        private double quitIn = -1;
        private string conditionError;
        private readonly List<string> failures = new List<string>();
        private readonly List<string> variants = new List<string>();
        private string variant;

        // The action that ends the game; a test replaces it.
        internal Action QuitGame = () => Application.Quit();

        // The series this probe belongs to, as the series file names it (K-pre).
        protected abstract string SeriesName { get; }

        // The scenario: yield a ProbeWait to let frames pass, call Check and Emit between the yields. It ends by returning.
        protected abstract IEnumerator<ProbeWait> Scenario();

        // The most real seconds the whole scenario may take (the series has 20 minutes for the whole start, the game needs about 90 s
        // to come up and some to quit).
        protected virtual double MaxSeconds
        {
            get { return 16 * 60; }
        }

        // The variants used when the command line names none.
        protected virtual IList<string> DefaultVariants
        {
            get { return new string[0]; }
        }

        // Switches a variant on; called by BeginVariant.
        protected virtual void ApplyVariant(string name)
        {
        }

        // Puts a variant back; called by EndVariant.
        protected virtual void RevertVariant(string name)
        {
        }

        // Whether the game is in the flight scene with a ready active vessel: the guard of the first wait of every probe that needs the flight.
        // FlightGlobals.ActiveVessel dereferences the FlightGlobals object, which does not exist in the main menu, so the scene is asked first.
        // A test replaces the state with Func<bool>.
        internal static Func<bool> FlightReadyState = () => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.fetch != null && FlightGlobals.ActiveVessel != null;

        protected static bool InFlightReady()
        {
            return FlightReadyState();
        }

        // A probe that needs the flight returns a reason here once its flight is gone (the scene changed, the vessel was destroyed); the base then
        // ends the scenario with that failure instead of letting the next step throw. Null while all is well, and before the flight was reached.
        protected virtual string LostGround()
        {
            return null;
        }

        // The command line a test gives the probe instead of the game's.
        internal string[] CommandLineOverride;

        // The game's command line; a test overrides it.
        protected virtual string[] Args()
        {
            return CommandLineOverride ?? Environment.GetCommandLineArgs();
        }

        // Whether the command line asks for this series. K-pre, Kpre and kpre are the same name.
        internal static bool IsActive(string[] args, string series)
        {
            string asked = CommandLine.After(args, SeriesSwitch, true);
            return asked != null && Normal(asked) == Normal(series);
        }

        // A series name without its dashes, underscores and spaces, in lower case.
        internal static string Normal(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c != '-' && c != '_' && c != ' ') sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // Whether this probe runs in this start of the game.
        internal bool Active
        {
            get { return state != State.Inactive; }
        }

        // Whether the verdict has been written.
        internal bool Finished
        {
            get { return state == State.Finished; }
        }

        // True when the scenario ended with no failed check.
        internal bool Passed
        {
            get { return state == State.Finished && failures.Count == 0; }
        }

        internal IList<string> Failures
        {
            get { return failures; }
        }

        // The probes of the process that run their scenario now; the game quits when none is left.
        private static readonly List<SeriesProbe> RunningProbes = new List<SeriesProbe>();

        private static bool OthersRunning()
        {
            lock (RunningProbes) return RunningProbes.Count > 0;
        }

        // A test starts from an empty set.
        internal static void ForgetRunningProbes()
        {
            lock (RunningProbes) RunningProbes.Clear();
        }

        // Whether the -atmoProbe value (one name or a comma-separated list) names the probe class.
        internal static bool NamesProbe(string list, string className)
        {
            foreach (string part in list.Split(','))
            {
                if (string.Equals(part.Trim(), className, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // The start of the series this run is (-atmoStart), or null.
        internal string StartId { get; private set; }

        // The variants of this start.
        internal IList<string> Variants
        {
            get { return variants; }
        }

        // The variant that is on, or null.
        internal string CurrentVariant
        {
            get { return variant; }
        }

        // Real seconds since the scenario began.
        internal double Elapsed
        {
            get { return elapsed; }
        }

        public void Init()
        {
            string[] args = Args();
            if (!IsActive(args, SeriesName)) return;
            // A start that names its probe (-atmoProbe D38Probe) leaves the other probes of the series idle: the first probe to finish
            // quits the game, so a long scenario cannot share a start with the short ones.
            // The switch takes one name or a comma-separated list (-atmoProbe B12Probe,C25Probe): the probes of a list share the start
            // and the game quits only after the last of them has its verdict.
            string only = CommandLine.After(args, OnlySwitch, true);
            if (!string.IsNullOrEmpty(only) && !NamesProbe(only, GetType().Name)) return;
            StartId = CommandLine.After(args, StartSwitch, true);
            string named = CommandLine.After(args, VariantsSwitch, true);
            variants.Clear();
            if (!string.IsNullOrEmpty(named))
            {
                foreach (string part in named.Split(','))
                {
                    if (part.Trim().Length > 0) variants.Add(part.Trim());
                }
            }
            else
            {
                variants.AddRange(DefaultVariants);
            }
            scenario = Scenario();
            state = State.Running;
            lock (RunningProbes) RunningProbes.Add(this);
            LogLine("Probe: series " + SeriesName + (StartId != null ? " start " + StartId : "") + " begins"
                    + (variants.Count > 0 ? " with variants " + string.Join(",", variants.ToArray()) : ""));
        }

        public virtual void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
            if (state == State.Finished)
            {
                if (quitIn >= 0 && !OthersRunning())
                {
                    quitIn -= dt;
                    if (quitIn < 0)
                    {
                        quitIn = -1;
                        QuitGame();
                    }
                }
                return;
            }
            if (state != State.Running) return;
            elapsed += dt;
            if (elapsed > MaxSeconds)
            {
                Finish("the scenario did not end within " + MaxSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s");
                return;
            }
            try
            {
                string lost = LostGround();
                if (lost != null)
                {
                    failures.Add(lost);
                    wait = null;
                    Finish(null);
                    return;
                }
                if (wait != null && !Satisfied(dt)) return;
                wait = null;
                conditionError = null;
                if (scenario.MoveNext())
                {
                    wait = scenario.Current;
                    framesLeft = wait.FrameCount;
                    secondsLeft = wait.SecondCount;
                    waited = 0;
                }
                else
                {
                    Finish(null);
                }
            }
            catch (Exception e)
            {
                failures.Add("exception in the scenario: " + e.GetType().Name + " " + e.Message);
                Finish(null);
            }
        }

        public void Shutdown()
        {
            if (state == State.Running)
            {
                failures.Add("the game shut down before the scenario ended");
                Finish(null);
            }
        }

        private bool Satisfied(double dt)
        {
            switch (wait.What)
            {
                case ProbeWait.Kind.Frames:
                    framesLeft--;
                    return framesLeft <= 0;
                case ProbeWait.Kind.Seconds:
                    secondsLeft -= dt;
                    return secondsLeft <= 0;
                default:
                    // A condition that throws (a game object that does not exist yet, as in the main menu) has not come true yet: the wait goes on
                    // and the last exception is named in the timeout, so a condition that never works is still reported.
                    try
                    {
                        if (wait.Condition()) return true;
                    }
                    catch (Exception e)
                    {
                        conditionError = e.GetType().Name + " " + e.Message;
                    }
                    waited += dt;
                    if (waited > wait.SecondCount)
                    {
                        failures.Add("timeout after " + wait.SecondCount.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s waiting for " + wait.Description
                                     + (conditionError != null ? " (the condition threw: " + conditionError + ")" : ""));
                        wait = null;
                        Finish(null);
                    }
                    return false;
            }
        }

        // Records a check; a failed one fails the probe (the scenario goes on, so that one start returns every row it can).
        protected void Check(string name, bool ok, string detail = null)
        {
            if (state == State.Finished) return;
            LogLine("Probe: check " + name + ": " + (ok ? "ok" : "FAILED") + (detail != null ? " (" + detail + ")" : ""));
            if (!ok) failures.Add(name + (detail != null ? " (" + detail + ")" : ""));
        }

        // Fails the probe with a reason and ends the scenario.
        protected void Fail(string reason)
        {
            failures.Add(reason);
            Finish(null);
        }

        // Writes a result line of the series, with the variant that is on.
        protected void Emit(string metric, double value)
        {
            if (state == State.Finished) return;
            AtmoResult.Emit(SeriesName, metric, value, variant);
        }

        protected void Emit(string metric, bool value)
        {
            AtmoResult.Emit(SeriesName, metric, value, variant);
        }

        protected void EmitText(string metric, string value)
        {
            AtmoResult.EmitText(SeriesName, metric, value, variant);
        }

        // Switches a variant on: the scenario's lines carry its name until EndVariant.
        protected void BeginVariant(string name)
        {
            if (variant != null) EndVariant();
            variant = name;
            LogLine("Probe: variant " + name + " on");
            ApplyVariant(name);
        }

        // Puts the variant back.
        protected void EndVariant()
        {
            if (variant == null) return;
            string name = variant;
            RevertVariant(name);
            variant = null;
        }

        // A constant of the look through the tuning override, effective at the next frame; ClearForced puts the file's values back.
        protected void ForceTuning(string key, float value)
        {
            Tuning.SetOverride(key, value);
        }

        protected void ClearForced()
        {
            Tuning.ClearOverrides();
        }

        // Precipitation on a disc through the field's test forcing; false where the field has none.
        protected bool ForcePrecipitation(int bodyIndex, double latDeg, double lonDeg, double radiusKm, float rateMmPerH, PrecipType type,
            double fromUt, double toUt)
        {
            var forcing = WeatherHub.Field as ISurfaceForcing;
            if (forcing == null) return false;
            forcing.AddPrecipitationDisc(bodyIndex, latDeg, lonDeg, radiusKm, rateMmPerH, type, fromUt, toUt);
            return true;
        }

        // A setting of the mod by its key (restored by the runner after the start).
        protected bool SetSetting(string key, double value)
        {
            return AtmospheresSettings.SetNumber(key, value);
        }

        protected bool SetSetting(string key, string value)
        {
            return AtmospheresSettings.SetText(key, value);
        }

        // The game's clock.
        protected virtual void ForceUniversalTime(double ut)
        {
            Planetarium.SetUniversalTime(ut);
        }

        // Writes the verdict once. A reason adds to the failures.
        private void Finish(string reason)
        {
            if (state == State.Finished) return;
            if (reason != null) failures.Add(reason);
            try
            {
                EndVariant();
            }
            catch (Exception e)
            {
                failures.Add("reverting the variant failed: " + e.Message);
            }
            state = State.Finished;
            lock (RunningProbes) RunningProbes.Remove(this);
            LogLine(failures.Count == 0 ? "Probe: PASSED" : "Probe: FAILED (" + string.Join("; ", failures.ToArray()) + ")");
            if (!CommandLine.Has(Args(), KeepSwitch, true)) quitIn = QuitDelaySeconds;
        }
    }
}
