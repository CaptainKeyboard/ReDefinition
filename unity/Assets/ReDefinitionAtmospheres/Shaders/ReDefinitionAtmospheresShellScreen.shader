// Screen-space vehicle cover (src/Atmospheres/Cover/CoverScreenPass.cs, method ScreenSpace, variant (f) of the plan): after the opaque
// pass, the pixels inside a vessel's region whose reconstructed face looks up get the cover colour. The world position comes from the
// depth texture and the camera's projection, the face from the derivatives of that position. No mesh, no second renderer: whatever
// shader a part uses, its pixels are the ones painted, and the upscaler's motion vectors are the part's own (the pass changes colour
// only). Limits: the face is the screen's, so thin parts seen edge-on fade out (depth-jump guard); the specks are fixed to the
// vessel's middle in world axes and slide when the vessel turns; the light is the sun and the ambient colour handed in, without
// shadows. A pixel the vessel's region contains but the vessel does not draw (ground beside it above the floor) is covered too.
// Orientation: SV_POSITION and the depth texture are both taken top-left on Direct3D (UNITY_UV_STARTS_AT_TOP) and the sign of
// _ProjectionParams.x tells a flipped projection. [VERIFY on the game's camera with the upscaler's jittered projection]
Shader "ReDefinition/AtmospheresShellScreen"
{
    Properties
    {
        _CoverColor ("Cover colour", Color) = (0.93, 0.95, 0.98, 1)
        _EdgeLow ("Mask low", Range(0, 1)) = 0.35
        _EdgeHigh ("Mask high", Range(0, 1)) = 0.75
        _Grain ("Grain scale", Float) = 40
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D_float _CameraDepthTexture;
            fixed4 _CoverColor;
            float _EdgeLow;
            float _EdgeHigh;
            float _Grain;
            float4 _CoverUp;
            float4 _CoverSunDir;
            float4 _CoverSunColor;
            float4 _CoverAmbient;
            float _CoverCount;
            // 0 is the cover; 1..6 write what the pass computes instead, for the player's diagnosis (1 flat magenta, 2 the world
            // position about _CoverDebugOrigin, 3 the face normal, 4 the region test, 5 the raw depth, the eye depth and the row,
            // 6 the depth parameters).
            float _CoverDebug;
            float4 _CoverDebugOrigin;
            // xyz the region's middle in world space, w its radius; x of _CoverState the lower limit along up, y the amount.
            float4 _CoverSphere[4];
            float4 _CoverState[4];

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // The triangle is given in clip space and covers the screen.
                // z in the middle of the clip range: not on a clip plane, whatever the depth direction.
                o.pos = float4(v.vertex.xy, 0.5, 1.0);
                return o;
            }

            float Hash(float3 p)
            {
                p = floor(p * _Grain);
                return frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_CoverDebug > 0.5 && _CoverDebug < 1.5) return fixed4(1, 0, 1, 1);
                float2 uv = i.pos.xy / _ScreenParams.xy;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                #if defined(UNITY_REVERSED_Z)
                if (raw <= 0.0) discard;
                #else
                if (raw >= 1.0) discard;
                #endif
                float d = LinearEyeDepth(raw);
                if (_CoverDebug > 4.5 && _CoverDebug < 5.5) return fixed4(raw * 10.0, d / 100.0, uv.y, 1);
                if (_CoverDebug > 5.5) return fixed4(_ZBufferParams.x / 1000.0, _ZBufferParams.y, _ZBufferParams.w, 1);
                float ndcX = uv.x * 2.0 - 1.0;
                #if defined(UNITY_UV_STARTS_AT_TOP)
                float ndcY = (1.0 - uv.y * 2.0) * _ProjectionParams.x;
                #else
                float ndcY = uv.y * 2.0 - 1.0;
                #endif
                // View space looks down -z; clip.x = m00 x + m02 z, w = -z, so x = (ndc.x + m02) d / m00 (and the same for y).
                float3 viewPos = float3((ndcX + unity_CameraProjection._m02) * d / unity_CameraProjection._m00,
                                        (ndcY + unity_CameraProjection._m12) * d / unity_CameraProjection._m11, -d);
                // The camera's axes in the world are the rows of the view matrix (looking down -z): right, up and minus forward. The player
                // measured unity_CameraToWorld with this reconstruction: the point came out behind the camera, so the matrix is not used.
                float3 wp = _WorldSpaceCameraPos + UNITY_MATRIX_V[0].xyz * viewPos.x + UNITY_MATRIX_V[1].xyz * viewPos.y - UNITY_MATRIX_V[2].xyz * d;
                if (_CoverDebug > 1.5 && _CoverDebug < 2.5) return fixed4(saturate((wp - _CoverDebugOrigin.xyz) / 16.0 + 0.5), 1);

                // Which vessel's region the pixel is in, and how much that vessel carries.
                float3 up = normalize(_CoverUp.xyz);
                float amount = 0.0;
                float3 centre = float3(0, 0, 0);
                for (int k = 0; k < 4; k++)
                {
                    if (k >= (int)(_CoverCount + 0.5)) break;
                    float3 r = wp - _CoverSphere[k].xyz;
                    float inside = step(dot(r, r), _CoverSphere[k].w * _CoverSphere[k].w) * step(_CoverState[k].x, dot(r, up));
                    if (inside * _CoverState[k].y > amount)
                    {
                        amount = inside * _CoverState[k].y;
                        centre = _CoverSphere[k].xyz;
                    }
                }
                if (_CoverDebug > 3.5 && _CoverDebug < 4.5) return fixed4(amount, step(0.0001, amount), 0, 1);
                if (amount <= 0.0) discard;

                // The face from the derivatives of the position, turned to the camera; edges (depth jumps) are left out.
                float3 dx = ddx(wp);
                float3 dy = ddy(wp);
                float3 n = normalize(cross(dy, dx));
                if (dot(n, _WorldSpaceCameraPos - wp) < 0.0) n = -n;
                float jump = max(abs(ddx(d)), abs(ddy(d)));
                float edgeOk = step(jump, max(0.5, 0.25 * d));
                if (_CoverDebug > 2.5 && _CoverDebug < 3.5) return fixed4(n * 0.5 + 0.5, 1);
                float mask = smoothstep(_EdgeLow, _EdgeHigh, dot(n, up)) * edgeOk;

                // The specks, as the shell's: visible where the share (amount x mask) is larger than the grain.
                float grain = Hash(wp - centre);
                float visible = step(grain * 0.999 + 0.001, amount * mask);
                if (visible <= 0.0) discard;

                float ndl = saturate(dot(n, _CoverSunDir.xyz));
                fixed3 lit = _CoverColor.rgb * (_CoverAmbient.rgb + _CoverSunColor.rgb * ndl);
                return fixed4(lit, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
