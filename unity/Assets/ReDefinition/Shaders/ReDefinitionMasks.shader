// The masks ReDefinition hands FSR next to colour, depth and motion vectors
// (UpscalerMasks in the plugin). Built into redefinition.shaders by BundleBuilder.
Shader "Hidden/ReDefinition/Masks"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    SubShader
    {
        // 0: transparency and composition, over the whole image. Addressed by
        // pixel like pass 1: the sources are the camera's own render targets.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            // EVE's raymarched clouds, bound by EVE's own command buffer at
            // AfterForwardOpaque: premultiplied cloud colour, and in alpha the
            // transmittance -- EVE/CompositeRaymarchedClouds draws
            // background * alpha + colour.
            sampler2D scattererReconstructedCloud;
            // Scatterer's ocean G-buffer depth, cleared to the far value where no
            // ocean is drawn (OceanCommandBuffer).
            sampler2D oceanGbufferDepth;
            // Scatterer's own switch for its ocean on the camera rendering now.
            float ScattererOceanActiveOnCurrentCamera;
            float _ReDefinitionClouds;
            // The fade EVE sets on its cloud composite (cloudFade).
            float _ReDefinitionCloudFade;
            float _ReDefinitionOcean;
            float4 _ReDefinitionMaskTexel;

            fixed4 frag (v2f_img i) : SV_Target
            {
                float2 uv = i.pos.xy * _ReDefinitionMaskTexel.xy;
                float clouds = _ReDefinitionClouds * _ReDefinitionCloudFade
                               * (1.0 - tex2D(scattererReconstructedCloud, uv).a);
                float depth = tex2D(oceanGbufferDepth, uv).r;
            #if UNITY_REVERSED_Z
                float ocean = depth > 0.0 ? 1.0 : 0.0;
            #else
                float ocean = depth < 1.0 ? 1.0 : 0.0;
            #endif
                ocean *= _ReDefinitionOcean * ScattererOceanActiveOnCurrentCamera;
                return saturate(max(clouds, ocean)).xxxx;
            }
            ENDCG
        }

        // 1: reactive, one transparent renderer at a time. Layers over one
        // another composite like alpha, 1 - (1 - a)(1 - b): the new value times
        // one minus what is there, plus what is there. Occluded by the scene's
        // depth in the shader: a mask target paired with the camera's own depth
        // buffer draws nothing.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend OneMinusDstColor One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _ReDefinitionTint;
            float _ReDefinitionVertexColour;
            float _ReDefinitionAdditive;
            // The mask's own copy of the camera's depth, the size of the mask; and
            // one over that size.
            sampler2D _ReDefinitionSceneDepth;
            float4 _ReDefinitionMaskTexel;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = lerp(fixed4(1, 1, 1, 1), v.color, _ReDefinitionVertexColour);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Both textures are the camera's size and addressed by pixel, so
                // the lookup needs no flip on any graphics API.
                float scene = tex2D(_ReDefinitionSceneDepth, i.pos.xy * _ReDefinitionMaskTexel.xy).r;
                // In front of the scene, give or take a thousandth of the
                // distance: depth runs with one over the distance, so a fixed
                // tolerance would be kilometres wide far away.
            #if UNITY_REVERSED_Z
                clip(i.pos.z - scene * (1.0 - 1e-3));
            #else
                clip((1.0 - i.pos.z) - (1.0 - scene) * (1.0 - 1e-3));
            #endif
                fixed4 c = tex2D(_MainTex, i.uv) * i.color * _ReDefinitionTint;
                // AMD: the alpha a blended pixel is composited with; for additive
                // blending what it adds.
                float reactive = lerp(c.a, max(c.r, max(c.g, c.b)) * c.a, _ReDefinitionAdditive);
                return saturate(reactive).xxxx;
            }
            ENDCG
        }

        // 2: after every renderer, AMD's ceiling -- the smaller of the mask and
        // _ReDefinitionMax.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            BlendOp Min
            Blend One One

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _ReDefinitionMax;

            fixed4 frag (v2f_img i) : SV_Target
            {
                return _ReDefinitionMax.xxxx;
            }
            ENDCG
        }
    }
}
