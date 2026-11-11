// The sea ice near the ground (src/Atmospheres/Ice/IceGroundOverlay.cs), behind the switch ice.groundOverlay until the series has measured
// that an opaque sheet hides the water at 5 km (RECON P10 and P11: holds at 50 m and 500 m, open at 5 km).
//
// A clipmap of flat rings is projected onto the sphere of radius body + sea height in the vertex shader. The fragment shader works out
// the ice from the direction of the pixel with the same function as src/Atmospheres/Ice/IceMask.cs (IceEval below repeats IceHash and
// IceMask operation by operation: the same constants, uint arithmetic, a 3 x 3 x 3 Worley search): the field's ice fraction and
// thickness from _IceMap (the field's 256 x 128 map), the coast from _ShoreMap (ShoreGrid), the drift offset. Open water, leads and
// the cut at the coast are DISCARD, so the real ocean (Scatterer's waves and foam, or the stock ocean) shows through unchanged: the
// water logic is never modified. Thin ice is a dither on the world-fixed lattice of the sphere (not the screen), so temporal upscalers
// see no shimmer [VERIFY under TAA, DLSS, FSR and XeSS at 5 km: the 1 m lattice is untested there]. Thin ice is therefore a stipple on an
// opaque pass: there is no blending, and no water is seen through thin ice. The sheet writes depth (queue 2504, after Scatterer's ocean at 2502) and is raised by _Lift metres along the normal.
//
// Pass 0 draws the sheet. Pass 1 (coverage) and pass 2 (hash) are for the series probe: Graphics.Blit into a float target, no camera.
// Pass 1 evaluates the ice on a window of latitude and longitude and writes (ice, lead, sea, local fraction); pass 2 evaluates the
// Worley noise and the value noise at a table of points and writes (F1, F2, cell value, noise); the integer hash is written by pass 3.
// Lighting: forward with fake lighting (Lambert with the sun and an ambient colour, low specular), fog applied. Deferred G-buffer variant
// and a MotionVectors pass are open [VERIFY in the series: DeferredPresent, UnityMotionRepair handling of custom shaders]: the sheet
// moves with the planet and the camera-only motion vectors are right for it.
Shader "ReDefinition/AtmospheresIce"
{
    Properties
    {
        _IceMap ("Field map (fraction, sqrt thickness, sqrt snow)", 2D) = "black" {}
        _ShoreMap ("Coast map", 2D) = "black" {}
        _IceColour ("Ice colour", Color) = (0.85, 0.92, 0.97, 1)
        _Roughness ("Roughness", Range(0, 1)) = 0.6
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _IceMap;
    sampler2D _ShoreMap;
    float4 _IceColour;
    float _Roughness;

    // The frame of the planet's latitude and longitude in the sphere's frame (KspFrame): (0, 0), (0, 90 east) and the north pole.
    float4 _FrameA;
    float4 _FrameB;
    float4 _FrameP;
    float4x4 _WorldToSphere;
    float4 _CenterRel;     // the body's centre minus the camera, world space
    float4 _NadirRel;      // the foot of the camera on the sea sphere minus the camera
    float4 _East;
    float4 _North;
    float4 _Sea;           // x radius + sea height, y lift (m), z wave lift (m), w unused

    float _Freq;           // cells along the radius
    float4 _Drift;         // the drift offset in cell units
    float _SeedLo;
    float _SeedHi;
    float _LeadFraction;
    float _ShoreGain;
    float _EdgeNoise;
    float _Permanent;
    float _PermanentThickness;
    float _Forced;         // a fraction that replaces the field's when it is 0 or more
    float _Aid;            // 1: flat magenta where the sheet is (the aid view)
    float4 _Fade;          // x fade start, y fade end (m from the camera), z detail range (m), w dither cells per radius
    float4 _SunDir;
    float4 _SunColour;
    float4 _AmbientColour;

    static const float ThinM = 0.02;
    static const float ThickM = 0.30;
    static const float ThinOpacity = 0.15;
    static const float ThicknessSpread = 0.25;
    static const float EdgeNoiseScale = 0.125;
    static const float MaxBandShare = 0.9;
    static const float BandWidths[11] = { 0.0, 0.01, 0.02, 0.05, 0.1, 0.15, 0.2, 0.3, 0.4, 0.5, 0.8 };
    static const float BandShares[11] = { 0.0, 0.0371, 0.0724, 0.176, 0.329, 0.4588, 0.57, 0.7435, 0.8638, 0.936, 1.0 };

    // ---- the hash (IceHash.cs) ----------------------------------------------------------------------------------------------------
    uint IceMix(uint v)
    {
        // lowbias32, Chris Wellons' hash-prospector (The Unlicense)
        v ^= v >> 16u;
        v *= 0x7feb352du;
        v ^= v >> 15u;
        v *= 0x846ca68bu;
        v ^= v >> 16u;
        return v;
    }

    uint IceCell(int3 c, uint seed)
    {
        return IceMix(IceMix(IceMix(seed + asuint(c.z)) + asuint(c.y)) + asuint(c.x));
    }

    float IceUnit(uint h)
    {
        return (h >> 8u) * (1.0 / 16777216.0);
    }

    // F1 and F2 in x and y, the nearest cell's value in z. The cell's hash is returned in cellHash.
    float3 IceWorley(float3 p, uint seed, out uint cellHash)
    {
        int3 i0 = (int3)floor(p);
        float f1 = 1e9, f2 = 1e9;
        uint nearest = 0u;
        [loop]
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int3 c = i0 + int3(dx, dy, dz);
                    uint h = IceCell(c, seed);
                    float3 jitter = 0.1 + 0.8 * float3(IceUnit(h), IceUnit(IceMix(h + 0x9E3779B9u)), IceUnit(IceMix(h + 0x7F4A7C15u)));
                    float3 q = (float3)c + jitter - p;
                    float d = sqrt(dot(q, q));
                    if (d < f1)
                    {
                        f2 = f1;
                        f1 = d;
                        nearest = h;
                    }
                    else if (d < f2)
                    {
                        f2 = d;
                    }
                }
            }
        }
        cellHash = nearest;
        return float3(f1, f2, IceUnit(IceMix(nearest ^ 0x6A09E667u)));
    }

    float IceCorner(int3 c, uint seed)
    {
        return IceUnit(IceCell(c, seed)) * 2.0 - 1.0;
    }

    float IceValueNoise(float3 p, uint seed)
    {
        float3 f = floor(p);
        int3 i0 = (int3)f;
        float3 t = p - f;
        t = t * t * (3.0 - 2.0 * t);
        float c000 = IceCorner(i0, seed), c100 = IceCorner(i0 + int3(1, 0, 0), seed);
        float c010 = IceCorner(i0 + int3(0, 1, 0), seed), c110 = IceCorner(i0 + int3(1, 1, 0), seed);
        float c001 = IceCorner(i0 + int3(0, 0, 1), seed), c101 = IceCorner(i0 + int3(1, 0, 1), seed);
        float c011 = IceCorner(i0 + int3(0, 1, 1), seed), c111 = IceCorner(i0 + int3(1, 1, 1), seed);
        float x00 = c000 + (c100 - c000) * t.x, x10 = c010 + (c110 - c010) * t.x;
        float x01 = c001 + (c101 - c001) * t.x, x11 = c011 + (c111 - c011) * t.x;
        float y0 = x00 + (x10 - x00) * t.y, y1 = x01 + (x11 - x01) * t.y;
        return y0 + (y1 - y0) * t.z;
    }

    // ---- the mask (IceMask.cs) ----------------------------------------------------------------------------------------------------
    float IceWidthFor(float share)
    {
        if (!(share > 0.0)) return 0.0;
        if (share >= BandShares[10]) return BandWidths[10];
        float width = BandWidths[10];
        [unroll]
        for (int i = 10; i >= 1; i--)
        {
            if (share <= BandShares[i])
            {
                float t = (share - BandShares[i - 1]) / (BandShares[i] - BandShares[i - 1]);
                width = BandWidths[i - 1] + (BandWidths[i] - BandWidths[i - 1]) * t;
            }
        }
        return width;
    }

    float IceOpacity(float thicknessM)
    {
        float t = saturate((thicknessM - ThinM) / (ThickM - ThinM));
        t = t * t * (3.0 - 2.0 * t);
        return ThinOpacity + (1.0 - ThinOpacity) * t;
    }

    struct IceResult
    {
        bool sea;
        bool ice;
        bool lead;
        float opacity;
        float thickness;
        float local;
        float snow;
    };

    // dir: unit direction in the sphere's frame. The game's latitude and longitude come from the frame vectors.
    IceResult IceEval(float3 dir)
    {
        IceResult r;
        r.sea = false; r.ice = false; r.lead = false; r.opacity = 0.0; r.thickness = 0.0; r.local = 0.0; r.snow = 0.0;
        float2 uvShore = float2(atan2(dir.x, dir.z) / 6.28318530718, asin(clamp(dir.y, -1.0, 1.0)) / 3.14159265359 + 0.5);
        float4 shoreTex = tex2Dlod(_ShoreMap, float4(uvShore, 0, 0));
        r.sea = shoreTex.b > 0.5;
        if (!r.sea) return r;

        float lat = asin(clamp(dot(dir, _FrameP.xyz), -1.0, 1.0));
        float lon = atan2(dot(dir, _FrameB.xyz), dot(dir, _FrameA.xyz));
        float2 uvField = float2(lon / 6.28318530718 + 0.5, lat / 3.14159265359 + 0.5);
        float4 field = tex2Dlod(_IceMap, float4(uvField, 0, 0));
        float fieldFraction = _Forced >= 0.0 ? _Forced : field.r;
        float thickness = 3.0 * field.g * field.g;
        r.snow = 0.1 * field.b * field.b;
        float shore = shoreTex.r, shoreMean = shoreTex.g;

        uint seed = ((uint)_SeedHi << 16u) | (uint)_SeedLo;
        float f = _Permanent > 0.5 ? 1.0 : saturate(fieldFraction);
        if (f <= 0.0) return r;
        if (thickness <= 0.0 && _Permanent > 0.5) thickness = _PermanentThickness;
        float3 p = dir * _Freq + _Drift.xyz;
        float local = f;
        if (f < 1.0)
        {
            float edge = 2.0 * min(f, 1.0 - f);
            float noise = IceValueNoise(p * EdgeNoiseScale + float3(7.3, 1.9, 4.1), seed ^ 0xA5A5A5A5u);
            local = f + _ShoreGain * edge * (saturate(shore) - saturate(shoreMean))
                    + _EdgeNoise * edge * (0.35 + 0.65 * (1.0 - saturate(shore))) * noise;
            local = saturate(local);
        }
        r.local = local;
        float band = min(MaxBandShare, max(0.0, _LeadFraction) * (1.0 - f));
        float width = IceWidthFor(band);
        float cellChance = min(1.0, local / max(1e-3, 1.0 - band));
        uint cellHash;
        float3 w = IceWorley(p, seed, cellHash);
        if (w.z >= cellChance) return r;
        if (w.y - w.x < width)
        {
            r.lead = true;
            return r;
        }
        r.ice = true;
        float spread = cellChance > 0.0 ? 1.0 - 2.0 * w.z / cellChance : 0.0;
        r.thickness = max(0.001, thickness * (1.0 + ThicknessSpread * spread));
        r.opacity = IceOpacity(r.thickness);
        return r;
    }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Geometry+504" "RenderType" = "Opaque" "IgnoreProjector" = "True" "ForceNoShadowCasting" = "True" }
        LOD 300

        // Pass 0: the sheet.
        Pass
        {
            Name "ICE"
            Tags { "LightMode" = "ForwardBase" }
            ZWrite On
            ZTest LEqual
            Cull Off
            Offset -1, -1

            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vertSheet
            #pragma fragment fragSheet
            #pragma multi_compile_fog

            struct SheetIn
            {
                float4 vertex : POSITION;
            };

            struct SheetV2F
            {
                float4 pos : SV_POSITION;
                float3 rel : TEXCOORD0;      // camera-relative world position
                UNITY_FOG_COORDS(1)
            };

            SheetV2F vertSheet(SheetIn v)
            {
                SheetV2F o;
                // The vertex is (x east, unused, z north) in metres from the foot of the camera, on the tangent plane; it is projected
                // onto the sea sphere along the direction from the centre, so the sheet follows the curvature.
                float3 flat = _NadirRel.xyz + _East.xyz * v.vertex.x + _North.xyz * v.vertex.z;
                float3 fromCentre = flat - _CenterRel.xyz;
                float3 dir = normalize(fromCentre);
                float3 rel = _CenterRel.xyz + dir * (_Sea.x + _Sea.y + _Sea.z);
                o.rel = rel;
                float3 world = rel + _WorldSpaceCameraPos;
                o.pos = UnityWorldToClipPos(float4(world, 1.0));
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 fragSheet(SheetV2F i) : SV_Target
            {
                float3 dirWorld = normalize(i.rel - _CenterRel.xyz);
                float3 dir = normalize(mul((float3x3)_WorldToSphere, dirWorld));
                IceResult r = IceEval(dir);
                if (!r.sea || !r.ice) discard;

                float dist = length(i.rel);
                float fade = saturate((_Fade.y - dist) / max(1.0, _Fade.y - _Fade.x));
                // The dither: a cell of the sphere-fixed lattice (about a metre), so it does not move with the camera or the jitter.
                int3 cell = (int3)floor(dir * _Fade.w);
                uint seed = ((uint)_SeedHi << 16u) | (uint)_SeedLo;
                float dither = IceUnit(IceCell(cell, seed ^ 0xBB67AE85u));
                float opacity = _Aid > 0.5 ? 1.0 : r.opacity;
                if (dither >= opacity * fade) discard;
                if (_Aid > 0.5) return fixed4(1, 0, 1, 1);

                // Look: white-blue base darker over thin ice, snow dust on top, cracks near the camera.
                float t = saturate((r.thickness - ThinM) / (ThickM * 2.0 - ThinM));
                float3 thin = float3(0.30, 0.46, 0.62);
                float3 albedo = lerp(thin, _IceColour.rgb, t);
                albedo = lerp(albedo, float3(0.96, 0.97, 0.99), saturate(r.snow / 0.05));
                float detail = saturate((_Fade.z - dist) / max(1.0, _Fade.z));
                if (detail > 0.0)
                {
                    uint dummy;
                    float3 crack = IceWorley(dir * _Freq * 60.0 + _Drift.xyz * 60.0, 0x1CE1CEu, dummy);
                    float crackLine = 1.0 - smoothstep(0.0, 0.06, crack.y - crack.x);
                    albedo *= 1.0 - 0.35 * detail * crackLine * (1.0 - saturate(r.snow / 0.05));
                }
                float3 normal = dirWorld;
                float3 viewDir = normalize(-i.rel);
                float ndl = saturate(dot(normal, normalize(_SunDir.xyz)));
                float3 halfDir = normalize(normalize(_SunDir.xyz) + viewDir);
                float gloss = lerp(60.0, 8.0, _Roughness);
                float spec = pow(saturate(dot(normal, halfDir)), gloss) * (1.0 - _Roughness) * 0.25 * ndl;
                float3 colour = albedo * (_AmbientColour.rgb + _SunColour.rgb * ndl) + spec * _SunColour.rgb;
                fixed4 result = fixed4(colour, 1.0);
                UNITY_APPLY_FOG(i.fogCoord, result);
                return result;
            }
            ENDCG
        }

        // Pass 1: the coverage of a window of latitude and longitude, for the series probe.
        Pass
        {
            Name "COVERAGE"
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert_img
            #pragma fragment fragCoverage

            float4 _Window; // latMin, latMax, lonMin, lonMax in degrees

            float4 fragCoverage(v2f_img i) : SV_Target
            {
                float lat = radians(lerp(_Window.x, _Window.y, i.uv.y));
                float lon = radians(lerp(_Window.z, _Window.w, i.uv.x));
                float c = cos(lat);
                float3 dir = _FrameA.xyz * (c * cos(lon)) + _FrameB.xyz * (c * sin(lon)) + _FrameP.xyz * sin(lat);
                IceResult r = IceEval(dir);
                return float4(r.ice ? 1.0 : 0.0, r.lead ? 1.0 : 0.0, r.sea ? 1.0 : 0.0, r.local);
            }
            ENDCG
        }

        // Pass 2: the Worley noise and the value noise at a table of points (a texture of float4 points, one row), for the hash check.
        Pass
        {
            Name "NOISE"
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert_img
            #pragma fragment fragNoise

            sampler2D _Points;
            float4 _PointsInfo; // x count, y seed

            float4 fragNoise(v2f_img i) : SV_Target
            {
                float index = floor(i.uv.x * _PointsInfo.x);
                float4 pt = tex2Dlod(_Points, float4((index + 0.5) / _PointsInfo.x, 0.5, 0, 0));
                uint cellHash;
                float3 w = IceWorley(pt.xyz, (uint)_PointsInfo.y, cellHash);
                float n = IceValueNoise(pt.xyz, 777u);
                return float4(w.x, w.y, w.z, n);
            }
            ENDCG
        }

        // Pass 3: the integer hash of a table of cells, written as two 16-bit halves (a float target holds 24 bits of an integer).
        Pass
        {
            Name "HASH"
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert_img
            #pragma fragment fragHash

            sampler2D _Points;
            float4 _PointsInfo; // x count, y seed

            float4 fragHash(v2f_img i) : SV_Target
            {
                float index = floor(i.uv.x * _PointsInfo.x);
                float4 pt = tex2Dlod(_Points, float4((index + 0.5) / _PointsInfo.x, 0.5, 0, 0));
                uint h = IceCell((int3)floor(pt.xyz), (uint)_PointsInfo.y);
                return float4((float)(h >> 16u), (float)(h & 0xFFFFu), 0, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
