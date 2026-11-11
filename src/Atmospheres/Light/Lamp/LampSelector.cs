using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The part of LampMath that needs the quality tier of the host's caps (WeatherHooks), so that LampMath itself links into the player.
    internal static partial class LampMath
    {
        // The most lamps drawn at a quality tier: Low 2, Medium 4, High and above 8; Auto and an unknown tier 4.
        internal static int TierCap(QualityTier tier)
        {
            switch (tier)
            {
                case QualityTier.Low: return 2;
                case QualityTier.Medium: return 4;
                case QualityTier.High:
                case QualityTier.Ultra:
                case QualityTier.Max: return 8;
                default: return 4;
            }
        }

        // The cap used: the setting (lightCount), held to the tier's cap.
        internal static int Cap(int setting, QualityTier tier)
        {
            int cap = TierCap(tier);
            return Math.Max(0, Math.Min(setting, cap));
        }
    }

    // A lamp that took part in a rescoring: its identity (the engine's instance id of the Light) and its score.
    internal struct LampCandidate
    {
        internal int Id;
        internal float Score;

        internal LampCandidate(int id, float score)
        {
            Id = id;
            Score = score;
        }
    }

    // The set of lamps that are drawn. Rescore (5 Hz) names the best N of the candidates; Step (every frame) fades the ones that entered
    // in and the ones that left out over FadeS seconds, so that a lamp never pops in or out. Pure: no engine type.
    internal sealed class LampSelector
    {
        private struct Entry
        {
            internal int Id;
            internal float Fade;
            internal bool Wanted;
        }

        private static readonly Comparison<LampCandidate> ByScore = (a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        };

        private readonly List<Entry> entries = new List<Entry>(16);

        // The number of lamps in the set, those fading out included.
        internal int Count
        {
            get { return entries.Count; }
        }

        internal int IdAt(int index)
        {
            return entries[index].Id;
        }

        // The fade of the lamp at an index, 0 (out) to 1 (fully in).
        internal float FadeAt(int index)
        {
            return entries[index].Fade;
        }

        internal bool WantedAt(int index)
        {
            return entries[index].Wanted;
        }

        // The fade of a lamp by id; false where it is not in the set.
        internal bool TryGetFade(int id, out float fade)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Id == id)
                {
                    fade = entries[i].Fade;
                    return true;
                }
            }
            fade = 0f;
            return false;
        }

        // Names the best `cap` of the candidates (highest score, lower id on a tie); the others are no longer wanted. The list is sorted
        // in place.
        internal void Rescore(List<LampCandidate> candidates, int cap)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                e.Wanted = false;
                entries[i] = e;
            }
            if (candidates == null || cap <= 0) return;
            candidates.Sort(ByScore);
            int n = Math.Min(cap, candidates.Count);
            for (int k = 0; k < n; k++)
            {
                int id = candidates[k].Id;
                int at = IndexOf(id);
                if (at >= 0)
                {
                    Entry e = entries[at];
                    e.Wanted = true;
                    entries[at] = e;
                }
                else
                {
                    entries.Add(new Entry { Id = id, Fade = 0f, Wanted = true });
                }
            }
        }

        // Moves every fade towards its target by dt / fadeS; a lamp that is not wanted and has faded out leaves the set.
        internal void Step(float dt, float fadeS)
        {
            float delta = fadeS > 1e-4f ? dt / fadeS : 1f;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry e = entries[i];
                e.Fade = e.Wanted ? Math.Min(1f, e.Fade + delta) : Math.Max(0f, e.Fade - delta);
                if (!e.Wanted && e.Fade <= 0f)
                {
                    entries.RemoveAt(i);
                    continue;
                }
                entries[i] = e;
            }
        }

        // Empties the set (a scene change).
        internal void Clear()
        {
            entries.Clear();
        }

        private int IndexOf(int id)
        {
            for (int i = 0; i < entries.Count; i++) if (entries[i].Id == id) return i;
            return -1;
        }
    }
}
