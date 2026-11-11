using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The quality tier of the atmospheres and what it does to the settings. The tier comes from the setting qualityTier; Auto takes the
    // host's tier (the main mod's graphics profile, WeatherHooks.Caps.Tier) and, without a host, Medium. Below 10 GB of video memory an
    // Auto tier falls to Low, because the volume and the maps then compete with the game for memory; a tier the player names is kept.
    //
    // Applying a tier writes the budget keys of its row (TierRows.SetKeys) and nothing else: a profile changes cost, never taste. It
    // happens when the tier changes (the first start included) and when the player resets; between those the player's own values of the
    // budget keys stay. Reset puts every key to its default and then writes the tier's row, so that it is the tier row and not the key
    // table's default that stands afterwards.
    internal static class QualityProfile
    {
        // Below this much video memory (MB) an Auto tier is Low.
        internal const long LowVramBelowMb = 10 * 1024;

        // The total video memory in MB; zero or less where it is not known. Tests assign their own.
        internal static Func<long> VramSource = EngineVram;

        // The host's tier; tests assign their own.
        internal static Func<QualityTier> HostTier = () => WeatherHooks.Caps.Tier;

        // The tier that stands for a request: a named tier as it is; Auto from the host, else Medium, and Low under the memory guard.
        internal static QualityTier Resolve(QualityTier requested, QualityTier hostTier, long vramMb)
        {
            if (requested != QualityTier.Auto) return requested;
            if (vramMb > 0 && vramMb < LowVramBelowMb) return QualityTier.Low;
            return hostTier != QualityTier.Auto ? hostTier : QualityTier.Medium;
        }

        // The tier in force now, from the setting, the host and the card.
        internal static QualityTier Effective()
        {
            return Resolve(AtmospheresSettings.QualityTier, HostTier(), Vram());
        }

        // The video memory in MB; zero (the guard off) where it cannot be read.
        internal static long Vram()
        {
            try
            {
                return VramSource();
            }
            catch (Exception e)
            {
                ForeignChanges.Report("the video memory could not be read, the memory guard is off (" + CompatibilityLog.Reason(e) + ")");
                return 0;
            }
        }

        // Writes the budget keys of a tier's row.
        internal static void Apply(QualityTier effective)
        {
            TierRow row = TierRows.For(effective);
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.VolumeSteps, row.VolumeSteps);
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.VolumeResolution, row.VolumeResolution(VolumeResolutionGate.Passed));
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.LightCount, row.LightCount);
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.SimGrid, (double)(int)row.SimGrid);
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.OrbitMapWidth, row.OrbitMapWidth);
        }

        // The row of the tier in force, for the renderers' budget numbers.
        internal static TierRow Current()
        {
            return TierRows.For(Effective());
        }

        // Reset: every setting to its default, foreign settings this product changed back to their originals, then the tier's row.
        // Returns the tier whose row was written.
        internal static QualityTier Reset()
        {
            AtmospheresSettings.ResetToDefaults();
            ForeignChanges.RestoreAll();
            QualityTier effective = Effective();
            Apply(effective);
            return effective;
        }

        // Whether the tier changed since the one the marker names, or no marker was written yet (the first start).
        internal static bool NeedsApply(QualityTier effective, QualityTier? marked)
        {
            return !marked.HasValue || marked.Value != effective;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long EngineVram()
        {
            return UnityEngine.SystemInfo.graphicsMemorySize;
        }
    }

    // The foreign settings this product changed, each with the way back. A module that sets a setting of another mod (EVE's layers,
    // a renderer switch) records the way back here when it makes its first change; Reset runs every way back once and forgets them.
    // Nothing is recorded for a setting the product did not change, so Reset leaves the player's own choices in other mods alone.
    internal static class ForeignChanges
    {
        private static readonly Dictionary<string, Action> ways = new Dictionary<string, Action>(StringComparer.Ordinal);

        // Where a failure is told; the tests assign their own.
        internal static Action<string> Report = AtmospheresLog.WarnOnce;

        internal static int Count
        {
            get { return ways.Count; }
        }

        // Notes a change with its way back; a second note for the same id keeps the first way (the original is the first value).
        internal static void Record(string id, Action restore)
        {
            if (string.IsNullOrEmpty(id) || restore == null) return;
            if (!ways.ContainsKey(id)) ways.Add(id, restore);
        }

        // Runs every way back, one failing does not keep the others from running, and forgets them. Returns how many ran.
        internal static int RestoreAll()
        {
            var pending = new List<KeyValuePair<string, Action>>(ways);
            ways.Clear();
            int ran = 0;
            foreach (KeyValuePair<string, Action> way in pending)
            {
                try
                {
                    way.Value();
                    ran++;
                }
                catch (Exception e)
                {
                    Report("the original of " + way.Key + " could not be restored (" + CompatibilityLog.Reason(e) + ")");
                }
            }
            return ran;
        }

        internal static void Clear()
        {
            ways.Clear();
        }
    }
}
