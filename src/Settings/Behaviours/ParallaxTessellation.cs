using System.Globalization;
using System.Reflection;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Settings.Behaviours
{
    // Parallax's tessellation edge length for the time the upscaler runs.
    //
    // Parallax subdivides a terrain triangle's edge once per that many pixels of
    // the target it renders into: EdgeTessellationFactor takes the edge's length
    // in normalized device coordinates times _ScreenParams.y over
    // _TessellationEdgeLength (Parallax-Continued
    // Assets/Shaders/Terrain/ParallaxUtils.cginc:83-91), and the terrain materials
    // get the setting as it stands (ToolbarMenu.UpdateTerrainMaterials,
    // ParallaxTerrainBody: ConfigLoader.parallaxGlobalSettings
    // .terrainGlobalSettings.tessellationEdgeLength, ParallaxContinued.dll,
    // decompiled). At the upscaler's lower render height the same edge covers
    // fewer pixels and the ground turns coarser than at display size. The
    // registered setting (parallax.tessellationEdgeLength) is divided by display
    // height over render height while the upscaler runs, with the LOD bias
    // (QualityOverrides), and put back when it stops -- only where it still holds
    // the value written here: a value the player set in Parallax's own window in
    // the meantime stands, and is the one scaled from the next refresh on.
    // Parallax's scaled-space bodies take a quarter of it once, as they are set
    // up (ParallaxScaledBody), and keep that.
    //
    // While scaled, the setting reads and takes the player's value: ReDefinition's
    // window, its store and the profiles see the unscaled one, and a value set
    // there is scaled as it is written. Parallax's save (ParallaxSettings
    // .SaveSettings -- its window's button, and ReDefinition's save of the
    // registration) writes the player's value: the member holds it for the call,
    // and the scaled one again after. Without that hook nothing is scaled, so the
    // scaled value never reaches Parallax's file.
    internal static class ParallaxTessellation
    {
        // The registered setting's key, Parallax's settings type whose save is hooked, and the hook's Harmony id.
        private const string Key = "parallax.tessellationEdgeLength";
        private const string SettingsTypeName = "Parallax.ParallaxSettings";
        private const string HarmonyId = "ReDefinition.ParallaxTessellation";

        // The registered setting, taken over while scaled.
        private static BundledSetting setting;
        // The setting's own read and write, as registered.
        private static Func<string> read;
        private static Action<string> write;
        // The player's value, the value written while scaled, the factor (render over display height), whether
        // it is scaled now, and whether the save's hook is in or could not be.
        private static float playerValue;
        private static float applied;
        private static float factor = 1f;
        private static bool scaled;
        private static bool hooked;
        private static bool hookFailed;

        // The edge length as the diagnostics show it: the player's value and the one written, or untouched.
        internal static string Describe()
        {
            return "Parallax edge length " + (scaled
                ? playerValue.ToString("0.##", CultureInfo.InvariantCulture) + " -> "
                  + applied.ToString("0.##", CultureInfo.InvariantCulture)
                : "untouched");
        }

        // `ratio`: display height over render height, as for the LOD bias.
        internal static void Scale(bool wanted, float ratio)
        {
            if (!wanted)
            {
                Restore();
                return;
            }

            if (!scaled)
            {
                BundledSetting found = BundledSettings.Find(Key);
                if (found == null || found.Read == null || found.Write == null || !Hook()) return;
                float now;
                if (!Parse(SafeRead(found.Read), out now)) return;
                setting = found;
                read = found.Read;
                write = found.Write;
                playerValue = now;
                applied = now;
                setting.Read = ReadPlayer;
                setting.Write = WritePlayer;
                scaled = true;
            }
            else
            {
                // A value set in Parallax's own window since: the player's now.
                float now;
                if (Parse(SafeRead(read), out now) && !Mathf.Approximately(now, applied)) playerValue = now;
            }

            factor = 1f / Mathf.Max(1f, ratio);
            Apply();
        }

        // The setting's own read and write back, and the player's value into Parallax where it still holds the
        // scaled one.
        private static void Restore()
        {
            if (!scaled) return;
            setting.Read = read;
            setting.Write = write;
            scaled = false;
            float now;
            if (Parse(SafeRead(read), out now) && Mathf.Approximately(now, applied) && !Mathf.Approximately(now, playerValue))
                SafeWrite(Format(playerValue));
            factor = 1f;
        }

        // The player's value times the factor into Parallax, where it does not hold that already.
        private static void Apply()
        {
            float now;
            float wantedValue = playerValue * factor;
            bool known = Parse(SafeRead(read), out now);
            applied = wantedValue;
            if (!known || !Mathf.Approximately(now, wantedValue)) SafeWrite(Format(wantedValue));
        }

        // The setting's read while scaled: the player's value where Parallax holds the one written here.
        private static string ReadPlayer()
        {
            string now = read();
            float value;
            // Unscaled where it still holds what was written here.
            return Parse(now, out value) && Mathf.Approximately(value, applied) ? Format(playerValue) : now;
        }

        // The setting's write while scaled: the player's value, scaled into Parallax.
        private static void WritePlayer(string text)
        {
            float value;
            if (!Parse(text, out value))
            {
                write(text);
                return;
            }
            playerValue = value;
            applied = value * factor;
            write(Format(applied));
        }

        // The hook on Parallax's save, once, where the build is the one checked; false where it is not in.
        private static bool Hook()
        {
            if (hooked) return true;
            if (hookFailed) return false;
            // The save and the shaders' edge length checked first (ForeignAssumptions.ParallaxTessellation).
            if (!ForeignAssumptions.ParallaxTessellation.Holds)
            {
                hookFailed = true;
                CompatibilityLog.Warn("parallax-tessellation", "Parallax is not the build its tessellation was checked against ("
                                                               + (ForeignAssumptions.ParallaxTessellation.Problem ?? "not installed")
                                                               + "): its tessellation edge length is not scaled to the"
                                                               + " upscaler's render size.");
                return false;
            }
            Type settings = TypeLookup.Find(SettingsTypeName);
            MethodInfo save = settings != null
                ? settings.GetMethod("SaveSettings", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                : null;
            try
            {
                HarmonyHooks.Install(HarmonyId, typeof(ParallaxTessellation),
                    new[] { new HarmonyHook(save, nameof(BeforeSave), null, nameof(AfterSave)) });
                hooked = true;
            }
            catch (Exception e)
            {
                hookFailed = true;
                CompatibilityLog.Warn("parallax-tessellation", "Parallax's save could not be hooked ("
                                                               + CompatibilityLog.Reason(e) + "): its tessellation"
                                                               + " edge length is not scaled to the upscaler's render"
                                                               + " size.");
            }
            return hooked;
        }

        // Runs inside Parallax: nothing may escape.
        private static void BeforeSave()
        {
            if (!scaled) return;
            try
            {
                float now;
                if (Parse(read(), out now) && Mathf.Approximately(now, applied)) write(Format(playerValue));
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("parallax-tessellation-save", "Parallax's tessellation edge length could not be"
                                                                    + " put back for its save ("
                                                                    + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Runs inside Parallax after its save, its exception passed on: the scaled value back in.
        private static Exception AfterSave(Exception __exception)
        {
            if (scaled)
            {
                try
                {
                    float now;
                    if (Parse(read(), out now) && Mathf.Approximately(now, playerValue)) write(Format(applied));
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("parallax-tessellation-save", "Parallax's tessellation edge length could"
                                                                        + " not be scaled again after its save ("
                                                                        + CompatibilityLog.Reason(e) + ").");
                }
            }
            return __exception;
        }

        // The reader's value; null where it throws.
        private static string SafeRead(Func<string> reader)
        {
            try
            {
                return reader();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Called from the upscaler's setup and teardown: another build of Parallax costs the
        // scaling, never the rest of them.
        private static void SafeWrite(string text)
        {
            try
            {
                write(text);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("parallax-tessellation-write", "Parallax's tessellation edge length could not be"
                                                                     + " written (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // A float read invariantly.
        private static bool Parse(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // A float written invariantly, round-trip exact.
        private static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
