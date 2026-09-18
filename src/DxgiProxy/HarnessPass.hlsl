// The harness's compute pass for Direct3D 12 for mods, compiled to DXIL by the build
// (CMakeLists.txt) where the Windows SDK's dxc is found; ProxyHarness.cpp compiles the
// same source to DXBC itself.
Texture2D<float4> input : register(t0);
RWTexture2D<float4> output : register(u0);
cbuffer Constants : register(b0) { float4 add; };

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) { output[id.xy] = input[id.xy] * 2.0 + add; }
