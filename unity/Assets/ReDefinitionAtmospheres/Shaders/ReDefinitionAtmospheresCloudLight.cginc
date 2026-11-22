// The light of the own cloud system (package T03), included twice by ReDefinitionAtmospheresLayers.shader:
//   CLOUDLIGHT_DECLARE  the uniforms and the analytic cell (a parity test hook, zero cost while _CellA.w = 0)
//   CLOUDLIGHT_FUNCS    the phase, the light march, the octave multiple scattering, the ambient, the lights and the light cache
// The functions need LayerDensity and LayerSpans of the including shader, so the second section comes after them.
//
// Model (a source function J at a march sample, radiance towards the viewer per unit extinction; the march integrates it with the
// energy-conserving step of Hillaire 2016, section 5.6.3):
//   J = albedo * ( powder * sum over lights Lc * sum_{n<N} b^n * Phase(mu, c^n) * exp(-tau_light * a^n)  +  ambient )
//   Phase  dual-lobe Henyey-Greenstein, forward g1, back g2, weight w1; octave n scales both g by c^n (Wrenninge et al. 2013 style octaves,
//          applied as in Hillaire 2016, section 5.8; a, b, c <= 1; the fitted brightness above 1 is documented in CloudLightMath.OctaveEnergy)
//   tau    the optical depth towards the light: a short march of a few taps with growing steps, optionally spread in a cone
//          (Schneider 2015: cone-sampled light march); octave n sees the medium thinned by a^n, the stand-in for light that scatters
//          more than once and reaches the sample around the dense core
//   powder 1 - exp(-2 sigma L): thin, low-density parts scatter little; strength and length are parameters (Schneider 2015)
//   ambient the body's sky colour from the layer's bottom to its top with a tint (grey .. sky hue), thinned by the optical depth
//          straight up (Frostbite 2016, section 5.5.1 gradient; Nubis Cubed ambient occlusion idea), never below a floor share of the sun
// Lights: the sun and up to two more (moons, planet shine), each marched with its own taps; lightning bolts as point lights.
//
// The light cache (decision prototype B): a camera-following atlas of tau towards the sun and straight up, read instead of the per-sample
// march inside its box.
#ifdef CLOUDLIGHT_DECLARE
float4 _LightP;   // octaves (1..4), a (light thinning per octave), b (weight per octave), c (eccentricity per octave)
float4 _LightQ;   // powder strength, powder length (m), ambient floor (share of the sun share), ambient occlusion strength (0: gradient only)
float4 _LightR;   // cone spread (tan of the half angle, 0: straight), tap step ratio, sky tint multiplier (0 grey .. 1 sky hue), multiple scattering brightness
float4 _LightS;   // moon taps, up taps, ambient gain, light cache (1 on)
float4 _LightT;   // bolt count, sun tap count override (0: the march's own), light cache fill taps, ambient occlusion mode (0 analytic from the local density, 1 taps straight up at every lit sample, 2 taps reused with the sun march, 3 reused and moved by the ray's progress)
float4 _SunAir;   // the sun's colour through the air (FX-T03): extinction rgb along the zenith path at sea level, scale height in m (w <= 0: the frame's sun colour as is); the aerosol component is _LightV.zw
float4 _LightU;   // diffusion share (light that has diffused into the deep core, isotropic, as a share of the sun's isotropic light), its optical depth scale, isotropic share of the octaves above the first
float4 _LightV;   // diffusion decay with the optical depth towards the light (0: the saturating term), the analytic occlusion's column ends at the cloud type's top (1) or at the layer's outer altitude (0), the sun air's second component (grey extinction along the zenith path at sea level, scale height in m)
float4 _LightM[4];  // per layer (E40 L6 light): x, y the sky's light inside the clouds (amplitude in shares of the sky's isotropic source at the surface, decay with the optical depth below the top; two-stream: 1 / (1 + decay tau)), z the side and back plateau of a Cheap or Fog layer's phase (ice) in units of the isotropic phase, added to the water drop's lobes, w the cell width in m of the cell diffusion (E40 LM2; 0: the diffusion term of _LightU / _LightV)
float4 _LightW;   // the cell diffusion (E40 LM2, CloudLightMath.CellDiffusion): gain, scaled-depth factor, extrapolation length, side-loss strength
float4 _LightX;   // the cell diffusion: the octaves' isotropic share at the reference incidence, the reference incidence, the power of the incidence on that share, the incidence's softening optical depth
float4 _LightL[4];  // per layer: multiple scattering brightness, powder scale, ambient scale, isotropic share of the Cheap and Fog kinds' single scattering (ice crystals, E40 look round 2)
float4 _MoonDir[2], _MoonCol[2];   // towards the light; colour times intensity (rgb, 0: off)
float4 _BoltPos[4], _BoltCol[4];   // planet frame position and range (w); colour times intensity
float4 _CellA;    // parity test cell: centre (planet frame) and horizontal radius (w > 0: the cell replaces the layers' density)
float4 _CellB;    // vertical radius, edge softness, flat base (1), extinction
// The light cache (LightCache.cs): a texture array (one slice per voxel layer) of the optical depth towards the sun over a box in a local frame of the planet; x, y, z of the box are its three
// edge vectors (planet frame, length the edge in m; w 1 / the edge squared), _CacheA its corner (w 1 while the march reads it).
float4 _CacheA, _CacheX, _CacheY, _CacheZ;
float4 _CacheH;   // x the near taps of the sun march (the march's own layout over the first taps), y the growth of each of the fill's taps (_LightT.z) over the one before, z the slices
float4 _CacheSlice;   // the fill (a global, never on the material): x the slice being filled, y the slices, z, w the voxels across
UNITY_DECLARE_TEX2DARRAY(_LightCache);
#ifdef CLOUD_LIGHTVOL
// The light volume (LightVolume.cs, prototype LV1 behind cloud.lightVolume): the optical depth towards the sun over two cascades of voxels whose third axis is the altitude and whose
// other two are the gnomonic tangent-plane coordinates (m at the ground) of a frame anchored where the volume was last laid. Each cascade is a window of _LvC.z x _LvC.z voxels addressed
// with wrap-around on a grid fixed in the anchored frame; slices c * _LvZ.z .. of the array hold cascade c.
// The gnomonic frame's cut (cosine of the largest angle from the anchor a point may have; LightVolumeMath.MinCos twins it), the slices of a cascade at most (the length of _LvO;
// LightVolumeMath.MaxSlices), the transmittance stored at most so deep (a half float's normal range; LightVolumeMath.MinStored).
#define LV_MIN_COS 0.5
#define LV_MAX_SLICES 64
#define LV_MIN_STORED 6.2e-5
float4 _LvA;     // x the hand-over distance in m (< 0: infinity, the full sun march everywhere), y the blend band in m, z the far path's near taps (0 .. 3), w the pixel's angular size (rad)
float4 _LvU, _LvE, _LvN;   // the anchored frame: up, east, north (planet frame, unit)
float4 _LvZ;      // x, y the altitude range in m, z the slices of a cascade, w the fade band at a window's edge in voxels
float4 _LvC;      // x, y the voxel size of cascade 0 and 1 in m, z the voxels across a window, w the cascades in use (1, 2)
float4 _LvF;      // x the fill's taps over the whole sun march (0: the march's own taps beyond the far near taps), y the growth of each fill tap over the one before
float4 _LvO[64];  // LV_MAX_SLICES; per slice: the window origin (voxel index) of cascade 0 (xy) and 1 (zw) when the slice was last filled (LightVolumeMath.NotFilled: not since the lay)
float4 _LvSlice;  // the fill (a global, never on the material): x the slice, y the cascade, z, w the window origin
float4 _LvP;      // LV2 fill: x the mode (0 direct march, 1 propagation), y, z the propagation's least and most samples
                  // per segment, w the longest horizontal run of a segment in voxels (beyond it the direct march)
