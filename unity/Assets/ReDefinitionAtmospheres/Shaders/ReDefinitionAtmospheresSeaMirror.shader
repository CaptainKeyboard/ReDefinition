// The sea mirrors the own clouds (master plan 46.1 part 4 stage D, PRUEFPROTOKOLL 321-322; OwnCloudSeaMirror.cs). Scatterer 0.908's ocean takes its mirror image
// from Deferred's screen-space reflections (the global ssrOutput: Lsky = lerp(Lsky, Fresnel x ssr.rgb, mask x ssr.a), read in the shipped shader's dump); the own
// clouds write no depth, so a ray into them misses. This pass makes a copy of ssrOutput in which every ocean pixel SSR missed holds the own sky probe's cube
// along the pixel's reflected ray (the own clouds over KSP's scene, in the display stage of the screen), and the copy is set as ssrOutput for the ocean's shading.
//
// Inputs, all globals of the frame: ssrOutput (Deferred, event BeforeImageEffectsOpaque), oceanGbufferDepth and oceanGbufferNormalsAndSigma (Scatterer's ocean
// G-buffer, event AfterForwardOpaque: the normal's xy as x 2 - 1, z from the unit length with the sign of w, the slope variance z x _VarianceMax.x),
// _VarianceMax (Scatterer, global). Draw: Blit into a temporary at half width (Deferred's own size), Cull / ZWrite off.
Shader "ReDefinition/AtmospheresSeaMirror"
{
    Properties
    {
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            sampler2D ssrOutput;
            sampler2D oceanGbufferDepth;
            sampler2D oceanGbufferNormalsAndSigma;
            float4 _VarianceMax;
            samplerCUBE _SmCube;
            float4 _SmForward;  // xyz: the camera's forward (world)
            float4 _SmRight;    // xyz: its right times tan(half fov) x aspect
            float4 _SmUp;       // xyz: its up times tan(half fov)
            float4 _SmZenith;   // xyz: the local up at the camera (world)
            float4 _SmP;        // x: the ocean depth's clear value (no ocean), y: the cube's last mip

            float4 frag(v2f_img i) : SV_Target
            {
                float4 ssr = tex2Dlod(ssrOutput, float4(i.uv, 0.0, 0.0));
                float d = tex2Dlod(oceanGbufferDepth, float4(i.uv, 0.0, 0.0)).x;
                if (abs(d - _SmP.x) < 1e-7) return ssr;
                float4 ns = tex2Dlod(oceanGbufferNormalsAndSigma, float4(i.uv, 0.0, 0.0));
                float2 nxy = ns.xy * 2.0 - 1.0;
                float nz = sqrt(1.0 - min(dot(nxy, nxy), 1.0));
                float3 n = normalize(float3(nxy, ns.w > 0.0 ? nz : -nz));
                float3 v = normalize(_SmForward.xyz + (i.uv.x * 2.0 - 1.0) * _SmRight.xyz + (i.uv.y * 2.0 - 1.0) * _SmUp.xyz);
                float3 r = reflect(v, n);
                // A ray the waves send below the horizon mirrors the sky just above it (the sea's own surface is not in the cube's lower half).
                float h = dot(r, _SmZenith.xyz);
                if (h < 0.002) r = normalize(r + (0.002 - h) * _SmZenith.xyz);
                // The waves' slope spread widens the mirrored cone: about twice the slope's standard deviation, as a mip of the 256 face (163 texels a radian).
                float sigma = sqrt(max(ns.z * _VarianceMax.x, 1e-8));
                float mip = clamp(log2(max(2.0 * sigma * 163.0, 1.0)), 0.0, _SmP.y);
                float3 c = texCUBElod(_SmCube, float4(r, mip)).rgb;
                return float4(lerp(c, ssr.rgb, saturate(ssr.a)), 1.0);
            }
            ENDCG
        }
    }
}
