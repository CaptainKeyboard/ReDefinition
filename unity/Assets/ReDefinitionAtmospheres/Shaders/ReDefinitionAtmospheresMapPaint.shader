// The cloud-map painter's passes (src/Atmospheres/Eve/MapPainter.cs). Each pass renders the whole target with Graphics.Blit; the source
// (_MainTex) is the field's map uploaded as a texture (Repeat in U, Clamp in V, bilinear), the target a render texture of the layer's
// map (R8 for coverage and type, RGBA for the orbit picture).
//
// The layout is EVE's (Utils/TextureWrapper.cs:596-604): u = 0.5 + atan2(x, z) / 2 pi, v = acos(y) / pi in the layer's frame; a field map
// (row 0 north, column 0 at longitude -180) differs by a longitude shift and, if the layer's longitude runs the other way, a mirror:
//   field u = frac(layer u + shift)          (_Mirror 0)
//   field u = frac(1 - layer u + shift)      (_Mirror 1)    shift in turns (degrees / 360)
// This is EveMapLayout.FieldU; MapPaintModel is the CPU twin of pass 0 and 1 (tests compare them).
//
// Pass 0 COVERAGE: the target moves toward the field's coverage by at most _MaxDelta per update (the slew limit, MapSlew). _Prev is a
//   copy of the target made before the pass. _Snap = 1 sets the target at once. Like MapSlew.StepByte, a texel within max(1 level,
//   _MaxDelta) of its target takes it.
// Pass 1 TYPE: the field's precipitation level goes through _Lut (CloudTypeEncoding.BuildLut, 256 x 1 point filtered) to the cloud type
//   value, then the same limit.
// Pass 2 ORBIT: the orbit and map picture: grey cloud with the opacity of the coverage and the level (OrbitPicture.OpacityAt, GreyAt),
//   the swirl cells of _Overlay (RGBA, layer frame) over it. The output is the _MainTex of EVE's 2D layer and the own shell's picture.
Shader "ReDefinition/AtmospheresMapPaint"
{
    Properties
    {
        _MainTex ("Field", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Lighting Off
        Fog { Mode Off }

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        sampler2D _Prev;
        sampler2D _Lut;
        sampler2D _Level;
        sampler2D _Overlay;
        float _Shift;
        float _Mirror;
        float _MaxDelta;
        float _Snap;
        float _Faint;
        float _SwirlFrom;
        float _GreyScale;

        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        v2f vert(appdata v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.uv;
            return o;
        }

        float2 FieldUv(float2 uv)
        {
            float u = _Mirror > 0.5 ? 1.0 - uv.x : uv.x;
            return float2(u + _Shift, uv.y);
        }

        float Slew(float prev, float target)
        {
            float d = target - prev;
            float stepSize = max(1.0 / 255.0, _MaxDelta);
            float limited = prev + clamp(d, -_MaxDelta, _MaxDelta);
            float result = abs(d) <= stepSize ? target : limited;
            return _Snap > 0.5 ? target : result;
        }

        float OpacityAt(float level)
        {
            if (level >= _SwirlFrom) return 1.0;
            float t = saturate(level / _SwirlFrom);
            t = t * t * (3.0 - 2.0 * t);
            return _Faint + (1.0 - _Faint) * t;
        }
        ENDCG

        Pass
        {
            Name "COVERAGE"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag(v2f i) : SV_Target
            {
                float target = tex2D(_MainTex, FieldUv(i.uv)).r;
                float prev = tex2D(_Prev, i.uv).r;
                return fixed4(Slew(prev, target), 0, 0, 0);
            }
            ENDCG
        }

        Pass
        {
            Name "TYPE"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag(v2f i) : SV_Target
            {
                float level = tex2D(_MainTex, FieldUv(i.uv)).r;
                float index = floor(saturate(level) * 255.0 + 0.5);
                float target = tex2D(_Lut, float2((index + 0.5) / 256.0, 0.5)).r;
                float prev = tex2D(_Prev, i.uv).r;
                return fixed4(Slew(prev, target), 0, 0, 0);
            }
            ENDCG
        }

        Pass
        {
            Name "ORBIT"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag(v2f i) : SV_Target
            {
                float2 fuv = FieldUv(i.uv);
                float coverage = tex2D(_MainTex, fuv).r;
                float level = tex2D(_Level, fuv).r;
                float grey = 0.93 - 0.25 * saturate(level);
                float alpha = coverage * OpacityAt(level);
                float4 swirl = tex2D(_Overlay, i.uv);
                float3 rgb = lerp(float3(grey, grey, grey), swirl.rgb, swirl.a) * _GreyScale;
                float a = swirl.a + alpha * (1.0 - swirl.a);
                return fixed4(rgb, a);
            }
            ENDCG
        }
    }
}
