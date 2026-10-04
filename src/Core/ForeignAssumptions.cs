using System.Collections.Generic;

namespace ReDefinition.Core
{
    // Everything ReDefinition takes for granted about other mods' code, in one
    // place: each with what it names, the code it was checked against, and what
    // happens where that changed (ForeignAssumption). The classes that rely on one
    // ask it (Holds) before they do; ForeignAssumptionReport says what came of each
    // in KSP.log and in the Debug tab, and tools/check_foreign_code.ps1 checks the
    // whole catalogue against the installed mods outside the game.
    //
    // The fingerprints are those of the builds named beside each; a new build of a
    // mod whose code changed shows here as Changed until its new code has been read
    // and the fingerprint added.
    internal static class ForeignAssumptions
    {
        private const string Camera = "UnityEngine.Camera";
        private const string Material = "UnityEngine.Material";
        private const string Vector3Type = "UnityEngine.Vector3";
        private const string RenderTexture = "UnityEngine.RenderTexture";
        private const string Shader = "UnityEngine.Shader";
        private const string Bool = "System.Boolean";
        private const string Int = "System.Int32";
        private const string Double = "System.Double";
        private const string Float = "System.Single";

        private const string EveClouds = "Atmosphere.DeferredRaymarchedVolumetricCloudsRenderer";
        private const string EveToScreen = "Atmosphere.DeferredRaymarchedRendererToScreen";
        private const string EveProperties = "Utils.ShaderProperties";
        private const string Godrays = "Scatterer.RaymarchedGodraysRenderer";
        private const string ScattererTaa = "Scatterer.TemporalAntiAliasing";

        // EVE 3.2.2 (volumetric build, Atmosphere.dll): while it prepares a camera's
        // clouds in OnPreRender it takes the projection without jitter from
        // VRUtils.GetNonJitteredProjectionMatrixForCamera for its rays' reprojection
        // and keeps it for the next frame (EveCloudMotion). A postfix hands it the
        // jittered one only inside OnPreRender: where OnPreRender does something else
        // with it, the clouds could be misplaced.
        public static readonly ForeignAssumption EveCloudJitter =
            new ForeignAssumption("eve-cloud-jitter", "EVE", "EVE's volumetric clouds take the upscaler's jitter")
                .Type(EveClouds).Type("Utils.VRUtils")
                .Method(EveClouds, "OnPreRender")
                .Method("Utils.VRUtils", "GetNonJitteredProjectionMatrixForCamera", Camera)
                .Field(EveClouds, "targetCamera", Camera)
                .Tested("6f92b507f52f") // EVE 3.2.2
                .OnChange(WhenChanged.Fallback, "the clouds render without the jitter, as EVE renders them");

        // EVE binds the reconstruction's outputs -- colour, motion vectors, distance --
        // and Scatterer's reconstruction writes the motion vectors as the change in
        // viewport position, below -1 where there is none (Scatterer-EVE/
        // ReconstructRaymarchedClouds, disassembled from Scatterer 0.908's
        // scatterershaders-directx). CloudMotionVectors reads them. A changed
        // binding or shader could hand it something else under the same name.
        public static readonly ForeignAssumption EveCloudMotion =
            new ForeignAssumption("eve-cloud-motion", "EVE", "the clouds' own motion vectors in the upscaler's")
                .Type(EveClouds).Type(EveProperties)
                .Method(EveClouds, "HandleRenderingCommands")
                .Method(EveClouds, "InitRenderTextures")
                .Field(EveClouds, "reconstructCloudShader", Shader)
                .Literal(EveProperties, "scattererReconstructedCloud", "scattererReconstructedCloudMotionVectors")
                .Tested("272d0883c397") // EVE 3.2.2
                .OnChange(WhenChanged.Fallback, "the clouds' motion is worked out from their layer's shell");

