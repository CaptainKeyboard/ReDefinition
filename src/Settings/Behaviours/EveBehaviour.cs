using System.Collections.Generic;
using System.Reflection;
using System;
using ReDefinition.Core;

namespace ReDefinition.Settings.Behaviours
{
    // RaymarchedCloudsQualityManager is fed from the EVE_RAYMARCHED_CLOUDS_QUALITY
    // node: Apply builds one object per child node of its configs, and
    // PostApplyConfigNodes copies the first object's values into private statics
    // and rebuilds the clouds. Its own window edits those nodes, *Apply* rebuilds
    // from them and *Save* (EVEManagerBase.SaveConfig) saves their files.
    //
    // A value set here goes the same way: into the first object node -- what EVE
    // applies next time and what its window shows -- and into the static the
    // clouds read now; the registration rebuilds the clouds after it. The light
    // volume is a node of its own inside the first object's, and one object the
    // manager and that object share: set in both. Only once EVE has loaded its
    // quality config -- in the main menu, five physics frames after its global
    // manager starts there (GlobalEVEManager.waitToRunLateSetup): before that the
    // static holds its class default, and EVE's apply would put the pack's value
    // back over the one set here.
    internal sealed class EveBehaviour : ModBehaviour
    {
        // Every member, of any visibility, the generic base's statics included.
        private const BindingFlags Any = TypeLookup.Any | BindingFlags.FlattenHierarchy;

        // EVE's members as Attach finds them: the quality manager's instance and configs, its SaveConfig and
        // its Apply, which an older build may lack.
        private FieldInfo managerInstance;
        private FieldInfo configs;
        private MethodInfo saveConfig;
        private MethodInfo apply;

