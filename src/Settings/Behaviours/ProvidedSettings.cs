using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System;
using ReDefinition.Core;

namespace ReDefinition.Settings.Behaviours
{
    // A behaviour a mod brings itself: where `behaviour` holds a type name rather
    // than one of ReDefinition's own names (`behaviour = MyMod.SettingsBridge`),
    // that type comes from the mod's own folder and answers for the settings a
    // member path cannot reach -- what ReDefinition's behaviours do for the mods it
    // ships registrations for (docs/modders/registering-a-mod.md).
    //
    // Its members are found by name and signature, so the mod needs no reference to
    // ReDefinition and runs without it:
    //
    //   string Read(string name)                 needed -- the value as text, null
    //                                            where it cannot be read now
    //   void Write(string name, string value)    needed -- bool may be returned:
    //                                            False is a value the mod refused
    //   bool Ready                               property, field or method: whether
    //                                            the mod can take values now
    //   void Save()                              as the mod's own window saves
    //   string[] Choices(string name)            a list only the running mod knows
    //   string Default(string name)              a default only the running game
    //                                            can tell; null keeps `default`
    //   string Version                           property, field or method
    //
    // Static members are called on the type. For members that are not, a static
    // Instance -- a property or field of the type itself -- is used where the type
    // has one, and where it has none the type is made once through its parameterless
    // constructor. A component of the scene without such an Instance is not reached:
    // Unity makes those, and the log says so.
    //
    // Every setting of the registration without a `member` goes through it; one with
    // a `member` keeps its path. Key bindings are not taken over: a binding without a
    // member is ReDefinition's to keep (KeptBindings), and the mod reads it through
    // ReDefinition.Api.Keys.
    internal sealed class ProvidedSettings : ModBehaviour
    {
        private const BindingFlags Any = TypeLookup.Any;
        private const BindingFlags StaticAny = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly string typeName;
        private readonly Type type;
        private readonly MethodInfo read;
        private readonly MethodInfo write;
        private readonly MethodInfo save;
        private readonly MethodInfo choices;
        private readonly MethodInfo defaultOf;
        private readonly MemberInfo ready;
        private readonly MemberInfo version;
        private readonly MemberInfo instance;
        // The settings whose list has been asked for, once each.
        private readonly HashSet<string> listed = new HashSet<string>();
        private object made;
        private bool cannotMake;

        private ProvidedSettings(string typeName, Type type, MethodInfo read, MethodInfo write)
        {
            this.typeName = typeName;
            this.type = type;
            this.read = read;
            this.write = write;
            save = Method(type, "Save", Type.EmptyTypes);
            choices = Method(type, "Choices", new[] { typeof(string) });
            if (choices != null && choices.ReturnType != typeof(string[])) choices = null;
            defaultOf = Method(type, "Default", new[] { typeof(string) });
            if (defaultOf != null && defaultOf.ReturnType != typeof(string)) defaultOf = null;
            ready = Member("Ready", typeof(bool), Any);
            version = Member("Version", typeof(string), Any);
            // Of the type itself: a static member named Instance that holds something
            // else is not the object these members are called on.
            instance = Member("Instance", null, StaticAny);
            if (instance != null && !type.IsAssignableFrom(TypeOfMember(instance))) instance = null;
        }

        // Null where the type is not there or has no Read and Write, with the reason.
        internal static ProvidedSettings For(string typeName, ModFolder folder, out string problem)
        {
            Type type = MemberPath.FindType(typeName, folder);
            if (type == null)
            {
                problem = "no type " + typeName + " in the mod's folder";
                return null;
            }

            MethodInfo read = Method(type, "Read", new[] { typeof(string) });
            MethodInfo write = Method(type, "Write", new[] { typeof(string), typeof(string) });
            if (read == null || read.ReturnType != typeof(string) || write == null)
            {
                problem = typeName + " has no string Read(string) and no Write(string, string)";
                return null;
            }
            problem = null;
            return new ProvidedSettings(typeName, type, read, write);
        }

        // Whether values reach the mod's own files through this behaviour: a
        // registration that says they do is checked against it.
        internal override bool Saves
        {
            get { return save != null; }
        }

