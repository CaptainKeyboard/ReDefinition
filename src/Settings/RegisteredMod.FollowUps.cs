using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System;
using ReDefinition.Core;

namespace ReDefinition.Settings
{
    // What follows a setting's write where the registration names it: a shader
    // global set from the value, a method of the mod called after it.
    internal sealed partial class RegisteredMod
    {
        // A shader global set with the value, and a call once at the end of the
        // frame, however many settings ask for the same.
        private Action<string> FollowedUp(SettingRegistration entry, Type type, string where, List<string> problems,
                                          Action<string> write, ref ApplyWindow window)
        {
            Action run = AfterCall(entry, where, problems, ref window, "the change");
            string global = entry.ShaderGlobal;
            if (global != null && (entry.Invert || !(type == typeof(float) || type == typeof(double) || type == typeof(int))))
            {
                problems.Add(where + ": shaderGlobal takes a number that reads the right way round -- ignored.");
                global = null;
            }
            // The global is set by name, which the mod's code must still use: renamed,
            // setting it would reach nothing, unseen.
            if (global != null && folder != null && !folder.Loads(global))
            {
                problems.Add(where + ": shaderGlobal " + global + " is not set anywhere in " + ModName
                             + "'s code -- ignored; the value takes effect when " + ModName + " next reads it.");
                global = null;
                if (run == null && window == ApplyWindow.Live) window = ApplyWindow.NextScene;
            }
            string key = "after-" + Id + "-" + entry.After;
            return text =>
            {
                // The global's value first: a text that is no number must not leave
                // the member written and the rest undone.
                float value = global != null ? Convert.ToSingle(SettingValues.Parse(text, typeof(double))) : 0f;
                write(text);
                if (global != null) UnityEngine.Shader.SetGlobalFloat(global, value);
                if (run != null) BundledSettingsAddon.AtEndOfFrame(key, run);
            };
        }

        // The call the registration's `after` names, for the end of the frame, or null where there is none:
        // no `after`, or one this build cannot give (said, and the row's window lowered from Live, since it
        // promises only what the call that is there can keep: the mod takes the value when it next reads it).
        // A call that fails is logged rather than thrown into the write already done -- with no planet loaded
        // Parallax has nothing to update, and the store would take a write that threw for one that did not
        // happen and drop the value from before ReDefinition with it. `takes` names what then waits for the
        // mod's next read in that log line.
        private Action AfterCall(SettingRegistration entry, string where, List<string> problems, ref ApplyWindow window,
                                 string takes)
        {
            if (entry.After == null) return null;
            string problem;
            MemberPath call = MemberPath.Resolve(entry.After, folder, out problem);
            if (call != null && !call.IsMethod)
            {
                problem = entry.After + " is no method";
                call = null;
            }
            if (call == null)
            {
                problems.Add(where + ": after: " + problem + " -- ignored.");
                if (window == ApplyWindow.Live) window = ApplyWindow.NextScene;
                return null;
            }
            string key = "after-" + Id + "-" + entry.After;
            string after = entry.After;
            string modName = ModName;
            return () =>
            {
                try
                {
                    call.Invoke();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn(key, modName + "'s " + after + " did not run after a change ("
                                               + CompatibilityLog.Reason(e) + "); " + takes + " takes effect when "
                                               + modName + " next reads it.");
                }
            };
        }
    }
}