        // The quality manager and renderer this is written against; false where this build cannot be asked to
        // save.
        public override bool Attach(RegisteredMod mod)
        {
            Type manager = TypeLookup.Find("Atmosphere.RaymarchedCloudsQualityManager");
            Type renderer = TypeLookup.Find("Atmosphere.DeferredRaymarchedVolumetricCloudsRenderer");
            if (manager == null || renderer == null) return false;

            MethodInfo reinitAll = renderer.GetMethod("ReinitAll", TypeLookup.Any, null, Type.EmptyTypes, null);
            // Protected statics of the generic base, GenericEVEManager<T>: found
            // only with FlattenHierarchy.
            managerInstance = manager.GetField("instance", Any);
            configs = manager.GetField("configs", Any);
            saveConfig = manager.GetMethod("SaveConfig", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            apply = manager.GetMethod("Apply", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (reinitAll != null && managerInstance != null && configs != null && saveConfig != null) return true;
            mod.Drop(RegisteredMod.WholeMod, "this build of EVE cannot be asked to save its cloud quality settings");
            return false;
        }

        // Named in its node as the setting is -- `lightVolumeSettings.x` in the node
        // of that name -- held where its member path says.
        public override bool Reach(RegisteredMod mod, SettingRegistration setting, out Func<string> read,
                                   out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            string problem;
            MemberPath path = setting.Member != null ? MemberPath.Resolve(setting.Member, mod.Folder, out problem) : null;
            if (path == null || path.IsMethod)
            {
                mod.MemberMissing(setting, "this build of EVE does not have it among its cloud quality settings");
                return true;
            }

            mod.Named(path.EndField);
            MemberPath member = path;
            Type valueType = path.ValueType;
            int dot = setting.Name.LastIndexOf('.');
            string group = dot > 0 ? setting.Name.Substring(0, dot) : null;
            string key = dot > 0 ? setting.Name.Substring(dot + 1) : setting.Name;
            type = valueType;
            read = () =>
            {
                object value;
                try
                {
                    value = member.Get();
                }
                catch (InvalidOperationException)
                {
                    value = null;
                }
                return value != null ? SettingValues.Text(value) : null;
            };
            write = text =>
            {
                object value = SettingValues.Parse(text, valueType);
                ConfigNode node = FirstObjectNode();
                if (group != null) node = node.GetNode(group) ?? node.AddNode(group);
                node.SetValue(key, SettingValues.Text(value), true);
                try
                {
                    member.Set(value);
                }
                catch (InvalidOperationException)
                {
                    // The light volume object is not made yet: its node holds it.
                    if (group == null) throw;
                }
            };
            return true;
        }

        // Each setting waits for EVE's quality config; the noise switch applies the node anew after a write.
        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            setting.Applicable = () => FirstObjectNode() != null;
            if (registration.Name != "nonTiling3DNoise") return;
            if (apply == null)
            {
                // Taken as the clouds are next built.
                if (setting.Window == ApplyWindow.Live) setting.Window = ApplyWindow.NextScene;
                return;
            }
            // Read as each cloud volume sets up its noise (CloudsRaymarchedVolume.Apply,
            // SetNoisetextureParams), which a rebuild of the renderers does not
            // repeat: the quality manager applies its node anew, as its window's
            // *Apply* does -- the quality object, the statics, the renderers and
            // every cloud layer (EVEManagerBase.Apply, PostApplyConfigNodes).
            Action<string> write = setting.Write;
            setting.Write = text =>
            {
                write(text);
                BundledSettingsAddon.AtEndOfFrame("eve-apply-quality", ApplyQuality);
            };
        }

        // The quality manager's Apply, as its window's: the statics, the renderers and every cloud layer anew.
        private void ApplyQuality()
        {
            try
            {
                object instance = managerInstance.GetValue(null);
                if (instance != null) apply.Invoke(instance, null);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("eve-apply-quality", "EVE's clouds could not be applied anew after a change ("
                                                           + CompatibilityLog.Reason(e) + "); they take it at their"
                                                           + " next rebuild.");
            }
        }

        // The registration's defaults are Volumetric Clouds' own: its Stock pack's
        // file (StockVolumetricClouds/raymarchedClouds.cfg sets horizontalResolution
        // 224 and directLightTimeSlicing 12) over the classes' initialisers (256
        // and 8, LightVolumeSettings). Where another pack's file is the one EVE
        // builds its quality from, Stock's numbers are not that pack's: its default
        // is what the installed pack held before ReDefinition first changed it --
        // the backup ReDefinition keeps, else the value EVE holds now, which is the
        // class's where the pack's file sets none.
        public override string Default(RegisteredMod mod, string setting)
        {
            UrlDir.UrlConfig[] list = configs.GetValue(null) as UrlDir.UrlConfig[];
            if (list == null || list.Length == 0 || list[0].parent == null) return null;
            if (list[0].parent.url.StartsWith(StockPack, StringComparison.OrdinalIgnoreCase)) return null;
            string key = mod.Id + "." + setting;
            foreach (BundledSetting candidate in mod.Settings)
                if (candidate.Key == key) return BundledSettings.BeforeReDefinition(candidate);
            return null;
        }

        // The folder of Volumetric Clouds' Stock pack, whose file the registration's defaults are.
        private const string StockPack = "StockVolumetricClouds/";

        // As its own window's Save: the files of its configs.
        public override void Save(RegisteredMod mod)
        {
            object instance = managerInstance.GetValue(null);
            if (instance == null || FirstObjectNode() == null)
                throw new InvalidOperationException("EVE has no cloud quality config loaded to save into");
            saveConfig.Invoke(instance, null);
        }

        // The node EVE builds its first quality object from -- the one whose values
        // it copies into the statics: the first child node of its first config
        // (EVEManagerBase.Apply, GenericEVEManager.ApplyConfigNode).
        private ConfigNode FirstObjectNode()
        {
            UrlDir.UrlConfig[] list = configs.GetValue(null) as UrlDir.UrlConfig[];
            if (list == null || list.Length == 0 || list[0].config == null) return null;
            return list[0].config.nodes.Count > 0 ? list[0].config.nodes[0] : null;
        }
    }
}
