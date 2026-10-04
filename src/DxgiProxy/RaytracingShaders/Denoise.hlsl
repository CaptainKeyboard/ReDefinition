// ReDefinition's own denoiser for the raytraced signals, after SVGF (Schied et al.
// 2017, "Spatiotemporal Variance-Guided Filtering"): accumulation over frames along
// the reprojected history, a variance estimate from the accumulated moments, and an
// à-trous wavelet filter whose weights stop at depth, normal and luminance edges.
//
// The temporal and variance passes take one signal each; each à-trous step takes all of
// them at once, so that a tap's depth and normal are read and weighed once for all. The
// passes share the frame's constants with the raytracing passes and take their
// per-dispatch values as root constants.

#include "RtCommon.hlsli"

cbuffer Step : register(b1)
{
    uint stepSize;      // the à-trous step, 1, 2, 4...
    uint signalKind;    // 0 a scalar in x, 1 a colour in rgb, 2 the reflections' colour
                        // (Raytracing.h, kRtDenoised); for the à-trous steps, which of the
                        // four signals (kAtrousSignals) are there, one bit each
    uint stepFlags;     // 1: the history is not to be used; 2: motion vectors are bound;
                        // 4: the last à-trous step, which writes the texture Unity reads
    uint iteration;
};

// The denoiser's bindings in space 3 of the service's root signature (Raytracing.cpp,
// BuildPipelinesLocked).
Texture2D<float4> inputA : register(t0, space3);
Texture2D<float4> inputB : register(t1, space3);
Texture2D<float4> inputC : register(t2, space3);
Texture2D<float> linearDepthNow : register(t3, space3);
Texture2D<float4> normalNow : register(t4, space3);
Texture2D<float> linearDepthBefore : register(t5, space3);
Texture2D<float4> normalBefore : register(t6, space3);
Texture2D<float4> motion : register(t7, space3);
RWTexture2D<float4> outputA : register(u0, space3);
RWTexture2D<float4> outputB : register(u1, space3);
// The à-trous steps' fourth signal, and their third and fourth outputs; Reconstruct.hlsl
// has the Ray Reconstruction guides in u2 and u3. inputD shares t5 with
// linearDepthBefore: the temporal step reads the one, the à-trous steps the other, and
// no pass reads both (RaytracingDenoise.cpp binds t5 by the step).
Texture2D<float4> inputD : register(t5, space3);
RWTexture2D<float4> outputC : register(u2, space3);
RWTexture2D<float4> outputD : register(u3, space3);

float Luminance(float4 value)
{
    return signalKind == 0u ? value.x : dot(value.rgb, float3(0.2126f, 0.7152f, 0.0722f));
}

