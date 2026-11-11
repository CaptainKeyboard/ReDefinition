using System;
using System.Threading;
using UnityEngine;

namespace ReDefinition.Core
{
    // One of the three layers of falling flakes (near, middle, far): the numbers the precipitation renderer draws a layer with.
    public struct FlakeLayer
    {
        // The width of the layer's box in metres.
        public float Width;
        // The share of the flake rate that falls in this layer.
        public float Share;
        // The smallest and the largest flake size in metres.
        public float SizeMin;
        public float SizeMax;
        public float Opacity;
    }

    // The weather at the camera for this frame, as the frame driver of the atmospheres wrote it: what the fog, the light, the veil, the
    // flakes and the wind do now. The class is sealed and its fields are mutated in place, so a read allocates nothing and a field
    // added later leaves older readers working.
    //
    // Rules: there is ONE writer, the frame driver of the atmospheres (written once per frame, in LateUpdate, before
    // Camera.onPreCull). Readers are on the main thread, from Camera.onPreCull or later in the frame; a read in Update sees the
    // previous frame. A worker thread never reads this class; it copies a WeatherSample. FrameStamp older than the current frame
    // minus two means "no weather". No public member returns a UnityEngine.Object.
    public sealed class WeatherView
    {
        // A value counts as moved when it differs from the old one by more than this; Version rises only then.
        public const float VersionEpsilon = 1e-3f;

        // --- Identity ---
        // Rises when any value moved by more than VersionEpsilon; readers cache on it.
        public int Version;
        // Time.frameCount of the last write.
        public int FrameStamp;
        // False where the atmospheres do not run or have nothing for this body.
        public bool Active;
        public string BodyName = "";

        // --- Precipitation ---
        public PrecipType Type;
        // 0 to 1; PrecipScale gives the band.
        public float Level;
        public PrecipBand Band;

        // --- The air at the camera ---
        public float VisibilityM = WeatherSample.ClearVisibilityM;
        // The exponent-squared fog density the driver wrote into RenderSettings, so that a reader can reproduce it.
        public float FogDensity;
        // Linear colour.
        public Color FogColour = Color.white;
        // The share of the sun's direct light that arrives ALONG the current sun ray at the camera, 0 to 1. The frame driver writes it
        // from SunTransmission.From; raytracing, solar panels and the sky read only this.
        public float SunTransmission = 1f;
        // 0 to 1.
        public float SkyDimming;
        // What the ambient light is multiplied by; 1 is none.
        public float AmbientFactor = 1f;
        public Color AmbientTint = Color.white;
        public float CloudCoverAlongSun;

        // --- Wind ---
        // The wind at the camera in the body-fixed frame (x east, y north, z up), in m/s, with the gust.
        public Vector3 WindWorld;
        // The gust phase from 0 to 1 (WindNoise.Phase01), shared by sound and pictures.
        public float GustPhase;

        // --- The look the host draws ---
        public float VeilGroundAlpha;
        public float VeilSkyAlpha;
        public Color VeilGroundColour = Color.white;
        public Color VeilSkyColour = Color.white;
        // The sun elevation range, in degrees, over which the overhead sky goes from clear to grey.
        public float SkyGreyStart;
        public float SkyGreyFull;
        // Flakes per second over all layers, the size and fall speed factors, the brightness and the seed of the pattern.
        public float FlakeRatePerS;
        public float FlakeSizeScale = 1f;
        public float FlakeFallMS;
        public float FlakeBrightness = 1f;
        public int FlakeSeed;
        public readonly FlakeLayer[] FlakeLayers = new FlakeLayer[3];

        // --- Clouds ---
        // The CloudMapsState.Version the driver copied: raised each time the maps EVE reads were repainted.
        public long CloudMapsVersion;
        // The name of the EVE layer that carries the weather, or empty.
        public string CloudLayerName = "";

        // --- Forecast ---
        public double ForecastHorizonS;
        // Seconds until the level changes by a band; a large number where it does not.
        public float NextChangeInS;

        // How far the field behind the values is along.
        public FieldReadiness Readiness;

        // Whether a value moved by more than VersionEpsilon.
        public static bool Moved(float oldValue, float newValue)
        {
            return Math.Abs(newValue - oldValue) > VersionEpsilon || (float.IsNaN(newValue) != float.IsNaN(oldValue));
        }

        // Ends a write: stamps the frame and raises Version if anything moved.
        public void Publish(int frameCount, bool anyValueMoved)
        {
            FrameStamp = frameCount;
            if (anyValueMoved) Version++;
        }

        // Whether a reader in that frame should treat the view as "no weather".
        public bool IsStale(int frameCount)
        {
            return !Active || FrameStamp < frameCount - 2;
        }

        // The state of a view with no weather: inactive, clear, neutral light.
        public void Clear()
        {
            Active = false;
            Type = PrecipType.None;
            Level = 0f;
            Band = PrecipBand.None;
            VisibilityM = WeatherSample.ClearVisibilityM;
            FogDensity = 0f;
            SunTransmission = 1f;
            SkyDimming = 0f;
            AmbientFactor = 1f;
            CloudCoverAlongSun = 0f;
            WindWorld = Vector3.zero;
            GustPhase = 0f;
            VeilGroundAlpha = 0f;
            VeilSkyAlpha = 0f;
            FlakeRatePerS = 0f;
            FlakeFallMS = 0f;
            ForecastHorizonS = 0.0;
            NextChangeInS = float.MaxValue;
            Readiness = FieldReadiness.Ready;
            Version++;
        }
    }

    // The state of the maps EVE reads, set by the painter of the cloud maps and copied into WeatherView by the frame driver. Thread-safe.
    public static class CloudMapsState
    {
        private static long version;
        private static string layerName = "";

        // Raised each time the maps were repainted.
        public static long Version
        {
            get { return Interlocked.Read(ref version); }
        }

        // The EVE layer that carries the weather.
        public static string LayerName
        {
            get { return Volatile.Read(ref layerName); }
        }

        // Called by the painter after it repainted: raises Version and names the layer.
        public static void Bump(string layer)
        {
            Volatile.Write(ref layerName, layer ?? "");
            Interlocked.Increment(ref version);
        }
    }
}
