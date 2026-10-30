using System.Collections.Generic;

namespace ReDefinition.Core
{
    // Everything ReDefinition takes for granted about other mods' code, in one
    // place: each with what it names, the code it was checked against, and what
    // happens where that changed (ForeignAssumption). Every one that falls back
    // where the code changed (WhenChanged.Fallback) is asked (Holds) by the class
    // that relies on it before it does, and so are some kept on a change. The
    // others are kept on a change and name only what their class looks up by
    // reflection itself. They are not asked: that class stands aside where something
    // named is missing (a hook whose method is gone is not installed, a feature
    // whose field is gone stays off), so Broken would change nothing there. For
    // them the entry is what the report says. ForeignAssumptionReport says what
    // came of each in KSP.log and in the diagnostics window, and tools/checks/check_foreign_code.ps1
    // checks the whole catalogue against the installed mods outside the game.
    //
    // The fingerprints are those of the builds named beside each; a new build of a
    // mod whose code changed shows here as Changed until its new code has been read
    // and the fingerprint added.
    internal static class ForeignAssumptions
    {
        // Full names of the Unity and system types the entries below pass as parameter and field types.
        private const string Camera = "UnityEngine.Camera";
        private const string Material = "UnityEngine.Material";
        private const string Mesh = "UnityEngine.Mesh";
        private const string ColorType = "UnityEngine.Color";
        private const string RenderTexture = "UnityEngine.RenderTexture";
        private const string ComputeBuffer = "UnityEngine.ComputeBuffer";
        private const string CommandBuffer = "UnityEngine.Rendering.CommandBuffer";
        private const string CameraEvent = "UnityEngine.Rendering.CameraEvent";
        private const string Shader = "UnityEngine.Shader";
        private const string Bool = "System.Boolean";
        private const string Int = "System.Int32";
        private const string Double = "System.Double";
        private const string Float = "System.Single";
        private const string Vector3Type = "UnityEngine.Vector3";
        private const string StringType = "System.String";
        private const string MechJebCore = "MuMech.MechJebCore";

        // Full names of the mods' types several entries name.
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

        // EVE binds the reconstruction's outputs: colour, motion vectors, distance.
        // Scatterer's reconstruction writes the motion vectors as the change in
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

        // Scatterer's raymarched godrays, sized by its own fractions for the
        // upscaler's cameras (ScattererCompatibility): Init sets the fractions, which
        // are read back, and every size written here is checked by name.
        public static readonly ForeignAssumption ScattererGodrays =
            new ForeignAssumption("scatterer-godrays", "Scatterer", "Scatterer's godrays sized for the upscaler")
                .Type(Godrays)
                .Method(Godrays, "Init")
                .Calls(Godrays, "ResizeRenderTextures", Bool)
                .Field(Godrays, "screenWidth", Int).Field(Godrays, "screenHeight", Int)
                .Field(Godrays, "renderWidth", Int).Field(Godrays, "renderHeight", Int)
                .Field(Godrays, "scatteringOcclusionMaterial", Material)
                .Field(Godrays, "downscaledDepth", RenderTexture)
                .Literal(Godrays, "godrayRenderingResolution")
                .Tested("e262d789e588") // Scatterer 0.908
                .OnChange(WhenChanged.Keep, "the godrays keep the screen's size");

        // Their history read with point filtering: a postfix on OnPreCull sets one texture
        // of godraysRT's pair to Point and the other to Bilinear, chosen by useFlipBuffer as
        // OnPreCull leaves it (ScattererCompatibility.AfterPreCull; RaymarchedGodraysRenderer
        // .OnPreCull, Scatterer 0.908, decompiled). Where OnPreCull uses them otherwise, the
        // filter could land on the wrong one, unseen.
        public static readonly ForeignAssumption ScattererGodrayHistory =
            new ForeignAssumption("scatterer-godray-history", "Scatterer", "Scatterer's godray history without outlines")
                .Type(Godrays)
                .Method(Godrays, "OnPreCull")
                .Field(Godrays, "godraysRT").Field(Godrays, "useFlipBuffer", Bool).Field(Godrays, "targetCamera", Camera)
                // Its postfix returns early where OnPreCull does (ScattererCompatibility).
                .Field(Godrays, "parentSkyNode").Field("Scatterer.SkyNode", "inScaledSpace", Bool)
                .Tested("e94b5c123c81") // Scatterer 0.908
                .OnChange(WhenChanged.Fallback, "the godrays' history keeps Scatterer's filtering");

        // Scatterer's TAA, turned off while the upscaler runs, with the motion vector
        // shader it installs and the globals it feeds that shader (HostStack).
        public static readonly ForeignAssumption ScattererTemporalAa =
            new ForeignAssumption("scatterer-taa", "Scatterer", "Scatterer's TAA off while the upscaler runs")
                .Type(ScattererTaa).Type("Scatterer.Scatterer")
                .Calls(ScattererTaa, "Awake")
                .Literal(ScattererTaa, "TAA_UseFloatingOriginCameraMotion", "TAA_PreviousFrameTransform")
                .Literal("Scatterer.Scatterer", "Scatterer/Internal-MotionVectors")
                // The vessels' renderers it sets to ForceNoMotion, freed again by a postfix.
                .Calls(ScattererTaa, "RegisterVesselRenderers", "Vessel")
                // Whether it resolves on its camera, for one antialiasing per camera
                // (HostStack.Duplicates).
                .Calls(ScattererTaa, "EffectiveRole")
                // Its history started afresh on the way to the space centre (KscArrival).
                .Calls(ScattererTaa, "ResetHistory")
                .OnChange(WhenChanged.Keep, "Scatterer's TAA stays on over the upscaler");

        // Scatterer's SMAA, turned off the same way: its command buffer taken off the
        // camera at its own event, and its "initialized" reset so that it attaches the
        // buffer again when switched back on (HostStack; SubpixelMorphologicalAntialiasing,
        // Scatterer 0.908, decompiled).
        public static readonly ForeignAssumption ScattererSmaa =
            new ForeignAssumption("scatterer-smaa", "Scatterer", "Scatterer's SMAA off while the upscaler runs")
                .Type("Scatterer.SubpixelMorphologicalAntialiasing")
                .Field("Scatterer.SubpixelMorphologicalAntialiasing", "SMAACameraEvent", CameraEvent)
                .Field("Scatterer.SubpixelMorphologicalAntialiasing", "SMAACommandBuffer", CommandBuffer)
                .Field("Scatterer.SubpixelMorphologicalAntialiasing", "initialized", Bool)
                .OnChange(WhenChanged.Keep, "Scatterer's SMAA is only disabled, its command buffer may stay");

