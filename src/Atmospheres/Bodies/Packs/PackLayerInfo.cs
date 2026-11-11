using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // One volumetric EVE cloud layer as a config file states it, as plain data: the input of the role discovery. It is made from an
    // EVE_CLOUDS OBJECT node (PackConfigReader, for the fixtures and the shipped files) or from a live layer (the scan of the game).
    internal sealed class PackLayerInfo
    {
        // The layer's name and the body it belongs to, as EVE reads them.
        internal string Name = "";
        internal string Body = "";
        // The radius of the layer above the body (the OBJECT's altitude), in metres: the order of the layers.
        internal double AltitudeM;
        // The cloud type names in the order of the layer's list.
        internal List<string> TypeNames = new List<string>();
        // The layer has a coverage map and a cloud type map: the two a painter writes to. A layer without both is not painted.
        internal bool HasCoverageMap, HasTypeMap;
        // A layer for particles and sound only (raymarchingSettings.fxOnlyLayer): it draws no cloud.
        internal bool FxOnly;
        // The name of its particle field config, empty where it has none.
        internal string ParticleField = "";
        // Where the layer came from (a file or a pack), for the log.
        internal string Origin = "";
    }

    // What a layer is for, by the cloud type names it holds and the body's role lists.
    [System.Flags]
    internal enum LayerRoles
    {
        None = 0,
        // A type of the snow list (snowTypeNames).
        Snow = 1,
        // A type of the stratus list (stratusTypeNames).
        Stratus = 2,
        // A type with "storm", "thunder" or "cumulonimbus" in its name.
        Storm = 4,
        // A type of the dust list (dustTypeNames).
        Dust = 8
    }

    // Why a layer of the body was not taken.
    internal enum SkipReason
    {
        None,
        // The layer belongs to another body.
        OtherBody,
        // It has no coverage map or no type map: nothing to paint.
        NoMaps,
        // It draws no cloud (fx only layer).
        FxOnly,
        // None of its cloud types plays a role of ours.
        NoRole,
        // The cap on the layers we drive was reached.
        OverCap,
        // The body's node asks for no EVE layers (discover = Off).
        DiscoveryOff,
        // The body's node names its layers (discover = Explicit or weatherLayers) and this one is not among them.
        NotNamed
    }

    // The decision about one layer.
    internal sealed class LayerDecision
    {
        internal LayerDecision(PackLayerInfo layer, LayerRoles roles, SkipReason skip)
        {
            Layer = layer;
            Roles = roles;
            Skip = skip;
        }

        internal PackLayerInfo Layer { get; }
        internal LayerRoles Roles { get; }
        internal SkipReason Skip { get; }
        internal bool Taken
        {
            get { return Skip == SkipReason.None; }
        }
    }
}
