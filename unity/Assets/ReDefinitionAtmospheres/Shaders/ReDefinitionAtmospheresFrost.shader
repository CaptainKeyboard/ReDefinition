// The camera frost of ReDefinition Atmospheres: frost growing in from the edges of the picture, flakes of snow stuck to the glass and
// drops sliding down it. One look, three ways to draw it:
//   pass 0  host: after the upscaler, onto the upscaled image. Refracts and scatters the image through a half-resolution copy of it.
//   pass 1  host: the copy (a bilinear halving of the image).
//   pass 2  fallback quad (Graphics.DrawMesh at queue 3500 on the flight camera, used without the main mod): a plain alpha overlay in
//           scene units, no refraction.
//   pass 3  the interior mask: where the internal camera (IVA) drew geometry, from its depth buffer; the host pass leaves those pixels
//           alone, so that only the windows are frosted.
//
// The frost shape is FrostShape.Alpha of the mod (function FrostAlpha below is the same arithmetic; the unit tests run the C# one against
// the atlas bytes, the weather player compares the coverage of this shader with it). The atlas is painted in C# (FrostAtlas): R frost
// mask, G flake cells (4 x 4), B and A relief normal.
//
// Motion vectors: none, the overlay is fixed to the screen. Depth: none. Draw order: after the upscaler and before TUFX's effects; in
// the HUD-less copy. Exposure: the colours are relative to the image's own (the image is exposed already), the quad fallback uses a
// colour in scene units from the CPU.
Shader "ReDefinition/AtmospheresFrost"
{
    Properties
    {
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    // ---- The contribution mask (atmoDebug = contrib) of unity/Assets/ReDefinitionAtmospheres/Include/AtmoContribution.cginc. The
    // weather player carries its shaders in Resources, where that include has no path both projects resolve, so the few lines are
    // repeated here under the include's own guard.
    #ifndef REDEFINITION_ATMO_CONTRIBUTION_INCLUDED
    #define REDEFINITION_ATMO_CONTRIBUTION_INCLUDED
    float _AtmoDebug;
    #define ATMO_FEATURE_FROST         4
    inline bool AtmoContribOn() { return _AtmoDebug > 0.5 && _AtmoDebug < 1.5; }
    inline float3 AtmoFeatureColour(int featureId)
    {
        if (featureId == 1) return float3(1.0, 0.0, 0.0);
        if (featureId == 2) return float3(0.0, 1.0, 0.0);
        if (featureId == 3) return float3(0.0, 0.0, 1.0);
        if (featureId == 4) return float3(1.0, 1.0, 0.0);
        if (featureId == 5) return float3(1.0, 0.0, 1.0);
        if (featureId == 6) return float3(0.0, 1.0, 1.0);
        if (featureId == 7) return float3(1.0, 0.5, 0.0);
        if (featureId == 8) return float3(0.8, 0.8, 0.8);
        return float3(1.0, 1.0, 1.0);
    }
    inline float4 AtmoContribOutput(float4 normalColour, float contribution, int featureId)
    {
        if (AtmoContribOn())
        {
            float c = saturate(contribution);
            return float4(AtmoFeatureColour(featureId) * c, c);
        }
        return normalColour;
    }
    #endif

    sampler2D _MainTex;
    sampler2D _FrostAtlas;
    sampler2D _FrostCopy;
    float4 _FrostCopy_TexelSize;
    sampler2D _FrostMask;

    // x = reach of the rim frost (share of the half-diagonal), y = exposure, z = aspect (width / height), w = unused.
    float4 _FrostA;
    // x = edge raggedness, y = radius of the clear centre, z = opacity, w = tiles of the atlas per picture height.
    float4 _FrostB;
    // x = refraction (uv per unit of relief normal), y = scatter blur in texels of the copy, z = whitening, w = light 0..1.
    float4 _FrostC;
    // x = flake count, y = drop count, z = drop lens gain, w = 1 where the target is upside down against the picture.
    float4 _FrostD;
    // x = 1 where the interior mask is bound, y = opacity of the fallback quad.
    float4 _FrostE;
    // rgb = colour of the scattered light, relative to the image.
    float4 _FrostTint;
    // xy = the direction down the picture in the picture plane (x right, y up, unit).
    float4 _FrostGravity;
    // rgb = the frost colour in scene units (fallback quad).
    float4 _FrostQuad;
    // x, y = place (picture units), z = radius in picture heights, w = variant + age (0..1).
    float4 _FrostFlakes[40];
    // x, y = head, z = radius in picture heights, w = length of the wet trail in picture heights.
    float4 _FrostDrops[24];
    // The picture cut into 32 x 18 tiles (row by row, row 0 at the bottom of the picture): x, y = the bits of the flakes that touch the
    // tile (0..19, 20..39), z = the bits of the drops (FrostTiles).
    float4 _FrostTiles[576];

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2f vertProc(uint vid : SV_VertexID)
    {
        v2f o;
        float2 p = float2((vid << 1) & 2, vid & 2);
        o.uv = p;
        o.pos = float4(p * 2 - 1, UNITY_NEAR_CLIP_VALUE, 1);
        return o;
    }

    struct appdata
    {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
    };

    // The quad of the fallback: its mesh holds the corners (uv 0 and 1), the vertex shader puts them on the screen's, flipped as the
    // camera's target is.
    v2f vertQuad(appdata v)
    {
        v2f o;
        o.uv = v.uv;
        o.pos = float4((v.uv * 2 - 1) * float2(1, _ProjectionParams.x), UNITY_NEAR_CLIP_VALUE, 1);
        return o;
    }

    float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

    // The rim frost at a point of the picture: the same arithmetic as FrostShape.Alpha. nrm is the relief normal there.
    float FrostAlpha(float2 uv, out float2 nrm)
    {
        nrm = float2(0, 0);
        float g = _FrostA.x;
        if (g <= 0.0005) return 0;
        float aspect = _FrostA.z;
        float2 p = (uv - 0.5) * float2(aspect, 1.0);
        float hd = 0.5 * sqrt(aspect * aspect + 1.0);
        float r = length(p) / hd;
        float4 at = tex2Dlod(_FrostAtlas, float4(p * _FrostB.w + 0.5, 0, 0));
        nrm = at.ba * 2.0 - 1.0;
        float jag = _FrostB.x * (0.4 + g);
        float thr = (r - (1.0 - g)) + jag * (at.r - 0.5) * 2.0;
        float geo = smoothstep(0.0, 0.05, thr) * smoothstep(_FrostB.y, _FrostB.y + 0.12, r);
        float relief = saturate(length(nrm) * 1.6);
        float thick = 0.62 + 0.38 * saturate(_FrostA.y);
        return geo * _FrostB.z * thick * (0.6 + 0.4 * relief);
    }

    // The stuck flakes at a point: their opacity and the offset (in uv) their lens gives the picture behind. tile is the point's entry of
    // _FrostTiles: bit k of x (flakes 0..19) and y (flakes 20..39) says that flake k touches the tile; the other flakes are skipped.
    void Flakes(float2 uv, float4 tile, out float a, out float2 lens)
    {
        a = 0;
        lens = float2(0, 0);
        uint lo = (uint)tile.x, hi = (uint)tile.y;
        if ((lo | hi) == 0u) return;
        float aspect = _FrostA.z;
        [loop]
        for (int i = 0; i < 40; i++)
        {
            uint m = i < 20 ? lo : hi;
            if (((m >> (uint)(i % 20)) & 1u) == 0u) continue;
            float4 f = _FrostFlakes[i];
            float2 d = (uv - f.xy) * float2(aspect, 1.0) / f.z;
            if (dot(d, d) > 1.0) continue;
            float variant = floor(f.w);
            float age = f.w - variant;
            float2 cell = float2(fmod(variant, 4.0), floor(variant / 4.0));
            float prof = tex2Dlod(_FrostAtlas, float4((cell + d * 0.5 + 0.5) / 4.0, 0, 0)).g * (1.0 - age * age);
            a = max(a, prof);
            lens += -d * prof * f.z * float2(0.8 / aspect, 0.8);
        }
    }

    // The drops at a point: the head's opacity, the share of frost they wipe, the wet trail's opacity and the lens offset (in uv).
    void Drops(float2 uv, float4 tile, out float head, out float wipe, out float trail, out float2 lens)
    {
        head = 0;
        wipe = 0;
        trail = 0;
        lens = float2(0, 0);
        uint bits = (uint)tile.z;
        if (bits == 0u) return;
        float aspect = _FrostA.z;
        float2 g = _FrostGravity.xy;
        float2 gp = float2(-g.y, g.x);
        [loop]
        for (int i = 0; i < 24; i++)
        {
            if (((bits >> (uint)i) & 1u) == 0u) continue;
            float4 dr = _FrostDrops[i];
            float2 q = (uv - dr.xy) * float2(aspect, 1.0);
            float reach = dr.w + dr.z * 2.0;
            if (dot(q, q) > reach * reach) continue;
            float along = dot(q, g);
            float perp = dot(q, gp);
            float rr = (perp * perp) / (dr.z * dr.z) + (along * along) / (dr.z * dr.z * 1.44);
            float h = 1.0 - smoothstep(0.55, 1.0, rr);
            float t = dr.w > 1e-4 ? saturate(-along / dr.w) : 1.0;
            float tr = 0;
            if (along < 0.0 && t < 1.0)
                tr = (1.0 - smoothstep(0.0, 1.0, abs(perp) / (dr.z * (0.55 - 0.3 * t)))) * (1.0 - t);
            head = max(head, h);
            wipe = max(wipe, max(h, tr));
            trail = max(trail, tr * 0.35);
            lens += -q * h * 0.7 * _FrostD.z * float2(1.0 / aspect, 1.0);
        }
    }

    // All layers at a point of the picture (puv: picture orientation, y up).
    void Layers(float2 puv, out float frost, out float flake, out float dropHead, out float trail, out float2 offset)
    {
        float2 nrm;
        frost = FrostAlpha(puv, nrm);
        float2 lensF, lensD;
        float wipe;
        // The tile lists are written only while there is a flake or a drop on the glass.
        float4 tile = float4(0, 0, 0, 0);
        if (_FrostD.x + _FrostD.y > 0.5)
        {
            int2 cell = (int2)clamp(floor(puv * float2(32.0, 18.0)), float2(0.0, 0.0), float2(31.0, 17.0));
            tile = _FrostTiles[cell.y * 32 + cell.x];
        }
        Flakes(puv, tile, flake, lensF);
        Drops(puv, tile, dropHead, wipe, trail, lensD);
        frost *= 1.0 - saturate(wipe);
        offset = nrm * _FrostC.x * frost + lensF + lensD;
    }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always

        // 0: host. Refracts and scatters the half-resolution copy of the image and blends over the image.
        Pass
        {
            Name "FrostHost"
            Tags { "LightMode" = "FrostHost" }
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertProc
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                bool flip = _FrostD.w > 0.5;
                float2 puv = float2(i.uv.x, flip ? 1.0 - i.uv.y : i.uv.y);
                float frost, flake, head, trail;
                float2 offset;
                Layers(puv, frost, flake, head, trail, offset);
                float keep = 1.0;
                if (_FrostE.x > 0.5) keep = 1.0 - tex2Dlod(_FrostMask, float4(i.uv, 0, 0)).r;
                float total = max(frost, max(flake * 0.9, max(head * 0.85, trail))) * keep;
                clip(total - 0.003);

                if (flip) offset.y = -offset.y;
                float2 su = i.uv + offset;
                float3 sharp = min(tex2D(_FrostCopy, su).rgb, 64.0);
                float2 texel = _FrostCopy_TexelSize.xy * _FrostC.y * (0.25 + frost + 0.5 * (flake + head));
                float3 blur = sharp;
                blur += min(tex2D(_FrostCopy, su + float2(texel.x, 0)).rgb, 64.0);
                blur += min(tex2D(_FrostCopy, su - float2(texel.x, 0)).rgb, 64.0);
                blur += min(tex2D(_FrostCopy, su + float2(0, texel.y)).rgb, 64.0);
                blur += min(tex2D(_FrostCopy, su - float2(0, texel.y)).rgb, 64.0);
                blur *= 0.2;
                float lum = Luma(blur);
                float3 col = lerp(sharp, blur, saturate(frost * 1.4));
                col = lerp(col, _FrostTint.rgb * (lum * 1.15 + 0.01 * _FrostC.w), _FrostC.z * frost);
                col = lerp(col, _FrostTint.rgb * (lum * 1.3 + 0.01 * _FrostC.w), flake * 0.5);
                return AtmoContribOutput(float4(col, total), total, ATMO_FEATURE_FROST);
            }
            ENDCG
        }

        // 1: the copy of the image at half size.
        Pass
        {
            Name "FrostCopy"
            Tags { "LightMode" = "FrostCopy" }
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertProc
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                return float4(min(tex2D(_MainTex, i.uv).rgb, 64.0), 1.0);
            }
            ENDCG
        }

        // 2: the fallback quad, in the scene, without refraction.
        Pass
        {
            Name "FrostQuad"
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertQuad
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                float frost, flake, head, trail;
                float2 offset;
                Layers(i.uv, frost, flake, head, trail, offset);
                float total = max(frost, max(flake * 0.9, max(head * 0.35, trail))) * _FrostE.y;
                clip(total - 0.003);
                float3 col = _FrostQuad.rgb;
                return AtmoContribOutput(float4(col, total), total, ATMO_FEATURE_FROST);
            }
            ENDCG
        }

        // 3: the interior mask from the internal camera's depth buffer: 1 where something was drawn.
        Pass
        {
            Name "FrostInterior"
            Tags { "LightMode" = "FrostInterior" }
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertProc
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                float d = tex2D(_MainTex, i.uv).r;
                #if defined(UNITY_REVERSED_Z)
                float covered = d > 0.00001 ? 1.0 : 0.0;
                #else
                float covered = d < 0.99999 ? 1.0 : 0.0;
                #endif
                return float4(covered, 0, 0, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
