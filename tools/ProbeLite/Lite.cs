using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ProbeLite
{
    // The state, the log, the configuration and the end of one run. Everything the plugin writes goes under GameData/ProbeLite/PluginData:
    // probelite.log (every line), assemblies.txt (what the game has loaded), shots/ (the pictures and results.tsv) and done (the last file;
    // its first line is "ok" or "failed"). tools/vanilla_ground.py waits for the game to quit and reads done.
    internal static class Lite
    {
        internal const string Tag = "[ProbeLite]";

        // The settings the runner may put into PluginData/config.txt (key=value per line); the values here are the defaults.
        internal static string Variant = "vanilla";
        internal static string SaveFolder = "ReDefinitionProbe";
        internal static double Ut = 1470283.6;
        internal static bool SpaceCentre = true;
        internal static bool Flight = true;
        internal static double WatchdogS = 17 * 60;
        // The pad (where the launch pad vessel stands); the flight takes the vessel's own spot.
        internal static double PadLat = -0.0972, PadLon = -74.5577;

        // The frames and seconds every picture waits for (stated here once, logged with each picture).
        internal const int MinFrames = 120;
        internal const double MinSettleS = 4.0, MaxSettleS = 20.0, SceneSettleMinS = 30.0, SceneSettleMaxS = 90.0, StableLuma = 1.0;

        internal static int PadVesselIndex = -1;
        internal static bool MenuDone, SpaceCentreDone, Finished;
        internal static double FrozenUt = double.NaN;
        internal static int Warnings;

        private static string folder;

        internal static string Folder
        {
            get { return folder ?? (folder = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "ProbeLite")); }
        }

        internal static string Data { get { return Path.Combine(Folder, "PluginData"); } }
        internal static string Shots { get { return Path.Combine(Data, "shots"); } }

        internal static string F(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        internal static void Say(string message)
        {
            Debug.Log(Tag + " " + message);
            try
            {
                Directory.CreateDirectory(Data);
                File.AppendAllText(Path.Combine(Data, "probelite.log"), DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // A log that cannot be written must not stop the run.
            }
        }

        internal static void Warn(string message)
        {
            Warnings++;
            Say("WARNING " + message);
        }

        internal static void ReadConfig()
        {
            string path = Path.Combine(Data, "config.txt");
            if (!File.Exists(path))
            {
                Say("no config.txt; defaults");
                return;
            }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                double d;
                bool isNumber = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                switch (key)
                {
                    case "variant": Variant = value; break;
                    case "saveFolder": SaveFolder = value; break;
                    case "ut": if (isNumber) Ut = d; break;
                    case "spaceCentre": SpaceCentre = value == "1" || value == "true"; break;
                    case "flight": Flight = value == "1" || value == "true"; break;
                    case "watchdogS": if (isNumber) WatchdogS = d; break;
                    case "padLat": if (isNumber) PadLat = d; break;
                    case "padLon": if (isNumber) PadLon = d; break;
                }
            }
            Say("config: variant " + Variant + ", save " + SaveFolder + ", UT " + F(Ut) + ", space centre " + SpaceCentre + ", flight " + Flight);
        }

        // Which assemblies the game loaded: proof of what ran. A line per assembly whose name starts with ReDefinition is also a log line.
        internal static void WriteAssemblies()
        {
            var sb = new StringBuilder();
            int ours = 0;
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = a.GetName().Name;
                sb.AppendLine(name + " " + a.GetName().Version);
                if (name.StartsWith("ReDefinition", StringComparison.OrdinalIgnoreCase)) ours++;
            }
            try { File.WriteAllText(Path.Combine(Data, "assemblies.txt"), sb.ToString()); } catch (Exception) { /* not needed for the run */ }
            Say("assemblies loaded: " + AppDomain.CurrentDomain.GetAssemblies().Length + ", of them named ReDefinition*: " + ours);
        }

        // The end: done is written once, then the game quits.
        internal static void Finish(bool ok, string message)
        {
            if (Finished) return;
            Finished = true;
            Say((ok ? "done: " : "FAILED: ") + message);
            try
            {
                Directory.CreateDirectory(Data);
                File.WriteAllText(Path.Combine(Data, "done"), (ok ? "ok" : "failed") + Environment.NewLine + "warnings=" + Warnings + Environment.NewLine + message + Environment.NewLine);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + " done could not be written: " + e.Message);
            }
            Application.Quit();
        }

        // A coroutine whose exception ends the run instead of the coroutine alone. The inner enumerator yields only YieldInstructions and null (no nested coroutines).
        internal static IEnumerator Safe(IEnumerator inner, string what)
        {
            while (true)
            {
                bool more;
                try
                {
                    more = inner.MoveNext();
                }
                catch (Exception e)
                {
                    Finish(false, what + ": " + e.GetType().Name + " " + e.Message + " " + e.StackTrace);
                    yield break;
                }
                if (!more) yield break;
                yield return inner.Current;
            }
        }

        internal static CelestialBody Sun()
        {
            return FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 ? FlightGlobals.Bodies[0] : null;
        }

        // The sun's elevation over the horizon at the camera, degrees.
        internal static double SunElevationDeg(Vector3d at, CelestialBody body)
        {
            CelestialBody sun = Sun();
            if (sun == null || body == null) return double.NaN;
            Vector3d toSun = (sun.position - at).normalized;
            Vector3d up = (at - body.position).normalized;
            return Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, up)))) * 180.0 / Math.PI;
        }
    }

    // Lives through the scenes: the watchdog (the run ends by itself when nothing else does) and the frozen clock.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class LiteHost : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Lite.ReadConfig();
            Lite.WriteAssemblies();
        }

        private void Update()
        {
            if (Lite.Finished) return;
            if (Time.realtimeSinceStartup > Lite.WatchdogS)
            {
                Lite.Finish(false, "watchdog: " + Lite.F(Lite.WatchdogS) + " s are up in scene " + HighLogic.LoadedScene);
                return;
            }
            if (!double.IsNaN(Lite.FrozenUt) && HighLogic.LoadedSceneIsFlight && FlightGlobals.ready)
            {
                try { Planetarium.SetUniversalTime(Lite.FrozenUt); }
                catch (Exception) { /* the clock is not up yet */ }
            }
        }
    }
}
