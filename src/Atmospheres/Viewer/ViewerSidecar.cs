using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReDefinition.Atmospheres.Viewer
{
    // What the viewer saw when a picture was taken.
    internal sealed class CaptureRecord
    {
        internal string Picture = "", Taken = "", Body = "", Pose = "", Weather = "";
        internal double LatDeg, LonDeg, AltitudeAslM, AltitudeAglM, CameraAzimuthDeg, CameraElevationDeg, Ut, SunElevationDeg;
        internal bool TimeFrozen, Baseline;
        internal float CloudSpeed = 1f;
        internal char Side = '-';
        internal int GeneratorSeed;
    }

    // The text file beside a picture: the scene and every option that differs from its default, one "key = value" line each, so that a report can cite what was
    // seen. Settings are the settings table's (the value as settings.cfg writes it); tuning keys are the cloud pass's, with their compiled default.
    internal static class ViewerSidecar
    {
        internal const string Head = "CLOUD_VIEWER_CAPTURE";

        private static string N(double v, string format)
        {
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        internal static string Format(CaptureRecord r, IEnumerable<SettingDef> settings, Func<string, double> number, Func<string, string> text,
                                      IEnumerable<CloudKey> keys, IOptionStore store)
        {
            var b = new StringBuilder();
            b.Append(Head).Append('\n').Append("{\n");
            Line(b, "picture", r.Picture);
            Line(b, "taken", r.Taken);
            Line(b, "body", r.Body);
            Line(b, "pose", r.Pose);
            Line(b, "latitude", N(r.LatDeg, "0.00000"));
            Line(b, "longitude", N(r.LonDeg, "0.00000"));
            Line(b, "altitudeAsl", N(r.AltitudeAslM, "0.0"));
            Line(b, "altitudeAgl", N(r.AltitudeAglM, "0.0"));
            Line(b, "cameraAzimuth", N(r.CameraAzimuthDeg, "0.0"));
            Line(b, "cameraElevation", N(r.CameraElevationDeg, "0.0"));
            Line(b, "ut", N(r.Ut, "0.0"));
            Line(b, "sunElevation", N(r.SunElevationDeg, "0.0"));
            Line(b, "timeFrozen", r.TimeFrozen ? "True" : "False");
            Line(b, "weather", r.Weather);
            Line(b, "generatorSeed", r.GeneratorSeed.ToString(CultureInfo.InvariantCulture));
            Line(b, "cloudSpeed", N(r.CloudSpeed, "0.###"));
            Line(b, "baselineNoLayers", r.Baseline ? "True" : "False");
            Line(b, "abSide", r.Side.ToString());
            b.Append("\tSETTINGS\n\t{\n");
            foreach (SettingDef def in settings)
            {
                if (def.Kind == SettingKind.Text)
                {
                    string t = text(def.Key);
                    if (!string.Equals(t, def.DefaultText, StringComparison.Ordinal)) Line(b, def.Key, t, "\t\t");
                    continue;
                }
                double v = number(def.Key);
                if (v != def.Default) Line(b, def.Key, def.Format(v) + "   // default " + def.Format(def.Default), "\t\t");
            }
            b.Append("\t}\n\tTUNING\n\t{\n");
            foreach (CloudKey key in keys)
            {
                float v = store.Effective(key.Key, key.Default);
                if (v != key.Default) Line(b, key.Key, v.ToString("R", CultureInfo.InvariantCulture) + "   // default " + key.Default.ToString("R", CultureInfo.InvariantCulture), "\t\t");
            }
            b.Append("\t}\n}\n");
            return b.ToString();
        }

        private static void Line(StringBuilder b, string key, string value, string indent = "\t")
        {
            b.Append(indent).Append(key).Append(" = ").Append(value ?? "").Append('\n');
        }
    }
}
