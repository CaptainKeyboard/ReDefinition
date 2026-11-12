// The scene depth of the layer scene's mountain view (package T01): the linear eye depth (the distance along the camera's forward axis,
// as the game's depth texture holds it after LinearEyeDepth) of one occluder sphere, 0 where the ray misses it. The camera comes in as
// a position and a basis in the planet frame, the same uniforms as the layer pass. Not shipped: the file lives in the weather player's
// own Resources.
Shader "Hidden/ReDefinition/LayersDepth"
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

            float4 _CamPos, _CamForward, _CamRight, _CamUp;
            float4 _Occ;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 ndc = i.uv * 2 - 1;
                float3 d = normalize(_CamForward.xyz + ndc.x * _CamRight.xyz + ndc.y * _CamUp.xyz);
                float3 oc = _CamPos.xyz - _Occ.xyz;
                float b = dot(oc, d);
                float c = dot(oc, oc) - _Occ.w * _Occ.w;
                float disc = b * b - c;
                if (_Occ.w <= 0 || disc <= 0) return 0;
                float t = -b - sqrt(disc);
                if (t <= 0) return 0;
                return t * dot(d, normalize(_CamForward.xyz));
            }
            ENDCG
        }
    }
}
