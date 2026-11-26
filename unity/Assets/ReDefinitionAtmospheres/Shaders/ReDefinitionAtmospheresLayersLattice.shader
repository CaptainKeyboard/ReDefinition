// The tools' layer shader with the test lattice (master plan 45.3): ReDefinitionAtmospheresLayers.shader's passes over the same code
// (ReDefinitionAtmospheresLayersCore.cginc) with CLOUD_LATTICE defined: the lattice of single cloud types (cloud.field off) that the probe's
// "nofield" variant and tools/weather_metrics/cellfield.py draw. LayerRenderer switches its material to this shader while cloud.field is off, so the
// product's shader holds no lattice variant. The pass blocks below are the product shader's, word for word (LayerShaderPassesTests).
Shader "ReDefinition/AtmospheresLayersLattice"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #define CLOUD_LATTICE
        #include "ReDefinitionAtmospheresLayersCore.cginc"
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_local __ CLOUD_DEV
            float4 frag(v2f i) : SV_Target { return Pass0(i); }
            ENDCG
        }

        Pass
        {
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_local __ CLOUD_DEV
            float4 frag(v2f i) : SV_Target { return PassComposite(i); }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_local __ CLOUD_DEV
            float4 frag(v2f i) : SV_Target { return PassAux(i); }
            ENDCG
        }

        // Pass 3: fills one slice (_CacheSlice.x) of the light cache with the optical depth towards the sun at the voxel centres (LightCache.cs).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_local __ CLOUD_DEV
            float4 frag(v2f i) : SV_Target
            {
                float3 uvw = float3(i.uv, (_CacheSlice.x + 0.5) / max(_CacheSlice.y, 1.0));
                float3 p = _CacheA.xyz + uvw.x * _CacheX.xyz + uvw.y * _CacheY.xyz + uvw.z * _CacheZ.xyz;
                // The march reads the cache only at the end of its near taps (at most one sun march's near length from a sample inside a layer's shell), through the
                // trilinear fetch (one voxel diagonal): a voxel farther than both from every shell is never read and stays empty.
                int steps = _LightT.y > 0.5 ? (int)_LightT.y : (int)_MarchP.y;
                float alt = length(p) - _PlanetR;
                float reach = length(float3(length(_CacheX.xyz), length(_CacheY.xyz), length(_CacheZ.xyz)) / float3(_CacheSlice.zw, _CacheSlice.y))
                    + _Sun.w * NearShare((int)_CacheH.x, steps, max(_LightR.y, 1.0));
                bool read = false;
                [loop] for (int lj = 0; lj < MAXL; lj++)
                {
                    if (lj >= (int)_LayerCount) break;
                    float inner = _LayerA[lj].z, outer = _LayerA[lj].w;
                    // A shell with no inner radius reaches down to the surface (the ray ends on the sea-level sphere of a body with a surface, _StepP.w) or without bound.
                    float lowest = inner > 0 ? inner - reach : (_StepP.w > 0.5 ? -reach : -1e30);
                    if (alt <= outer + reach && alt >= lowest) read = true;
                }
                if (!read) return float4(0, 0, 0, 0);
                return float4(LightTau(p, _Sun.xyz, -1, max(1, (int)_LightT.z), 0.0, 0, _Sun.w, _CacheH.y), 0, 0, 0);
            }
            ENDCG
        }

        // Pass 4: the final composite over the camera target, with the display stage (_OutP; identical to pass 1 while it is off).
        Pass
        {
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_local __ CLOUD_DEV
            float4 frag(v2f i) : SV_Target { return OutputStage(PassComposite(i)); }
            ENDCG
        }

        // Pass 5 (T16): the tile map of the culling, one texel per tile of the full target (the layers each tile marches).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_local __ CLOUD_DEV
            float4 frag(v2f i) : SV_Target { return CullTilePass(i); }
            ENDCG
        }

        // Pass 6: the march of pass 0 with the cloud's distance in a second target (recon.trueDistance; LayerReconstructor draws it as a procedural triangle).
        Pass
        {
            CGPROGRAM
            #pragma vertex vertProc
            #pragma fragment frag
            #pragma target 3.5
            // The test variant of pass 0, so that the march is the same whichever pass draws it.
            #pragma multi_compile_local __ CLOUD_DEV
            MarchTargets frag(v2f i) { return Pass0Targets(i); }
            ENDCG
        }

        // Pass 7 (recon.marchTriangle): pass 0 drawn as pass 6's procedural triangle into one target. The target is bit-identical to the Blit's (HIST2 marchcheck,
        // 2026-11-20: twelve frames on three captures, no differing texel) and the march 1.7-2.3 ms (5-7 %) cheaper at 1707 x 960 on the A4500.
        Pass
        {
            CGPROGRAM
            #pragma vertex vertProc
            #pragma fragment frag
            #pragma target 3.5
            // The test variant of pass 0, so that the march is the same whichever pass draws it.
            #pragma multi_compile_local __ CLOUD_DEV
            float4 frag(v2f i) : SV_Target { return Pass0(i); }
            ENDCG
        }
    }
}
