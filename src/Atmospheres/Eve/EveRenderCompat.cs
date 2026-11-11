using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Whether EVE's volumetric clouds can be rendered in this install.
    internal enum EveRenderStatus
    {
        // EVE is not installed or its members are not as assumed.
        NotUsable,
        // EVE is there and nothing says its renderer fails.
        Ready,
        // EVE holds volumetric layers and Scatterer is not loaded. Measured twice (RECON, run2 and run3, EVE present, Scatterer absent):
        // the frame does not follow the scene and EVE logs "Invalid pass number (6)" and "(7)" for Graphics.Blit of EVE/RaymarchCloud
        // thousands of times; with Scatterer (run4) or without EVE (run5) neither happens.
        NoScatterer,
        // EVE's renderer logged the invalid-pass error repeatedly in this run, whatever the mod set.
        RenderFailing
    }

    // Counts the lines of the invalid-pass error, pure: the log hook feeds it, a test drives it.
    internal sealed class RenderFailureCounter
    {
        // The number of lines after which the renderer counts as failing: the error comes thousands of times a second when it comes at
        // all (7312 in one run3 start), so a handful is no accident of a scene load.
        internal const int Threshold = 30;

        internal int Count { get; private set; }

        internal bool Tripped
        {
            get { return Count >= Threshold; }
        }

        // The error EVE's renderer logs: "Invalid pass number (6) ... Graphics.Blit (Material "EVE/RaymarchCloud" with 2 passes)".
        internal static bool IsInvalidPass(string message)
        {
            return !string.IsNullOrEmpty(message) && message.IndexOf("Invalid pass number", StringComparison.Ordinal) >= 0
                && message.IndexOf("RaymarchCloud", StringComparison.Ordinal) >= 0;
        }

        // Feeds one log line; true when this line made the counter trip.
        internal bool Add(string message)
        {
            if (!IsInvalidPass(message)) return false;
            bool was = Tripped;
            Count++;
            return !was && Tripped;
        }
    }

    // The combination EVE without Scatterer, detected cheaply and reported. What it can detect:
    //   - at the first look (module start): EVE's types are loaded and no assembly named Scatterer is, and EVE holds at least one
    //     volumetric layer (the layers whose raymarch pass is what fails): status NoScatterer, from the assembly list and EVE's layer list;
    //   - while running: the error line of the failing pass, counted from the game's log (a hook on Application.logMessageReceived that
    //     looks at the text only): status RenderFailing after RenderFailureCounter.Threshold lines.
    // What it cannot detect: the cause inside EVE (a pass index of 6 and 7 against a two-pass material is not isolated), and a failing
    // renderer of a mod set that has Scatterer but another defect: only the second rule would catch that, after the lines came.
    // The consequence the module takes: cloudSource Eve and Both are treated as Ours (CloudModeModel.Effective): nothing is bound to
    // EVE's layers and the own shell shows the picture. The freeze itself is EVE's own and is not stopped by this. The status and the
    // reason are for the settings window (D36) and the log; the status is logged once per change.
    internal static class EveRenderCompat
    {
        private static EveRenderStatus status = EveRenderStatus.NotUsable;
        private static string reason = "EVE is not looked at yet";
        private static readonly RenderFailureCounter Failures = new RenderFailureCounter();
        private static bool hooked;
        private static volatile bool failureTripped;

        internal static EveRenderStatus Status
        {
            get { return status; }
        }

        // The reason in words, for the log and the settings window.
        internal static string Reason
        {
            get { return reason; }
        }

        // Whether an assembly named Scatterer is loaded (for the series probe's rows).
        internal static bool ScattererLoaded
        {
            get { return AssemblyLoaded("Scatterer"); }
        }

        // How many lines of the invalid-pass error the hook has counted.
        internal static int FailureLines
        {
            get { return Failures.Count; }
        }

        // EVE is there and cannot be rendered with: Eve and Both do not bind.
        internal static bool Blocked
        {
            get { return status == EveRenderStatus.NoScatterer || status == EveRenderStatus.RenderFailing; }
        }

        // The cloudSource in force for a body whose own or global setting is source.
        internal static CloudSourceKind EffectiveSource(CloudSourceKind source)
        {
            return CloudModeModel.Effective(source, Blocked);
        }

        // The status from what is known, pure (the decision table the tests drive).
        internal static EveRenderStatus Decide(bool eveUsable, bool scattererLoaded, int volumetricLayers, bool failureSeen, out string why)
        {
            if (!eveUsable)
            {
                why = "EVE is not installed or its members are not as the painter assumes";
                return EveRenderStatus.NotUsable;
            }
            if (failureSeen)
            {
                why = "EVE's raymarch pass logged the invalid-pass error " + RenderFailureCounter.Threshold + " times or more; the clouds are not bound to EVE";
                return EveRenderStatus.RenderFailing;
            }
            if (!scattererLoaded && volumetricLayers > 0)
            {
                why = "EVE holds " + volumetricLayers + " volumetric layer(s) and Scatterer is not installed: EVE's renderer freezes the frame (RECON K-pre run2 and run3); the clouds are not bound to EVE";
                return EveRenderStatus.NoScatterer;
            }
            why = scattererLoaded ? "EVE and Scatterer are loaded" : "EVE is loaded without Scatterer and holds no volumetric layer";
            return EveRenderStatus.Ready;
        }

        // Looks again; called by the module once a second at most. Logs once when the status changes.
        internal static void Update(bool eveUsable)
        {
            if (eveUsable) Hook();
            string why;
            int layers = eveUsable && !AssemblyLoaded("Scatterer") ? EveLayerAccess.VolumetricLayerCount() : 0;
            EveRenderStatus now = Decide(eveUsable, AssemblyLoaded("Scatterer"), layers, failureTripped, out why);
            if (now == status && why == reason) return;
            bool changed = now != status;
            status = now;
            reason = why;
            if (changed) AtmospheresLog.Say("EVE render check: " + now + " (" + why + ")");
        }

        internal static void Shutdown()
        {
            if (hooked)
            {
                Application.logMessageReceived -= OnLog;
                hooked = false;
            }
        }

        private static void Hook()
        {
            if (hooked) return;
            hooked = true;
            Application.logMessageReceived += OnLog;
        }

        // Runs for every line of the game's log; the work is one text test and, for the error, a counter.
        private static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Log) return;
            if (Failures.Add(message)) failureTripped = true;
        }

        private static bool AssemblyLoaded(string name)
        {
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