        // Deferred's SMAA, which it adds to the editors' Main Camera, turned off as
        // Scatterer's: the same fields (SubpixelMorphologicalAntialiasing, Deferred 1.3.5,
        // decompiled).
        public static readonly ForeignAssumption DeferredSmaa =
            new ForeignAssumption("deferred-smaa", "Deferred", "Deferred's SMAA off in the editors while the upscaler runs")
                .Type("Deferred.SubpixelMorphologicalAntialiasing")
                .Field("Deferred.SubpixelMorphologicalAntialiasing", "SMAACameraEvent", CameraEvent)
                .Field("Deferred.SubpixelMorphologicalAntialiasing", "SMAACommandBuffer", CommandBuffer)
                .Field("Deferred.SubpixelMorphologicalAntialiasing", "initialized", Bool)
                .OnChange(WhenChanged.Keep, "Deferred's SMAA is only disabled, its command buffer may stay");

        // Scatterer's ocean in the upscaler's masks: its G-buffer depth and its
        // switch for the current camera, globals Scatterer sets.
        public static readonly ForeignAssumption ScattererOceanMask =
            new ForeignAssumption("scatterer-ocean-mask", "Scatterer", "Scatterer's ocean in the upscaler's masks")
                .Type("Scatterer.OceanCommandBuffer").Type("Scatterer.ShaderProperties")
                .Literal("Scatterer.OceanCommandBuffer", "oceanGbufferDepth")
                .Literal("Scatterer.ShaderProperties", "ScattererOceanActiveOnCurrentCamera")
                .OnChange(WhenChanged.Keep, "the ocean is left out of the masks");

        // Scatterer's ocean takes the raytraced reflections where Deferred's are held
        // (RtCamera, the ocean's reflections). This rests on its G-buffer as Deferred's
        // reflections read it, and on its switch for them, which it reads with Deferred's
        // ssrOutput (OceanCommandBuffer, Scatterer 0.0908).
        public static readonly ForeignAssumption ScattererOceanReflections =
            new ForeignAssumption("scatterer-ocean-reflections", "Scatterer", "Scatterer's ocean mirrors the raytraced scene")
                .Type("Scatterer.OceanCommandBuffer").Type("Scatterer.ShaderProperties")
                .Literal("Scatterer.OceanCommandBuffer", "oceanGbufferDepth", "oceanGbufferNormalsAndSigma", "useOceanSSR")
                .Literal("Scatterer.ShaderProperties", "_VarianceMax")
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the ocean keeps Deferred's screen-space reflections, which stay on");

        // The strength of Scatterer's sun glint on the ocean, which it sets on its ocean's
        // material once, from its node's field (OceanNode.InitOceanMaterial), and which the
        // raytraced sun's and moons' glints stand in for (SeaHighlights).
        public static readonly ForeignAssumption ScattererOceanSun =
            new ForeignAssumption("scatterer-ocean-sun", "Scatterer", "the sun's reflection on the ocean")
                .Type("Scatterer.OceanNode")
                .Field("Scatterer.OceanNode", "m_oceanMaterial", Material)
                .Field("Scatterer.OceanNode", "sunReflectionStrength", Float)
                .Literal("Scatterer.OceanNode", "sunReflectionStrength")
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
                .OnChange(WhenChanged.Fallback, "Deferred's own reflections shader stays, and the raytraced light is laid"
                                                + " over the frame as without Deferred");

        // Deferred's screen-space reflections, sized for the upscaler's cameras.
        public static readonly ForeignAssumption DeferredSsr =
            new ForeignAssumption("deferred-ssr", "Deferred", "Deferred's reflections sized for the upscaler")
                .Type("Deferred.ScreenSpaceReflections")
                .Calls("Deferred.ScreenSpaceReflections", "RecreateTexturesAndBuffers", Int, Bool)
                .Field("Deferred.ScreenSpaceReflections", "targetCamera", Camera)
                .Field("Deferred.ScreenSpaceReflections", "screenColor")
                .OnChange(WhenChanged.Keep, "Deferred's reflections keep the screen's size");

        // Deferred's screen-space reflections held while the raytraced ones take their
        // place (RtKspMods.HoldDeferredReflections). The component is switched off in Update,
        // outside any camera's render. That rests on its OnPreRender setting
        // useSSROnCurrentCamera to 1 and adding its command buffers, and its OnPostRender
        // taking those off and setting it back to 0. So a component off leaves 0 and no
        // pass. Its result, ssrOutput, is the global Scatterer's ocean reads, and the raytraced
        // reflections are handed to it under (RtCamera.OceanReflections). Deferred 1.3.5,
        // decompiled.
        public static readonly ForeignAssumption DeferredSsrHold =
            new ForeignAssumption("deferred-ssr-hold", "Deferred", "Deferred's reflections held for the raytraced ones")
                .Type("Deferred.ScreenSpaceReflections")
                .Method("Deferred.ScreenSpaceReflections", "OnPreRender")
                .Method("Deferred.ScreenSpaceReflections", "OnPostRender")
                .Literal("Deferred.ScreenSpaceReflections", "useSSROnCurrentCamera", "ssrOutput")
                .Tested("17e177397045") // Deferred 1.3.5
                .OnChange(WhenChanged.Fallback, "Deferred's screen-space reflections stay on beside the raytraced ones");

        // TUFX's post-processing split around the upscaler: PostProcessing v2 as TUFX
        // carries it (TufxPostProcessing, PostProcessing v2 in TUFX 1.1.1, decompiled).
        // What the split rests on beyond names is three things. PostProcessManager.UpdateSettings
        // blends a layer's settings anew from the defaults every frame (ReplaceData), after which
        // the postfix switches effects off for that frame. PostProcessLayer's
        // BuildCommandBuffers has a setup the layer after the upscaler repeats
        // (SetupContext, UpdateVolumeSystem, Render). The prefix sets the texture and
        // coordinates of Dithering.Render in its place.
        public static readonly ForeignAssumption TufxPostProcessing =
            new ForeignAssumption("tufx-post-processing", "TUFX", "TUFX's effects split around the upscaler")
                .Type("TUFX.TexturesUnlimitedFXLoader")
                .Type("UnityEngine.Rendering.PostProcessing.PostProcessLayer")
                .Type("UnityEngine.Rendering.PostProcessing.PostProcessManager")
                .Method("UnityEngine.Rendering.PostProcessing.PostProcessManager", "UpdateSettings",
                        "UnityEngine.Rendering.PostProcessing.PostProcessLayer", Camera)
                .Method("UnityEngine.Rendering.PostProcessing.PostProcessManager", "ReplaceData",
                        "UnityEngine.Rendering.PostProcessing.PostProcessLayer")
                .Method("UnityEngine.Rendering.PostProcessing.PostProcessLayer", "BuildCommandBuffers")
                .Method("UnityEngine.Rendering.PostProcessing.PostProcessLayer", "UpdateVolumeSystem",
                        Camera, CommandBuffer)
                .Method("UnityEngine.Rendering.PostProcessing.PostProcessLayer", "Render",
                        "UnityEngine.Rendering.PostProcessing.PostProcessRenderContext")
                .Method("UnityEngine.Rendering.PostProcessing.Dithering", "Render",
                        "UnityEngine.Rendering.PostProcessing.PostProcessRenderContext")
                .Tested("b2d414868765") // TUFX 1.1.1
                .OnChange(WhenChanged.Fallback, "TUFX's effects all run before the upscaler, as without the split");