        public override string Version(RegisteredMod mod)
        {
            if (version == null) return null;
            try
            {
                object target;
                if (!Target(IsStatic(version), out target)) return null;
                return Get(version, target) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override bool Reach(RegisteredMod mod, SettingRegistration setting, out Func<string> read,
                                   out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            // A setting with a path of its own keeps it, and a key binding without one
            // is ReDefinition's to keep.
            if (setting.Member != null || setting.IsBinding) return false;

            string name = setting.Name;
            read = () => Value(name);
            write = text => Set(name, text);
            type = TypeOf(setting);
            return true;
        }

        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            string name = registration.Name;
            bool provided = registration.Member == null && !registration.IsBinding;

            // Ready holds for the whole mod, as a `ready` member does: it gates every
            // setting, whether it goes through this behaviour or through a path.
            if (ready != null)
            {
                Func<bool> applicable = setting.Applicable;
                Func<string> value = setting.Read;
                setting.Applicable = applicable == null
                    ? (Func<bool>)(() => IsReady() && AskedForTheList(setting, name, provided))
                    : () => IsReady() && AskedForTheList(setting, name, provided) && applicable();
                setting.Read = () => IsReady() ? value() : null;
            }
            // Asked here as well: a mod that can answer now says its lists at once,
            // and the row stands as a list from its first opening.
            AskedForTheList(setting, name, provided);
        }

        // Before the `save` a registration names. Not while the mod cannot take
        // values: what waits for it would count as saved; the store tries again at
        // the next scene change.
        public override void Save(RegisteredMod mod)
        {
            if (save == null) return;
            if (!IsReady())
                throw new InvalidOperationException(mod.ModName + " cannot save its settings yet: " + typeName
                                                    + ".Ready is False.");
            object target;
            if (!Target(save.IsStatic, out target))
                throw new InvalidOperationException(typeName + " is not there to save with.");
            save.Invoke(target, null);
        }

        private string Value(string name)
        {
            object target;
            if (!Target(read.IsStatic, out target)) return null;
            return read.Invoke(target, new object[] { name }) as string;
        }

        // A Write that answers False has refused the value: the store says so and
        // keeps the value, as for a member path that throws.
        private void Set(string name, string text)
        {
            object target;
            if (!Target(write.IsStatic, out target))
                throw new InvalidOperationException(typeName + " is not there to take a value.");
            object answer = write.Invoke(target, new object[] { name, text });
            if (write.ReturnType == typeof(bool) && answer is bool && !(bool)answer)
                throw new InvalidOperationException(typeName + " did not take '" + text + "' for " + name + ".");
        }

        // The setting's list, asked once the mod can answer it: with a list, its row
        // chooses from it; without one, the setting keeps the control its value's
        // type gives. Always true: this stands in the row's Applicable.
        private bool AskedForTheList(BundledSetting setting, string name, bool provided)
        {
            if (!provided || choices == null || listed.Contains(name)) return true;
            string[] list = Choices(name);
            // Asked again later while the mod has no answer yet.
            if (list == null) return true;
            listed.Add(name);
            if (list.Length == 0) return true;
            setting.Control = SettingControl.Choice;
            setting.ValueType = null;
            setting.ChoicesSource = () => Choices(name);
            return true;
        }

        public override string Default(RegisteredMod mod, string setting)
        {
            object target;
            if (defaultOf == null || !Target(defaultOf.IsStatic, out target)) return null;
            return defaultOf.Invoke(target, new object[] { setting }) as string;
        }

        private string[] Choices(string name)
        {
            object target;
            if (!Target(choices.IsStatic, out target)) return null;
            return choices.Invoke(target, new object[] { name }) as string[];
        }

        // Whether the mod can take values now. A Ready that throws counts as not
        // ready: this is asked while the window draws.
        private bool IsReady()
        {
            if (ready == null) return true;
            try
            {
                object target;
                if (!Target(IsStatic(ready), out target)) return false;
                object value = Get(ready, target);
                return value is bool && (bool)value;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("provided-ready-" + typeName, typeName + ".Ready could not be read ("
                                      + CompatibilityLog.Reason(e) + "); its settings wait.");
                return false;
            }
        }

        // The object a member that is not static is called on: the type's own
        // Instance where it has one -- null while the mod has not made it yet -- or
        // one made here.
        private bool Target(bool isStatic, out object target)
        {
            target = null;
            if (isStatic) return true;
            if (instance != null)
            {
                try
                {
                    target = Get(instance, null);
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("provided-instance-" + typeName, typeName + ".Instance could not be read ("
                                          + CompatibilityLog.Reason(e) + "); its settings stay as they are.");
                    return false;
                }
                return target != null;
            }
            if (made != null)
            {
                target = made;
                return true;
            }
            if (cannotMake) return false;
            // A component of the scene is never made here: Unity makes those, and one
            // built beside a game object holds none of the mod's values.
            if (typeof(UnityEngine.Component).IsAssignableFrom(type))
            {
                cannotMake = true;
                CompatibilityLog.Warn("provided-settings-" + typeName, typeName + " is a component of the scene without"
                                      + " a static Instance, so its settings cannot be reached: its members have to be"
                                      + " static, or the type needs one.");
                return false;
            }
            try
            {
                made = Activator.CreateInstance(type, true);
            }
            catch (Exception e)
            {
                cannotMake = true;
                CompatibilityLog.Warn("provided-settings-" + typeName, typeName + " could not be made ("
                                      + CompatibilityLog.Reason(e) + "); its settings stay as they are.");
                return false;
            }
            target = made;
            return true;
        }

        // A method of that shape; none where the type has several of that name and
        // the call is ambiguous.
        private static MethodInfo Method(Type type, string name, Type[] parameters)
        {
            try
            {
                return type.GetMethod(name, Any, null, parameters, null);
            }
            catch (AmbiguousMatchException)
            {
                return null;
            }
        }

        // A property, a field or a method without parameters, of that type where one
        // is given.
        private MemberInfo Member(string name, Type of, BindingFlags flags)
        {
            try
            {
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.CanRead && (of == null || property.PropertyType == of)) return property;
                FieldInfo field = type.GetField(name, flags);
                if (field != null && (of == null || field.FieldType == of)) return field;
            }
            catch (AmbiguousMatchException)
            {
                return null;
            }
            if (of == null) return null;
            MethodInfo method = Method(type, name, Type.EmptyTypes);
            return method != null && method.ReturnType == of ? method : null;
        }

