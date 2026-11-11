using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the frame driver reads for one frame, gathered by the host (WeatherFrameDriver) so that the composer needs no game object.
    internal struct DriverInput
    {
        // False for a body whose status is Off (or when the atmospheres are off): the view is cleared.
        public bool Active;
        public string BodyName;
        // The field's weather at the camera.
        public WeatherSample Sample;
        public double Ut;
        // The game time since the last frame and the real time, in seconds.
        public double DtGame;
        public float DtReal;
        // The height of the camera above sea level in metres.
        public float AltitudeM;
        // The sun above the local horizon in radians.
        public float SunElevationRad;
        // The camera in the body-fixed frame, for the travelling gust wave.
        public Vector3 CameraBodyPos;
        public int Seed;
        public int FrameCount;
        // The centre and width in C of the body's snow-to-rain change, and the body's own kind (dust, sand, ash, methane) or None.
        public float CentreC, WidthC;
        public PrecipType BodyKind;
        // The level jumped (diagnostic force, teleport, body change): no rate limit this frame.
        public bool Jump;
        // The cloud maps version and layer name of CloudMapsState.
        public long CloudMapsVersion;
        public string CloudLayerName;
        // The vertical optical depth of the cloud over the camera (the field's, or the host's WeatherHooks.CloudOpticalDepth).
        public float OpticalDepth;
        // The colours of the body's look (linear).
        public Color FogColour, VeilGroundColour, VeilSkyColour;
        // Whether the shower pulse is on; whether the freezing-rain flag is allowed.
        public bool ShowerPulse;
        public bool FreezingRain;
        // A level that replaces the field's (diagnostics), or a negative number for none.
        public float LevelOverride;
        // The player's scales of the 1.0 column (settings veilStrength / 0.85, blizzardVisibilityM / 60, skyDimming / 0.6); 1 is the
        // table as it stands.
        public float VeilScale, VisibilityTopScale, DarkeningScale;
        // Setting typeByTemperature: false takes the fractions the field gives instead of the temperature.
        public bool TypeByTemperature;

        // An input with neutral scales, for the host and for tests.
        public static DriverInput Neutral()
        {
            var input = new DriverInput();
            input.VeilScale = 1f;
            input.VisibilityTopScale = 1f;
            input.DarkeningScale = 1f;
            input.TypeByTemperature = true;
            input.LevelOverride = -1f;
            input.BodyName = "";
            input.CloudLayerName = "";
            input.FogColour = new Color(0.78f, 0.80f, 0.84f, 1f);
            input.VeilGroundColour = new Color(0.86f, 0.88f, 0.92f, 1f);
            input.VeilSkyColour = new Color(0.70f, 0.73f, 0.78f, 1f);
            input.CentreC = PrecipTypeModel.DefaultCentreC;
            input.WidthC = PrecipTypeModel.DefaultWidthC;
            return input;
        }
    }

    // Composes the WeatherView of a frame from the field's weather at the camera. The composer is the whole of the frame driver that needs
    // no game object: pure but for its own state, with no allocation in a call. WeatherFrameDriver calls it in LateUpdate, before
    // Camera.onPreCull, as the single writer of the view.
    //
    // Order of a frame: the field level at the camera times the height fade (full to FadeStartM, gone from FadeEndM above sea level) is the
    // target; the level follows it at most 1/90 per game second (Level, the number the forecast and the cover would read); the shower
    // pulse multiplies it for the picture only (DisplayLevel); the look table gives visibility, flakes, veil, light and sound; the type
    // comes from the field's temperature; sun transmission is SunTransmission.From of the optical depth and the sun's elevation.
    // Stale reads: a reader in Update sees the previous frame's view; a view older than two frames is "no weather" (WeatherView.IsStale).
    internal sealed class FrameComposer
    {
        // The fog is a share of the sun's brightness: 0.12 at night to 1 by day.
        internal const float NightFogFloor = 0.12f;

        private bool started;
        private float level;
        private float displayLevel;
        private float visibility = WeatherSample.ClearVisibilityM;
        private float windSpeed;
        private float flakeDirection;
        private TypeMix mix;
        private float lastFogDensity;
        private bool lastFreezing;
        private long layersBuiltFor = -1;
        private readonly FlakeLayer[] layerTemplate = new FlakeLayer[3];

        // The level the field gave, after the height fade and the rate limit, without the shower pulse: what a forecast or a cover reads.
        internal float Level
        {
            get { return level; }
        }

        // The level the picture uses: Level times the shower pulse.
        internal float DisplayLevel
        {
            get { return displayLevel; }
        }

        // The fractions of snow, sleet and rain now (WeatherView carries only the type).
        internal TypeMix Mix
        {
            get { return mix; }
        }

        // The look at the displayed level, as of the last frame.
        internal LookValues Look { get; private set; }

        // The view is cleared and the state forgotten (a scene change).
        internal void Reset()
        {
            started = false;
            level = 0f;
            displayLevel = 0f;
            visibility = WeatherSample.ClearVisibilityM;
            windSpeed = 0f;
            mix = new TypeMix();
        }

        // The height factor: 1 up to fadeStartM, 0 from fadeEndM, smooth between.
        internal static float HeightFactor(float altitudeM, float fadeStartM, float fadeEndM)
        {
            if (float.IsNaN(altitudeM)) return 0f;
            float span = Math.Max(fadeEndM - fadeStartM, 1f);
            float t = (fadeEndM - altitudeM) / span;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
        }

        // The brightness of the fog from the sun's elevation: grey-white by day, dark by night.
        internal static float FogBrightness(float sunElevationRad)
        {
            float sin = (float)Math.Sin(sunElevationRad);
            float t = (sin + 0.1f) / 0.4f;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return NightFogFloor + (1f - NightFogFloor) * t * t * (3f - 2f * t);
        }

        // Writes the view for this frame.
        internal void Compose(ref DriverInput input, WeatherView view)
        {
            if (!input.Active)
            {
                if (view.Active) view.Clear();
                view.Publish(input.FrameCount, false);
                started = false;
                return;
            }

            bool moved = !view.Active;
            bool jump = input.Jump || !started;
            started = true;

            float fadeStart = Tuning.Get("driver.heightFadeStartM", 4000f);
            float fadeEnd = Tuning.Get("driver.heightFadeEndM", 10000f);
            float field = input.LevelOverride >= 0f ? input.LevelOverride : input.Sample.Level01;
            float target = Clamp01(field) * HeightFactor(input.AltitudeM, fadeStart, fadeEnd);
            level = LevelLimiter.Step(level, target, input.DtGame, jump || input.LevelOverride >= 0f);
            float pulse = input.ShowerPulse ? ShowerPulse.Factor(input.Ut, input.Seed, level) : 1f;
            displayLevel = level * pulse;

            LookValues look = PrecipitationLook.Evaluate(displayLevel);
            // The player's scales: the visibility at the top of the scale (weighted by the level squared), the veil, the darkening.
            if (input.VisibilityTopScale > 0f && input.VisibilityTopScale != 1f)
                look.VisibilityM *= (float)Math.Pow(input.VisibilityTopScale, displayLevel * displayLevel);
            look.VeilAlpha = Math.Min(look.VeilAlpha * input.VeilScale, 0.95f);
            look.SkyGreyShare = Clamp01(look.SkyGreyShare * input.DarkeningScale);
            look.AmbientFactor = Math.Max(0f, 1f - (1f - look.AmbientFactor) * input.DarkeningScale);
            Look = look;
            bool falling = displayLevel >= PrecipScale.FlurriesFrom;

            // The type: the body's own kind, else the temperature of the field.
            PrecipType type = PrecipType.None;
            if (falling)
            {
                if (input.BodyKind != PrecipType.None)
                {
                    type = input.BodyKind;
                    mix = new TypeMix();
                }
                else
                {
                    TypeMix targetMix = float.IsNaN(input.Sample.TemperatureK) || !input.TypeByTemperature
                        ? TypeMix.Of(input.Sample.SnowFraction, input.Sample.SleetFraction, input.Sample.RainFraction)
                        : PrecipTypeModel.Mix(input.Sample.TemperatureK, displayLevel, input.CentreC, input.WidthC);
                    mix = PrecipTypeModel.Limit(mix, targetMix, input.DtGame, jump);
                    type = mix.Dominant;
                    lastFreezing = false;
                    if (input.FreezingRain && (type == PrecipType.Rain || type == PrecipType.Sleet) && !float.IsNaN(input.Sample.TemperatureK)
                        && input.Sample.TemperatureK < 273.15f && mix.Snow > 0.2f && mix.Snow < 0.8f && PrecipTypeModel.FreezingRainFlag(input.Ut, input.Seed))
                    {
                        type = PrecipType.FreezingRain;
                        lastFreezing = true;
                    }
                }
            }
            else
            {
                mix = new TypeMix();
            }

            // The wind: the field's, else the look's, along the field's direction (east where it gives none).
            Vector3 wind = input.Sample.WindWorld;
            float horizontal = (float)Math.Sqrt(wind.x * wind.x + wind.y * wind.y);
            Vector3 dir = horizontal > 0.01f ? new Vector3(wind.x / horizontal, wind.y / horizontal, 0f) : new Vector3(1f, 0f, 0f);
            float meanSpeed = horizontal > 0.01f ? horizontal : look.WindMs;
            windSpeed = jump ? meanSpeed : WindNoise.LimitWind(windSpeed, meanSpeed, input.DtReal);
            float gust = WindNoise.Gust(input.Ut, input.CameraBodyPos, dir, displayLevel, input.Seed);
            float gustScale = input.Sample.GustFactor > 0f ? input.Sample.GustFactor : 1f;
            gust = 1f + (gust - 1f) * gustScale;
            float sustained = windSpeed;
            float flakeDir = (float)(Math.Atan2(dir.y, dir.x) * 57.29577951308232);
            flakeDirection = jump ? flakeDir : WindNoise.LimitFlakeDirection(flakeDirection, flakeDir, input.DtReal);

            // The air: the visibility is the smaller of the table's and the field's, and it moves at most a quarter of itself per second.
            float visTarget = Math.Min(look.VisibilityM, input.Sample.VisibilityM > 0f ? input.Sample.VisibilityM : PrecipitationLook.ClearVisibilityM);
            visibility = jump ? visTarget : WindNoise.LimitVisibility(visibility, visTarget, input.DtReal);
            float fogDensity = visibility >= PrecipitationLook.ClearVisibilityM ? 0f : PrecipitationLook.FogDensity(visibility);
            float brightness = FogBrightness(input.SunElevationRad);
            var fogColour = new Color(input.FogColour.r * brightness, input.FogColour.g * brightness, input.FogColour.b * brightness, 1f);

            float sunTransmission = ReDefinition.Core.SunTransmission.From(input.OpticalDepth, input.SunElevationRad);

            // Write the view; Version rises only if a value moved.
            moved |= Set(ref view.Level, displayLevel);
            moved |= view.Type != type;
            view.Type = type;
            PrecipBand band = PrecipScale.BandOf(displayLevel, sustained);
            moved |= view.Band != band;
            view.Band = band;
            view.Active = true;
            if (!ReferenceEquals(view.BodyName, input.BodyName)) view.BodyName = input.BodyName ?? "";

            moved |= Set(ref view.VisibilityM, visibility);
            moved |= Set(ref view.FogDensity, fogDensity);
            moved |= SetColour(ref view.FogColour, fogColour);
            moved |= Set(ref view.SunTransmission, sunTransmission);
            float skyShare = falling ? look.SkyGreyShare : 0f;
            moved |= Set(ref view.SkyDimming, skyShare);
            moved |= Set(ref view.AmbientFactor, falling ? look.AmbientFactor : 1f);
            moved |= Set(ref view.CloudCoverAlongSun, input.Sample.CloudCover01);

            Vector3 windWorld = new Vector3(dir.x * sustained * gust, dir.y * sustained * gust, wind.z);
            moved |= Set(ref view.WindWorld.x, windWorld.x);
            moved |= Set(ref view.WindWorld.y, windWorld.y);
            moved |= Set(ref view.WindWorld.z, windWorld.z);
            moved |= Set(ref view.GustPhase, WindNoise.Phase01(input.Ut, input.CameraBodyPos, dir, input.Seed));

            float veil = falling ? look.VeilAlpha : 0f;
            float skyVeilGain = Tuning.Get("driver.skyVeilGain", 1.1f);
            moved |= Set(ref view.VeilGroundAlpha, veil);
            moved |= Set(ref view.VeilSkyAlpha, falling ? Math.Max(veil, Math.Min(veil * skyVeilGain, 0.97f)) : 0f);
            moved |= SetColour(ref view.VeilGroundColour, Scaled(input.VeilGroundColour, brightness));
            moved |= SetColour(ref view.VeilSkyColour, Scaled(input.VeilSkyColour, brightness));
            moved |= Set(ref view.SkyGreyStart, Tuning.Get("driver.skyGreyStartDeg", 0f));
            moved |= Set(ref view.SkyGreyFull, Tuning.Get("driver.skyGreyFullDeg", 23.6f));

            moved |= Set(ref view.FlakeRatePerS, falling ? look.FlakesPerS : 0f);
            moved |= Set(ref view.FlakeSizeScale, look.FlakeSizeFactor);
            moved |= Set(ref view.FlakeFallMS, falling ? Tuning.Get("driver.fallBaseMs", 1.1f) * look.FallFactor : 0f);
            moved |= Set(ref view.FlakeBrightness, Tuning.Get("driver.flakeBrightness", 1f) * brightness);
            if (view.FlakeSeed != input.Seed)
            {
                view.FlakeSeed = input.Seed;
                moved = true;
            }
            FillLayers(view, look.FlakeSizeFactor);

            if (view.CloudMapsVersion != input.CloudMapsVersion)
            {
                view.CloudMapsVersion = input.CloudMapsVersion;
                moved = true;
            }
            string layer = input.CloudLayerName ?? "";
            if (!ReferenceEquals(view.CloudLayerName, layer) && view.CloudLayerName != layer)
            {
                view.CloudLayerName = layer;
                moved = true;
            }
            view.Readiness = input.Sample.Readiness;
            lastFogDensity = fogDensity;
            view.Publish(input.FrameCount, moved);
        }

        // Whether the last frame set the freezing-rain flag.
        internal bool FreezingRain
        {
            get { return lastFreezing; }
        }

        // The direction in degrees the flakes are blown to (limited in turning speed), for the flake renderer.
        internal float FlakeDirectionDeg
        {
            get { return flakeDirection; }
        }

        // The fog density of the last frame.
        internal float FogDensity
        {
            get { return lastFogDensity; }
        }

        // The three flake layers of the view: widths, shares and sizes from the table (tuning keys flake.<layer>.*), sizes by the level's
        // size factor.
        private void FillLayers(WeatherView view, float sizeFactor)
        {
            long version = Tuning.Version;
            if (layersBuiltFor != version)
            {
                layersBuiltFor = version;
                string[] names = { "near", "mid", "far" };
                float[] width = { 7f, 18f, 36f }, share = { 0.06f, 0.34f, 0.60f }, sizeMin = { 0.10f, 0.03f, 0.02f }, sizeMax = { 0.26f, 0.08f, 0.05f };
                float[] opacity = { 0.5f, 0.95f, 1f };
                for (int i = 0; i < 3; i++)
                {
                    string p = "flake." + names[i] + ".";
                    layerTemplate[i].Width = Tuning.Get(p + "width", width[i]);
                    layerTemplate[i].Share = Tuning.Get(p + "share", share[i]);
                    layerTemplate[i].SizeMin = Tuning.Get(p + "sizeMin", sizeMin[i]);
                    layerTemplate[i].SizeMax = Tuning.Get(p + "sizeMax", sizeMax[i]);
                    layerTemplate[i].Opacity = Tuning.Get(p + "opacity", opacity[i]);
                }
            }
            for (int i = 0; i < 3; i++)
            {
                FlakeLayer t = layerTemplate[i];
                t.SizeMin *= sizeFactor;
                t.SizeMax *= sizeFactor;
                view.FlakeLayers[i] = t;
            }
        }

        private static Color Scaled(Color c, float k)
        {
            return new Color(c.r * k, c.g * k, c.b * k, 1f);
        }

        private static bool Set(ref float field, float value)
        {
            bool moved = WeatherView.Moved(field, value);
            field = value;
            return moved;
        }

        private static bool SetColour(ref Color field, Color value)
        {
            bool moved = WeatherView.Moved(field.r, value.r) || WeatherView.Moved(field.g, value.g) || WeatherView.Moved(field.b, value.b);
            field = value;
            return moved;
        }

        private static float Clamp01(float v)
        {
            return v > 0f ? (v < 1f ? v : 1f) : 0f;
        }
    }
}
