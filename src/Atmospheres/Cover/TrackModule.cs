using System;
using System.Collections.Generic;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Tracks of wheels and feet in the cover (brief item 5). Ten times a second the loaded vessels that stand on cover mark it: a
    // grounded ModuleWheelBase marks the place where the wheel touches (its centre one radius along the vessel's down), a kerbal on
    // foot marks its own place; a mark presses a disc into the body's TrackTiles where the cover is at least 0.2. The tiles fade by
    // their stamp (TrackTiles), are saved per body in PluginData/<save>/tracks-<body>.bin, and a snowfall fills them in. The drawing is
    // TrackDraw (stage 2 ribbons: the PQS vertices of Kerbin lie 66 to 93 m apart, RECON P3, so vertex colours cannot show a wheel's
    // track). Stage 1 (vertex colours) is not built. Tuning keys: tracks.halfWidthM 0.25, tracks.tauScale 1, tracks.fillSeconds 900,
    // tracks.stepM 0.25 (the least travel between two marks of one wheel).
    [AtmospheresModule(68)]
    internal sealed class TrackModule : IAtmospheresModule
    {
        internal const string FilePrefix = "tracks-";
        internal const float StampSeconds = 0.1f;
        internal const float SaveSeconds = 60f;
        private const float DropSeconds = 1f;

        private sealed class Wheels
        {
            internal ModuleWheelBase[] Modules;
            // The half width of each wheel's mark in metres (TrackWidth), found when the wheel is first met.
            internal float[] HalfWidths;
        }

        // The half width of a wheel's mark: the smallest dimension of its largest mesh (TrackWidth), the tuning value where it has none.
        private static float HalfWidthOf(ModuleWheelBase wheel, float fallback)
        {
            if (wheel == null || wheel.wheelTransform == null) return fallback;
            MeshFilter best = null;
            float bestVolume = 0f;
            foreach (MeshFilter f in wheel.wheelTransform.GetComponentsInChildren<MeshFilter>(false))
            {
                if (f == null || f.sharedMesh == null) continue;
                Vector3 s = Vector3.Scale(f.sharedMesh.bounds.size, f.transform.lossyScale);
                float volume = Mathf.Abs(s.x * s.y * s.z);
                if (volume > bestVolume)
                {
                    bestVolume = volume;
                    best = f;
                }
            }
            if (best == null) return fallback;
            Vector3 size = Vector3.Scale(best.sharedMesh.bounds.size, best.transform.lossyScale);
            return TrackWidth.HalfWidthM(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z), fallback);
        }

        private readonly Dictionary<Part, Wheels> wheelsOfPart = new Dictionary<Part, Wheels>();
        private readonly Dictionary<object, Vector3d> lastMark = new Dictionary<object, Vector3d>();
        private readonly HashSet<int> loadedBodies = new HashSet<int>();
        private readonly TrackDraw draw = new TrackDraw();
        private string loadedSave;
        private float stampClock, dropClock, saveClock, fillClock;
        private bool dirty;

        internal static TrackModule Current { get; private set; }
        // For the probe: marks pressed, tiles live, and the milliseconds of the last stamping round.
        internal int Marks { get; private set; }
        internal long LiveBytes { get; private set; }
        internal int LiveTiles { get; private set; }
        internal double LastStampMs { get; private set; }
        internal TrackDraw Draw { get { return draw; } }

        public void Init()
        {
            Current = this;
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
            if (!CoverModule.Enabled || !AtmospheresSettings.Tracks)
            {
                draw.Hide();
                return;
            }
            double ut;
            List<Vessel> loaded = GameGlobals.VesselsLoaded;
            if (loaded == null || !CoverClock.TryUt(out ut)) return;
            EnsureSave();
            stampClock += dt;
            dropClock += dt;
            saveClock += dt;
            fillClock += dt;
            if (dropClock >= DropSeconds)
            {
                dropClock = 0f;
                DropFuture(ut);
            }
            if (stampClock >= StampSeconds)
            {
                stampClock = 0f;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < loaded.Count; i++)
                    if (loaded[i] != null && loaded[i].loaded) Stamp(loaded[i], ut);
                LastStampMs = watch.Elapsed.TotalMilliseconds;
            }
            if (fillClock >= 5f)
            {
                Fill(fillClock, ut);
                fillClock = 0f;
            }
            LiveBytes = 0;
            LiveTiles = 0;
            foreach (CoverBodyState s in CoverBodies.All)
                if (s.HasTracks)
                {
                    LiveBytes += s.Tracks.LiveBytes;
                    LiveTiles += s.Tracks.LiveCount;
                }
            if (saveClock >= SaveSeconds && dirty) Save();
            draw.Tick(ut, ActiveVessel());
        }

        public void Shutdown()
        {
            Save();
            draw.Dispose();
            if (Current == this) Current = null;
        }

        private static Vessel ActiveVessel()
        {
            return GameGlobals.ActiveVessel;
        }

        // ---- the marks ------------------------------------------------------------------------------------------------------------

        private void Stamp(Vessel v, double ut)
        {
            if (v.mainBody == null || !(v.Landed || v.isEVA && v.situation != Vessel.Situations.FLYING)) return;
            CoverBodyState state = CoverBodies.Of(v.mainBody);
            if (state == null || !state.Def.Tracks || state.Map.Current.Number == 0) return;
            EnsureBody(state, ut);
            float halfWidth = Tuning.Get("tracks.halfWidthM", 0.25f);
            double tau = state.Def.TracksTauSeconds * Tuning.Get("tracks.tauScale", 1f);
            double stepM = Tuning.Get("tracks.stepM", 0.25f);
            if (v.isEVA)
            {
                if (v.horizontalSrfSpeed > 0.3) Mark(state, v, v.GetWorldPos3D(), ut, halfWidth * 0.8f, tau, stepM);
                return;
            }
            if (!AtmospheresAssumptions.Holds(AssumptionsD34.WheelId)) return;
            Vector3 down = -(Vector3)(v.GetWorldPos3D() - v.mainBody.position).normalized;
            foreach (Part p in v.parts)
            {
                if (p == null) continue;
                Wheels w;
                if (!wheelsOfPart.TryGetValue(p, out w))
                {
                    w = new Wheels { Modules = p.FindModulesImplementing<ModuleWheelBase>().ToArray() };
                    w.HalfWidths = new float[w.Modules.Length];
                    for (int k = 0; k < w.Modules.Length; k++) w.HalfWidths[k] = HalfWidthOf(w.Modules[k], halfWidth);
                    wheelsOfPart[p] = w;
                }
                for (int k = 0; k < w.Modules.Length; k++)
                {
                    ModuleWheelBase wheel = w.Modules[k];
                    if (wheel == null || !wheel.isGrounded || wheel.wheelTransform == null) continue;
                    Vector3d contact = (Vector3d)wheel.wheelTransform.position + (Vector3d)(down * Mathf.Max(0.05f, wheel.radius));
                    Mark(state, wheel, contact, ut, w.HalfWidths[k], tau, stepM);
                }
            }
        }

        // Presses a disc at a place unless this wheel (or kerbal) marked within stepM of it a moment ago.
        private void Mark(CoverBodyState state, object who, Vector3d world, double ut, float halfWidthM, double tau, double stepM)
        {
            Vector3d last;
            if (lastMark.TryGetValue(who, out last) && (last - world).sqrMagnitude < stepM * stepM) return;
            double lat = state.Body.GetLatitude(world), lon = state.Body.GetLongitude(world);
            CoverMap.Frame frame = state.Map.Current;
            double fieldDepthM = frame.Sample01(lat, lon) * state.Def.MaxDepthM;
            double mask = CoverDepth.Mask(state.Def, lat, Math.Max(0.0, state.Body.GetAltitude(world)), 0.0, false);
            double cover = CoverDepth.Depth01(state.Def, fieldDepthM, mask, 1.0, 0.0, AtmospheresSettings.CoverStrength);
            lastMark[who] = world;
            if (state.Tracks.Stamp(lat, lon, halfWidthM, ut, tau, cover))
            {
                Marks++;
                dirty = true;
            }
        }

        // Falling snow fills the tracks in: the share of what shows that a snowfall of the layer's type takes in the seconds since the last
        // look, taken at the active vessel's place.
        private void Fill(float seconds, double ut)
        {
            Vessel v = ActiveVessel();
            if (v == null || v.mainBody == null) return;
            CoverBodyState state = CoverBodies.Of(v.mainBody);
            if (state == null || !state.HasTracks) return;
            WeatherSample s;
            if (!WeatherHub.Sample(state.Index, v.latitude, v.longitude, Math.Max(0.0, v.altitude), ut, out s)) return;
            double fall = VehicleCover.FallOf(state.Def, s);
            if (fall <= 0.01) return;
            state.Tracks.Attenuate(fall * seconds / Math.Max(10.0, Tuning.Get("tracks.fillSeconds", 900f)));
            dirty = true;
        }

        private void DropFuture(double ut)
        {
            foreach (CoverBodyState s in CoverBodies.All)
                if (s.HasTracks && s.Tracks.DropAfter(ut + 1.0) > 0) dirty = true;
        }

        // ---- the side files ---------------------------------------------------------------------------------------------------------

        private static string PathOf(string save, CoverBodyState state)
        {
            return ModFolder.PluginFile(AtmospheresSettings.Mod, save + "/" + FilePrefix + state.Body.name + ".bin");
        }

        private void EnsureSave()
        {
            string save = HighLogic.SaveFolder;
            if (string.IsNullOrEmpty(save) || save == loadedSave) return;
            if (loadedSave != null) Save();
            loadedSave = save;
            loadedBodies.Clear();
            wheelsOfPart.Clear();
            lastMark.Clear();
            foreach (CoverBodyState s in CoverBodies.All) s.ResetTracks();
        }

        // Reads the body's tracks from the side file the first time a vessel marks on it.
        private void EnsureBody(CoverBodyState state, double ut)
        {
            if (loadedSave == null || !loadedBodies.Add(state.Index)) return;
            try
            {
                string path = PathOf(loadedSave, state);
                if (!File.Exists(path)) return;
                int kept = state.Tracks.LoadAll(File.ReadAllBytes(path), ut);
                AtmospheresLog.Say("tracks: " + (kept < 0 ? "the file of " + state.Body.name + " is not usable" : kept + " tiles of " + state.Body.name + " read from " + Path.GetFileName(path)));
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-tracks-read", "the tracks file could not be read", e);
            }
        }

        private void Save()
        {
            saveClock = 0f;
            dirty = false;
            if (loadedSave == null || !ForcedField.AllowSideFileWrite()) return;
            double ut;
            if (!CoverClock.TryUt(out ut)) return;
            foreach (CoverBodyState s in CoverBodies.All)
            {
                if (!s.HasTracks || s.Tracks.LiveCount + s.Tracks.ParkedCount == 0) continue;
                try
                {
                    string path = PathOf(loadedSave, s);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, s.Tracks.SaveAll(ut));
                }
                catch (Exception e)
                {
                    AtmospheresLog.Failed("atmospheres-tracks-write", "the tracks file could not be written", e);
                }
            }
        }
    }
}
