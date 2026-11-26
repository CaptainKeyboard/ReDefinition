// The depth the own cloud pass limits its march with in the game (package T09): the camera's depth texture as the linear distance along the camera's
// forward axis in m (the unit of _SceneDepth in ReDefinitionAtmospheresLayers.shader), 0 where the pixel is sky. The weather player draws
// the same quantity from an analytic occluder; the game has the depth texture Unity makes for the camera.
Shader "ReDefinition/AtmospheresLayerDepth"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            // The greatest depth in m that counts (0: none; master plan 46.4, PRUEFPROTOKOLL 349). While the body's ground is not drawn in flight (its PQS is off and the
            // planet is the scaled one, which is not in this depth), only vessels are local, and none is drawn beyond the range at which KSP unloads them. After a
            // jump back from far KSP kept a ground tile in the depth texture (260 to 446 km away, nothing of it in the picture) and the clouds were cut in it.
            float _LayerDepthMaxM;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float frag(v2f i) : SV_Target
            {
                float raw = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(i.uv, 0, 0));
                // The sky is the far plane: 0 with the reversed depth of Direct3D 11, 1 otherwise. The march takes 0 as "no limit".
            #if defined(UNITY_REVERSED_Z)
                if (raw <= 1e-7) return 0;
            #else
                if (raw >= 1.0 - 1e-7) return 0;
            #endif
                float z = LinearEyeDepth(raw);
                if (_LayerDepthMaxM > 0.0 && z > _LayerDepthMaxM) return 0;
                return z;
            }
            ENDCG
        }
    }
}
