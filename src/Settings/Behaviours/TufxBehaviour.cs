using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System;
using ReDefinition.Core;
using ReDefinition.Upscaler;

namespace ReDefinition.Settings.Behaviours
{
    // TUFX applies a post-processing profile per scene, at every scene load and
    // change of camera mode: in the main menu the one in its configuration
    // (TUFX_CONFIGURATION), elsewhere the one in the loaded save's game parameters
    // (TUFXGameSettings), and with no save loaded the configuration's
    // (GetProfileNameForScene). Its window's choice goes through
    // ChangeProfileForScene: for the main menu into its configuration, whose file
    // it saves; for any other scene into the save, which KSP writes when it saves
    // the game.
    //
    // A choice here goes the same way: the main menu's into the configuration,
    // every other scene's into the loaded save -- so only with a save loaded. That
    // choice is for the game, not for one save: BundledSettings keeps it and sets
    // it into every save as that save loads. The configuration's other scenes,
    // which only a new save starts from, stay TUFX's. Where its scene is shown the
    // profile is applied at once, through TUFX's private ApplyProfile. A choice
    // made in TUFX's own window becomes the choice here (a Harmony postfix on
    // ChangeProfileForScene). Its profiles are known only from the running mod, so
    // a choice waits until TUFX has loaded them.
    //
    // Utils.GetCurrentScene calls everything it does not know Flight -- the loading
    // screen, KSP's settings screen -- so a profile is applied at once only in the
    // five scenes TUFX has profiles for.
    internal sealed class TufxBehaviour : ModBehaviour
    {
        private const string HarmonyId = "ReDefinition.TufxHooks";
        private const BindingFlags Any = HostStack.Any;

        // The mod the hook reports to; one per run.
        private static RegisteredMod hooked;

        private FieldInfo instance;
        private PropertyInfo profiles;
        private MethodInfo applyProfile;
        private MethodInfo profileByName;
        private MethodInfo nameForScene;
        private MethodInfo changeProfile;
        private MethodInfo currentScene;
        private FieldInfo configuration;
        private FieldInfo configurationUrl;
        private FieldInfo mainMenuProfile;
        private string configurationNode;
        private Type gameSettings;
        private MethodInfo setProfileName;
        private Type sceneType;
        private object mainMenuScene;
        // The profile TUFX applies for a name it does not have
        // (Configuration.EMPTY_PROFILE_NAME, GetProfileByName).
        private string emptyProfile;
        private bool configurationChanged;

        public override bool Attach(RegisteredMod mod)
        {
            Type loader = TypeLookup.Find("TUFX.TexturesUnlimitedFXLoader");
            Type utils = TypeLookup.Find("TUFX.Utils");
            Type profile = TypeLookup.Find("TUFX.TUFXProfile");
            gameSettings = TypeLookup.Find("TUFX.TUFXGameSettings");
            sceneType = TypeLookup.Find("TUFX.TUFXScene");
            if (loader == null || utils == null || profile == null || gameSettings == null || sceneType == null) return false;

            instance = loader.GetField("INSTANCE", Any);
            profiles = loader.GetProperty("Profiles", Any);
            applyProfile = loader.GetMethod("ApplyProfile", Any, null, new[] { profile, sceneType }, null);
            nameForScene = loader.GetMethod("GetProfileNameForScene", Any, null, new[] { sceneType }, null);
            currentScene = utils.GetMethod("GetCurrentScene", Any, null, Type.EmptyTypes, null);
            if (instance == null || profiles == null || applyProfile == null || nameForScene == null || currentScene == null)
                return false;

            changeProfile = loader.GetMethod("ChangeProfileForScene", Any, null, new[] { typeof(string), sceneType }, null);
            profileByName = loader.GetMethod("GetProfileByName", Any, null, new[] { typeof(string) }, null);
            configuration = loader.GetField("defaultConfiguration", Any);
            configurationUrl = loader.GetField("defaultConfigUrl", Any);
            setProfileName = gameSettings.GetMethod("SetProfileName", Any, null, new[] { typeof(string), sceneType }, null);
            FieldInfo nodeName = configuration != null ? configuration.FieldType.GetField("NODE_NAME", Any) : null;
            configurationNode = nodeName != null ? nodeName.GetValue(null) as string : null;
            // The field Configuration.GetProfileName answers the main menu from.
            mainMenuProfile = configuration != null ? configuration.FieldType.GetField("MainMenuProfile", Any) : null;
            if (changeProfile == null || configuration == null || !configuration.IsStatic || configurationUrl == null
                || !configurationUrl.IsStatic || setProfileName == null || string.IsNullOrEmpty(configurationNode)
                || mainMenuProfile == null || mainMenuProfile.IsStatic || mainMenuProfile.FieldType != typeof(string))
            {
                mod.Drop(RegisteredMod.WholeMod, "this build of TUFX keeps its scene profiles where this was not written to"
                                                  + " save them");
                return false;
            }

            mainMenuScene = SceneValue("MainMenu");
            FieldInfo empty = configuration.FieldType.GetField("EMPTY_PROFILE_NAME", Any);
            emptyProfile = empty != null && empty.IsStatic ? empty.GetValue(null) as string : null;
            return true;
        }

