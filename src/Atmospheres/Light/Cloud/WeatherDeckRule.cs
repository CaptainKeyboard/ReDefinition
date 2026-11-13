using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What may draw a low weather deck (fog, stratus, nimbostratus): the rule of a LAYER that carries "deckRule = 1" in the cfg. Until E40 the type map of every layer
    // was the precipitation level, so at no precipitation the deck drew its first type (Fog, 0 to 210 m on Kerbin) wherever its coverage map passed the threshold: a cream
    // ground fog under fair weather (E40 start 55, wC5o0: 0.957 of the ground region changed with the sky 0.000 closed). The rule decides per texel, from the weather field:
    //   precipitation   the level of PrecipScale: no deck below FlurriesFrom (the band None of the HUD), the full deck from LightFrom; its type is the layer's PrecipType
    //                   (Stratus) up to the level itself, so light rain and drizzle draw stratus and heavy rain nimbostratus, as before for the levels at or above PrecipType
    //   fog             only where the air is near saturation (the dew-point spread of the surface temperature and the humidity map, Magnus as HeightModel.DewPointK) and the
    //                   wind is light; its type is FogType (the first type)
    //   neither         no deck: the coverage of the layer is multiplied by 0 (the cumulus layer draws the sky of fair weather)
    // Rain and its cloud agree both ways (E40 look-correct, RainCoherenceTests): the deck has a coverage floor under precipitation (RainFloor, so no rain falls from a clear sky), and the
    // cumulonimbus layer carries the same rule without fog and without a floor (drawn only as far as it rains).
    // The deck's activity is max(precipitation share, fog share); the coverage map is multiplied by it, and the type is blended from FogType to the precipitation type by the
    // precipitation share. The module applies it to the maps the GPU and every CPU reader (sun dimming, cloud shadow, CloudCoverOracle, the ray pack) share, so there is
    // one source of truth. cloud.deckRule.<layer> 0 gives the old rule back, 1 forces it on with the defaults below.
    // Sources: the fog condition is the aviation rule of thumb "fog is likely when the temperature-dew-point spread is 5 F (2.8 K) or less" (the FAA-published sample
    // question wording; radiation fog needs light wind); the Magnus constants are those of HeightModel (WMO CIMO Guide 2008).
    // [UNKNOWN] starting values, to be tuned against the generator's maps: FogSpreadBandK, FogWindMs, FogWindBandMs (the wind limit of fog has no single value: radiation
    // fog needs a nearly calm surface layer, advection fog a few m/s) and PrecipFullAt (the upper end of the flurries band is the project's, the ramp shape is not).
    internal sealed class WeatherDeckRule
    {
        // The centre and half width of the spread step in K: the fog share is 0.5 at FogSpreadK and goes from 1 to 0 over +- FogSpreadBandK.
        internal float FogSpreadK = 2.8f, FogSpreadBandK = 1.0f;
        // The same for the wind in m/s (the share falls from 1 to 0 over FogWindMs +- FogWindBandMs).
        internal float FogWindMs = 5f, FogWindBandMs = 2f;
        // The precipitation level where the deck begins (PrecipScale.FlurriesFrom) and where it is complete (PrecipScale.LightFrom).
        internal float PrecipFrom = PrecipScale.FlurriesFrom, PrecipFullAt = PrecipScale.LightFrom;
        // The type values (0 to 1 over the layer's type table): the fog type, and the lightest precipitating type (Stratus on the weather decks).
        internal float FogType = 0f, PrecipType = 0.5f;
        // Whether fog air draws the layer (the weather decks); a rain-bearing layer that is no fog (the cumulonimbus layer) has none.
        internal bool Fog = true;
        // The coverage that precipitation itself guarantees, 0 to 1 (cfg deckRainFloor; 0: none): wherever it rains the layer is at least this thick, so that no rain falls from a clear
        // sky. It reaches its full value at a quarter of the way from PrecipFrom to PrecipFullAt (level 0.08 with the defaults) and is 0 below PrecipFrom. The weather decks have it;
        // the cumulonimbus layer does not (it is drawn only as far as it rains, never forced).
        internal float RainFloor = 0f;

        internal static readonly WeatherDeckRule Defaults = new WeatherDeckRule();

        private static float Step(float edge0, float edge1, float x)
        {
            if (!(edge1 > edge0)) return x >= edge1 ? 1f : 0f;
            float t = (x - edge0) / (edge1 - edge0);
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
        }

        // The dew-point spread in K of the air at a temperature (K) and a relative humidity (0 to 1), as HeightModel; a temperature that is no air (NaN, below 150 K) gives a huge spread.
        internal static float SpreadK(float temperatureK, float humidity01)
        {
            if (float.IsNaN(temperatureK) || float.IsNaN(humidity01) || temperatureK < 150f) return 1000f;
            return (float)HeightModel.DewPointSpreadK(temperatureK, humidity01);
        }

        // The share of a texel that is fog air, 0 to 1, from the spread (K) and the wind (m/s).
        internal float FogShare(float spreadK, float windMs)
        {
            float s = 1f - Step(FogSpreadK - FogSpreadBandK, FogSpreadK + FogSpreadBandK, spreadK);
            float w = 1f - Step(FogWindMs - FogWindBandMs, FogWindMs + FogWindBandMs, windMs);
            return s * w;
        }

        // The share of a texel that precipitates, 0 to 1, from the level.
        internal float PrecipShare(float level)
        {
            return Step(PrecipFrom, PrecipFullAt, level);
        }

        // The activity (the factor of the coverage map) and the type value of one texel.
        internal void Texel(float level, float spreadK, float windMs, out float activity, out float typeValue)
        {
            Combine(level, Fog ? FogShare(spreadK, windMs) : 0f, out activity, out typeValue);
        }

        // The coverage floor of a texel (0 to 1) that the precipitation level guarantees.
        internal float FloorOf(float level)
        {
            if (!(RainFloor > 0f)) return 0f;
            return RainFloor * Step(PrecipFrom, PrecipFrom + 0.25f * (PrecipFullAt - PrecipFrom), level);
        }

        // The same from the level and a fog share that is already known (the probe forces both, cloud.deckPrecip.<name> and cloud.deckFog.<name>).
        internal void Combine(float level, float fogShare, out float activity, out float typeValue)
        {
            float sP = PrecipShare(level);
            activity = Math.Max(sP, fogShare);
            float rainType = Math.Max(PrecipType, level);
            typeValue = FogType + (rainType - FogType) * sP;
        }

        // The coverage and type bytes of a layer from the field's maps: cover (0 to 255 per texel) times the activity; the type value as bytes. spread may be null (no humidity map:
        // no fog); wind may be null (calm). All arrays have the same length. forcePrecip and forceFog (0 to 1; negative: the field's) replace the level and the fog share everywhere.
        internal void Apply(byte[] cover, float[] level, float[] spreadK, float[] windMs, byte[] outCover, byte[] outType, float forcePrecip = -1f, float forceFog = -1f)
        {
            for (int i = 0; i < cover.Length; i++)
            {
                float a, t;
                float lv = forcePrecip >= 0f ? forcePrecip : level[i];
                float fog = forceFog >= 0f ? forceFog : Fog ? FogShare(spreadK != null ? spreadK[i] : 1000f, windMs != null ? windMs[i] : 0f) : 0f;
                Combine(lv, fog, out a, out t);
                double c = Math.Max(cover[i] * a, FloorOf(lv) * 255.0);
                outCover[i] = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(c, MidpointRounding.ToEven)));
                outType[i] = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(t * 255.0, MidpointRounding.ToEven)));
            }
        }
    }
}
