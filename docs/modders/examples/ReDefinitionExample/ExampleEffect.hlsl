// The example's compute pass: the upscaled image darkened towards its edges. ReDefinition
// compiles it when the example loads (CreateComputePassFromFile), against the root
// signature every pass shares: t0-t7, u0-u7, b0, s0 and s1.

Texture2D<float4> Source : register(t0);
RWTexture2D<float4> Result : register(u0);

cbuffer Parameters : register(b0)
{
    float Strength;
    float3 Padding;
};

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint width, height;
    Result.GetDimensions(width, height);
    if (id.x >= width || id.y >= height)
        return;

    float2 fromCentre = (id.xy + 0.5) / float2(width, height) - 0.5;
    float falloff = saturate(1.0 - Strength * dot(fromCentre, fromCentre) * 4.0);
    float4 colour = Source[id.xy];
    Result[id.xy] = float4(colour.rgb * falloff, colour.a);
}