        // TUFX's antialiasing off while the upscaler runs (HostStack.Tufx): the loader's
        // current profile, its Antialiasing and SecondaryCameraAntialiasing (structs with
        // a Mode), the cameras' PostProcessLayer.antialiasingMode, and RefreshCameras to
        // hand them theirs back (TUFX 1.1.1, decompiled).
        private const string TufxLoader = "TUFX.TexturesUnlimitedFXLoader";
        private const string PostProcessAntialiasing = "UnityEngine.Rendering.PostProcessing.PostProcessLayer+Antialiasing";
        public static readonly ForeignAssumption TufxAntialiasing =
            new ForeignAssumption("tufx-antialiasing", "TUFX", "TUFX's antialiasing off while the upscaler runs")
                .Type(TufxLoader).Type("TUFX.TUFXProfile").Type("TUFX.AntialiasingParameters")
                .Field(TufxLoader, "INSTANCE", TufxLoader)
                .Calls(TufxLoader, "get_CurrentProfile")
                .Calls(TufxLoader, "RefreshCameras")
                .Calls("TUFX.TUFXProfile", "get_ProfileName")
                .Field("TUFX.TUFXProfile", "Antialiasing", "TUFX.AntialiasingParameters")
                .Field("TUFX.TUFXProfile", "SecondaryCameraAntialiasing", "TUFX.AntialiasingParameters")
                .Field("TUFX.AntialiasingParameters", "Mode", PostProcessAntialiasing)
                .Field("UnityEngine.Rendering.PostProcessing.PostProcessLayer", "antialiasingMode", PostProcessAntialiasing)
                // Its own Save writes the player's modes, swapped in for it (HostStack.Tufx).
                .Calls("TUFX.TUFXProfile", "SaveToDisk")
                // Its temporal history started afresh on the way to the space centre (KscArrival).
                .Calls("UnityEngine.Rendering.PostProcessing.PostProcessLayer", "ResetHistory")
                .OnChange(WhenChanged.Keep, "TUFX's antialiasing stays as its profile sets it");

        // TUFX's scene profiles in ReDefinition's window (TufxBehaviour). A choice in TUFX's
        // window comes through ChangeProfileForScene, hooked. A choice here is applied through
        // ApplyProfile. It is saved where TUFX saves its own: the configuration (defaultConfiguration's
        // MainMenuProfile, written to defaultConfigUrl's file) or the save's TUFXGameSettings
        // (SetProfileName). TUFX 1.1.1, decompiled.
        private const string TufxConfiguration = "TUFX.TexturesUnlimitedFXLoader+Configuration";
        public static readonly ForeignAssumption TufxProfiles =
            new ForeignAssumption("tufx-profiles", "TUFX", "TUFX's scene profiles in ReDefinition's window")
                .Type(TufxLoader).Type("TUFX.TUFXScene").Type("TUFX.TUFXGameSettings").Type("TUFX.Utils").Type(TufxConfiguration)
                .Field(TufxLoader, "INSTANCE", TufxLoader)
                .Calls(TufxLoader, "get_Profiles")
                .Calls(TufxLoader, "ChangeProfileForScene", StringType, "TUFX.TUFXScene")
                .Calls(TufxLoader, "ApplyProfile", "TUFX.TUFXProfile", "TUFX.TUFXScene")
                .Calls(TufxLoader, "GetProfileNameForScene", "TUFX.TUFXScene")
                .Calls(TufxLoader, "GetProfileByName", StringType)
                .Calls("TUFX.Utils", "GetCurrentScene")
                .Calls("TUFX.TUFXGameSettings", "SetProfileName", StringType, "TUFX.TUFXScene")
                .Field(TufxLoader, "defaultConfiguration", TufxConfiguration)
                .Field(TufxLoader, "defaultConfigUrl", "UrlDir+UrlConfig")
                .Field(TufxConfiguration, "NODE_NAME", StringType)
                .Field(TufxConfiguration, "MainMenuProfile", StringType)
                .OnChange(WhenChanged.Keep, "TUFX's scene profiles are chosen in its own window only");

        // Parallax's tessellation edge length, scaled for the upscaler's render
        // height: it counts pixels of the target it renders into (ParallaxUtils
        // .cginc, Parallax Continued 1.0.0) -- shader code, which only the version
        // can tell. Its save writes the setting as it stands, and a prefix and a
        // postfix hand it the player's value for that (ParallaxSettings.SaveSettings,
        // decompiled; ParallaxTessellation).
        public static readonly ForeignAssumption ParallaxTessellation =
            new ForeignAssumption("parallax-tessellation", "Parallax", "Parallax's tessellation kept at display density")
                .Type("Parallax.ParallaxSettings")
                .Method("Parallax.ParallaxSettings", "SaveSettings")
                .Tested("84470e62cb62") // Parallax Continued 1.0.0
                .TestedVersions("1.0.0.0")
                .OnChange(WhenChanged.Fallback, "Parallax's tessellation follows the render size");

        // Parallax's scatters in the raytraced scene and on the space centre's lawns
        // (Scenery.ParallaxScatterFields, KscGrass). It reads the active renderers and, of
        // each, the planet, the scatter name, the range and whether it is culled about the vessel.
        // It reads the three levels' meshes, materials and output buffers as 64-byte clumps.
        // It reads the points its scatters are culled about (RuntimeOperations.vectorCraftPos,
        // vectorCameraPos) and where the farthest level starts (DistributionParams.lod2.range).
        // It reads _InstanceData too, the buffer Parallax's materials draw from (Parallax Continued 1.0.0, decompiled).
        private const string ScatterRenderer = "Parallax.ScatterRenderer";
        public static readonly ForeignAssumption ParallaxScatters =
            new ForeignAssumption("parallax-scatters", "Parallax", "Parallax's scatters in the raytraced scene and on the lawns")
                .Type("Parallax.ScatterManager").Type(ScatterRenderer).Type("Parallax.Scatter").Type("Parallax.DistributionParams")
                .Type("Parallax.RuntimeOperations").Type("Parallax.LOD")
                .Field("Parallax.DistributionParams", "lod2", "Parallax.LOD").Field("Parallax.LOD", "range", Float)
                .Field("Parallax.RuntimeOperations", "vectorCraftPos", Vector3Type)
                .Field("Parallax.RuntimeOperations", "vectorCameraPos", Vector3Type)
                .Field("Parallax.ScatterManager", "Instance", "Parallax.ScatterManager")
                .Field("Parallax.ScatterManager", "activeScatterRenderers", "System.Collections.Generic.List`1")
                .Field(ScatterRenderer, "planetName", StringType)
                .Field(ScatterRenderer, "scatter", "Parallax.Scatter")
                .Field("Parallax.Scatter", "scatterName", StringType)
                .Field("Parallax.Scatter", "distributionParams", "Parallax.DistributionParams")
                .Field("Parallax.DistributionParams", "range", Float)
                .Field("Parallax.Scatter", "useCraftPosition", Bool)
                .Field(ScatterRenderer, "meshLOD0", Mesh).Field(ScatterRenderer, "meshLOD1", Mesh).Field(ScatterRenderer, "meshLOD2", Mesh)
                .Field(ScatterRenderer, "instancedMaterialLOD0", Material).Field(ScatterRenderer, "instancedMaterialLOD1", Material)
                .Field(ScatterRenderer, "instancedMaterialLOD2", Material)
                .Field(ScatterRenderer, "outputLOD0", ComputeBuffer).Field(ScatterRenderer, "outputLOD1", ComputeBuffer)
                .Field(ScatterRenderer, "outputLOD2", ComputeBuffer)
                .Literal(ScatterRenderer, "_InstanceData")
                .TestedVersions("1.0.0.0")
                .OnChange(WhenChanged.Fallback, "the scatters are left out of the rays, and the space centre's lawns stay bare");

