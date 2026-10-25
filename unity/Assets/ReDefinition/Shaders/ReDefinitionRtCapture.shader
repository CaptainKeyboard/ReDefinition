// The raytracing's inputs, copied in the scene camera's OnRenderImage
// (src/Raytracing/RtCamera.cs): depth as Direct3D's clip-space z, the G-buffer as
// Unity's deferred path left it, the motion vectors, and the sky's cube map as a
// latitude-longitude image. Each pass writes one; the source of the blit is only
// there for its orientation. The last pass makes a material's small texture
// (src/Raytracing/RtScene.cs, ReadTexture) from its full-size one. Passes 7 to 10:
// Scatterer's ocean as a surface for the raytraced reflections, and their result as
// the reflections Scatterer's ocean reads (RtCamera, the ocean's reflections); pass 11
// the sun's and the moons' discs the waves mirror, added to the ocean Scatterer drew.
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
    // reflection of the sky towards them by their weight (alpha) and its Fresnel term:
    // where a ray met the scene (w its distance, below 60000), what it met, as the frame's
    // colour, and the whole weight; where it met nothing the weight 0, and the ocean keeps
    // its own reflection of the sky. The sun's and the moons' discs the waves mirror there
    // are added to the ocean once Scatterer has drawn it (fragOceanDiscs): Scatterer
    // weighs what it reads here down from 1 km of the camera's height over the sea and to
    // nothing from 2 km (its OceanWhiteCapsDeferred, disassembled: 1 - saturate((height -
    // 1000) / 1000) on the weight), where the discs would vanish from the sea.
    float4 fragOceanCompose(v2f i) : SV_Target
    {
        if (NoOcean(tex2D(oceanGbufferDepth, i.uv).r))
            return 0.0;
        float4 traced = tex2D(_ReDefinitionRtOcean, i.uv);
        if (traced.w >= 60000.0)
            return 0.0;
        float3 linearLight = max(traced.rgb, 0.0);
        float brightest = max(linearLight.r, max(linearLight.g, linearLight.b));
        float3 colour = linearLight / max(brightest, 1.0);
    #if defined(UNITY_COLORSPACE_GAMMA)
        colour = LinearToGammaSpace(colour);
    #endif
        return float4(colour, 1.0);
    }

    // The sun's and the moons' discs the waves mirror (RtCommon.hlsli, BodiesMirroredAt;
    // the traced reflections where a ray met nothing, w 60000 and more), added to the
    // ocean Scatterer drew, before its atmosphere draws over it: its pixel turned back
    // into Scatterer's light (ScattererLight), the discs' light added in its light as the
    // frame's colour gives it, and the sum through its tonemapper again. Water's Fresnel
    // term over the waves' variance as Scatterer takes it for its sky, 0.02 + 0.98 times
    // Bruneton's MeanFresnel ((1 - N.V) to the power 5 exp(-2.69 sigma), over 1 + 22.7
    // sigma^1.5): what it reads from ssrOutput it weighs by MeanFresnel alone (its
    // OceanWhiteCapsDeferred, disassembled), near 0 looking steeply down, where water
    // reflects 2 %. The discs' light as Scatterer's
    // light by its luminance as the frame's colour, its own colour kept in proportion:
    // Scatterer's inverse taken per channel would tint a faint light towards its strongest
    // channel.
    // The scene camera's screen position (-1 to 1, y up) to a world direction, without the
    // upscaler's jitter (RtCamera, AttachOceanDiscs).
    float4x4 _ReDefinitionOceanScreenToWorld;
    // The frame as Scatterer's ocean left it, copied (RtCamera, AttachOceanDiscs).
    sampler2D _ReDefinitionOceanFrame;

    struct v2fQuad
    {
        float4 pos : SV_POSITION;
        float4 screen : TEXCOORD0;
    };

    // The camera's full-screen quad (RtCamera, screenQuad): the screen's textures at the
    // pixel's place as the camera's projection has it.
    v2fQuad vertQuad(float4 vertex : POSITION)
    {
        v2fQuad o;
        o.pos = float4(vertex.xy, 0.0, 1.0);
        o.screen = ComputeScreenPos(o.pos);
        return o;
    }

    float4 fragOceanDiscs(v2fQuad i) : SV_Target
    {
        float2 uv = i.screen.xy / i.screen.w;
        float4 frame = tex2D(_ReDefinitionOceanFrame, uv);
        float oceanDepth = tex2D(oceanGbufferDepth, uv).r;
        if (NoOcean(oceanDepth))
            return frame;
        // Only where the ocean is in front: its G-buffer holds the ocean behind a vessel too,
        // where the scene's depth lies nearer (Direct3D's z reversed, nearer larger; the
        // player saw the sea's glints on a plane's parts).
        float sceneDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
    #if defined(UNITY_REVERSED_Z)
        if (sceneDepth > oceanDepth * 1.0001 + 1e-7)
            return frame;
    #else
        if (sceneDepth < oceanDepth * 0.9999 - 1e-7)
            return frame;
    #endif
        float4 traced = tex2D(_ReDefinitionRtOcean, uv);
        float3 linearLight = max(traced.rgb, 0.0);
        if (traced.w < 60000.0 || all(linearLight <= 0.0))
            return frame;
        float luminance = dot(linearLight, float3(0.2126, 0.7152, 0.0722));
        float shown = min(luminance, 1.0);
    #if defined(UNITY_COLORSPACE_GAMMA)
        float screen = LinearToGammaSpace(shown.xxx).x;
    #else
        float screen = shown;
    #endif
        float3 light = linearLight * (ScattererLight(screen.xxx).x / max(shown, 1e-6));

        float4 packed = tex2D(oceanGbufferNormalsAndSigma, uv);
        float3 normal;
        normal.xy = packed.xy * 2.0 - 1.0;
        normal.z = sqrt(1.0 - saturate(dot(normal.xy, normal.xy)));
        normal.z = packed.w > 0.0 ? normal.z : -normal.z;
        float sigma = sqrt(max(packed.z * _VarianceMax.x, 0.0));
        float4 far = mul(_ReDefinitionOceanScreenToWorld, float4(uv * 2.0 - 1.0, 1.0, 1.0));
        float3 ray = normalize(far.xyz / far.w);
        float nv = saturate(dot(-ray, normal));
        float fresnel = 0.02 + 0.98 * min(pow(1.0 - nv, 5.0 * exp(-2.69 * sigma)) / (1.0 + 22.7 * pow(sigma, 1.5)), 1.0);
        return float4(ScattererScreen(ScattererLight(frame.rgb) + fresnel * light), frame.a);
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

    // The surfaces as their materials mean them (RtCommon.hlsli, SurfaceIntent), into the
    // G-buffer's specular colour and smoothness (RtCamera, the surfaces written back).
    sampler2D _ReDefinitionRtSurface;

    half4 fragSurface(v2fQuad i) : SV_Target
    {
        return tex2D(_ReDefinitionRtSurface, i.screen.xy / i.screen.w);
    }

    // The surfaces' overlay (RtCamera.SurfaceView), over the finished picture: each surface's
    // shine as its material means it. Dark grey matt; blue to green to yellow to red from
    // rough to smooth, brighter the stronger it reflects; striped where the G-buffer's own
    // shine was kept (Unity's physically based or an unknown shader), striped across for the
    // space centre's ground, striped down with magenta where no material was found at the
    // pixel, each by the shine it gets; cyan KSP's sea; the sky as it is.
    sampler2D _ReDefinitionRtSurfaceMode;

    // The terrain past Parallax's grass in a meadow's shade, and the space centre's lawn as bright as the
    // terrain beside it (RtCommon.hlsli, MeadowShade, in the surface mode's y), multiplied into the
    // G-buffer's albedo: a distant meadow's darker colour, for the sun's light and the sky's alike; and the
    // lawn's occlusion as the terrain's (RtCommon.hlsli, LawnOcclusion, in w) into the G-buffer's. 1 where
    // the raytracing found no surface. Half the factors, which pass 16 blends in twice, up to 2.
    half4 fragMeadow(v2fQuad i) : SV_Target
    {
        float4 found = tex2D(_ReDefinitionRtSurfaceMode, i.screen.xy / i.screen.w);
        half shade = found.a < 0.5 ? 1.0 : clamp(found.g, 0.0, 2.0);
        half occlusion = found.a < 0.5 ? 1.0 : saturate(found.a);
        return half4(shade, shade, shade, occlusion) * 0.5;
    }

    half4 fragSurfaceView(v2fQuad i) : SV_Target
    {
        float2 uv = i.screen.xy / i.screen.w;
        float4 surface = tex2D(_ReDefinitionRtSurface, uv);
        float4 found = tex2D(_ReDefinitionRtSurfaceMode, uv);
        if (found.a < 0.5)
            return half4(0.0, 0.0, 0.0, 0.0);
        float mode = found.r * 8.0;
        float2 pixel = uv * _ScreenParams.xy;
        float strength = max(surface.r, max(surface.g, surface.b));
        half3 colour;
        if (mode > 4.5 && mode < 5.5)
            colour = half3(0.0, 0.8, 1.0);
        else if (strength <= 1e-4 && (mode > 0.5 && mode < 1.5 || mode > 5.5))
            colour = half3(0.12, 0.12, 0.14);
        else
        {
            float s = saturate(surface.a);
            colour = s < 0.33 ? lerp(half3(0.1, 0.2, 1.0), half3(0.1, 1.0, 0.2), s / 0.33)
                   : s < 0.66 ? lerp(half3(0.1, 1.0, 0.2), half3(1.0, 1.0, 0.1), (s - 0.33) / 0.33)
                   : lerp(half3(1.0, 1.0, 0.1), half3(1.0, 0.1, 0.05), (s - 0.66) / 0.34);
            colour *= 0.3 + 0.7 * saturate(sqrt(strength * 4.0));
            // Striped where the G-buffer's own shine was kept, across for the space centre's ground,
            // down where no material was found at the pixel.
            if (mode < 0.5 && frac((pixel.x + pixel.y) / 16.0) < 0.5)
                colour *= 0.4;
            else if (mode > 6.5 && frac(pixel.y / 8.0) < 0.5)
                colour *= 0.4;
            else if (mode > 5.5 && mode < 6.5 && frac(pixel.x / 8.0) < 0.5)
                colour = lerp(colour, half3(1.0, 0.0, 1.0), 0.6);
        }
        return half4(colour, 1.0);
    }

    // Pass 14, after the G-buffer, from a copy of the light buffer: KSP's flat ambient
    // light taken out where a surface's own G-buffer pass put it there, its albedo times
    // RenderSettings.ambientLight -- Unity's for the shaders Deferred does not replace;
    // the space centre's flag (a SkinnedMeshRenderer) and the launch pad's grate at night
    // held 0.19 there, 0.24 times their albedo, every other surface none (measured
    // 2026-10-19). The raytraced diffuse light comes in its place in the reflections pass
    // (ReDefinitionDeferredReflections.shader), as for every other surface. Only where the
    // light buffer holds that and nothing else, within a tenth: an emissive surface keeps
    // its light.
    half4 _ReDefinitionFlatAmbient;

    half4 fragUnbakeAmbient(v2f i) : SV_Target
    {
        half4 light = tex2D(_MainTex, i.uv);
        half3 baked = tex2D(_CameraGBufferTexture0, i.uv).rgb * _ReDefinitionFlatAmbient.rgb;
        bool holds = max(baked.r, max(baked.g, baked.b)) > 0.01 && all(abs(light.rgb - baked) <= baked * 0.1 + 0.002);
        return half4(holds ? max(light.rgb - baked, 0.0) : light.rgb, light.a);
    }

    // Pass 15, after the sun's screen-space shadows, into two texels: how Unity fades its
    // shadow out towards the shadow distance in forward rendering, as it set it for them
    // (UnityShadowLibrary.cginc, UnityComputeShadowFade): _LightShadowData, whose z and w
    // are the fade's scale and bias, and unity_ShadowFadeCenterAndType. Only for the
    // raytraced camera: the sun's other cameras leave what it wrote.
    float _ReDefinitionRtOn;

    float4 fragShadowFade(v2f i) : SV_Target
    {
        if (_ReDefinitionRtOn < 0.5)
            discard;
        return i.uv.x < 0.5 ? _LightShadowData : unity_ShadowFadeCenterAndType;
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
        Pass { CGPROGRAM
            #pragma vertex vertQuad
            #pragma fragment fragOceanDiscs
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vertQuad
            #pragma fragment fragSurface
            ENDCG }
        Pass {
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vertQuad
            #pragma fragment fragSurfaceView
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragUnbakeAmbient
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragShadowFade
            ENDCG }
        // Pass 16: the meadow's shade into the G-buffer's albedo, the lawn's occlusion into its alpha:
        // each times twice the output, so that the factor may lift the albedo too (the space centre's lawn).
        Pass {
            Blend DstColor SrcColor
            ColorMask RGBA
            CGPROGRAM
            #pragma vertex vertQuad
            #pragma fragment fragMeadow
            ENDCG }
    }
    Fallback Off
}
