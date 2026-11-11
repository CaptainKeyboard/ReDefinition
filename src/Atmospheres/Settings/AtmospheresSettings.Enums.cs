namespace ReDefinition.Atmospheres
{
    // Where the clouds of a body come from (key cloudSource; the table of what each value draws is in CONTRACT.md).
    internal enum CloudSourceKind
    {
        // Our own volume pass draws the decks; EVE's weather layers on the body are off.
        Ours,
        // EVE draws the decks from the maps our field paints.
        Eve,
        // As Eve, plus EVE's native weather on the layers we do not paint.
        Both,
        // No clouds from us.
        None
    }

    // Who composes the veil and the flakes (key weatherCompose).
    internal enum WeatherCompose
    {
        // Host where the main mod's after-upscaling hook answers, else Scene.
        Auto,
        // In the scene, before the upscaler.
        Scene,
        // After the upscaler, through the host.
        Host
    }

    // Whose wind is used (key windSource).
    internal enum WindSourceMode
    {
        // A foreign provider owns the wind where one is installed, else ours.
        Auto,
        Ours,
        Foreign,
        Off
    }

    // How the wind reaches a vehicle (key windInjection).
    internal enum WindInjection
    {
        Auto,
        // Through the aerodynamic velocity.
        Aero,
        // As a force on the parts.
        Force
    }

    // Where the volume pass is composed (key volumeCompose).
    internal enum VolumeCompose
    {
        Scene,
        PostUpscale
    }

    // The aurora (key aurora).
    internal enum AuroraMode
    {
        Off,
        Auto,
        // A mesh shell, depth-tested.
        Shell2D,
        // An EVE volumetric layer.
        Volumetric
    }

    // Who muffles the sound in a helmet or a cabin (key audioMuffling).
    internal enum AudioMuffling
    {
        Auto,
        Own,
        // Rocket Sound Enhancement's mixer.
        Rse
    }

    // Which wind-storm loop is heard where EVE has one (key eveSound).
    internal enum EveSoundPolicy
    {
        Auto,
        Eve,
        Ours,
        Both
    }

    // What the weather bed is made of (key audioBedSource).
    internal enum AudioBedSource
    {
        Synth,
        Recorded,
        Mix
    }

    // When a vessel carries cover (key vehicleCover).
    internal enum VehicleCoverMode
    {
        Off,
        Landed,
        Always
    }

    // How a vehicle shows cover (key vehicleCoverMethod): Shell puts a second mesh with the up-facing triangles over each part with a
    // stock-like shader (parts with a foreign opaque shader stay bare); ScreenSpace paints the up-facing pixels in the vessel's bounds
    // after the opaque pass and needs no mesh, so it also covers parts with a foreign shader.
    internal enum VehicleCoverMethod
    {
        Shell,
        ScreenSpace
    }

    // Sea ice (key seaIce).
    internal enum SeaIceMode
    {
        Off,
        // Only in the orbit and map view.
        Orbit,
        OrbitAndGround,
        // Also a collider sheet and buoyancy (experimental).
        Physical
    }

    // Which planets the orbit view paints (key orbitBodies).
    internal enum OrbitBodiesMode
    {
        Focused,
        Visible,
        All
    }

    // The size of the simulation grid against the body's own (key simGrid).
    internal enum SimGridChoice
    {
        // The body's SIM rules.
        Auto,
        Coarse,
        Normal,
        Fine
    }

    // A named level of precipitation the player picks (keys alertMinLevel, launchMaxLevel): PrecipBand without None.
    internal enum LevelChoice
    {
        Flurries,
        Light,
        Moderate,
        Heavy,
        Blizzard
    }

    // A precipitation type that is forced in diagnostics (key forceType); Auto leaves the field's own.
    internal enum ForceType
    {
        Auto,
        Snow,
        Rain,
        Sleet,
        FreezingRain,
        Dust,
        Sand,
        Ash,
        Methane
    }

    // What the debug overlay shows (key atmoDebug).
    internal enum AtmoDebugMode
    {
        Off,
        // The contribution mask: which pass drew each pixel.
        Contrib,
        // The level at the camera as text.
        Level,
        // The maps of the field.
        Maps
    }
}
