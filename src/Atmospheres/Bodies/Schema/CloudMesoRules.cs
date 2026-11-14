namespace ReDefinition.Atmospheres
{
    // E40 L3 (type mix and field size): the two data nodes that give a cloud layer's maps the mesoscale sizes of the body (in depths of its troposphere).
    // Both are plain data read from the layer's cfg node and applied by CloudMesoStage in the cloud painter, between the generator's maps and the layer pass.
    //
    //   LAYER { ... MESO { scaleDepths = 5 octaves = 3 amount = 0.7 edge = 0.25 stretch = 1 rainKeep = 1 } ... }
    //   TYPE  { ... CLIMATE { weight = 1  LATITUDE { key0 = 0 1  key1 = 1 0.2 }  SURFACE { ... }  HUMIDITY { ... }  PRECIPITATION { ... } } }

    // The meso-scale cover pattern of a layer (cfg node MESO): the generator's cover map has features of 300 km and more (frontal systems, the ITCZ), the layer noise cells of a few km;
    // this adds the band between, as fields and clear gaps of tens of km. The map m of a texel becomes (1 - amount) m + amount x (a pattern of cloud and gap whose share is m): the
    // cover of a region keeps its mean, and where it is 0 or 1 it stays so. amount 0 (the default) is off.
    internal sealed class CloudMesoSpec
    {
        // The cell of the coarsest octave of the pattern's noise on the body's surface; each further octave halves it. cfg key scaleDepths: the size in depths of the body's troposphere
        // (the mesoscale organisation of convection scales with the depth it fills: cells and fields of a few to ten depths); cfg key scaleKm, where set (above 0), is the size in km instead.
        internal float ScaleDepths { get; set; } = 4.3f;
        internal float ScaleKm { get; set; }
        // The type patches' default size in depths of the troposphere (CloudLayerSpec.TypePatchDepths).
        internal const float DefaultTypePatchDepths = 6.7f;
        internal int Octaves { get; set; } = 2;
        // 0 to 1: how far the map is replaced by the pattern.
        internal float Amount { get; set; }
        // The width of the field's edge in rank units of the noise (0.02 hard, 1 the whole range: a soft gradient).
        internal float Edge { get; set; } = 0.25f;
        // The power of the ramp from gap to core (1: a linear ramp; above: a field has a small core and a long shoulder, so a region of cover 0.6 breaks into fields instead of closing into one).
        internal float Shoulder { get; set; } = 1f;
        // false: the cloud is where the noise is high (blobs and gaps; a region of cover above one half is one connected sheet with holes). true: the gaps are lanes along the noise's median, a
        // connected network, so that the cloud breaks into separate cells whatever the cover (as the closed cells of a stratocumulus field, and TVC's cell tiles).
        internal bool Cells { get; set; }
        // How much longer the features are along a parallel than across it (1: round cells; above: lines along the wind, as cirrus fibres).
        internal float Stretch { get; set; } = 1f;
        // The share of the generator's cover that stays where precipitation falls (1: the pattern never opens a gap in rain; 0: it may).
        internal float RainKeep { get; set; } = 1f;

        internal bool On { get { return Amount > 0f; } }
    }

    // The climate of one cloud type (cfg node CLIMATE of a TYPE): how likely the type is at a place, as the product of a base weight and curves of the place's latitude (x: the body's circulation coordinate, 0 at the equator, 1/3 at the edge of the first circulation cell, 2/3 at the second, 1 at the pole; CloudMesoStage.LatitudeCoordinate), surface (0 sea, 0.5 lowland, 1 high ground), relative humidity and precipitation level. A curve that is absent is 1 everywhere. A layer where any type has a CLIMATE
    // node picks its types from these weights and the patch noise (CloudMesoStage.TypeValueAt) instead of from the precipitation level alone.
    internal sealed class CloudClimateSpec
    {
        internal float Weight { get; set; } = 1f;
        internal Curve Latitude { get; set; }
        internal Curve Surface { get; set; }
        internal Curve Humidity { get; set; }
        internal Curve Precipitation { get; set; }

        internal static float Eval(Curve c, float x)
        {
            return c == null ? 1f : c.Evaluate(x);
        }
    }
}
