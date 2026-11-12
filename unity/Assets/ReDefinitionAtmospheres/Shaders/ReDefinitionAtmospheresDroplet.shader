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
// drops, no state on the GPU). Three layers: runners, large drops that stick and run (a lattice aligned with the way they run; each
// lattice cell holds at most one drop, on a cycle of its own: stuck, a quick run, stuck, and a new drop forms when the cycle restarts),
// middle drops that stay, and small beads that stay. The runners' cells are long along the sliding direction so that a drop's run and its
// trail stay inside its cell. The share of cells that hold a drop follows the wetness (wet^0.85 times the configured coverage over the
// design coverage of the layers, DESIGN_COVER).
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
    // x = darkening of a drop's rim, y = opacity of the wet trails.
    float4 _DrpD;
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

    // The integer hash PCG (technique: M. Jarzynski and M. Olano, "Hash Functions for GPU Rendering", Journal of Computer Graphics
    // Techniques 9(3), 2020); the cell index is the integer part of the argument, so the pattern is the same on every platform.
    uint Pcg(uint v)
    {
        uint state = v * 747796405u + 2891336453u;
        uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
        return (word >> 22u) ^ word;
    }

    float2 Hash22(float2 p)
    {
        uint2 q = (uint2)((int2)floor(p));
        uint a = Pcg(q.x + Pcg(q.y));
        uint b = Pcg(a + 1013904223u);
        return float2(a, b) * (1.0 / 4294967296.0);
    }

    // What the drops do at a point of the picture (picture heights, y up): the disc's weight, the wet trail's, the bead's, the lens
    // position (the point of the drop in units of its radius, xy) and the drop's rim darkening and highlight.
    struct Drops
    {
        float disc, trail, bead, rim, spot;
        float2 lens;     // (pixel - centre) / radius in the picture plane
        float radius;    // in picture heights
    };

    // Rotation by an angle given as (cos, sin), and back.
    float2 Rot2(float2 v, float2 cs) { return float2(cs.x * v.x - cs.y * v.y, cs.y * v.x + cs.x * v.y); }
    float2 Unrot2(float2 v, float2 cs) { return float2(cs.x * v.x + cs.y * v.y, -cs.y * v.x + cs.x * v.y); }

    // The share of the glass the three layers cover when every cell holds its drop (measured by the weather player, row
    // droplet.design_cover); the wetness thins the cells out so that the covered share follows wet^0.85 * the configured coverage.
    #define DESIGN_COVER 0.28

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

        float r = _DrpB.w;
        // All offsets below are in picture heights. A cell is addressed along the sliding direction (s) and across it (c).

        // ---- the runners: large drops that stick and run. A cell is long along the run (7 widths), so that a run of 14 radii and its
        // trail stay inside it.
        {
            float cellC = 3.4 * r, cellS = 7.0 * cellC;
            float2 id = floor(float2(s / cellS, c / cellC));
            float2 f = float2(s - id.x * cellS, c - id.y * cellC);
            float2 h = Hash22(id);
            float2 h2 = Hash22(id + 31.7);
            if (h.x < present)
            {
                // The cycle: stuck, a quick run over 40 % of it, stuck. A new drop forms when it restarts (the radius grows in).
                // Every drop starts and runs its own way: where in the cell it forms (0.04 to 0.15 of its length) and how far it goes.
                float2 h3 = Hash22(id + 77.3);
                float s0 = cellS * (0.04 + 0.11 * h3.x);
                float travel = cellS * (0.35 + 0.30 * h3.y);
                float eff = max(speed, 0.004) * (0.6 + 0.8 * h2.x);
                float period = travel / eff * 2.5;
                float phase = frac(t / period + h2.y);
                float run = smoothstep(0.5, 0.9, phase);
                float born = smoothstep(0.0, 0.12, phase);
                float2 centre = float2(s0 + travel * run, cellC * (0.3 + 0.4 * h.y));
                float rad = max(r * (0.5 + 0.5 * h2.y) * born, 1e-5);
                float2 local = (f - centre) / rad;
                // Elongated along the run a little.
                float2 shaped = float2(local.x / 1.12, local.y);
                d.disc = 1.0 - smoothstep(0.86, 1.0, length(shaped));
                d.lens = dir * local.x + perp * local.y;
                d.radius = rad;
                // The wet trail: from where the drop started this cycle to where it is, narrow.
                float inTrail = step(s0, f.x) * step(f.x, centre.x - rad * 0.8);
                float across = abs(f.y - centre.y) / max(rad * 0.45, 1e-5);
                d.trail = inTrail * (1.0 - smoothstep(0.6, 1.0, across)) * run * (1.0 - smoothstep(0.93, 1.0, phase)) * 0.9;
            }
        }
        // ---- the middle drops: they stay.
        {
            // The lattice is turned by 31 degrees against the sliding direction so that the rows of drops do not read as a grid.
            const float2 cs = float2(0.857, 0.515);
            float cell = 2.4 * r;
            float2 q = Rot2(float2(s, c), cs) / cell + 17.0;
            float2 id = floor(q);
            float2 f = (frac(q)) * cell;
            float2 h = Hash22(id + 3.7);
            float2 h2 = Hash22(id + 61.9);
            if (h.x < present)
            {
                float2 centre = cell * float2(0.3 + 0.4 * h.y, 0.3 + 0.4 * h2.x);
                float rad = r * (0.4 + 0.2 * h2.y);
                float2 local = (f - centre) / rad;
                float disc = 1.0 - smoothstep(0.86, 1.0, length(local));
                if (disc > d.disc)
                {
                    float2 inLattice = Unrot2(local, cs);
                    d.disc = disc;
                    d.lens = dir * inLattice.x + perp * inLattice.y;
                    d.radius = rad;
                }
            }
        }
        // ---- the beads: small, many, they stay.
        {
            // Turned by -23 degrees.
            const float2 cs = float2(0.9205, -0.3907);
            float cell = 1.3 * r;
            float2 q = Rot2(float2(s, c), cs) / cell + 53.0;
            float2 id = floor(q);
            float2 f = frac(q) * cell;
            float2 h = Hash22(id + 7.1);
            float2 h2 = Hash22(id + 91.3);
            if (h.x < present)
            {
                float2 centre = cell * float2(0.3 + 0.4 * h.y, 0.3 + 0.4 * h2.x);
                float rad = r * (0.25 + 0.15 * h2.y);
                float2 local = (f - centre) / rad;
                float bead = 1.0 - smoothstep(0.8, 1.0, length(local));
                // A bead that sits on a drop's disc is part of it.
                d.bead = bead * (1.0 - d.disc);
                if (d.disc < 0.01 && bead > 0.0)
                {
                    float2 inLattice = Unrot2(local, cs);
                    d.lens = dir * inLattice.x + perp * inLattice.y;
                    d.radius = rad;
                }
            }
        }
        // The rim and the spot of the light follow from the lens position in the picture's own axes: the light comes from the upper left.
        {
            float qd = length(d.lens);
            float inside = step(0.001, d.disc) * step(qd, 1.0);
            d.rim = smoothstep(0.55, 1.0, qd) * d.disc;
            float2 toLight = d.lens - float2(-0.35, 0.35);
            d.spot = exp(-dot(toLight, toLight) * 9.0) * inside;
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
                float total = max(disc, d.trail * _DrpD.y) * keep;
                clip(total - 0.003);

                // The lens: the picture seen through the drop is upside down and compressed, a point of the drop at lens (in radii from
                // its centre) shows the point mirrored through the centre and scaled out.
                float gain = 2.4 * _DrpB.z;
                float2 off = -d.lens * d.radius * gain;
                float2 offUv = float2(off.x / _DrpA.z, flip ? -off.y : off.y);
                float3 sharp = Safe(tex2D(_DrpCopy, i.uv).rgb);
                float3 seen = Safe(tex2D(_DrpCopy, i.uv + offUv).rgb);
                float3 col = seen * (1.0 - _DrpD.x * d.rim) * (1.0 + 0.06);
                col += _DrpC.z * (0.55 * d.spot + 0.12 * d.bead);
                // The trail is a thin wet streak: the picture seen through the film, a little darker, with the light's glint on it.
                float3 streak = lerp(sharp, seen, 0.5) * 0.72 + _DrpC.z * 0.05;
                bool isDisc = disc >= d.trail * _DrpD.y;
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
                float total = max(disc * (0.35 + 0.55 * d.rim), d.trail * _DrpD.y * 0.6) * _DrpC.y;
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
