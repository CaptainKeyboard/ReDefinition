using System.Reflection;
using System;
using ReDefinition.Core;
using ReDefinition.Upscaler;

namespace ReDefinition.Settings.Behaviours
{
    // Its main settings are a MainSettingsReadWrite on Scatterer.Instance
    // (mainSettings), loaded in every scene's Awake from the Scatterer_config node
    // in the GameDatabase (loadMainSettings). Its window changes the running object;
    // saveMainSettingsIfChanged, at every scene end, replaces the node with it and
    // saves the node's file where one of a fixed list of fields differs.
    //
    // A value set here goes into the running object, where Scatterer runs in this
    // scene, and into the node, whose file is then saved -- so its window and the
    // next scene see it. Not through that routine: its list leaves out the ocean's
    // screen-space reflections and the cloud light shafts with their steps, and it
    // would save whatever else differs in the running object as well. Only fields
    // Scatterer saves are offered: its own save writes the node anew from its
    // [Persistent] fields and would drop any other value put there. A field it
    // saves that no registration names yet is bundled all the same, without a row.
    internal sealed class ScattererBehaviour : ModBehaviour
    {
        private const string NodeName = "Scatterer_config";

        private PropertyInfo instance;
        private FieldInfo mainSettings;
        private FieldInfo isActive;
        private Type settingsType;
        private bool nodeChanged;
        // The node read as Scatterer reads it, once per node rather than once per
        // field; Scatterer's own save puts a new node in.
        private ConfigNode parsedNode;
        private object parsed;

        // Its window's keys are not among the main settings: they stand on
        // Scatterer.Instance.pluginData, each as a KeyCode field it reads and a
        // string field it saves (PluginDataReadWrite). Its window opens on either
        // modifier with either key.
        private FieldInfo pluginData;
        private MethodInfo savePluginData;
        private bool pluginDataChanged;

        public override bool Attach(RegisteredMod mod)
        {
            Type scatterer = TypeLookup.Find("Scatterer.Scatterer");
            settingsType = TypeLookup.Find("Scatterer.MainSettingsReadWrite");
            if (scatterer == null || settingsType == null) return false;
            instance = scatterer.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            mainSettings = scatterer.GetField("mainSettings", HostStack.Any);
            isActive = scatterer.GetField("isActive", HostStack.Any);
            if (instance == null || mainSettings == null) return false;
            pluginData = scatterer.GetField("pluginData", HostStack.Any);
            Type pluginDataType = TypeLookup.Find("Scatterer.PluginDataReadWrite");
            savePluginData = pluginDataType != null
                ? pluginDataType.GetMethod("savePluginData", HostStack.Any, null, Type.EmptyTypes, null)
                : null;
            if (isActive != null && !isActive.IsStatic && isActive.FieldType == typeof(bool)) return true;
            mod.Drop(RegisteredMod.WholeMod, "this build of Scatterer cannot be asked whether it runs in this scene");
            return false;
        }

        public override bool Reach(RegisteredMod mod, SettingRegistration setting, out Func<string> read,
                                   out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            if (setting.IsBinding) return ReachBinding(mod, setting, out read, out write);
            FieldInfo field = settingsType.GetField(setting.Name, HostStack.Any);
            if (field == null)
            {
                mod.MemberMissing(setting, "this build of Scatterer does not have it");
                return true;
            }
            mod.Named(field);
            // quarterResScattering is such a field in 0.908 -- its [Persistent] is
            // commented out.
            if (!field.IsDefined(typeof(Persistent), true))
            {
                mod.Drop(setting.Name, "Scatterer does not save it, so its own save would drop it from its file");
                return true;
            }
            Reach(field, out read, out write);
            type = field.FieldType;
            return true;
        }

        // Each of its keys has one modifier beside it, so a row for one holds one.
        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            if (registration.IsBinding) setting.MaxModifiers = 1;
        }

        public override void Complete(RegisteredMod mod)
        {
            foreach (FieldInfo field in settingsType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                // A field its registration names and leaves out stays out.
                if (mod.Has(field.Name) || mod.Registration.Setting(field.Name) != null
                    || !field.IsDefined(typeof(Persistent), true)) continue;
                Func<string> read;
                Action<string> write;
                Reach(field, out read, out write);
                mod.AddFound(field.Name, ApplyWindow.NextScene, read, write, field.FieldType);
            }
        }

