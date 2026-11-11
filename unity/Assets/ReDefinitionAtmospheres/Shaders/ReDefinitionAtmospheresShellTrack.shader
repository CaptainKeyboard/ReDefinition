// The track ribbons (src/Atmospheres/Cover/TrackDraw.cs, stage 2 of the plan): strips of the width of a tyre just above the ground that
// darken the cover where wheels and feet pressed it. One mesh, one draw; the vertex colour is the tint (rgb) and the darkness (a).
// The draw multiplies the lit frame (Blend DstColor Zero): the result is the scene colour times lerp(1, tint, a), so the strip needs
// no lighting of its own and follows whatever light the ground has (deferred or forward). It is drawn after the opaque queues and
// before the transparent ones (queue 2600), writes no depth and, being above 2500, writes no motion vector: the ground under it
// keeps its own, which is static world geometry. ZTest LEqual with Offset -2,-2 keeps it from fighting the ground within the
// centimetres the mesh lifts it; Parallax's displacement can still put the ground above the strip. [VERIFY in the game]
Shader "ReDefinition/AtmospheresShellTrack"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Geometry+600" "IgnoreProjector" = "True" "ForceNoShadowCasting" = "True" }
        Pass
        {
            Blend DstColor Zero
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -2, -2
            Lighting Off
            Fog { Mode Off }

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
                fixed3 tint = lerp(fixed3(1, 1, 1), i.color.rgb, i.color.a);
                return fixed4(tint, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
