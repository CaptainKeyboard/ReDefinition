using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Solar panels dimmed by cloud and storm along the sun ray (package D31, optional, setting solarDimming).
    //
    // How the factor reaches the panel. In the decompiled Assembly-CSharp (KSP 1.12.5, ModuleDeployableSolarPanel) the line
    // `efficiencyMult = ApplyEfficiencyAdjustments(originalEfficiencyMultiplier);` runs on every tick before the flow is worked out, and
    // `_efficMult = temperatureEfficCurve.Evaluate(skinTemperature) * timeEfficCurve.Evaluate((UT - launchUT) * 1.1574074074074073E-05) * efficiencyMult;
    // _flowRate = sunAOA * _efficMult * _distMult;` is the flow. efficiencyMult is a persistent field, so scaling it after the panel's
    // update has no effect on that tick and would be written to the save. The factor therefore goes into timeEfficCurve (a public
    // configuration field that the panel multiplies into _efficMult, not persistent): the panel's own curve is replaced by a copy scaled by the factor and put back when the
    // factor is 1, at scene exit, on rails, at a save and at shutdown. efficiencyMult is never written, so a save mid-storm holds the
    // value it held without it (row: the saved efficiencyMult).
    // Unknown panel modules of other mods are skipped and named once. Vessels on rails and unloaded vessels are not touched.
    internal static class SolarDimming
    {
        private sealed class Record
        {
            internal FloatCurve Original;
            internal FloatCurve Scaled;
            internal Vessel Owner;
            internal double Applied = 1.0;
        }

        private sealed class VesselRecord
        {
            internal double Factor = 1.0;
            internal double Timer;
            internal bool Seeded;
        }

        private static readonly Dictionary<ModuleDeployableSolarPanel, Record> Panels = new Dictionary<ModuleDeployableSolarPanel, Record>();
        private static readonly Dictionary<Vessel, VesselRecord> Vessels = new Dictionary<Vessel, VesselRecord>();
        private static readonly HashSet<string> Skipped = new HashSet<string>();

        // The last factor worked out for any vessel and its transmission, for the log and the probe.
        internal static double LastFactor = 1.0, LastTransmission = 1.0;
        // The number of panels that carry a scaled curve now.
        internal static int ScaledPanels { get { int n = 0; foreach (Record r in Panels.Values) if (r.Applied < 1.0) n++; return n; } }

        // The factor now smoothed for a vessel (1 for one never seen).
        internal static double FactorOf(Vessel v)
        {
            VesselRecord r;
            return v != null && Vessels.TryGetValue(v, out r) ? r.Factor : 1.0;
        }

        // Called from the vessel's physics tick: works the factor out at the interval and applies it.
        internal static void Tick(Vessel v, double dt)
        {
            if (v == null || !v.loaded || v.packed) return;
            if (!Wanted(v))
            {
                // Nothing dimmed anywhere (the usual case with the setting off): no walk over the vessel.
                if (Vessels.ContainsKey(v)) Restore(v);
                return;
            }
            VesselRecord r;
            if (!Vessels.TryGetValue(v, out r))
            {
                r = new VesselRecord();
                Vessels[v] = r;
            }
            r.Timer += dt;
            if (r.Seeded && r.Timer < SolarMath.UpdateIntervalSeconds) return;
            double elapsed = r.Seeded ? r.Timer : SolarMath.SmoothingSeconds * 10.0;
            r.Timer = 0.0;
            r.Seeded = true;
            double target = TargetFactor(v);
            r.Factor = SolarMath.Smooth(r.Factor, target, elapsed);
            LastFactor = r.Factor;
            Apply(v, r.Factor);
        }

        private static bool Wanted(Vessel v)
        {
            return AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && AtmospheresSettings.SolarDimming
                   && v.mainBody != null && v.mainBody.atmosphere;
        }

        // The factor the sky asks for at the vessel: the transmission along the sun ray, the body's solarLoss and the setting.
        private static double TargetFactor(Vessel v)
        {
            CelestialBody body = v.mainBody;
            BodyClimate climate = Bodies.Get(body);
            if (climate == null || !climate.Status.WeatherRuns) return 1.0;
            double ut = Planetarium.GetUniversalTime();
            WeatherSample s;
            if (!WeatherHub.Sample(body.flightGlobalsIndex, v.latitude, v.longitude, v.altitude, ut, out s)) return 1.0;
            double tau = s.OpticalDepthVertical;
            // Where the host reads EVE's decks its value counts and not the field's: the same rule as the frame driver's, so that the panels
            // and the picture agree (the field's cloud is painted into those decks, and the two must not add).
            Func<double, double, double, double, float> hook = WeatherHooks.CloudOpticalDepth;
            if (hook != null)
            {
                float t = hook(v.latitude, v.longitude, v.altitude, ut);
                if (t > 0f) tau = t;
            }
            CelestialBody sun = GameGlobals.Sun;
            double elevation = Math.PI / 2.0;
            if (sun != null)
            {
                Vector3d toSun = (sun.position - v.GetWorldPos3D()).normalized;
                Vector3d up = (v.GetWorldPos3D() - body.position).normalized;
                elevation = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, up))));
            }
            LastTransmission = SolarMath.Transmission(tau, elevation);
            return SolarMath.Factor(LastTransmission, climate.Gameplay.SolarLoss, AtmospheresSettings.SolarDimmingStrength);
        }

        private static void Apply(Vessel v, double factor)
        {
            var parts = v.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                var modules = parts[i].Modules;
                for (int j = 0; j < modules.Count; j++)
                {
                    PartModule m = modules[j];
                    var panel = m as ModuleDeployableSolarPanel;
                    if (panel == null)
                    {
                        NoteUnknown(m);
                        continue;
                    }
                    ApplyTo(panel, factor);
                }
            }
        }

        private static void NoteUnknown(PartModule m)
        {
            string name = m.GetType().Name;
            if (name.IndexOf("Solar", StringComparison.OrdinalIgnoreCase) < 0 || name == "ModuleDeployableSolarPanel") return;
            if (Skipped.Add(name)) AtmospheresLog.Say("solar: the module " + name + " is not a stock solar panel and is not dimmed");
        }

        private static void ApplyTo(ModuleDeployableSolarPanel panel, double factor)
        {
            Record r;
            if (!Panels.TryGetValue(panel, out r))
            {
                if (factor >= 1.0 - SolarMath.ApplyEpsilon || panel.timeEfficCurve == null) return;
                r = new Record { Original = panel.timeEfficCurve, Owner = panel.vessel };
                Panels[panel] = r;
            }
            // The panel's curve was replaced by someone else since we scaled it: leave it alone and forget it.
            if (r.Scaled != null && !ReferenceEquals(panel.timeEfficCurve, r.Scaled) && !ReferenceEquals(panel.timeEfficCurve, r.Original))
            {
                Panels.Remove(panel);
                AtmospheresLog.Warn("solar-curve", "a panel's efficiency curve was replaced by another mod; the dimming leaves that panel alone");
                return;
            }
            if (Math.Abs(factor - r.Applied) < SolarMath.ApplyEpsilon && !(factor >= 1.0 && r.Applied < 1.0)) return;
            if (factor >= 1.0 - SolarMath.ApplyEpsilon)
            {
                panel.timeEfficCurve = r.Original;
                r.Scaled = null;
                r.Applied = 1.0;
                return;
            }
            r.Scaled = Scale(r.Original, (float)factor);
            panel.timeEfficCurve = r.Scaled;
            r.Applied = factor;
        }

        // A copy of a curve with every value (and tangent) multiplied.
        internal static FloatCurve Scale(FloatCurve source, float factor)
        {
            return new FloatCurve(ScaleKeys(source.Curve.keys, factor));
        }

        // The keys of a curve with every value and tangent multiplied (the array is the caller's copy and is changed in place).
        internal static Keyframe[] ScaleKeys(Keyframe[] keys, float factor)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i].value *= factor;
                keys[i].inTangent *= factor;
                keys[i].outTangent *= factor;
            }
            return keys;
        }

        // Puts a vessel's panels back.
        internal static void Restore(Vessel v)
        {
            if (v == null) return;
            Vessels.Remove(v);
            var parts = v.parts;
            if (parts == null) return;
            for (int i = 0; i < parts.Count; i++)
            {
                var modules = parts[i].Modules;
                for (int j = 0; j < modules.Count; j++)
                {
                    var panel = modules[j] as ModuleDeployableSolarPanel;
                    if (panel != null) RestorePanel(panel);
                }
            }
        }

        // Puts the panels back and keeps the smoothed factors, so that the dimming picks up where it was (a save).
        internal static void RestorePanels()
        {
            var saved = new Dictionary<Vessel, VesselRecord>(Vessels);
            RestoreAll();
            foreach (KeyValuePair<Vessel, VesselRecord> pair in saved)
            {
                if (pair.Key == null) continue;
                // The next tick works the factor out at once and applies it from the smoothed value.
                pair.Value.Timer = SolarMath.UpdateIntervalSeconds;
                Vessels[pair.Key] = pair.Value;
            }
        }

        // Puts every panel back (a scene change, the switch going off).
        internal static void RestoreAll()
        {
            foreach (KeyValuePair<ModuleDeployableSolarPanel, Record> pair in new List<KeyValuePair<ModuleDeployableSolarPanel, Record>>(Panels))
            {
                try
                {
                    if (pair.Key != null && pair.Value.Original != null) pair.Key.timeEfficCurve = pair.Value.Original;
                }
                catch (Exception e)
                {
                    AtmospheresLog.Failed("solar-restore", "a solar panel could not be put back", e);
                }
            }
            Panels.Clear();
            Vessels.Clear();
        }

        // Forgets the vessels' smoothing so that the factor starts again; the panels keep what they have.
        // The vessel is gone (destroyed or unloaded): its panels' records go with it, so that a destroyed vessel leaves nothing in Panels.
        internal static void ForgetVessel(Vessel v)
        {
            if (v == null) return;
            Vessels.Remove(v);
            if (Panels.Count == 0) return;
            List<ModuleDeployableSolarPanel> gone = null;
            foreach (KeyValuePair<ModuleDeployableSolarPanel, Record> pair in Panels)
            {
                if (pair.Key == null || ReferenceEquals(pair.Value.Owner, v))
                {
                    if (gone == null) gone = new List<ModuleDeployableSolarPanel>();
                    gone.Add(pair.Key);
                }
            }
            if (gone != null) foreach (ModuleDeployableSolarPanel p in gone) Panels.Remove(p);
        }

        private static void RestorePanel(ModuleDeployableSolarPanel panel)
        {
            Record r;
            if (!Panels.TryGetValue(panel, out r)) return;
            if (r.Original != null) panel.timeEfficCurve = r.Original;
            Panels.Remove(panel);
        }

        // The startup check: every panel in the game whose curve differs from the one remembered while no dimming is wanted. Logs a mismatch.
        internal static int Mismatches()
        {
            int bad = 0;
            foreach (KeyValuePair<ModuleDeployableSolarPanel, Record> pair in Panels)
            {
                if (pair.Key == null) continue;
                bool scaled = ReferenceEquals(pair.Key.timeEfficCurve, pair.Value.Scaled);
                bool original = ReferenceEquals(pair.Key.timeEfficCurve, pair.Value.Original);
                if (!scaled && !original) bad++;
            }
            return bad;
        }
    }

    // The vessel module that gives the dimming its tick on every loaded vessel in the flight scene.
    public sealed class SolarVesselModule : VesselModule
    {
        public override Activation GetActivation()
        {
            return Activation.FlightScene | Activation.LoadedVessels;
        }

        public override void OnGoOnRails()
        {
            SolarDimming.Restore(vessel);
        }

        private void FixedUpdate()
        {
            try
            {
                SolarDimming.Tick(vessel, Time.fixedDeltaTime);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("solar-tick", "the solar dimming failed for a vessel", e);
            }
        }

        private void OnDestroy()
        {
            // The vessel and its parts go; there is nothing to put back on them, only the record to drop.
            SolarDimming.ForgetVessel(vessel);
        }
    }

    // Puts the panels back on every path that leaves the flight or writes the game, and when the setting goes off.
    [AtmospheresModule(63)]
    internal sealed class SolarDimmingModule : IAtmospheresModule
    {
        private LateSubscription events;
        private bool wasOn;
        private float checkTimer;

        public void Init()
        {
            events = new LateSubscription("solar-events", "the solar dimming cannot subscribe to the save events", SubscribeEvents, UnsubscribeEvents);
            events.Ensure();
        }

        // False while one of the game's event objects does not exist yet (nothing is added then); LateSubscription asks again next tick.
        private static bool SubscribeEvents()
        {
            if (GameEvents.onGameStateSave == null || GameEvents.onGameSceneLoadRequested == null) return false;
            GameEvents.onGameStateSave.Add(OnSave);
            GameEvents.onGameSceneLoadRequested.Add(OnScene);
            return true;
        }

        private static void UnsubscribeEvents()
        {
            GameEvents.onGameStateSave.Remove(OnSave);
            GameEvents.onGameSceneLoadRequested.Remove(OnScene);
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
            if (events != null) events.Ensure();
            bool on = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && AtmospheresSettings.SolarDimming;
            if (wasOn && !on) SolarDimming.RestoreAll();
            wasOn = on;
            checkTimer += dt;
            if (on && checkTimer > 10f)
            {
                checkTimer = 0f;
                int bad = SolarDimming.Mismatches();
                if (bad > 0) AtmospheresLog.Warn("solar-mismatch", bad + " solar panels carry an efficiency curve that is neither the original nor the dimmed one");
            }
        }

        public void Shutdown()
        {
            SolarDimming.RestoreAll();
            if (events != null) events.Release();
        }

        private static void OnSave(ConfigNode node)
        {
            // The curve is not saved; the panels are put back anyway, so that nothing of the dimming is ever in the game's state, and the next
            // tick of each vessel scales them again from the smoothed factor it had.
            SolarDimming.RestorePanels();
        }

        private static void OnScene(GameScenes scene)
        {
            SolarDimming.RestoreAll();
        }
    }
}
