// The space centre's ground as Deferred colours it against KSP's own shader, per pixel,
// for the forward light without Deferred (src/Raytracing/RtCamera.cs, RtDeferredLook.cs):
// Deferred's ground shader multiplies the tarmac by _TarmacColor, at most 0.89, which
// KSP's own leaves out; and takes _NearGrassTexture at every distance, where KSP's own
// blends towards _FarGrassTexture by smoothstep(0, _FarGrassBlendDistance, distance) --
// by their averages, _ReDefinitionNearGrass over the mix of it and _ReDefinitionFarGrass.
// Both mixed by _BlendMaskTexture's red at uv1, and times Deferred's diffuse share of its
// metallic setup, _ReDefinitionDeferredScale. Drawn with each ground renderer into a
// target cleared to white, where the camera's depth shows that surface;
// ReDefinitionRtForwardLight.shader's relight pass multiplies the frame's diffuse light
// by it.
Shader "Hidden/ReDefinition/RtTarmac"
{
    Properties
    {
        _BlendMaskTexture ("Blend mask", 2D) = "black" {}
        _ReDefinitionTarmac ("Tarmac factor", Float) = 1.0
        _ReDefinitionNearGrass ("Near grass average", Color) = (1, 1, 1, 1)
        _ReDefinitionFarGrass ("Far grass average", Color) = (1, 1, 1, 1)
        _FarGrassBlendDistance ("Far grass distance", Float) = 1.0
        _ReDefinitionDeferredScale ("Deferred's specular share", Float) = 1.0
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
            #include "UnityCG.cginc"

            sampler2D _BlendMaskTexture;
            float4 _BlendMaskTexture_ST;
            float _ReDefinitionTarmac;
            float4 _ReDefinitionNearGrass;
            float4 _ReDefinitionFarGrass;
            float _FarGrassBlendDistance;
            float _ReDefinitionDeferredScale;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv2 : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 screen : TEXCOORD1;
                float depth : TEXCOORD2;
                float3 world : TEXCOORD3;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv2, _BlendMaskTexture);
                o.screen = ComputeScreenPos(o.pos);
                COMPUTE_EYEDEPTH(o.depth);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Only where this surface is the one the camera sees.
                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.screen.xy / i.screen.w));
                if (abs(scene - i.depth) > 0.01 * i.depth + 0.2)
                    discard;
                float tarmac = tex2D(_BlendMaskTexture, i.uv).r;
                float far = smoothstep(0.0, _FarGrassBlendDistance, distance(_WorldSpaceCameraPos, i.world));
                float3 stock = lerp(_ReDefinitionNearGrass.rgb, _ReDefinitionFarGrass.rgb, far);
                float3 grass = _ReDefinitionNearGrass.rgb / max(stock, 1e-3);
                return float4(lerp(grass, _ReDefinitionTarmac.xxx, tarmac) * _ReDefinitionDeferredScale, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
