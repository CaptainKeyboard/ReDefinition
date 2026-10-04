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
            // The global is set by name, which the mod's code must still use: renamed,
            // setting it would reach nothing, unseen.
            if (global != null && folder != null && !folder.Loads(global))
            {
                problems.Add(where + ": shaderGlobal " + global + " is not set anywhere in " + ModName
                             + "'s code -- ignored; the value takes effect when " + ModName + " next reads it.");
                global = null;
                if (call == null && window == ApplyWindow.Live) window = ApplyWindow.NextScene;
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
    }
}
