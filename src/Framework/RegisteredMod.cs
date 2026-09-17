using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace ReDefinition.Framework
{
    // A mod bundled from its registration (docs/modders/registering-a-mod.md): its
    // settings reached through their member paths, or through a behaviour where a
    // path cannot say it.
    //
    // A mod whose detect type is not loaded is not installed, without a word. A
    // setting whose member is not there is left out -- reported, unless it is
    // `optional`, whose reason is kept instead -- and the rest of the mod stays. A
    // mod without one of its `needs`, or without the member of a `required`
    // setting, is a settings object of another shape, and is not bundled at all.
    internal sealed class RegisteredMod : IBundledMod
    {
        private const BindingFlags Any = HostStack.Any;

        private readonly ModRegistration registration;
        private readonly ModFolder folder;
        private readonly Dictionary<string, ModBehaviour> behaviours = new Dictionary<string, ModBehaviour>();
        private readonly ModBehaviour modBehaviour;
        private readonly MemberPath save;
        private readonly MemberPath ready;
        private readonly string build;
        private readonly List<BundledSetting> settings = new List<BundledSetting>();
        private readonly List<string> dropped = new List<string>();
        // A dropped line names what went, then why: the whole mod by this name.
        internal const string WholeMod = "(the whole mod)";
        internal const string DropSeparator = " -- ";
        private bool wholeModDropped;
        private readonly string title;
        // The member of a `required` setting is not there.
        private bool lostRequired;

        public ModRegistration Registration
        {
            get { return registration; }
        }

        // The behaviour the registration names for the whole mod, if any.
        internal ModBehaviour MainBehaviour
        {
            get { return modBehaviour; }
        }

        // Whether its detect type is loaded -- bundled or not.
        internal bool Detected { get; private set; }

        // Whether the mod's code is loaded: its detect type, or an assembly of it
        // without that type (LoadedAssemblyOf). Its own code runs either way.
        internal bool AssemblyLoaded { get; private set; }

        internal ModFolder Folder
        {
            get { return folder; }
        }

        public string Id
        {
            get { return registration.Name; }
        }

        public string ModName
        {
            get { return title; }
        }

        public string ButtonAssembly
        {
            get { return registration.Button; }
        }

        public string ToolbarControlNamespace
        {
            get { return registration.ToolbarControl; }
        }

        public SettingsSaving Saving
        {
            get { return registration.Saving; }
        }

        public bool IsInstalled { get; private set; }

        public IList<BundledSetting> Settings
        {
            get { return settings; }
        }

        public IList<string> DroppedMembers
        {
            get { return dropped; }
        }

        public string OwnWindow { get; private set; }

        public Type OwnWindowType { get; private set; }

        public string Version { get; private set; }

        public string Build
        {
            get
            {
                string told = modBehaviour != null ? modBehaviour.Build(this) : null;
                return told ?? build;
            }
        }

        // `leftOutWith` is applied once every mod is built (LeaveOutHeld).
        public RegisteredMod(ModRegistration registration, List<string> problems)
        {
            this.registration = registration;
            title = Localized(registration.Title);
            Type detect = TypeLookup.Find(registration.Detect);
            if (detect == null)
            {
                // Its assembly loaded all the same: a build without that type, said in
                // the log and the diagnostics. Not detected either way.
                string loaded = LoadedAssemblyOf(registration);
                if (loaded == null) return;
                AssemblyLoaded = true;
                Drop(WholeMod, "this build has no " + registration.Detect + " (its assembly " + loaded + " is loaded)");
                return;
            }
            AssemblyLoaded = true;
            Detected = true;
            folder = ModFolder.Of(detect.Assembly);
            VersionFrom(detect);
            string where = "Mod '" + registration.Name + "'";

            foreach (BuildRegistration candidate in registration.Builds)
            {
                if (!MemberPath.Exists(candidate.Has, folder)) continue;
                build = candidate.Name;
                break;
            }

            foreach (string needed in registration.Needs)
            {
                if (MemberPath.Exists(needed, folder)) continue;
                Drop(WholeMod, "this build of " + ModName + " has no " + needed
                                        + ", which its settings cannot be trusted without");
                return;
            }

            if (registration.Behaviour != null)
            {
                modBehaviour = MakeBehaviour(registration.Behaviour, where, problems);
                if (modBehaviour == null)
                {
                    Drop(WholeMod, "no behaviour '" + registration.Behaviour + "' in this ReDefinition");
                    return;
                }
                // A behaviour that cannot hook in says why where it knows. The whole
                // mod goes with it, said so where the behaviour named only a setting
                // or nothing, so the log tells why all its rows are gone after an
                // update of the mod.
                int droppedBefore = dropped.Count;
                if (!modBehaviour.Attach(this))
                {
                    if (!wholeModDropped)
                        Drop(WholeMod, "this build of " + ModName + " lacks what ReDefinition's "
                                                + registration.Behaviour + " behaviour hooks into"
                                                + (dropped.Count > droppedBefore ? ", named above" : ""));
                    return;
                }
                // Empty: the behaviour knows the version cannot be told here.
                string version = modBehaviour.Version(this);
                if (version != null) Version = version.Length > 0 ? version : null;
            }

            if (registration.Save != null)
            {
                string problem;
                save = MemberPath.Resolve(registration.Save, folder, out problem);
                if (save == null || !save.IsMethod)
                {
                    Drop(WholeMod, "this build of " + ModName + " has no " + registration.Save
                                            + " to save its settings with");
                    return;
                }
            }

            if (registration.Ready != null)
            {
                string problem;
                ready = MemberPath.ResolveReadable(registration.Ready, folder, out problem);
                if (ready != null && ready.IsMethod)
                {
                    // A mistake of the registration, not a build of another
                    // shape.
                    problems.Add(where + ": ready: " + registration.Ready + " is a method, not a value -- not bundled.");
                    Drop(WholeMod, "ready names a method, " + registration.Ready + ", not a value");
                    return;
                }
                if (ready == null)
                {
                    Drop(WholeMod, "this build of " + ModName + " has no " + registration.Ready
                                            + " to tell when it can take values");
                    return;
                }
            }

            foreach (SettingRegistration setting in registration.Settings) AddFrom(setting, where, problems);
            if (lostRequired)
            {
                Drop(WholeMod, "this build of " + ModName + " lacks a member it cannot be bundled without");
                return;
            }
            if (modBehaviour != null) modBehaviour.Complete(this);
            if (ready != null) WaitForReady();
            WindowFrom(where, problems);
            IsInstalled = settings.Count > 0;
        }

        public void Save()
        {
            // Not before the mod can take values: its save would do nothing, and what
            // waited for it would count as saved.
            if (ready != null && !IsReady())
                throw new InvalidOperationException(ModName + " cannot save its settings yet: " + registration.Ready
                                                    + " holds nothing.");
            if (modBehaviour != null) modBehaviour.Save(this);
            if (save != null) save.Invoke();
        }

        public void InstallHooks()
        {
            if (modBehaviour != null) modBehaviour.InstallHooks(this);
        }

        // Whether a game is loaded: KSP has none in the main menu, which sets
        // HighLogic.CurrentGame to null (MainMenu, decompiled).
        internal static bool InGame()
        {
            return HighLogic.CurrentGame != null && HighLogic.LoadedScene != GameScenes.MAINMENU;
        }

        // The save a per-save mod keeps its values for now, by the folder KSP
        // names it with; empty outside a save.
        internal static string LoadedSave()
        {
            return InGame() ? HighLogic.SaveFolder ?? "" : "";
        }

        // Settings another mod holds for itself (`leftOutWith`): left out while that
        // mod is loaded, bundled or not -- its own code holds them either way. Once
        // every mod is built, whatever their order.
        internal void LeaveOutHeld(Func<string, bool> loaded)
        {
            if (!IsInstalled) return;
            foreach (SettingRegistration entry in registration.Settings)
            {
                if (entry.LeftOutWith == null || !loaded(entry.LeftOutWith)) continue;
                string key = Id + "." + entry.Name;
                for (int i = settings.Count - 1; i >= 0; i--)
                    if (settings[i].Key == key) settings.RemoveAt(i);
                Drop(entry.Name, "left to " + entry.LeftOutWith + ", which holds it itself");
            }
            IsInstalled = settings.Count > 0;
        }

        // A setting this build of the mod does not offer, or one that cannot be
        // set without its file being written: no row for it, the rest of the mod
        // bundled as before, and the reason kept -- BundledSettings logs it once
        // and the check outside the game reports it. No logging here: mods are
        // built outside the game too, where Unity's logging is not there to call.
        internal void Drop(string name, string why)
        {
            if (name == WholeMod) wholeModDropped = true;
            string line = name + DropSeparator + why;
            if (!dropped.Contains(line)) dropped.Add(line);
        }

        // For behaviours and member paths alike: the member a setting names is not
        // in this build. Left out with the `optional` reason, without a word for
        // `True`; a `required` one takes the mod with it.
        internal void MemberMissing(SettingRegistration entry, string why)
        {
            if (!IsTrue(entry.Optional)) Drop(entry.Name, entry.Optional ?? why);
            if (entry.Required) lostRequired = true;
        }

        internal bool Has(string name)
        {
            string key = Id + "." + name;
            foreach (BundledSetting setting in settings)
                if (setting.Key == key) return true;
            return false;
        }

        // A setting a behaviour finds on its own -- a field Scatterer saves that no
        // registration names yet: kept, reset and set by profiles, without a row.
        internal BundledSetting AddFound(string name, ApplyWindow window, Func<string> read, Action<string> write, Type type)
        {
            BundledSetting setting = Add(name, name, window, "", read, write);
            ControlFor(setting, type);
            return setting;
        }

        private void AddFrom(SettingRegistration entry, string modWhere, List<string> problems)
        {
            string where = modWhere + ", setting '" + entry.Name + "'";
            if (entry.LeftOut != null)
            {
                Drop(entry.Name, entry.LeftOut);
                return;
            }

            ModBehaviour behaviour = modBehaviour;
            if (entry.Behaviour != null)
            {
                behaviour = MakeBehaviour(entry.Behaviour, where, problems);
                if (behaviour == null)
                {
                    Drop(entry.Name, "no behaviour '" + entry.Behaviour + "' in this ReDefinition");
                    return;
                }
            }

            Func<string> read;
            Action<string> write;
            Type type;
            if (behaviour != null && behaviour.Reach(this, entry, out read, out write, out type))
            {
                if (read == null) return;
            }
            else if (!Through(entry, where, problems, out read, out write, out type))
            {
                return;
            }

            // A build where the member changed its type would take the reset and the
            // profiles it refuses on every apply -- EVE's upscaling as a number,
            // written its steps' names.
            if (!Parses(entry, type))
            {
                string problem = "its default '" + entry.Default + "' is no value of its " + type.Name;
                problems.Add(where + ": " + problem + " -- left out.");
                Drop(entry.Name, problem);
                return;
            }

            ApplyWindow window = entry.TakesEffect;
            if (entry.ShaderGlobal != null || entry.After != null)
                write = FollowedUp(entry, type, where, problems, write, ref window);

            BundledSetting setting = Add(entry.Name, Localized(entry.Title), window, Localized(entry.Tooltip), read, write);
            setting.Kind = entry.Kind;
            if (entry.Min != null && entry.Max != null)
            {
                Slider(setting, entry.Min.Value, entry.Max.Value, entry.Whole ?? type == typeof(int));
            }
            else if (entry.Choices != null)
            {
                Choice(setting, entry.Choices, entry.Labels != null ? Array.ConvertAll(entry.Labels, Localized) : null);
            }
            else if (type != null && type != typeof(object))
            {
                ControlFor(setting, type);
            }
            else if (!IsBool(entry.Default))
            {
                // A member typed object whose value is not there yet: checked as a
                // number where its default is one.
                setting.Control = SettingControl.Value;
                setting.ValueType = IsNumber(entry.Default) ? typeof(double) : null;
            }
            if (registration.Saving == SettingsSaving.PerSave && entry.PerSave != false) setting.Context = LoadedSave;
            if (behaviour != null) behaviour.Finish(this, setting, entry);
        }

        // Through the member path, as the registration names it.
        private bool Through(SettingRegistration entry, string where, List<string> problems, out Func<string> read,
                             out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            if (entry.Member == null)
            {
                problems.Add(where + ": no member, and no behaviour reaches it -- left out.");
                Drop(entry.Name, "no member");
                return false;
            }
            string problem;
            MemberPath path = MemberPath.Resolve(entry.Member, folder, out problem);
            if (path != null && path.IsMethod)
            {
                problem = entry.Member + " is a method, not a value";
                path = null;
            }
            if (path == null)
            {
                if (entry.Optional == null) problems.Add(where + ": " + problem + " -- left out.");
                MemberMissing(entry, problem);
                return false;
            }

            // A member typed object -- Firefly's indexer -- has the type of the
            // value it holds.
            type = path.ValueType;
            if (type == typeof(object))
            {
                object now = SafeGet(path);
                type = now != null ? now.GetType() : typeof(object);
            }
            if (type != typeof(object) && !SettingValues.CanParse(type))
            {
                problem = "a " + type.Name + " cannot be set from text";
                problems.Add(where + ": " + problem + " -- left out.");
                Drop(entry.Name, problem);
                return false;
            }

            MemberPath member = path;
            bool invert = entry.Invert;
            Type declared = type;
            read = () =>
            {
                object value = member.Get();
                string text = value != null ? SettingValues.Text(value) : null;
                return invert ? SettingValues.Invert(text) : text;
            };
            write = text =>
            {
                Type target = declared;
                if (target == typeof(object))
                {
                    object now = member.Get();
                    if (now == null) throw new InvalidOperationException(member.Text + " is not there to set.");
                    target = now.GetType();
                }
                member.Set(SettingValues.Parse(invert ? SettingValues.Invert(text) : text, target));
            };
            return true;
        }

        // A shader global set with the value, and a call once at the end of the
        // frame, however many settings ask for the same.
        private Action<string> FollowedUp(SettingRegistration entry, Type type, string where, List<string> problems,
                                          Action<string> write, ref ApplyWindow window)
        {
            MemberPath call = null;
            if (entry.After != null)
            {
                string problem;
                call = MemberPath.Resolve(entry.After, folder, out problem);
                if (call != null && !call.IsMethod)
                {
                    problem = entry.After + " is no method";
                    call = null;
                }
                if (call == null)
                {
                    problems.Add(where + ": after: " + problem + " -- ignored.");
                    // The row promises only what the call that is there can keep: the
                    // mod takes the value when it next reads it.
                    if (window == ApplyWindow.Live) window = ApplyWindow.NextScene;
                }
            }
            string global = entry.ShaderGlobal;
            if (global != null && (entry.Invert || !(type == typeof(float) || type == typeof(double) || type == typeof(int))))
            {
                problems.Add(where + ": shaderGlobal takes a number that reads the right way round -- ignored.");
                global = null;
            }
            string key = "after-" + Id + "-" + entry.After;
            MemberPath follow = call;
            string after = entry.After;
            string modName = ModName;
            // A call that fails is logged rather than thrown into the write already
            // done -- with no planet loaded Parallax has nothing to update.
            Action run = () =>
            {
                try
                {
                    follow.Invoke();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn(key, modName + "'s " + after + " did not run after a change ("
                                               + CompatibilityLog.Reason(e) + "); the change takes effect when "
                                               + modName + " next reads it.");
                }
            };
            return text =>
            {
                // The global's value first: a text that is no number must not leave
                // the member written and the rest undone.
                float value = global != null ? Convert.ToSingle(SettingValues.Parse(text, typeof(double))) : 0f;
                write(text);
                if (global != null) UnityEngine.Shader.SetGlobalFloat(global, value);
                if (follow != null) BundledSettingsAddon.AtEndOfFrame(key, run);
            };
        }

        // Its own settings window, only from a type of its folder -- ModWindowClose
        // hooks that type -- and checked against the build installed: a method that
        // is not there is dropped, and the mod gets no Advanced button and no close
        // button.
        private void WindowFrom(string where, List<string> problems)
        {
            if (registration.Window == null) return;
            string buttonStays = registration.Button != null || registration.ToolbarControl != null
                ? " -- its toolbar button stays"
                : "";
            int dot = registration.Window.LastIndexOf('.');
            if (dot <= 0)
            {
                problems.Add(where + ": window '" + registration.Window + "' is not Type.Method -- ignored.");
                return;
            }
            string typeName = registration.Window.Substring(0, dot);
            string method = registration.Window.Substring(dot + 1);
            Type type = MemberPath.FindType(typeName, folder);
            if (type == null)
            {
                problems.Add(where + ": window: no type " + typeName + " from the mod's folder -- ignored.");
                Drop("(own window)", "this build has no " + registration.Window + " drawing its own window" + buttonStays);
                return;
            }
            if (!Array.Exists(type.GetMethods(Any), m => m.Name == method))
            {
                Drop("(own window)", "this build has no " + typeName + "." + method + " drawing its own window"
                                     + buttonStays);
                return;
            }
            OwnWindow = typeName + "." + method;
            OwnWindowType = type;
        }

        // The assembly of a mod whose detect type is not loaded, where one is: from
        // GameData, and named as its button's assembly or as the detect type's first
        // namespace -- Scatterer.dll for Scatterer.Scatterer. Null otherwise; KSP's
        // and Unity's own assemblies never count.
        private static string LoadedAssemblyOf(ModRegistration registration)
        {
            int dot = registration.Detect != null ? registration.Detect.IndexOf('.') : -1;
            string root = dot > 0 ? registration.Detect.Substring(0, dot) : null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (!string.Equals(name, registration.Button, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, root, StringComparison.OrdinalIgnoreCase))
                    continue;
                string location;
                try
                {
                    location = assembly.IsDynamic ? null : assembly.Location;
                }
                catch (Exception)
                {
                    location = null;
                }
                if (InGameData(location)) return name;
            }
            return null;
        }

        // Whether a file lies under a GameData folder.
        internal static bool InGameData(string location)
        {
            return location != null
                   && location.Replace('\\', '/').IndexOf("/GameData/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // `ready`: while its member holds nothing, or False, the mod's settings are
        // not there to read or set -- Trajectories' before the first flight of a
        // run, whose members read 0 and whose save does nothing. Nothing is read
        // from the mod then, so nothing counts as its value from before ReDefinition,
        // and what is set waits in the store until the mod can take it.
        private void WaitForReady()
        {
            foreach (BundledSetting setting in settings)
            {
                Func<bool> applicable = setting.Applicable;
                Func<string> read = setting.Read;
                setting.Applicable = applicable == null ? (Func<bool>)IsReady : () => IsReady() && applicable();
                setting.Read = () => IsReady() ? read() : null;
            }
        }

        private bool IsReady()
        {
            object value = SafeGet(ready);
            UnityEngine.Object unity = value as UnityEngine.Object;
            if (value == null || (!ReferenceEquals(unity, null) && unity == null)) return false;
            return !(value is bool) || (bool)value;
        }

        private ModBehaviour MakeBehaviour(string name, string where, List<string> problems)
        {
            ModBehaviour behaviour;
            if (behaviours.TryGetValue(name, out behaviour)) return behaviour;
            behaviour = ModBehaviours.Create(name);
            if (behaviour == null) problems.Add(where + ": no behaviour '" + name + "' in this ReDefinition -- left out.");
            behaviours[name] = behaviour;
            return behaviour;
        }

        private BundledSetting Add(string name, string title, ApplyWindow window,
                                   string tooltip, Func<string> read, Action<string> write)
        {
            BundledSetting setting = new BundledSetting
            {
                Key = Id + "." + name,
                Title = title,
                Control = SettingControl.Toggle,
                Window = window,
                Tooltip = tooltip,
                Read = read,
                Write = write,
                Owner = this,
            };
            settings.Add(setting);
            return setting;
        }

        private static void Slider(BundledSetting setting, float min, float max, bool wholeNumbers)
        {
            setting.Control = SettingControl.Slider;
            setting.Min = min;
            setting.Max = max;
            setting.WholeNumbers = wholeNumbers;
        }

        private static void Choice(BundledSetting setting, string[] choices, string[] labels)
        {
            setting.Control = SettingControl.Choice;
            setting.Choices = choices;
            setting.ChoiceLabels = labels;
        }

        // For a setting the window does not show: a switch for a bool, the names
        // of an enum, else a value checked by its type.
        private static void ControlFor(BundledSetting setting, Type type)
        {
            if (type == typeof(bool))
            {
                setting.Control = SettingControl.Toggle;
                return;
            }
            if (type.IsEnum)
            {
                Choice(setting, Enum.GetNames(type), null);
                return;
            }
            setting.Control = SettingControl.Value;
            setting.ValueType = type;
        }

        // From the assembly the mod's type lives in: its file version, as Windows
        // shows it, where it names one; else its informational or its assembly
        // version.
        private void VersionFrom(Type type)
        {
            Assembly assembly = type.Assembly;
            object[] file = assembly.GetCustomAttributes(typeof(AssemblyFileVersionAttribute), false);
            string version = file.Length > 0 ? ((AssemblyFileVersionAttribute)file[0]).Version : null;
            if (string.IsNullOrEmpty(version) || version == "0.0.0.0")
            {
                object[] informational = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                version = informational.Length > 0 ? ((AssemblyInformationalVersionAttribute)informational[0]).InformationalVersion : null;
            }
            if (string.IsNullOrEmpty(version) || version == "0.0.0.0") version = assembly.GetName().Version.ToString();
            Version = version;
        }

        // Whether the setting's own default is a value of its member's type -- known
        // only for a type Parse takes.
        private static bool Parses(SettingRegistration entry, Type type)
        {
            if (entry.Default == null || type == null || type == typeof(object) || !SettingValues.CanParse(type)) return true;
            try
            {
                SettingValues.Parse(entry.Invert ? SettingValues.Invert(entry.Default) : entry.Default, type);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static object SafeGet(MemberPath path)
        {
            try
            {
                return path.Get();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsBool(string text)
        {
            bool value;
            return text != null && bool.TryParse(text, out value);
        }

        private static bool IsNumber(string text)
        {
            double value;
            return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // A title, tooltip or label may be a localization tag -- KSP's own
        // #autoLOC_... or a mod's #LOC_...: shown in the player's language. Where KSP's
        // Localizer is not there, outside the game, the tag stands.
        private static string Localized(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '#') return text;
            try
            {
                return KSP.Localization.Localizer.Format(text);
            }
            catch (Exception)
            {
                return text;
            }
        }

        // `optional = True`: left out without a word.
        internal static bool IsTrue(string text)
        {
            bool value;
            return text != null && bool.TryParse(text, out value) && value;
        }
    }
}
