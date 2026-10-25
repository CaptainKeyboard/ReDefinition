// The space centre's ground drawn with its lawn as Parallax's terrain beside it (src/Scenery/KscGround.cs).
// Built into redefinition.shaders by BundleBuilder.
//
// KSP's ground shader (KSP/Scenery/Diffuse Ground KSC) mixes its tarmac and its own grass by a blend mask;
// Deferred replaces it and its specular variant with its own (DiffuseKSCGround.shader and
// DiffuseKSCGroundSpecular.shader, GPL-3.0), whose tarmac this one keeps: the tarmac texture on the first
// texture coordinates times _TarmacColor (at most 0.89, as Deferred holds it), the mask's red on the
// second, a smoothness of 0.25 (the specular variant's 0.55, with its tarmac normal map) times the root of
// the tarmac's alpha, and Unity's dielectric shine as its standard shader gives it in gamma space (a
// specular colour of 0.2209, the diffuse colour times 0.7791). Its lawn is drawn as Parallax Continued's
// terrain draws Kerbin's meadow at that height, from the same textures and values (KscGround copies them
// from Parallax's own terrain material), so that it reads as the terrain beside it, as Parallax's terrain
// shader draws it (Parallax.shader and its includes, read 2026-10-25):
//
// * Its textures are laid on in world space, at the position plus Parallax's _TerrainShaderOffset,
//   which holds it to the ground over the floating origin's shifts, on two of the three axis-aligned
//   planes: the one facing the surface's normal most, and the next. Each plane's weight, from the
//   normal's component along it, rises from 0 at 1/sqrt(3) to 1 at 1 and is taken to the power 1/2;
//   the two samples are mixed by their weights.
// * At two scales chosen by the distance to the camera: with L = log2(0.04 d + 0.4), the texture
//   repeats every 2^(floor L - 1) / _Tiling and 2^(floor L) / _Tiling metres, mixed by the fraction of
//   L, so that the next level takes over without a jump. Sampled with the gradients of the plain
//   world position at each scale.
// * Kerbin's meadow band (the mid one: its terrain at the space centre's 65 to 75 m lies past the low
//   band's end at 60 m), the influence and occlusion maps' green channel. Its colour: the vertex colour
//   -- the planet's colour map, as the terrain's quads carry it -- times the texture's luminance plus a
//   half, towards the texture's own colour by the influence. The occlusion into the G-buffer's. The
//   normal map added in the plane's two axes to the surface's normal, per plane, the planes mixed as
//   the colours.
// * Its shine as Parallax converts its legacy highlight: a specular colour of its intensity over pi,
//   a smoothness of its power to the 0.215 times the texture's alpha times the root of its intensity;
//   the diffuse colour times one less the specular colour.
// The vertex colour comes from the terrain's quads under the space centre (KscGround's colour map).
Shader "ReDefinition/Scenery/Diffuse Ground KSC"
{
    Properties
    {
        _TarmacTexture ("Ground Color Texture", 2D) = "gray" {}
        _BlendMaskTexture ("Blend Mask", 2D) = "gray" {}
        _TarmacColor ("Ground Color", Color) = (1, 1, 1, 1)
        _TarmacNormal ("Ground Normal Texture", 2D) = "bump" {}
        _TarmacNormalOn ("The tarmac's normal map used", Float) = 0
        _TarmacSmoothness ("The tarmac's smoothness", Float) = 0.25
        _GrassColor ("Grass Color", Color) = (1, 1, 1, 1)
        _MainTexMid ("Parallax's meadow texture", 2D) = "white" {}
        _BumpMapMid ("Parallax's meadow normals", 2D) = "bump" {}
        _InfluenceMap ("Parallax's influence map", 2D) = "black" {}
        _OcclusionMap ("Parallax's occlusion map", 2D) = "white" {}
        _Tiling ("Parallax's tiling", Float) = 0.03
        _BumpScale ("Parallax's normal scale", Float) = 1
        _SpecularPower ("Parallax's specular power", Float) = 5
        _SpecularIntensity ("Parallax's specular intensity", Float) = 0.12
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    // The material's properties above: KSP's tarmac, mask and tarmac normal as Deferred takes them, and
    // Parallax's meadow textures and values, which KscGround copies from its terrain material.
    sampler2D _TarmacTexture;
    float4 _TarmacTexture_ST;
    sampler2D _BlendMaskTexture;
    float4 _BlendMaskTexture_ST;
    float4 _TarmacColor;
    sampler2D _TarmacNormal;
    float _TarmacNormalOn;
    float _TarmacSmoothness;
    sampler2D _MainTexMid;
    sampler2D _BumpMapMid;
    sampler2D _InfluenceMap;
    sampler2D _OcclusionMap;
    float _Tiling;
    float _BumpScale;
    float _SpecularPower;
    float _SpecularIntensity;

    // Parallax's, set every frame (its RuntimeOperations): where its terrain's textures are anchored.
    float3 _TerrainShaderOffset;
    // KscGround's: the terrain's vertex colours under the space centre, in the plane of its root.
    sampler2D _ReDefinitionKscColours;
    float4x4 _ReDefinitionKscWorldToRoot;
    // x, z of the colour map's corner in the root's plane, one over its width and depth in metres.
    float4 _ReDefinitionKscColourGrid;

    // The ground mesh's vertex.
    struct appdata
    {
        // The position, normal and tangent; the tarmac's and the blend mask's texture coordinates.
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float4 tangent : TANGENT;
        float2 uv0 : TEXCOORD0;
        float2 uv1 : TEXCOORD1;
    };

    // What vertGround hands the fragment functions.
    struct v2f
    {
        float4 pos : SV_POSITION; // the vertex's clip position
        float4 uv : TEXCOORD0;            // xy the tarmac's, zw the mask's
        float3 worldPos : TEXCOORD1;
        float3 worldNormal : TEXCOORD2;
        float2 colourUv : TEXCOORD3;
        float4 worldTangent : TEXCOORD5;
    };

    // The tarmac's and the mask's texture coordinates, the world position, normal and tangent, and where the
    // vertex lies on KscGround's colour map.
    v2f vertGround(appdata v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv.xy = TRANSFORM_TEX(v.uv0, _TarmacTexture);
        o.uv.zw = TRANSFORM_TEX(v.uv1, _BlendMaskTexture);
        o.worldPos = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
        o.worldNormal = UnityObjectToWorldNormal(v.normal);
        o.worldTangent = float4(mul((float3x3)unity_ObjectToWorld, v.tangent.xyz), v.tangent.w * unity_WorldTransformParams.w);
        float3 root = mul(_ReDefinitionKscWorldToRoot, float4(o.worldPos, 1.0)).xyz;
        o.colourUv = (root.xz - _ReDefinitionKscColourGrid.xy) * _ReDefinitionKscColourGrid.zw;
        return o;
    }

    // The two planes a surface facing n is textured on: the one it faces most, and the next.
    void Planes(float3 n, out int major, out int median, out float wMajor, out float wMedian)
    {
        float3 a = abs(n);
        major = a.x >= a.y ? (a.x >= a.z ? 0 : 2) : (a.y >= a.z ? 1 : 2);
        int minor = a.x < a.y ? (a.x < a.z ? 0 : 2) : (a.y < a.z ? 1 : 2);
        if (minor == major) minor = major == 0 ? 2 : major - 1;
        median = 3 - major - minor;
        float2 w = float2(a[major], a[median]);
        w = sqrt(saturate(2.365744 * w - 1.365744));
        float sum = max(w.x + w.y, 1e-5);
        wMajor = w.x / sum;
        wMedian = w.y / sum;
    }

    // A point's coordinates on the plane across axis k, and the plane's own two axes.
    float2 OnPlane(float3 p, int k)
    {
        return k == 0 ? p.yz : k == 1 ? p.zx : p.xy;
    }

    // The plane's first axis in the world, along OnPlane's x.
    float3 PlaneAxisU(int k)
    {
        return k == 0 ? float3(0, 1, 0) : k == 1 ? float3(0, 0, 1) : float3(1, 0, 0);
    }

    // The plane's second axis in the world, along OnPlane's y.
    float3 PlaneAxisV(int k)
    {
        return k == 0 ? float3(0, 0, 1) : k == 1 ? float3(1, 0, 0) : float3(0, 1, 0);
    }

    // The two scales a texture is laid on at, chosen by the distance.
    struct Levels
    {
        float scale0, scale1, t; // the texture's scales at the finer and the coarser level, and the mix
    };

    // The two scales at a distance from the camera, and the fraction that mixes them.
    Levels LevelsAt(float distance)
    {
        Levels l;
        float level = log2(0.04 * distance + 0.4);
        float whole = floor(level);
        l.scale0 = _Tiling / exp2(whole - 1.0);
        l.scale1 = _Tiling / exp2(whole);
        l.t = level - whole;
        return l;
    }

    // A texture on one plane at both levels, mixed.
    float4 OnLevels(sampler2D tex, float3 p, float3 dx, float3 dy, int k, Levels l)
    {
        float2 uv = OnPlane(p, k), gx = OnPlane(dx, k), gy = OnPlane(dy, k);
        float4 a = tex2Dgrad(tex, uv * l.scale0, gx * l.scale0, gy * l.scale0);
        float4 b = tex2Dgrad(tex, uv * l.scale1, gx * l.scale1, gy * l.scale1);
        return lerp(a, b, l.t);
    }

    // The surface's normal bent by the meadow's normal map on one plane, at both levels mixed.
    float3 NormalOnLevels(float3 p, float3 dx, float3 dy, int k, Levels l, float3 n)
    {
        float2 uv = OnPlane(p, k), gx = OnPlane(dx, k), gy = OnPlane(dy, k);
        float3 a = UnpackNormal(tex2Dgrad(_BumpMapMid, uv * l.scale0, gx * l.scale0, gy * l.scale0));
        float3 b = UnpackNormal(tex2Dgrad(_BumpMapMid, uv * l.scale1, gx * l.scale1, gy * l.scale1));
        float2 bend = lerp(a, b, l.t).xy * _BumpScale;
        return normalize(n + bend.x * PlaneAxisU(k) + bend.y * PlaneAxisV(k));
    }

    // The surface a pixel of the ground has, as the G-buffer and the forward pass take it.
    struct Ground
    {
        // The diffuse and specular colour, the smoothness, the occlusion and the world normal.
        float3 diffuse;
        float3 specular;
        float smoothness;
        float occlusion;
        float3 normal;
    };

    // The tarmac's normal: its normal map in the mesh's tangent space where the material has one (KSP's
    // specular variant of the ground shader, as Deferred reads it), else the surface's. A mesh without
    // tangents keeps the surface's.
    float3 TarmacNormal(v2f i, float3 n)
    {
        float tangentLength = length(i.worldTangent.xyz);
        if (_TarmacNormalOn < 0.5 || tangentLength < 1e-4)
            return n;
        float3 t = i.worldTangent.xyz / tangentLength;
        float3 b = cross(n, t) * (i.worldTangent.w < 0.0 ? -1.0 : 1.0);
        float3 m = UnpackNormalDXT5nm(tex2D(_TarmacNormal, i.uv.xy));
        return normalize(t * m.x + b * m.y + n * m.z);
    }

    // The tarmac and the lawn at a pixel, mixed by the blend mask's red: 1 the tarmac, 0 the lawn.
    Ground GroundAt(v2f i)
    {
        float3 n = normalize(i.worldNormal);
        float mask = tex2D(_BlendMaskTexture, i.uv.zw).r;

        // The tarmac as Deferred draws it.
        float4 tarmacTexture = tex2D(_TarmacTexture, i.uv.xy);
        float4 tarmacColour = min(_TarmacColor, float4(0.89, 0.89, 0.89, 1.0));
        float3 tarmac = tarmacColour.rgb * tarmacTexture.rgb;
        float tarmacSmoothness = _TarmacSmoothness * sqrt(max(tarmacTexture.a * tarmacColour.a, 1e-7));
        const float dielectric = 0.220916301;

        // The lawn as Parallax's terrain.
        float3 p = i.worldPos + _TerrainShaderOffset;
        float3 dx = ddx(i.worldPos), dy = ddy(i.worldPos);
        Levels l = LevelsAt(length(_WorldSpaceCameraPos - i.worldPos));
        int major, median;
        float wMajor, wMedian;
        Planes(n, major, median, wMajor, wMedian);
        float4 meadow = OnLevels(_MainTexMid, p, dx, dy, major, l) * wMajor + OnLevels(_MainTexMid, p, dx, dy, median, l) * wMedian;
        float influence = OnLevels(_InfluenceMap, p, dx, dy, major, l).g * wMajor + OnLevels(_InfluenceMap, p, dx, dy, median, l).g * wMedian;
        float occlusion = OnLevels(_OcclusionMap, p, dx, dy, major, l).g * wMajor + OnLevels(_OcclusionMap, p, dx, dy, median, l).g * wMedian;
        float3 bent = NormalOnLevels(p, dx, dy, major, l, n) * wMajor + NormalOnLevels(p, dx, dy, median, l, n) * wMedian;
        float3 vertexColour = tex2D(_ReDefinitionKscColours, i.colourUv).rgb;
        float luminance = 0.21 * meadow.r + 0.72 * meadow.g + 0.07 * meadow.b + 0.5;
        float3 lawn = lerp(vertexColour * luminance, meadow.rgb, influence);
        float intensity = saturate(_SpecularIntensity);
        float lawnSpecular = intensity / UNITY_PI;
        float lawnSmoothness = pow(max(_SpecularPower, 1e-4), 0.215) * meadow.a * sqrt(intensity);

        Ground g;
        g.diffuse = lerp(lawn * (1.0 - lawnSpecular), tarmac * (1.0 - dielectric), mask);
        g.specular = lerp(lawnSpecular, dielectric, mask).xxx;
        g.smoothness = saturate(lerp(lawnSmoothness, tarmacSmoothness, mask));
        g.occlusion = lerp(occlusion, 1.0, mask);
        g.normal = lerp(bent, TarmacNormal(i, n), mask);
        return g;
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        // Deferred: the G-buffer as Unity's standard specular setup packs it; the stencil Deferred's
        // space centre ground writes (local scenery).
        Pass
        {
            Tags { "LightMode" = "Deferred" }
            Stencil
            {
                Ref 3
                Comp Always
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex vertGround
            #pragma fragment fragDeferred
            #pragma target 3.0
            #pragma multi_compile _ UNITY_HDR_ON
            #include "UnityGBuffer.cginc"

            // The ground into the G-buffer's four targets; the fourth without emission.
            void fragDeferred(v2f i, out half4 gbuffer0 : SV_Target0, out half4 gbuffer1 : SV_Target1,
                              out half4 gbuffer2 : SV_Target2, out half4 gbuffer3 : SV_Target3)
            {
                Ground g = GroundAt(i);
                UnityStandardData data;
                data.diffuseColor = g.diffuse;
                data.occlusion = g.occlusion;
                data.specularColor = g.specular;
                data.smoothness = g.smoothness;
                data.normalWorld = normalize(g.normal);
                UnityStandardDataToGbuffer(data, gbuffer0, gbuffer1, gbuffer2);
                // No emission, and no ambient light: Deferred lights the scene's ambient itself.
                #if defined(UNITY_HDR_ON)
                gbuffer3 = half4(0, 0, 0, 1);
                #else
                gbuffer3 = half4(1, 1, 1, 1);
                #endif
            }
            ENDCG
        }

        // Forward, for the cameras that draw without a G-buffer: the sun and the ambient light on the
        // same colours, Lambert's.
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }

            CGPROGRAM
            #pragma vertex vertForward
            #pragma fragment fragForward
            #pragma target 3.0
            #pragma multi_compile_fwdbase
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            // v2f with the sun's shadow coordinates, which the forward pass needs.
            struct v2fForward
            {
                // As in v2f, for GroundAt.
                float4 pos : SV_POSITION;
                float4 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
                float2 colourUv : TEXCOORD3;
                float4 worldTangent : TEXCOORD5;
                SHADOW_COORDS(4)
            };

            // vertGround's output copied over, with the sun's shadow coordinates.
            v2fForward vertForward(appdata v)
            {
                v2f g = vertGround(v);
                v2fForward o;
                o.pos = g.pos;
                o.uv = g.uv;
                o.worldPos = g.worldPos;
                o.worldNormal = g.worldNormal;
                o.colourUv = g.colourUv;
                o.worldTangent = g.worldTangent;
                TRANSFER_SHADOW(o)
                return o;
            }

            // The ground's colours lit by the sun with its shadow and by the ambient light, without a shine.
            half4 fragForward(v2fForward i) : SV_Target
            {
                v2f g0;
                g0.pos = i.pos;
                g0.uv = i.uv;
                g0.worldPos = i.worldPos;
                g0.worldNormal = i.worldNormal;
                g0.colourUv = i.colourUv;
                g0.worldTangent = i.worldTangent;
                Ground g = GroundAt(g0);
                float3 n = normalize(g.normal);
                UNITY_LIGHT_ATTENUATION(attenuation, i, i.worldPos);
                float3 light = _LightColor0.rgb * saturate(dot(n, normalize(_WorldSpaceLightPos0.xyz))) * attenuation;
                return half4(g.diffuse * (light + ShadeSH9(float4(n, 1.0)) * g.occlusion), 1.0);
            }
            ENDCG
        }
    }
    Fallback "Diffuse"
}
