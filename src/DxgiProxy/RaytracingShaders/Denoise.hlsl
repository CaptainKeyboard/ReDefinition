// ReDefinition's own denoiser for the raytraced signals, after SVGF (Schied et al.
// 2017, "Spatiotemporal Variance-Guided Filtering"): accumulation over frames along
// the reprojected history, a variance estimate from the accumulated moments, and an
// à-trous wavelet filter whose weights stop at depth, normal and luminance edges.
//
// Each signal is filtered on its own; the passes share the frame's constants with the
// raytracing passes and take their per-dispatch values as root constants.

#include "RtCommon.hlsli"

cbuffer Step : register(b1)
{
    uint stepSize;      // the à-trous step, 1, 2, 4...
    uint signalKind;    // 0 a scalar in x, 1 a colour in rgb
    uint stepFlags;     // 1: the history is not to be used; 2: motion vectors are bound
    uint iteration;
};

// Bindings of the denoiser's own root signature (Raytracing.cpp, BuildDenoiser).
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
// u2, u3 in space 3 are the Ray Reconstruction guides' (Reconstruct.hlsl).

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
    outputB[id.xy] = float4(SurfaceNormal(id.xy, position), HAS_GBUFFER ? gbuffer1[id.xy].a : 0.0f);
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
        // Smooth surfaces keep a shorter history, as their reflections move with the view.
        float smooth = signalKind == 1u ? normalNow[id.xy].w : 0.0f;
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

// Variance: from the moments where the history is long enough, else from the
// neighbourhood's colours. Writes the colour with the variance in w (A).
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
                float w = DepthWeight(z, zq, 3.0f) * pow(saturate(dot(n, normalNow[p].xyz)), 32.0f);
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
    outputA[id.xy] = float4(signalKind == 0u ? float3(colour.x, 0.0f, 0.0f) : colour.rgb, variance);
}

// One à-trous step at stepSize: the colour and its variance (A) filtered by a 5x5 B3
// spline kernel, each tap weighed by how alike its depth, normal and luminance are.
[numthreads(8, 8, 1)]
void DenoiseAtrous(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float4 centre = inputA[id.xy];
    float zp = linearDepthNow[id.xy];
    if (zp <= 0.0f)
    {
        outputA[id.xy] = centre;
        return;
    }
    float4 normalSmooth = normalNow[id.xy];
    // Smooth reflections keep their detail: only the first two steps filter them.
    if (signalKind == 1u && normalSmooth.w > 0.8f && iteration >= 2u)
    {
        outputA[id.xy] = centre;
        return;
    }

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
            variance += inputA[p].w * k;
            blur += k;
        }
    variance /= blur;

    const float kernel[3] = { 3.0f / 8.0f, 1.0f / 4.0f, 1.0f / 16.0f };
    float lp = signalKind == 0u ? centre.x : dot(centre.rgb, float3(0.2126f, 0.7152f, 0.0722f));
    float lumaScale = 4.0f * sqrt(max(variance, 0.0f)) + 1e-4f;
    float4 sum = 0.0f;
    float weights = 0.0f;
    float varianceSum = 0.0f;
    for (int y = -2; y <= 2; ++y)
        for (int x = -2; x <= 2; ++x)
        {
            int2 p = int2(id.xy) + int2(x, y) * int(stepSize);
            if (any(p < 0) || any(p >= int2(frameSize)))
                continue;
            float zq = linearDepthNow[p];
            if (zq <= 0.0f)
                continue;
            float4 sample = inputA[p];
            float lq = signalKind == 0u ? sample.x : dot(sample.rgb, float3(0.2126f, 0.7152f, 0.0722f));
            float w = kernel[abs(x)] * kernel[abs(y)];
            w *= DepthWeight(zp, zq, float(stepSize));
            w *= pow(saturate(dot(normalSmooth.xyz, normalNow[p].xyz)), 64.0f);
            w *= exp(-abs(lp - lq) / lumaScale);
            sum.rgb += sample.rgb * w;
            varianceSum += sample.w * w * w;
            weights += w;
        }
    if (weights <= 0.0f)
    {
        outputA[id.xy] = centre;
        return;
    }
    outputA[id.xy] = float4(sum.rgb / weights, varianceSum / (weights * weights));
}

// The filtered signal into the texture Unity reads: a scalar in x, a colour in rgb.
[numthreads(8, 8, 1)]
void DenoiseResolve(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float4 value = inputA[id.xy];
    outputA[id.xy] = signalKind == 0u ? float4(value.x, 0.0f, 0.0f, 1.0f) : float4(value.rgb, 1.0f);
}
