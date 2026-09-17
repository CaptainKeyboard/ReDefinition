using System;
using System.Reflection;

namespace ReDefinition.Framework
{
    // Its registration reaches its settings through ModSettings.I's indexer; what a
    // path cannot say is here.
    //
    // Its window keeps each field's text beside the value (ConfigField.uiText) and
    // parses its float fields back from that text in every frame
    // (GuiUtils.DrawSettingsFieldFloat), so the text is brought up to date with
    // each write, as its own loader does (ConfigField.UpdateUiText). Where its
    // settings are already there, every key its registration shows must be: a build
    // that renamed them would leave the rows dead and its button hidden.
    // hdr_override is looked for alone: a build without it keeps the rest bundled.
    internal sealed class FireflyBehaviour : ModBehaviour
    {
        private static readonly string[] Keys = { "strength_base", "length_mult", "disable_bowshock", "disable_particles" };

        private PropertyInfo instance;
        private MethodInfo getField;
        private MethodInfo updateUiText;

        public override bool Attach(RegisteredMod mod)
        {
            Type settings = TypeLookup.Find("Firefly.ModSettings");
            instance = settings != null ? settings.GetProperty("I", BindingFlags.Public | BindingFlags.Static) : null;
            getField = settings != null ? settings.GetMethod("GetField", new[] { typeof(string) }) : null;
            Type field = TypeLookup.Find("Firefly.ConfigField");
            updateUiText = field != null
                ? field.GetMethod("UpdateUiText", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                : null;
            if (instance == null || getField == null || updateUiText == null)
            {
                mod.Drop(RegisteredMod.WholeMod, "this build of Firefly keeps its window's text in a way this was not"
                                                  + " written for, and its window would put the old value back");
                return false;
            }

            object current = instance.GetValue(null, null);
            if (current == null) return true;
            foreach (string key in Keys)
            {
                if (getField.Invoke(current, new object[] { key }) != null) continue;
                mod.Drop(key, "this build of Firefly does not have it among its settings");
                return false;
            }
            return true;
        }

        public override bool Reach(RegisteredMod mod, SettingRegistration setting, out Func<string> read,
                                   out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            if (setting.Name != "hdr_override") return false;
            object current = instance.GetValue(null, null);
            if (current == null || getField.Invoke(current, new object[] { "hdr_override" }) != null) return false;
            mod.MemberMissing(setting, "this build of Firefly does not have it among its settings");
            return true;
        }

        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            string key = Key(registration.Member);
            if (key == null) return;
            Action<string> write = setting.Write;
            setting.Write = text =>
            {
                write(text);
                object settings = instance.GetValue(null, null);
                object field = settings != null ? getField.Invoke(settings, new object[] { key }) : null;
                if (field != null) updateUiText.Invoke(field, null);
            };
        }

        // The key of a path's indexer: strength_base in Firefly.ModSettings.I[strength_base].
        private static string Key(string member)
        {
            if (member == null) return null;
            int open = member.LastIndexOf('[');
            return open >= 0 && member.EndsWith("]", StringComparison.Ordinal)
                ? member.Substring(open + 1, member.Length - open - 2)
                : null;
        }
    }
}