float4 _LvD;      // unused (kept so that the uniform layout stays)
float4 _LvR;      // LV2 read: x k of the stored value (0: the optical depth; above 0: exp(-tau / k), interpolated as such and read back as -k log); yzw unused
float4 _LvS;      // LV3 lateral spread of the direct light: x the source (0 off, 1 the blurred volume, 2 the far cascade, 3 the volume as filled), y the blur's radius in m,
                  // z the optical depth over which the spread's weight grows (1 - exp(-tau / z)), w the largest weight
float4 _LvBlur;   // the blur pass (a global, never on the material): x the source (0 the volume's slice y, 1 the 2D target _LvBlurSrc), y the array slice, z, w the direction in texels
UNITY_DECLARE_TEX2DARRAY(_LightVolume);
UNITY_DECLARE_TEX2DARRAY_NOSAMPLER(_LightVolumeSpread);   // read with _LightVolume's sampler (the march has no sampler register left)
#endif

// The cell's bounding sphere along a ray: false where the ray misses it.
bool CellHit(float3 o, float3 d, out float t0, out float t1)
{
    float3 oc = o - _CellA.xyz;
    float b = dot(oc, d);
    float c = dot(oc, oc) - _CellA.w * _CellA.w * 1.1025;
    float disc = b * b - c;
    t0 = 0; t1 = 0;
    if (disc <= 0) return false;
    float s = sqrt(disc);
    t0 = -b - s; t1 = -b + s;
    return true;
}

// The spans of a ray cut to the cell's bounding sphere.
void CellClip(float3 o, float3 d, inout float4 sp)
{
    if (DEV(_CellA.w, 0.0) <= 0) return;
    float c0, c1;
    if (!CellHit(o, d, c0, c1)) { sp = 0; return; }
    sp.x = max(sp.x, c0); sp.y = min(sp.y, c1);
    sp.z = max(sp.z, c0); sp.w = min(sp.w, c1);
    if (sp.y <= sp.x) sp.xy = 0;
    if (sp.w <= sp.z) sp.zw = 0;
}

