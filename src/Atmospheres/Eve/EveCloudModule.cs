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
        internal static int Losses, Rebinds, WatchdogFinds;
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
    // Per frame: the binding is observed (an event of EVE's, the material's identity, and the watchdog's read-back every second) and made
    // again where it is gone; the mirror works through a band of rows. Every PaintInterval (0.25 s) the field's maps are fetched, uploaded
    // and painted with the slew limit. The ground effects run in Camera.onPreCull, after EVE's Update.
    [AtmospheresModule(60)]
    internal sealed class EveCloudModule : IAtmospheresModule
    {
        private const float ScanIntervalS = 3f;
        private const float OrbitIntervalFarS = 2f, OrbitIntervalNearS = 10f;
        private const double LiveBelowM = 30000;

        private MapPainter painter;
        private readonly EveBinding binding = new EveBinding();
        private readonly CpuMirror mirror = new CpuMirror();
        private readonly EveGroundEffects ground = new EveGroundEffects();
        private OwnShell shell;
        private List<EveLayer> layers = new List<EveLayer>();
        private readonly Dictionary<PaintSet, byte[]> luts = new Dictionary<PaintSet, byte[]>();
        private readonly List<EveCoverageMath.Blob> cells = new List<EveCoverageMath.Blob>();
        private readonly List<EveCoverageMath.Rect> painted = new List<EveCoverageMath.Rect>();
        private byte[] overlay;

        private CelestialBody body;
        private BodyClimate climate;
        private bool applyEventSubscribed, applyFlag, preCullSubscribed, shaderFailedLogged, wasActive;
        private float nextScan, nextPaint, nextOrbit, lastPaint = -1f, nextStats;
        private bool mirrorNew;
        private double paintMsSum;
        private int paintCount;

        public void Init()
        {
            Services.EveFieldSuppressor = ground;
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
        }

        public void Tick(float dt)
        {
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
            CloudSourceKind source = c.Clouds.Source ?? AtmospheresSettings.CloudSource;
            bool eveMode = (source == CloudSourceKind.Eve || source == CloudSourceKind.Both) && EveLayerAccess.Available;
            bool ownMode = source == CloudSourceKind.Ours;
            if (!eveMode)
            {
                if (layers.Count > 0) ReleaseEve("cloudSource is " + source);
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
            if (now >= nextPaint) Paint(now, b, ut);
            binding.WatchdogIntervalS = Tuning.Get("eve.watchdogS", (float)BindingTracker.WatchdogIntervalS);
            int bound = binding.Tick(now, Choose, text => AtmospheresLog.Say("cloud painter: " + text));
            applyFlag = false;
            foreach (EveLayer h in layers)
            {
                if (h.Tracker.State != BindState.Bound) continue;
                PaintSet set = Choose(h);
                if (set != null && (!h.MirrorInjected || bound > 0)) mirror.Inject(h, set);
            }
            bool snap = mirrorNew;
            mirrorNew = false;
            if (mirror.Tick(now, painter.CoverageMap, painter.LevelMap, Tuning.Get("eve.slewRate", MapSlew.DefaultRate), snap))
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
            Vessel v = FlightGlobals.ActiveVessel;
            return v != null && v.mainBody == b && v.altitude < LiveBelowM;
        }

        // The paint set a layer is bound to, or null while its layout is unknown or nothing is painted into it yet.
        private PaintSet Choose(EveLayer h)
        {
            if (!h.LayoutKnown || painter == null) return null;
            PaintSet set = painter.SetFor(h.ShiftDeg, h.Mirrored);
            if (!luts.ContainsKey(set))
            {
                byte[] lut = CloudTypeEncoding.BuildLut(h.Slots, false);
                luts[set] = lut;
                mirror.Ensure(set, lut);
                mirrorNew = true;
            }
            return set.Primed ? set : null;
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
            painter.Upload(painter.CoverageMap, painter.LevelMap);
            double ms = 0;
            foreach (EveLayer h in layers)
            {
                if (!h.LayoutKnown || h.LeftReason.Length > 0) continue;
                painter.SetFor(h.ShiftDeg, h.Mirrored);
            }
            foreach (PaintSet set in painter.Sets)
            {
                byte[] lut;
                if (luts.TryGetValue(set, out lut)) painter.SetLut(lut);
                ms += painter.Paint(set, dt, rate, false);
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
                bool both = (climate.Clouds.Source ?? AtmospheresSettings.CloudSource) == CloudSourceKind.Both && AtmospheresSettings.UseEveWeather;
                GroundEffectMath.Factors f = EveGroundEffects.FactorsFor(sample, EveRainLink.Coverage, both, centre, width);
                float level = Math.Max(sample.Level01, GroundEffectMath.EveLevel(EveRainLink.Coverage, false, false) * (both ? 1f : 0f));
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
            CloudSourceKind source = climate.Clouds.Source ?? AtmospheresSettings.CloudSource;
            if (source == CloudSourceKind.Ours) return true;
            if (source == CloudSourceKind.None) return false;
            // EVE carries the picture where one of its layers has a 2D layer; the shell stands in where none has.
            foreach (EveLayer h in layers)
            {
                if (h.Layer2D != null && h.Tracker.State == BindState.Bound) return false;
            }
            return layers.Count > 0;
        }

        private void RunPictures(float now, BodyClimate c, CelestialBody b, bool eveMode, bool ownMode)
        {
            bool far = MapView.MapIsEnabled || (FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.altitude > 100000);
            bool shellShown = false;
            if (shell != null)
            {
                bool wanted = ShellWanted(b);
                if (wanted && far)
                {
                    if (!shell.Calibrated || !shell.Visible) shell.Update(b, true);
                    shellShown = shell.Visible;
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
                        if (!EveLayerAccess.Intact2D(h, painter.Orbit(MapPainter.OrbitEve))) EveLayerAccess.Bind2D(h, painter.Orbit(MapPainter.OrbitEve));
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
            foreach (EveLayer h in layers)
            {
                if (h.Tracker.State == BindState.Bound || h.MirrorInjected) EveLayerAccess.Restore(h);
            }
            AtmospheresLog.Say("cloud painter: EVE layers released (" + reason + "), losses " + binding.TotalLosses + ", rebinds " + binding.TotalRebinds);
            binding.Clear();
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
                        + finds + "), rebinds " + EveCloudStats.Rebinds + ", worst latency " + EveCloudStats.MaxRebindLatencyS.ToString("0.00")
                        + " s, paints " + EveCloudStats.Paints + " (mean " + EveCloudStats.MeanPaintMs.ToString("0.00") + " ms, worst "
                        + EveCloudStats.WorstPaintMs.ToString("0.00") + " ms), mirror cycles " + mirror.Version + " (worst frame "
                        + mirror.WorstFrameMs.ToString("0.00") + " ms)");
                }
            }
        }
    }
}