        // The other half of it: Scatterer's reconstruction shader, which only its
        // version can tell.
        public static readonly ForeignAssumption ScattererCloudReconstruction =
            new ForeignAssumption("scatterer-cloud-reconstruction", "Scatterer",
                                  "the motion vectors Scatterer's cloud reconstruction writes")
                .Type("Scatterer.ShaderReplacer")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the clouds' motion is worked out from their layer's shell");

        // EVE's cloud composite: its fade and the cloud shell's radii, which EVE
        // sets on the material only, so a renamed one would read as 0 unseen.
        public static readonly ForeignAssumption EveCloudComposite =
            new ForeignAssumption("eve-cloud-composite", "EVE", "the clouds' fade and shell in the upscaler's masks")
                .Type(EveClouds).Type(EveToScreen).Type(EveProperties)
                .Field(EveClouds, "deferredRaymarchedRendererToScreen", EveToScreen)
                .Field(EveToScreen, "compositeColorMaterial", Material)
                .Literal(EveProperties, "cloudFade", "innerSphereRadius", "outerSphereRadius")
                .OnChange(WhenChanged.Keep, "the clouds count as fully faded in, and their motion comes from"
                                            + " EVE's motion vectors alone");

        // EVE's screen-space cloud shadows and its renderers, sized anew for the
        // upscaler's cameras (EveCompatibility).
        public static readonly ForeignAssumption EveShadows =
            new ForeignAssumption("eve-shadows", "EVE", "EVE's cloud shadows and clouds sized for the upscaler")
                .Type(EveClouds).Type("Atmosphere.ScreenSpaceShadowsRenderer").Type("Atmosphere.ScreenSpaceShadowsManager")
                .Calls(EveClouds, "ReinitAll")
                .Calls("Atmosphere.ScreenSpaceShadowsRenderer", "SetRenderingResolution")
                .Calls("Atmosphere.ScreenSpaceShadowsManager", "UpdateRenderers")
                .Field("Atmosphere.ScreenSpaceShadowsRenderer", "screenWidth", Int)
                .Field("Atmosphere.ScreenSpaceShadowsRenderer", "screenHeight", Int)
                .Field("Atmosphere.ScreenSpaceShadowsManager", "instance")
                .Field("Atmosphere.ScreenSpaceShadowsManager", "cameraToShadowsRenderer")
                .OnChange(WhenChanged.Keep, "EVE's shadows keep the screen's size");

        // Scatterer's raymarched godrays: sized by its own fractions for the
        // upscaler's cameras, and their history read with point filtering
        // (ScattererCompatibility).
        public static readonly ForeignAssumption ScattererGodrays =
            new ForeignAssumption("scatterer-godrays", "Scatterer",
                                  "Scatterer's godrays sized for the upscaler, their history without outlines")
                .Type(Godrays)
                .Method(Godrays, "OnPreCull")
                .Method(Godrays, "Init")
                .Calls(Godrays, "ResizeRenderTextures", Bool)
                .Field(Godrays, "screenWidth", Int).Field(Godrays, "screenHeight", Int)
                .Field(Godrays, "renderWidth", Int).Field(Godrays, "renderHeight", Int)
                .Field(Godrays, "scatteringOcclusionMaterial", Material)
                .Field(Godrays, "downscaledDepth", RenderTexture)
                .Field(Godrays, "godraysRT").Field(Godrays, "useFlipBuffer", Bool).Field(Godrays, "targetCamera", Camera)
                .Literal(Godrays, "godrayRenderingResolution")
                .Tested("eee2c26a7da2") // Scatterer 0.908
                .OnChange(WhenChanged.Keep, "the godrays keep the screen's size and their history its filtering");