        // Distant Object's sky dimming, without the sun's term where the sun is
        // hidden: DarkenSky.Update as decompiled, and CalculatePlanetsBrightness
        // called with its arguments by position, on SolarSystemEngine.Instance with
        // the settings' SkyboxBrightness values (DistantObjectBehaviour).
        private const string DistantSkybox = "DistantObject.SettingsBuffer+SkyboxBrightnessClass";
        public static readonly ForeignAssumption DistantObjectSky =
            new ForeignAssumption("distantobject-sky", "Distant Object", "the night sky's stars behind a body")
                .Type("DistantObject.DarkenSky").Type("DistantObject.Contract.SolarSystemEngine").Type("DistantObject.Settings")
                .Method("DistantObject.DarkenSky", "Update")
                .Calls("DistantObject.Contract.SolarSystemEngine+Interface", "CalculatePlanetsBrightness",
                       Double, Double, Double, Camera)
                .Calls("DistantObject.Contract.SolarSystemEngine", "get_Instance")
                .Field("DistantObject.Settings", "SkyboxBrightness", DistantSkybox)
                .Field(DistantSkybox, "minimumSignificantBodySize", Double)
                .Field(DistantSkybox, "minimumTargetRelativeAngle", Double)
                .Field(DistantSkybox, "referenceBodySize", Double)
                .Field(DistantSkybox, "maxBrightness", Double)
                .Tested("6d3386a7d13e") // Distant Object 2.2.1.7
                .OnChange(WhenChanged.Fallback, "Distant Object dims the sky its own way");

        // Distant Object's settings in ReDefinition's window (DistantObjectBehaviour):
        // It names Settings.Instance, its Load and Commit, and its window. The window is
        // SettingsGui's buffer, held by SettingsGuiOnMainMenu and SettingsGuiOnGameScenes, and
        // ApplySettings, hooked so that a change made there reaches ReDefinition's window
        // (Distant Object 2.2.1.7, decompiled).
        public static readonly ForeignAssumption DistantObjectSettings =
            new ForeignAssumption("distantobject-settings", "Distant Object", "Distant Object's settings in ReDefinition's window")
                .Type("DistantObject.Settings").Type("DistantObject.SettingsGui")
                .Calls("DistantObject.Settings", "get_Instance")
                .Calls("DistantObject.Settings", "Load")
                .Calls("DistantObject.Settings", "Commit")
                .Calls("DistantObject.SettingsGui", "ApplySettings")
                .Field("DistantObject.SettingsGui", "buffer", "DistantObject.SettingsBuffer")
                .Field("DistantObject.SettingsGuiOnMainMenu", "settingsGui", "DistantObject.SettingsGui")
                .Field("DistantObject.SettingsGuiOnGameScenes", "settingsGui", "DistantObject.SettingsGui")
                .OnChange(WhenChanged.Keep, "what of its settings cannot be reached is left to its own window");

        // Other mods' UI scaling, turned off so ReDefinition's applies alone. ReDefinition
        // skips whole methods of 4kSP, reroutes writes of Maneuver Node Evolved
        // (OtherScalers) and moves Better Time Warp's button (TimeWarpButton).
        //
        // The entries for 4kSP and Maneuver Node Evolved are named here without fingerprints: no
        // build of them was installed to take one from.
        public static readonly ForeignAssumption FourkSp =
            new ForeignAssumption("4ksp", "4kSP", "4kSP's map scaling left to ReDefinition's")
                .Type("FourkSP.FourkSP")
                .Calls("FourkSP.FourkSP", "UpdateNodes")
                .Calls("FourkSP.FourkSP", "AddToolbarButton")
                .OnChange(WhenChanged.Keep, "the skipped method is skipped all the same");

        // 4kSP Expanded's R&D part: its bootstrap's Start, which patches the R&D part list,
        // and RDSceneScaler.Start, which hooks the R&D screen's opening, both skipped.
        public static readonly ForeignAssumption FourkSpRnD =
            new ForeignAssumption("4ksp-rnd", "4kSP Expanded", "4kSP's R&D scaling left to ReDefinition's")
                .Type("_4kSP_RnD.RDScalerHarmonyBootstrap").Type("_4kSP_RnD.RDSceneScaler")
                .Calls("_4kSP_RnD.RDScalerHarmonyBootstrap", "Start")
                .Calls("_4kSP_RnD.RDSceneScaler", "Start")
                .OnChange(WhenChanged.Keep, "the skipped methods are skipped all the same");

        // 4kSP Expanded's window part: its bootstrap's Start, which patches the IMGUI window
        // methods, skipped.
        public static readonly ForeignAssumption FourkSpWindows =
            new ForeignAssumption("4ksp-windows", "4kSP Expanded", "4kSP's window scaling left to ReDefinition's")
                .Type("_4kSP_ModWindows.ModWindowScalerHarmonyBootstrap")
                .Calls("_4kSP_ModWindows.ModWindowScalerHarmonyBootstrap", "Start")
                .OnChange(WhenChanged.Keep, "the skipped method is skipped all the same");

