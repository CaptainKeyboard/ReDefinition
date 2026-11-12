using System;
using System.Collections.Generic;
using System.Threading;
using ReDefinition.Atmospheres.Audio;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Lightning in flight (package T07): the schedule of the flashes from the field's convective cells, the bolt, the flash of the cloud, the
    // light on the terrain and the vessels, and the thunder after the distance's delay.
    //
    // Scheduling structure and the gates after EVE's lightning (Atmosphere/RaymarchedClouds/Lightning/Lightning.cs, MIT, Ryan Bray, Warwick
    // Allison, Ghassen Lahmar): spawn only below a warp rate of 5, only where the cloud cover is above 0.1, a bolt as a point light plus a
    // camera-facing mesh with a life of half a second, at most four at once, the thunder delayed by the distance over the speed of sound.
    // The flashes themselves are not EVE's: they come from a lattice of squares, a draw per square and second (LightningSchedule), driven by
    // the generator's cells and levels, a pure function of the field and the time.
    //
    // Rendering statement: the bolt is an additive mesh in layer Local (Bolt shader), no motion vectors, no reactive mask; a flash start
    // does not reset the upscaler's history (the brightness jumps in a few milliseconds; the interaction rows decide). The cloud flash is the point light of the
    // cloud pass (CloudLightSettings.Bolts), the terrain light a pooled Unity point light, at most two. Exposure follows the camera's.
    [AtmospheresModule(71)]
    internal sealed class LightningModule : IAtmospheresModule
    {
        internal const string ShaderName = "ReDefinition/AtmospheresBolt";
        private const int MaxFlashes = 4, MaxLights = 2, MaxSlotsPerFrame = 4;

        private sealed class Flash
        {
            internal FlashEvent Event;
            internal double Duration;
            // The strike point and the light centre of the cloud in the body's local frame, and the bolt's frame.
            internal Vector3 StrikeLocal, CloudLocal;
            internal float DistanceM;
            internal GameObject Bolt;
            internal Mesh Mesh;
            internal MeshRenderer Renderer;
            internal MaterialPropertyBlock Block = new MaterialPropertyBlock();
            internal float Level;
        }

        private sealed class Voice
        {
            internal AudioSource Source;
            internal AudioClip Clip;
            internal float[] Samples;
            internal volatile bool Ready;
            internal ThunderPlan Plan;
            internal float StartAt;
            internal bool Started;
        }

        private readonly LightningParams parameters = new LightningParams();
        private readonly BoltParams boltParams = new BoltParams();
        private readonly LightningScanner scanner;
        private readonly BoltMesh boltMesh = new BoltMesh();
        private readonly List<Flash> flashes = new List<Flash>();
        private readonly List<FlashEvent> events = new List<FlashEvent>();
        private readonly List<Voice> voices = new List<Voice>();
        private readonly List<Light> lights = new List<Light>();
        private FlashLimiter limiter = new FlashLimiter(LightningSwitches.MaxBumpsPerSecondDefault);
        private IList<LightningCell> cells = new List<LightningCell>();
        private Material material;
        private bool shaderTried;
        private long lastSlot = long.MinValue;
        private float scanTimer;
        private int bodyIndex = -1;
        private float visibilityM = 100000f, nextVisibilityAt;
        private bool paused;
        private Transform root;

        public LightningModule()
        {
            scanner = new LightningScanner(parameters);
        }

        // The module of the running game, or null: the cloud pass asks it for the bolts.
        internal static LightningModule Current { get; private set; }

        // The probe and the tests: a source of cells that replaces the generator's, and what the module did (strokes begun, thunders planned,
        // flashes that spawned at a warp rate it should not).
        internal ICellSource ForcedSource;
        internal readonly List<double> StrokeTimes = new List<double>();
        internal readonly List<ThunderRecord> ThunderLog = new List<ThunderRecord>();
        internal int GateViolations;
        private readonly double[] strokeScratch = new double[3];

        internal struct ThunderRecord
        {
            internal double DistanceM, PlannedDelayS;
            internal float Warp;
        }

        // The flashes now alive (bolt, light), for the probe and the log.
        internal int FlashCount { get { return flashes.Count; } }
        internal int Spawned { get; private set; }
        internal int Dropped { get { return limiter.Dropped; } }

        public void Init()
        {
            Current = this;
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            Clear();
            lastSlot = long.MinValue;
            cells = new List<LightningCell>();
            scanTimer = 0f;
        }

        public void Shutdown()
        {
            Clear();
            DestroyRoot();
            if (material != null) UnityEngine.Object.Destroy(material);
            material = null;
            if (ReferenceEquals(Current, this)) Current = null;
        }

        public void Tick(float dt)
        {
            LightningSwitches.Apply(AtmospheresSettings.Number);
            if (!LightningSwitches.Enabled || !HighLogic.LoadedSceneIsFlight || FlightCamera.fetch == null)
            {
                if (flashes.Count > 0 || voices.Count > 0) Clear();
                lastSlot = long.MinValue;
                return;
            }
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null || !body.atmosphere)
            {
                if (flashes.Count > 0 || voices.Count > 0) Clear();
                lastSlot = long.MinValue;
                return;
            }
            GeneratorField gen = GeneratorModule.Current;
            GeneratorState state = gen != null ? gen.StateOf(body.flightGlobalsIndex) : null;
            if (state == null && ForcedSource == null) return;

            double ut = Planetarium.GetUniversalTime();
            // The product with Time.timeScale is EVE's gate; whether physics warp is counted twice by it is [VERIFY] in the game.
            float warp = TimeWarp.CurrentRate * Time.timeScale;
            Vector3d camWorld = FlightCamera.fetch.transform.position;
            double lat = body.GetLatitude(camWorld), lon = body.GetLongitude(camWorld);
            double height = Math.Max(0.0, body.GetAltitude(camWorld));
            if (limiter.MaxBumpsPerSecond != Math.Max(1, Math.Min(LightningSwitches.MaxBumpsPerSecond, 3)))
                limiter = new FlashLimiter(LightningSwitches.MaxBumpsPerSecond);
            bodyIndex = body.flightGlobalsIndex;

            if (Time.realtimeSinceStartup >= nextVisibilityAt)
            {
                nextVisibilityAt = Time.realtimeSinceStartup + 2f;
                WeatherSample ws;
                if (WeatherHub.Field.Sample(bodyIndex, lat, lon, height, ut, out ws)) visibilityM = Mathf.Max(ws.VisibilityM, 500f);
            }
            UpdateScan(state, body, lat, lon, height, ut, dt);
            Schedule(state, body, ut, warp, camWorld);
            UpdateFlashes(body, ut, camWorld);
            UpdateVoices();
        }

        // ---- the scan ---------------------------------------------------------------------------------------------------

        private void UpdateScan(GeneratorState state, CelestialBody body, double lat, double lon, double height, double ut, float dt)
        {
            if (scanner.Running)
            {
                if (scanner.Step(3)) cells = scanner.Result;
                return;
            }
            scanTimer -= dt;
            if (scanTimer > 0f) return;
            scanTimer = LightningSwitches.ScanIntervalS;
            scanner.Begin(ForcedSource ?? new GeneratorCellSource(state), body.Radius, lat, lon, LightningSchedule.ScanRangeM(parameters, height), ut);
        }

        // ---- the schedule -----------------------------------------------------------------------------------------------

        private void Schedule(GeneratorState state, CelestialBody body, double ut, float warp, Vector3d camWorld)
        {
            long now = LightningSchedule.SlotOf(parameters, ut);
            if (lastSlot == long.MinValue || now < lastSlot || now - lastSlot > MaxSlotsPerFrame || warp >= parameters.MaxWarpRate)
            {
                // A first frame, a rewind, a hitch or the warp: nothing is made up for the time that was not watched.
                if (now < lastSlot) limiter.Reset();
                lastSlot = now;
                return;
            }
            if (now == lastSlot) return;
            events.Clear();
            uint seed = (state != null ? (uint)(state.Seed ^ (state.Seed >> 32)) : 0u) ^ 0x4C494754u;
            for (long slot = lastSlot + 1; slot <= now; slot++)
                LightningSchedule.EventsInSlot(parameters, seed, slot, cells, events);
            lastSlot = now;
            events.Sort((a, b) => a.Ut.CompareTo(b.Ut));
            for (int i = 0; i < events.Count; i++)
            {
                FlashEvent e = events[i];
                if (!LightningSchedule.Allows(parameters, warp, CoverageOf(e))) continue;
                if (flashes.Count >= MaxFlashes) break;
                if (!limiter.TryAccept(ref e)) continue;
                Spawn(body, e, ut, camWorld, warp);
            }
        }

        private float CoverageOf(FlashEvent e)
        {
            for (int i = 0; i < cells.Count; i++)
                if (cells[i].Row == e.Row && cells[i].Col == e.Col) return cells[i].Coverage;
            return 0f;
        }

        // ---- a flash ----------------------------------------------------------------------------------------------------

        private void Spawn(CelestialBody body, FlashEvent e, double ut, Vector3d camWorld, float warp)
        {
            var f = new Flash { Event = e };
            f.Duration = FlashEnvelope.DurationS(ref e);
            if (ut - e.Ut > f.Duration) return;
            double ground = Math.Max(0.0, body.TerrainAltitude(e.LatDeg, e.LonDeg, true));
            Vector3d strike = body.GetWorldSurfacePosition(e.LatDeg, e.LonDeg, ground);
            Vector3d cloud = body.GetWorldSurfacePosition(e.LatDeg, e.LonDeg, ground + e.CloudLightHeightM);
            Transform frame = body.bodyTransform;
            f.StrikeLocal = frame.InverseTransformPoint((Vector3)strike);
            f.CloudLocal = frame.InverseTransformPoint((Vector3)cloud);
            Vector3d from = e.Kind == FlashKind.Ground ? strike : cloud;
            f.DistanceM = (float)(from - camWorld).magnitude;
            if (LightningSwitches.Bolts && f.DistanceM < LightningSwitches.BoltRangeM) MakeBolt(f, body, e);
            flashes.Add(f);
            Spawned++;
            if (warp >= parameters.MaxWarpRate) GateViolations++;
            FlashEnvelope.BumpTimes(ref e, strokeScratch);
            for (int k = 0; k < e.Bumps; k++) StrokeTimes.Add(e.Ut + strokeScratch[k]);
            if (StrokeTimes.Count > 4096) StrokeTimes.RemoveRange(0, 2048);
            // No history reset of the upscaler per flash (review of wave 4): up to three strokes per second would cost a frame of unfiltered image each.
            // The flash is a brightness change of the exposure's kind; the DLSS and frame generation interaction rows decide whether a reset is needed.
            if (LightningSwitches.Thunder) MakeThunder(e, f.DistanceM, warp, (Vector3)(from - camWorld), ut - e.Ut);
        }

        private void MakeBolt(Flash f, CelestialBody body, FlashEvent e)
        {
            Material m = Material();
            if (m == null) return;
            EnsureRoot();
            var go = new GameObject("ReDefinition lightning bolt");
            go.layer = 15;
            go.transform.SetParent(body.bodyTransform, false);
            Vector3 up = f.StrikeLocal.normalized;
            List<BoltStrand> strands;
            Quaternion rot;
            Vector3 origin;
            if (e.Kind == FlashKind.Ground)
            {
                var offset = new Vector2((float)(LightningSchedule.Draw(e.Seed, 50) - 0.5) * 2400f, (float)(LightningSchedule.Draw(e.Seed, 51) - 0.5) * 2400f);
                strands = BoltGeometry.Build(e.Seed, e.StartHeightM, offset, boltParams);
                rot = Quaternion.FromToRotation(Vector3.up, up);
                origin = f.StrikeLocal;
            }
            else
            {
                // A discharge inside the cloud: the same channel laid on its side at the light centre, a few km long.
                float length = 2500f + 3500f * (float)LightningSchedule.Draw(e.Seed, 52);
                strands = BoltGeometry.Build(e.Seed, length, Vector2.zero, boltParams);
                Vector3 horizontal = Vector3.Cross(up, Vector3.up).sqrMagnitude > 1e-4f ? Vector3.Cross(up, Vector3.up).normalized : Vector3.right;
                horizontal = Quaternion.AngleAxis((float)LightningSchedule.Draw(e.Seed, 53) * 360f, up) * horizontal;
                rot = Quaternion.FromToRotation(Vector3.up, horizontal);
                origin = f.CloudLocal - horizontal * (length * 0.5f);
            }
            f.Mesh = new Mesh { name = "ReDefinition bolt" };
            f.Mesh.MarkDynamic();
            boltMesh.Build(f.Mesh, strands, rot, Vector3.zero, e.Kind == FlashKind.Ground ? 1f : 0.55f);
            go.transform.localPosition = origin;
            go.AddComponent<MeshFilter>().sharedMesh = f.Mesh;
            f.Renderer = go.AddComponent<MeshRenderer>();
            f.Renderer.sharedMaterial = m;
            f.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            f.Renderer.receiveShadows = false;
            f.Bolt = go;
        }

        private Material Material()
        {
            if (material != null) return material;
            if (shaderTried) return null;
            shaderTried = true;
            string reason;
            Shader shader = AtmosphereShaders.Get(ShaderName, out reason);
            if (shader == null)
            {
                AtmospheresLog.Say("lightning: no bolt shader (" + reason + "), flashes light the clouds and the ground only");
                return null;
            }
            material = new Material(shader) { name = "ReDefinition bolt" };
            material.renderQueue = 3020;
            return material;
        }

        private void UpdateFlashes(CelestialBody body, double ut, Vector3d camWorld)
        {
            float visibility = visibilityM;
            for (int i = flashes.Count - 1; i >= 0; i--)
            {
                Flash f = flashes[i];
                double age = ut - f.Event.Ut;
                if (age > f.Duration)
                {
                    DestroyFlash(f);
                    flashes.RemoveAt(i);
                    continue;
                }
                f.Level = (float)FlashEnvelope.Level(ref f.Event, age) * Mathf.Clamp01(LightningSwitches.Brightness);
                if (f.Renderer != null)
                {
                    f.Block.SetFloat("_BoltFade", f.Level);
                    f.Block.SetFloat("_BoltHaze", 3.912f / Mathf.Max(visibility, 2000f));
                    f.Renderer.SetPropertyBlock(f.Block);
                }
            }
            UpdateLights(body);
        }

        private void UpdateLights(CelestialBody body)
        {
            if (!LightningSwitches.TerrainLight)
            {
                for (int i = 0; i < lights.Count; i++) lights[i].enabled = false;
                return;
            }
            // The two flashes that are brightest at the camera get a light.
            Flash a = null, b = null;
            float sa = 0f, sb = 0f;
            for (int i = 0; i < flashes.Count; i++)
            {
                Flash f = flashes[i];
                float reach = LightningSwitches.TerrainLightRangeM + 2000f;
                if (f.DistanceM > reach) continue;
                float score = f.Level / (1f + f.DistanceM * f.DistanceM / (4000f * 4000f));
                if (score > sa) { b = a; sb = sa; a = f; sa = score; }
                else if (score > sb) { b = f; sb = score; }
            }
            SetLight(0, a, body);
            SetLight(1, b, body);
        }

        private void SetLight(int index, Flash f, CelestialBody body)
        {
            while (lights.Count <= index)
            {
                EnsureRoot();
                var go = new GameObject("ReDefinition lightning light");
                go.layer = 15;
                go.transform.SetParent(root, false);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.shadows = LightShadows.None;
                l.cullingMask = (1 << 15) | (1 << 0) | (1 << 16) | (1 << 17);
                l.enabled = false;
                lights.Add(l);
            }
            Light light = lights[index];
            if (f == null || f.Level <= 0.001f)
            {
                light.enabled = false;
                return;
            }
            Vector3 local = f.Event.Kind == FlashKind.Ground ? f.StrikeLocal + f.StrikeLocal.normalized * 600f : f.CloudLocal;
            light.transform.position = body.bodyTransform.TransformPoint(local);
            light.color = new Color(0.82f, 0.88f, 1f);
            light.range = LightningSwitches.TerrainLightRangeM;
            light.intensity = LightningSwitches.TerrainLightPeak * f.Level * (f.Event.Kind == FlashKind.Ground ? 1f : 0.45f);
            light.enabled = true;
        }

        // The flashes as the cloud pass's lights: fills the settings' bolts, in the coordinates of the frame (the world's). Returns the count.
        // peakScale: the sun's brightness in the frame's units divided by the units the peak is given in (1 where the frame uses the harness's).
        internal int FillBolts(CloudLightSettings settings, CelestialBody body, float peakScale)
        {
            int n = 0;
            if (!LightningSwitches.Enabled || !LightningSwitches.CloudFlash || body == null)
            {
                settings.BoltCount = 0;
                return 0;
            }
            for (int i = 0; i < flashes.Count && n < CloudLightSettings.MaxBolts; i++)
            {
                Flash f = flashes[i];
                if (f.Level <= 0.001f) continue;
                Vector3 center = f.Event.Kind == FlashKind.Ground ? Vector3.Lerp(f.StrikeLocal, f.CloudLocal, 0.8f) : f.CloudLocal;
                float k = f.Level * LightningSwitches.CloudPeak * peakScale * Mathf.Clamp01(LightningSwitches.Brightness) * (f.Event.Kind == FlashKind.Ground ? 1f : 0.8f);
                settings.Bolts[n] = new CloudBolt
                {
                    Position = body.bodyTransform.TransformPoint(center),
                    RangeM = FlashRangeM(f.Event),
                    Colour = new Color(0.82f * k, 0.88f * k, 1f * k)
                };
                n++;
            }
            settings.BoltCount = n;
            return n;
        }

        // The range in m at which a cloud flash has fallen to half: a share of the cell's height above a floor.
        internal static float FlashRangeM(FlashEvent e)
        {
            return 600f + 0.15f * Mathf.Max(e.TopM, 500f);
        }

        // ---- the thunder ------------------------------------------------------------------------------------------------

        private void MakeThunder(FlashEvent e, float distanceM, float warp, Vector3 toFlash, double ageS)
        {
            float ambience = Mathf.Clamp01(GameSettings.AMBIENCE_VOLUME);
            ListenerState listener = Listener();
            int active = 0;
            for (int i = 0; i < voices.Count; i++) active++;
            ThunderPlan plan = ThunderPolicy.Plan(distanceM, listener, true, warp, ambience, active, LightningSwitches.ThunderSettings);
            if (!plan.Play) return;
            ThunderLog.Add(new ThunderRecord { DistanceM = distanceM, PlannedDelayS = plan.DelayS * Math.Max(1f, warp), Warp = warp });
            if (ThunderLog.Count > 256) ThunderLog.RemoveRange(0, 128);
            EnsureRoot();
            // The flash began up to a slot ago: that time has already passed on the way of the sound.
            double age = Math.Max(0.0, ageS) / Math.Max(1f, warp);
            var v = new Voice { Plan = plan, StartAt = Time.realtimeSinceStartup + (float)Math.Max(0.0, plan.DelayS - age) };
            var go = new GameObject("ReDefinition thunder");
            go.transform.SetParent(root, false);
            go.transform.position = FlightCamera.fetch.transform.position;
            var src = go.AddComponent<AudioSource>();
            // Thunder is a sound of the whole sky: a 2D source, placed left to right by the direction of the flash.
            src.spatialBlend = 0f;
            Transform cam = FlightCamera.fetch.transform;
            src.panStereo = DistantCellModel.Pan(toFlash, cam.right, cam.up) * 0.7f;
            src.playOnAwake = false;
            src.loop = false;
            src.volume = plan.Gain;
            v.Source = src;
            voices.Add(v);
            uint seed = e.Seed;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    v.Samples = ThunderSynth.Render(seed, plan);
                    v.Ready = true;
                }
                catch (Exception)
                {
                    v.Samples = null;
                    v.Ready = true;
                }
            });
        }

        private static ListenerState Listener()
        {
            CameraManager.CameraMode mode = CameraManager.Instance != null ? CameraManager.Instance.currentCameraMode : CameraManager.CameraMode.Flight;
            bool cabin = mode == CameraManager.CameraMode.IVA || mode == CameraManager.CameraMode.Internal;
            Vessel vessel = GameGlobals.ActiveVessel;
            bool eva = vessel != null && vessel.isEVA;
            return ListenerModel.From(cabin, MapView.MapIsEnabled || mode == CameraManager.CameraMode.Map, eva);
        }

        private void UpdateVoices()
        {
            bool pause = FlightDriver.Pause || (PauseMenu.exists && PauseMenu.isOpen);
            for (int i = voices.Count - 1; i >= 0; i--)
            {
                Voice v = voices[i];
                if (v.Source == null) { voices.RemoveAt(i); continue; }
                if (!v.Started)
                {
                    if (!v.Ready) continue;
                    if (v.Samples == null) { DestroyVoice(v); voices.RemoveAt(i); continue; }
                    v.Clip = AudioClip.Create("ReDefinition thunder", v.Samples.Length, 1, ThunderSynth.SampleRate, false);
                    v.Clip.SetData(v.Samples, 0);
                    v.Source.clip = v.Clip;
                    float remaining = Mathf.Max(0f, v.StartAt - Time.realtimeSinceStartup);
                    v.Source.PlayDelayed(remaining);
                    v.Started = true;
                    v.Samples = null;
                    continue;
                }
                if (pause != paused)
                {
                    if (pause) v.Source.Pause(); else v.Source.UnPause();
                }
                bool waiting = Time.realtimeSinceStartup < v.StartAt;
                if (!v.Source.isPlaying && !waiting && !pause)
                {
                    DestroyVoice(v);
                    voices.RemoveAt(i);
                }
            }
            paused = pause;
        }

        // ---- cleanup ----------------------------------------------------------------------------------------------------

        private void EnsureRoot()
        {
            if (root != null) return;
            var go = new GameObject("ReDefinition lightning");
            UnityEngine.Object.DontDestroyOnLoad(go);
            root = go.transform;
        }

        private void DestroyRoot()
        {
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            root = null;
            lights.Clear();
        }

        private void DestroyFlash(Flash f)
        {
            if (f.Bolt != null) UnityEngine.Object.Destroy(f.Bolt);
            if (f.Mesh != null) UnityEngine.Object.Destroy(f.Mesh);
            f.Bolt = null;
            f.Mesh = null;
        }

        private static void DestroyVoice(Voice v)
        {
            if (v.Source != null) UnityEngine.Object.Destroy(v.Source.gameObject);
            if (v.Clip != null) UnityEngine.Object.Destroy(v.Clip);
            v.Source = null;
            v.Clip = null;
        }

        private void Clear()
        {
            for (int i = 0; i < flashes.Count; i++) DestroyFlash(flashes[i]);
            flashes.Clear();
            for (int i = 0; i < voices.Count; i++) DestroyVoice(voices[i]);
            voices.Clear();
            for (int i = 0; i < lights.Count; i++) if (lights[i] != null) lights[i].enabled = false;
        }
    }
}