        private const string AmbientOcclusionCheck = "TufxAmbientOcclusion";

        // TUFX is the same build either way; its defaults are Volumetric Clouds'
        // author's where that is installed -- the author's profile in every scene.
        // Volumetric Clouds: EVE's build with its cloud quality manager, told by
        // that type, not by a folder a pack can rename -- a type of another mod's
        // folder, which no path of TUFX's reaches.
        public override string Build(RegisteredMod mod)
        {
            return TypeLookup.Find("Atmosphere.RaymarchedCloudsQualityManager") != null ? "volumetric" : "public";
        }

        // Volumetric Clouds' requirement on the profile in flight (EVE's
        // registration): whether TUFX renders that profile with ambient occlusion.
        public override bool? Check(RegisteredMod mod, string check, string value)
        {
            return check == AmbientOcclusionCheck ? HasEffect(value, "AmbientOcclusion") : null;
        }

        public override bool Provides(string check)
        {
            return check == AmbientOcclusionCheck;
        }

        public override bool Reach(RegisteredMod mod, SettingRegistration setting, out Func<string> read,
                                   out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            if (setting.Name == "ShowToolbarButton") return ToolbarSwitch(mod, out read, out write, out type);
            if (!setting.Name.StartsWith("profile", StringComparison.Ordinal)) return false;

            string sceneName = setting.Name.Substring("profile".Length);
            object scene = SceneValue(sceneName);
            if (scene == null)
            {
                // Under the setting's name, as every drop.
                mod.MemberMissing(setting, "this build of TUFX has no profile scene " + sceneName);
                return true;
            }
            read = () => Read(scene);
            write = text => Write(scene, text);
            type = typeof(string);
            return true;
        }

        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            if (registration.Name == "ShowToolbarButton")
            {
                setting.Applicable = () => configuration.GetValue(null) != null;
                return;
            }
            setting.Control = SettingControl.Choice;
            setting.ValueType = null;
            setting.ChoicesSource = ProfileNames;
            if (registration.Name == "profileMainMenu") setting.Applicable = HasProfiles;
            else setting.Applicable = () => HasProfiles() && RegisteredMod.InGame();
        }

        // The configuration's file, as TUFX's own window saves it for the main menu
        // (ChangeProfileForScene). A save's choices KSP writes as it saves the game.
        public override void Save(RegisteredMod mod)
        {
            if (!configurationChanged) return;
            UrlDir.UrlConfig url = configurationUrl.GetValue(null) as UrlDir.UrlConfig;
            if (url == null) throw new InvalidOperationException("TUFX has no " + configurationNode + " file to save into");

            ConfigNode node = new ConfigNode(configurationNode);
            ConfigNode.CreateConfigFromObject(configuration.GetValue(null), 0, node);
            url.config = node;
            url.parent.SaveConfigs();
            configurationChanged = false;
        }

        public override void InstallHooks(RegisteredMod mod)
        {
            HarmonyHooks.Install(HarmonyId, typeof(TufxBehaviour), new[]
            {
                new HarmonyHook(changeProfile, null, nameof(ChangeProfilePostfix), null),
            });
            hooked = mod;
        }

        // Whether TUFX renders the profile of that name with the effect of that type
        // switched on: the profile it has loaded (TUFXProfile.Settings, each effect
        // with its `enabled`), so a change made in its window counts, or -- as it
        // applies a name it does not have -- its empty one (GetProfileByName). Null
        // while its profiles are not loaded, or where they are not the shape this
        // was written against.
        internal bool? HasEffect(string profileName, string effectType)
        {
            IDictionary all = All();
            if (all == null || all.Count == 0) return null;
            object profile = !string.IsNullOrEmpty(profileName) && all.Contains(profileName) ? all[profileName] : null;
            if (profile == null && emptyProfile != null && all.Contains(emptyProfile)) profile = all[emptyProfile];
            if (profile == null) return false;

            FieldInfo list = profile.GetType().GetField("Settings", BindingFlags.Public | BindingFlags.Instance);
            IEnumerable effects = list != null ? list.GetValue(profile) as IEnumerable : null;
            if (effects == null) return null;
            foreach (object effect in effects)
            {
                if (effect == null || effect.GetType().Name != effectType) continue;
                FieldInfo enabled = effect.GetType().GetField("enabled", BindingFlags.Public | BindingFlags.Instance);
                object parameter = enabled != null ? enabled.GetValue(effect) : null;
                FieldInfo value = parameter != null
                    ? parameter.GetType().GetField("value", BindingFlags.Public | BindingFlags.Instance)
                    : null;
                if (value == null) return null;
                object on = value.GetValue(parameter);
                if (on is bool && (bool)on) return true;
            }
            return false;
        }

