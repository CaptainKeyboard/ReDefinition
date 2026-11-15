using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // The GPU profiler's markers that RtCamera puts in its command buffers, as
    // src/Probe/GpuProfiler.cs has them: the test player has no profiler, so none are
    // put in.
    internal static class GpuProfiler
    {
        // Never on, so RtCamera adds no markers.
        internal static bool On
        {
            get { return false; }
        }

        // Puts no marker in: there is no profiler to read it.
        internal static void Mark(CommandBuffer buffer, string label)
        {
        }
    }
}

namespace ReDefinition.Proxy
{
    // Whether Windows refused the game's dxgi.dll proxy (src/Proxy/ProxyLoad.cs), which
    // NativeUpscalerLink names first: the test player has the proxy beside its exe and checks nothing.
    internal static class ProxyLoad
    {
        // No refusal to name: the test player does not ask Windows.
        internal static string Refused
        {
            get { return null; }
        }
    }
}

// The few KSP types the linked raytracing sources name (RtMaterialIntent's Parallax terrain, RtCamera.Probe's file name,
// CaptureFolder's root): the test player has no game, no config and no scene.
public class CelestialBody
{
    public string bodyName = "Kerbin";
}

public static class FlightGlobals
{
    public static CelestialBody GetHomeBody() { return null; }
}

public class ConfigNode
{
    public ConfigNode[] GetNodes(string name) { return new ConfigNode[0]; }
    public ConfigNode GetNode(string name) { return null; }
    public string GetValue(string name) { return null; }
}

public class GameDatabase
{
    // No database: RtMaterialIntent reads no Parallax terrain.
    public static GameDatabase Instance { get { return null; } }
    public ConfigNode[] GetConfigNodes(string name) { return new ConfigNode[0]; }
}

public static class HighLogic
{
    public static string LoadedScene { get { return "Player"; } }
}

public static class KSPUtil
{
    public static string ApplicationRootPath { get { return UnityEngine.Application.dataPath + "/.."; } }
}

namespace ReDefinition.Modules
{
    // The three weather switches RtCamera.Weather reads from the add-on's live settings.
    internal sealed class OwnSettings
    {
        public bool RtWeatherFog = true, RtWeatherReachCap, RtWeatherFewerBounces;
    }
}

namespace ReDefinition
{
    // No add-on in the test player: RtCamera.Weather takes its defaults.
    public sealed class ReDefinitionAddon
    {
        internal static ReDefinitionAddon Instance { get { return null; } }
        internal Modules.OwnSettings Live = new Modules.OwnSettings();
    }
}

namespace ReDefinition.Core
{
    // The weather the atmospheres write (src/Common/Weather/WeatherView.cs): none in the test player.
    internal sealed class WeatherView
    {
        public bool Active;
        public float Level, SunTransmission = 1f, VisibilityM = 1e5f, FogDensity;
        public UnityEngine.Color FogColour = UnityEngine.Color.white;
        public bool IsStale(int frameCount) { return true; }
    }

    internal static class WeatherHooks
    {
        public static readonly WeatherView View = new WeatherView();
    }

    // The planner's extra assumptions (src/Common/PlannerHooks.cs): none.
    public static class PlannerHooks
    {
        internal static ForeignAssumption[] ExtraAssumptions;
    }
}

namespace ReDefinition.Raytracing
{
    // Parallax's grass ranges, which RtCamera reads for the meadow's shade (src/Raytracing/Ksp/RtKspMods.cs): the
    // test player has no Parallax, so there is no grass.
    internal static class RtKspMods
    {
        // No grass, so no level of detail to start from.
        internal static float ParallaxGrassLodStart { get { return 0f; } }

        // No grass range about the camera or the vessel.
        internal static float ParallaxGrassRange(out float vesselRange)
        {
            vesselRange = 0f;
            return 0f;
        }

        // No vessel grass: the camera's own position.
        internal static UnityEngine.Vector3 ParallaxVessel(UnityEngine.Vector3 camera)
        {
            return camera;
        }
    }
}
