// The own cloud system's layer core (package T01): a planet-wide shell of up to four stacked cloud layers marched as one ray.
//
// Geometry: the planet's centre is _PlanetCenter (world space); a layer is the shell between two altitudes above sea level (the radius
// _PlanetR plus the altitude), seen from below, inside or above it. Each layer gives up to two spans along the ray (the near stretch and,
// where the ray enters the hollow inner sphere and the body has no surface, the far wall: the gas-giant case); the spans of all layers
// are sorted front to back and marched in turn with one transmittance, so two layers stack as 1 - (1 - a1)(1 - a2). The march ends at the
// scene's depth (the composite is depth limited) and at the horizon sphere of a body with a surface.
//
// Density at a point of a layer: the layer's extinction times the coverage shape (a threshold on the coverage map, soft over
// _LayerB[i].z) times the type table's profile, where the type value of the type map picks two neighbouring types of the layer's table
// and interpolates their base, top and profile. The maps are equirectangular, row 0 at the north pole, column 0 at longitude -180,
// read in the map frame (_WorldToMap: x towards longitude 0, y towards 90 east, z north) turned by the layer's longitude shift.
// The shape noise moves the coverage threshold and the detail erodes the edge (ReDefinitionAtmospheresNoise.cginc, package T02). The light is
// ReDefinitionAtmospheresCloudLight.cginc (package T03): dual-lobe phase, octave multiple scattering, powder, light march with cone taps,
// ambient, extra lights, the light cache.
//
// Techniques (no code taken): the sphere-shell ray intervals and the type-by-height table follow the common shell cloud renderers (Schneider,
// "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015; Hillaire, "Physically Based Sky, Atmosphere and Cloud
// Rendering in Frostbite", 2016); the energy-conserving integration of a step is Hillaire's (section 5.6.3).
//
// Cost (package T01c): the leap over empty samples with the step back to the grid point after the last empty one follows Schneider's
// cheap-sample march (SIGGRAPH 2015, same reference); the coverage bound lets the noise run only where the map can reach the threshold.
//
// Passes: 0 march into a premultiplied (radiance, alpha) target, 1 depth-aware composite over the camera target, 2 aux output
// (alpha, tau, steps, first hit distance) for the parity checks and the aid pictures, 3 light cache fill (T03), 4 the final composite with the
// display stage, 5 the culling's tile map (T16), 6 the march with the cloud's distance in a second target (recon.trueDistance), 7 the march
// drawn as one procedural triangle (recon.marchTriangle). The light volume's fill (pass 8) and blur (pass 9) were removed with the volume (master plan 44 steps 2 and 5).
//
// The code is ReDefinitionAtmospheresLayersCore.cginc (master plan 45.3).
Shader "ReDefinition/AtmospheresLayers"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
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

        // Pass 8 (master plan 46.1 part 4, OwnCloudSkyProbe): the march of pass 7 through a face of the own sky probe's cube (the face's basis, no scene depth, no
        // reconstruction: a MaterialPropertyBlock), with the display stage, over the face (KSP's probe's face copied there first): what the sea and the land mirror.
        Pass
        {
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vertProc
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_local __ CLOUD_DEV
            float4 frag(v2f i) : SV_Target { return SkyProbeFace(i); }
            ENDCG
        }
    }
}