        // Whether TUFX shows its button, from its configuration -- for the reset, not
        // shown in the window, whose bundling hides the button either way. Read as
        // TUFX starts.
        private bool ToolbarSwitch(RegisteredMod mod, out Func<string> read, out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            FieldInfo field = configuration.FieldType.GetField("ShowToolbarButton", Any);
            if (field == null || field.IsStatic || field.FieldType != typeof(bool))
            {
                mod.Drop("ShowToolbarButton", "this build of TUFX has no such switch in its configuration");
                return true;
            }
            read = () =>
            {
                object defaults = configuration.GetValue(null);
                return defaults != null ? SettingValues.Text(field.GetValue(defaults)) : null;
            };
            write = text =>
            {
                object defaults = configuration.GetValue(null);
                if (defaults == null) throw new InvalidOperationException("TUFX has no configuration to set it in.");
                bool value = (bool)SettingValues.Parse(text, typeof(bool));
                if ((bool)field.GetValue(defaults) == value) return;
                field.SetValue(defaults, value);
                configurationChanged = true;
            };
            type = typeof(bool);
            return true;
        }

        // A profile chosen in TUFX's own window, which has set, saved and applied it:
        // the choice here follows.
        private static void ChangeProfilePostfix(object[] __args)
        {
            RegisteredMod mod = hooked;
            if (mod == null || __args == null || __args.Length < 2 || __args[1] == null) return;
            HarmonyHooks.Safely("tufx-window", "A profile chosen in TUFX's own window did not reach ReDefinition's window",
                () => BundledSettings.TakeFromMod(BundledSettings.Find(mod.Id + ".profile" + __args[1]), __args[0] as string));
        }

        private object Loader()
        {
            return instance.GetValue(null);
        }

        private static bool TufxScene()
        {
            GameScenes loaded = HighLogic.LoadedScene;
            return loaded == GameScenes.MAINMENU || loaded == GameScenes.SPACECENTER || loaded == GameScenes.EDITOR
                   || loaded == GameScenes.FLIGHT || loaded == GameScenes.TRACKSTATION;
        }

        private object SceneValue(string sceneName)
        {
            try
            {
                return Enum.Parse(sceneType, sceneName);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // Whether this is the scene TUFX is showing -- then a profile for it is
        // applied at once.
        private bool CurrentScene(object scene)
        {
            object loader = Loader();
            return loader != null && TufxScene() && Equals(currentScene.Invoke(null, null), scene);
        }

        // TUFX's own choice for the scene, as it applies it: the loaded save's, or
        // its configuration's. An empty one is a choice too.
        private string Read(object scene)
        {
            object loader = Loader();
            if (loader == null) return null;
            return nameForScene.Invoke(loader, new[] { scene }) as string ?? "";
        }

        // Where TUFX's own window puts a choice: the main menu's into the
        // configuration, every other scene's into the loaded save; then applied as
        // TUFX applies it, which falls back to its empty profile.
        private void Write(object scene, string name)
        {
            if (name == null) throw new ArgumentNullException("name");

            if (Equals(scene, mainMenuScene))
            {
                object defaults = configuration.GetValue(null);
                if (defaults == null) throw new InvalidOperationException("TUFX has no configuration to set it in.");
                if (!string.Equals(mainMenuProfile.GetValue(defaults) as string, name, StringComparison.Ordinal))
                {
                    mainMenuProfile.SetValue(defaults, name);
                    configurationChanged = true;
                }
            }
            else
            {
                Game game = HighLogic.CurrentGame;
                object settings = game != null && game.Parameters != null ? game.Parameters.CustomParams(gameSettings) : null;
                if (settings == null) throw new InvalidOperationException("The loaded save has no TUFX settings to set it in.");
                setProfileName.Invoke(settings, new[] { name, scene });
            }
            if (CurrentScene(scene)) Apply(scene, name);
        }

        private IDictionary All()
        {
            object loader = Loader();
            return loader != null ? profiles.GetValue(loader, null) as IDictionary : null;
        }

        private bool HasProfiles()
        {
            IDictionary all = All();
            return all != null && all.Count > 0;
        }

        // As TUFX's own ChangeProfileForScene applies a profile. A build without its
        // private GetProfileByName gets the profile from its list.
        private void Apply(object scene, string name)
        {
            object loader = Loader();
            IDictionary all = profileByName == null ? All() : null;
            object profile = profileByName != null
                ? profileByName.Invoke(loader, new object[] { name })
                : all != null && all.Contains(name) ? all[name] : null;
            applyProfile.Invoke(loader, new[] { profile, scene });
            HostStack.ReassertNow();
        }

        private string[] ProfileNames()
        {
            IDictionary all = All();
            if (all == null) return new string[0];

            List<string> names = new List<string>();
            foreach (object key in all.Keys)
            {
                string name = key as string;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }
    }
}
