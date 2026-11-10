// The weather player's fake sky: a gradient of the horizon colour and the zenith colour over the elevation, and nothing else,
// like an overcast day. The colours are fitted to the stored game frames (tools/analyze_weather_player.py --fit-sky).
Shader "Hidden/ReDefinition/WeatherPlayerSky"
{
    Properties
    {
        _Horizon ("Horizon", Color) = (0.62, 0.64, 0.68, 1)
        _Zenith ("Zenith", Color) = (0.36, 0.39, 0.45, 1)
        _Curve ("Curve", Float) = 0.5
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Horizon;
            float4 _Zenith;
            float _Curve;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.dir = vertex.xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float t = pow(saturate(d.y), _Curve);
                return float4(lerp(_Horizon.rgb, _Zenith.rgb, t), 1.0);
            }
            ENDCG
        }
    }
}