        // Better Time Warp's button copied into the time panel (TimeWarpButton,
        // TimeWarpBar). ReDefinition routes its OnGUI's two GUI.Toggle calls, shows its
        // buttonContent on the copy, switches its UIOpen by a press on it, and keeps its toolbar
        // button by the namespace it registers with ToolbarControl
        // (BetterTimeWarp.MODID). Better Time Warp 2.3.14.2, decompiled.
        public static readonly ForeignAssumption BetterTimeWarp =
            new ForeignAssumption("better-time-warp", "Better Time Warp", "Better Time Warp's button in the time panel")
                .Type("BetterTimeWarp.BetterTimeWarp")
                .Method("BetterTimeWarp.BetterTimeWarp", "OnGUI")
                .Field("BetterTimeWarp.BetterTimeWarp", "buttonContent", "UnityEngine.GUIContent")
                .Calls("BetterTimeWarp.BetterTimeWarp", "get_UIOpen")
                .Calls("BetterTimeWarp.BetterTimeWarp", "set_UIOpen", Bool)
                .Literal("BetterTimeWarp.BetterTimeWarp", "BetterTimeWarp_NS")
                .Tested("054d15081476") // Better Time Warp 2.3.14.2
                .OnChange(WhenChanged.Keep, "its button is moved where the code still matches, else left");

        // Maneuver Node Evolved's gizmo scaling turned off (Scaling.OtherScalers): a transpiler on LateUpdate and
        // gizmoSpawn reroutes their writes to the gizmo's screenSize, so ReDefinition's UI scale is the only one.
        public static readonly ForeignAssumption ManeuverNodeEvolved =
            new ForeignAssumption("maneuver-node-evolved", "Maneuver Node Evolved", "the maneuver gizmo's size")
                .Type("BetterManeuvering.ManeuverController")
                .Calls("BetterManeuvering.ManeuverController", "LateUpdate")
                .Calls("BetterManeuvering.ManeuverController", "gizmoSpawn")
                .OnChange(WhenChanged.Keep, "its size writes are rerouted where the code still matches, else left");

        // Kopernicus's shader settings, read for KSP's terrain shader quality
        // (KspBehaviour.KopernicusShaderLevel): RuntimeUtility.KopernicusConfig, its
        // EnforceShaders, WarnShaders and EnforcedShaderLevel (Kopernicus 1.12.249,
        // decompiled).
        private const string KopernicusConfig = "Kopernicus.Configuration.ConfigReader";
        public static readonly ForeignAssumption KopernicusShaders =
            new ForeignAssumption("kopernicus-shaders", "Kopernicus", "Kopernicus's enforced terrain shader level")
                .Type("Kopernicus.RuntimeUtility.RuntimeUtility").Type(KopernicusConfig)
                .Field("Kopernicus.RuntimeUtility.RuntimeUtility", "KopernicusConfig", KopernicusConfig)
                .Field(KopernicusConfig, "EnforceShaders", Bool)
                .Field(KopernicusConfig, "WarnShaders", Bool)
                .Field(KopernicusConfig, "EnforcedShaderLevel", Int)
                .OnChange(WhenChanged.Keep, "KSP's terrain shader quality is offered as it is");

        // Kopernicus's star in the raytracing (RtKspMods): KopernicusStar.Current, the star
        // nearest the focus, whose light lights the scene, and its shifter's sunlightColor,
        // the colour Kopernicus gives that light (KopernicusStar, LightShifter; Kopernicus
        // 1.12.249, decompiled).
        private const string KopernicusStar = "Kopernicus.Components.KopernicusStar";
        public static readonly ForeignAssumption KopernicusStarLight =
            new ForeignAssumption("kopernicus-star", "Kopernicus", "the raytraced sun as Kopernicus's current star")
                .Type(KopernicusStar).Type("Kopernicus.Components.LightShifter")
                .Field(KopernicusStar, "Current", KopernicusStar)
                .Field(KopernicusStar, "shifter", "Kopernicus.Components.LightShifter")
                .Field("Kopernicus.Components.LightShifter", "sunlightColor", ColorType)
                .OnChange(WhenChanged.Keep, "the raytraced sun is KSP's, in white");

        // Firefly (FireflyIdle): its AtmoFxModule's fxVessel with the command buffer it
        // keeps on the flight camera, its airstream camera and its effect material, whose
        // _EntryStrength it sets each physics frame (UpdateMaterialProperties); its
        // PopulateCommandBuffer fills the buffer again. Its shader draws nothing below a
        // strength of 50 in its main pass and of 1000 in its bow shock, and next to nothing
        // in its glow below 1 (FireflyShader, GlowPass.cginc of the Firefly repository).
        public static readonly ForeignAssumption FireflyIdle =
            new ForeignAssumption("firefly-idle", "Firefly", "Firefly's re-entry effects skipped while they show nothing")
                .Type("Firefly.AtmoFxModule").Type("Firefly.AtmoFxVessel")
                .Field("Firefly.AtmoFxModule", "fxVessel").Field("Firefly.AtmoFxModule", "isLoaded", Bool)
                .Field("Firefly.AtmoFxVessel", "commandBuffer").Field("Firefly.AtmoFxVessel", "airstreamCamera", Camera)
                .Field("Firefly.AtmoFxVessel", "material", Material)
                .Method("Firefly.AtmoFxModule", "PopulateCommandBuffer")
                .Method("Firefly.AtmoFxModule", "UpdateMaterialProperties")
                .Literal("Firefly.AtmoFxModule", "_EntryStrength")
                .Tested("fdf8ca4c28ff") // Firefly 1.0.6
                // The thresholds are its shader's, which only the version can tell.
                .TestedVersions("1.0.6.0")
                .OnChange(WhenChanged.Fallback, "Firefly draws its effects every frame, as it does by itself");

        // EVE's cloud layer in the raytracing (RtEveClouds): the loaded bodies' cloud objects,
        // their volumetric and 2D layers, the volume's material, switch, maps, radii,
        // rotation and fades, the 2D layer's shadow material.
        private const string EveVolume = "Atmosphere.CloudsRaymarchedVolume";
        public static readonly ForeignAssumption EveRtClouds =
            new ForeignAssumption("eve-rt-clouds", "EVE", "EVE's clouds over the rays: the moons' light, the water's discs, reflections")
                .Type("Atmosphere.CloudsManager").Type("Atmosphere.CloudsObject").Type(EveVolume).Type("Atmosphere.Clouds2D")
                .Type("Utils.TextureWrapper")
                .Field("EVEManager.GenericEVEManager`1", "ObjectList")
                .Method("Atmosphere.CloudsObject", "get_Body").Method("Atmosphere.CloudsObject", "get_Layer2D")
                .Method("Atmosphere.CloudsObject", "get_LayerRaymarchedVolume")
                .Method(EveVolume, "get_CloudRotationMatrix").Method(EveVolume, "get_InnerSphereRadius")
                .Method(EveVolume, "get_OuterSphereRadius").Method(EveVolume, "get_CurrentTimeFadeCoverage")
                .Method(EveVolume, "get_CurrentTimeFadeDensity").Method(EveVolume, "get_CoverageMap").Method(EveVolume, "get_CloudTypeMap")
                .Method(EveVolume, "get_RaymarchedCloudMaterial").Method(EveVolume, "get_enabled")
                .Method(EveVolume, "get_VolumetricLayerScaledFade")
                .Field(EveVolume, "curvesTexture")
                .Method("Atmosphere.Clouds2D", "get_ScreenSpaceShadowMaterial").Method("Atmosphere.Clouds2D", "get_Scaled")
                .Method("Utils.TextureWrapper", "GetTexture")
                .Field("Utils.TextureWrapper", "type").Field("Utils.TextureWrapper", "alphaMask")
                // The fades read off the 2D layer's shadow material (RtEveClouds), which EVE sets.
                .Literal("Atmosphere.Clouds2D", "cloudTimeFadeDensity")
                .Tested("f36a77d52162") // EVE 3.2.2.0, the volumetric build of 2026-10
                .OnChange(WhenChanged.Fallback, "the rays leave EVE's clouds out");

