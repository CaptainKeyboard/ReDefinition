using System;
using System.Collections.Generic;
using System.Reflection;

namespace ReDefinition.Framework
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
        private const BindingFlags Any = HostStack.Any | BindingFlags.FlattenHierarchy;

        private FieldInfo managerInstance;
        private FieldInfo configs;
        private MethodInfo saveConfig;

        public override bool Attach(RegisteredMod mod)
        {
            Type manager = TypeLookup.Find("Atmosphere.RaymarchedCloudsQualityManager");
            Type renderer = TypeLookup.Find("Atmosphere.DeferredRaymarchedVolumetricCloudsRenderer");
            if (manager == null || renderer == null) return false;

            MethodInfo reinitAll = renderer.GetMethod("ReinitAll", HostStack.Any, null, Type.EmptyTypes, null);
            // Protected statics of the generic base, GenericEVEManager<T>: found
            // only with FlattenHierarchy.
            managerInstance = manager.GetField("instance", Any);
            configs = manager.GetField("configs", Any);
            saveConfig = manager.GetMethod("SaveConfig", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
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

        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            setting.Applicable = () => FirstObjectNode() != null;
        }

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
