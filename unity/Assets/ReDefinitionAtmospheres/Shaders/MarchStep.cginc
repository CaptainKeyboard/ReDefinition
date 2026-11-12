// The edge-adaptive resolution of the layer pass, march side (package T12, the plan's T05b).
//
// ReDefinitionAtmospheresLayers.shader includes this file once, at the end of its shared CGINCLUDE (after Pass0 is defined), and nothing
// else of that shader changes. The file wraps Pass0: a draw that is the EDGE pass (_EdgeP.x = 1, the 2 x 2 pattern of rays) marches only the
// pixels whose 4 x 4 block the edge mask flags and returns zero for the rest, so the rays, the sun marches and the entry refinement are paid in
// the narrow band around the cloud contours; the coarse pass (_EdgeP.x = 0, the default of a material that never set it) is Pass0 unchanged.
// The step band is the draw's own _RefineP (the caller sets the entry refinement on the edge draw and, with edgeStepBand, off on the coarse
// one), so the fine steps near the surface are found only inside the band, and the interior steps stay the grid and the transmittance growth
// of package T05.
//
// The mask is built by ReDefinitionAtmospheresEdge.shader from the coarse samples' alpha gradient and the last picture (see there).
//
// Technique: a ray density that follows the screen-space gradient of the alpha, with a dilated mask and a history control by variance
// clipping (Salvi, GDC 2016; Karis, SIGGRAPH 2014), and TVC's adaptive step factor as a behavioural reference.

#ifndef REDEFINITION_MARCH_STEP_INCLUDED
#define REDEFINITION_MARCH_STEP_INCLUDED

sampler2D _EdgeMask;   // the edge mask: one texel per 4 x 4 block of the full target, 1 where the block holds a contour
float4 _EdgeP;         // x: 1 on the edge draw (march only the flagged blocks), 0 otherwise; y: the mask block's side in pixels; z, w: 0

// Whether the full-target pixel (as the uv Pass0 uses) lies in a flagged block.
bool EdgeBlockFlagged(float2 fullUv)
{
    return tex2Dlod(_EdgeMask, float4(fullUv, 0, 0)).r > 0.5;
}

float4 EdgePass0(v2f i)
{
    if (_EdgeP.x > 0.5 && !EdgeBlockFlagged(FullUv(i.uv))) return float4(0, 0, 0, 0);
    // The contour gate of package T14 takes quad derivatives, which the edge draw's per-pixel early return leaves undefined: that draw refines as asked.
    gGateOff = _EdgeP.x > 0.5 ? 1.0 : 0.0;
    return Pass0(i);
}

#define Pass0 EdgePass0

#endif
