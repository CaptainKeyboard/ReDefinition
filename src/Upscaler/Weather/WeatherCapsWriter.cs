using ReDefinition.Core;
using ReDefinition.Proxy;

namespace ReDefinition.Upscaler
{
    // Fills WeatherHooks.Caps, the host's statement to the atmospheres: which upscaler runs, whether frame generation runs, that the
    // host API answers, and the quality tier of the profile. Only the host writes it (HostLink calls Fill every frame).
    internal static class WeatherCapsWriter
    {
        // The caps for this frame. backend is read only where the upscaler is active.
        internal static void Fill(WeatherRenderCaps caps, bool upscalerActive, UpscalerBackend backend,
                                  bool frameGenerationOn, string profile)
        {
            caps.Upscaler = upscalerActive ? KindOf(backend) : UpscalerKind.None;
            caps.FrameGenerationOn = frameGenerationOn;
            caps.HostApiAvailable = true;
            caps.Tier = TierOf(profile);
        }

        // FSR 3 and the AMD DLL are both FSR for the atmospheres: the same mask and history rules apply.
        internal static UpscalerKind KindOf(UpscalerBackend backend)
        {
            switch (backend)
            {
                case UpscalerBackend.Dlss: return UpscalerKind.Dlss;
                case UpscalerBackend.Xess: return UpscalerKind.Xess;
                default: return UpscalerKind.Fsr;
            }
        }

        // The profile's name as its GRAPHICS_PROFILE node gives it (low, medium, high, ultra, max); Auto where none is chosen or the
        // name is unknown.
        internal static QualityTier TierOf(string profile)
        {
            if (string.IsNullOrEmpty(profile)) return QualityTier.Auto;
            switch (profile.ToLowerInvariant())
            {
                case "low": return QualityTier.Low;
                case "medium": return QualityTier.Medium;
                case "high": return QualityTier.High;
                case "ultra": return QualityTier.Ultra;
                case "max": return QualityTier.Max;
                default: return QualityTier.Auto;
            }
        }
    }
}
