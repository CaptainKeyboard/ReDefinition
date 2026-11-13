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
float4 _SunAir;   // the sun's colour through the air (FX-T03): extinction rgb along the zenith path at sea level, scale height in m (w <= 0: the frame's sun colour as is)
float4 _LightU;   // diffusion share (light that has diffused into the deep core, isotropic, as a share of the sun's isotropic light), its optical depth scale, isotropic share of the octaves above the first
float4 _LightV;   // diffusion decay with the optical depth towards the light (0: the saturating term), the analytic occlusion's column ends at the cloud type's top (1) or at the layer's outer altitude (0), unused, unused
float4 _LightL[4];  // per layer: multiple scattering brightness, powder scale, ambient scale, unused
float4 _MoonDir[2], _MoonCol[2];   // towards the light; colour times intensity (rgb, 0: off)
float4 _BoltPos[4], _BoltCol[4];   // planet frame position and range (w); colour times intensity
float4 _CellA;    // parity test cell: centre (planet frame) and horizontal radius (w > 0: the cell replaces the layers' density)
float4 _CellB;    // vertical radius, edge softness, flat base (1), extinction
float4 _CacheA;   // light cache box: minimum corner (planet frame)
float4 _CacheB;   // light cache box: size in m; w: tiles per row of the atlas
sampler2D _LightCache;
static const int CACHE_N = 64;
static const int CACHE_NZ = 32;

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
    if (_CellA.w <= 0) return;
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
    float3 tau = _SunAir.xyz * exp(-max(length(pc) - _PlanetR, 0.0) / _SunAir.w);
    // White balance (T03v2, _LightU.w): the frame's sun colour is the sun at the zenith above the point; only the air path beyond it reddens.
    if (_LightU.w > 0.5) m = max(m - 1.0, 0.0);
    return exp(-tau * m) * smoothstep(-0.02, 0.0, s);
}

// The optical depth from a point along a direction through all layers: the own layer with `steps` taps of growing size over at most _Sun.w m,
// the other layers with half of them (own < 0: all layers with `steps`). The taps may be spread over a cone of tangent `spread`.
float LightTau(float3 pc, float3 dir, int own, int steps, float t, float spread, float rot)
{
    float tau = 0;
    gNoiseLod = 1;   // the light path reads the shape only, not the erosion detail (T02)
    float3 ax = abs(dir.z) < 0.9 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 u = normalize(cross(dir, ax));
    float3 v = cross(dir, u);
    float ratio = max(_LightR.y, 1.0);
    [loop] for (int lj = 0; lj < MAXL; lj++)
    {
        if (lj >= (int)_LayerCount) break;
        float4 sp;
        LayerSpans(lj, pc, dir, 1e30, sp);
        CellClip(pc, dir, sp);
        int n = (lj == own || own < 0) ? steps : max(1, steps / 2);
        [loop] for (int k = 0; k < 2; k++)
        {
            float a = k == 0 ? sp.x : sp.z, b = k == 0 ? sp.y : sp.w;
            if (b <= a) continue;
            b = min(b, a + _Sun.w);
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
                acc += LayerDensity(lj, q, length(q) - _PlanetR, t, qs) * (edge - prev);
                prev = edge;
                gain *= ratio;
            }
            tau += acc;
        }
    }
    gNoiseLod = 0;
    return tau;
}

// ---- the light cache ----
// The atlas pixel of a voxel, and back. CACHE_N x CACHE_N voxels per tile, CACHE_NZ tiles, _CacheB.w tiles per row.
float3 CacheVoxelCentre(float2 pixel)
{
    int tilesX = (int)_CacheB.w;
    int tx = (int)floor(pixel.x / CACHE_N), ty = (int)floor(pixel.y / CACHE_N);
    int iz = ty * tilesX + tx;
    float3 f = float3(fmod(pixel.x, (float)CACHE_N), fmod(pixel.y, (float)CACHE_N), (float)iz) + 0.5;
    return _CacheA.xyz + f / float3(CACHE_N, CACHE_N, CACHE_NZ) * _CacheB.xyz;
}

