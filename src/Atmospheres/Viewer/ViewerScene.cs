using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // A layer of the cloud stack as the weather presets see it: its name and group.
    internal struct ViewerLayer
    {
        internal string Name, Group;
        internal float InnerAltitudeM, ThicknessM;
    }

    // The weather states the viewer forces. Field is the weather field's own state; the others hold every layer's map at a constant value through the per-layer
    // tuning keys (cloud.mapOverride.<layer>, cloud.typeOverride.<layer>, cloud.deckRule.<layer>), the same keys the K-T2 probe forces its states with.
    internal enum ViewerWeather
    {
        Field,
        Fair,
        Cumulus,
        Overcast,
        RainDeck,
        CbCell
    }

    // The weather presets as tuning overrides and forced settings, from the layers of the stack (no layer name is fixed: a layer's role is read from its group
    // and name, so another body's stack works the same way).
    internal static class WeatherPresets
    {
        internal enum Role
        {
            Other,
            Deck,
            Cumulus,
            Cumulonimbus,
            Cirrus
        }

        internal static Role RoleOf(ViewerLayer layer)
        {
            string name = layer.Name ?? "";
            if (string.Equals(layer.Group, CloudCoverShift.RainGroup, StringComparison.Ordinal)) return Role.Deck;
            if (name.IndexOf("cumulonimbus", StringComparison.OrdinalIgnoreCase) >= 0) return Role.Cumulonimbus;
            if (name.IndexOf("cumulus", StringComparison.OrdinalIgnoreCase) >= 0) return Role.Cumulus;
            if (name.IndexOf("cirrus", StringComparison.OrdinalIgnoreCase) >= 0) return Role.Cirrus;
            return Role.Other;
        }

        // The per-layer keys a preset writes (and Field clears).
        internal static IEnumerable<string> KeysOf(ViewerLayer layer)
        {
            yield return CloudLookTuning.TuneMapOverride + layer.Name;
            yield return CloudLookTuning.TuneTypeOverride + layer.Name;
            yield return CloudLookTuning.TuneDeckRule + layer.Name;
        }

        // The map value, the type value (-1: the field's) and the deck rule (-1: the layer's cfg; 0 off, so a constant map draws everywhere) of a role in a preset.
        internal static void Look(ViewerWeather weather, Role role, out float map, out float type, out float deck)
        {
            map = 0f;
            type = -1f;
            deck = -1f;
            switch (weather)
            {
                case ViewerWeather.Fair:
                    if (role == Role.Cumulus) { map = 0.25f; type = 0.25f; }
                    else if (role == Role.Cirrus) map = 0.15f;
                    break;
                case ViewerWeather.Cumulus:
                    if (role == Role.Cumulus) { map = 0.6f; type = 0.5f; }
                    else if (role == Role.Cirrus) map = 0.2f;
                    break;
                case ViewerWeather.Overcast:
                    if (role == Role.Deck) { map = 1f; type = 0.5f; deck = 0f; }
                    else if (role == Role.Cirrus) map = 0.3f;
                    break;
                case ViewerWeather.RainDeck:
                    if (role == Role.Deck) { map = 1f; type = 1f; deck = 0f; }
                    else if (role == Role.Cumulus) { map = 0.3f; type = 0.75f; }
                    break;
                case ViewerWeather.CbCell:
                    if (role == Role.Cumulonimbus) { map = 0.35f; type = 0.5f; deck = 0f; }
                    else if (role == Role.Cumulus) { map = 0.3f; type = 0.5f; }
                    else if (role == Role.Cirrus) map = 0.2f;
                    break;
            }
        }

        // The overrides of a preset: key and value; a value of NaN means the key's override is removed. Field removes them all.
        internal static List<KeyValuePair<string, float>> Overrides(ViewerWeather weather, IEnumerable<ViewerLayer> layers)
        {
            var list = new List<KeyValuePair<string, float>>();
            foreach (ViewerLayer layer in layers)
            {
                if (string.IsNullOrEmpty(layer.Name)) continue;
                if (weather == ViewerWeather.Field)
                {
                    foreach (string key in KeysOf(layer)) list.Add(new KeyValuePair<string, float>(key, float.NaN));
                    continue;
                }
                float map, type, deck;
                Look(weather, RoleOf(layer), out map, out type, out deck);
                list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneMapOverride + layer.Name, map));
                list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneTypeOverride + layer.Name, type < 0f ? float.NaN : type));
                list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneDeckRule + layer.Name, deck < 0f ? float.NaN : deck));
            }
            return list;
        }

        // The precipitation the preset forces at the camera (settings forceLevel and forceType): a negative level leaves the setting as it was at the viewer's start.
        internal static void Precipitation(ViewerWeather weather, out double level, out ForceType type)
        {
            level = -1.0;
            type = ForceType.Auto;
            if (weather != ViewerWeather.RainDeck) return;
            level = 0.6;
            type = ForceType.Rain;
        }

        internal static string Title(ViewerWeather weather)
        {
            switch (weather)
            {
                case ViewerWeather.Fair: return "Fair";
                case ViewerWeather.Cumulus: return "Cumulus Field";
                case ViewerWeather.Overcast: return "Overcast";
                case ViewerWeather.RainDeck: return "Rain Deck";
                case ViewerWeather.CbCell: return "Cb Cell";
                default: return "Weather Field";
            }
        }
    }

    // A pose of the viewer: metres above the ground (or the sea) at the site, the camera's elevation above the horizon and compass heading, its distance from the
    // vessel. A NaN elevation looks at the horizon (the limb from orbit).
    internal struct ViewerPose
    {
        internal string Name;
        internal double AglM, ElevationDeg, HeadingDeg, CameraDistM;
    }

    // The pose presets: the K-T2 probe's views (T09Probe.Views) and three of the viewer's own, the heights of "inside a layer" and "above the deck" taken from
    // the stack where it stands.
    internal static class PosePresets
    {
        internal const double FiveKmM = 5000.0, AboveDeckMarginM = 800.0, CbDistanceAglM = 1500.0;

        internal static List<ViewerPose> For(IList<ViewerLayer> layers)
        {
            var poses = new List<ViewerPose>();
            foreach (T09Probe.View v in T09Probe.Views)
            {
                var pose = new ViewerPose { Name = v.Name, AglM = v.AglM, ElevationDeg = v.PitchDeg, HeadingDeg = v.HeadingDeg, CameraDistM = v.CameraDistM };
                if (v.Name == "inside" && InsideHeight(layers, out double inside)) pose.AglM = inside;
                if (v.Name == "above" && AboveHeight(layers, out double above)) pose.AglM = above;
                poses.Add(pose);
            }
            poses.Add(new ViewerPose { Name = "5km", AglM = FiveKmM, ElevationDeg = 5.0, HeadingDeg = 90.0, CameraDistM = 150.0 });
            poses.Add(new ViewerPose { Name = "cb_distance", AglM = CbDistanceAglM, ElevationDeg = 3.0, HeadingDeg = 90.0, CameraDistM = 120.0 });
            return poses;
        }

        // The middle of the cumulus layer (else the lowest layer).
        internal static bool InsideHeight(IList<ViewerLayer> layers, out double metres)
        {
            metres = 0.0;
            if (layers == null || layers.Count == 0) return false;
            ViewerLayer pick = layers[0];
            bool found = false;
            foreach (ViewerLayer l in layers)
                if (WeatherPresets.RoleOf(l) == WeatherPresets.Role.Cumulus) { pick = l; found = true; break; }
            if (!found)
                foreach (ViewerLayer l in layers)
                    if (l.InnerAltitudeM < pick.InnerAltitudeM) pick = l;
            metres = pick.InnerAltitudeM + 0.5 * pick.ThicknessM;
            return metres > 0.0;
        }

        // Above the tops of the deck and the cumulus layer (cirrus and cumulonimbus left out: they reach far higher).
        internal static bool AboveHeight(IList<ViewerLayer> layers, out double metres)
        {
            metres = 0.0;
            if (layers == null) return false;
            foreach (ViewerLayer l in layers)
            {
                WeatherPresets.Role r = WeatherPresets.RoleOf(l);
                if (r == WeatherPresets.Role.Deck || r == WeatherPresets.Role.Cumulus) metres = Math.Max(metres, l.InnerAltitudeM + l.ThicknessM + AboveDeckMarginM);
            }
            return metres > 0.0;
        }

        internal static string Title(string name)
        {
            switch (name)
            {
                case "ground": return "Ground";
                case "inside": return "Inside Layer";
                case "above": return "Above Deck";
                case "orbit_edge": return "Orbit Edge";
                case "sky3": return "Ground Sky";
                case "5km": return "5 km";
                case "cb_distance": return "Cb Distance";
                default: return name;
            }
        }
    }

    // The altitude control: a slider on a log scale of the height above the ground, from MinAglM to the body's orbit edge.
    internal static class AltitudeScale
    {
        internal const double MinAglM = 10.0;

        internal static double MaxAglM(double atmosphereDepthM)
        {
            return Math.Max(T09Probe.Views[3].AglM, atmosphereDepthM > 0.0 ? atmosphereDepthM * 1.2 : 0.0);
        }

        internal static float ToSlider(double aglM)
        {
            return (float)Math.Log10(Math.Max(MinAglM, aglM));
        }

        internal static double FromSlider(float slider, double maxAglM)
        {
            double v = Math.Pow(10.0, slider);
            return Math.Max(MinAglM, Math.Min(maxAglM, v));
        }
    }

    // The schedule of a picture: the window is hidden for HideFrames frames, the picture is asked for in the frame after, and the window stays hidden through that
    // frame too (the screen is read at the end of the frame the request is made in), then it is shown again.
    internal sealed class CaptureSchedule
    {
        private readonly int hideFrames;
        private int hiding = -1, holdAfter;

        internal CaptureSchedule(int hideFrames)
        {
            this.hideFrames = hideFrames;
        }

        // The window must be hidden: from the request until the frame after the picture was asked for.
        internal bool Busy
        {
            get { return hiding >= 0 || holdAfter > 0; }
        }

        // Starts a picture; false where one is under way.
        internal bool Request()
        {
            if (Busy) return false;
            hiding = hideFrames;
            return true;
        }

        // One frame; true in the frame in which the picture is to be asked for.
        internal bool Step()
        {
            if (holdAfter > 0) holdAfter--;
            if (hiding >= 0 && --hiding < 0)
            {
                holdAfter = 1;
                return true;
            }
            return false;
        }
    }

    // The game time while the viewer runs. The natural time is the time the game would show without the viewer: it advances by what the game advanced since the
    // viewer last wrote the clock. The clock is written as the natural time plus the player's offset (time of day), or held at one value (Freeze Time).
    // Close gives the natural time back, so the viewer leaves no shift in the game's clock.
    internal sealed class ViewerClock
    {
        private double lastWritten;

        internal ViewerClock(double ut)
        {
            Natural = ut;
            lastWritten = ut;
        }

        internal double Natural { get; private set; }
        internal double OffsetS { get; set; }
        internal bool Frozen { get; private set; }
        internal double FrozenUt { get; private set; }

        // Holds the clock at the time it shows now, or lets it run again.
        internal void Freeze(bool on)
        {
            if (on && !Frozen) FrozenUt = lastWritten;
            if (!on && Frozen) OffsetS = FrozenUt - Natural;
            Frozen = on;
        }

        // One frame: observed is the game's clock now. Returns the time to write.
        internal double Step(double observed)
        {
            double advanced = observed - lastWritten;
            if (!double.IsNaN(advanced) && !double.IsInfinity(advanced) && advanced > 0.0) Natural += advanced;
            lastWritten = Frozen ? FrozenUt : Natural + OffsetS;
            return lastWritten;
        }

        // The time the clock shows (the last written).
        internal double Shown
        {
            get { return lastWritten; }
        }
    }
}