        private static Type TypeOfMember(MemberInfo member)
        {
            PropertyInfo property = member as PropertyInfo;
            if (property != null) return property.PropertyType;
            FieldInfo field = member as FieldInfo;
            return field != null ? field.FieldType : ((MethodInfo)member).ReturnType;
        }

        private static object Get(MemberInfo member, object target)
        {
            PropertyInfo property = member as PropertyInfo;
            if (property != null) return property.GetValue(target, null);
            FieldInfo field = member as FieldInfo;
            if (field != null) return field.GetValue(target);
            return ((MethodInfo)member).Invoke(target, null);
        }

        private static bool IsStatic(MemberInfo member)
        {
            PropertyInfo property = member as PropertyInfo;
            if (property != null) return property.GetGetMethod(true).IsStatic;
            FieldInfo field = member as FieldInfo;
            if (field != null) return field.IsStatic;
            return ((MethodInfo)member).IsStatic;
        }

        // The type its control follows, from the registration: a list or a slider
        // says it, otherwise the default does -- True or False a switch, a number a
        // number, anything else text.
        private static Type TypeOf(SettingRegistration setting)
        {
            if (setting.Choices != null) return typeof(string);
            if (setting.Min != null && setting.Max != null)
                return setting.Whole == true ? typeof(int) : typeof(double);
            string text = setting.Default;
            if (text == null) return typeof(string);
            bool flag;
            if (bool.TryParse(text, out flag)) return typeof(bool);
            int whole;
            if (setting.Whole != false
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole)) return typeof(int);
            double number;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return typeof(double);
            return typeof(string);
        }
    }
}