float4 CacheTexel(int ix, int iy, int iz)
{
    int tilesX = (int)_CacheB.w;
    float2 px = float2((iz % tilesX) * CACHE_N + ix, (iz / tilesX) * CACHE_N + iy) + 0.5;
    int rows = (CACHE_NZ + tilesX - 1) / tilesX;
    return tex2Dlod(_LightCache, float4(px / float2(tilesX * CACHE_N, rows * CACHE_N), 0, 0));
}

// Trilinear read of (tau towards the sun, tau straight up) from the atlas; inside is false outside the box.
float2 CacheFetch(float3 pc, out bool inside)
{
    float3 f = (pc - _CacheA.xyz) / _CacheB.xyz * float3(CACHE_N, CACHE_N, CACHE_NZ) - 0.5;
    inside = all(f > -0.5) && f.x < CACHE_N - 0.5 && f.y < CACHE_N - 0.5 && f.z < CACHE_NZ - 0.5;
    float3 fc = clamp(f, 0.0, float3(CACHE_N - 1, CACHE_N - 1, CACHE_NZ - 1));
    int3 i0 = (int3)floor(fc);
    int3 i1 = min(i0 + 1, int3(CACHE_N - 1, CACHE_N - 1, CACHE_NZ - 1));
    float3 w = fc - i0;
    float2 c00 = lerp(CacheTexel(i0.x, i0.y, i0.z).xy, CacheTexel(i1.x, i0.y, i0.z).xy, w.x);
    float2 c10 = lerp(CacheTexel(i0.x, i1.y, i0.z).xy, CacheTexel(i1.x, i1.y, i0.z).xy, w.x);
    float2 c01 = lerp(CacheTexel(i0.x, i0.y, i1.z).xy, CacheTexel(i1.x, i0.y, i1.z).xy, w.x);
    float2 c11 = lerp(CacheTexel(i0.x, i1.y, i1.z).xy, CacheTexel(i1.x, i1.y, i1.z).xy, w.x);
    return lerp(lerp(c00, c10, w.y), lerp(c01, c11, w.y), w.z);
}

// ---- the octaves ----
// sum_n b^n * Phase(mu, c^n) * exp(-tau * a^n); octave 0 is the single scattering, the others carry the multiple scattering brightness.
float OctaveSum(float mu, float tau, float msBrightness)
{
    int n = (int)clamp(_LightP.x, 1.0, 4.0);
    float sum = 0;
    float an = 1.0, bn = 1.0, cn = 1.0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        // The octaves above the first stand for light that has scattered several times: their directions are more isotropic than the single
        // scattering's, which _LightU.z (the isotropic share) expresses (FX-T03).
        if (i < n) sum += (i == 0 ? 1.0 : msBrightness) * bn * (i == 0 ? Phase(mu, cn) : lerp(Phase(mu, cn), LIGHT_INV4PI, _LightU.z)) * exp(-tau * an);
        an *= _LightP.y; bn *= _LightP.z; cn *= _LightP.w;
    }
    return sum;
}

