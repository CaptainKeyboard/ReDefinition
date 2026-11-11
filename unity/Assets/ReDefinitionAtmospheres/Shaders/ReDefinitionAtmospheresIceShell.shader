// The sea ice in the orbit and map view (src/Atmospheres/Ice/IceOrbitLayer.cs): a sphere in scaled space slightly above the planet that
// draws the ice picture (rgba, alpha the opacity of the ice; made on a worker thread by IceOrbitPicture from the field's ice fraction
// and thickness, the coast map and the same floe pattern as the near-ground overlay) over the planet. The picture is in the field's
// map layout (column 0 at longitude -180, row 0 at the south in the texture); a pixel's direction on the sphere gives the layer's
// coordinates (u = 0.5 + atan2(x, z) / 2 pi, EVE's formula), and _Shift / _Mirror move them to the field's layout the way
// EveMapLayout does for the cloud shell. Where the picture is opaque the planet's sun glint is hidden, because the ice is drawn over it.
// Lit by the sun direction with a floor of _Ambient on the night side. After the planet, before the cloud shell; no depth write.
Shader "ReDefinition/AtmospheresIceShell"
{
    Properties
    {
        _Picture ("Picture", 2D) = "black" {}
        _Shift ("Shift (turns)", Float) = 0
        _Mirror ("Mirrored", Float) = 0
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Ambient ("Night side", Range(0, 1)) = 0.12
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Back
        ZWrite Off
        Lighting Off
        Fog { Mode Off }
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _Picture;
            float _Shift;
            float _Mirror;
            float _Opacity;
            float _Ambient;
            float4 _SunDir;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 objectDir : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.objectDir = v.vertex.xyz;
                o.worldNormal = normalize(mul((float3x3)unity_ObjectToWorld, v.vertex.xyz));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.objectDir);
                float layerU = 0.5 + atan2(n.x, n.z) / 6.28318530718;
                float fieldU = _Mirror > 0.5 ? 0.5 - (layerU - 0.5) + _Shift : layerU + _Shift;
                fieldU = frac(fieldU);
                // The picture's first row is the south pole: v runs with the latitude.
                float v = 0.5 + asin(clamp(n.y, -1.0, 1.0)) / 3.14159265359;
                float4 picture = tex2Dlod(_Picture, float4(fieldU, v, 0, 0));
                float lit = max(_Ambient, saturate(dot(normalize(i.worldNormal), normalize(_SunDir.xyz)) * 1.6 + 0.2));
                return fixed4(picture.rgb * lit, picture.a * _Opacity);
            }
            ENDCG
        }
    }
    Fallback Off
}