        // Scatterer's TAA, turned off while the upscaler runs, with the motion vector
        // shader it installs and the globals it feeds that shader (HostStack).
        public static readonly ForeignAssumption ScattererTemporalAa =
            new ForeignAssumption("scatterer-taa", "Scatterer", "Scatterer's TAA off while the upscaler runs")
                .Type(ScattererTaa).Type("Scatterer.Scatterer")
                .Calls(ScattererTaa, "Awake")
                .Literal(ScattererTaa, "TAA_UseFloatingOriginCameraMotion", "TAA_PreviousFrameTransform")
                .Literal("Scatterer.Scatterer", "Scatterer/Internal-MotionVectors")
                .OnChange(WhenChanged.Keep, "Scatterer's TAA stays on over the upscaler");

        // Scatterer's SMAA, turned off the same way.
        public static readonly ForeignAssumption ScattererSmaa =
            new ForeignAssumption("scatterer-smaa", "Scatterer", "Scatterer's SMAA off while the upscaler runs")
                .Type("Scatterer.SubpixelMorphologicalAntialiasing")
                .Field("Scatterer.SubpixelMorphologicalAntialiasing", "SMAACameraEvent")
                .Field("Scatterer.SubpixelMorphologicalAntialiasing", "SMAACommandBuffer")
                .OnChange(WhenChanged.Keep, "Scatterer's SMAA is only disabled, its command buffer may stay");

        // Scatterer's ocean in the upscaler's masks: its G-buffer depth and its
        // switch for the current camera, globals Scatterer sets.
        public static readonly ForeignAssumption ScattererOceanMask =
            new ForeignAssumption("scatterer-ocean-mask", "Scatterer", "Scatterer's ocean in the upscaler's masks")
                .Type("Scatterer.OceanCommandBuffer").Type("Scatterer.ShaderProperties")
                .Literal("Scatterer.OceanCommandBuffer", "oceanGbufferDepth")
                .Literal("Scatterer.ShaderProperties", "ScattererOceanActiveOnCurrentCamera")
                .OnChange(WhenChanged.Keep, "the ocean is left out of the masks");

        // Scatterer's ocean taking the raytraced reflections where Deferred's are held
        // (RtCamera, the ocean's reflections): its G-buffer as Deferred's reflections read
        // it, and its switch for them, which it reads with Deferred's ssrOutput
        // (OceanCommandBuffer, Scatterer 0.0908).
        public static readonly ForeignAssumption ScattererOceanReflections =
            new ForeignAssumption("scatterer-ocean-reflections", "Scatterer", "Scatterer's ocean mirrors the raytraced scene")
                .Type("Scatterer.OceanCommandBuffer").Type("Scatterer.ShaderProperties")
                .Literal("Scatterer.OceanCommandBuffer", "oceanGbufferDepth", "oceanGbufferNormalsAndSigma", "useOceanSSR")
                .Literal("Scatterer.ShaderProperties", "_VarianceMax")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the ocean keeps Deferred's screen-space reflections, which stay on");

        // The strength of Scatterer's sun glint on the ocean, which it sets on its ocean's
        // material once, from its node's field (OceanNode.InitOceanMaterial); and the
        // colour its ocean multiplies its reflections with, but not its sun glint, which
        // the raytraced sun's and moons' glints stand in for.
        public static readonly ForeignAssumption ScattererOceanSun =
            new ForeignAssumption("scatterer-ocean-sun", "Scatterer", "the sun's reflection on the ocean")
                .Type("Scatterer.OceanNode")
                .Field("Scatterer.OceanNode", "m_oceanMaterial", Material)
                .Field("Scatterer.OceanNode", "sunReflectionStrength", Float)
                .Field("Scatterer.OceanNode", "reflectionColor", Vector3Type)
                .Literal("Scatterer.OceanNode", "sunReflectionStrength", "reflectionColor")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the sun's reflection stays as Scatterer sets it");

