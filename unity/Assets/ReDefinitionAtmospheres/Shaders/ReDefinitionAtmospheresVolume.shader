// The volume pass of ReDefinition Atmospheres: storm cores with a shelf and an anvil, towering cells, cumulus and deck slabs, raymarched
// from the local 3D window the weather field gives (cloud water, updraft, temperature; src/Atmospheres/Render/Volume), plus a coverage
// and type map for the decks. No EVE layer is read and no cell is placed by hand: the shapes are the window's.
//
// Technique: the usual one for raymarched cloud volumes (Schneider and Vos, "The Real-time Volumetric Cloudscapes of Horizon: Zero
// Dawn", 2015; Guerrilla's "Nubis Evolved", 2023): a base shape eroded by tiled noise (with a curl offset), a dual-lobe Henyey-Greenstein
// phase, a short march towards the sun, a multiple-scattering approximation by octaves of weaker extinction and a flatter phase (after
// Hillaire 2016), the powder effect, a dark underside from the cloud mass above, a distance-adaptive step and a transmittance cut-off with
// energy renormalisation. The dither is the interleaved gradient noise of Jimenez (SIGGRAPH 2014).
//
// Passes (the half-resolution target is a RenderTexture of the pass; the march samples the depth at its own pixel):
//   0 MARCH      premultiplied colour and opacity into the target
//   1 COMPOSITE  the target over the scene at full size: a bilinear tap set weighted by the likeness of each tap's scene depth to the
//                pixel's own (nearest-depth upsampling), so that the cloud does not bleed over a foreground edge
//   2 HIT        the distance along the ray at which the cloud is met (the opacity-weighted depth), for the analytic motion and depth
//   3 MOTION     where the cloud covers a pixel, the motion of the point where its ray meets the cloud, from the window's advection
//   4 DEPTH      the depth of that point
// Keywords (four): VOLUME_POWDER, VOLUME_CURL, VOLUME_DECK, VOLUME_JITTER_IGN (the dither keyed to the upscaler's jitter index).
//
// Frame: the window's centre at the ground (_Cell.xyz), up, east (_Right) and north (_North); the ground curves away, so a height above
// it is the height along up plus curvature * r^2 (as the storm curtain shader has it).
Shader "ReDefinition/AtmospheresVolume"
{
    Properties
    {
        _Window ("Window", 3D) = "black" {}
        _Deck ("Deck", 2D) = "black" {}
        _Noise ("Noise", 3D) = "white" {}
        _MainTex ("Target", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-6" "RenderType" = "Transparent" "IgnoreProjector" = "True" "ForceNoShadowCasting" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler3D _Window;
        sampler2D _Deck;
        sampler3D _Noise;
        sampler2D _MainTex;
        sampler2D _HitTex;
        sampler2D _SceneMotion;
        sampler2D _SceneDepth;
        float4 _MainTex_TexelSize;
        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

        // xyz: the window's centre at the ground, world space; w: its half-width, m.
        float4 _Cell;
        // xyz: up; w: the planet's curvature, 1 / (2 * radius), per m.
        float4 _Up;
        float4 _Right;
        float4 _North;
        // x: the window's height, m; y: the erosion tile, m; z: the shape tile, m; w: the wind slant, m of shift per m of height.
        float4 _WinSize;
        // xy: the pattern's offset east and north, m (wrapped by the C# side); zw: the drift of the window since its snapshot, east and north, m.
        float4 _Wind;
        // x: the extinction at full density, per m; y: the erosion depth; z: the curl strength; w: the powder strength.
        float4 _Dens;
        // x: the sun's share; y: the sky's share; z: the dark base, 0 to 1; w: the transmittance below which the march ends.
        float4 _Light;
        // x: the steps; y: the longest distance a ray is marched, m; z: the frame number; w: the jitter index.
        float4 _Look;
        // x: the growth of the step; y: the taps towards the sun; z: the deck's coverage factor; w: the cloud water that fills a deck.
        float4 _Step;
        // The deck's base and thickness (m above the ground) for the three types: x,y stratus; z,w cumulus. _DeckB.xy: high deck.
        float4 _DeckA;
        float4 _DeckB;
        float4 _Sun;
        float4 _SunColour;
        float4 _AmbientColour;
        float4 _CamRight;
        float4 _CamUp;
        float4 _CamForward;
        // xyz: the pattern's velocity in world space, m/s; w: the opacity above which the cloud replaces the motion and depth.
        float4 _AdvectVel;
        float4x4 _CurViewProj;
        float4x4 _PrevViewProj;
        float4 _Gate;

        static const float Ceiling = 1.0;
        static const float Pi = 3.14159265;

        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        v2f vert(appdata v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.uv;
            return o;
        }

        // Interleaved gradient noise of the pixel. With VOLUME_JITTER_IGN the pattern moves with the upscaler's jitter index, so that the
        // upscaler sees a repeating pattern in step with its own jitter; otherwise it is fixed.
        float Dither(float2 pixel)
        {
            #if defined(VOLUME_JITTER_IGN)
            pixel += 5.588238 * fmod(_Look.w, 64.0);
            #endif
            return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
        }

        float Noise(float3 p, float lod)
        {
            return tex3Dlod(_Noise, float4(p, lod)).r;
        }

        float HG(float cosine, float g)
        {
            float g2 = g * g;
            return (1.0 - g2) / (4.0 * Pi * pow(max(1.0 + g2 - 2.0 * g * cosine, 1e-3), 1.5));
        }

        // The window's cloud water at a place of the window's frame (east, north, height above the ground); 0 outside it. The window is
        // moved by its drift since the snapshot.
        float WindowWater(float lx, float lz, float h)
        {
            float W = _Cell.w;
            float3 uvw = float3((lx - _Wind.z) / (2.0 * W) + 0.5, h / _WinSize.x, (lz - _Wind.w) / (2.0 * W) + 0.5);
            if (any(uvw < 0.0) || any(uvw > 1.0)) return 0.0;
            return tex3Dlod(_Window, float4(uvw, 0.0)).r;
        }

        // The deck's density share at a place: its coverage against a low-frequency noise and its height profile, 0 to 1.
        float DeckDensity(float lx, float lz, float h, float lod)
        {
            #if defined(VOLUME_DECK)
            float W = _Cell.w;
            float2 duv = float2(lx / (2.0 * W) + 0.5, lz / (2.0 * W) + 0.5);
            if (any(duv < 0.0) || any(duv > 1.0)) return 0.0;
            float2 deck = tex2Dlod(_Deck, float4(duv, 0.0, 0.0)).rg;
            float coverage = saturate(deck.r * _Step.z);
            if (coverage < 0.01) return 0.0;
            float type = deck.g;
            // Stratus, cumulus layer or high deck by the type.
            float lowShare = saturate(1.0 - type * 3.0);
            float highShare = saturate(type * 3.0 - 2.0);
            float midShare = 1.0 - lowShare - highShare;
            float base = _DeckA.x * lowShare + _DeckA.z * midShare + _DeckB.x * highShare;
            float thick = _DeckA.y * lowShare + _DeckA.w * midShare + _DeckB.y * highShare;
            float u = (h - base) / max(thick, 1.0);
            if (u < 0.0 || u > 1.0) return 0.0;
            float profile = smoothstep(0.0, 0.12, u) * (1.0 - smoothstep(0.55, 1.0, u));
            float3 p = float3((lx - _Wind.x) / _WinSize.z * 2.0, h / _WinSize.z, (lz - _Wind.y) / _WinSize.z * 2.0);
            float n = Noise(p, lod);
            float cover = smoothstep(1.0 - coverage, 1.0 - coverage + 0.3, n + coverage * 0.5);
            return profile * cover;
            #else
            return 0.0;
            #endif
        }

        // The extinction per metre at a place. detail: 1 for the eroded shape, 0 for the cheap one (window and deck only: the taps towards the
        // sun and the sky). dens01 gets the density before the scale.
        float Density(float lx, float lz, float h, float lod, float detail, out float dens01)
        {
            dens01 = 0.0;
            if (h < 0.0 || h > _WinSize.x) return 0.0;
            float w = WindowWater(lx, lz, h);
            float deck = DeckDensity(lx, lz, h, lod);
            if (w < 0.004 && deck <= 0.0) return 0.0;
            float d = w;
            if (detail > 0.5)
            {
                // The shape: lumps from a low-frequency noise, then the erosion by the fine one (offset by a curl where it is on).
                float slant = _WinSize.w * h;
                float sx = lx - _Wind.x + slant, sz = lz - _Wind.y;
                float shapeTile = _WinSize.z;
                float nb = Noise(float3(sx / shapeTile, h / shapeTile * 1.7, sz / shapeTile), lod);
                d = saturate((w * (0.65 + 0.7 * nb) - 0.06) / 0.94);
                if (d > 0.0)
                {
                    float3 pe = float3(sx, h * 1.3, sz) / _WinSize.y;
                    #if defined(VOLUME_CURL)
                    float3 curl = float3(Noise(pe * 0.37 + float3(0.11, 0.0, 0.0), lod),
                                         Noise(pe * 0.37 + float3(0.0, 0.43, 0.0), lod),
                                         Noise(pe * 0.37 + float3(0.0, 0.0, 0.71), lod)) - 0.5;
                    pe += curl * _Dens.z * 3.0;
                    #endif
                    float e = Noise(pe, lod);
                    d = saturate((d - e * _Dens.y) / (1.0 - e * _Dens.y));
                }
            }
            d = max(d, deck * (detail > 0.5 ? 1.0 : 0.9) * _Step.w);
            dens01 = saturate(d);
            return dens01 * _Dens.x;
        }

        float HeightOf(float3 local)
        {
            return local.y + _Up.w * (local.x * local.x + local.z * local.z);
        }

        // The ray in the window's frame: origin and direction; false when it misses the window's volume. t0, t1 along the ray.
        bool RayInWindow(float2 uv, out float3 rd, out float3 lo, out float3 ld, out float t0, out float t1)
        {
            float3 ro = _WorldSpaceCameraPos;
            float2 ndc = uv * 2.0 - 1.0;
            ndc -= float2(_CamRight.w, _CamUp.w);
            rd = normalize(_CamForward.xyz + ndc.x * _CamRight.xyz + ndc.y * _CamUp.xyz);
            float3 rel = ro - _Cell.xyz;
            lo = float3(dot(rel, _Right.xyz), dot(rel, _Up.xyz), dot(rel, _North.xyz));
            ld = float3(dot(rd, _Right.xyz), dot(rd, _Up.xyz), dot(rd, _North.xyz));
            t0 = 0.0;
            t1 = 0.0;
            float W = _Cell.w;
            float a = ld.x * ld.x + ld.z * ld.z;
            float b = lo.x * ld.x + lo.z * ld.z;
            float c = lo.x * lo.x + lo.z * lo.z - W * W;
            float tc0 = -1e9;
            float tc1 = 1e9;
            if (a > 1e-8)
            {
                float disc = b * b - a * c;
                if (disc <= 0.0) return false;
                float root = sqrt(disc);
                tc0 = (-b - root) / a;
                tc1 = (-b + root) / a;
            }
            else if (c > 0.0)
                return false;
            float lowH = -_Up.w * W * W - 50.0;
            float highH = _WinSize.x + 50.0;
            float ty0 = -1e9;
            float ty1 = 1e9;
            if (abs(ld.y) > 1e-5)
            {
                float ta = (lowH - lo.y) / ld.y;
                float tb = (highH - lo.y) / ld.y;
                ty0 = min(ta, tb);
                ty1 = max(ta, tb);
            }
            else if (lo.y < lowH || lo.y > highH)
                return false;
            t0 = max(max(tc0, ty0), _ProjectionParams.y);
            t1 = min(min(tc1, ty1), min(_Look.y, _ProjectionParams.z));
            float eye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
            if (eye < _ProjectionParams.z * 0.99)
            {
                float forward = dot(rd, _CamForward.xyz);
                t1 = min(t1, eye / max(forward, 1e-3));
            }
            return t1 - t0 > 1.0;
        }

        // The march of one ray: premultiplied colour in rgb, opacity in a.
        float4 March(float2 uv, float2 pixel)
        {
            float3 rd, lo, ld;
            float t0, t1;
            if (!RayInWindow(uv, rd, lo, ld, t0, t1)) return 0.0;

            float steps = clamp(_Look.x, 8.0, 96.0);
            float growth = _Step.x;
            float L = t1 - t0;
            float dt = growth < 1.0001 ? L / steps : L * (growth - 1.0) / (pow(growth, steps) - 1.0);
            float t = t0 + dt * Dither(pixel);
            float cosSun = dot(rd, _Sun.xyz);
            float phase = lerp(HG(cosSun, -0.15), HG(cosSun, 0.62), 0.45) * 4.0 * Pi * 0.5;
            int sunTaps = (int)_Step.y;

            float3 colour = 0.0;
            float transmittance = 1.0;
            [loop]
            for (int k = 0; k < 96; k++)
            {
                if (k >= (int)steps) break;
                float3 p = lo + ld * t;
                float h = HeightOf(p);
                float lod = clamp(log2(max(dt, 1.0) / 80.0), 0.0, 5.0);
                float dens;
                float sigma = Density(p.x, p.z, h, lod, 1.0, dens);
                if (sigma * dt < 0.002)
                {
                    // A wisp: it keeps its opacity, but costs no light taps.
                    transmittance *= 1.0 - sigma * dt;
                }
                else
                {
                    float opacity = 1.0 - exp(-sigma * dt);

                    // The sun's light through taps towards the sun, each farther than the last.
                    float3 sp = p;
                    float depthSun = 0.0;
                    float stepSun = 250.0;
                    float3 sunLocal = float3(dot(_Sun.xyz, _Right.xyz), dot(_Sun.xyz, _Up.xyz), dot(_Sun.xyz, _North.xyz));
                    [loop]
                    for (int s = 0; s < 4; s++)
                    {
                        if (s >= sunTaps) break;
                        sp += sunLocal * stepSun;
                        float sd;
                        depthSun += Density(sp.x, sp.z, HeightOf(sp), lod + 2.0, 0.0, sd) * stepSun;
                        stepSun *= 2.3;
                    }
                    // Multiple scattering as octaves with less extinction and a flatter phase.
                    float sun = 0.0;
                    float attenuation = 1.0;
                    float contribution = 1.0;
                    float flatten = 1.0;
                    [unroll]
                    for (int o = 0; o < 3; o++)
                    {
                        sun += contribution * exp(-depthSun * attenuation) * lerp(1.0, phase, flatten);
                        attenuation *= 0.45;
                        contribution *= 0.5;
                        flatten *= 0.5;
                    }
                    #if defined(VOLUME_POWDER)
                    float powder = 1.0 - exp(-2.0 * dens * 3.0);
                    sun *= lerp(1.0, powder * 1.6, saturate(_Dens.w * 0.5) * (1.0 - saturate(cosSun)));
                    #endif

                    // The sky's light: less where much cloud stands above, so the base is dark.
                    float dUp1;
                    float above = Density(p.x, p.z, h + 1200.0, lod + 3.0, 0.0, dUp1) * 2000.0;
                    float open = exp(-above * 0.7);
                    float under = lerp(1.0 - _Light.z, 1.0, open);
                    float inner = lerp(1.0, 0.6, saturate(dens));
                    float3 ambient = _AmbientColour.rgb * _Light.y * (0.55 + 0.45 * saturate(h / (_WinSize.x * 0.5))) * under * inner;
                    float3 light = min(_SunColour.rgb * (sun * _Light.x) * lerp(0.35, 1.0, open) + ambient, Ceiling);
                    colour += transmittance * opacity * light;
                    transmittance *= 1.0 - opacity;
                    if (transmittance < _Light.w)
                    {
                        // Energy renormalisation: what is left of the ray is taken as opaque, so the colour is scaled to the opacity it would have.
                        colour /= max(1.0 - transmittance, 1e-3);
                        transmittance = 0.0;
                        break;
                    }
                }
                t += dt;
                dt *= growth;
            }
            return float4(colour, 1.0 - transmittance);
        }

        // The distance at which the ray meets the cloud: a coarse march without light, the point where the optical depth reaches 0.7, or the
        // optical-depth weighted mean; 0 where the ray meets none.
        float Hit(float2 uv)
        {
            float3 rd, lo, ld;
            float t0, t1;
            if (!RayInWindow(uv, rd, lo, ld, t0, t1)) return 0.0;
            float steps = 24.0;
            float dt = (t1 - t0) / steps;
            float tau = 0.0;
            float weighted = 0.0;
            float t = t0 + dt * 0.5;
            [loop]
            for (int k = 0; k < 24; k++)
            {
                float3 p = lo + ld * t;
                float dens;
                float sigma = Density(p.x, p.z, HeightOf(p), 2.0, 0.0, dens);
                float segment = sigma * dt;
                weighted += t * segment;
                tau += segment;
                if (tau >= 0.7) return t;
                t += dt;
            }
            return tau > 0.02 ? weighted / tau : 0.0;
        }

        float2 ToUv(float4 clip)
        {
            float2 ndc = clip.xy / clip.w;
            // Unity's motion on Direct3D 11 is measured in y-up clip space (ReDefinition.cginc, ReDefinitionMotionVector).
            return ndc * 0.5 + 0.5;
        }

        ENDCG

        // Pass 0: the march, into the volume texture.
        Pass
        {
            Name "MARCH"
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMarch
            #pragma target 3.5
            #pragma multi_compile_local __ VOLUME_POWDER
            #pragma multi_compile_local __ VOLUME_CURL
            #pragma multi_compile_local __ VOLUME_DECK
            #pragma multi_compile_local __ VOLUME_JITTER_IGN
            float4 fragMarch(v2f i) : SV_Target
            {
                return March(i.uv, i.pos.xy);
            }
            ENDCG
        }

        // Pass 1: the volume texture over the scene, with the nearest-depth weighting.
        Pass
        {
            Name "COMPOSITE"
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragComposite
            #pragma target 3.5
            float SceneEye(float2 uv)
            {
                return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
            }
            float4 fragComposite(v2f i) : SV_Target
            {
                float2 size = _MainTex_TexelSize.zw;
                float2 pos = i.uv * size - 0.5;
                float2 base = floor(pos);
                float2 f = pos - base;
                float eye = SceneEye(i.uv);
                float4 sum = 0.0;
                float weights = 0.0;
                [unroll]
                for (int j = 0; j < 4; j++)
                {
                    float2 o = float2(j & 1, j >> 1);
                    float2 tuv = (base + o + 0.5) / size;
                    float bilinear = (o.x > 0.5 ? f.x : 1.0 - f.x) * (o.y > 0.5 ? f.y : 1.0 - f.y);
                    float tapEye = SceneEye(tuv);
                    // The tap whose scene depth is like this pixel's counts; one across a depth edge does not.
                    float likeness = 1.0 / (1e-3 + abs(tapEye - eye) / max(eye * 0.04, 1.0));
                    float w = bilinear * likeness;
                    sum += tex2Dlod(_MainTex, float4(tuv, 0.0, 0.0)) * w;
                    weights += w;
                }
                return sum / max(weights, 1e-5);
            }
            ENDCG
        }

        // Pass 2: the hit distance, into a texture the size of the volume texture.
        Pass
        {
            Name "HIT"
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragHit
            #pragma target 3.5
            #pragma multi_compile_local __ VOLUME_DECK
            float fragHit(v2f i) : SV_Target
            {
                return Hit(i.uv);
            }
            ENDCG
        }

        // Pass 3: the motion, at the size of the scene's motion texture (_MainTex of the blit is the scene's motion; _SlabTex is replaced
        // by the volume texture through _CloudTex).
        Pass
        {
            Name "MOTION"
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMotion
            #pragma target 3.5
            sampler2D _CloudTex;
            float2 fragMotion(v2f i) : SV_Target
            {
                float2 scene = tex2D(_SceneMotion, i.uv).rg;
                float4 cloud = tex2D(_CloudTex, i.uv);
                if (cloud.a < _AdvectVel.w) return scene;
                float hit = tex2D(_HitTex, i.uv).r;
                if (hit <= 0.0) return scene;
                float eye = LinearEyeDepth(tex2D(_SceneDepth, i.uv).r);
                float2 ndc = i.uv * 2.0 - 1.0;
                float3 rd = normalize(_CamForward.xyz + ndc.x * _CamRight.xyz + ndc.y * _CamUp.xyz);
                if (eye < _ProjectionParams.z * 0.99 && eye < hit * dot(rd, _CamForward.xyz)) return scene;
                float3 pos = _WorldSpaceCameraPos + rd * hit;
                float2 now = ToUv(mul(_CurViewProj, float4(pos, 1.0)));
                float2 before = ToUv(mul(_PrevViewProj, float4(pos - _AdvectVel.xyz * _Gate.x, 1.0)));
                float2 motion = now - before;
                return _Gate.y > 0.5 ? -motion : motion;
            }
            ENDCG
        }

        // Pass 4: the depth of the point the motion pass moves.
        Pass
        {
            Name "DEPTH"
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            #pragma target 3.5
            sampler2D _CloudTex;
            float fragDepth(v2f i) : SV_Target
            {
                float scene = tex2D(_SceneDepth, i.uv).r;
                float4 cloud = tex2D(_CloudTex, i.uv);
                if (cloud.a < _AdvectVel.w) return scene;
                float hit = tex2D(_HitTex, i.uv).r;
                if (hit <= 0.0) return scene;
                float eye = LinearEyeDepth(tex2D(_SceneDepth, i.uv).r);
                float2 ndc = i.uv * 2.0 - 1.0;
                float3 rd = normalize(_CamForward.xyz + ndc.x * _CamRight.xyz + ndc.y * _CamUp.xyz);
                if (eye < _ProjectionParams.z * 0.99 && eye < hit * dot(rd, _CamForward.xyz)) return scene;
                float4 clip = mul(_CurViewProj, float4(_WorldSpaceCameraPos + rd * hit, 1.0));
                return clip.z / clip.w;
            }
            ENDCG
        }
    }
}