        // Scatterer's main settings in ReDefinition's window (ScattererBehaviour):
        // Scatterer.Instance, its mainSettings and isActive, the Scatterer_config node they
        // load from, and pluginData with its savePluginData for the window's keys
        // (Scatterer 0.908, decompiled).
        public static readonly ForeignAssumption ScattererSettings =
            new ForeignAssumption("scatterer-settings", "Scatterer", "Scatterer's settings in ReDefinition's window")
                .Type("Scatterer.Scatterer").Type("Scatterer.MainSettingsReadWrite").Type("Scatterer.PluginDataReadWrite")
                .Calls("Scatterer.Scatterer", "get_Instance")
                .Field("Scatterer.Scatterer", "mainSettings", "Scatterer.MainSettingsReadWrite")
                .Field("Scatterer.Scatterer", "isActive", Bool)
                .Field("Scatterer.Scatterer", "pluginData", "Scatterer.PluginDataReadWrite")
                .Calls("Scatterer.PluginDataReadWrite", "savePluginData")
                .Literal("Scatterer.MainSettingsReadWrite", "Scatterer_config")
                .OnChange(WhenChanged.Keep, "what of its settings cannot be reached is left to its own window");

        // Kerbal Frame Generator's frame blend off while the upscaler runs (HostStack.Kfg):
        // its global switch, KFG_Settings.effectEnabled, a public static bool it reads every
        // frame. Named from its source without a fingerprint: no build of it was installed
        // to take one from.
        public static readonly ForeignAssumption KerbalFrameGenerator =
            new ForeignAssumption("kerbal-frame-generator", "Kerbal Frame Generator", "its frame blend off while the upscaler runs")
                .Type("KFG_Settings")
                .Field("KFG_Settings", "effectEnabled", Bool)
                .OnChange(WhenChanged.Keep, "its blend stays on over the upscaler");

        // The Toolbar's buttons and MechJeb's menu kept off KSP's loading screen
        // (Window.LoadingScreenWindows). Their OnGUI is skipped while its camera is on, by a
        // prefix whose effect does not depend on what OnGUI draws (Toolbar 1.8.1.2,
        // MechJeb 2.15.3, decompiled).
        public static readonly ForeignAssumption ToolbarLoadingScreen =
            new ForeignAssumption("toolbar-loading-screen", "Toolbar", "the Toolbar's buttons off KSP's loading screen")
                .Type("Toolbar.ToolbarManager")
                .Calls("Toolbar.ToolbarManager", "OnGUI")
                .OnChange(WhenChanged.Keep, "its buttons show on the loading screen");

        // The same for MechJeb's menu: MechJebCore's OnGUI skipped while the loading screen's camera is on.
        public static readonly ForeignAssumption MechJebLoadingScreen =
            new ForeignAssumption("mechjeb-loading-screen", "MechJeb", "MechJeb's menu off KSP's loading screen")
                .Type("MuMech.MechJebCore")
                .Calls("MuMech.MechJebCore", "OnGUI")
                .OnChange(WhenChanged.Keep, "its menu shows on the loading screen");

        // Other mods' buttons in KSP's toolbar hidden or kept by the namespace they register
        // with ToolbarControl (Window.ToolbarTakeover): its static tcList, each one's
        // nameSpace and stockButton (ToolbarControl 0.1.9.14, decompiled).
        public static readonly ForeignAssumption ToolbarControlButtons =
            new ForeignAssumption("toolbarcontrol-buttons", "ToolbarControl", "other mods' toolbar buttons hidden by their mod")
                .Type("ToolbarControl_NS.ToolbarControl")
                .Field("ToolbarControl_NS.ToolbarControl", "tcList", "System.Collections.Generic.List`1")
                .Field("ToolbarControl_NS.ToolbarControl", "nameSpace", StringType)
                .Field("ToolbarControl_NS.ToolbarControl", "stockButton", "KSP.UI.Screens.ApplicationLauncherButton")
                .OnChange(WhenChanged.Keep, "the buttons are told apart by the launcher's own list only");

        // Persistent Rotation's toolbar button hidden for good (Window.ToolbarHides). It
        // registers with ToolbarControl as "PersistentRotation" (Interface.MODID). Its
        // OnClick only shows and hides its window, which a row of ReDefinition's window
        // does (Persistent Rotation 1.9.2.1, decompiled).
        public static readonly ForeignAssumption PersistentRotationButton =
            new ForeignAssumption("persistentrotation-button", "Persistent Rotation", "its toolbar button left to the window's row")
                .Type("PersistentRotation.Interface")
                .Method("PersistentRotation.Interface", "OnClick")
                .Literal("PersistentRotation.Interface", "PersistentRotation")
                .Tested("c72a2f85bff4") // Persistent Rotation 1.9.2.1
                .OnChange(WhenChanged.Keep, "its button is hidden while the row stands");

