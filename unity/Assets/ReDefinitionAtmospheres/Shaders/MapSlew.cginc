// The slew limit of the layer maps (LayerMapStore.GpuSlew.cs, MapSlewField.cs is the CPU twin). A map array slice holds two values per texel: r the value the map showed
// when its newest goal arrived, g the goal. reach (one component per layer, a share of full scale) is the store's rate times the seconds since the goal arrived, so the
// shown value is r moved toward g by at most that much: no texel changes faster than the rate, however the frames fall. A map that is not slewing holds the value in both
// channels, whatever the reach.
#ifndef REDEFINITION_ATMOSPHERES_MAP_SLEW
#define REDEFINITION_ATMOSPHERES_MAP_SLEW

float SlewedMap(float2 fromTo, float4 reach, float slice)
{
    int s = (int)(slice + 0.5);
    float m = s <= 0 ? reach.x : (s == 1 ? reach.y : (s == 2 ? reach.z : reach.w));
    return fromTo.x + clamp(fromTo.y - fromTo.x, -m, m);
}

#endif
