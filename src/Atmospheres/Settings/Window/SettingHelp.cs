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
                { "ownClouds", "Draws the clouds with the mod's own volumetric cloud system: a planet-wide stack of layers marched from the ground, from inside and from above. It runs where Cloud Source is Ours, or where EVE is not installed." },
                { "layerCap", "The most cloud layers one picture draws, from 1 to 4. The lowest layers are kept first. Fewer layers cost less." },
                { "layerStepQuality", "How finely the own clouds are marched: Low takes 32 steps per layer and 2 towards the sun, Medium 48 and 3, High 64 and 4. More steps look smoother and cost more." },
                { "layerStepGrowth", "How fast the march steps grow inside a cloud as the light dies out. 0 keeps the step fixed, 2 is the measured value, higher is cheaper and softer in thick cloud." },
                { "cloudNoise", "Shapes the clouds with noise: ragged edges, cauliflower tops and holes. Off, the clouds are the smooth maps alone and cost less." },
                { "cloudNoiseUntile", "Breaks the repetition of the noise, so that a wide deck shows no regular pattern." },
                { "cloudNoiseCurl", "Swirls the edge of the clouds with a curl noise, for billowing shapes." },
                { "cloudNoiseTier", "The size of the noise volumes: Low 32 cells wide for the shape (35 KB), Medium 64 (0.4 MB), High 128 (2.4 MB). Larger volumes show finer detail and use more video memory." },
                { "cloudNoiseDistanceLod", "Fades the finest detail out where it could only shimmer, with distance. Off, the detail is read at every step and costs more." },
                { "cloudFineDetail", "Adds a second, finer octave of detail to the cloud edge. Off, the edge is smoother." },
                { "cloudEntryRefinement", "Finds the front of a cloud with finer steps, which calms the grain of a single frame and costs more. Off by default." },
                { "cloudHashDither", "Offsets the march of each pixel with white noise. Off, the offset follows a regular pattern that can show as a faint grid in a single frame." },
                { "cloudLightQuality", "How the sunlight in the clouds is marched: Low takes 2 steps towards the sun and 2 scattering octaves, Medium 3 and 3, High 4 and 4. More steps give softer, brighter cloud bodies and cost more." },
                { "cloudLightOctaves", "How many times the light scatters inside a cloud: 1 is single scattering, 4 gives the soft glow of a thick cloud. The quality level sets the upper limit." },
                { "cloudPowder", "The dark rim a cloud shows towards the sun, where the light cannot go deep. 0 is none, 1 the strongest." },
                { "cloudSunTint", "Reddens the sunlight with the thickness of the air it crossed, so that clouds turn gold and red at sunrise and sunset." },
                { "cloudMoonLights", "The most moons and planets that light the clouds at night. 0 leaves the night clouds to the sky alone." },
                { "cloudAmbientTaps", "Darkens the underside of a cloud by marching straight up through the cloud above it. Off, a cheaper estimate from the local density is used." },
                { "cloudAmbientReuse", "With Cloud Ambient Taps: 0 marches up at every lit step, 1 reuses the sun march's result, 2 reuses it and moves it by the ray's progress. Higher is cheaper." },
                { "cloudLightCache", "Keeps the light of the clouds in a small 3D cache around the camera, refilled every frame, instead of marching to the sun at every step. Saves frame time; the look stays close to the default. Off by default." },
                { "cloudShadows", "Lets the own clouds shade the ground, the vessels and the sea." },
                { "cloudShadowQuality", "How finely the cloud shadow is made: Low takes 4 steps and 1 direction across the sun's disc, Medium 8 and 4, High 12 and 6. More gives softer shadow edges." },
                { "cloudShadowFloor", "The share of the direct sunlight that always remains under a cloud. 0 gives the full cloud shadow, 1 no shadow." },
                { "cloudReconMode", "How the cloud picture is filled each frame. Full and Block follow Cloud Resolution. Low marches a smaller picture and ignores Cloud Resolution." },
                { "cloudResolution", "How many pixels of the clouds are marched each frame. 1 marches a half-size picture (a quarter of the pixels) and stretches it. 2 marches one pixel of each 2 x 2 block, as many as 1, for a full-size picture at about the same cost. 3 (a ninth) and 4 (a sixteenth) are faster and softer when the camera moves." },
                { "cloudRefineAtContours", "With Cloud Entry Refinement: finds the front of a cloud with the finer steps only where the picture has a cloud edge, and leaves the flat parts of a cloud on the normal steps. Saves frame time but keeps little of what the refinement gains, because most of that gain is on the flat faces. Off by default." },
                { "cloudRefineBisect", "Finds the front of a cloud by halving the step that first reaches it this many times: a few extra reads at each front, a cheap stand-in for Cloud Entry Refinement. 0 is off. It acts only where the full refinement is off. Off by default." },
                { "cloudRefineEdgeMask", "Runs the fine entry search only in picture blocks that hold a cloud edge; the rest uses the Cloud Entry Bisection Steps. It switches on the edge-adaptive cloud picture, which draws the flat faces of a cloud at a lower resolution. Off by default." },
                { "cloudSkipEmpty", "Starts no cloud ray in a 16 by 16 pixel tile whose rays cannot meet a cloud: the sky between clouds, rays that end on the ground before the layer, rays that miss the layer. The test is conservative, so the picture should not change. Off by default." },
                { "cloudSkipClosed", "Does not march the layers above a deck that is solid over a whole tile when seen from below. The march already stops at a solid cloud, so this saves little. Off by default." },
                { "cloudLayerDistanceCut", "Fades out and then skips cloud layers that the air has made invisible at their distance. It needs Cloud Far Haze, because without the haze nothing hides a far layer. Off by default." },
                { "cloudStepGrowth", "Makes the ray steps longer the farther along a layer they are, so the far end of long grazing views is sampled more coarsely. Faster, a little softer far away. Off by default." },
                { "cloudTapLod", "Marches towards the sun less often in the far field, where the difference cannot be seen. Faster, a little less accurate shading far away. Off by default." },
                { "cloudDetailLod", "Drops the fine cloud detail where one screen pixel is wider than the detail, and averages the shape noise there. Removes grain of far clouds and saves noise reads. Off by default." },
                { "cloudLayerCapDistance", "Beyond this distance in km only the nearest two cloud layers of a view are drawn and the others fade out. 0 is off, so every layer is drawn at any distance. Off by default." },
                { "rtOwnClouds", "Makes all own cloud layers available to the ray tracing, so that cloud shadows and reflections can follow the clouds the own pass draws. It bakes a map of the clouds a few texels per frame. Off by default; it acts once the ray tracing reads the map." },
                { "rtOwnCloudsLayerCap", "The most cloud layers the ray tracing sees, counted from the lowest. 0 is every layer. A lower number saves map memory and ray work and leaves the upper layers out of reflections and shadows. Off by default (every layer)." },
                { "cloudSunMarchLong","How far the sunlight is followed through a cloud: off 4 km, on 9 km. The long march darkens the base and the shaded flank of a deep storm cloud, with the same number of steps spread wider." },
                { "cloudSunWhiteBalance", "On, the sun at the zenith above a cloud counts as the sun outside the air, so that only the air beyond the zenith reddens it (an older compensation, paler red). Off, the cloud gets the sun through the whole air above it." },
                { "cloudDiffusionDecay", "How fast the soft glow inside a thick cloud fades with the thickness towards the light. 0 keeps the earlier saturating glow, higher darkens the deep parts of thick clouds." },
                { "cloudFarHaze", "Lets the air between the camera and a far cloud tint it blue and lower its contrast, for clouds seen from high up or from far away. Already on by itself wherever the clouds are drawn behind Scatterer's scattering; this switch forces it in every order." },
                { "cloudFarShapeMean", "Where the march steps are wider than the noise features, replaces the noise by its average, which removes the grain of far clouds." },
                { "cloudFarMinSamples", "The fewest samples taken through a cloud layer seen from far away. 0 leaves the march's own count; a thin layer then flickers from step to step. More costs more." },
                { "cloudFarPolicy", "Takes fewer, longer steps from orbit height, where the detail cannot be seen. Saves frame time and softens clouds seen from high up." },
                { "cloudFarMapFade", "Fades the cloud shell in the map view with the camera's distance. Off, the shell stays at full strength." },
                { "wetSurfaces", "Darkens and glosses the ground, the scenery and the vessels while it rains and after, and lets them dry. It writes into the deferred lighting, which costs a little on every lit surface." },
                { "windowDroplets", "Lets rain drops run down the camera glass in rain, with trails and a view bent by each drop. Needs the deferred rendering path." },
                { "lightning", "Lets storms flash: a bolt, light on the ground and the vessels, a flash in the clouds and thunder after the flash." },
                { "lightningMaxPerSecond", "The most strokes that may begin in any one second, from 1 to 3. 3 is the usual safety limit for flashing images; lower is calmer." },
                { "lightningBrightness", "The brightness of the bolts and the light of a flash from 0 to 1. Lower is calmer for eyes that find the flashes too hard." },
                { "lightningBolts", "Draws the bolt itself for the flashes within 60 km." },
                { "lightningTerrainLight", "Lights the ground and the vessels near a flash with a short light." },
                { "lightningCloudFlash", "Lights the clouds from inside when a flash strikes." },
                { "lightningThunder", "Plays thunder after a flash, from a synthesis, no sound file is used." },
                { "lightningRealisticDelay", "Delays the thunder by the time sound needs for the distance, about 3 seconds per kilometre. Off, the thunder follows the flash at once." },
                { "cloudBaseVariation", "Lets the cloud base lie at slightly different heights over the sky (a slow wave of a few hundred metres, and lower bases under larger cloud fields). Off, every cloud of a type starts at one level." },
                { "cloudBasePhysics", "Sets the base of the low clouds from the air's temperature and humidity: moist air condenses lower, dry air higher (about 125 m per kelvin of dew-point spread)." },
                { "cloudTopFromUpdraft", "Lets the tops of the clouds follow the strength of the updraft of their cell: strong cells build towering cloud, weak ones stay flat. Off, every type has its fixed top." },
                { "baseRagged", "Roughens the underside of the clouds with fine detail, so that it is not a clean cut. Off, the base is smooth." },

                // Wind
                { "windSource", "Whose wind is used. Auto leaves the base wind to a wind mod where one is installed and adds our storm wind on top; Ours replaces it; Foreign and Off leave our wind off while the visuals still draw." },
                { "windInjection", "How the wind reaches a vehicle. Aero changes the air speed the drag sees and needs ModularFlightIntegrator; Force pushes the parts. Auto picks Aero where it can." },
                { "windForces", "Lets the wind push vehicles. Off, the wind is seen and heard but does not move anything." },
                { "windStrength", "How strong the wind is against a vehicle: 0 none, 1 as simulated, 2 twice as strong." },
                { "windOnLanded", "Lets the wind move vehicles that stand on the ground: light ones move when the wind beats their friction. Off, only flying vehicles feel it." },
                { "gustStrength", "How hard the sudden gusts hit. Gusts are short bursts of wind above the steady wind; 0 gives a smooth wind, 1 the simulated gusts, 2 gusts twice as strong." },
                { "windShear", "Lets the wind change with height, so that low and high clouds move in different directions and a jet stream blows aloft. Off, every height has the wind of the ground." },
                { "layerWindSteering", "How the cloud layers of EVE get the wind of their height. Main Period turns each layer's own drift to match it, Maps Only leaves the drift stopped and moves the painted maps, Off keeps the layers drifting with the weather patterns only." },
                { "verticalMotion", "Lets updrafts build cloud towers that grow and sink with the life of a storm cell, and lets EVE's upward cloud speed follow the updraft." },
                { "anvilTilt", "Lets the top of a tall cloud lean downwind with the wind shear and spread into an anvil." },
                { "solarDimming","Lets cloud and precipitation dim the solar panels." },
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
                { "vehicleCoverMethod", "How vehicles show cover: Shell draws a thin second mesh over each part, ScreenSpace paints the up-facing pixels of the vessel and also covers parts with a foreign shader." },
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
            { AtmospheresSettings.GreyReasons.NeedsOwnSource, "Cloud Source is not Ours." },
            { AtmospheresSettings.GreyReasons.NeedsOwnClouds, "Own Volumetric Clouds is off." },
            { AtmospheresSettings.GreyReasons.NeedsCloudNoise, "Cloud Shape Noise is off." },
            { AtmospheresSettings.GreyReasons.NeedsCloudShadows, "Cloud Shadows is off." },
            { AtmospheresSettings.GreyReasons.NeedsAmbientTaps, "Cloud Ambient Taps is off." },
            { AtmospheresSettings.GreyReasons.NeedsEntryRefinement, "Cloud Entry Refinement is off." },
            { AtmospheresSettings.GreyReasons.NeedsLightning, "Lightning is off." },
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