        // The light of EVE's clouds where Scatterer integrates them (EveCloudMoonlight). After
        // SkyNode.UpdateEVECloudMaterials has set a body's cloud materials for the frame, it sets the
        // sun's direction and colour on its raymarched ones (_Sun_WorldSunDir, _sunColor). It sets
        // a second light on its 2D ones (PLANETSHINE_ON, planetShineSources,
        // cloudPlanetShineRGB, as Scatterer sets a secondary sun's). Those names are what the
        // shaders Scatterer puts in for EVE's read (Scatterer-EVE/RaymarchCloud and
        // Scatterer-EVE/Cloud, scatterershaders-directx of 0.908): their use only the
        // version can tell.
        private const string SkyNode = "Scatterer.SkyNode";
        private const string CloudLayer = "Scatterer.EVECloudLayer";
        public static readonly ForeignAssumption ScattererCloudLight =
            new ForeignAssumption("scatterer-cloud-light", "Scatterer", "EVE's clouds lit by a moon at night")
                .Type(SkyNode).Type(CloudLayer).Type("Scatterer.EVEReflectionHandler").Type("Scatterer.Scatterer")
                .Method(SkyNode, "UpdateEVECloudMaterials")
                .Field(SkyNode, "celestialBodyName")
                .Field("Scatterer.Scatterer", "EveReflectionHandler")
                .Field("Scatterer.EVEReflectionHandler", "EVECloudLayers")
                .Field(CloudLayer, "Clouds2dMaterial", Material)
                .Field(CloudLayer, "RaymarchedVolumetricsMaterial", Material)
                .Field(CloudLayer, "ReflectionProbeRaymarchedVolumetricsMaterial", Material)
                .Method("Scatterer.Scatterer", "get_Instance")
                .Literal("Scatterer.ShaderProperties", "_Sun_WorldSunDir", "_sunColor", "cloudSunColor", "planetShineSources",
                         "cloudPlanetShineRGB")
                .Literal(SkyNode, "PLANETSHINE_ON", "PLANETSHINE_OFF")
                // The layers' radii, which EVE sets on the same materials.
                .Literal("Atmosphere.CloudsRaymarchedVolume", "innerSphereRadius", "outerSphereRadius")
                .Tested("963805946f31") // Scatterer 0.908
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "EVE's clouds take the sun's light alone, dark at night");

        // The moon's light on EVE's raymarched clouds per pixel (EveCloudRelight). Right after
        // DeferredRaymarchedVolumetricCloudsRenderer.OnPreRender has added its command buffer
        // for a camera, the history it reconstructs into this frame (historyRT and
        // historyDistanceRT at [useFlipUpscalingBuffer, eye, face], HistoryManager's indexer)
        // is copied with the moon's light added. The copy is bound in its place (colorBuffer,
        // scattererReconstructedCloud). Before its OnPostRender the copy's command buffer
        // comes off again. The layout of those buffers (premultiplied light and the
        // transmittance, the distance in hundreds of metres) and the lighting it repeats
        // are Scatterer's shaders' (ScattererCloudReconstruction, scatterer-cloud-light).
        public static readonly ForeignAssumption EveCloudRelight =
            new ForeignAssumption("eve-cloud-relight", "EVE", "EVE's clouds lit by a moon per pixel as the sun sets")
                .Type(EveClouds).Type(EveVolume).Type("Utils.HistoryManager`1").Type(EveProperties)
                .Method(EveClouds, "OnPreRender").Method(EveClouds, "OnPostRender")
                .Field(EveClouds, "targetCamera", Camera)
                .Field(EveClouds, "renderingEnabled", Bool)
                .Field(EveClouds, "reflectionProbeCamera", Bool)
                .Field(EveClouds, "useFlipUpscalingBuffer", Bool)
                .Field(EveClouds, "volumesAdded")
                .Field(EveClouds, "historyRT")
                .Field(EveClouds, "historyDistanceRT")
                .Field(EveClouds, "CloudRenderingCameraEvent", CameraEvent)
                .Method("Utils.HistoryManager`1", "get_Item", Bool, Bool, Int)
                .Method(EveVolume, "get_RaymarchedCloudMaterial").Method(EveVolume, "get_InnerSphereRadius")
                .Method(EveVolume, "get_OuterSphereRadius")
                .Literal(EveProperties, "colorBuffer", "scattererReconstructedCloud")
                .Literal(EveVolume, "directPhaseFunctionProperties", "multiplePhaseFunctionProperties", "cloudColor")
                // The material properties the light per pixel reads off the layers (EVE's cloud materials), and the one that
                // tells a layer that shines by itself.
                .Literal(EveVolume, "skylightTintMultiplier", "skylightMultiplier", "cloudTypeProperties0",
                         "multipleScatteringBrightnessArray", "numberOfCloudTypes")
                .Tested("427bd9602cbb") // EVE 3.2.2
                .OnChange(WhenChanged.Fallback, "EVE's clouds take the moon's light all at once as the sun sets");

