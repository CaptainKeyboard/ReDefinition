// The raytracing's inputs, copied in the scene camera's OnRenderImage
// (src/Raytracing/RtCamera.cs): depth as Direct3D's clip-space z, the G-buffer as
// Unity's deferred path left it, the motion vectors, and the sky's cube map as a
// latitude-longitude image. Each pass writes one; the source of the blit is only
// there for its orientation. The last pass makes a material's small texture
// (src/Raytracing/RtScene.cs, ReadTexture) from its full-size one. Passes 7 to 10:
// Scatterer's ocean as a surface for the raytraced reflections, and their result as
// the reflections Scatterer's ocean reads (RtCamera, the ocean's reflections).
Shader "Hidden/ReDefinition/RtCapture"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    sampler2D _CameraGBufferTexture0;
    sampler2D _CameraGBufferTexture1;
    sampler2D _CameraGBufferTexture2;
    sampler2D_half _CameraMotionVectorsTexture;
    UNITY_DECLARE_TEXCUBE(_ReDefinitionRtCube);
    half4 _ReDefinitionRtCube_HDR;
    sampler2D _MainTex;
    float _ReDefinitionRtCutoff;

    // Scatterer's ocean G-buffer (OceanCommandBuffer, Scatterer 0.0908; read as
    // Deferred's screen-space reflections read it, ScreenSpaceReflections.shader,
    // CombineGbufferAndOcean): the depth as the camera's, the clear value where there is
    // no ocean; the normal's x and y, its variance over _VarianceMax.x and the sign of
    // its z.
    sampler2D oceanGbufferDepth;
    sampler2D oceanGbufferNormalsAndSigma;
    float2 _VarianceMax;
    // The raytraced reflections of the ocean (the proxy's specular signal): the light
    // arriving, linear, and the hit distance, FP16_MAX where the ray met nothing.
    sampler2D _ReDefinitionRtOcean;
    // Scatterer's tonemapper (Utils.SetToneMapping: 0 none, 1 Bruneton's, 2 Uncharted's).
    int TONEMAPPING_MODE;
    // The colour Scatterer's ocean multiplies its reflections with but not its own sun
    // glint (OceanNode.reflectionColor; src/Flight/OceanSunReflection.cs); w 0 where unknown.
    float4 _ReDefinitionOceanReflectionColor;

    bool NoOcean(float depth)
    {
    #if defined(UNITY_REVERSED_Z)
        return depth == 0.0;
    #else
        return depth == 1.0;
    #endif
    }

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2f vert(appdata_img v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }

    float fragDepth(v2f i) : SV_Target
    {
        return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
    }

    half4 fragGBuffer0(v2f i) : SV_Target { return tex2D(_CameraGBufferTexture0, i.uv); }
    half4 fragGBuffer1(v2f i) : SV_Target { return tex2D(_CameraGBufferTexture1, i.uv); }
    half4 fragGBuffer2(v2f i) : SV_Target { return tex2D(_CameraGBufferTexture2, i.uv); }

    float fragOceanDepth(v2f i) : SV_Target
    {
        return tex2D(oceanGbufferDepth, i.uv).r;
    }

    // As Unity's G-buffer 1: the specular colour, water's 0.02, and the smoothness from
    // the ocean's variance as Deferred converts it (its "purely empirical conversion").
    half4 fragOceanSurface1(v2f i) : SV_Target
    {
        if (NoOcean(tex2D(oceanGbufferDepth, i.uv).r))
            return 0.0;
        float sigmaSq = tex2D(oceanGbufferNormalsAndSigma, i.uv).z * _VarianceMax.x;
        float smoothness = 1.0 - saturate(sqrt(sqrt(sigmaSq) * 3.0 / 6.0));
        return half4(0.02, 0.02, 0.02, smoothness);
    }

    // As Unity's G-buffer 2: the world normal times a half plus a half.
    half4 fragOceanSurface2(v2f i) : SV_Target
    {
        if (NoOcean(tex2D(oceanGbufferDepth, i.uv).r))
            return half4(0.5, 1.0, 0.5, 1.0);
        float4 packed = tex2D(oceanGbufferNormalsAndSigma, i.uv);
        float3 normal;
        normal.xy = packed.xy * 2.0 - 1.0;
        normal.z = sqrt(1.0 - saturate(dot(normal.xy, normal.xy)));
        normal.z = packed.w > 0.0 ? normal.z : -normal.z;
        return half4(normal * 0.5 + 0.5, 1.0);
    }

    // Scatterer's ocean reads its reflections as a screen colour and turns it back into
    // its own light (Scatterer 0.0908, OceanWhiteCapsDeferred, its main lighting pass,
    // per tonemapper): ScattererLight is that inverse as it computes it, ScattererScreen
    // its tonemapper, which ScattererLight undoes.
    float3 ScattererLight(float3 v)
    {
        if (TONEMAPPING_MODE == 2)
        {
            float3 a = 0.114083 * v - 0.005;
            float3 root = sqrt(max(a * a - 0.05476 * v * (0.043351 * v - 0.0532), 0.0));
            float3 curve = (-a - root) / (0.38 * (0.228165 * v - 0.28));
            float3 line_ = (0.76055 * v - 0.930971) * 100000.0 + 1000.0;
            return (v > 1.224076 ? line_ : curve) / 3.0;
        }
        if (TONEMAPPING_MODE == 1)
        {
            float3 c = clamp(v, 0.0, 0.99);
            float3 low = pow(c, 2.2) * 2.609808;
            float3 high = -log(1.0 - c) + 0.000139;
            return v > 1.0 ? (v - 1.0) * 100000.0 + 20.0 : (low < 0.541343 ? low : high);
        }
        return v;
    }

    float3 ScattererScreen(float3 x)
    {
        if (TONEMAPPING_MODE == 2)
        {
            float3 y = 3.0 * x;
            float3 curve = (y * (0.57 * x + 0.05) + 0.004) / (y * (0.57 * x + 0.5) + 0.06) - 0.066667;
            float3 line_ = (y - 1000.0) * 0.00001 + 0.930971;
            return (x > 333.333344 ? line_ : curve) * 1.314838;
        }
        if (TONEMAPPING_MODE == 1)
        {
            float3 low = pow(x / 2.609808, 1.0 / 2.2);
            float3 high = 1.0 - exp(-(x - 0.000139));
            float3 beyond = 1.000001 + max(x - 20.0, 0.0) * 0.00001;
            return x < 0.541343 ? low : (x <= 4.6 ? high : beyond);
        }
        return x;
    }

    // The reflections as Deferred's ssrOutput gives them to the ocean, which moves its own
    // reflection of the sky towards them by their weight (alpha) and its Fresnel term.
    // Where a ray met the scene (w its distance, below 60000), what it met, as the frame's
    // colour, and the whole weight. Where it met nothing, only the sun's and the moons'
    // discs the waves mirror (RtCommon.hlsli, BodiesMirrored), as light added to the
    // ocean's own sky: a small weight, which keeps nearly all of that sky, and a colour
    // as much brighter in Scatterer's light, so that the weight times it is the discs'
    // light as the frame shows it -- past white as bright as it is: the sun in a wave
    // outshines the sky by far. The colour is past white then: the target is float.
    float4 fragOceanCompose(v2f i) : SV_Target
    {
        if (NoOcean(tex2D(oceanGbufferDepth, i.uv).r))
            return 0.0;
        float4 traced = tex2D(_ReDefinitionRtOcean, i.uv);
        float3 linearLight = max(traced.rgb, 0.0);
        float brightest = max(linearLight.r, max(linearLight.g, linearLight.b));
        float3 colour = linearLight / max(brightest, 1.0);
    #if defined(UNITY_COLORSPACE_GAMMA)
        colour = LinearToGammaSpace(colour);
    #endif
        if (traced.w < 60000.0)
            return float4(colour, 1.0);
        if (brightest <= 0.0)
            return 0.0;
        // Scatterer's light for the discs' brightness (their luminance as the frame's
        // colour), the light's own colour kept in proportion: Scatterer's inverse taken per
        // channel would tint a faint light towards its strongest channel.
        float luminance = dot(linearLight, float3(0.2126, 0.7152, 0.0722));
        float shown = min(luminance, 1.0);
    #if defined(UNITY_COLORSPACE_GAMMA)
        float screen = LinearToGammaSpace(shown.xxx).x;
    #else
        float screen = shown;
    #endif
        float3 light = linearLight * (ScattererLight(screen.xxx).x / max(shown, 1e-6));
        // The discs' glints stand in for Scatterer's sun glint, which its reflection colour
        // does not dim: taken back from them.
        if (_ReDefinitionOceanReflectionColor.w > 0.0)
            light /= max(_ReDefinitionOceanReflectionColor.rgb, 0.05);
        float weight = 0.05 * saturate(brightest / 0.0005);
        return float4(ScattererScreen(light / weight), weight);
    }

    half2 fragMotion(v2f i) : SV_Target
    {
        return tex2D(_CameraMotionVectorsTexture, i.uv).rg;
    }

    // x the angle around the vertical from the +z axis, y from straight up; as
    // RaytracingShaders/RtCommon.hlsli reads it (Sky). The first row in memory, where
    // the proxy starts, is Unity's top of a render texture: straight up (measured in
    // the raytracing test player).
    half4 fragEnvironment(v2f i) : SV_Target
    {
        float v = i.uv.y;
        float phi = (i.uv.x - 0.5) * 2.0 * UNITY_PI;
        float theta = (1.0 - v) * UNITY_PI;
        float3 direction = float3(sin(theta) * sin(phi), cos(theta), sin(theta) * cos(phi));
        half4 encoded = UNITY_SAMPLE_TEXCUBE_LOD(_ReDefinitionRtCube, direction, 2);
        return half4(DecodeHDR(encoded, _ReDefinitionRtCube_HDR), 1.0);
    }

    // A texel of a material's small texture from the full-size texture's texels under
    // it, read at full size in 8 x 8 points: a mip level's averaged alpha would blur a
    // cut-out's edge into the threshold. For a cut-out material (_ReDefinitionRtCutoff
    // above 0) the alpha is the share of points the cut-out keeps and the colour that of
    // those points alone, as only they are ever seen; otherwise both are the average.
    half4 fragMaterialTexture(v2f i) : SV_Target
    {
        float2 span = float2(abs(ddx(i.uv.x)), abs(ddy(i.uv.y)));
        float3 kept = 0.0;
        float3 all = 0.0;
        float covered = 0.0;
        float alpha = 0.0;
        [unroll]
        for (int y = 0; y < 8; y++)
        {
            [unroll]
            for (int x = 0; x < 8; x++)
            {
                float2 uv = i.uv + span * ((float2(x, y) + 0.5) / 8.0 - 0.5);
                half4 c = tex2Dlod(_MainTex, float4(uv, 0.0, 0.0));
                float keep = c.a >= _ReDefinitionRtCutoff ? 1.0 : 0.0;
                kept += c.rgb * keep;
                covered += keep;
                all += c.rgb;
                alpha += c.a;
            }
        }
        if (_ReDefinitionRtCutoff <= 0.0)
            return half4(all / 64.0, alpha / 64.0);
        return half4(covered > 0.0 ? kept / covered : all / 64.0, covered / 64.0);
    }
    ENDCG

    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGBuffer0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGBuffer1
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGBuffer2
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMotion
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragEnvironment
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMaterialTexture
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOceanDepth
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOceanSurface1
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOceanSurface2
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOceanCompose
            ENDCG }
    }
    Fallback Off
}
