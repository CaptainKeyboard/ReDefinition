// The far view of the own cloud system's layer core (package T06): the aerial perspective of the clouds and the distance LOD of the shape
// noise. Included by ReDefinitionAtmospheresLayers.shader after ReDefinitionAtmospheresNoise.cginc (it uses HitSphere, _PlanetR, _LayerD,
// the noise uniforms and the march's gNoiseFoot of those files). The CPU twin in double precision is
// src/Atmospheres/Render/Layers/FarView/FarViewMath.cs; the unit tests hold the two to each other.
//
// Aerial perspective: the optical depth of the air along the ray from the camera to the cloud (the tau-weighted mean distance of the cloud on
// the ray), an exponential atmosphere of a Rayleigh and a grey aerosol species, the straight ray cut into six pieces over each of which the
// altitude is taken as linear (the integral of an exponential over a linear altitude is closed). The cloud's premultiplied colour is mixed
// with the horizon sky by the transmittance of that air: rgb' = rgb T + alpha L (1 - T).
//
// Distance LOD of the shape: where the march's sample spacing is wider than the noise's feature size, the single noise sample is replaced
// by the mean of the shape over the noise (the volume is rank normalised, so the noise value is uniform), in closed form. The grain of an
// under-sampled limb or horizon (one sample per kilometre-wide step hits or misses a cloud by chance) becomes the coverage it stands for.
//
// Techniques: Bruneton and Neyret, "Precomputed Atmospheric Scattering", EGSR 2008, and Hillaire, "A Scalable and Production
// Ready Sky and Atmosphere Rendering Technique", EGSR 2020 (the exponential atmosphere and its coefficients); the exponential fog mix of the
// real-time cloud renderers (Schneider, SIGGRAPH 2015; Hillaire, SIGGRAPH 2016).
#ifndef REDEFINITION_ATMOSPHERES_FARVIEW
#define REDEFINITION_ATMOSPHERES_FARVIEW

float4 _FarA;   // x aerial perspective on (1), y strength on the optical depth, z Rayleigh scale height (m), w aerosol scale height (m)
float4 _FarB;   // Rayleigh extinction per m at sea level (rgb), aerosol extinction per m (w, grey)
float4 _FarC;   // the in-scattered colour of the air at the horizon (rgb)
float4 _FarD;   // x shape-mean LOD on (1), y the spacing in noise scales from which the mean takes over, z the spacing at which it has taken over,
                // w the fewest samples in a span of a layer (0: the march's own count)

// The integral of exp(-h / H) over a path of length L along which the altitude is linear from h0 to h1.
float FarChord(float h0, float h1, float len, float H)
{
    h0 = max(h0, 0.0);
    h1 = max(h1, 0.0);
    float dh = h1 - h0;
    if (abs(dh) < 1e-3 * H) return len * exp(-0.5 * (h0 + h1) / H);
    return len * H / dh * (exp(-h0 / H) - exp(-h1 / H));
}

// The optical depth per channel of the air along o + s d, s from 0 to t (o relative to the planet's centre).
float3 FarAirTau(float3 o, float3 d, float t)
{
    float top = _PlanetR + 7.0 * max(_FarA.z, _FarA.w);
    float a0, a1;
    if (!HitSphere(o, d, top, a0, a1)) return 0;
    float s0 = max(0.0, a0), s1 = min(t, a1);
    if (s1 <= s0) return 0;
    float seg = (s1 - s0) / 6.0;
    float hPrev = length(o + d * s0) - _PlanetR;
    float sumR = 0, sumM = 0;
    [unroll] for (int k = 0; k < 6; k++)
    {
        float hNext = length(o + d * (s0 + seg * (k + 1))) - _PlanetR;
        sumR += FarChord(hPrev, hNext, seg, _FarA.z);
        sumM += FarChord(hPrev, hNext, seg, _FarA.w);
        hPrev = hNext;
    }
    return (_FarB.rgb * sumR + _FarB.w * sumM) * _FarA.y;
}

// The antiderivative of clamp(m (u - er), 0, 1).
float FarAnti(float u, float er, float m, float u1)
{
    if (u <= er) return 0.0;
    if (u < u1) return 0.5 * m * (u - er) * (u - er);
    float p = u1 - er;
    return 0.5 * m * p * p + (u - u1);
}

// The value of the shape at u = (cov + (n - 0.5) amount + coverage - 1) / edge + 0.5 once the detail is dropped (NoiseShareAt at its coarsest LOD).
float FarEdgeValue(float u, float erosion)
{
    float s = saturate(u);
    if (s <= 0.0 || s >= 1.0 || erosion <= 0.0) return s;
    float er = erosion * (1.0 - _NoiseS.w);
    float c = saturate((s - er) / max(1.0 - er, 1e-3));
    return saturate(c * (_NoiseR.x + _NoiseR.y * erosion));
}

// The mean of the shape over the uniform noise value, in closed form (FarViewMath.ShapeMean).
float FarShareMean(float cov, float coverage, float edge, float amount, float erosion)
{
    float x0 = (cov + coverage - 1.0) / edge + 0.5;
    float a = amount / edge;
    if (a < 1e-3) return FarEdgeValue(x0, erosion);
    float lo = x0 - 0.5 * a, hi = x0 + 0.5 * a;
    float er = erosion > 0.0 ? erosion * (1.0 - _NoiseS.w) : 0.0;
    float k = erosion > 0.0 ? _NoiseR.x + _NoiseR.y * erosion : 1.0;
    float m = max(k, 1e-6) / max(1.0 - er, 1e-3);
    float u1 = er + 1.0 / m;
    float b = min(hi, 1.0);
    float integral = b > lo ? FarAnti(b, er, m, u1) - FarAnti(lo, er, m, u1) : 0.0;
    float ones = max(0.0, hi - max(lo, 1.0));
    return (integral + ones) / (hi - lo);
}

// The share of the column that is cloud at a point: the noise sample where the march's spacing resolves the noise, its mean where it does
// not, a blend between. Replaces the noise file's NoiseShare (which is renamed NoiseShareNear); the light path (gNoiseLod) keeps the sample.
float NoiseShare(int li, float3 pc, float cov, float coverage, float edge, float amount, float erosion)
{
    float scale = _LayerD[li].y > 0 ? 1.0 / _LayerD[li].y : 0;
    if (_FarD.x > 0.5 && gNoiseLod == 0 && gNoiseFoot > 0 && scale > 0 && amount > 0 && _NoiseP.x > 0.5)
    {
        float lod = saturate((gNoiseFoot - _FarD.y * scale) / max((_FarD.z - _FarD.y) * scale, 1e-3));
        if (lod >= 1.0) return FarShareMean(cov, coverage, edge, amount, erosion);
        if (lod > 0.0) return lerp(NoiseShareNear(li, pc, cov, coverage, edge, amount, erosion), FarShareMean(cov, coverage, edge, amount, erosion), lod);
    }
    return NoiseShareNear(li, pc, cov, coverage, edge, amount, erosion);
}

#endif
