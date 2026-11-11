using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The graphics vendor and upscaler pairs for which the composition of the veil and flakes after the upscaler (weatherCompose = Host)
    // has recorded player metrics. weatherCompose = Auto resolves to Scene for every other pair, and the settings window says
    // "unverified" in that row's tooltip. A pair is added here by E40 when its series has measured it; none has yet.
    internal static class ComposeEvidence
    {
        private static readonly HashSet<string> Recorded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The key of a pair.
        internal static string Pair(string gpuVendor, UpscalerKind upscaler)
        {
            return (gpuVendor ?? "").Trim() + "/" + upscaler;
        }

        internal static void Record(string gpuVendor, UpscalerKind upscaler)
        {
            Recorded.Add(Pair(gpuVendor, upscaler));
        }

        internal static void Clear()
        {
            Recorded.Clear();
        }

        // Whether that pair has recorded metrics. With no upscaler there is nothing to compose after, and nothing to verify.
        internal static bool IsVerified(string gpuVendor, UpscalerKind upscaler)
        {
            return upscaler == UpscalerKind.None || Recorded.Contains(Pair(gpuVendor, upscaler));
        }

        // The tooltip line for weatherCompose = Auto: what it resolves to and, for an unrecorded pair, that this is unverified. Empty
        // where nothing needs saying.
        internal static string AutoLine(string gpuVendor, UpscalerKind upscaler, bool hostApi)
        {
            if (upscaler == UpscalerKind.None) return "Auto: the weather is drawn in the scene, as no upscaler runs.";
            if (!hostApi) return "Auto: the weather is drawn in the scene, as the host does not answer.";
            if (IsVerified(gpuVendor, upscaler)) return "Auto: composed after the upscaler; player metrics are recorded for " + Pair(gpuVendor, upscaler) + ".";
            return "Auto: drawn in the scene (unverified: no player metrics are recorded for " + Pair(gpuVendor, upscaler) + ").";
        }
    }
}
