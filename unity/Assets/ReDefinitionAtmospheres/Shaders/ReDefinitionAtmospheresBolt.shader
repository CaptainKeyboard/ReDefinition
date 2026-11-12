// The lightning bolt of ReDefinition Atmospheres (package T07): the branched channels of a flash as ribbons that turn to the camera.
//
// The mesh (src/Atmospheres/Render/Lightning/BoltMesh.cs) holds the polyline points of every strand twice, one vertex for each side of
// the ribbon. The vertex shader widens the ribbon across the view direction: the core is the channel's width in m but never narrower than
// a pixel and a half at any distance, the glow around it four times that. The fragment adds a bright core and a soft glow, scaled by the
// flash envelope (_BoltFade, the sum of the strokes' light at this moment) and the strand's brightness, and thinned by the haze between the
// bolt and the camera. The sum is added to the picture; the scene's depth hides the part of a channel behind terrain.
//
// Rendering statement: additive HDR colour, depth tested and not written, no motion vectors (a flash lives half a second and is not
// reprojected), no reactive mask (the flash history reset of the upscaler is requested by the module when a flash starts), not an input of
// the raytracing; the colour is in scene units, exposure follows the camera's.
Shader "ReDefinition/AtmospheresBolt"
{
    Properties
    {
        _BoltFade ("Flash level", Float) = 1
        _BoltTint ("Tint", Color) = (0.82, 0.88, 1.0, 1)
        _BoltHaze ("Haze per m", Float) = 0.00003
        _BoltPower ("Core power", Float) = 6
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Bolt"
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float _BoltFade;
            float4 _BoltTint;
            float _BoltHaze;
            float _BoltPower;

            // The contribution mask of atmoDebug (a copy of the function of Include/AtmoContribution.cginc: the bundle project resolves no
            // include from the Shaders folder; the include has no feature id for lightning yet, so the mask is the mode's blue for any id).
            float _AtmoDebug;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 tangent : NORMAL;
                float2 side : TEXCOORD0;     // x = -1 or +1 across the ribbon, y = 0 to 1 along the strand
                float2 shape : TEXCOORD1;    // x = the core width in m, y = the strand's brightness
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 across : TEXCOORD0;   // x = position across the ribbon -1..1, y = the core's share of the half width
                float2 light : TEXCOORD1;    // x = brightness times haze, y = unused
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 centre = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
                float3 along = normalize(mul((float3x3)unity_ObjectToWorld, v.tangent));
                float3 toCam = _WorldSpaceCameraPos - centre;
                float dist = length(toCam);
                float3 side = cross(along, toCam / max(dist, 1e-3));
                float sideLen = length(side);
                side = sideLen > 1e-4 ? side / sideLen : float3(0, 1, 0);

                // The size of a pixel in m at this distance: the projection's focal factor is unity_CameraProjection._m11.
                float pixelM = dist * 2.0 / (max(unity_CameraProjection._m11, 1e-3) * max(_ScreenParams.y, 1.0));
                float core = max(v.shape.x * 0.5, pixelM * 0.75);
                float reach = core * 4.0;
                float3 world = centre + side * v.side.x * reach;

                o.pos = UnityWorldToClipPos(float4(world, 1.0));
                o.across = float2(v.side.x, core / reach);
                // A channel thinner than a pixel and a half is drawn at that width and loses brightness by the ratio, so its energy follows its size.
                float sizeEnergy = lerp(0.5, 1.0, saturate(v.shape.x * 0.5 / (pixelM * 0.75)));
                o.light = float2(v.shape.y * sizeEnergy * exp(-dist * _BoltHaze), 0.0);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float x = abs(i.across.x);
                float core = i.across.y;
                float core01 = saturate(1.0 - x / max(core, 1e-3));
                float coreTerm = core01 * core01 * _BoltPower;
                float glow = exp(-x * x * 7.0) * 0.55;
                float3 colour = _BoltTint.rgb * (coreTerm + glow) * i.light.x * _BoltFade;
                float c = saturate(dot(colour, float3(0.33, 0.33, 0.33)));
                if (_AtmoDebug > 0.5 && _AtmoDebug < 1.5)
                    return float4(float3(0.0, 0.0, 1.0) * c, c);
                return float4(colour, c);
            }
            ENDCG
        }
    }
    Fallback Off
}
