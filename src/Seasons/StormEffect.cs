using System;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // The look of a snow storm in flight. Unity's global fog, a veil in front of the camera (light toward the ground, dark storm grey
    // toward the zenith), falling snow, a lower ambient light and EVE's snow particle field where EVE has one. It reads the storm
    // intensity (StormModel) at the active vessel, moves a smoothed copy toward it and applies it. It sets no image effect and no
    // command buffer, so it does not meet the upscalers or raytracing of ReDefinition. Every global it changes is put back when the
    // storm ends and when the scene is left.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class StormEffect : MonoBehaviour
    {
        // How fast the applied intensity follows the target, per second of real time: a full change takes two seconds.
        internal const float FollowPerSecond = 0.5f;

        // Below this applied intensity nothing is drawn and the globals are put back.
        internal const float ActiveBelow = 0.002f;

        // The fog colour by day, before StormLook.FogBrightness.
        private static readonly Color FogDay = new Color(0.80f, 0.82f, 0.85f, 1f);

        private float current;
        private float target;
        private bool targetForced;
        private bool active;
        private bool fogSaved;
        private bool savedFog;
        private FogMode savedMode;
        private float savedDensity;
        private Color savedColour;
        private float writtenDensity = -1f;
        private bool outsideChange;
        private float settledLogged = -1f;
        private int stormsSeen;
        private double yearSeconds;
        private StormVeil veil;
        private StormFlakes flakes;
        private readonly StormAmbient ambient = new StormAmbient();
        private float ambientFactor = 1f;
        private float eveRaise;
        private bool hooked;

        private void Start()
        {
            veil = new StormVeil();
            flakes = new StormFlakes();
            Debug.Log(Log.Tag + " Seasons: storm effect ready in flight: " + (SeasonsSettings.StormsEnabled ? "storms on" : "storms off")
                      + (SeasonsSettings.ForceStorm >= 0f ? ", FORCED STORM " + SeasonsSettings.ForceStorm.ToString("0.00", CultureInfo.InvariantCulture) : "")
                      + ", seed " + SeasonsSettings.StormSeed + ", visibility at full " + SeasonsSettings.StormVisibilityM.ToString("0", CultureInfo.InvariantCulture)
                      + " m, veil " + SeasonsSettings.StormVeil.ToString("0.00", CultureInfo.InvariantCulture)
                      + ", snowfall " + (SeasonsSettings.StormSnowfall ? "on" : "off")
                      + ", sky darkening " + SeasonsSettings.StormSkyDarkening.ToString("0.00", CultureInfo.InvariantCulture)
                      + ", EVE snow " + (SeasonsSettings.StormEveSnow ? "on" : "off") + " (" + EveSnowLink.Summary + ")");
        }

        // Runs for every camera before it culls, after every Update and LateUpdate: the ambient light and EVE's snow coverage are
        // written here, once per frame, so that nobody else's write of the frame comes after ours.
        private void OnPreCull(Camera camera)
        {
            if (!active) return;
            ambient.Apply(ambientFactor);
            if (eveRaise > 0f) EveSnowLink.Raise(eveRaise);
        }

        private void Update()
        {
            target = Target();
            float step = FollowPerSecond * Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            current = Mathf.MoveTowards(current, target, step);
        }

        private void LateUpdate()
        {
            if (current > ActiveBelow)
            {
                Apply();
                return;
            }
            if (active) End();
        }

        private void OnDestroy()
        {
            if (active) End();
            if (veil != null) veil.Dispose();
            if (flakes != null) flakes.Dispose();
        }

        // The intensity the active vessel is in now, 0 to 1.
        private float Target()
        {
            targetForced = false;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!SeasonsSettings.Enabled || vessel == null || vessel.mainBody == null) return 0f;
            CelestialBody body = vessel.mainBody;
            if (!SeasonClock.Applies(body.name) || !body.atmosphere || body.orbit == null) return 0f;
            double height = StormLook.HeightFactor(vessel.altitude);
            if (height <= 0) return 0f;
            yearSeconds = SeasonClock.YearSeconds(body.orbit.period, SeasonsSettings.YearLengthFactor);
            double scheduled = StormModel.Intensity(Planetarium.GetUniversalTime(), vessel.latitude, yearSeconds, SeasonsSettings.ForcePhase,
                SeasonsSettings.WinterSnowLine, SeasonsSettings.StormSeed, SeasonsSettings.StormFrequency);
            targetForced = SeasonsSettings.ForceStorm >= 0f;
            return (float)(StormModel.Resolve(SeasonsSettings.ForceStorm, SeasonsSettings.StormsEnabled, scheduled) * height);
        }

        private void Apply()
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (cam == null || vessel == null) return;
            if (!active) Begin(cam);

            double visibility = StormLook.VisibilityMetres(current, SeasonsSettings.StormVisibilityM);
            Vector3 up = vessel.upAxis;
            double sinSun = 0;
            if (FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 && FlightGlobals.Bodies[0] != null)
            {
                Vector3d toSun = (FlightGlobals.Bodies[0].position - vessel.GetWorldPos3D()).normalized;
                sinSun = Vector3d.Dot(toSun, vessel.upAxis);
            }
            float brightness = (float)StormLook.FogBrightness(sinSun);
            Color colour = new Color(FogDay.r * brightness, FogDay.g * brightness, FogDay.b * brightness, 1f);

            float density = (float)StormLook.FogDensity(visibility);
            if (writtenDensity >= 0f && !outsideChange && Mathf.Abs(RenderSettings.fogDensity - writtenDensity) > 1e-6f)
            {
                outsideChange = true;
                Debug.LogWarning(Log.Tag + " Seasons: storm: another mod changes RenderSettings.fogDensity while the storm runs (" + RenderSettings.fogDensity.ToString("R", CultureInfo.InvariantCulture)
                                 + " against " + writtenDensity.ToString("R", CultureInfo.InvariantCulture) + "); the storm sets it again every frame");
            }
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = density;
            RenderSettings.fogColor = colour;
            writtenDensity = density;

            // The veil: toward the ground the light fog colour, toward the zenith the storm grey, heavier (StormLook.SkyGreyShare).
            float darkening = SeasonsSettings.StormSkyDarkening;
            Color skyGrey = new Color(StormLook.SkyGreyDay[0] * brightness, StormLook.SkyGreyDay[1] * brightness, StormLook.SkyGreyDay[2] * brightness, 1f);
            float groundAlpha = (float)StormLook.VeilAlpha(current, SeasonsSettings.StormVeil);
            float skyAlpha = Mathf.Max(groundAlpha, (float)StormLook.SkyVeilAlpha(current, SeasonsSettings.StormVeil));
            veil.Show(cam, up, groundAlpha, colour, skyAlpha, skyGrey, current, darkening);
            if (SeasonsSettings.StormSnowfall) flakes.Show(cam, up, current, Planetarium.GetUniversalTime(), SeasonsSettings.StormSeed, brightness);
            else flakes.Hide();
            ambientFactor = (float)StormLook.AmbientFactor(current, darkening);
            eveRaise = SeasonsSettings.StormEveSnow ? current : 0f;
            if (SeasonsSettings.StormEveSnow) EveSnowLink.Scan(vessel.mainBody.name);
            if (Mathf.Abs(current - target) < 0.005f && Mathf.Abs(target - settledLogged) > 0.01f)
            {
                settledLogged = target;
                Debug.Log(Log.Tag + " Seasons: storm settled at intensity " + target.ToString("0.00", CultureInfo.InvariantCulture)
                          + (targetForced ? " (forced)" : " (schedule)")
                          + ": visibility " + visibility.ToString("0", CultureInfo.InvariantCulture) + " m, fog density "
                          + RenderSettings.fogDensity.ToString("0.00000", CultureInfo.InvariantCulture) + " read back from RenderSettings (fog "
                          + RenderSettings.fog + ", " + RenderSettings.fogMode + "), veil alpha "
                          + StormLook.VeilAlpha(current, SeasonsSettings.StormVeil).ToString("0.00", CultureInfo.InvariantCulture)
                          + ", sky veil alpha " + skyAlpha.ToString("0.00", CultureInfo.InvariantCulture)
                          + ", flakes " + (SeasonsSettings.StormSnowfall ? flakes.Count.ToString(CultureInfo.InvariantCulture) : "off")
                          + ", fog brightness " + brightness.ToString("0.00", CultureInfo.InvariantCulture)
                          + ", ambient factor " + ambientFactor.ToString("0.00", CultureInfo.InvariantCulture) + ": " + ambient.DescribeScaling()
                          + " (baseline rewritten by others " + ambient.ExternalWrites + " times)"
                          + ", EVE snow fields raised " + (SeasonsSettings.StormEveSnow ? EveSnowLink.Fields.ToString(CultureInfo.InvariantCulture) : "off"));
            }
        }

        // The first frame of a storm: remember the fog, create the pieces and say which path is used.
        private void Begin(Camera cam)
        {
            active = true;
            stormsSeen++;
            outsideChange = false;
            settledLogged = -1f;
            if (!fogSaved)
            {
                savedFog = RenderSettings.fog;
                savedMode = RenderSettings.fogMode;
                savedDensity = RenderSettings.fogDensity;
                savedColour = RenderSettings.fogColor;
                fogSaved = true;
            }
            string flakeShader = SeasonsSettings.StormSnowfall ? flakes.Prepare(cam) : "off";
            string veilShader = veil.Prepare(cam);
            if (!hooked)
            {
                Camera.onPreCull += OnPreCull;
                hooked = true;
            }
            Vessel active0 = FlightGlobals.ActiveVessel;
            int eveFields = SeasonsSettings.StormEveSnow && active0 != null && active0.mainBody != null ? EveSnowLink.Scan(active0.mainBody.name) : 0;
            Debug.Log(Log.Tag + " Seasons: storm " + stormsSeen + " begins: path RenderSettings fog (ExponentialSquared) + sky veil + flakes, ambient light and EVE snow coverage in Camera.onPreCull, no image effect and no command buffer; camera "
                      + cam.name + " renders " + cam.actualRenderingPath + ", HDR " + cam.allowHDR + ", MSAA " + cam.allowMSAA
                      + "; veil shader " + veilShader + "; flake shader " + flakeShader
                      + "; fog before: " + (savedFog ? "on" : "off") + " " + savedMode + " " + savedDensity.ToString("0.#####", CultureInfo.InvariantCulture)
                      + "; ambient before: " + RenderSettings.ambientMode + " " + StormAmbient.Describe()
                      + "; EVE snow field: " + (!SeasonsSettings.StormEveSnow ? "off by setting" : EveSnowLink.Controllable
                          ? (eveFields > 0 ? "found " + eveFields + " (" + EveSnowLink.Names() + "), raised with the storm" : "found in EVE's code but no snow field is loaded for this body")
                          : EveSnowLink.Summary));
        }

        // The storm is over: the fog is as it was, the veil and the flakes are hidden.
        private void End()
        {
            active = false;
            if (fogSaved)
            {
                RenderSettings.fog = savedFog;
                RenderSettings.fogMode = savedMode;
                RenderSettings.fogDensity = savedDensity;
                RenderSettings.fogColor = savedColour;
                fogSaved = false;
            }
            writtenDensity = -1f;
            settledLogged = -1f;
            eveRaise = 0f;
            if (hooked)
            {
                Camera.onPreCull -= OnPreCull;
                hooked = false;
            }
            ambient.Restore();
            EveSnowLink.Release();
            if (veil != null) veil.Hide();
            if (flakes != null) flakes.Hide();
            Debug.Log(Log.Tag + " Seasons: storm ends: fog restored to " + (RenderSettings.fog ? "on" : "off") + " " + RenderSettings.fogMode + " "
                      + RenderSettings.fogDensity.ToString("0.#####", CultureInfo.InvariantCulture) + ", ambient restored to " + StormAmbient.Describe()
                      + ", EVE snow coverage put back, veil and flakes hidden");
        }
    }
}
