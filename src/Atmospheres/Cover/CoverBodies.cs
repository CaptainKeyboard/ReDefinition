using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The cover of one body in the game: the layer that is read from the field's surface map, the resampled map and the tracks.
    // The state lives as long as the game session; the map is filled by CoverResampler.
    internal sealed class CoverBodyState
    {
        internal CoverBodyState(CelestialBody body, BodyClimate climate, CoverDef def)
        {
            Body = body;
            Index = body.flightGlobalsIndex;
            Climate = climate;
            Def = def;
        }

        internal readonly CelestialBody Body;
        internal readonly int Index;
        internal BodyClimate Climate;
        internal CoverDef Def;
        internal readonly CoverMap Map = new CoverMap();
        // The tracks of the body, made when the first wheel marks (the radius is the body's).
        private TrackTiles tracks;

        internal TrackTiles Tracks
        {
            get { return tracks ?? (tracks = new TrackTiles(Body.Radius)); }
        }

        internal bool HasTracks
        {
            get { return tracks != null; }
        }

        // Forgets the tracks (another save game was loaded).
        internal void ResetTracks()
        {
            tracks = null;
        }

        private KspFrame frame;

        // The frame from the sphere's directions to the game's latitude and longitude, measured on first use (main thread only).
        internal KspFrame Frame
        {
            get { return frame ?? (frame = Calibrate()); }
        }

        private KspFrame Calibrate()
        {
            try
            {
                Vector3d a = Body.GetRelSurfaceNVector(0.0, 0.0), b = Body.GetRelSurfaceNVector(0.0, 90.0), p = Body.GetRelSurfaceNVector(90.0, 0.0);
                var f = new KspFrame(a.x, a.y, a.z, b.x, b.y, b.z, p.x, p.y, p.z);
                if (f.Valid)
                {
                    AtmospheresLog.Say("cover: the latitude and longitude of " + Body.name + " read from the sphere's directions (0,0) = ("
                                   + a.x.ToString("0.###") + ", " + a.y.ToString("0.###") + ", " + a.z.ToString("0.###") + "), (0,90) = ("
                                   + b.x.ToString("0.###") + ", " + b.y.ToString("0.###") + ", " + b.z.ToString("0.###") + ")");
                    return f;
                }
                AtmospheresLog.WarnOnce("cover: the directions of latitude and longitude of " + Body.name + " are not a frame; the sphere's own convention is used.");
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("cover: the frame of " + Body.name + " could not be read; the sphere's own convention is used (" + CompatibilityLog.Reason(e) + ")");
            }
            return KspFrame.Sphere;
        }
    }

    // The bodies that carry cover, by the body's index: created from the body's climate when the cover is first asked for.
    internal static class CoverBodies
    {
        private static readonly Dictionary<int, CoverBodyState> States = new Dictionary<int, CoverBodyState>();

        internal static int Count { get { return States.Count; } }

        // The state of a body, or null where the body carries no cover (no layer, switched off, broken). Reads the climate once per
        // call: a changed climate (a reset of the provider) gives a new definition.
        internal static CoverBodyState Of(CelestialBody body)
        {
            if (body == null) return null;
            BodyClimate climate = Bodies.Get(body);
            if (climate == null || climate.Status.Kind == BodyStatusKind.Off || climate.Status.Kind == BodyStatusKind.Broken) return null;
            CoverRules rules = Primary(climate);
            if (rules == null) return null;
            CoverBodyState state;
            if (States.TryGetValue(body.flightGlobalsIndex, out state))
            {
                if (ReferenceEquals(state.Climate, climate)) return state;
                state.Climate = climate;
                state.Def = CoverDefs.From(rules);
                return state;
            }
            state = new CoverBodyState(body, climate, CoverDefs.From(rules));
            States[body.flightGlobalsIndex] = state;
            return state;
        }

        internal static void Clear()
        {
            States.Clear();
        }

        internal static IEnumerable<CoverBodyState> All { get { return States.Values; } }

        // The rule whose depth the field's surface map holds: the sand or dust layer on a dusty body (the generator reads its map as
        // dust there), else the snow layer, else the first layer that is not None.
        internal static CoverRules Primary(BodyClimate climate)
        {
            if (climate == null) return null;
            CoverRules snow = null, first = null;
            bool dusty = GeneratorBodies.DustType(climate) != PrecipType.None;
            foreach (CoverRules rule in climate.Covers)
            {
                if (rule.Kind == CoverKind.None) continue;
                if (first == null) first = rule;
                if (dusty && (rule.Kind == CoverKind.Sand || rule.Kind == CoverKind.Dust)) return rule;
                if (!dusty && rule.Kind == CoverKind.Snow && snow == null) snow = rule;
            }
            return snow ?? first;
        }
    }

    internal static class CoverDefs
    {
        // The definition of a layer from its rule. The ground colour map's mean is not read: the rule always carries a colour.
        internal static CoverDef From(CoverRules r)
        {
            Color c = r.Colour;
            return new CoverDef(r.Id, r.Kind, c.r, c.g, c.b, r.Roughness, r.MaxDepthM, r.MinLatDeg, r.MaxLatDeg, r.MinAltM, r.MaxAltM,
                r.MaxSlopeDeg, r.NoWater, r.Tracks, r.TracksTauHours * 3600.0, r.DriftAmount, r.DriftStretch);
        }
    }

    // The game time as the cover reads it: null where the game has none (menu, loading).
    internal static class CoverClock
    {
        internal static bool TryUt(out double ut)
        {
            ut = 0;
            if (Planetarium.fetch == null) return false;
            ut = Planetarium.GetUniversalTime();
            return !double.IsNaN(ut);
        }
    }
}
