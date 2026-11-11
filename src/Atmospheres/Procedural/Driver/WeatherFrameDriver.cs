using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The frame driver of the flight scene: the SINGLE writer of WeatherHooks.View. Each frame, in LateUpdate (so before Camera.onPreCull,
    // where the light is written), it asks the weather field (WeatherHub) for the weather at the flight camera and has FrameComposer write
    // the view; LightWriter then writes the sun and the ambient light in onPreCull. It reads only IWeatherField through WeatherHub, so the
    // A02 legacy bridge, the simulation of B10 or a test field all drive it. Nothing is allocated per frame. A failure logs once, puts the
    // light back and clears the view for the rest of the scene.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class WeatherFrameDriver : MonoBehaviour
    {
        private readonly FrameComposer composer = new FrameComposer();
        private readonly LightWriter light = new LightWriter();
        private bool broken;
        private int bodyIndex = -1;
        private double lastUt = double.NaN;
        private PrecipBand loggedBand = PrecipBand.None;
        private PrecipType loggedType = PrecipType.None;
        private int loggedExternal;
        private bool wasActive;

        // The light writer of the running driver, or null outside the flight scene (the legacy storm effect logs its state).
        internal static LightWriter Light { get; private set; }

        private void Start()
        {
            Light = light;
            AtmospheresLog.Say(StormLog.DriverReady());
        }

        private void LateUpdate()
        {
            if (broken) return;
            try
            {
                WeatherView view = WeatherHooks.View;
                DriverInput input;
                bool have = Gather(out input);
                composer.Compose(ref input, view);
                if (have) light.Update(view, SunLight());
                else light.Update(view, null);
                LogChanges(view);
            }
            catch (Exception e)
            {
                broken = true;
                AtmospheresLog.Failed("atmospheres-driver", "the frame driver failed and is switched off for this scene", e);
                Teardown();
            }
        }

        private void OnDestroy()
        {
            Teardown();
        }

        private void Teardown()
        {
            Quietly(light.Shutdown);
            Quietly(() => WeatherHooks.View.Clear());
            composer.Reset();
            if (ReferenceEquals(Light, light)) Light = null;
        }

        private static void Quietly(Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-driver-teardown", "a step of putting the frame driver away failed", e);
            }
        }

        private static Light SunLight()
        {
            return Sun.Instance != null ? Sun.Instance.sunLight : null;
        }

        // Reads the game for this frame. The per-frame allocation of this method and LogChanges is not covered by an L0 test (they touch
        // the game); the composer is. Measure it in the first L3 series row (GC alloc per frame in flight). False when there is no flight camera or vessel (the input is then inactive).
        private bool Gather(out DriverInput input)
        {
            input = DriverInput.Neutral();
            input.FrameCount = Time.frameCount;
            Vessel vessel = FlightGlobals.ActiveVessel;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (!AtmospheresSettings.Enabled || vessel == null || vessel.mainBody == null || cam == null) return false;
            CelestialBody body = vessel.mainBody;
            BodyClimate climate = Bodies.Get(body);
            if (climate == null || climate.Status.Kind == BodyStatusKind.Off || !body.atmosphere) return false;

            double ut = Planetarium.GetUniversalTime();
            Vector3d position = cam.transform.position;
            double lat = body.GetLatitude(position), lon = body.GetLongitude(position), alt = body.GetAltitude(position);
            WeatherSample sample;
            if (!WeatherHub.Sample(body.flightGlobalsIndex, lat, lon, alt, ut, out sample)) sample = WeatherSample.Clear(body.flightGlobalsIndex);

            input.Active = true;
            input.BodyName = body.name;
            input.Sample = sample;
            input.Ut = ut;
            input.DtGame = double.IsNaN(lastUt) ? 0.0 : Math.Max(ut - lastUt, 0.0);
            lastUt = ut;
            input.DtReal = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            input.AltitudeM = (float)alt;
            input.Seed = climate.Seed;
            input.Jump = bodyIndex != body.flightGlobalsIndex;
            bodyIndex = body.flightGlobalsIndex;
            Vector3d up = vessel.upAxis;
            Vector3d sun = FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 && FlightGlobals.Bodies[0] != null
                ? (FlightGlobals.Bodies[0].position - vessel.GetWorldPos3D()).normalized
                : up;
            input.SunElevationRad = (float)Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(sun, up))));
            input.CameraBodyPos = body.transform.InverseTransformPoint(cam.transform.position);
            PrecipTypeModel.Parameters(climate.Precip, out input.CentreC, out input.WidthC);
            input.BodyKind = PrecipTypeModel.BodyKind(climate.Precip);
            input.CloudMapsVersion = CloudMapsState.Version;
            input.CloudLayerName = CloudMapsState.LayerName;
            Func<double, double, double, double, float> opticalDepth = WeatherHooks.CloudOpticalDepth;
            float depth = opticalDepth != null ? opticalDepth(lat, lon, alt, ut) : 0f;
            input.OpticalDepth = depth > 0f ? depth : sample.OpticalDepthVertical;
            input.FogColour = climate.Look.FogColour;
            input.VeilGroundColour = climate.Look.VeilGroundColour;
            input.VeilSkyColour = climate.Look.VeilSkyColour;
            input.ShowerPulse = Tuning.Flag("driver.showerPulse", true);
            input.FreezingRain = Tuning.Flag("driver.freezingRain", false);
            float forced = AtmospheresSettings.ForceLevel;
            input.LevelOverride = forced >= 0f ? forced : -1f;
            input.VeilScale = AtmospheresSettings.VeilStrength / 0.85f;
            input.VisibilityTopScale = AtmospheresSettings.BlizzardVisibilityM / 60f;
            input.DarkeningScale = AtmospheresSettings.SkyDimming / 0.6f;
            input.TypeByTemperature = AtmospheresSettings.TypeByTemperature;
            return true;
        }

        // A line when the band or the type changed, and when other mods start writing the light we scale. Strings are built only then.
        private void LogChanges(WeatherView view)
        {
            if (view.Active != wasActive)
            {
                wasActive = view.Active;
                AtmospheresLog.Say(wasActive ? "frame driver: weather on for " + view.BodyName : "frame driver: weather off");
            }
            if (!view.Active) return;
            if (view.Band != loggedBand || view.Type != loggedType)
            {
                loggedBand = view.Band;
                loggedType = view.Type;
                AtmospheresLog.Say(StormLog.DriverBand(view, composer.Level, composer.Mix, light));
            }
            int external = light.ExternalWrites;
            if (external != loggedExternal && external >= 10 && external >= loggedExternal * 2)
            {
                loggedExternal = external;
                AtmospheresLog.Say("frame driver: other mods rewrote the ambient light or the sun " + external + " times while the weather scaled them");
            }
        }
    }
}
