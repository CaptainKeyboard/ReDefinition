// Water drops on the windows of a cabin (IVA) and the visor of a kerbal (package T08, src/Atmospheres/Light/Droplets). A camera effect like
// the frost (D37): drawn after the upscaler onto the upscaled image through the host hook, or as a plain quad where the main mod is not
// there. Three ways to draw one look:
//   pass 0  host: the drops onto the upscaled image, each one a small lens that shows the picture upside down and compressed (a real drop
//           is a short-focus lens), with a dark rim, a bright spot of the light and a wet trail behind a drop that has run.
//   pass 1  host: the copy (a bilinear halving of the image) the lenses read.
//   pass 2  fallback quad (Graphics.DrawMesh at queue 3500 on the flight camera): a plain alpha overlay of rims and highlights, no lens.
//   pass 3  aid map: R = share of the drop discs, G = share of the wet trails, B = share of the small beads (the weather player's second
//           view; the in-game contribution mask is atmoDebug = contrib).
//
// The population is a pure function of the window's wetness, the clock and the sliding velocity, built from a hash lattice (no list of
// drops, no state on the GPU). Two layers: large drops that stick and run (a lattice aligned with the way they run; each lattice cell
// holds at most one drop, on a cycle of its own: stuck, a quick run, stuck, and a new drop forms when the cycle restarts) and small
// beads that stay. The cells are laid out along the sliding direction so that a drop's run and its trail stay inside its cell. The
// share of cells that hold a drop follows the wetness (wet^0.85 times the configured coverage over the design coverage of 0.35).
//
// Technique reference: the lattice-of-drops idea with a stick-and-slip cycle and a trail is the common way to draw rain on glass in a
// pixel shader; the arithmetic below is written for this mod (hash, cycle, lens and rim are its own).
//
// Motion vectors: none, the overlay is fixed to the screen. Depth: none. Draw order: after the upscaler and before TUFX's effects; in the
// HUD-less copy. Exposure: colours are relative to the image's own; the quad uses a colour in scene units from the CPU.
Shader "ReDefinition/AtmospheresDroplet"
{
    Properties
    {
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    #ifndef REDEFINITION_ATMO_CONTRIBUTION_INCLUDED
    #define REDEFINITION_ATMO_CONTRIBUTION_INCLUDED
    float _AtmoDebug;
    inline bool AtmoContribOn() { return _AtmoDebug > 0.5 && _AtmoDebug < 1.5; }
    inline float4 AtmoContribOutput(float4 normalColour, float contribution, int featureId)
    {
        if (AtmoContribOn())
        {
            float c = saturate(contribution);
            return float4(float3(1.0, 1.0, 1.0) * c, c);
        }
        return normalColour;
    }
    #endif
    #define ATMO_FEATURE_DROPLETS 9

    sampler2D _MainTex;
    sampler2D _DrpCopy;
    float4 _DrpCopy_TexelSize;
    sampler2D _DrpMask;

    // x = wetness 0..1, y = the clock in seconds, z = aspect (width / height), w = 1 where the target is upside down against the picture.
    float4 _DrpA;
    // xy = the sliding velocity in picture heights per second (x right, y up), z = lens strength, w = radius of a large drop in picture heights.
    float4 _DrpB;
    // x = 1 where the interior mask is bound, y = opacity of the quad, z = light 0..1, w = configured coverage at full wetness.
    float4 _DrpC;
    // rgb = colour of the rims and highlights for the quad (scene units).
    float4 _DrpQuad;

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

    v2f vertQuad(appdata v)
    {
        v2f o;
        o.uv = v.uv;
        o.pos = float4((v.uv * 2 - 1) * float2(1, _ProjectionParams.x), UNITY_NEAR_CLIP_VALUE, 1);
        return o;
    }

    float3 Safe(float3 c) { return max(min(c, 64.0), 0.0); }

    float2 Hash22(float2 p)
    {
        float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
        p3 += dot(p3, p3.yzx + 33.33);
        return frac((p3.xx + p3.yz) * p3.zy);
    }

    float Hash12(float2 p)
    {
        float3 p3 = frac(float3(p.xyx) * 0.1031);
        p3 += dot(p3, p3.yzx + 33.33);
        return frac((p3.x + p3.y) * p3.z);
    }

    // What the drops do at a point of the picture (picture heights, y up): the disc's weight, the wet trail's, the bead's, the lens
    // position (the point of the drop in units of its radius, xy) and the drop's rim darkening and highlight.
    struct Drops
    {
        float disc, trail, bead, rim, spot;
        float2 lens;     // (pixel - centre) / radius in the picture plane
        float radius;    // in picture heights
    };

    // design coverage of the two layers at full wetness: big drops 0.18, beads 0.17
    #define DESIGN_COVER 0.35

    Drops Evaluate(float2 p)
    {
        Drops d;
        d.disc = 0; d.trail = 0; d.bead = 0; d.rim = 0; d.spot = 0; d.lens = float2(0, 0); d.radius = _DrpB.w;
        float wet = saturate(_DrpA.x);
        if (wet <= 0.0) return d;
        float present = saturate(pow(wet, 0.85) * _DrpC.w / DESIGN_COVER);

        float2 v = _DrpB.xy;
        float speed = length(v);
        float2 dir = speed > 1e-4 ? v / speed : float2(0, -1);
        float2 perp = float2(-dir.y, dir.x);
        float s = dot(p, dir), c = dot(p, perp);
        float t = _DrpA.y;

        // ---- the large drops
        {
            float r = _DrpB.w;
            float cell = 3.2 * r;
            float2 q = float2(s, c) / cell;
            float2 id = floor(q);
            float2 f = frac(q);
            float2 h = Hash22(id);
            float2 h2 = Hash22(id + 31.7);
            if (h.x < present)
            {
                // The cycle: stuck, a quick run over 40 % of it, stuck. A new drop forms when it restarts (the radius grows in).
                float travel = 0.36;
                float eff = max(speed, 0.004) * (0.6 + 0.8 * h2.x);
                float period = travel * cell / eff * 2.5;
                float phase = frac(t / period + h2.y);
                float run = smoothstep(0.5, 0.9, phase);
                float born = smoothstep(0.0, 0.12, phase);
                float2 centre = float2(0.32 + travel * run, 0.32 + 0.36 * h.y);
                float rad = r * (0.5 + 0.5 * h2.y) / cell * born;
                float2 dd = (f - centre) / max(rad, 1e-4);
                // Elongated along the run a little.
                dd.x /= 1.12;
                float qd = length(dd);
                d.disc = 1.0 - smoothstep(0.86, 1.0, qd);
                float2 lensLocal = (f - centre) / max(rad, 1e-4);
                d.lens = dir * lensLocal.x + perp * lensLocal.y;
                d.rim = smoothstep(0.55, 1.0, qd) * d.disc;
                d.spot = exp(-dot(dd - float2(-0.35, 0.35), dd - float2(-0.35, 0.35)) * 9.0) * step(qd, 1.0);
                d.radius = rad * cell;
                // The wet trail: from where the drop started this cycle to where it is, narrow.
                float startS = 0.32;
                float along = f.x;
                float inTrail = step(startS, along) * step(along, centre.x - rad * 0.8);
                float across = abs(f.y - centre.y) / max(rad * 0.28, 1e-4);
                d.trail = inTrail * (1.0 - smoothstep(0.6, 1.0, across)) * run * (1.0 - smoothstep(0.93, 1.0, phase)) * 0.9;
            }
        }
        // ---- the beads: small, many, they stay
        {
            float cell = 1.45 * _DrpB.w;
            float2 q = float2(s, c) / cell + 53.0;
            float2 id = floor(q);
            float2 f = frac(q);
            float2 h = Hash22(id + 7.1);
            float2 h2 = Hash22(id + 91.3);
            if (h.x < present)
            {
                float2 centre = float2(0.3 + 0.4 * h.y, 0.3 + 0.4 * h2.x);
                float rad = 0.3 * (0.6 + 0.4 * h2.y);
                float qd = length((f - centre) / rad);
                float bead = 1.0 - smoothstep(0.8, 1.0, qd);
                // A bead that sits on a big drop's disc is part of it.
                d.bead = bead * (1.0 - d.disc);
                if (d.disc < 0.01 && bead > 0.0)
                {
                    float2 lensLocal = (f - centre) / rad;
                    d.lens = dir * lensLocal.x + perp * lensLocal.y;
                    d.radius = rad * cell;
                }
            }
        }
        return d;
    }

    float2 PictureOf(float2 uv, bool flip)
    {
        float2 puv = float2(uv.x, flip ? 1.0 - uv.y : uv.y);
        return float2(puv.x * _DrpA.z, puv.y);
    }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always

        // 0: the host composite.
        Pass
        {
            Name "DropHost"
            Tags { "LightMode" = "DropHost" }
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertProc
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                bool flip = _DrpA.w > 0.5;
                float2 p = PictureOf(i.uv, flip);
                Drops d = Evaluate(p);
                float keep = 1.0;
                if (_DrpC.x > 0.5) keep = 1.0 - tex2Dlod(_DrpMask, float4(i.uv, 0, 0)).r;
                float disc = max(d.disc, d.bead * 0.9);
                float total = max(disc, d.trail * 0.35) * keep;
                clip(total - 0.003);

                // The lens: the picture seen through the drop is upside down and compressed, a point of the drop at lens (in radii from
                // its centre) shows the point mirrored through the centre and scaled out.
                float gain = 2.4 * _DrpB.z;
                float2 off = -d.lens * d.radius * gain;
                float2 offUv = float2(off.x / _DrpA.z, flip ? -off.y : off.y);
                float3 sharp = Safe(tex2D(_DrpCopy, i.uv).rgb);
                float3 seen = Safe(tex2D(_DrpCopy, i.uv + offUv).rgb);
                float3 col = seen * (1.0 - 0.45 * d.rim) * (1.0 + 0.06);
                col += _DrpC.z * (0.55 * d.spot + 0.12 * d.bead);
                // The trail is a faint wet streak: the picture a little brighter and flatter.
                float3 streak = lerp(sharp, seen, 0.4) * 0.97;
                bool isDisc = disc >= d.trail * 0.35;
                return AtmoContribOutput(float4(isDisc ? col : streak, total), total, ATMO_FEATURE_DROPLETS);
            }
            ENDCG
        }

        // 1: the copy of the image at half size.
        Pass
        {
            Name "DropCopy"
            Tags { "LightMode" = "DropCopy" }
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertProc
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                return float4(Safe(tex2D(_MainTex, i.uv).rgb), 1.0);
            }
            ENDCG
        }

        // 2: the fallback quad, in the scene, without a lens.
        Pass
        {
            Name "DropQuad"
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertQuad
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                float2 p = float2(i.uv.x * _DrpA.z, i.uv.y);
                Drops d = Evaluate(p);
                float disc = max(d.disc, d.bead * 0.9);
                float total = max(disc * (0.35 + 0.55 * d.rim), d.trail * 0.2) * _DrpC.y;
                clip(total - 0.003);
                float3 col = _DrpQuad.rgb * (0.25 + 0.75 * saturate(d.spot * 1.5 + d.rim * 0.4));
                return AtmoContribOutput(float4(col, total), total, ATMO_FEATURE_DROPLETS);
            }
            ENDCG
        }

        // 3: the aid map.
        Pass
        {
            Name "DropMap"
            Tags { "LightMode" = "DropMap" }
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertProc
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                bool flip = _DrpA.w > 0.5;
                Drops d = Evaluate(PictureOf(i.uv, flip));
                float keep = 1.0;
                if (_DrpC.x > 0.5) keep = 1.0 - tex2Dlod(_DrpMask, float4(i.uv, 0, 0)).r;
                return float4(d.disc * keep, d.trail * keep, d.bead * keep, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
