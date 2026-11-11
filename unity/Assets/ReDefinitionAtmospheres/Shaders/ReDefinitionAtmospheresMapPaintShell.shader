// The own 2D cloud shell (src/Atmospheres/Eve/OwnShell.cs): a sphere in scaled space that draws the orbit picture (the ORBIT pass of
// ReDefinition/AtmospheresMapPaint, in the field's own frame) over the planet in the orbit and map view, for bodies whose clouds are
// ours (cloudSource Ours) or that have no EVE 2D layer. The picture is looked up per pixel from the direction of the pixel on the
// sphere (u = 0.5 + atan2(x, z) / 2 pi, v = acos(y) / pi, the layout of EveMapLayout), so no seam is interpolated across vertices.
// Lit by the sun direction _SunDir (world space) with a floor of _Ambient on the night side. Draws after the planet, no depth write.
Shader "ReDefinition/AtmospheresMapPaintShell"
{
    Properties
    {
        _Picture ("Picture", 2D) = "black" {}
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Ambient ("Night side", Range(0, 1)) = 0.12
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
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
                float u = 0.5 + atan2(n.x, n.z) / 6.28318530718;
                float v = acos(clamp(n.y, -1.0, 1.0)) / 3.14159265359;
                float4 picture = tex2Dlod(_Picture, float4(u, v, 0, 0));
                float lit = max(_Ambient, saturate(dot(normalize(i.worldNormal), normalize(_SunDir.xyz)) * 1.6 + 0.2));
                return fixed4(picture.rgb * lit, picture.a * _Opacity);
            }
            ENDCG
        }
    }
}
