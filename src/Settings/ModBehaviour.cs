using System.Collections.Generic;
using System;
using ReDefinition.Settings.Behaviours;

namespace ReDefinition.Settings
{
    // What a registration cannot say in data (docs/development/architecture.md):
    // a mod that keeps its settings per scene, in a config node beside its
    // running object, behind hooks. Named from a registration with `behaviour =`,
    // on the mod or on one setting; a class of ReDefinition's own.
    //
    // Every step has a default that does nothing, so a behaviour says only what
    // is its mod's alone.
    internal abstract class ModBehaviour
    {
        // Before the settings are built. False: the mod is not the shape this was
        // written against, and is not bundled -- the behaviour drops with its
        // reason (RegisteredMod.Drop).
        public virtual bool Attach(RegisteredMod mod)
        {
            return true;
        }

        // How a setting is read and written where its member path cannot say it,
        // and its value's type; false leaves it to its member path. True with no
        // read: the behaviour has left the setting out.
        public virtual bool Reach(RegisteredMod mod, SettingRegistration setting, out Func<string> read,
                                  out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            return false;
        }

        // After a setting is built from its registration: choices only the
        // running mod knows, when it can take a value, which save it belongs to.
        public virtual void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
        }

        // After every setting.
        public virtual void Complete(RegisteredMod mod)
        {
        }

        // The build and version where the mod's members or assembly cannot tell
        // them; null leaves them to the registration.
        public virtual string Build(RegisteredMod mod)
        {
            return null;
        }

        public virtual string Version(RegisteredMod mod)
        {
            return null;
        }

        // A default only the running game can tell, for a setting whose default
        // depends on it -- KSP's UI scale on the screen's height; null keeps the
        // registration's.
        public virtual string Default(RegisteredMod mod, string setting)
        {
            return null;
        }

        // As the mod's own window saves, before a `save` the registration names.
        public virtual void Save(RegisteredMod mod)
        {
        }

        // Whether values reach the mod's files through this behaviour, for a
        // registration that says they are kept there.
        internal virtual bool Saves
        {
            get { return true; }
        }

        public virtual void InstallHooks(RegisteredMod mod)
        {
        }

        // A check a REQUIRES names for a setting of this mod: whether `value`
        // passes it; null where that cannot be told now.
        public virtual bool? Check(RegisteredMod mod, string check, string value)
        {
            return null;
        }

        // Whether this behaviour answers the check of that name.
        public virtual bool Provides(string check)
        {
            return false;
        }
    }

    // The behaviours by the names registrations give them.
    internal static class ModBehaviours
    {
        private static readonly Dictionary<string, Func<ModBehaviour>> known = new Dictionary<string, Func<ModBehaviour>>
        {
            { "DistantObject", () => new DistantObjectBehaviour() },
            { "Eve", () => new EveBehaviour() },
            { "Firefly", () => new FireflyBehaviour() },
            { "Ksp", () => new KspBehaviour() },
            { "ParallaxScatter", () => new ParallaxScatterBehaviour() },
            { "Scatterer", () => new ScattererBehaviour() },
            { "Tufx", () => new TufxBehaviour() },
        };

        public static ModBehaviour Create(string name)
        {
            Func<ModBehaviour> make;
            return name != null && known.TryGetValue(name, out make) ? make() : null;
        }

        internal static ICollection<string> Names()
        {
            return known.Keys;
        }
    }
}