        // Deferred's reflections shader, of which ReDefinition carries a changed copy
        // for the raytraced camera (ReDefinitionDeferredReflections.shader, from
        // Deferred 1.3.5): the globals it reads, which Deferred sets. Its shading
        // beyond them only the version can tell.
        public static readonly ForeignAssumption DeferredReflections =
            new ForeignAssumption("deferred-reflections", "Deferred",
                                  "raytraced light through ReDefinition's copy of Deferred's reflections shader")
                .Type("Deferred.Deferred")
                .Literal(null, "useReflectionProbeOnCurrentCamera", "disableProbeReflectionsOnCurrentCamera",
                         "useSSROnCurrentCamera", "deferredAmbientBrightness", "deferredAmbientTint",
                         "internalSpaceToWorld", "legacyAmbientColor")
                .TestedVersions("1.3.5.0")
                .OnChange(WhenChanged.Keep, "the raytraced light goes in without the reflections pass's share");

        // Deferred's screen-space reflections, sized for the upscaler's cameras.
        public static readonly ForeignAssumption DeferredSsr =
            new ForeignAssumption("deferred-ssr", "Deferred", "Deferred's reflections sized for the upscaler")
                .Type("Deferred.ScreenSpaceReflections")
                .Calls("Deferred.ScreenSpaceReflections", "RecreateTexturesAndBuffers", Int, Bool)
                .Field("Deferred.ScreenSpaceReflections", "targetCamera", Camera)
                .Field("Deferred.ScreenSpaceReflections", "screenColor")
                .OnChange(WhenChanged.Keep, "Deferred's reflections keep the screen's size");

        // TUFX's post-processing split around the upscaler: PostProcessing v2 as TUFX
        // carries it (TufxPostProcessing).
        public static readonly ForeignAssumption TufxPostProcessing =
            new ForeignAssumption("tufx-post-processing", "TUFX", "TUFX's effects split around the upscaler")
                .Type("TUFX.TexturesUnlimitedFXLoader")
                .Type("UnityEngine.Rendering.PostProcessing.PostProcessLayer")
                .Type("UnityEngine.Rendering.PostProcessing.PostProcessManager")
                .Calls("UnityEngine.Rendering.PostProcessing.PostProcessManager", "UpdateSettings",
                       "UnityEngine.Rendering.PostProcessing.PostProcessLayer", Camera)
                .Calls("UnityEngine.Rendering.PostProcessing.Dithering", "Render",
                       "UnityEngine.Rendering.PostProcessing.PostProcessRenderContext")
                .Calls("UnityEngine.Rendering.PostProcessing.PostProcessLayer", "Render",
                       "UnityEngine.Rendering.PostProcessing.PostProcessRenderContext")
                .OnChange(WhenChanged.Keep, "TUFX's effects all run after the upscaler");

        // Parallax's tessellation edge length, scaled for the upscaler's render
        // height: it counts pixels of the target it renders into (ParallaxUtils
        // .cginc, Parallax Continued 1.0.0) -- shader code, which only the version
        // can tell.
        public static readonly ForeignAssumption ParallaxTessellation =
            new ForeignAssumption("parallax-tessellation", "Parallax", "Parallax's tessellation kept at display density")
                .Type("Parallax.ParallaxSettings")
                .Calls("Parallax.ParallaxSettings", "SaveSettings")
                .TestedVersions("1.0.0.0")
                .OnChange(WhenChanged.Keep, "Parallax's tessellation follows the render size");

        // Parallax's scatters in the raytraced scene.
        public static readonly ForeignAssumption ParallaxScatters =
            new ForeignAssumption("parallax-scatters", "Parallax", "Parallax's scatters in the raytraced scene")
                .Type("Parallax.ScatterManager").Type("Parallax.ScatterRenderer")
                .Field("Parallax.ScatterManager", "Instance").Field("Parallax.ScatterManager", "activeScatterRenderers")
                .Field("Parallax.ScatterRenderer", "meshLOD0").Field("Parallax.ScatterRenderer", "instancedMaterialLOD0")
                .Field("Parallax.ScatterRenderer", "outputLOD0")
                .OnChange(WhenChanged.Keep, "the scatters are left out of the rays");

