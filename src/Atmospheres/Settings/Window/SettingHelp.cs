using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The words of the settings window beyond the key table's names (SettingDef.Tooltip is the row's name in Title Case): one plain
    // explanation for every setting, the text of every reason a control is greyed out, and the names of the groups. Strings live here
    // alone, so that they can be translated.
    internal static class SettingHelp
    {
        private static readonly Dictionary<string, string> Help = Build();

        // The explanation of a setting; empty for a key that is not a setting.
        internal static string For(string key)
        {
            string text;
            return Help.TryGetValue(key, out text) ? text : "";
        }

        // The keys that have an explanation.
        internal static IEnumerable<string> Keys
        {
            get { return Help.Keys; }
        }

        private static Dictionary<string, string> Build()
        {
            return new Dictionary<string, string>
            {
                // General
                { "enabled", "Switches the whole mod off or on. Off, nothing of it runs." },
                { "weatherEffects", "The master switch of the weather: clouds, precipitation, wind, cover, sound and forecast. Off, the game looks and runs as without the mod." },
                { "yearLengthFactor", "How long the seasons last against the body's own year. 2 makes every season twice as long, 0.5 half as long." },
                { "seasonMode", "Global Summer and Global Winter hold every body at midsummer or midwinter. Global Seasons runs one season for both hemispheres. Real Seasons follows each body's orbit and gives the hemispheres opposite seasons where the body is tilted." },
                { "qualityTier", "How much the weather may cost. Auto follows the main mod's graphics profile and takes Low below 10 GB of video memory; Max has no limit. A tier changes cost and never taste." },
                { "qualityGovernor", "Lets the mod lower its own cost when frames run short and raise it again later." },

                // Clouds
                { "cloudSource", "Who draws the clouds. Ours uses the mod's own volume pass, Eve paints the weather into EVE's clouds, Both does both, None draws no clouds." },
                { "weatherCompose", "Where the fog veil and the flakes are put on the picture. Scene is before the upscaler, Host is after it through the main mod. Auto picks Scene unless the pair of graphics card and upscaler is recorded." },
                { "useEveWeather", "Lets EVE's own weather layers stay on the bodies where this mod paints no map." },
                { "eveLayerCap", "The most EVE cloud layers the mod paints on one body. Fewer layers cost less." },
                { "lightWeatherFrequency", "How often light weather forms: 0 none, 1 the body's own rate, 2 twice as often." },
                { "heavyShare", "How large a share of the weather is heavy: 0 none, 1 the body's own share, 2 twice as much." },
                { "typeByTemperature", "Lets the air temperature decide whether it snows, sleets or rains. Off, the type is the body's usual one." },

                // Wind
                { "windSource", "Whose wind is used. Auto leaves the base wind to a wind mod where one is installed and adds our storm wind on top; Ours replaces it; Foreign and Off leave our wind off while the visuals still draw." },
                { "windInjection", "How the wind reaches a vehicle. Aero changes the air speed the drag sees and needs ModularFlightIntegrator; Force pushes the parts. Auto picks Aero where it can." },
                { "windForces", "Lets the wind push vehicles. Off, the wind is seen and heard but does not move anything." },
                { "windStrength", "How strong the wind is against a vehicle: 0 none, 1 as simulated, 2 twice as strong." },
                { "windOnLanded", "Lets the wind move vehicles that stand on the ground: light ones move when the wind beats their friction. Off, only flying vehicles feel it." },
                { "gustStrength", "How hard the sudden gusts hit. Gusts are short bursts of wind above the steady wind; 0 gives a smooth wind, 1 the simulated gusts, 2 gusts twice as strong." },
                { "solarDimming", "Lets cloud and precipitation dim the solar panels." },
                { "solarDimmingStrength", "How much of the dimming the panels get: 0 none, 1 all of it." },
                { "visibilityExport", "Tells other mods how far one can see in fog and precipitation." },

                // Simulation
                { "genEnabled", "Runs the weather generator. Off, there is no weather: no moving systems, no precipitation and no wind from the field." },
                { "simGrid", "How fine the weather grid is against the body's own: Coarse costs least, Fine is most detailed. Auto takes the body's own setting." },
                { "simLevels", "How many layers of air the simulation has. 0 takes the body's own number." },
                { "simSliceMs", "The most time in milliseconds the simulation may take in one frame. A smaller slice avoids hitches and catches up slower." },
                { "genSeed", "The number the weather grows from. The same number gives the same weather on the same day." },

                // Volume
                { "volumePass", "Draws the clouds and the curtains of precipitation as a volume. Off, only the flat parts are drawn." },
                { "volumeResolution", "The size of the volume against the picture. 1 is full size; 0.5 and 0.25 cost less and look softer." },
                { "volumeSteps", "How finely the volume is sampled along each ray. More steps look smoother and cost more." },
                { "volumeCompose", "Where the volume is put on the picture. Scene is before the upscaler, PostUpscale is after it and needs the main mod." },

                // Precipitation
                { "precipDensity", "How dense the falling precipitation is drawn: 0 none, 1 as simulated, 2 twice as dense." },
                { "precipSnow", "Draws falling snow." },
                { "precipRain", "Draws falling rain and freezing rain." },
                { "precipSleet", "Draws sleet." },
                { "precipDust", "Draws falling dust." },
                { "precipSand", "Draws blowing sand." },
                { "precipAsh", "Draws falling ash." },
                { "precipMethane", "Draws methane precipitation." },
                { "stormCullDistance", "Leaves out terrain beyond what the storm's fog shows. Saves frame time in a blizzard." },
                { "fogBrightness", "How bright the fog of the weather is." },
                { "veilStrength", "How much a heavy storm washes out the picture." },
                { "blizzardVisibilityM", "How far one can see in the worst blizzard, in metres." },
                { "skyDimming", "How much a storm darkens the sky." },

                // Light
                { "lightInMedium", "Lets lamps and headlights light up fog and falling precipitation." },
                { "lightCount", "The most lamps that scatter light at once. Fewer lamps cost less." },
                { "lightInMediumStrength", "How strong the glow of the lamps is in fog and precipitation." },
                { "aurora", "The northern and southern lights. Auto draws one where the body has them and no other mod draws one; Shell2D is a flat shell, Volumetric a volume in EVE's clouds." },
                { "auroraAmbientTint", "How much the aurora tints the light on the ground with a faint green." },
                { "cameraFrost", "Lets frost grow on the camera in bitter cold." },

                // Audio
                { "weatherAudio", "Plays the sound of wind, precipitation and steps in weather." },
                { "weatherAudioVolume", "The loudness of the weather sounds." },
                { "audioWind", "Plays the wind." },
                { "audioPrecip", "Plays rain, sleet and snow." },
                { "audioFootsteps", "Plays the steps of a Kerbal on foot." },
                { "audioFootstepsSurface", "Lets the steps sound like the ground under them." },
                { "audioDistantStorm", "Plays a faint rumble of storms far away. Off by default." },
                { "audioQuietSnow", "Plays a faint room tone over calm snow cover." },
                { "audioMuffling", "Who muffles the sound in a helmet or a cabin. Own is built in, Rse hands it to Rocket Sound Enhancement." },
                { "eveSound", "Which wind-storm loop is heard where EVE has one. Auto picks one so that the two do not play together." },
                { "audioBedSource", "What the wind is made of: Synth is made on the fly, Recorded is a sound file, Mix uses both." },
                { "audioProbeSolo", "For test runs: mutes every other sound so that only the weather sounds are measured." },

                // Cover
                { "groundCover", "Lets snow, sand and dust cover the ground and the vehicles." },
                { "coverStrength", "How thick the cover is: 0 none, 1 as simulated." },
                { "vehicleCover", "When a vehicle carries cover: Off never, Landed while it stands on the ground, Always." },
                { "tracks", "Lets wheels and feet leave tracks in the cover." },
                { "coverDrift", "How far the wind moves the cover about." },

                // Ice
                { "seaIce", "Ice on the sea. Orbit shows it in the map view only, OrbitAndGround also on the surface, Physical adds a solid sheet (experimental)." },
                { "iceDrawDistanceKm", "How far from the camera the ice is drawn, in kilometres." },
                { "iceCollision", "Makes the ice solid so that vehicles can drive on it. Experimental." },

                // Forecast
                { "forecastEnabled", "Shows the forecast window, the line in the flight view and the weather in the map." },
                { "alertsEnabled", "Warns before bad weather reaches a place you watch." },
                { "alertLeadHours", "How many hours before the weather arrives the warning comes." },
                { "alertMinLevel", "The weakest level that raises a warning." },
                { "alertWindMs", "The wind speed in metres per second that raises a warning." },
                { "alertWarpStop", "Stops time warp when a warning comes." },
                { "alertKac", "Makes an alarm in Kerbal Alarm Clock for each warning." },
                { "alertMessageApp", "Puts each warning into the message app." },
                { "alertScreenMessage", "Shows each warning as a message on the screen." },
                { "forecastOverlay", "Draws the weather systems and their tracks in the map view." },
                { "alertSites", "The places to watch, besides the active vessel, as names separated by commas." },
                { "forecastHorizonHours", "How many hours ahead the forecast looks." },
                { "forecastUncertainty", "How uncertain the far forecast is made: 0 exact, 1 as uncertain as the simulation allows." },
                { "launchMaxLevel", "The strongest precipitation at which the launch indicator still says go." },
                { "launchMaxWindMs", "The strongest wind in metres per second at which the launch indicator still says go." },
                { "launchMinVisibilityM", "The shortest visibility in metres at which the launch indicator still says go." },
                { "launchLeadHours", "How many hours ahead the launch indicator looks." },

                // Orbit
                { "orbitBodies", "Which planets show weather in the map view: the focused one, the visible ones, or all." },
                { "orbitOverlay", "Shows clouds and snow on the planets in the map view." },
                { "orbitMapWidth", "The width of the weather map in the map view, in pixels. Wider maps are sharper and use more memory." },

                // Diagnostics
                { "forceLevel", "For screenshots and tests: a precipitation level from 0 to 1 at the camera whatever the weather is. Negative leaves it to the weather." },
                { "forceType", "For screenshots and tests: the type of precipitation to show. Auto leaves it to the weather." },
                { "forceBody", "For screenshots and tests: the body the forced weather is on. Empty is the body you are at." },
                { "forcePhase", "For screenshots and tests: a season from 0 to 1 that replaces the clock. Negative leaves it to the clock." },
                { "atmoDebug", "Shows how the weather is made: which pass drew each pixel, the level at the camera, or the maps." }
            };
        }

        // The texts of the reasons a control is greyed out or a choice is not offered. A reason of the key table (GreyReasons) or one of the
        // window's own (the ids below GreyReasons.All).
        internal const string ModOff = "mod-off";
        internal const string NeedsShellShaders = "needs-shell-shaders";
        internal const string NeedsKac = "needs-kac";
        internal const string NeedsModularFlightIntegrator = "needs-modular-flight-integrator";
        internal const string NeedsRse = "needs-rse";
        internal const string NeedsHostForCompose = "needs-host-compose";
        internal const string NeedsForeignWind = "needs-foreign-wind";

        private static readonly Dictionary<string, string> Reasons = new Dictionary<string, string>
        {
            { ModOff, "Atmospheres is switched off." },
            { AtmospheresSettings.GreyReasons.NeedsMaster, "Weather Effects is off." },
            { AtmospheresSettings.GreyReasons.NeedsEve, "EVE is not installed." },
            { AtmospheresSettings.GreyReasons.NeedsHost, "The main mod is not installed or does not answer." },
            { AtmospheresSettings.GreyReasons.NeedsSim, "Weather Simulation is off." },
            { AtmospheresSettings.GreyReasons.NeedsVolume, "Volumetric Clouds And Curtains is off." },
            { AtmospheresSettings.GreyReasons.NeedsAudio, "Weather Audio is off." },
            { AtmospheresSettings.GreyReasons.NeedsForecast, "Forecast is off." },
            { AtmospheresSettings.GreyReasons.NeedsAlerts, "Weather Alerts is off." },
            { AtmospheresSettings.GreyReasons.NeedsWindForces, "Wind Forces On Vehicles is off." },
            { AtmospheresSettings.GreyReasons.NeedsGroundCover, "Ground Cover is off." },
            { AtmospheresSettings.GreyReasons.NeedsSeaIce, "Sea Ice is off." },
            { AtmospheresSettings.GreyReasons.NeedsLightInMedium, "Light In Fog And Precipitation is off." },
            { AtmospheresSettings.GreyReasons.NeedsPrecip, "Precipitation Density is 0." },
            { AtmospheresSettings.GreyReasons.NeedsSolarDimming, "Solar Panel Dimming is off." },
            { NeedsShellShaders, "The shaders of the vehicle cover are not available." },
            { NeedsKac, "Kerbal Alarm Clock is not installed." },
            { NeedsModularFlightIntegrator, "ModularFlightIntegrator is not installed." },
            { NeedsRse, "Rocket Sound Enhancement is not installed." },
            { NeedsHostForCompose, "The main mod is not installed or does not answer." },
            { NeedsForeignWind, "No other wind mod is installed." }
        };

        // The text of a reason id; the id itself where it has none.
        internal static string ReasonText(string id)
        {
            string text;
            return id != null && Reasons.TryGetValue(id, out text) ? text : (id ?? "");
        }

        // The ids that have a text.
        internal static IEnumerable<string> ReasonIds
        {
            get { return Reasons.Keys; }
        }

        // The title and the order of the groups of the window.
        internal static string CategoryTitle(string category)
        {
            switch (category)
            {
                case AtmospheresSettings.Categories.Volume: return "Volume";
                case AtmospheresSettings.Categories.Light: return "Light And Frost";
                case AtmospheresSettings.Categories.Cover: return "Surface Cover";
                case AtmospheresSettings.Categories.Ice: return "Sea Ice";
                case AtmospheresSettings.Categories.Orbit: return "Orbit View";
                default: return category;
            }
        }
    }
}