        // The other half of it: Scatterer's atmosphere on EVE's raymarched cloud materials
        // (SkyNode.InitPostprocessMaterialUniforms). That is the transmittance table's place and size in
        // the atlas, the radii, scale heights and extinction. The atlas itself is read from
        // SkyNode.atmosphereAtlas, as the shader does not declare it. It also covers the way its raymarching
        // shader lights the clouds with them, which EveCloudRelight repeats for the moon
        // (Scatterer-EVE/RaymarchCloud, scatterershaders-directx of 0.908, disassembled). Only
        // its version can tell the latter.
        public static readonly ForeignAssumption ScattererCloudAtmosphere =
            new ForeignAssumption("scatterer-cloud-atmosphere", "Scatterer", "the moon's light per pixel lit as Scatterer lights the clouds")
                .Type(SkyNode).Type("Scatterer.ShaderProperties")
                .Method(SkyNode, "InitPostprocessMaterialUniforms")
                .Field(SkyNode, "atmosphereAtlas", "UnityEngine.Texture2D")
                .Literal("Scatterer.ShaderProperties", "AtmosphereAtlas", "TransmittanceAtlasScaleAndOffset", "AtmosphereAtlasDimensions",
                         "TRANSMITTANCE_W", "TRANSMITTANCE_H", "Rg", "Rt", "HR", "HM", "betaR", "betaMEx")
                .Tested("07ac45f0abe4") // Scatterer 0.908
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "EVE's clouds take the moon's light all at once as the sun sets");

        // The names that tell EVE's weather layers from the layers that shine by themselves (CloudLayerKinds): the cloud object's
        // name, and Scatterer's layer holding that object. A layer's tint, a material property, is named with the light per pixel
        // (EveCloudRelight). Without the names the tint decides alone.
        public static readonly ForeignAssumption EveCloudKinds =
            new ForeignAssumption("eve-cloud-kinds", "EVE", "the auroras told from the clouds by name")
                .Type("Atmosphere.CloudsObject")
                .Method("Atmosphere.CloudsObject", "get_Name")
                .Field(CloudLayer, "CloudObject")
                .Tested("c1c52b581bea") // EVE 3.2.2
                .OnChange(WhenChanged.Keep, "the auroras are told from the clouds by their tint alone");

        // MechJeb as the map window's autopilot flies a route (Autopilot/MechJebDriver): the node executor, the landing and ascent
        // autopilots, the target and warp controllers and the staging limit, reached through the vessel's master core. MechJeb 2.16 (the
        // development builds), decompiled: the ascent is PSG and `MechJebCore` has `Hoverslam` and `SmartASS`; the members named here
        // are those of 2.15 as well, apart from the ascent. Without it the autopilot is greyed out.
        public static readonly ForeignAssumption MechJebAutopilot =
            new ForeignAssumption("mechjeb-autopilot", "MechJeb", "MechJeb flying the planner's route")
                .Type(MechJebCore).Type("MuMech.VesselExtensions")
                .Method("MuMech.VesselExtensions", "GetMasterMechJeb", "Vessel")
                .Field(MechJebCore, "Node").Field(MechJebCore, "Landing").Field(MechJebCore, "Target").Field(MechJebCore, "Warp")
                .Field(MechJebCore, "Staging").Field(MechJebCore, "AscentSettings")
                .Method(MechJebCore, "GetComputerModule", StringType)
                .Method("MuMech.MechJebModuleNodeExecutor", "ExecuteOneNode", "System.Object")
                .Method("MuMech.MechJebModuleNodeExecutor", "ExecuteAllNodes", "System.Object")
                .Method("MuMech.MechJebModuleNodeExecutor", "Abort")
                .Field("MuMech.MechJebModuleNodeExecutor", "Autowarp").Field("MuMech.MechJebModuleNodeExecutor", "LeadTime")
                .Field("MuMech.MechJebModuleNodeExecutor", "State")
                .Method("MuMech.MechJebModuleLandingAutopilot", "LandAtPositionTarget", "System.Object")
                .Method("MuMech.MechJebModuleLandingAutopilot", "LandUntargeted", "System.Object")
                .Method("MuMech.MechJebModuleLandingAutopilot", "StopLanding")
                .Method("MuMech.MechJebModuleTargetController", "SetPositionTarget", "CelestialBody", Double, Double)
                .Method("MuMech.MechJebModuleTargetController", "Unset")
                .Method("MuMech.MechJebModuleWarpController", "WarpToUT", Double, Double)
                .Method("MuMech.MechJebModuleStagingController", "AutoStageLimitRequest", Int, "System.Object")
                .Method("MuMech.MechJebModuleStagingController", "AutoStageLimitRemove", "System.Object")
                .Field("MuMech.MechJebModuleAscentSettings", "DesiredOrbitAltitude")
                .Field("MuMech.MechJebModuleAscentSettings", "DesiredInclination")
                .Method("MuMech.MechJebModuleAscentSettings", "get_AscentAutopilot")
                .Method("MuMech.EditableDoubleMult", "set_Val", Double)
                .Field("MuMech.ComputerModule", "Users").Method("MuMech.ComputerModule", "get_Enabled")
                .Method("MuMech.UserPool", "Add", "System.Object").Method("MuMech.UserPool", "Remove", "System.Object")
                .Type("MuMech.MechJebModuleAscentPSGAutopilot")
                .Tested("69a956b331f3") // MechJeb 2.16.0.0, development build 1771
                .TestedVersions("2.16.0.0")
                .OnChange(WhenChanged.Fallback, "the autopilot is greyed out, and the nodes are flown by hand");

        // Parsek's ghosts as the autopilot tells them (Autopilot/ParsekBridge). Parsek has no API: a ghost is told by its persistent id through
        // reflection. Parsek 0.10.6, source read (a beta: the format is generation 4 and changes). Nothing of Parsek is written. Each further
        // thing the autopilot takes from Parsek gets an entry of its own, so that a change in one switches off that one alone.
        public static readonly ForeignAssumption ParsekGhost =
            new ForeignAssumption("parsek-ghost", "Parsek", "the autopilot kept off Parsek's ghosts")
                .Type("Parsek.GhostMapPresence")
                .Method("Parsek.GhostMapPresence", "IsGhostMapVessel", "System.UInt32")
                .Tested("10d2995d828d") // Parsek 0.10.6
                .OnChange(WhenChanged.Fallback, "every vessel counts as real");

        // Parsek's recorder as the map window shows it: whether this flight is being recorded, and how many recordings are kept. Both are read, nothing
        // is started or stopped. Parsek 0.10.6, source read: ParsekFlight.Instance holds the flight's recorder, whose IsRecording says it,
        // and RecordingStore.CommittedRecordings is the list of the recordings that were committed.
        public static readonly ForeignAssumption ParsekRecorder =
            new ForeignAssumption("parsek-recorder", "Parsek", "the map window says whether Parsek records the flight")
                .Type("Parsek.ParsekFlight").Type("Parsek.FlightRecorder").Type("Parsek.RecordingStore")
                .Method("Parsek.ParsekFlight", "get_Instance")
                .Field("Parsek.ParsekFlight", "recorder", "Parsek.FlightRecorder")
                .Method("Parsek.FlightRecorder", "get_IsRecording")
                .Method("Parsek.RecordingStore", "get_CommittedRecordings")
                .Tested("dd556c4ede68") // Parsek 0.10.6
                .OnChange(WhenChanged.Fallback, "the window does not say whether Parsek records");

        // The autopilot has Parsek record the flight it flies, which Parsek does by itself only for a launch. Parsek 0.10.6, source read:
        // ParsekFlight.StartRecording(suppressStartScreenMessage, fromPrelaunchTransition) begins the recording of the active vessel in the flight's
        // recorder, and FlightRecorder.IsRecording says whether one runs. Parsek's own merge dialog commits it when the flight is left.
        public static readonly ForeignAssumption ParsekStart =
            new ForeignAssumption("parsek-start", "Parsek", "the autopilot has Parsek record its flight")
                .Type("Parsek.ParsekFlight").Type("Parsek.FlightRecorder")
                .Method("Parsek.ParsekFlight", "get_Instance")
                .Method("Parsek.ParsekFlight", "StartRecording", "System.Boolean", "System.Boolean")
                .Field("Parsek.ParsekFlight", "recorder", "Parsek.FlightRecorder")
                .Method("Parsek.FlightRecorder", "get_IsRecording")
                .Tested("1f633d761b0e") // Parsek 0.10.6
                .OnChange(WhenChanged.Fallback, "Parsek is not asked to record the autopilot's flight");

        // The whole catalogue, in the order the report lists it.
        public static readonly ForeignAssumption[] All =
        {
            EveCloudJitter, EveCloudMotion, ScattererCloudReconstruction, EveCloudComposite, EveShadows,
            ScattererGodrays, ScattererGodrayHistory, ScattererCloudLight, EveCloudRelight, ScattererCloudAtmosphere, EveCloudKinds, MechJebAutopilot, ParsekGhost, ParsekRecorder, ParsekStart,
            ScattererTemporalAa, ScattererSmaa, ScattererOceanMask,
            ScattererOceanReflections, ScattererOceanSun, ScattererSettings,
            DeferredReflections, DeferredSsr, DeferredSsrHold, DeferredSmaa,
            TufxPostProcessing, TufxAntialiasing, TufxProfiles, ParallaxTessellation, ParallaxScatters,
            DistantObjectSky, DistantObjectSettings, FourkSp, FourkSpRnD, FourkSpWindows, BetterTimeWarp, ManeuverNodeEvolved,
            KopernicusShaders, KopernicusStarLight, FireflyIdle, EveRtClouds, KerbalFrameGenerator,
            ToolbarLoadingScreen, MechJebLoadingScreen, ToolbarControlButtons, PersistentRotationButton,
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
