using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.C25
{
    // Reads the reconnaissance dumps of B09 (tests/data/recon/dumps): flat JSON with numeric arrays, numbers and one string. The dumps are the
    // real heights, ocean mask and temperature samples of KSP's Kerbin, Duna, Laythe and Eve.
    internal static class DumpReader
    {
        internal static string Folder()
        {
            return RepoRoot.Locate("tests", "data", "recon", "dumps");
        }

        internal static double[] Array(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\":\\[([^\\]]*)\\]");
            if (!m.Success) return null;
            string[] parts = m.Groups[1].Value.Split(',');
            var result = new List<double>(parts.Length);
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (t.Length > 0) result.Add(double.Parse(t, CultureInfo.InvariantCulture));
            }
            return result.ToArray();
        }

        // An array of strings (the golden values are hex strings of the bits of doubles).
        internal static string[] Strings(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\":\\s*\\[([^\\]]*)\\]");
            if (!m.Success) return null;
            var result = new List<string>();
            foreach (Match s in Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\"")) result.Add(s.Groups[1].Value);
            return result.ToArray();
        }

        internal static double Number(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\":(-?[0-9.eE+-]+)");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
        }

        internal static string Text(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\":\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // The temperature table of a body in the layout of ThermalSamples.
        internal static ThermalSamples Thermal(string body)
        {
            string json = File.ReadAllText(Path.Combine(Folder(), "run2-temperature-" + body + ".json"));
            var s = new ThermalSamples();
            s.LatitudeDeg = Array(json, "latitude_deg");
            s.AltitudeM = Array(json, "altitude_m");
            s.LongitudeDeg = Array(json, "longitude_deg");
            s.Phases = Array(json, "phase_ut").Length;
            s.Kelvin = Array(json, "temperature_k");
            return s;
        }

        // The heights (metres, row 0 at the NORTH pole as the generator's raster has it) and whether the body has an ocean.
        internal static float[] Heights(string body, out int width, out int height, out bool ocean)
        {
            string json = File.ReadAllText(Path.Combine(Folder(), "run1-heights-" + body + ".json"));
            width = (int)Number(json, "width");
            height = (int)Number(json, "height");
            ocean = json.Contains("\"ocean\":true");
            double[] south = Array(json, "heights_m");
            var north = new float[south.Length];
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++) north[row * width + column] = (float)south[(height - 1 - row) * width + column];
            }
            return north;
        }
    }
}
