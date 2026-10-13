// KSP's terrain as Deferred colours it against KSP's own shaders, per pixel, for the
// forward light without Deferred (src/Raytracing/RtCamera.cs, RtDeferredLook.cs): drawn
// with each terrain quad into the target the space centre's ground goes into
// (ReDefinitionRtTarmac.shader), the ratio of the two colours, which the relight pass
// multiplies the frame by. Both from the vertex colour, the steepness (uv2.y) and the
// textures' averages in Unity's colour space -- the atlas layers by the strengths packed
// in uv3, or the height bands by the vertex colour's alpha:
// - KSP 1.12.5 (its compiled shaders): v = _contrast (s - t) + t, s the vertex colour
//   saturated by _saturation, t = _tintColor.rgb * _tintColor.a; lerp(v, v * texture,
//   its lerp), towards the steep texture by saturate(uv2.y * _steepPower), times its
//   brightness and, for the standard-specular ones, 1 - max(_specularColor);
// - Deferred 1.3.5 (TerrainReplacementShader.cginc): the texture towards the steep one
//   alike, then on the atlas times 1.75 and the raw vertex colour, otherwise
//   lerp(c, c * texture, 0.8) with c = lerp(t, vertex colour, _contrast); times
//   _albedoBrightness and, by the metallic setup, 1 - 0.2209 in gamma space.
Shader "Hidden/ReDefinition/RtTerrainLook"
{
    Properties
    {
        _ReDefinitionAtlas ("Atlas", Float) = 0
        _ReDefinitionStockLerp ("KSP's texture share", Float) = 1
        _ReDefinitionStockScale ("KSP's brightness and specular share", Float) = 1
        _ReDefinitionDeferredScale ("Deferred's brightness and specular share", Float) = 1
        _ReDefinitionSteep ("Steep average", Color) = (1, 1, 1, 1)
        _ReDefinitionLow ("Low band average", Color) = (1, 1, 1, 1)
        _ReDefinitionMid ("Mid band average", Color) = (1, 1, 1, 1)
        _ReDefinitionHigh ("High band average", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Pass
        {
            ZWrite Off ZTest Always Cull Back
            ColorMask RGB
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            float _ReDefinitionAtlas;
            float _ReDefinitionStockLerp;
            float _ReDefinitionStockScale;
            float _ReDefinitionDeferredScale;
            float4 _ReDefinitionSteep;
            float4 _ReDefinitionLow, _ReDefinitionMid, _ReDefinitionHigh;
            float4 _ReDefinitionLayers[32];
            float _saturation, _contrast, _steepPower;
            float4 _tintColor;
            float _lowStart, _lowEnd, _highStart, _highEnd;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv2 : TEXCOORD1;
                float2 uv3 : TEXCOORD2;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float4 screen : TEXCOORD0;
                float depth : TEXCOORD1;
                float steep : TEXCOORD2;
                float3 atlas : TEXCOORD3;
            };

            // The atlas's colour at a vertex: its layers' averages by the strengths in uv3.
            float3 Atlas(float2 packed)
            {
                float x = round(packed.x), y = round(packed.y);
                float i0 = floor(x / 32768.0);
                float i1 = fmod(floor(x / 1024.0), 32.0);
                float i2 = fmod(floor(x / 32.0), 32.0);
                float i3 = fmod(x, 32.0);
                float s0 = floor(y / 40000.0) / 200.0;
                float rest = y - 40000.0 * floor(y / 40000.0);
                float s1 = floor(rest / 200.0) * 0.01;
                float s2 = (rest - 200.0 * floor(rest / 200.0)) * 0.01;
                float s3 = 1.0 - s0 - s1 - s2;
                return _ReDefinitionLayers[(int)i0].rgb * s0 + _ReDefinitionLayers[(int)i1].rgb * s1
                       + _ReDefinitionLayers[(int)i2].rgb * s2 + _ReDefinitionLayers[(int)i3].rgb * max(s3, 0.0);
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.screen = ComputeScreenPos(o.pos);
                COMPUTE_EYEDEPTH(o.depth);
                o.steep = saturate(v.uv2.y * _steepPower);
                o.atlas = _ReDefinitionAtlas > 0.5 ? Atlas(v.uv3) : 1.0;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Only where this surface is the one the camera sees.
                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.screen.xy / i.screen.w));
                if (abs(scene - i.depth) > 0.01 * i.depth + 0.5)
                    discard;
                float3 c = i.color.rgb;
                float3 t = _tintColor.rgb * _tintColor.a;
                float lum = dot(c, float3(0.299, 0.587, 0.114));
                float3 v = _contrast * (lum + _saturation * (c - lum) - t) + t;
                float3 tex;
                if (_ReDefinitionAtlas > 0.5)
                    tex = i.atlas;
                else
                {
                    float hi = smoothstep(_highStart, _highEnd, i.color.a);
                    float lo = smoothstep(_lowStart, _lowEnd, i.color.a);
                    tex = _ReDefinitionLow.rgb * (1.0 - lo) + _ReDefinitionMid.rgb * (lo - hi) + _ReDefinitionHigh.rgb * hi;
                }
                float3 stock = lerp(lerp(v, v * tex, _ReDefinitionStockLerp), _ReDefinitionSteep.rgb, i.steep) * _ReDefinitionStockScale;
                float3 textured = lerp(tex, _ReDefinitionSteep.rgb, i.steep);
                float3 deferred;
                if (_ReDefinitionAtlas > 0.5)
                    deferred = textured * 1.75 * c;
                else
                {
                    float3 d = lerp(t, c, _contrast);
                    deferred = lerp(d, d * textured, 0.8);
                }
                deferred *= _ReDefinitionDeferredScale;
                return float4(clamp(deferred / max(stock, 1e-3), 0.0, 4.0), 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
