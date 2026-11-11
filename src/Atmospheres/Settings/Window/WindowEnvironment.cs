using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the settings window needs to know about the game it runs in: which optional mods are there, whether the main mod answers,
    // and the graphics vendor and upscaler. Plain data, so that the rules of the window are tested with any combination.
    internal sealed class WindowEnvironment
    {
        // EVE (the volumetric clouds), the mod that draws the clouds of a map we paint.
        internal bool EveInstalled;
        // ModularFlightIntegrator, through which the wind reaches the aerodynamics of a vehicle.
        internal bool ModularFlightIntegrator;
        // Rocket Sound Enhancement, whose mixer muffles the sound in a helmet or a cabin.
        internal bool RocketSoundEnhancement;
        // Kerbal Alarm Clock, which can hold the alarms of a storm.
        internal bool KerbalAlarmClock;
        // Whether the main mod is installed, and whether its after-upscaling hook answers.
        internal bool MainModPresent;
        internal bool HostApi;
        // Whether the shell shaders of the vehicle cover are available (the bundle holds them and they compile here).
        internal bool ShellShaders = true;
        // ZTheme restyles the dialogs where it is installed.
        internal bool ZTheme;
        // The name of the wind mod that applies a base wind (empty for none), and the graphics vendor and upscaler as the caps say.
        internal string ForeignWindOwner = "";
        internal string GpuVendor = "";
        internal UpscalerKind Upscaler = UpscalerKind.None;
        // The total video memory in MB; zero where it is not known.
        internal long VramMb;

        // An environment with every optional mod present and the host answering: the plain case of the tests.
        internal static WindowEnvironment Full()
        {
            return new WindowEnvironment
            {
                EveInstalled = true,
                ModularFlightIntegrator = true,
                RocketSoundEnhancement = true,
                KerbalAlarmClock = true,
                MainModPresent = true,
                HostApi = true,
                ShellShaders = true,
                ZTheme = true,
                GpuVendor = "NVIDIA",
                Upscaler = UpscalerKind.Dlss,
                VramMb = 16384
            };
        }

        // An environment with none of the optional mods and no host: the mod alone.
        internal static WindowEnvironment Alone()
        {
            return new WindowEnvironment();
        }

        // Where the wind mod that applies a base wind is named (the wind package fills it); empty for none.
        internal static Func<string> WindOwnerSource = () => "";

        // Reads the game: the loaded assemblies by name, the caps the host wrote, the card.
        internal static WindowEnvironment Detect()
        {
            var env = new WindowEnvironment();
            env.EveInstalled = Loaded("EVEManager") || TypeLookup.Find("Atmosphere.RaymarchedCloudsQualityManager") != null;
            env.ModularFlightIntegrator = Loaded("ModularFlightIntegrator") || TypeLookup.Find("ModularFI.ModularFlightIntegrator") != null;
            env.RocketSoundEnhancement = Loaded("RocketSoundEnhancement");
            env.KerbalAlarmClock = Loaded("KerbalAlarmClock");
            env.ZTheme = Loaded("ZTheme");
            env.MainModPresent = !HostSeams.StandsAlone();
            env.HostApi = WeatherHooks.Caps.HostApiAvailable;
            env.Upscaler = WeatherHooks.Caps.Upscaler;
            env.ShellShaders = true;
            try
            {
                env.ForeignWindOwner = WindOwnerSource() ?? "";
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the wind owner could not be read (" + CompatibilityLog.Reason(e) + ")");
            }
            ReadCard(env);
            return env;
        }

        // Whether an assembly of that simple name is loaded.
        internal static bool Loaded(string simpleName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void ReadCard(WindowEnvironment env)
        {
            try
            {
                ReadEngineCard(env);
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("the graphics card could not be read (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadEngineCard(WindowEnvironment env)
        {
            env.GpuVendor = UnityEngine.SystemInfo.graphicsDeviceVendor ?? "";
            env.VramMb = UnityEngine.SystemInfo.graphicsMemorySize;
        }
    }
}
