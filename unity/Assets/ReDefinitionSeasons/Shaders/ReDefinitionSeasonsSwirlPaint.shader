// Draws the storm swirl into the picture of EVE's 2D cloud layer (src/Seasons/EveLayerStorm.cs), a render texture, with GL.
// The vertex colour is the colour and the opacity of the swirl at that vertex.
//
// Pass 0 paints: the colour replaces the picture's colour where the swirl is opaque and mixes with it towards the rim, and the
// picture's opacity grows to the swirl's (a + dst * (1 - a)). Pass 1 erases: the colour stays, the picture's opacity is lowered by the
// vertex opacity. Together they make the swirl independent of what the picture has under it: a lane can be clear sky over a cloud.
Shader "ReDefinition/SeasonsSwirlPaint"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always
        Lighting Off
        Fog { Mode Off }

        Pass
        {
            Name "PAINT"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }

        Pass
        {
            Name "ERASE"
            ColorMask A
            Blend Zero OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}