// The analytic cell of the parity harness (tools/weather_metrics/parity_ref.py scene_single_cloud): an ellipsoid with a smoothstep edge, a
// flat bottom, the extinction _CellB.w.
float CellDensity(float3 pc)
{
    float3 q = pc - _CellA.xyz;
    float rv = _CellB.x;
    float r = sqrt((q.x * q.x + q.y * q.y) / (_CellA.w * _CellA.w) + q.z * q.z / (rv * rv));
    float x = saturate((1.0 - r) / max(_CellB.y, 1e-4));
    float v = x * x * (3.0 - 2.0 * x);
    if (_CellB.z > 0.5 && q.z <= -0.5 * rv) v = 0;
    return _CellB.w * v;
}
#endif

#ifdef CLOUDLIGHT_FUNCS
static const float LIGHT_INV4PI = 0.07957747;

float Hg(float mu, float g)
{
    return (1.0 - g * g) / (4.0 * PI * pow(max(1.0 + g * g - 2.0 * g * mu, 1e-6), 1.5));
}

// The dual-lobe phase function (_PhaseP: g forward, g back, forward weight); ecc scales both lobes (the octaves' c^n).
float Phase(float mu, float ecc)
{
    return _PhaseP.z * Hg(mu, _PhaseP.x * ecc) + (1.0 - _PhaseP.z) * Hg(mu, _PhaseP.y * ecc);
}

float Phase(float mu)
{
    return Phase(mu, 1.0);
}

float Luma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// The share of the sun's colour that reaches a point after the air above it (FX-T03): Beer-Lambert with an extinction per channel, the
// airmass of Kasten and Young (1989, Applied Optics 28: m = 1 / (sin h + 0.50572 (h + 6.07995 deg)^-1.6364)) and the air thinning with the
// point's altitude. A low sun comes out red (blue is removed first); the sun below the horizon gives nothing.
float3 SunAir(float3 pc, float3 sunDir)
{
    if (_SunAir.w <= 0) return 1.0;
    float3 up = pc * rsqrt(max(dot(pc, pc), 1.0));
    float s = clamp(dot(up, sunDir), -1.0, 1.0);
    float elev = asin(s) * 57.29577951;
    float m = 1.0 / (max(s, 0.0) + 0.50572 * pow(max(elev + 6.07995, 0.05), -1.6364));
    float h = max(length(pc) - _PlanetR, 0.0);
    float3 tau = _SunAir.xyz * exp(-h / _SunAir.w) + _LightV.z * exp(-h / _LightV.w);   // Rayleigh and aerosol, each with its own scale height (E40)
    // White balance (T03v2, _LightU.w): the frame's sun colour is the sun at the zenith above the point; only the air path beyond it reddens.
    if (_LightU.w > 0.5) m = max(m - 1.0, 0.0);
    return exp(-tau * m) * smoothstep(-0.02, 0.0, s);
}

// The optical depth from a point along a direction through all layers: the own layer with `steps` taps of growing size (each `ratio` times the one before) over at most `range` m (below 0: up to -range m from pc),
// the other layers with half of them (own < 0: all layers with `steps`). The taps may be spread over a cone of tangent `spread`.
// CLOUDR2 P3: the first `eroded` taps of each span read the density with its erosion detail (the lobes that shade each other are the detail's, not the shape's); the others the shape only.
float LightTau(float3 pc, float3 dir, int own, int steps, float t, float spread, float rot, int eroded, float range, float ratioIn)
{
    float tau = 0;
    float3 ax = abs(dir.z) < 0.9 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 u = normalize(cross(dir, ax));
    float3 v = cross(dir, u);
    float ratio = max(ratioIn, 1.0);
    [loop] for (int lj = 0; lj < MAXL; lj++)
    {
        if (lj >= (int)_LayerCount) break;
        // Cost attribution only (layers.costDebug bit 8): the march towards a light crosses the sample's own layer alone.
        if (own >= 0 && lj != own && fmod(floor(DEV(_CostP.y, 0.0) * 0.125), 2.0) > 0.5) continue;
        float4 sp;
        LayerSpans(lj, pc, dir, 1e30, sp);
        CellClip(pc, dir, sp);
        int n = (lj == own || own < 0) ? steps : max(1, steps / 2);
        [loop] for (int k = 0; k < 2; k++)
        {
            float a = k == 0 ? sp.x : sp.z, b = k == 0 ? sp.y : sp.w;
            // range > 0: at most that far from the span's start; range < 0: no farther than -range from pc (the light cache's near taps, whose far part the cache holds).
            b = range < 0 ? min(b, -range) : min(b, a + range);
            if (b <= a) continue;
            float norm = ratio == 1.0 ? (float)n : (pow(ratio, (float)n) - 1.0) / (ratio - 1.0);
            float prev = a;
            float gain = 1.0;
            float acc = 0;
            [loop] for (int i = 0; i < 64; i++)
            {
                if (i >= n) break;
                float len = (b - a) * gain / norm;
                float edge = prev + len;
                float mid = 0.5 * (prev + edge);
                float3 q = pc + dir * mid;
                if (spread > 0)
                {
                    float ang = i * 2.3999632 + rot;
                    float rad = sqrt((i + 0.5) / n) * spread * mid;
                    q += (u * cos(ang) + v * sin(ang)) * rad;
                }
                float qs;
                acc += LayerDensityEx(lj, q, length(q) - _PlanetR, t, i < eroded ? 0 : 1, qs) * (edge - prev);   // the light path reads the shape only, not the erosion detail (T02), except the first taps (P3)
                prev = edge;
                gain *= ratio;
            }
            tau += acc;
        }
    }
    return tau;
}