// The view distance (clip w) and world normal of this frame, for the denoiser and for
// the next frame's history.
[numthreads(8, 8, 1)]
void DenoisePrepare(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    if (IsSky(depth))
    {
        outputA[id.xy] = float4(0.0f, 0.0f, 0.0f, 0.0f);
        outputB[id.xy] = float4(0.0f, 1.0f, 0.0f, 0.0f);
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    float4 clip = mul(worldToClip, float4(position, 1.0f));
    outputA[id.xy] = float4(clip.w, 0.0f, 0.0f, 0.0f);
    SurfaceData surface = SurfaceAt(id.xy, position);
    outputB[id.xy] = float4(surface.normal, surface.smoothness);
}

// Temporal: the noisy signal (A) blended into the history (B) found where the pixel was
// the frame before, with the moments (C). Writes the colour (A) and the moments and
// history length (B).
[numthreads(8, 8, 1)]
void DenoiseTemporal(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float4 noisy = inputA[id.xy];
    float z = linearDepthNow[id.xy];
    if (z <= 0.0f)
    {
        outputA[id.xy] = noisy;
        outputB[id.xy] = 0.0f;
        return;
    }
    float3 n = normalNow[id.xy].xyz;
    float2 size = float2(frameSize);
    float2 uv = (float2(id.xy) + 0.5f) / size;

    // Where the pixel was: by the motion vectors where they are bound, else by the
    // camera's two matrices.
    float2 previousUv;
    float expectedDepth;
    float3 position = ClipToWorld(PixelToNdc(id.xy), depthTexture[id.xy]);
    float4 previousClip = mul(prevWorldToClip, float4(position, 1.0f));
    expectedDepth = previousClip.w;
    if ((stepFlags & 2u) != 0u)
        previousUv = uv - motion[id.xy].xy;
    else
        previousUv = float2(previousClip.x / previousClip.w * 0.5f + 0.5f, 0.5f - previousClip.y / previousClip.w * 0.5f);

    float4 history = 0.0f;
    float4 moments = 0.0f;
    float weightSum = 0.0f;
    if ((stepFlags & 1u) == 0u && all(previousUv > 0.0f) && all(previousUv < 1.0f))
    {
        // The four texels around the previous position, each kept where its depth and
        // normal say it is the same surface.
        float2 texel = previousUv * size - 0.5f;
        int2 base = int2(floor(texel));
        float2 f = texel - float2(base);
        [unroll]
        for (int k = 0; k < 4; ++k)
        {
            int2 offset = int2(k & 1, k >> 1);
            int2 p = base + offset;
            if (any(p < 0) || any(p >= int2(frameSize)))
                continue;
            float w = (offset.x == 1 ? f.x : 1.0f - f.x) * (offset.y == 1 ? f.y : 1.0f - f.y);
            float zBefore = linearDepthBefore[p];
            float3 nBefore = normalBefore[p].xyz;
            bool same = zBefore > 0.0f && abs(zBefore - expectedDepth) < 0.03f * expectedDepth + 0.02f
                        && dot(nBefore, n) > 0.9f;
            if (!same)
                continue;
            history += inputB[p] * w;
            moments += inputC[p] * w;
            weightSum += w;
        }
    }

    float luminance = Luminance(noisy);
    float4 current = float4(luminance, luminance * luminance, 1.0f, 0.0f);
    if (weightSum > 0.01f)
    {
        history /= weightSum;
        moments /= weightSum;
        float length = min(moments.z + 1.0f, 32.0f);
        // Smooth surfaces keep a shorter history of their reflections, which move with the
        // view.
        float smooth = signalKind == 2u ? normalNow[id.xy].w : 0.0f;
        float maxLength = lerp(32.0f, 8.0f, saturate((smooth - 0.6f) / 0.4f));
        length = min(length, maxLength);
        float alpha = max(1.0f / length, 0.03f);
        float4 colour = lerp(history, noisy, alpha);
        float2 m = lerp(moments.xy, current.xy, max(1.0f / length, 0.2f));
        outputA[id.xy] = colour;
        outputB[id.xy] = float4(m, length, 0.0f);
    }
    else
    {
        outputA[id.xy] = noisy;
        outputB[id.xy] = current;
    }
}

float DepthWeight(float zp, float zq, float step)
{
    return exp(-abs(zp - zq) / (zp * 0.01f * step + 1e-3f));
}

// x to the 32nd and 64th power by squaring, without pow's logarithm and exponential.
float Pow32(float x)
{
    x *= x;
    x *= x;
    x *= x;
    x *= x;
    return x * x;
}

float Pow64(float x)
{
    float x32 = Pow32(x);
    return x32 * x32;
}

// Variance: from the moments where the history is long enough, else from the
// neighbourhood's colours. Writes the colour with the standard deviation in w (A): the
// later à-trous steps keep it in half floats, whose range a variance outgrows.
[numthreads(8, 8, 1)]
void DenoiseVariance(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float4 colour = inputA[id.xy];
    float4 moments = inputB[id.xy];
    float z = linearDepthNow[id.xy];
    if (z <= 0.0f)
    {
        outputA[id.xy] = float4(colour.rgb, 0.0f);
        return;
    }
    float variance = max(0.0f, moments.y - moments.x * moments.x);
    if (moments.z < 4.0f)
    {
        float3 n = normalNow[id.xy].xyz;
        float sum = 0.0f, sum2 = 0.0f, weights = 0.0f;
        for (int y = -3; y <= 3; ++y)
            for (int x = -3; x <= 3; ++x)
            {
                int2 p = int2(id.xy) + int2(x, y);
                if (any(p < 0) || any(p >= int2(frameSize)))
                    continue;
                float zq = linearDepthNow[p];
                if (zq <= 0.0f)
                    continue;
                float w = DepthWeight(z, zq, 3.0f) * Pow32(saturate(dot(n, normalNow[p].xyz)));
                float l = Luminance(inputA[p]);
                sum += l * w;
                sum2 += l * l * w;
                weights += w;
            }
        if (weights > 0.0f)
        {
            float mean = sum / weights;
            variance = max(variance, max(0.0f, sum2 / weights - mean * mean) * 4.0f / max(moments.z, 1.0f));
        }
    }
    outputA[id.xy] = float4(signalKind == 0u ? float3(colour.x, 0.0f, 0.0f) : colour.rgb, sqrt(variance));
}

// The signals of the à-trous steps, by their bit in signalKind: the sun's visibility, the
// diffuse light, the reflections, the occlusion (Raytracing.h, kRtDenoised).
static const uint kAtrousSignals = 4u;

bool IsColour(uint s)
{
    return s == 1u || s == 2u;
}

float4 ReadSignal(uint s, int2 p)
{
    switch (s)
    {
    case 0u: return inputA[p];
    case 1u: return inputB[p];
    case 2u: return inputC[p];
    default: return inputD[p];
    }
}

float SignalLuminance(uint s, float4 value)
{
    return IsColour(s) ? dot(value.rgb, float3(0.2126f, 0.7152f, 0.0722f)) : value.x;
}

// The half float nearest to each value: a typed store of a full float into a half-float
// texture rounds towards zero, which over the three steps in half floats took 0.06 to
// 0.09 % off the signals (measured); the nearest errs both ways alike.
float4 NearestHalf(float4 value)
{
    uint4 below = f32tof16(value);
    float4 low = f16tof32(below);
    float4 high = f16tof32(below + 1u);
    return select(abs(value - low) <= abs(high - value), low, high);
}

// What an à-trous step writes: the colour and its standard deviation for the next step;
// from the last, the texture Unity reads, a scalar in x with the view depth in w, as the
// sun's pass writes it, or a colour in rgb.
void WriteSignal(uint s, uint2 id, float4 value, float z)
{
    if ((stepFlags & 4u) != 0u)
        value = IsColour(s) ? float4(value.rgb, 1.0f) : float4(value.x, 0.0f, 0.0f, z);
    else if (iteration > 0u)
        value = NearestHalf(value);
    switch (s)
    {
    case 0u: outputA[id] = value; break;
    case 1u: outputB[id] = value; break;
    case 2u: outputC[id] = value; break;
    default: outputD[id] = value; break;
    }
}

// One à-trous step at stepSize for each signal there: its colour and variance (as the
// standard deviation, in w) filtered by a 5x5 B3 spline kernel, each tap weighed by how
// alike its depth, normal and luminance are.
[numthreads(8, 8, 1)]
void DenoiseAtrous(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float4 centre[kAtrousSignals];
    [unroll]
    for (uint s = 0u; s < kAtrousSignals; ++s)
        centre[s] = (signalKind & (1u << s)) != 0u ? ReadSignal(s, int2(id.xy)) : 0.0f;
    float zp = linearDepthNow[id.xy];
    float4 normalSmooth = normalNow[id.xy];
    // Smooth reflections keep their detail: only the first two steps filter them.
    bool smoothStop = normalSmooth.w > 0.8f && iteration >= 2u;
    bool filtered[kAtrousSignals];
    float lp[kAtrousSignals];
    float lumaRate[kAtrousSignals];
    [unroll]
    for (uint s = 0u; s < kAtrousSignals; ++s)
    {
        filtered[s] = (signalKind & (1u << s)) != 0u && zp > 0.0f && !(s == 2u && smoothStop);
        lp[s] = SignalLuminance(s, centre[s]);
        lumaRate[s] = 0.0f;
        if (!filtered[s])
            continue;
        // The variance around the centre, blurred 3x3, steadies the luminance weight.
        float variance = 0.0f;
        float blur = 0.0f;
        [unroll]
        for (int vy = -1; vy <= 1; ++vy)
            [unroll]
            for (int vx = -1; vx <= 1; ++vx)
            {
                int2 p = clamp(int2(id.xy) + int2(vx, vy), int2(0, 0), int2(frameSize) - 1);
                float k = (vx == 0 ? 0.5f : 0.25f) * (vy == 0 ? 0.5f : 0.25f);
                float deviation = ReadSignal(s, p).w;
                variance += deviation * deviation * k;
                blur += k;
            }
        variance /= blur;
        // The luminance weight's exponent per unit of difference, in log2: one exp2 per
        // tap for it and the depth weight, exp(-a) exp(-b) = exp2(-(a + b) log2(e)).
        lumaRate[s] = 1.442695f / (4.0f * sqrt(max(variance, 0.0f)) + 1e-4f);
    }
    float depthRate = 1.442695f / (zp * 0.01f * float(stepSize) + 1e-3f);

    const float kernel[3] = { 3.0f / 8.0f, 1.0f / 4.0f, 1.0f / 16.0f };
    float3 sum[kAtrousSignals];
    float weights[kAtrousSignals];
    float varianceSum[kAtrousSignals];
    [unroll]
    for (uint s = 0u; s < kAtrousSignals; ++s)
    {
        sum[s] = 0.0f;
        weights[s] = 0.0f;
        varianceSum[s] = 0.0f;
    }
    if (filtered[0] || filtered[1] || filtered[2] || filtered[3])
    {
        [unroll]
        for (int y = -2; y <= 2; ++y)
            [unroll]
            for (int x = -2; x <= 2; ++x)
            {
                int2 p = int2(id.xy) + int2(x, y) * int(stepSize);
                if (any(p < 0) || any(p >= int2(frameSize)))
                    continue;
                float zq = linearDepthNow[p];
                if (zq <= 0.0f)
                    continue;
                // The kernel, the normal and the depth: alike for every signal.
                float geometry = kernel[abs(x)] * kernel[abs(y)] * Pow64(saturate(dot(normalSmooth.xyz, normalNow[p].xyz)));
                float depthTerm = abs(zp - zq) * depthRate;
                [unroll]
                for (uint s = 0u; s < kAtrousSignals; ++s)
                {
                    if (!filtered[s])
                        continue;
                    float4 sample = ReadSignal(s, p);
                    float lq = SignalLuminance(s, sample);
                    float w = geometry * exp2(-(depthTerm + abs(lp[s] - lq) * lumaRate[s]));
                    sum[s] += (IsColour(s) ? sample.rgb : float3(sample.x, 0.0f, 0.0f)) * w;
                    varianceSum[s] += sample.w * sample.w * w * w;
                    weights[s] += w;
                }
            }
    }
    [unroll]
    for (uint s = 0u; s < kAtrousSignals; ++s)
    {
        if ((signalKind & (1u << s)) == 0u)
            continue;
        if (filtered[s] && weights[s] > 0.0f)
            WriteSignal(s, id.xy, float4(sum[s] / weights[s], sqrt(varianceSum[s]) / weights[s]), zp);
        else
            WriteSignal(s, id.xy, centre[s], zp);
    }
}