        // Distant Object's sky dimming, without the sun's term where the sun is
        // hidden: DarkenSky.Update as decompiled, and CalculatePlanetsBrightness
        // called with its arguments by position.
        public static readonly ForeignAssumption DistantObjectSky =
            new ForeignAssumption("distantobject-sky", "Distant Object", "the night sky's stars behind a body")
                .Type("DistantObject.DarkenSky").Type("DistantObject.Contract.SolarSystemEngine")
                .Method("DistantObject.DarkenSky", "Update")
                .Calls("DistantObject.Contract.SolarSystemEngine+Interface", "CalculatePlanetsBrightness",
                       Double, Double, Double, Camera)
                .Tested("6d3386a7d13e") // Distant Object 2.2.1.7
                .OnChange(WhenChanged.Fallback, "Distant Object dims the sky its own way");

        // Other mods' UI scaling, turned off so ReDefinition's applies alone: whole
        // methods skipped (4kSP) and writes rerouted (Maneuver Node Evolved,
        // OtherScalers), and Better Time Warp's button moved (TimeWarpButton).
        //
        // 4kSP and Maneuver Node Evolved are named here without fingerprints: no
        // build of them was installed to take one from.
        public static readonly ForeignAssumption FourkSp =
            new ForeignAssumption("4ksp", "4kSP", "4kSP's map scaling left to ReDefinition's")
                .Type("FourkSP.FourkSP")
                .Calls("FourkSP.FourkSP", "UpdateNodes")
                .OnChange(WhenChanged.Keep, "the skipped method is skipped all the same");

        public static readonly ForeignAssumption BetterTimeWarp =
            new ForeignAssumption("better-time-warp", "Better Time Warp", "Better Time Warp's button in the time panel")
                .Type("BetterTimeWarp.BetterTimeWarp")
                .Method("BetterTimeWarp.BetterTimeWarp", "OnGUI")
                .Tested("054d15081476") // Better Time Warp 2.3.14.2
                .OnChange(WhenChanged.Keep, "its button is moved where the code still matches, else left");

        public static readonly ForeignAssumption ManeuverNodeEvolved =
            new ForeignAssumption("maneuver-node-evolved", "Maneuver Node Evolved", "the maneuver gizmo's size")
                .Type("BetterManeuvering.ManeuverController")
                .Calls("BetterManeuvering.ManeuverController", "LateUpdate")
                .Calls("BetterManeuvering.ManeuverController", "gizmoSpawn")
                .OnChange(WhenChanged.Keep, "its size writes are rerouted where the code still matches, else left");

        // Kopernicus's shader settings, read for KSP's terrain shader quality.
        public static readonly ForeignAssumption KopernicusShaders =
            new ForeignAssumption("kopernicus-shaders", "Kopernicus", "Kopernicus's enforced terrain shader level")
                .Type("Kopernicus.RuntimeUtility.RuntimeUtility")
                .OnChange(WhenChanged.Keep, "KSP's terrain shader quality is offered as it is");

        public static readonly ForeignAssumption[] All =
        {
            EveCloudJitter, EveCloudMotion, ScattererCloudReconstruction, EveCloudComposite, EveShadows,
            ScattererGodrays, ScattererTemporalAa, ScattererSmaa, ScattererOceanMask, ScattererOceanReflections, ScattererOceanSun,
            DeferredReflections, DeferredSsr, TufxPostProcessing, ParallaxTessellation, ParallaxScatters,
            DistantObjectSky, FourkSp, BetterTimeWarp, ManeuverNodeEvolved, KopernicusShaders,
        };

        // The installed mods' assumptions, in the catalogue's order.
        public static List<ForeignAssumption> Installed()
        {
            List<ForeignAssumption> installed = new List<ForeignAssumption>();
            foreach (ForeignAssumption assumption in All)
                if (assumption.State != ForeignState.Absent) installed.Add(assumption);
            return installed;
        }
    }
}