// ---- the light cache ----
// The optical depth towards the sun at a point from the cache (bilinear in each slice, linear between the two slices); inside is false outside the box.
float CacheFetch(float3 pc, out bool inside)
{
    float3 f = pc - _CacheA.xyz;
    float3 uvw = float3(dot(f, _CacheX.xyz) * _CacheX.w, dot(f, _CacheY.xyz) * _CacheY.w, dot(f, _CacheZ.xyz) * _CacheZ.w);
    inside = all(uvw >= 0.0) && all(uvw <= 1.0);
    float z = clamp(uvw.z * _CacheH.z - 0.5, 0.0, _CacheH.z - 1.0);
    float z0 = floor(z);
    float a = UNITY_SAMPLE_TEX2DARRAY_LOD(_LightCache, float3(uvw.xy, z0), 0).r;
    float b = UNITY_SAMPLE_TEX2DARRAY_LOD(_LightCache, float3(uvw.xy, min(z0 + 1.0, _CacheH.z - 1.0)), 0).r;
    return lerp(a, b, z - z0);
}

// The share of the sun march's length that its first `near` taps cover (the layout of LightTau: each tap `ratio` times the one before).
float NearShare(int near, int steps, float ratio)
{
    return ratio <= 1.0 ? (float)near / steps : (pow(ratio, (float)near) - 1.0) / (pow(ratio, (float)steps) - 1.0);
}

#ifdef CLOUD_LIGHTVOL
// ---- the light volume (LV1) ----
// The optical depth towards the sun at a point from the light volume and the weight w (0 .. 1) the volume has there: the finest cascade whose window holds the point, faded
// over _LvZ.w voxels at the window's edge into the next cascade; what is left of w towards 1 is the caller's (the full march). A window counts per slice as it was when that slice
// was last filled, so a voxel that entered the window after its slice's last refresh is never read.
// The stored value of an optical depth and back (_LvR.x k > 0: the transmittance exp(-tau / k), which the trilinear read then interpolates instead of tau).
float LightVolumeEncode(float tau)
{
    return _LvR.x > 0.0 ? max(exp(-tau / _LvR.x), LV_MIN_STORED) : tau;
}

float LightVolumeDecode(float v)
{
    return _LvR.x > 0.0 ? -_LvR.x * log(max(v, LV_MIN_STORED)) : v;
}

// The far path's near length at p: what the first `keep` of the full march's `steps` taps cover (the march and the fill take the same).
float LightVolumeNear(int keep, int steps, float3 p, float3 sunDir)
{
    if (keep <= 0) return 0.0;
    return _Sun.w * NearShare(keep, steps, max(_LightR.y, 1.0));
}

// LV2, the propagation fill: the optical depth stored in slice z (the slice's centre altitude, no vertical blend) at q, from cascade `pref` where the window that slice was filled for
// holds q's bilinear footprint, else from the other cascade; ok false where neither does.
float LightVolumeSlice(float3 q, int z, int pref, out bool ok)
{
    ok = false;
    float3 n = normalize(q);
    float c = dot(n, _LvU.xyz);
    if (c <= LV_MIN_COS) return 0.0;
    float2 xy = _PlanetR * float2(dot(n, _LvE.xyz), dot(n, _LvN.xyz)) / c;
    float4 o = _LvO[z];
    float gridN = _LvC.z;
    [loop] for (int k = 0; k < 2; k++)
    {
        int ci = k == 0 ? pref : 1 - pref;
        if (ci >= (int)_LvC.w) continue;
        float vox = ci == 0 ? _LvC.x : _LvC.y;
        float2 g = xy / vox - 0.5;
        float2 oc = ci == 0 ? o.xy : o.zw;
        float2 d = min(g - oc, oc + gridN - 1.0 - g);
        if (min(d.x, d.y) < 0.0) continue;
        ok = true;
        return LightVolumeDecode(UNITY_SAMPLE_TEX2DARRAY_LOD(_LightVolume, float3(xy / (vox * gridN), ci * _LvZ.z + z), 0).r);
    }
    return 0.0;
}

