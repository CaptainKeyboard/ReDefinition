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
float4 _LightR;   // x unused (the cone spread went, 2026-11-23), tap step ratio, sky tint multiplier (0 grey .. 1 sky hue), multiple scattering brightness
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

// The most taps a march sample's light takes through one OTHER layer (LightTau).
#define LIGHT_OTHER_TAPS 16

// The optical depth from a point along a direction through all layers: the own layer with `steps` taps of growing size (each `ratio` times the one before) over at most `range` m (below 0: up to -range m from pc),
// the other layers with even taps of about one of that layer's noise scales (at least half of `steps`, at most LIGHT_OTHER_TAPS) (own < 0: all layers with `steps` growing taps).
// The other layers' taps: four taps each twice the one before over a span of 10 km are 0.7 to 5.3 km long, and each reads the density at one point: a cloud above
// between those points cast no shadow, one on a point a hard-edged shadow of the tap's whole length, and the shadows changed completely with the tap count (K-T2
// perfNpic2560dlssqshadowtaps, PRUEFPROTOKOLL 202). Even taps of the layer's noise scale (a cumulus layer's 250 m, up to the cap) resolve the clouds the way the layer
// draws them; a map-empty tap ends at the coverage bound.
// CLOUDR2 P3: the first `eroded` taps of each span read the density with its erosion detail (the lobes that shade each other are the detail's, not the shape's); the others the shape only.
float LightTau(float3 pc, float3 dir, int own, int steps, float t, int eroded, float range, float ratioIn)
{
    float tau = 0;
    float ratio = max(ratioIn, 1.0);
    // Measurement only (layers.costDebug bit 32): the other layers with the former half of the taps, each `ratio` times the one before.
    bool formerOther = fmod(floor(DEV(_CostP.y, 0.0) / 32.0), 2.0) > 0.5;
    [loop] for (int lj = 0; lj < MAXL; lj++)
    {
        if (lj >= (int)_LayerCount) break;
        // Cost attribution only (layers.costDebug bit 8): the march towards a light crosses the sample's own layer alone.
        if (own >= 0 && lj != own && fmod(floor(DEV(_CostP.y, 0.0) * 0.125), 2.0) > 0.5) continue;
        float4 sp;
        LayerSpans(lj, pc, dir, 1e30, sp);
        CellClip(pc, dir, sp);
        bool other = own >= 0 && lj != own;
        [loop] for (int k = 0; k < 2; k++)
        {
            float a = k == 0 ? sp.x : sp.z, b = k == 0 ? sp.y : sp.w;
            // range > 0: at most that far from the span's start; range < 0: no farther than -range from pc (the light cache's near taps, whose far part the cache holds).
            b = range < 0 ? min(b, -range) : min(b, a + range);
            if (b <= a) continue;
            int n = other ? max(1, steps / 2) : steps;
            float r = ratio;
            [branch] if (other && !formerOther)
            {
                float scale = _LayerD[lj].y > 0.0 ? 1.0 / _LayerD[lj].y : b - a;
                n = clamp((int)ceil((b - a) / max(scale, 1.0)), max(1, steps / 2), LIGHT_OTHER_TAPS);
                r = 1.0;
            }
            float norm = r == 1.0 ? (float)n : (pow(r, (float)n) - 1.0) / (r - 1.0);
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
                float qs;
                DensityRead qr;   // the tap needs only the density
                acc += LayerDensityEx(lj, q, length(q) - _PlanetR, t, i < eroded ? 0 : 1, qs, qr) * (edge - prev);   // the light path reads the shape only, not the erosion detail (T02), except the first taps (P3)
                prev = edge;
                gain *= r;
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
// topAlt: the altitudes of the top and the base of the cloud type at the sample (the march's DensityRead.topAlt, .baseAlt): the end of the analytic occlusion's column and the type's
// height for the cell diffusion (E40 LM2).

float3 LightSource(float3 pc, float3 d, float dens, float share, int li, float t, float tauReuse, float tauUpReuse, float2 topAlt, out float3 dirPart,
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
    [branch] if (_LightS.w > 0.5 && tauReuse < 0 && (int)_CacheH.x < steps)
    {
        float near = _Sun.w * NearShare((int)_CacheH.x, steps, max(_LightR.y, 1.0));
        bool inside;
        float c = CacheFetch(pc + sunDir * near, inside);
        if (inside) { sunRange = -near; sunSteps = max(1, (int)_CacheH.x); farTau = c; }
    }
    if (tauReuse >= 0) tauSun = tauReuse;
    taps = tauSun >= 0 ? 0.5 : sunSteps;
    float3 sunCol = _SunColor.rgb * SunAir(pc, sunDir);
    // CLOUDR2 P4: the sun's, both moons' and the up tap's optical depths come from ONE call site of LightTau in a loop (the compiler inlines every call: five copies of the light march, each
    // with a copy of the density, were what the Unity 2019.4 shader compiler choked on). Slot 0 the sun, 1 and 2 the moons, 3 straight up.
    int moonSteps = max(1, (int)_LightS.x);
    #define LIGHT_SLOTS 4
    #define LIGHT_ERODED k == 0 ? (int)_BlendP.w : 0
    float3 tdir[LIGHT_SLOTS];
    int tsteps[LIGHT_SLOTS];
    float ttau[LIGHT_SLOTS], trange[LIGHT_SLOTS];
    bool need[LIGHT_SLOTS];
    tdir[0] = sunDir; tsteps[0] = sunSteps; need[0] = tauSun < 0 && Luma(sunCol) > 0;
    trange[0] = sunRange; trange[1] = _Sun.w; trange[2] = _Sun.w; trange[3] = _Sun.w;
    tdir[1] = _MoonDir[0].xyz; tsteps[1] = moonSteps; need[1] = Luma(_MoonCol[0].rgb) > 0;
    tdir[2] = _MoonDir[1].xyz; tsteps[2] = moonSteps; need[2] = Luma(_MoonCol[1].rgb) > 0;
    tdir[3] = normalize(pc); tsteps[3] = max(1, (int)_LightS.y);
    need[3] = _LightQ.w > 0 && tauUp < 0 && !(_LightT.w > 1.5 && tauUpReuse >= 0) && _LightT.w > 0.5;
    [loop] for (int k = 0; k < LIGHT_SLOTS; k++)
    {
        ttau[k] = 0.0;
        if (!need[k]) continue;
        ttau[k] = LightTau(pc, tdir[k], li, tsteps[k], t, LIGHT_ERODED, trange[k], _LightR.y);
    }
    if (need[0]) tauSun = ttau[0] + farTau;
    #undef LIGHT_SLOTS
    #undef LIGHT_ERODED
    tauSunOut = max(tauSun, 0.0);
    float2 cell = float2(1.0, -1.0);
    [branch] if (_LightM[li].w > 0)
    {
        // PRUEFPROTOKOLL 271: in a plane-parallel layer (cells at least 8 times as wide as the type is tall: a deck) the own optical depth towards the sun is the column above over the
        // sun's sine; what tauSun holds beyond it lies in other layers' clouds, which pass the two-stream share 1 / (1 + decay tau) of the sun's flux on, diffused
        // (CloudLightMath.OtherLayersTransmission). With the whole tauSun a deck under a thin cloud read a grazing incidence and lost its diffusion: a cloud of tau 2 took 55 % of
        // the deck's light, the physics' 18 %. A cell (a cumulus) keeps the whole tauSun: its own optical depth along the sun is not its column over the sine.
        float typeH = max(topAlt.x - topAlt.y, 1.0);
        float plane = saturate((_LightM[li].w - 4.0 * typeH) / (4.0 * typeH));
        float r = length(pc);
        float own = dens * max(topAlt.x - (r - _PlanetR), 0.0) / clamp(dot(pc, sunDir) / r, 0.05, 1.0);
        float other = plane * max(tauSunOut - own, 0.0);
        cell = CellLight(pc, dens, tauSunOut - other, topAlt, _LightM[li].w);
        cell.y /= 1.0 + _LightM[li].y * other;
    }
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