        // Its window's keys: the KeyCode fields it reads, and the string fields it
        // saves, both written. The binding's name is the key field's, guiKey1 or
        // guiKey2, and the modifier beside it carries the same number.
        private bool ReachBinding(RegisteredMod mod, SettingRegistration setting, out Func<string> read,
                                  out Action<string> write)
        {
            read = null;
            write = null;
            string number = setting.Name.Substring(setting.Name.Length - 1);
            FieldInfo key = PluginField("guiKey" + number, typeof(UnityEngine.KeyCode));
            FieldInfo keyText = PluginField("guiKey" + number + "String", typeof(string));
            FieldInfo modifier = PluginField("guiModifierKey" + number, typeof(UnityEngine.KeyCode));
            FieldInfo modifierText = PluginField("guiModifierKey" + number + "String", typeof(string));
            if (pluginData == null || key == null || keyText == null || modifier == null || modifierText == null
                || savePluginData == null)
            {
                mod.MemberMissing(setting, "this build of Scatterer keeps its window's keys elsewhere");
                return true;
            }

            read = () =>
            {
                object data = PluginData();
                if (data == null) return null;
                return new KeyCombination((UnityEngine.KeyCode)key.GetValue(data),
                    (UnityEngine.KeyCode)modifier.GetValue(data), UnityEngine.KeyCode.None).ToString();
            };
            write = text =>
            {
                object data = PluginData();
                if (data == null) throw new InvalidOperationException("Scatterer has no plugin data to set");
                KeyCombination combination = KeyCombination.Parse(text);
                key.SetValue(data, combination.Key);
                keyText.SetValue(data, combination.Key.ToString());
                modifier.SetValue(data, combination.FirstModifier);
                modifierText.SetValue(data, combination.FirstModifier.ToString());
                pluginDataChanged = true;
            };
            return true;
        }

        private FieldInfo PluginField(string name, Type type)
        {
            Type pluginDataType = pluginData != null ? pluginData.FieldType : null;
            FieldInfo field = pluginDataType != null ? pluginDataType.GetField(name, HostStack.Any) : null;
            return field != null && field.FieldType == type ? field : null;
        }

        private object PluginData()
        {
            UnityEngine.Object scatterer = instance.GetValue(null, null) as UnityEngine.Object;
            return scatterer == null ? null : pluginData.GetValue(scatterer);
        }

        // The node's file, as Scatterer's own save writes it.
        public override void Save(RegisteredMod mod)
        {
            if (pluginDataChanged)
            {
                object data = PluginData();
                if (data != null) savePluginData.Invoke(data, null);
                pluginDataChanged = false;
            }
            if (!nodeChanged) return;
            UrlDir.UrlConfig config = Config();
            if (config == null) throw new InvalidOperationException("Scatterer has no " + NodeName + " node to save");
            config.parent.SaveConfigs();
            nodeChanged = false;
        }

        private void Reach(FieldInfo field, out Func<string> read, out Action<string> write)
        {
            read = () => Read(field);
            write = text => Write(field, text);
        }

        // Scatterer itself takes the first, and says so when there are more.
        private static UrlDir.UrlConfig Config()
        {
            if (GameDatabase.Instance == null) return null;
            UrlDir.UrlConfig[] configs = GameDatabase.Instance.GetConfigs(NodeName);
            return configs.Length > 0 ? configs[0] : null;
        }

        // Its running settings where it runs in this scene. Its static instance is
        // never cleared: a destroyed one still answers, and in a scene it does not
        // run in -- the editors -- the instance there never loaded its settings
        // (Scatterer.Awake sets isActive only where it runs).
        private object Running()
        {
            UnityEngine.Object scatterer = instance.GetValue(null, null) as UnityEngine.Object;
            if (scatterer == null || !(bool)isActive.GetValue(scatterer)) return null;
            return mainSettings.GetValue(scatterer);
        }

        // The running object where Scatterer runs -- what its window shows --
        // otherwise what the next scene loads from the node, read as Scatterer reads
        // it (loadMainSettings) into a fresh settings object: a key its shipped file
        // leaves out is its class default, not nothing.
        private string Read(FieldInfo field)
        {
            object running = Running();
            if (running != null) return SettingValues.Text(field.GetValue(running));

            UrlDir.UrlConfig config = Config();
            if (config == null) return null;
            if (!ReferenceEquals(parsedNode, config.config) || parsed == null)
            {
                object fresh = Activator.CreateInstance(settingsType, true);
                ConfigNode.LoadObjectFromConfig(fresh, config.config);
                parsed = fresh;
                parsedNode = config.config;
            }
            return SettingValues.Text(field.GetValue(parsed));
        }

        private void Write(FieldInfo field, string text)
        {
            object value = SettingValues.Parse(text, field.FieldType);
            UrlDir.UrlConfig config = Config();
            if (config == null) throw new InvalidOperationException("Scatterer has no " + NodeName + " node to set it in.");

            object running = Running();
            if (running != null) field.SetValue(running, value);
            config.config.SetValue(field.Name, SettingValues.Text(value), true);
            nodeChanged = true;
            parsedNode = null;
        }
    }
}