// The fetch from the volume (spread false) or its blurred copy (spread true), from cascade `first` on.
float LightVolumeFetchFrom(float3 q, bool spread, int first, out float w)
{
    w = 0.0;
    float r = length(q);
    float3 n = q / max(r, 1.0);
    float c = dot(n, _LvU.xyz);
    if (c <= LV_MIN_COS) return 0.0;
    float2 xy = _PlanetR * float2(dot(n, _LvE.xyz), dot(n, _LvN.xyz)) / c;
    float slices = _LvZ.z;
    float zf = clamp((r - _PlanetR - _LvZ.x) / max(_LvZ.y - _LvZ.x, 1.0) * slices - 0.5, 0.0, slices - 1.0);
    float z0 = floor(zf), z1 = min(z0 + 1.0, slices - 1.0), fz = zf - z0;
    float4 oa = _LvO[(int)z0], ob = _LvO[(int)z1];
    float gridN = _LvC.z, sum = 0.0;
    [loop] for (int ci = 0; ci < 2; ci++)
    {
        if (ci >= (int)_LvC.w || w >= 1.0) break;
        if (ci < first) continue;
        float vox = ci == 0 ? _LvC.x : _LvC.y;
        float2 g = xy / vox - 0.5;
        float2 o0 = ci == 0 ? oa.xy : oa.zw, o1 = ci == 0 ? ob.xy : ob.zw;
        float2 d0 = min(g - o0, o0 + gridN - 1.0 - g), d1 = min(g - o1, o1 + gridN - 1.0 - g);
        float d = min(min(d0.x, d0.y), min(d1.x, d1.y));
        float wc = saturate(d / max(_LvZ.w, 1e-3));
        if (wc <= 0.0) continue;
        float2 uv = xy / (vox * gridN);
        float base = ci * slices;
        float a, b;
        [branch] if (spread)
        {
            a = UNITY_SAMPLE_TEX2DARRAY_SAMPLER_LOD(_LightVolumeSpread, _LightVolume, float3(uv, base + z0), 0).r;
            b = UNITY_SAMPLE_TEX2DARRAY_SAMPLER_LOD(_LightVolumeSpread, _LightVolume, float3(uv, base + z1), 0).r;
        }
        else
        {
            a = UNITY_SAMPLE_TEX2DARRAY_LOD(_LightVolume, float3(uv, base + z0), 0).r;
            b = UNITY_SAMPLE_TEX2DARRAY_LOD(_LightVolume, float3(uv, base + z1), 0).r;
        }
        float take = (1.0 - w) * wc;
        sum += take * LightVolumeDecode(lerp(a, b, fz));
        w += take;
    }
    return w > 0.0 ? sum / w : 0.0;
}

float LightVolumeFetch(float3 q, out float w)
{
    return LightVolumeFetchFrom(q, false, 0, w);
}

// LV3, the lateral spread of the direct light: the sun's optical depth the direct term reads, blended in transmittance between the exact one (the march's, tau) and a spread
// one from the volume at the sample (spreadTau, with the volume's weight wv there; _LvS.x: 1 the blurred copy, 2 the far cascade, 3 the volume as filled), by a weight that grows
// with the depth into the cloud (tau): nothing at the sunlit surface, so its rim and the billows' own shadow stay; up to _LvS.w deep inside and on the faces in another lobe's
// shadow. Where the volume holds nothing: tau. A diffusion stand-in: light that scattered several times reaches the sample from the side. It does not conserve energy (the
// transmittance is averaged over a neighbourhood that includes clear air) and it cannot tell a gap the light passes through from one it does not.
float LightVolumeSpread(float tau, float spreadTau, float wv)
{
    float k = wv * _LvS.w * (1.0 - exp(-tau / max(_LvS.z, 1e-3)));
    if (k <= 0.0) return tau;
    float t = lerp(exp(-tau), exp(-spreadTau), k);
    return -log(max(t, 1e-30));
}

// The split of a lit sample's sun march by its distance from the camera (LV1). share: the weight of the far path (near taps and one volume read) against the full march, 0 nearer
// than the hand-over distance, rising over the band (the caller multiplies it by the volume's weight at the read point); near, nearSteps: the far path's own taps, over the length the first _LvA.z taps of
// the full march cover (the same places), fewer and longer where the shortest of them is shorter than the pixel's footprint (none: the volume is read at the sample); volTau the
// volume's optical depth at the end of the near taps.
void LightVolumePlan(float3 pc, float3 sunDir, float dist, int steps, out float share, out float near, out int nearSteps, out float volTau)
{
    share = 0.0; near = 0.0; nearSteps = 0; volTau = 0.0;
    if (_LvA.x < 0.0) return;
    float far = saturate((dist - _LvA.x) / max(_LvA.y, 1e-3));
    if (far <= 0.0) return;
    float ratio = max(_LightR.y, 1.0);
    int keep = min((int)_LvA.z, steps - 1);
    if (keep > 0)
    {
        near = LightVolumeNear(keep, steps, pc, sunDir);
        float foot = _LvA.w * dist;
        [loop] for (int i = 0; i < 3; i++)
        {
            // the first (shortest) tap of `keep` taps over `near`
            float norm = ratio == 1.0 ? (float)keep : (pow(ratio, (float)keep) - 1.0) / (ratio - 1.0);
            if (keep <= 0 || near / norm >= foot) break;
            keep--;
        }
        nearSteps = keep;
        if (keep <= 0) near = 0.0;
    }
    // The volume's reads happen at LightSource's one fetch site (the caller multiplies the share by the read's weight and sets volTau).
    share = far;
}
#endif

