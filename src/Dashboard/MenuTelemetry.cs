using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Dashboard
{
    // What the dashboard's menu page shows and does, in no vessel: the game's scene, KSP's progress
    // while it loads, the saves to go on with, and loading one or quitting from the main menu.
    internal sealed class MenuTelemetry
    {
        // A save is looked for again at most this often, and only in the scenes that show the list.
        private const float SavesEvery = 3f;

        // One save as last read: its files' times, by which a change is seen, and its entry in the list.
        private sealed class Save
        {
            public DateTime SfsTime, MetaTime; // when persistent.sfs and persistent.loadmeta were last written
            public Dictionary<string, object> Entry; // what the list shows of it (Entry)
        }

        // The saves read so far by folder name, the list as JSON when it was last made, and when that was.
        private readonly Dictionary<string, Save> saves = new Dictionary<string, Save>();
        private Json.Raw savesShown;
        private float savesRead = -1e9f;
        // When a load was last asked for: a second one within LoadAgainAfter is not taken, so that
        // two dashboards, or a tap twice, do not load twice; a load KSP refused can be asked again.
        private const float LoadAgainAfter = 10f;
        private float loadAskedAt = -1e9f;

        // g.scene: the scene KSP has loaded, by its GameScenes name (MAINMENU, SPACECENTER, FLIGHT ...).
        internal static string Scene
        {
            get
            {
                GameScenes scene = HighLogic.LoadedScene;
                if (sceneName == null || scene != sceneNamed)
                {
                    sceneNamed = scene;
                    sceneName = scene.ToString();
                }
                return sceneName;
            }
        }

        // The scene whose name was last made, and that name, so that the per-frame read does not format the enum each time.
        private static GameScenes sceneNamed;
        private static string sceneName;

        // As a save's summary writes it: 1.12.5.
        private static string KspVersion
        {
            get
            {
                return Versioning.fetch != null
                    ? Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision
                    : null;
            }
        }

        // KSP's own version and this mod's.
        internal static Dictionary<string, object> Versions()
        {
            return new Dictionary<string, object>
            {
                { "ksp", KspVersion },
                { "mod", typeof(MenuTelemetry).Assembly.GetName().Version.ToString(3) },
            };
        }

        // While KSP loads at its start: the step it is at, how far it is, as its loading screen's bar
        // has it, and the seconds since the start; null in every other scene.
        internal static Dictionary<string, object> Loading()
        {
            LoadingScreen screen = LoadingScreen.Instance;
            if (HighLogic.LoadedScene != GameScenes.LOADING || screen == null) return null;
            int count = screen.loaders.Count;
            float[] weights = new float[count];
            bool[] ready = new bool[count];
            float current = 0f;
            string title = null;
            for (int i = 0; i < count; i++)
            {
                LoadingSystem loader = screen.loaders[i];
                weights[i] = loader.LoadWeight();
                ready[i] = loader.IsReady();
                if (!ready[i] && title == null)
                {
                    current = loader.ProgressFraction();
                    title = loader.ProgressTitle();
                }
            }
            return new Dictionary<string, object>
            {
                { "title", title },
                { "fraction", Math.Round(LoadFraction(weights, ready, current), 3) },
                { "seconds", Math.Floor(Time.realtimeSinceStartup) },
            };
        }

        // As KSP's loading screen counts it: the loaders done before the first one not done, and that
        // one's share, by their weights over the weight of all of them.
        internal static double LoadFraction(float[] weights, bool[] ready, float currentFraction)
        {
            double total = 0, done = 0;
            bool counting = true;
            for (int i = 0; i < weights.Length; i++)
            {
                total += weights[i];
                if (!counting) continue;
                if (ready[i]) done += weights[i];
                else
                {
                    done += weights[i] * Mathf.Clamp01(currentFraction);
                    counting = false;
                }
            }
            return total > 0 ? done / total : 0;
        }

        // The saves, last played first, as one JSON array; null outside the main menu and the scenes
        // reached from it, so that no folder is read in flight.
        internal object Saves()
        {
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.MAINMENU && scene != GameScenes.SETTINGS && scene != GameScenes.CREDITS) return null;
            float now = Time.realtimeSinceStartup;
            if (savesShown == null || now - savesRead >= SavesEvery)
            {
                savesRead = now;
                savesShown = new Json.Raw(Json.Value(Read(KSPUtil.ApplicationRootPath + "saves", KspVersion, Date)));
            }
            return savesShown;
        }

        // The saves in root: every folder with a persistent.sfs, what its .loadmeta (KSP's own summary,
        // written by its Load dialog and with each save) says of it, last played first. A summary is
        // read again only when its file has changed.
        internal List<Dictionary<string, object>> Read(string root, string kspVersion, Func<double, string> date)
        {
            var list = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>();
            string[] folders;
            try { folders = Directory.GetDirectories(root); }
            catch (Exception) { return list; }
            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);
                string sfs = Path.Combine(folder, "persistent.sfs"), meta = Path.Combine(folder, "persistent.loadmeta");
                try
                {
                    if (!File.Exists(sfs)) continue;
                    DateTime sfsTime = File.GetLastWriteTimeUtc(sfs);
                    DateTime metaTime = File.Exists(meta) ? File.GetLastWriteTimeUtc(meta) : DateTime.MinValue;
                    seen.Add(name);
                    Save save;
                    if (!saves.TryGetValue(name, out save) || save.SfsTime != sfsTime || save.MetaTime != metaTime)
                        saves[name] = save = new Save
                        {
                            SfsTime = sfsTime,
                            MetaTime = metaTime,
                            Entry = Entry(name, sfsTime, metaTime != DateTime.MinValue ? File.ReadAllText(meta) : null, kspVersion, date),
                        };
                    list.Add(save.Entry);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " Dashboard: the save " + name + " could not be read: " + e.Message);
                }
            }
            foreach (string gone in new List<string>(saves.Keys))
                if (!seen.Contains(gone)) saves.Remove(gone);
            list.Sort((a, b) => ((long)b["played"]).CompareTo((long)a["played"]));
            return list;
        }

        // One save: its name and when it was last saved, and where KSP's summary is there, its mode,
        // date, funds, science, reputation, contracts and vessels, and the KSP version it was made with
        // where that is another.
        internal static Dictionary<string, object> Entry(string name, DateTime saved, string meta, string kspVersion,
                                                         Func<double, string> date)
        {
            var entry = new Dictionary<string, object>
            {
                { "name", name },
                { "played", (long)(saved - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds },
            };
            ConfigNode node = meta != null ? ConfigNode.Parse(meta) : null;
            if (node == null || !node.HasValue("gameMode")) return entry;
            entry["mode"] = node.GetValue("gameMode");
            double ut;
            if (Number(node, "UT", out ut) && ut >= 0)
            {
                entry["ut"] = ut;
                entry["date"] = date(ut);
            }
            foreach (var field in new[]
                     {
                         new[] { "funds", "funds" }, new[] { "science", "science" }, new[] { "reputationPercent", "reputation" },
                         new[] { "ongoingContracts", "contracts" }, new[] { "vesselCount", "vessels" },
                     })
            {
                double value;
                if (Number(node, field[0], out value)) entry[field[1]] = Math.Round(value);
            }
            string made = node.GetValue("kspVersion");
            if (!string.IsNullOrEmpty(made) && kspVersion != null && made != kspVersion) entry["version"] = made;
            return entry;
        }

        // A value of the summary read as a number; false where it is missing or none.
        private static bool Number(ConfigNode node, string name, out double value)
        {
            return double.TryParse(node.GetValue(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // In the game's calendar, as KSP writes a date: the Kerbin or the Earth one, or a mod's.
        private static string Date(double ut)
        {
            try { return KSPUtil.PrintDate(ut, true); }
            catch (Exception) { return null; }
        }

        // The main menu's private end of its Load dialog, which loads the save of that name; null where KSP has none.
        internal static readonly MethodInfo LoadDialogFinished = typeof(MainMenu).GetMethod("OnLoadDialogFinished",
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);

        // g.load[name]: the save loaded as KSP's Load dialog does, through the main menu's own end of
        // that dialog (its upgrade pipeline and all). The name comes URI-encoded, so that commas and
        // brackets in it survive the command's syntax.
        internal void Load(string[] args)
        {
            if (args.Length == 0) return;
            string name = Uri.UnescapeDataString(string.Join(",", args));
            if (HighLogic.LoadedScene != GameScenes.MAINMENU || Time.realtimeSinceStartup - loadAskedAt < LoadAgainAfter) return;
            if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name == "." || name == ".."
                || !File.Exists(KSPUtil.ApplicationRootPath + "saves/" + name + "/persistent.sfs"))
            {
                LogLine("no save " + name + " to load");
                return;
            }
            MainMenu menu = UnityEngine.Object.FindObjectOfType<MainMenu>();
            if (menu == null || LoadDialogFinished == null)
            {
                LogLine("the main menu cannot load a save from here (" + (menu == null ? "no main menu" : "KSP has no OnLoadDialogFinished") + ")");
                return;
            }
            loadAskedAt = Time.realtimeSinceStartup;
            LogLine("loading the save " + name);
            LoadDialogFinished.Invoke(menu, new object[] { name });
        }

        // g.quit: from the main menu only, where nothing is left unsaved.
        internal static void Quit()
        {
            if (HighLogic.LoadedScene != GameScenes.MAINMENU) return;
            LogLine("quitting KSP");
            Application.Quit();
        }

        // A line in KSP.log under the dashboard's tag.
        private static void LogLine(string message)
        {
            Debug.Log(Log.Tag + " Dashboard: " + message);
        }
    }
}
