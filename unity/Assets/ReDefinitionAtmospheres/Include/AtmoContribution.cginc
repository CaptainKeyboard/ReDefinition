// The contribution mask of ReDefinition Atmospheres (atmoDebug = contrib): which pass drew each pixel, and how much.
//
// A pass that draws into the scene (the volume pass, the precipitation, the lamp scatter, the camera frost, the ground cover, the
// sky layers) includes this file and, in its fragment function, returns AtmoContribOutput(normalColour, contribution, featureId):
//
//   contribution  0..1, the share of the pixel the pass changes: the alpha it blends with, or the weight of its additive term
//   featureId     a small integer naming the pass (the ids are below)
//
// With the debug mode off (the global _AtmoDebug is 0) the function returns the normal colour and costs one compare. With the mode
// set to contrib (1) it returns the feature's colour times the contribution, with the contribution as alpha, so that a capture of a
// frame cleared to black and blended as the pass blends holds black where the pass drew nothing. tools/atmo_ab.py contrib reads such
// a capture: the pixels contributed, their share of the frame and the overlap with an expected region.
//
// The runtime sets _AtmoDebug from the setting atmoDebug (AtmoDebugMode: Off 0, Contrib 1, Level 2, Maps 3) with
// Shader.SetGlobalFloat; without it the global stays zero and nothing changes. Unity 2019.4 (KSP 1.12), built-in pipeline.

#ifndef REDEFINITION_ATMO_CONTRIBUTION_INCLUDED
#define REDEFINITION_ATMO_CONTRIBUTION_INCLUDED

// 0 off, 1 contribution mask, 2 level, 3 maps.
float _AtmoDebug;

// The ids of the passes. A pass of a package that is not listed takes the next free id in its own include and adds it here.
#define ATMO_FEATURE_VOLUME        1
#define ATMO_FEATURE_PRECIP        2
#define ATMO_FEATURE_LAMP_SCATTER  3
#define ATMO_FEATURE_FROST         4
#define ATMO_FEATURE_COVER         5
#define ATMO_FEATURE_SKY           6
#define ATMO_FEATURE_AURORA        7
#define ATMO_FEATURE_FOG           8
#define ATMO_FEATURE_TEST          15

// Whether the contribution mask is on.
inline bool AtmoContribOn()
{
    return _AtmoDebug > 0.5 && _AtmoDebug < 1.5;
}

// The colour of a feature id: 1 red, 2 green, 3 blue, 4 yellow, 5 magenta, 6 cyan, 7 orange, 8 white-grey; 15 is white (the test
// pass of tests/WeatherPlayer). Every colour has a channel at 1, so that a contribution of 1 reads as 1 in the brightest channel.
inline float3 AtmoFeatureColour(int featureId)
{
    if (featureId == 1) return float3(1.0, 0.0, 0.0);
    if (featureId == 2) return float3(0.0, 1.0, 0.0);
    if (featureId == 3) return float3(0.0, 0.0, 1.0);
    if (featureId == 4) return float3(1.0, 1.0, 0.0);
    if (featureId == 5) return float3(1.0, 0.0, 1.0);
    if (featureId == 6) return float3(0.0, 1.0, 1.0);
    if (featureId == 7) return float3(1.0, 0.5, 0.0);
    if (featureId == 8) return float3(0.8, 0.8, 0.8);
    return float3(1.0, 1.0, 1.0);
}

// The colour a pass returns: its normal colour, or the mask when the mask is on.
inline float4 AtmoContribOutput(float4 normalColour, float contribution, int featureId)
{
    if (AtmoContribOn())
    {
        float c = saturate(contribution);
        return float4(AtmoFeatureColour(featureId) * c, c);
    }
    return normalColour;
}

#endif