// ---- the octaves ----
// sum_n b^n * Phase(mu, c^n) * exp(-tau * a^n); octave 0 is the single scattering, the others carry the multiple scattering brightness.
// iso (E40 LM2) scales the octaves' isotropic share: with a layer's cell diffusion it follows the light's incidence, the forward share stays.
float OctaveSum(float mu, float tau, float msBrightness, float iso)
{
    int n = (int)clamp(_LightP.x, 1.0, 4.0);
    float sum = 0;
    float an = 1.0, bn = 1.0, cn = 1.0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        // The octaves above the first stand for light that has scattered several times: their directions are more isotropic than the single
        // scattering's, which _LightU.z (the isotropic share) expresses (FX-T03).
        if (i < n) sum += (i == 0 ? 1.0 : msBrightness) * bn * (i == 0 ? Phase(mu, cn) : lerp(Phase(mu, cn), LIGHT_INV4PI * iso, _LightU.z)) * exp(-tau * an);
        an *= _LightP.y; bn *= _LightP.z; cn *= _LightP.w;
    }
    return sum;
}

// One light's contribution at a sample: its colour times the octave sum with its optical depth tau (marched by LightSource's one LightTau call site: CLOUDR2 P4).
// cell (E40 LM2): x the factor on the octaves' isotropic share, y the cell diffusion's source (below 0: none, the diffusion term below).
float3 DirectOfLight(float3 col, float3 ldir, float3 d, int li, float tau, float2 cell)
{
    if (Luma(col) <= 0) return 0;
    // The octaves thin out with depth (exp(-tau a^n)); deep in a thick core the light that entered has diffused and leaves isotropically, which
    // the octaves cannot reach at tau of tens: a saturating isotropic term takes over there, nothing at the sunlit edge (tau -> 0).
    // T03v2: the term falls off again by 1 / (1 + decay tau) (a conservatively scattering slab's transmission), so that the far side of a thick cloud is dark.
    float diffuse = cell.y >= 0 ? cell.y : _LightU.x * LIGHT_INV4PI * (1.0 - exp(-tau / max(_LightU.y, 1e-3))) / (1.0 + _LightV.x * tau);
    return col * (OctaveSum(dot(d, ldir), tau, _LightR.w * _LightL[li].x, cell.x) + diffuse);
}

// E40 LM2, the cell diffusion of the sun's light (CloudLightMath.CellDiffusion is the twin): (the octaves' isotropic factor, the source; (1, -1) where the layer has no cell width).
// mi the incidence (tau up to the type's top over tau to the sun), tot the optical depth of the chord of an ellipsoid of the type's height and the cell width along the sun, the slab's
// diffusion profile in the lit surface's coordinates, the side losses, the share that has scattered at all. Computed once per lit sample for the sun; the moons keep the diffusion term.
float2 CellLight(float3 pc, float dens, float tau, float2 topAlt, float w)
{
    float r = length(pc), up = dens * max(topAlt.x - (r - _PlanetR), 0.0), h = max(topAlt.x - topAlt.y, 1.0);
    float mi = clamp((up + _LightX.w) / (tau + _LightX.w), 0.05, 1.0), se = clamp(abs(dot(pc, _Sun.xyz)) / r, 1e-3, 1.0), bn = _LightW.y * mi;
    float tot = max(dens * rsqrt(se * se / (h * h) + (1.0 - se * se) / max(w * w, 1.0)), tau);
    float src = _LightW.x * LIGHT_INV4PI * mi * (_LightW.z + bn * (tot - tau)) / (2.0 * _LightW.z + bn * tot) * exp(-_LightW.w * bn * tau / (_LightW.y * dens * w + 2.0 * _LightW.z)) * (1.0 - exp(-bn * tot));
    return float2(_LightX.x * pow(mi / _LightX.y, _LightX.z), src);
}

float3 BoltLight(float3 pc)
{
    float3 sum = 0;
    int n = (int)_LightT.x;
    [loop] for (int k = 0; k < 4; k++)
    {
        if (k >= n) break;
        float3 v = _BoltPos[k].xyz - pc;
        float r = max(_BoltPos[k].w, 1.0);
        sum += _BoltCol[k].rgb / (1.0 + dot(v, v) / (r * r));
    }
    return sum;
}

// The source function at a march sample of a lit layer (kind 0 weather). dir and amb return the direct and ambient parts (the aid view).
// tauReuse >= 0: the optical depth towards the sun of an earlier sample of the span (the cost package's reuse); tauSunOut returns the one used.
// tauUpReuse >= 0: the optical depth straight up of an earlier sample (the cost package T05's up-tap reuse, UpMode 1); tauUpOut returns the one used.
// _LightT.w is the up mode: 0 a tap march at every lit sample, 1 reuse together with the sun march's reuse, 2 the analytic term (no taps).
// topAlt: the altitudes of the top and the base of the cloud type at the sample (the march's copy of gTopAlt, gBaseAlt): the end of the analytic occlusion's column and the type's
// height for the cell diffusion (E40 LM2).