// One light's contribution at a sample: its colour times the octave sum with its own light march. tauIn >= 0 skips the march.
float3 DirectOfLight(float3 col, float3 ldir, float3 pc, float3 d, int li, int steps, float t, float tauIn, float rot)
{
    if (Luma(col) <= 0) return 0;
    float tau = tauIn >= 0 ? tauIn : LightTau(pc, ldir, li, steps, t, _LightR.x, rot);
    // The octaves thin out with depth (exp(-tau a^n)); deep in a thick core the light that entered has diffused and leaves isotropically, which
    // the octaves cannot reach at tau of tens: a saturating isotropic term takes over there, nothing at the sunlit edge (tau -> 0).
    // T03v2: the term falls off again by 1 / (1 + decay tau) (a conservatively scattering slab's transmission), so that the far side of a thick cloud is dark.
    float diffuse = _LightU.x * LIGHT_INV4PI * (1.0 - exp(-tau / max(_LightU.y, 1e-3))) / (1.0 + _LightV.x * tau);
    return col * (OctaveSum(dot(d, ldir), tau, _LightR.w * _LightL[li].x) + diffuse);
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
// topAlt: the altitude of the top of the cloud type at the sample (the march's copy of gTopAlt), the end of the analytic occlusion's column.
float3 LightSource(float3 pc, float3 d, float dens, float share, int li, float t, float rot, float tauReuse, float tauUpReuse, float topAlt, out float3 dirPart,
                   out float3 ambPart, out float taps, out float tauSunOut, out float tauUpOut)
{
    float3 sunDir = _Sun.xyz;
    int steps = _LightT.y > 0.5 ? (int)_LightT.y : (int)_MarchP.y;
    taps = 0;
    float tauSun = -1, tauUp = -1;
    if (_LightS.w > 0.5)
    {
        bool inside;
        float2 c = CacheFetch(pc, inside);
        if (inside) { tauSun = c.x; tauUp = c.y; }
    }
    if (tauReuse >= 0) tauSun = tauReuse;
    taps = tauSun >= 0 ? 0.5 : steps;
    float3 sunCol = _SunColor.rgb * SunAir(pc, sunDir);
    if (tauSun < 0 && Luma(sunCol) > 0) tauSun = LightTau(pc, sunDir, li, steps, t, _LightR.x, rot);
    tauSunOut = max(tauSun, 0.0);
    float3 direct = DirectOfLight(sunCol, sunDir, pc, d, li, steps, t, max(tauSun, 0.0), rot);
    [unroll] for (int m = 0; m < 2; m++)
    {
        direct += DirectOfLight(_MoonCol[m].rgb, _MoonDir[m].xyz, pc, d, li, max(1, (int)_LightS.x), t, -1.0, rot);
        if (Luma(_MoonCol[m].rgb) > 0) taps += max(1, (int)_LightS.x);
    }
    float powder = lerp(1.0, 1.0 - exp(-2.0 * dens * _LightQ.y), saturate(_LightQ.x * _LightL[li].y));
    // The ambient: the sky from the layer's bottom to its top, tinted, thinned by what lies above, never below the floor.
    float3 sky = lerp(_AmbientBottom.rgb, _AmbientTop.rgb, saturate(share));
    float l = Luma(sky);
    sky = lerp(l.xxx, sky, _LightR.z) * _LightS.z * _LightL[li].z;
    float open = 1.0;
    tauUpOut = 0;
    if (_LightQ.w > 0)
    {
        float tu;
        if (tauUp >= 0) tu = tauUp;
        else if (_LightT.w > 1.5 && tauUpReuse >= 0) tu = tauUpReuse;
        else if (_LightT.w > 0.5)
        {
            tu = LightTau(pc, normalize(pc), li, max(1, (int)_LightS.y), t, 0, 0);
            taps += max(1, (int)_LightS.y);
        }
        else
        {
            // FX-T03: the optical depth straight up from the local density and the layer's remaining height (no extra taps: the taps cost 0.9 to 2.4 ms).
            // The column ends at the top of the cloud type at the sample (_LightV.y 1, the default), not at the layer's outer altitude: a layer of several types is
            // far taller than its low types (Kerbin's cumulus layer reaches 7.4 km, its stratocumulus 2.3 km), and to the layer's top the column of a sample at the
            // very top of a low cloud was still 5 km of the local density, which removed the sky's light from every low cloud (E40 lead investigation, series e40-fl).
            // _LightV.y 0 keeps the layer's outer altitude (the earlier behaviour, for the comparison).
            float columnTop = _LightV.y > 0.5 ? topAlt : _LayerA[li].y;
            tu = dens * max(columnTop - (length(pc) - _PlanetR), 0.0);
        }
        tauUpOut = tu;
        open = exp(-tu * _LightQ.w);
    }
    // The floor is a share of the isotropic light of all lights, so a night lit by a moon is not black either.
    float lightLuma = Luma(sunCol) + Luma(_MoonCol[0].rgb) + Luma(_MoonCol[1].rgb);
    float3 floorLight = _LightQ.z * lightLuma * LIGHT_INV4PI;
    dirPart = direct * powder * _PhaseP.w;
    ambPart = max(sky * open * _PhaseP.w, floorLight.xxx);
    return dirPart + ambPart + BoltLight(pc) * _PhaseP.w;
}
#endif
