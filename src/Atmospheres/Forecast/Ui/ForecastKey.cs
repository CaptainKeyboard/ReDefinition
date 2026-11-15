using System;
using System.Reflection;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The key that opens and closes the forecast window, in every scene. ReDefinition keeps the binding (the KEY block "forecastWindow" of
    // src/Settings/Atmospheres/Atmospheres.cfg, shown in the Keys tab, default None); this asks ReDefinition.Api.Keys.Pressed(id) once per
    // frame, through reflection, so that the atmospheres need no reference to the main mod. Without the main mod the type is not there
    // and the window opens from the launcher's button only.
    internal sealed class ForecastKey
    {
        // The binding's id: the registration's name, a dot, the block's name.
        internal const string BindingId = "atmospheres.forecastWindow";
        internal const string KeysTypeName = "ReDefinition.Api.Keys";

        private readonly MethodInfo pressed;
        private readonly Action<string> log;
        // The argument of the call, made once (the key is asked every frame).
        private readonly object[] args;
        private readonly string bindingId;
        private bool failed;

        // The game's bridge: the type is found among the loaded assemblies.
        internal ForecastKey() : this(TypeLookup.Find(KeysTypeName))
        {
        }

        // The game's bridge for another binding of the registration (the cloud viewer's keys).
        internal ForecastKey(string bindingId) : this(TypeLookup.Find(KeysTypeName), null, bindingId)
        {
        }

        // A bridge to a given type (a test passes one of its own; null stands for an absent main mod).
        internal ForecastKey(Type keys, Action<string> log = null, string bindingId = BindingId)
        {
            this.log = log ?? AtmospheresLog.WarnOnce;
            this.bindingId = bindingId;
            args = new object[] { bindingId };
            if (keys == null) return;
            try
            {
                pressed = keys.GetMethod("Pressed", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            }
            catch (Exception)
            {
                pressed = null;
            }
        }

        // Whether the main mod's key interface is there.
        internal bool Available
        {
            get { return pressed != null && !failed; }
        }

        // True on the frame the player pressed the key.
        internal bool Pressed()
        {
            if (!Available) return false;
            try
            {
                return (bool)pressed.Invoke(null, args);
            }
            catch (Exception e)
            {
                failed = true;
                log("the key " + bindingId + " could not be read (" + CompatibilityLog.Reason(e) + ")");
                return false;
            }
        }

        // Calls the action on the frame the key is pressed. Returns whether it did.
        internal bool Poll(Action toggle)
        {
            if (toggle == null || !Pressed()) return false;
            toggle();
            return true;
        }
    }
}