float3 LightSource(float3 pc, float3 d, float dens, float share, int li, float t, float rot, float tauReuse, float tauUpReuse, float2 topAlt, out float3 dirPart,
                   out float3 ambPart, out float taps, out float tauSunOut, out float tauUpOut)
{
    float3 sunDir = _Sun.xyz;
    int steps = _LightT.y > 0.5 ? (int)_LightT.y : (int)_MarchP.y;
    taps = 0;
    float tauSun = -1, tauUp = -1;
    // The light cache: the march's own first _CacheH.x taps over the distance they cover in a 12 km span (where a span is that long, their places are unchanged), the rest
    // of the way to the sun from the cache at that distance; every layer's near taps stop there, so nothing is counted twice.
    float sunRange = _Sun.w, farTau = 0.0;
    int sunSteps = steps;
#ifdef CLOUD_LIGHTVOL
    // The light volume (LV1) replaces the light cache: a sample that marches to the sun (no reuse) takes the full march near the camera, the far path (lvNear's taps and one
    // read of the volume) beyond the hand-over distance, both blended over the band (LightVolumePlan); slot 4 of the loop below is the far path's near taps.
    float lvShare = 0.0, lvNear = 0.0, lvTau = 0.0;
    int lvSteps = 0;
    bool lvLit = Luma(_SunColor.rgb * SunAir(pc, sunDir)) > 0;
    [branch] if (tauReuse < 0 && lvLit) LightVolumePlan(pc, sunDir, t, steps, lvShare, lvNear, lvSteps, lvTau);
    // The volume's reads, at ONE call site (each call is inlined; copies overflowed the shader compiler's stack): 0 the far path's read point, 1 the spread's read at the sample.
    float3 lvPt[2];
    bool lvNeed[2], lvSp[2];
    int lvFirst[2];
    float lvVal[2], lvW[2];
    lvPt[0] = pc + sunDir * lvNear; lvNeed[0] = lvShare > 0.0; lvSp[0] = false; lvFirst[0] = 0;
    lvPt[1] = pc; lvNeed[1] = _LvS.x > 0.5 && lvLit; lvSp[1] = _LvS.x < 1.5; lvFirst[1] = (_LvS.x > 1.5 && _LvS.x < 2.5) ? 1 : 0;
    [loop] for (int lr = 0; lr < 2; lr++)
    {
        lvVal[lr] = 0.0;
        lvW[lr] = 0.0;
        if (!lvNeed[lr]) continue;
        lvVal[lr] = LightVolumeFetchFrom(lvPt[lr], lvSp[lr], lvFirst[lr], lvW[lr]);
    }
    lvTau = lvVal[0];
    lvShare *= lvW[0];
#else
    [branch] if (_LightS.w > 0.5 && tauReuse < 0 && (int)_CacheH.x < steps)
    {
        float near = _Sun.w * NearShare((int)_CacheH.x, steps, max(_LightR.y, 1.0));
        bool inside;
        float c = CacheFetch(pc + sunDir * near, inside);
        if (inside) { sunRange = -near; sunSteps = max(1, (int)_CacheH.x); farTau = c; }
    }
#endif
    if (tauReuse >= 0) tauSun = tauReuse;
    taps = tauSun >= 0 ? 0.5 : sunSteps;
    float3 sunCol = _SunColor.rgb * SunAir(pc, sunDir);
    // CLOUDR2 P4: the sun's, both moons' and the up tap's optical depths come from ONE call site of LightTau in a loop (the compiler inlines every call: five copies of the light march, each
    // with a copy of the density, were what the Unity 2019.4 shader compiler choked on). Slot 0 the sun, 1 and 2 the moons, 3 straight up.
    int moonSteps = max(1, (int)_LightS.x);
#ifdef CLOUD_LIGHTVOL
    #define LIGHT_SLOTS 5
    #define LIGHT_ERODED ((k == 0 || k == 4) ? (int)_BlendP.w : 0)
#else
    #define LIGHT_SLOTS 4
    #define LIGHT_ERODED k == 0 ? (int)_BlendP.w : 0
#endif
    float3 tdir[LIGHT_SLOTS];
    int tsteps[LIGHT_SLOTS];
    float tspread[LIGHT_SLOTS], trot[LIGHT_SLOTS], ttau[LIGHT_SLOTS], trange[LIGHT_SLOTS];
    bool need[LIGHT_SLOTS];
    float coneSpread = DEV(_LightR.x, 0.0);
    tdir[0] = sunDir; tsteps[0] = sunSteps; tspread[0] = coneSpread; trot[0] = rot; need[0] = tauSun < 0 && Luma(sunCol) > 0;
    trange[0] = sunRange; trange[1] = _Sun.w; trange[2] = _Sun.w; trange[3] = _Sun.w;
    tdir[1] = _MoonDir[0].xyz; tsteps[1] = moonSteps; tspread[1] = coneSpread; trot[1] = rot; need[1] = Luma(_MoonCol[0].rgb) > 0;
    tdir[2] = _MoonDir[1].xyz; tsteps[2] = moonSteps; tspread[2] = coneSpread; trot[2] = rot; need[2] = Luma(_MoonCol[1].rgb) > 0;
    tdir[3] = normalize(pc); tsteps[3] = max(1, (int)_LightS.y); tspread[3] = 0.0; trot[3] = 0.0;
    need[3] = _LightQ.w > 0 && tauUp < 0 && !(_LightT.w > 1.5 && tauUpReuse >= 0) && _LightT.w > 0.5;
#ifdef CLOUD_LIGHTVOL
    // Slot 0 the full march (needed while the far path's share is below 1), slot 4 the far path's near taps (the same first places and erosion reads as slot 0's).
    bool sunNeeded = need[0];
    need[0] = sunNeeded && lvShare < 1.0;
    tdir[4] = sunDir; tsteps[4] = max(lvSteps, 1); tspread[4] = coneSpread; trot[4] = rot; trange[4] = -lvNear;
    need[4] = sunNeeded && lvShare > 0.0 && lvSteps > 0;
#endif
    [loop] for (int k = 0; k < LIGHT_SLOTS; k++)
    {
        ttau[k] = 0.0;
        if (!need[k]) continue;
        ttau[k] = LightTau(pc, tdir[k], li, tsteps[k], t, tspread[k], trot[k], LIGHT_ERODED, trange[k], _LightR.y);
    }
#ifdef CLOUD_LIGHTVOL
    if (sunNeeded)
    {
        tauSun = lvShare <= 0.0 ? ttau[0] : lvShare >= 1.0 ? ttau[4] + lvTau : lerp(ttau[0], ttau[4] + lvTau, lvShare);
        taps = (need[0] ? steps : 0) + (need[4] ? lvSteps : 0);
    }
#else
    if (need[0]) tauSun = ttau[0] + farTau;
#endif
    #undef LIGHT_SLOTS
    #undef LIGHT_ERODED
    tauSunOut = max(tauSun, 0.0);
#ifdef CLOUD_LIGHTVOL
    // LV3: the direct term reads the spread optical depth; the reuse along the span keeps the exact one.
    [branch] if (lvNeed[1] && tauSun > 0.0) tauSun = LightVolumeSpread(tauSun, lvVal[1], lvW[1]);
#endif
    float2 cell = float2(1.0, -1.0);
    [branch] if (_LightM[li].w > 0) cell = CellLight(pc, dens, max(tauSun, 0.0), topAlt, _LightM[li].w);
    float3 direct = DirectOfLight(sunCol, sunDir, d, li, max(tauSun, 0.0), cell);
    direct += DirectOfLight(_MoonCol[0].rgb, _MoonDir[0].xyz, d, li, ttau[1], float2(1.0, -1.0));
    if (need[1]) taps += moonSteps;
    direct += DirectOfLight(_MoonCol[1].rgb, _MoonDir[1].xyz, d, li, ttau[2], float2(1.0, -1.0));
    if (need[2]) taps += moonSteps;
    float powder = lerp(1.0, 1.0 - exp(-2.0 * dens * _LightQ.y), saturate(_LightQ.x * _LightL[li].y));
    // The ambient: the sky from the layer's bottom to its top, tinted, thinned by what lies above, never below the floor.
    float3 sky = lerp(_AmbientBottom.rgb, _AmbientTop.rgb, saturate(share));
    float l = Luma(sky);
    sky = lerp(l.xxx, sky, _LightR.z) * _LightS.z * _LightL[li].z;
    float open = 1.0, skyMs = 0;
    tauUpOut = 0;
    if (_LightQ.w > 0)
    {
        float tu;
        if (tauUp >= 0) tu = tauUp;
        else if (_LightT.w > 1.5 && tauUpReuse >= 0) tu = tauUpReuse;
        else if (_LightT.w > 0.5)
        {
            tu = ttau[3];
            taps += max(1, (int)_LightS.y);
        }
        else
        {
            // FX-T03: the optical depth straight up from the local density and the layer's remaining height (no extra taps: the taps cost 0.9 to 2.4 ms).
            // The column ends at the top of the cloud type at the sample (_LightV.y 1, the default), not at the layer's outer altitude: a layer of several types is
            // far taller than its low types (Kerbin's cumulus layer reaches 7.4 km, its stratocumulus 2.3 km), and to the layer's top the column of a sample at the
            // very top of a low cloud was still 5 km of the local density, which removed the sky's light from every low cloud (E40 lead investigation, series e40-fl).
            // _LightV.y 0 keeps the layer's outer altitude (the earlier behaviour, for the comparison).
            float columnTop = _LightV.y > 0.5 ? topAlt.x : _LayerA[li].y;
            tu = dens * max(columnTop - (length(pc) - _PlanetR), 0.0);
        }
        tauUpOut = tu;
        open = exp(-tu * _LightQ.w);
        skyMs = _LightM[li].x / (1.0 + _LightM[li].y * tu);   // the sky's light that has diffused into the cloud: the two-stream's transmission of the column above
    }
    // The floor is a share of the isotropic light of all lights, so a night lit by a moon is not black either.
    float lightLuma = Luma(sunCol) + Luma(_MoonCol[0].rgb) + Luma(_MoonCol[1].rgb);
    float3 floorLight = _LightQ.z * lightLuma * LIGHT_INV4PI;
    dirPart = direct * powder * _PhaseP.w;
    float3 skyTop = lerp(Luma(_AmbientTop.rgb).xxx, _AmbientTop.rgb, _LightR.z) * _LightS.z * _LightL[li].z;   // the diffused light came in at the top: the sky above, not the local gradient
    ambPart = max((sky * open + skyTop * skyMs) * _PhaseP.w, floorLight.xxx);
    return dirPart + ambPart + BoltLight(pc) * _PhaseP.w;
}
#endif
