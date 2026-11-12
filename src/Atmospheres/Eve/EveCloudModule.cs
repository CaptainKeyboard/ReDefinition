using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The numbers of the painter for the log and the series rows (tests/series/rows/C23.json): read by the probe, never by the game.
    internal static class EveCloudStats
    {
        internal static int Paints;
        internal static double LastPaintMs, WorstPaintMs, MeanPaintMs;
        internal static int Losses, Rebinds, WatchdogFinds, Reasserts;
        internal static double MaxRebindLatencyS;
        internal static int LayersBound;
        internal static double MirrorWorstFrameMs;
        internal static long MirrorVersion;
        internal static string Layers = "";
    }

    // The cloud-map painter of the atmospheres (brief C23): ties the GPU painter, the binding to EVE's layers, the CPU mirror, the ground
    // effects, the 2D picture and the own shell to the weather field. Runs while the mod and weatherEffects are on and the active body
    // has weather; for cloudSource Eve and Both it drives EVE, for Ours it draws the own shell, for None it does nothing.
    //
    // Per frame: the binding is observed (an event of EVE's, the identity of the materials and of the paint set, the keyword sentinel
    // and the watchdog's re-assertion every second; nothing is read back from a texture property, EVE's shader declares none) and made
    // again where it is gone; the mirror works through a band of rows. Every PaintInterval (0.25 s) the field's maps are fetched, uploaded
    // and painted with the slew limit. The ground effects run in Camera.onPreCull, after EVE's Update.
    //
    // Where EVE cannot render (EveRenderCompat: EVE with volumetric layers and without Scatterer, or the invalid-pass error counted in the
    // log) cloudSource Eve and Both are treated as Ours: nothing is bound to EVE's layers and the own shell carries the picture. EVE's
    // own renderer is not stopped by this.
    //
    // Layer wind (package E43): after the drift is frozen (FreezeDrift) each bound layer is steered to the wind of its height and its
    // upwardsCloudSpeed follows the updraft (LayerWindSteering, mode LayerWindOptions.Steering). Not done (brief item 6, runtime
    // steering): scaledFade*, layer colour, per-type densities, RebuildCurvesFromConfig and SetTimeFade.
    [AtmospheresModule(60)]
    internal sealed class EveCloudModule : IAtmospheresModule
    {
        private const float ScanIntervalS = 3f;
        private const float OrbitIntervalFarS = 2f, OrbitIntervalNearS = 10f;
        private const double LiveBelowM = 30000;
        private const float RenderCheckIntervalS = 1f;

        private MapPainter painter;
        private readonly EveBinding binding = new EveBinding();
        private readonly CpuMirror mirror = new CpuMirror();
        private readonly EveGroundEffects ground = new EveGroundEffects();
        private OwnShell shell;
        private List<EveLayer> layers = new List<EveLayer>();
        private readonly Dictionary<PaintSet, byte[]> luts = new Dictionary<PaintSet, byte[]>();
        private readonly Dictionary<EveLayer, LayerSteerState> steering = new Dictionary<EveLayer, LayerSteerState>();
        // The layer maps of the paint sets of MapsOnly (CPU copies for the mirror) and the sets painted from one in the current paint.
        private readonly Dictionary<PaintSet, WeatherMap> layerMaps = new Dictionary<PaintSet, WeatherMap>();
        private readonly HashSet<PaintSet> layerSetsInUse = new HashSet<PaintSet>();
        private readonly List<EveCoverageMath.Blob> cells = new List<EveCoverageMath.Blob>();
        private readonly List<EveCoverageMath.Rect> painted = new List<EveCoverageMath.Rect>();
        private byte[] overlay;
        private readonly List<object> paintedObjects = new List<object>();

        private CelestialBody body;
        private BodyClimate climate;
        private bool applyEventSubscribed, applyFlag, preCullSubscribed, shaderFailedLogged, wasActive;
        private float nextScan, nextPaint, nextOrbit, lastPaint = -1f, nextStats, nextRenderCheck;
        private bool mirrorNew;
        private double paintMsSum;
        private int paintCount;

        // The running module, for the series probe (Probe/C23Probe.cs); null while the module is not running.
        internal static EveCloudModule Current { get; private set; }

        // True while this module drives EVE's rain and snow entries (it holds layers of the body): the old storm effect writes the same
        // static entries (EveRainLink, EveSnowLink) and must leave them alone then. The old path goes with C25.
        internal static bool DrivesEveGroundEffects
        {
            get { return Current != null && Current.layers.Count > 0 && Current.preCullSubscribed; }
        }

        public void Init()
        {
            Services.EveFieldSuppressor = ground;
            Current = this;
        }

        // --- For the series probe ------------------------------------------------------------------------------------

        internal EveLayer FirstBoundLayer()
        {
            foreach (EveLayer h in layers)
            {
                if (h.Tracker.State == BindState.Bound) return h;
            }
            return null;
        }

        // The number of paint sets painted from a layer map in the last paint (E43, layerWindSteering MapsOnly).
        internal int LayerSetsInUse
        {
            get { return layerSetsInUse.Count; }
        }

        // The steering state of a layer (E43), or null.
        internal LayerSteerState SteeringOf(EveLayer h)
        {
            LayerSteerState st;
            return steering.TryGetValue(h, out st) ? st : null;
        }

        // The 2D cloud shell (null before the painter exists): the own cloud pass sets its NearMarchActive (package T09).
        internal OwnShell Shell
        {
            get { return shell; }
        }

        internal IList<EveLayer> CurrentLayers
        {
            get { return layers; }
        }

        internal CelestialBody CurrentBody
        {
            get { return body; }
        }

        // The painted coverage of the layer's set read back from the GPU, the mirror's coverage map of the same set, and the set. False
        // where there is no painter or the layer has no set yet. A readback waits for the GPU: for a probe, never for the frame path.
        internal bool ReadPaintedAndMirror(EveLayer layer, out byte[] painted, out byte[] mirrored)
        {
            painted = null;
            mirrored = null;
            if (painter == null || layer == null || !layer.LayoutKnown) return false;
            PaintSet set = painter.SetFor(layer.ShiftDeg, layer.Mirrored);
            if (!set.Primed) return false;
            painted = MapPainter.ReadBackRed(set.Coverage);
            return mirror.TryGetMaps(set, out mirrored, out _);
        }

        // The coverage the painter is told to paint everywhere instead of the field's (null: the field's own). For the series probe's
        // render proof only; set through ForceCoverage so that the next paint jumps.
        private float? debugCoverage;
        private bool debugSnapPending;

        internal void ForceCoverage(float? value)
        {
            debugCoverage = value;
            debugSnapPending = value.HasValue;
            nextPaint = 0f;
        }

        internal bool CoverageForced
        {
            get { return debugCoverage.HasValue; }
        }

        // The paint set a layer is bound to (null while its layout is unknown or there is no painter).
        internal PaintSet SetOfLayer(EveLayer layer)
        {
            return painter != null && layer != null && layer.LayoutKnown ? painter.SetFor(layer.ShiftDeg, layer.Mirrored) : null;
        }

        internal int PaintWidth { get { return painter != null ? painter.Width : 0; } }
        internal int PaintHeight { get { return painter != null ? painter.Height : 0; } }

        // Has EVE build the layer again (what the GUI and EVE's managers do), for the forced-apply rows.
        internal static bool ForceApply(EveLayer layer)
        {
            if (layer == null || layer.Object == null) return false;
            System.Reflection.MethodInfo apply = layer.Object.GetType().GetMethod("Apply", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (apply == null) return false;
            apply.Invoke(layer.Object, null);
            return true;
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            ReleaseEve("body changed");
            if (shell != null) shell.Hide();
            body = null;
            climate = null;
        }

        public void Shutdown()
        {
            Teardown("shutdown");
            if (shell != null)
            {
                OrbitOverlayLayers.Unregister(shell);
                shell.Destroy();
                shell = null;
            }
            if (preCullSubscribed)
            {
                Camera.onPreCull -= OnPreCull;
                preCullSubscribed = false;
            }
            if (ReferenceEquals(Services.EveFieldSuppressor, ground)) Services.EveFieldSuppressor = null;
            EveRenderCompat.Shutdown();
            if (ReferenceEquals(Current, this)) Current = null;
        }

        public void Tick(float dt)
        {
            // The look at EVE's renderer runs before any early return, so that the combination EVE without Scatterer is reported at the
            // first tick whatever the body or the switches are.
            float checkNow = Time.realtimeSinceStartup;
            if (checkNow >= nextRenderCheck)
            {
                nextRenderCheck = checkNow + RenderCheckIntervalS;
                EveRenderCompat.Update(EveLayerAccess.Available);
            }
            bool on = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects;
            CelestialBody b = BodyWatcher.ActiveBody;
            BodyClimate c = b != null ? Bodies.Get(b) : null;
            if (!on || b == null || c == null || !c.Status.WeatherRuns || !b.atmosphere)
            {
                if (wasActive) Teardown(on ? "no weather on the body" : "switched off");
                wasActive = false;
                return;
            }
            wasActive = true;
            body = b;
            climate = c;
            CloudSourceKind configured = c.Clouds.Source ?? AtmospheresSettings.CloudSource;
            CloudSourceKind source = EveRenderCompat.EffectiveSource(configured);
            bool eveMode, ownMode;
            CloudModeModel.Decide(source, EveLayerAccess.Available, out eveMode, out ownMode);
            if (!eveMode)
            {
                if (layers.Count > 0)
                {
                    ReleaseEve(source != configured ? "EVE cannot render here (" + EveRenderCompat.Reason + ")" : "cloudSource is " + source);
                }
            }
            if (!eveMode && !ownMode)
            {
                if (shell != null) shell.Hide();
                return;
            }
            if (!EnsurePainter()) return;
            float now = Time.realtimeSinceStartup;
            if (eveMode)
            {
                if (!applyEventSubscribed)
                {
                    applyEventSubscribed = EveLayerAccess.SubscribeApply(OnEveApply);
                    if (!applyEventSubscribed) applyEventSubscribed = true; // the watchdog covers a missing event; do not try again
                }
                if (!preCullSubscribed)
                {
                    Camera.onPreCull += OnPreCull;
                    preCullSubscribed = true;
                }
                RunEve(now, c, b);
            }
            else
            {
                FetchForShell(now, b);
            }
            RunPictures(now, c, b, eveMode, ownMode);
            PublishStats(now);
        }

        // --- EVE --------------------------------------------------------------------------------------------------

        private void RunEve(float now, BodyClimate c, CelestialBody b)
        {
            double ut = Planetarium.GetUniversalTime();
            if (applyFlag || now >= nextScan)
            {
                nextScan = now + ScanIntervalS;
                int cap = c.Clouds.EveLayerCap > 0 ? c.Clouds.EveLayerCap : AtmospheresSettings.EveLayerCap;
                List<EveLayer> found = EveLayerAccess.Scan(b.name, c.Clouds, cap, layers);
                foreach (EveLayer h in found)
                {
                    if (!layers.Contains(h))
                    {
                        EveLayerAccess.AssignSlots(h, c.Clouds);
                        AtmospheresLog.Say("cloud painter: layer '" + h.Name + "' on " + b.name + ", types [" + string.Join(", ", h.TypeNames)
                            + "], roles light " + h.Slots.IndexOf(CloudRole.SnowLight) + " stratus " + h.Slots.IndexOf(CloudRole.SnowStratus)
                            + " storm " + h.Slots.IndexOf(CloudRole.Storm) + (h.LeftReason.Length > 0 ? ", left to EVE (" + h.LeftReason + ")" : ""));
                    }
                    else
                    {
                        EveLayerAccess.AssignSlots(h, c.Clouds);
                    }
                }
                layers = found;
                binding.SetLayers(layers);
            }
            foreach (EveLayer h in layers)
            {
                if (h.Tracker.State == BindState.Left || h.LeftReason.Length > 0) continue;
                if (!h.DriftFrozen) h.DriftFrozen = EveLayerAccess.FreezeDrift(h, ut);
                if (!h.LayoutKnown && h.DriftFrozen && Live(b) && now - h.CalibratedAt > 1.0)
                {
                    h.CalibratedAt = now;
                    double worst;
                    bool ok = EveLayerAccess.Calibrate(h, b, out worst);
                    if (ok)
                    {
                        AtmospheresLog.Say("cloud painter: layer '" + h.Name + "' lies at shift " + h.ShiftDeg.ToString("0.00") + " deg"
                            + (h.Mirrored ? ", mirrored" : "") + " (fit " + worst.ToString("0.00") + " deg)");
                    }
                    else
                    {
                        h.LeftReason = "map layout does not fit the body (" + worst.ToString("0.0") + " deg)";
                        AtmospheresLog.Say("cloud painter: layer '" + h.Name + "' is left to EVE: " + h.LeftReason);
                    }
                }
            }
            // E43: each bound layer gets the wind of its height (option A, LayerWindSteering); a no-op in the modes Off and MapsOnly
            foreach (EveLayer h in layers)
            {
                if (h.Tracker.State != BindState.Bound || h.LeftReason.Length > 0) continue;
                LayerSteerState st;
                if (!steering.TryGetValue(h, out st))
                {
                    st = new LayerSteerState();
                    steering[h] = st;
                }
                LayerWindSteering.Update(h, st, b, ut, now);
            }
            if (now >= nextPaint) Paint(now, b, ut);
            binding.WatchdogIntervalS = Tuning.Get("eve.watchdogS", (float)BindingTracker.WatchdogIntervalS);
            int bound = binding.Tick(now, Choose, text => AtmospheresLog.Say("cloud painter: " + text));
            // The 2D layer's rendering material is a new object after EVE's Apply: the picture is set on it at once, not at the next
            // repaint (the picture cannot be read back from the material either, so it is set whenever the layer was touched).
            if (applyFlag || bound > 0) nextOrbit = 0f;
            applyFlag = false;
            paintedObjects.Clear();
            foreach (EveLayer h in layers)
            {
                if (h.Tracker.State == BindState.Bound) paintedObjects.Add(h.Object);
            }
            EveRainLink.SetPainted(paintedObjects);
            foreach (EveLayer h in layers)
            {
                if (h.Tracker.State != BindState.Bound) continue;
                PaintSet set = Choose(h);
                if (set != null && (!h.MirrorInjected || bound > 0)) mirror.Inject(h, set);
            }
            bool snap = mirrorNew;
            mirrorNew = false;
            if (mirror.Tick(now, painter.CoverageMap, painter.LevelMap, Tuning.Get("eve.slewRate", MapSlew.DefaultRate), snap, LayerMapOf))
            {
                string first = FirstBoundName();
                if (first.Length > 0) CloudMapsState.Bump(first);
            }
        }

        private string FirstBoundName()
        {
            foreach (EveLayer h in layers)
            {
                if (h.Tracker.State == BindState.Bound) return h.Name;
            }
            return "";
        }

        private static bool Live(CelestialBody b)
        {
            if (MapView.MapIsEnabled) return false;
            Vessel v = GameGlobals.ActiveVessel;
            return v != null && v.mainBody == b && v.altitude < LiveBelowM;
        }

        // The paint set a layer is bound to, or null while its layout is unknown or nothing is painted into it yet.
        private PaintSet Choose(EveLayer h)
        {
            if (!h.LayoutKnown || painter == null) return null;
            int key = LayerKeyOf(h);
            PaintSet set = painter.SetFor(h.ShiftDeg, h.Mirrored, key);
            // until the layer's own map is painted the layer shows the shared one (no gap, no jump back to EVE's own picture)
            if (key != 0 && !set.Primed) set = painter.SetFor(h.ShiftDeg, h.Mirrored, 0);
            if (!luts.ContainsKey(set))
            {
                byte[] lut = CloudTypeEncoding.BuildLut(h.Slots, false);
                luts[set] = lut;
                mirror.Ensure(set, lut);
                mirrorNew = true;
            }
            return set.Primed ? set : null;
        }

        // Option B of the layer wind (layerWindSteering MapsOnly): the layers of one height share a paint set of their own that paints the field's layer map of that
        // height (GeneratorField.GetLayerCoverage), so the painted pattern carries the wind of the height while EVE's drift stays stopped. 0 (the shared map) in every
        // other mode, where the generator is not ours, and where the layer's height is not known.
        private int LayerKeyOf(EveLayer h)
        {
            if (LayerWindOptions.Steering != LayerSteeringMode.MapsOnly || body == null || !h.DriftFrozen || GeneratorModule.Current == null) return 0;
            double height = LayerWindSteering.LayerMidHeightM(h.InnerRadiusM, h.OuterRadiusM, body.Radius);
            if (height < 100.0) return 0;
            return 1 + (int)Math.Round(height / GeneratorMaps.LayerStepM);
        }

        private WeatherMap LayerMapOf(PaintSet set)
        {
            WeatherMap m;
            return layerMaps.TryGetValue(set, out m) ? m : null;
        }

        // Fetches and uploads the layer map of every layer set in use; the sets whose map is not there yet are left unpainted (Choose returns null for them until
        // they are primed). Returns the sets painted from a layer map in this paint.
        private void UploadLayerMaps(CelestialBody b, double ut)
        {
            layerSetsInUse.Clear();
            GeneratorField gf = GeneratorModule.Current;
            if (gf == null) return;
            foreach (EveLayer h in layers)
            {
                if (!h.LayoutKnown || h.LeftReason.Length > 0) continue;
                int key = LayerKeyOf(h);
                if (key == 0) continue;
                PaintSet set = painter.SetFor(h.ShiftDeg, h.Mirrored, key);
                if (layerSetsInUse.Contains(set)) continue;
                WeatherMap map;
                if (!layerMaps.TryGetValue(set, out map))
                {
                    map = new WeatherMap();
                    layerMaps[set] = map;
                }
                if (!gf.GetLayerCoverage(b.flightGlobalsIndex, ut, (key - 1) * GeneratorMaps.LayerStepM, map)) continue;
                painter.UploadLayer(set, map);
                layerSetsInUse.Add(set);
            }
        }

        private void Paint(float now, CelestialBody b, double ut)
        {
            float interval = Tuning.Get("eve.paintIntervalS", MapSlew.DefaultIntervalS);
            float rate = Tuning.Get("eve.slewRate", MapSlew.DefaultRate);
            if (!MapSlew.Quantises(rate, interval)) interval = Math.Max(interval, MapSlew.MinLevelsPerUpdate / (255f * rate));
            nextPaint = now + interval;
            IWeatherField field = WeatherHub.Field;
            if (field is NullField) return;
            if (!field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Coverage, ut, painter.CoverageMap)) return;
            if (!field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Precipitation, ut, painter.LevelMap)) return;
            float dt = lastPaint < 0f ? interval : now - lastPaint;
            lastPaint = now;
            // The probe's render proof: a uniform coverage, jumped to at once (the slew would take 12 s from 0 to 1 at the default rate).
            bool snap = false;
            if (debugCoverage.HasValue)
            {
                float[] values = painter.CoverageMap.Values;
                for (int i = 0; i < values.Length; i++) values[i] = debugCoverage.Value;
                snap = debugSnapPending;
                debugSnapPending = false;
            }
            painter.Upload(painter.CoverageMap, painter.LevelMap);
            UploadLayerMaps(b, ut);
            double ms = 0;
            foreach (EveLayer h in layers)
            {
                if (!h.LayoutKnown || h.LeftReason.Length > 0) continue;
                painter.SetFor(h.ShiftDeg, h.Mirrored, LayerKeyOf(h));
            }
            foreach (PaintSet set in painter.Sets)
            {
                // a layer set paints only while its layer map is there and wanted (the mode MapsOnly)
                if (set.LayerKey != 0 && !layerSetsInUse.Contains(set)) continue;
                byte[] lut;
                if (luts.TryGetValue(set, out lut)) painter.SetLut(lut);
                ms += painter.Paint(set, dt, rate, snap);
            }
            if (ms > 0)
            {
                paintCount++;
                paintMsSum += ms;
                EveCloudStats.Paints = paintCount;
                EveCloudStats.LastPaintMs = ms;
                if (ms > EveCloudStats.WorstPaintMs) EveCloudStats.WorstPaintMs = ms;
                EveCloudStats.MeanPaintMs = paintMsSum / paintCount;
            }
        }

        // Without EVE the shell is the only reader of the maps: they are fetched and uploaded once a second.
        private void FetchForShell(float now, CelestialBody b)
        {
            if (now < nextPaint) return;
            nextPaint = now + 1f;
            IWeatherField field = WeatherHub.Field;
            if (field is NullField) return;
            double ut = Planetarium.GetUniversalTime();
            if (!field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Coverage, ut, painter.CoverageMap)) return;
            if (!field.GetMap(b.flightGlobalsIndex, WeatherMapKind.Precipitation, ut, painter.LevelMap)) return;
            painter.Upload(painter.CoverageMap, painter.LevelMap);
        }

        private void OnEveApply()
        {
            applyFlag = true;
            binding.OnApply();
        }

        // --- Ground effects ------------------------------------------------------------------------------------------

        private void OnPreCull(Camera cam)
        {
            try
            {
                if (cam == null || layers.Count == 0 || body == null || climate == null || FlightCamera.fetch == null) return;
                if (!ReferenceEquals(cam, FlightCamera.fetch.mainCamera)) return;
                if (EveRainLinkAvailable() == false) return;
                double ut = Planetarium.GetUniversalTime();
                Vector3d position = cam.transform.position;
                WeatherSample sample;
                if (!WeatherHub.Sample(body.flightGlobalsIndex, body.GetLatitude(position), body.GetLongitude(position),
                        body.GetAltitude(position), ut, out sample)) sample = WeatherSample.Clear(body.flightGlobalsIndex);
                float centre, width;
                PrecipTypeModel.Parameters(climate.Precip, out centre, out width);
                bool both = EveRenderCompat.EffectiveSource(climate.Clouds.Source ?? AtmospheresSettings.CloudSource) == CloudSourceKind.Both
                    && AtmospheresSettings.UseEveWeather;
                GroundEffectMath.Factors f = EveGroundEffects.FactorsFor(sample, EveRainLink.UnpaintedCoverage, EveRainLink.UnpaintedThunder, both, centre, width);
                float level = Math.Max(sample.Level01, GroundEffectMath.EveLevel(EveRainLink.UnpaintedCoverage, EveRainLink.UnpaintedThunder, true) * (both ? 1f : 0f));
                ground.Apply(body.name, f, level);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-eve-ground", "the EVE ground effects failed", e);
            }
        }

        private static bool EveRainLinkAvailable()
        {
            return EveRainLink.Controllable || EveSnowLink.Controllable;
        }

        // --- Pictures ------------------------------------------------------------------------------------------------

        private bool ShellWanted(CelestialBody b)
        {
            if (climate == null || !ReferenceEquals(b, body)) return false;
            CloudSourceKind source = EveRenderCompat.EffectiveSource(climate.Clouds.Source ?? AtmospheresSettings.CloudSource);
            // EVE carries the picture where one of its layers has a 2D layer it does not leave alone; the shell stands in where none has.
            bool eveCarries = false;
            foreach (EveLayer h in layers)
            {
                if (h.Layer2D != null && h.LeftReason.Length == 0) eveCarries = true;
            }
            return CloudModeModel.ShellWanted(source, eveCarries);
        }

        private void RunPictures(float now, BodyClimate c, CelestialBody b, bool eveMode, bool ownMode)
        {
            Vessel flying = GameGlobals.ActiveVessel;
            bool far = MapView.MapIsEnabled || (flying != null && flying.altitude > 100000);
            bool shellShown = false;
            if (shell != null)
            {
                bool wanted = ShellWanted(b);
                if (wanted && far)
                {
                    shell.Update(b, true);
                    // Visible once the picture exists; until then it is painted as soon as the shell knows its layout.
                    shellShown = shell.Visible || (shell.Calibrated && painter.Orbit(MapPainter.OrbitShell) == null);
                }
                else
                {
                    shell.Hide();
                }
            }
            bool needEvePicture = false;
            foreach (EveLayer h in layers)
            {
                if (h.Layer2D != null && h.Tracker.State == BindState.Bound) needEvePicture = true;
            }
            if (now < nextOrbit || (!needEvePicture && !shellShown)) return;
            nextOrbit = now + (far ? OrbitIntervalFarS : OrbitIntervalNearS);
            if (painter.LevelMap.Values.Length == 0) return;
            if (needEvePicture)
            {
                EveLayer reference = null;
                foreach (EveLayer h in layers)
                {
                    if (h.Layer2D != null && h.Tracker.State == BindState.Bound && h.LayoutKnown) { reference = h; break; }
                }
                if (reference != null)
                {
                    PaintOrbitPicture(MapPainter.OrbitEve, b, reference.ShiftDeg, reference.Mirrored, OrbitPicture.EveGreyScale);
                    foreach (EveLayer h in layers)
                    {
                        if (h.Layer2D == null || h.Tracker.State != BindState.Bound || !h.LayoutKnown) continue;
                        EveLayerAccess.Bind2D(h, painter.Orbit(MapPainter.OrbitEve));
                    }
                }
            }
            if (shellShown) PaintOrbitPicture(MapPainter.OrbitShell, b, shell.ShiftDeg, shell.Mirrored, 1f);
        }

        private void PaintOrbitPicture(int slot, CelestialBody b, double shiftDeg, bool mirrored, float greyScale)
        {
            if (overlay == null)
            {
                overlay = new byte[EveSwirlMath.Width * EveSwirlMath.Height * 4];
            }
            for (int i = 0; i < painted.Count; i++) EveSwirlMath.Clear(overlay, painted[i]);
            painted.Clear();
            OrbitPicture.FindCells(painter.LevelMap.Values, painter.CoverageMap.Values, painter.LevelMap.Width, painter.LevelMap.Height,
                b.Radius, shiftDeg, mirrored, cells);
            for (int i = 0; i < cells.Count; i++) painted.Add(EveSwirlMath.Paint(overlay, cells[i], b.Radius, 0.0));
            painter.PaintOrbit(slot, shiftDeg, mirrored, overlay, greyScale);
        }

        // --- Life ----------------------------------------------------------------------------------------------------

        private bool EnsurePainter()
        {
            if (painter != null) return true;
            string error;
            Shader shader = AtmosphereShaders.Get("ReDefinition/AtmospheresMapPaint", out error);
            if (shader == null)
            {
                if (!shaderFailedLogged)
                {
                    shaderFailedLogged = true;
                    AtmospheresLog.Say("cloud painter: no shader (" + error + "); the clouds stay as EVE has them");
                }
                return false;
            }
            painter = new MapPainter(shader);
            if (shell == null)
            {
                Shader shellShader = AtmosphereShaders.Get(OwnShell.ShaderName, out error);
                if (shellShader != null)
                {
                    shell = new OwnShell(shellShader, ShellWanted, () => painter != null ? painter.Orbit(MapPainter.OrbitShell) : null);
                    OrbitOverlayLayers.Register(shell);
                }
            }
            return true;
        }

        // Puts EVE back as it was and drops everything of the body.
        private void ReleaseEve(string reason)
        {
            if (layers.Count == 0 && luts.Count == 0) return;
            double releaseUt = Planetarium.GetUniversalTime();
            foreach (EveLayer h in layers)
            {
                LayerSteerState st;
                if (steering.TryGetValue(h, out st)) LayerWindSteering.Release(h, st, releaseUt);
                if (h.Tracker.State == BindState.Bound || h.MirrorInjected) EveLayerAccess.Restore(h);
            }
            steering.Clear();
            layerMaps.Clear();
            layerSetsInUse.Clear();
            AtmospheresLog.Say("cloud painter: EVE layers released (" + reason + "), losses " + binding.TotalLosses + ", rebinds " + binding.TotalRebinds);
            binding.Clear();
            EveRainLink.SetPainted(null);
            layers = new List<EveLayer>();
            ground.Release();
            mirror.Clear();
            luts.Clear();
            if (painter != null)
            {
                painter.Dispose();
                painter = null;
            }
            CloudMapsState.Bump("");
            lastPaint = -1f;
        }

        private void Teardown(string reason)
        {
            ReleaseEve(reason);
            if (shell != null) shell.Hide();
            if (painter != null)
            {
                painter.Dispose();
                painter = null;
            }
        }

        private void PublishStats(float now)
        {
            EveCloudStats.Losses = binding.TotalLosses;
            EveCloudStats.Rebinds = binding.TotalRebinds;
            EveCloudStats.Reasserts = binding.TotalReasserts;
            EveCloudStats.MaxRebindLatencyS = binding.MaxLatencyS;
            int bound = 0;
            int finds = 0;
            foreach (EveLayer h in layers)
            {
                if (h.Tracker.State == BindState.Bound) bound++;
                finds += h.Tracker.WatchdogFinds;
            }
            EveCloudStats.LayersBound = bound;
            EveCloudStats.WatchdogFinds = finds;
            EveCloudStats.MirrorWorstFrameMs = mirror.WorstFrameMs;
            EveCloudStats.MirrorVersion = mirror.Version;
            if (now >= nextStats)
            {
                nextStats = now + 30f;
                if (layers.Count > 0)
                {
                    AtmospheresLog.Say("cloud painter: " + bound + "/" + layers.Count + " layers bound, losses " + EveCloudStats.Losses + " (watchdog "
                        + finds + "), rebinds " + EveCloudStats.Rebinds + ", reasserts " + EveCloudStats.Reasserts + ", worst latency " + EveCloudStats.MaxRebindLatencyS.ToString("0.00")
                        + " s, paints " + EveCloudStats.Paints + " (mean " + EveCloudStats.MeanPaintMs.ToString("0.00") + " ms, worst "
                        + EveCloudStats.WorstPaintMs.ToString("0.00") + " ms), mirror cycles " + mirror.Version + " (worst frame "
                        + mirror.WorstFrameMs.ToString("0.00") + " ms)");
                }
            }
        }
    }
}
