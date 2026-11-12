using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Audio
{
    // The weather sound in the flight scene (package D33): wind bed with gusts, snowfall hiss, hull ticks, the quiet snow ambience, the
    // distant storm and the kerbal's steps in snow. The mixer of package B15 makes the sound on the audio thread from AudioParams; this
    // module reads the game once per frame, writes the parameters and handles everything that is the game's: the scene, the pause, the time
    // warp, the camera, the sliders, EVE's own loop and Rocket Sound Enhancement.
    //
    // Output path B (RECON P5): an AudioClip.Create stream on a 2D looping source. KSP's FlightDriver.SetPause does not set AudioListener.pause,
    // so the pause is read here and the gain goes to zero. KerbalEVA has no step hook, so StrideIntegrator finds the steps.
    // Master off, or any scene but the flight: no object exists and Tick reads nothing but the settings.
    [AtmospheresModule(91)]
    internal sealed class WeatherAudioModule : IAtmospheresModule
    {
        internal static WeatherAudioModule Instance { get; private set; }

        private const string RecordedBedPath = ClipLibrary<AudioClip>.Folder + "wind_texture_01";
        private const float VolumeReadSeconds = 1f;
        private const float CellSeconds = 0.5f;
        private const float SnowMapSeconds = 3f;
        private const float FootCacheSeconds = 0.5f;
        private const float SoloScanSeconds = 2f;
        private const float CreateRetrySeconds = 5f;

        private readonly SharedAudioState shared = new SharedAudioState();
        private readonly AudioGate gate = new AudioGate();
        private readonly AudioGate stepGate = new AudioGate();
        private readonly StrideIntegrator stride = new StrideIntegrator();
        private readonly ClipLibrary<AudioClip> library = new ClipLibrary<AudioClip>(AtmospheresLog.WarnOnce);
        private readonly ClipLibrary<AudioClip> customLibrary = new ClipLibrary<AudioClip>(AtmospheresLog.WarnOnce);
        private readonly WeatherMap snowMap = new WeatherMap();
        private readonly WeatherMap iceMap = new WeatherMap();
        private readonly Dictionary<AudioSource, bool> muted = new Dictionary<AudioSource, bool>();

        private WeatherAudioObjects objects;
        private AudioSwitches switches = AudioSwitches.Defaults();
        private ListenerState listener;
        private GameVolumes volumes = GameVolumes.Full();
        private EveSoundDecision eve = EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, false);
        private MufflingPlan muffling = MufflingTable.Decide(AudioMuffling.Auto, false, false, false);
        private bool rsePresent;
        private bool rseRoutes;
        private bool rseChecked;
        private RseClipRouter rseRouter;
        private bool rseGroupObserved;
        private bool rseGroupEverSeen;
        private bool stepsStoppedForPause;
        private WeatherAudioInputs lastInputs;
        private bool textureLoaded;
        private bool subscribed;
        private float nextVolumeRead;
        private float nextCreateTry;
        private float nextCell;
        private float nextSnowMap = -1f;
        private float nextSolo;
        private float snowCoverListener;
        private TimedValue footCover;
        private TimedValue footIce;
        private CellPlace cell;
        private float cellCloseness;
        private float cellPan;
        private uint rng = 2463534242u;
        private int bodyIndex = -1;
        private int stepsFound;
        private int stepsPlayed;
        private float distanceWalked;
        private AudioListener cachedListener;
        private float nextListenerScan;

        // --- What the series reads ---
        internal bool HasObjects { get { return objects != null && objects.Alive; } }
        internal AudioGate Gate { get { return gate; } }
        internal SharedAudioState Shared { get { return shared; } }
        internal WeatherAudioObjects Objects { get { return objects; } }
        internal EveSoundDecision EveDecision { get { return eve; } }
        internal MufflingPlan Muffling { get { return muffling; } }
        internal ListenerState Listener { get { return listener; } }
        internal CellPlace Cell { get { return cell; } }
        internal bool RsePresent { get { return rsePresent; } }
        internal bool RseRoutesOurClips { get { return rseRoutes; } }
        // A step source of ours has been seen with an output mixer group (RSE took the clip), at least once since the objects were made.
        internal bool RseGroupEverSeen { get { return rseGroupEverSeen; } }
        // The inputs of the last frame (the game's own read, or the probe's forced ones).
        internal WeatherAudioInputs LastInputs { get { return lastInputs; } }
        internal GameVolumes Volumes { get { return volumes; } }
        internal float CellPanNow { get { return cellPan; } }
        internal float CellClosenessNow { get { return cellCloseness; } }
        internal bool TextureLoaded { get { return textureLoaded; } }
        internal int StepsFound { get { return stepsFound; } }
        internal int StepsPlayed { get { return stepsPlayed; } }
        internal float DistanceWalkedM { get { return distanceWalked; } }
        internal int FootstepClips { get { return library.Count; } }
        internal AudioListener ActiveListener { get { return cachedListener; } }
        internal int CreatedObjects { get; private set; }

        public void Init()
        {
            Instance = this;
            if (!subscribed)
            {
                GameEvents.onGameSceneLoadRequested.Add(OnSceneRequested);
                subscribed = true;
            }
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            // The body's sound node may name other clips: the objects are made again when the body changes.
            if (objects != null) Teardown("body changed");
        }

        public void Shutdown()
        {
            Teardown("shutdown");
            if (subscribed)
            {
                GameEvents.onGameSceneLoadRequested.Remove(OnSceneRequested);
                subscribed = false;
            }
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void OnSceneRequested(GameScenes scene)
        {
            Teardown("scene change");
        }

        public void Tick(float dt)
        {
            switches = AudioOverrides.Switches ?? AudioSwitches.FromSettings();
            bool flight = HighLogic.LoadedSceneIsFlight && GameGlobals.Present;
            if (!switches.Master || !flight)
            {
                if (objects != null) Teardown(switches.Master ? "no flight scene" : "switched off");
                return;
            }
            if (objects == null)
            {
                // A failed start is tried again after a few seconds, not every frame.
                if (Time.realtimeSinceStartup < nextCreateTry) return;
                if (!Create())
                {
                    nextCreateTry = Time.realtimeSinceStartup + CreateRetrySeconds;
                    return;
                }
            }

            float now = Time.realtimeSinceStartup;
            float real = Time.unscaledDeltaTime;
            Vessel vessel = GameGlobals.ActiveVessel;
            bool eva = vessel != null && vessel.isEVA;
            if (AudioOverrides.Volumes.HasValue) volumes = AudioOverrides.Volumes.Value;
            else if (now >= nextVolumeRead) ReadVolumes(now);

            // The listener rules.
            if (AudioOverrides.Listener.HasValue)
            {
                listener = AudioOverrides.Listener.Value;
            }
            else
            {
                CameraManager.CameraMode mode = CameraManager.Instance != null ? CameraManager.Instance.currentCameraMode : CameraManager.CameraMode.Flight;
                bool cabinCamera = mode == CameraManager.CameraMode.IVA || mode == CameraManager.CameraMode.Internal;
                listener = ListenerModel.From(cabinCamera, MapView.MapIsEnabled || mode == CameraManager.CameraMode.Map, eva);
            }

            // The weather at the listener.
            WeatherAudioInputs inputs;
            if (AudioOverrides.Inputs.HasValue) inputs = AudioOverrides.Inputs.Value;
            else inputs = ReadWeather(now, vessel);

            // EVE's own loop.
            string bodyName = vessel != null && vessel.mainBody != null ? vessel.mainBody.name : "";
            EveSoundAccess.Scan(bodyName);
            bool eveAudible = EveSoundAccess.AudibleVolume() > EveSoundPolicyTable.AudibleVolume;
            eve = EveSoundPolicyTable.Decide(switches.Eve, eveAudible);

            // The distant storm. A probe that forces the inputs may still ask for the real cell chain (tracks, listener transform).
            bool realCell = !AudioOverrides.Inputs.HasValue || AudioOverrides.RealCell;
            if (switches.DistantStorm && realCell) ReadCell(now, vessel);
            else if (!switches.DistantStorm) { cellCloseness = 0f; cellPan = 0f; }
            if (realCell)
            {
                inputs.CellCloseness = cellCloseness;
                inputs.CellPan = cellPan;
            }
            lastInputs = inputs;

            // The gate: switch, air, pause, warp. The steps have their own gate: a step is felt through the ground, so a body without
            // air does not silence it (pause and warp still do).
            bool paused = AudioOverrides.Paused ?? ReadPaused();
            bool rails = AudioOverrides.OnRailsWarp ?? ReadOnRailsWarp();
            float gain = gate.Step(real, inputs.Air, paused, rails);
            float stepGain = stepGate.Step(real, true, paused, rails);
            if (paused)
            {
                // A pause does not end a one-shot that is sounding: the steps and the thump stop once.
                if (!stepsStoppedForPause)
                {
                    objects.StopSteps();
                    stepsStoppedForPause = true;
                }
            }
            else
            {
                stepsStoppedForPause = false;
            }

            AudioParamsMapper.Apply(inputs, listener, switches, volumes, eve, gain, ref shared.Params);
            shared.Params.bedSource = BedSourceRule.Effective(switches.Bed, textureLoaded);
            shared.MapBlend = listener.MapView ? 1f : 0f;
            shared.HelmetBlend = listener.Eva && !listener.MapView ? 1f : 0f;

            // Muffling of the sample clips.
            rseGroupObserved = objects.StepMixerGroup.Length > 0;
            muffling = MufflingTable.Decide(switches.Muffling, rsePresent, rseRoutes, rseGroupObserved);
            if (rseGroupObserved) rseGroupEverSeen = true;
            if (muffling.DetachFromRse) objects.DetachStepMixerGroup();
            float lowPass = 22000f;
            if (muffling.OwnOnSamples) lowPass = listener.MapView ? (float)WeatherAudioStream.MapCutoffHz : listener.Cabin ? 900f : 22000f;
            objects.SetStepLowPass(lowPass);

            UpdateFootsteps(vessel, eva, stepGain);
            if (switches.Solo) MuteOthers(now); else if (muted.Count > 0) RestoreOthers();
        }

        // Called from LateUpdate of the object: after EVE's update of the frame.
        internal void LateTick()
        {
            if (eve.SilenceEve) EveSoundAccess.Silence();
            if (switches.Solo) MuteOthers(Time.realtimeSinceStartup);
        }

        // --- Objects ---

        private bool Create()
        {
            WeatherAudioObjects created = null;
            try
            {
                int rate = WeatherAudioObjects.OutputRate();
                created = new WeatherAudioObjects(shared, rate, 0x5EA50001u);
                float[] texture = null;
                int textureRate = 0;
                textureLoaded = false;
                Vessel vessel = GameGlobals.ActiveVessel;
                BodyClimate climate = vessel != null && vessel.mainBody != null ? Bodies.Get(vessel.mainBody) : Bodies.Active;
                SoundRules rules = climate != null ? climate.Sound : SoundRules.Default;
                string bedPath = string.IsNullOrEmpty(rules.WindBed) ? RecordedBedPath : rules.WindBed;
                if (switches.Bed != AudioBedSource.Synth) textureLoaded = LoadTexture(bedPath, out texture, out textureRate);
                created.Create(texture, textureRate);
                objects = created;
                CreatedObjects++;

                Func<string, bool> exists = p => GameDatabase.Instance != null && GameDatabase.Instance.ExistsAudioClip(p);
                Func<string, AudioClip> get = p => GameDatabase.Instance.GetAudioClip(p);
                library.Load(exists, get);
                customLibrary.Load(p => false, get);
                if (!string.IsNullOrEmpty(rules.FootstepSurface)) customLibrary.AddSingle(1, rules.FootstepSurface, exists, get);
                if (!library.Any) AtmospheresLog.WarnOnce("weather sound: no footstep clip exists under " + ClipLibrary<AudioClip>.Folder + "; footsteps are off");
                DetectRse();
                gate.Arrive();
                stepGate.Arrive();
                // [VERIFY] The kerbal's walk and run speeds are not read from the game and not measured: 3 m/s as the boundary of a run
                // and the 6 m/s of StepShaper are estimates, and so are vessel.srfSpeed, Landed and OnALadder as the inputs of the
                // stride integrator for a KerbalEVA vessel.
                stride.RunSpeedMs = 3.0f;
                AtmospheresLog.Say("weather sound: started (" + rate + " Hz, footstep clips " + library.Count + ", bed " + (textureLoaded ? "texture" : "synth")
                    + ", RSE " + (rsePresent ? (rseRoutes ? "routes our clips" : "present") : "absent") + ", EVE sound " + EveSoundAccess.Summary + ")");
                return true;
            }
            catch (Exception e)
            {
                objects = null;
                // Whatever was made before the failure goes again.
                if (created != null)
                {
                    try { created.Destroy(); } catch (Exception) { }
                }
                AtmospheresLog.Failed("atmospheres-audio-create", "weather sound: could not start", e);
                return false;
            }
        }

        private void Teardown(string why)
        {
            if (objects == null) return;
            try
            {
                objects.Destroy();
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-audio-destroy", "weather sound: could not destroy", e);
            }
            objects = null;
            RestoreOthers();
            cachedListener = null;
            footCover.Reset();
            footIce.Reset();
            nextSnowMap = -1f;
            gate.Arrive();
            stepGate.Arrive();
            rseGroupEverSeen = false;
            rseGroupObserved = false;
            stepsStoppedForPause = false;
            AtmospheresLog.Say("weather sound: stopped (" + why + ")");
        }

        private static bool LoadTexture(string path, out float[] mono, out int rate)
        {
            mono = null;
            rate = 0;
            if (GameDatabase.Instance == null || !GameDatabase.Instance.ExistsAudioClip(path))
            {
                AtmospheresLog.WarnOnce("weather sound: " + path + " does not exist; the synthesised bed plays");
                return false;
            }
            AudioClip clip = GameDatabase.Instance.GetAudioClip(path);
            if (clip == null || clip.samples < 2 || !clip.LoadAudioData())
            {
                AtmospheresLog.WarnOnce("weather sound: " + path + " could not be decoded; the synthesised bed plays");
                return false;
            }
            var data = new float[clip.samples * clip.channels];
            if (!clip.GetData(data, 0))
            {
                AtmospheresLog.WarnOnce("weather sound: " + path + " could not be read; the synthesised bed plays");
                return false;
            }
            mono = new float[clip.samples];
            for (int i = 0; i < clip.samples; i++)
            {
                float sum = 0f;
                for (int c = 0; c < clip.channels; c++) sum += data[i * clip.channels + c];
                mono[i] = sum / clip.channels;
            }
            rate = clip.frequency;
            return true;
        }

        // --- Reading the game ---

        private void ReadVolumes(float now)
        {
            nextVolumeRead = now + VolumeReadSeconds;
            volumes.Ambience = Mathf.Clamp01(GameSettings.AMBIENCE_VOLUME);
            volumes.Ship = Mathf.Clamp01(GameSettings.SHIP_VOLUME);
        }

        internal static bool ReadPaused()
        {
            return FlightDriver.Pause || (PauseMenu.exists && PauseMenu.isOpen) || Time.timeScale <= 0f;
        }

        internal static bool ReadOnRailsWarp()
        {
            return TimeWarp.fetch != null && TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRate > 1.01f;
        }

        private WeatherAudioInputs ReadWeather(float now, Vessel vessel)
        {
            WeatherView view = WeatherHooks.View;
            var w = new WeatherAudioInputs();
            CelestialBody body = vessel != null ? vessel.mainBody : null;
            bool air = body != null && body.atmosphere && vessel.altitude < body.atmosphereDepth && !view.IsStale(Time.frameCount);
            w.Air = air;
            if (!air) return w;
            w.Type = view.Type;
            w.Level01 = view.Level;
            float wave = 2f * view.GustPhase - 1f;
            float horizontal = (float)Math.Sqrt(view.WindWorld.x * view.WindWorld.x + view.WindWorld.y * view.WindWorld.y);
            float gustFactor = Math.Max(0.3f, 1f + WindNoise.Depth(view.Level) * wave);
            w.GustWave = wave;
            w.MeanWindMs = horizontal / gustFactor;
            BodyClimate climate = Bodies.Get(body);
            w.BodyGrit = climate != null && climate.Sound.Grit;
            w.SnowCover01 = SnowCoverAt(now, body, vessel.latitude, vessel.longitude);
            return w;
        }

        // The snow cover at a place from the field's SnowCover map, the map refreshed every few seconds.
        private float SnowCoverAt(float now, CelestialBody body, double latDeg, double lonDeg)
        {
            bool needed = switches.QuietSnow || (switches.Footsteps && GameGlobals.ActiveVessel != null && GameGlobals.ActiveVessel.isEVA);
            if (!needed) return 0f;
            if (now >= nextSnowMap || bodyIndex != body.flightGlobalsIndex)
            {
                nextSnowMap = now + SnowMapSeconds;
                bodyIndex = body.flightGlobalsIndex;
                try
                {
                    if (!WeatherHub.Field.GetMap(bodyIndex, WeatherMapKind.SnowCover, Planetarium.GetUniversalTime(), snowMap)) snowMap.Width = 0;
                    if (!WeatherHub.Field.GetMap(bodyIndex, WeatherMapKind.IceFraction, Planetarium.GetUniversalTime(), iceMap)) iceMap.Width = 0;
                }
                catch (Exception e)
                {
                    snowMap.Width = 0;
                    AtmospheresLog.Failed("atmospheres-audio-map", "weather sound: the surface maps could not be read", e);
                }
            }
            snowCoverListener = snowMap.Width > 0 ? Mathf.Clamp01(snowMap.Sample(latDeg, lonDeg)) : 0f;
            return snowCoverListener;
        }

        private void ReadCell(float now, Vessel vessel)
        {
            if (now < nextCell) return;
            nextCell = now + CellSeconds;
            cell = new CellPlace();
            cellCloseness = 0f;
            CelestialBody body = vessel != null ? vessel.mainBody : null;
            Func<int, ForecastTrack[]> tracks = WeatherHooks.BodyTracks;
            if (body == null || tracks == null) return;
            cell = DistantCellModel.Nearest(tracks(body.flightGlobalsIndex), Planetarium.GetUniversalTime(), vessel.latitude, vessel.longitude, body.Radius);
            if (!cell.Found) return;
            cellCloseness = DistantCellModel.Closeness(cell.EdgeDistanceM) * cell.Strength01;
            AudioListener l = FindListener(now);
            Transform t = l != null ? l.transform : null;
            if (t == null) return;
            Vector3d cellWorld = body.GetWorldSurfacePosition(cell.LatDeg, cell.LonDeg, 0.0);
            Vector3 toCell = (Vector3)(cellWorld - (Vector3d)t.position);
            Vector3 up = (Vector3)(((Vector3d)t.position - body.position).normalized);
            cellPan = DistantCellModel.Pan(toCell, t.right, up);
        }

        private AudioListener FindListener(float now)
        {
            if (cachedListener != null && cachedListener.enabled && cachedListener.gameObject.activeInHierarchy && now < nextListenerScan) return cachedListener;
            nextListenerScan = now + 2f;
            cachedListener = null;
            // The listener of the camera that is rendering: ScaledSpace's camera carries an inactive one.
            // Among the enabled listeners on active objects the one whose object has the enabled camera with the highest depth.
            AudioListener[] all = UnityEngine.Object.FindObjectsOfType<AudioListener>();
            float bestDepth = float.NegativeInfinity;
            for (int i = 0; i < all.Length; i++)
            {
                AudioListener candidate = all[i];
                if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy) continue;
                Camera camera = candidate.GetComponent<Camera>();
                float depth = camera != null && camera.enabled ? camera.depth : float.NegativeInfinity;
                if (cachedListener == null || depth > bestDepth)
                {
                    cachedListener = candidate;
                    bestDepth = depth;
                }
            }
            return cachedListener;
        }

        // The listener the series puts its probe on.
        internal AudioListener ListenerNow()
        {
            return FindListener(Time.realtimeSinceStartup);
        }

        // The same after a camera change: the cached listener is dropped and the scene is searched again.
        internal AudioListener RescanListener()
        {
            nextListenerScan = 0f;
            return FindListener(Time.realtimeSinceStartup);
        }

        // --- RSE ---

        private void DetectRse()
        {
            if (rseChecked) return;
            rseChecked = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, "RocketSoundEnhancement", StringComparison.Ordinal))
                {
                    rsePresent = true;
                    rseRouter = RseClipRouter.FromAssembly(assembly);
                    break;
                }
            }
            if (!rsePresent) return;
            if (rseRouter == null) AtmospheresLog.WarnOnce("weather sound: RSE is installed but its clip routing table was not found; the footsteps keep the own low-pass");
            try
            {
                // Every one of our twelve clips must be in a routing node, not the first alone.
                var all = new List<string>();
                AddRouted(all, ClipLibrary<AudioClip>.SnowSoft);
                AddRouted(all, ClipLibrary<AudioClip>.SnowMedium);
                AddRouted(all, ClipLibrary<AudioClip>.SnowHard);
                foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes("RSE_SETTINGS"))
                {
                    ConfigNode routing = node.GetNode("CustomMixerRouting");
                    ConfigNode clips = routing != null ? routing.GetNode("AudioClips") : null;
                    if (clips == null) continue;
                    bool every = true;
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (!clips.HasValue(all[i])) { every = false; break; }
                    }
                    if (every) { rseRoutes = true; break; }
                }
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-audio-rse", "weather sound: RSE settings could not be read", e);
            }
        }

        private static void AddRouted(List<string> into, string[] names)
        {
            for (int i = 0; i < names.Length; i++) into.Add(ClipLibrary<AudioClip>.Folder + names[i]);
        }

        // --- Footsteps ---

        // gain: the gate of the steps (open without air; pause and warp close it). Steps are not pre-started: the first block of the
        // stream path has its start-up gap, the steps are one-shot sources and have none.
        private void UpdateFootsteps(Vessel vessel, bool eva, float gain)
        {
            if (!switches.Footsteps) return;
            float speed;
            bool grounded;
            bool simulated = AudioOverrides.Walking;
            float step = Time.deltaTime;
            if (simulated)
            {
                speed = AudioOverrides.WalkSpeedMs;
                grounded = true;
            }
            else if (eva)
            {
                speed = (float)vessel.srfSpeed;
                grounded = vessel.Landed && (vessel.evaController == null || !vessel.evaController.OnALadder);
            }
            else
            {
                speed = 0f;
                grounded = false;
            }
            StepEvent ev;
            float distance = grounded ? speed * step : 0f;
            if (grounded && speed >= StrideIntegrator.StandSpeedMs) distanceWalked += distance;
            if (!stride.Feed(distance, speed, grounded, step, out ev)) return;
            stepsFound++;
            if (!library.Any && !customLibrary.Any) return;

            float now = Time.realtimeSinceStartup;
            if (footCover.NeedsUpdate(now))
            {
                float cover = AudioOverrides.ForcedCover ?? (vessel != null && vessel.mainBody != null ? SnowCoverAt(now, vessel.mainBody, vessel.latitude, vessel.longitude) : 0f);
                footCover.Set(cover, now, FootCacheSeconds);
                float ice = iceMap.Width > 0 && vessel != null ? iceMap.Sample(vessel.latitude, vessel.longitude) : 0f;
                footIce.Set(ice, now, FootCacheSeconds);
            }
            var query = new SurfaceQuery
            {
                Cover01 = footCover.Value,
                Water = !simulated && vessel != null && vessel.Splashed,
                IceRink = footIce.Value > 0.5f,
                HasCustomClip = customLibrary.Any
            };
            StepSurface surface = FootstepGate.Decide(query, switches.FootstepsSurface);
            if (surface == StepSurface.None || gain < 0.02f) return;

            float r1 = NextRandom(), r2 = NextRandom();
            float mass = vessel != null ? (float)vessel.GetTotalMass() : StepShaper.ReferenceMassT;
            StepVoice voice = StepShaper.Shape(ev, footCover.Value, mass, r1, r2);
            ClipLibrary<AudioClip> source = surface == StepSurface.Custom ? customLibrary : library;
            AudioClip crunch = source.Pick(voice.Class, (int)(rng >> 8));
            float volume = switches.Volume * gain * volumes.Ambience * (listener.MapView ? 0.25f : 1f);
            Vector3 position = vessel != null ? vessel.transform.position : Vector3.zero;
            if (simulated)
            {
                // The simulated walker has no kerbal: the steps sound two metres ahead of the listener.
                AudioListener at = FindListener(now);
                if (at != null) position = at.transform.position + at.transform.forward * 2f;
            }
            objects.PlayStep(crunch, voice, volume, position, ev.LeftFoot, muffling.DetachFromRse ? null : rseRouter);
            stepsPlayed++;
        }

        private float NextRandom()
        {
            rng ^= rng << 13;
            rng ^= rng >> 17;
            rng ^= rng << 5;
            return (rng & 0xFFFFFFu) / 16777216f;
        }

        // Counters for the series: set to zero before a walk.
        internal void ResetStepCounters()
        {
            stepsFound = 0;
            stepsPlayed = 0;
            distanceWalked = 0f;
        }

        // --- Solo ---

        // Mutes every other audio source of the scene (key audioProbeSolo), so that a probe measures our layers alone.
        private void MuteOthers(float now)
        {
            if (now < nextSolo) return;
            nextSolo = now + SoloScanSeconds;
            AudioSource[] all = UnityEngine.Object.FindObjectsOfType<AudioSource>();
            Transform mine = objects != null && objects.Root != null ? objects.Root.transform : null;
            for (int i = 0; i < all.Length; i++)
            {
                AudioSource s = all[i];
                if (s == null || (mine != null && s.transform.IsChildOf(mine))) continue;
                if (!muted.ContainsKey(s)) muted[s] = s.mute;
                s.mute = true;
            }
        }

        private void RestoreOthers()
        {
            foreach (KeyValuePair<AudioSource, bool> pair in muted)
            {
                if (pair.Key != null) pair.Key.mute = pair.Value;
            }
            muted.Clear();
        }
    }
}
