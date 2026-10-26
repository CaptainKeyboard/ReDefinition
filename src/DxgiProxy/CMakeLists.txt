# The proxy: a dxgi.dll that hands Unity a shared D3D11 texture as its backbuffer
# and presents through a D3D12 swapchain, with NVIDIA's DLSS, Intel's XeSS or AMD's
# FSR frame generation, and runs DLSS and AMD's upscaler for the mod
# (docs/development/frame-generation.md).
#
# Build:  cmake -S src/DxgiProxy -B build/DxgiProxy -A x64
#         cmake --build build/DxgiProxy --config Release

cmake_minimum_required(VERSION 3.20)
project(ReDefinitionProxy LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# The mod's version, from src/ReDefinition.csproj, for the proxy's version resource.
set(REDEFINITION_CSPROJ "${CMAKE_CURRENT_SOURCE_DIR}/../ReDefinition.csproj")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${REDEFINITION_CSPROJ}")
file(READ "${REDEFINITION_CSPROJ}" REDEFINITION_CSPROJ_TEXT)
if(NOT REDEFINITION_CSPROJ_TEXT MATCHES "<Version>([0-9]+)\\.([0-9]+)\\.([0-9]+)</Version>")
    message(FATAL_ERROR "No <Version>major.minor.patch</Version> in ${REDEFINITION_CSPROJ}")
endif()
set(REDEFINITION_VERSION_MAJOR ${CMAKE_MATCH_1})
set(REDEFINITION_VERSION_MINOR ${CMAKE_MATCH_2})
set(REDEFINITION_VERSION_PATCH ${CMAKE_MATCH_3})
set(REDEFINITION_VERSION "${CMAKE_MATCH_1}.${CMAKE_MATCH_2}.${CMAKE_MATCH_3}")
set(REDEFINITION_INI_TEMPLATE "${CMAKE_CURRENT_SOURCE_DIR}/ReDefinitionProxy.ini")
configure_file(ReDefinitionProxy.rc.in "${CMAKE_CURRENT_BINARY_DIR}/ReDefinitionProxy.rc" @ONLY)
# The resource holds the template: built again when it changes.
set_property(SOURCE "${CMAKE_CURRENT_BINARY_DIR}/ReDefinitionProxy.rc" APPEND PROPERTY OBJECT_DEPENDS
             "${REDEFINITION_INI_TEMPLATE}")

# The Windows SDK's dxc, for the raytracing shaders (DXIL, shader model 6.5) and the
# harness's DXIL pass; it signs them with the dxil.dll beside it, which Direct3D 12
# requires.
find_program(REDEFINITION_DXC dxc
    HINTS "$ENV{ProgramFiles\(x86\)}/Windows Kits/10/bin/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/x64")

# The raytracing passes (Raytracing.cpp), one entry point each, compiled to DXIL and
# embedded in dxgi.dll as RCDATA named RT_<entry>. Without dxc the proxy is built
# without them and says so in its log.
# The folders of the proxy's sources and the harnesses': every source includes its headers by their
# plain names, found through these, and the one header outside its folder (Raytracing/Shaders/RtContract.h)
# through the proxy's root.
set(REDEFINITION_HARNESS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../tests/DxgiProxy")
set(REDEFINITION_SOURCE_DIRS
    "${CMAKE_CURRENT_SOURCE_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}/Common" "${CMAKE_CURRENT_SOURCE_DIR}/Diagnostics"
    "${CMAKE_CURRENT_SOURCE_DIR}/FrameGeneration" "${CMAKE_CURRENT_SOURCE_DIR}/Latency"
    "${CMAKE_CURRENT_SOURCE_DIR}/Managed" "${CMAKE_CURRENT_SOURCE_DIR}/Raytracing"
    "${CMAKE_CURRENT_SOURCE_DIR}/SwapChain" "${CMAKE_CURRENT_SOURCE_DIR}/Upscalers")

set(REDEFINITION_RT_SHADER_DIR "${CMAKE_CURRENT_SOURCE_DIR}/Raytracing/Shaders")
# Each entry as file:entry point.
set(REDEFINITION_RT_ENTRIES
    Raytracing:SunVisibility Raytracing:AmbientOcclusion Raytracing:DiffuseLight Raytracing:SpecularLight
    Raytracing:PrimaryCheck Raytracing:ForwardLight Raytracing:DiffuseLightCached Raytracing:SpecularLightCached
    Denoise:DenoisePrepare Denoise:DenoiseTemporal Denoise:DenoiseVariance Denoise:DenoiseAtrous
    Reconstruct:ReconstructGuides Reconstruct:FrameGuides
    GpuInstances:GpuInstances
    Regenerate:RegenerationGuides RegenerateAlbedo:RegenerationAlbedo
    RadianceCache:CacheClear RadianceCache:CacheBegin RadianceCache:CacheResolve)
set(REDEFINITION_RT_OUTPUTS)
set(REDEFINITION_RT_RC "${CMAKE_CURRENT_BINARY_DIR}/RaytracingShaders.rc")
if(REDEFINITION_DXC)
    set(REDEFINITION_RT_RC_TEXT "")
    file(GLOB REDEFINITION_RT_SOURCES "${REDEFINITION_RT_SHADER_DIR}/*.hlsl" "${REDEFINITION_RT_SHADER_DIR}/*.hlsli"
        "${REDEFINITION_RT_SHADER_DIR}/*.h")
    foreach(item ${REDEFINITION_RT_ENTRIES})
        string(REPLACE ":" ";" parts "${item}")
        list(GET parts 0 file)
        list(GET parts 1 entry)
        set(output "${CMAKE_CURRENT_BINARY_DIR}/rt/${entry}.cso")
        add_custom_command(OUTPUT "${output}"
            COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_BINARY_DIR}/rt"
            COMMAND "${REDEFINITION_DXC}" -nologo -T cs_6_5 -E ${entry} -HV 2021 -O3 -WX
                -Fo "${output}" "${REDEFINITION_RT_SHADER_DIR}/${file}.hlsl"
            DEPENDS ${REDEFINITION_RT_SOURCES}
            COMMENT "Raytracing pass ${entry} compiled to DXIL")
        list(APPEND REDEFINITION_RT_OUTPUTS "${output}")
        string(APPEND REDEFINITION_RT_RC_TEXT "RT_${entry} RCDATA \"${output}\"\n")
    endforeach()
    file(WRITE "${REDEFINITION_RT_RC}" "${REDEFINITION_RT_RC_TEXT}")
    set_source_files_properties("${REDEFINITION_RT_RC}" PROPERTIES OBJECT_DEPENDS "${REDEFINITION_RT_OUTPUTS}")
else()
    message(WARNING "dxc not found: dxgi.dll is built without raytracing")
    file(WRITE "${REDEFINITION_RT_RC}" "")
endif()

add_library(ReDefinitionProxy SHARED
    Upscalers/AmdUpscaler.cpp
    Upscalers/AmdUpscaler.h
    Common/Log.cpp
    Common/Log.h
    Common/Config.cpp
    Common/Config.h
    Managed/D3d12Compute.cpp
    Managed/D3d12Compute.h
    Common/D3d12Util.h
    Upscalers/Dlss.cpp
    Upscalers/Dlss.h
    Upscalers/DlssRayReconstruction.cpp
    Upscalers/DlssRayReconstruction.h
    Common/FileUtil.cpp
    Common/FileUtil.h
    FrameGeneration/FidelityFx.cpp
    FrameGeneration/FidelityFx.h
    FrameGeneration/FrameGeneration.cpp
    FrameGeneration/FrameGeneration.h
    FrameGeneration/FrameGenerationCheck.cpp
    FrameGeneration/FrameGenerationContext.cpp
    FrameGeneration/FrameGenerationDump.cpp
    FrameGeneration/FrameGenerationInputs.cpp
    FrameGeneration/FrameGenerationUi.cpp
    FrameGeneration/FrameGenerationDlss.cpp
    FrameGeneration/FrameGenerationXess.cpp
    FrameGeneration/FrameGenerationPrepare.cpp
    FrameGeneration/FrameGenerationRate.cpp
    Latency/CpuSampler.cpp
    Latency/CpuSampler.h
    Diagnostics/GpuProfiler.cpp
    Diagnostics/GpuProfiler.h
    Latency/LoadMonitor.cpp
    Latency/LoadMonitor.h
    Latency/LowLatency.cpp
    Latency/LowLatency.h
    Managed/ManagedBridge.cpp
    Upscalers/NgxCore.cpp
    Upscalers/NgxCore.h
    Diagnostics/ScreenRecorder.cpp
    Diagnostics/ScreenRecorder.h
    Upscalers/NvidiaGpu.cpp
    Upscalers/NvidiaGpu.h
    Common/ProjectIdentity.h
    Raytracing/Raytracing.cpp
    Raytracing/Raytracing.h
    Raytracing/RaytracingCache.cpp
    Raytracing/RaytracingDenoise.cpp
    Raytracing/RaytracingBuild.cpp
    Raytracing/RaytracingBuild.h
    Raytracing/RaytracingBuildFrame.h
    Raytracing/RaytracingMegaGeometry.cpp
    Raytracing/RaytracingMegaGeometry.h
    Raytracing/RaytracingOmm.cpp
    Raytracing/RaytracingOmm.h
    Raytracing/RaytracingOmmBake.cpp
    Raytracing/RaytracingOmmBake.h
    Raytracing/RaytracingDevice.cpp
    Raytracing/RaytracingInternal.h
    Raytracing/RaytracingReconstruct.cpp
    Raytracing/RaytracingRegenerate.cpp
    Raytracing/RaytracingScene.cpp
    FrameGeneration/Streamline.cpp
    FrameGeneration/Streamline.h
    SwapChain/SwapChainProxy.cpp
    SwapChain/SwapChainProxy.h
    SwapChain/SwapChainProxyBuffers.cpp
    SwapChain/SwapChainProxyForwarding.cpp
    SwapChain/SwapChainProxyPacing.cpp
    SwapChain/SwapChainProxyPresent.cpp
    SwapChain/SwapChainProxyState.h
    SwapChain/SwapChainProxyStreamline.cpp
    SwapChain/SwapChainProxyXess.cpp
    FrameGeneration/XessFg.cpp
    FrameGeneration/XessFg.h
    Upscalers/XessUpscaler.cpp
    Upscalers/XessUpscaler.h
    SwapChain/DxgiExports.cpp
    ReDefinitionProxy.def
    "${CMAKE_CURRENT_BINARY_DIR}/ReDefinitionProxy.rc"
    "${REDEFINITION_RT_RC}"
    ${REDEFINITION_RT_OUTPUTS}
)

# AMD's FidelityFX API headers and Intel's XeSS and XeLL headers, MIT, vendored
# under extern/ (extern/XeSS/MIT.txt). The runtimes are DLLs loaded at run time;
# XeSS frame generation's declarations are the proxy's own (XessFg.h).
target_include_directories(ReDefinitionProxy PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/extern")
target_include_directories(ReDefinitionProxy PRIVATE ${REDEFINITION_SOURCE_DIRS})

# NVIDIA's Streamline headers, MIT (extern/Streamline/license.txt), for DLSS frame
# generation. NVIDIA's DLLs are loaded at run time.
target_include_directories(ReDefinitionProxy PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/extern/Streamline/include")

# The file is called dxgi.dll: Windows searches the application directory before
# system32, so it is loaded in place of the system's.
set_target_properties(ReDefinitionProxy PROPERTIES
    OUTPUT_NAME "dxgi"
    PREFIX ""
)

target_compile_definitions(ReDefinitionProxy PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    UNICODE
    _UNICODE
)

target_compile_options(ReDefinitionProxy PRIVATE /W4 /permissive-)

# Not dxgi.lib: an import of dxgi.dll from this dxgi.dll would resolve to itself.
# CreateDXGIFactory1, which the proxy calls too, is its own export.
target_link_libraries(ReDefinitionProxy PRIVATE d3d11 d3d12 winmm version windowscodecs ole32)

# A test driver that exercises the proxy without KSP. It lands in the same
# directory as dxgi.dll, so LoadLibrary("dxgi.dll") finds the proxy by the
# ordinary search order, as in the game.
add_executable(ProxyHarness "${REDEFINITION_HARNESS_DIR}/ProxyHarness.cpp" Common/FileUtil.cpp Common/FileUtil.h)
target_compile_definitions(ProxyHarness PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_compile_options(ProxyHarness PRIVATE /W4 /permissive-)
target_link_libraries(ProxyHarness PRIVATE d3d11 dxgi version dbghelp)
target_include_directories(ProxyHarness PRIVATE ${REDEFINITION_SOURCE_DIRS})
add_dependencies(ProxyHarness ReDefinitionProxy)

# The raytracing harness (docs/development/raytracing.md): the proxy's raytracing
# without KSP, against the CPU.
add_executable(RaytracingHarness "${REDEFINITION_HARNESS_DIR}/RaytracingHarness.cpp" "${REDEFINITION_HARNESS_DIR}/RaytracingOmmHarness.cpp" Raytracing/RaytracingOmmBake.cpp)
target_compile_definitions(RaytracingHarness PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_compile_options(RaytracingHarness PRIVATE /W4 /permissive-)
target_include_directories(RaytracingHarness PRIVATE ${REDEFINITION_SOURCE_DIRS} "${CMAKE_CURRENT_SOURCE_DIR}/extern"
    "${CMAKE_CURRENT_SOURCE_DIR}/extern/Streamline/include")
target_link_libraries(RaytracingHarness PRIVATE d3d11 d3d12 dxgi d3dcompiler)
add_dependencies(RaytracingHarness ReDefinitionProxy)

# The harness's ray query for the opacity micromaps' checks against the driver
# (RaytracingOmmHarness.cpp), DXIL as the passes are.
if(REDEFINITION_DXC)
    add_custom_command(TARGET RaytracingHarness POST_BUILD
        COMMAND "${REDEFINITION_DXC}" -nologo -T cs_6_5 -E main -HV 2021
            -Fo "$<TARGET_FILE_DIR:RaytracingHarness>/OmmProbe.cso" "${REDEFINITION_HARNESS_DIR}/OmmProbe.hlsl"
        COMMENT "OmmProbe.cso compiled to DXIL")
endif()

# The example mod's compute shader beside the harness, which compiles it through the proxy.
add_custom_command(TARGET ProxyHarness POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/../../docs/modders/examples/ReDefinitionExample/ExampleEffect.hlsl"
        "$<TARGET_FILE_DIR:ProxyHarness>/ExampleEffect.hlsl"
    COMMENT "ExampleEffect.hlsl copied beside the harness")

# The harness's DXIL compute pass, where the Windows SDK's dxc is found.
if(REDEFINITION_DXC)
    add_custom_command(TARGET ProxyHarness POST_BUILD
        COMMAND "${REDEFINITION_DXC}" -nologo -T cs_6_0 -E main
            -Fo "$<TARGET_FILE_DIR:ProxyHarness>/HarnessPass.cso" "${REDEFINITION_HARNESS_DIR}/HarnessPass.hlsl"
        COMMENT "HarnessPass.cso compiled to DXIL")
endif()

# Deploy next to the game if the path is known, mirroring what the C# project
# does. Overridable: -DKSP_DIR=...
if(NOT DEFINED KSP_DIR)
    set(KSP_DIR "C:/Program Files (x86)/Steam/steamapps/common/Kerbal Space Program")
endif()

if(EXISTS "${KSP_DIR}")
    add_custom_command(TARGET ReDefinitionProxy POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:ReDefinitionProxy>" "${KSP_DIR}/dxgi.dll"
        COMMENT "dxgi.dll copied to ${KSP_DIR}")

    # Only when it is not there yet: copy_if_different would put the defaults back
    # at every build, over what is set in the game folder.
    if(NOT EXISTS "${KSP_DIR}/ReDefinitionProxy.ini")
        add_custom_command(TARGET ReDefinitionProxy POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy
                "${CMAKE_CURRENT_SOURCE_DIR}/ReDefinitionProxy.ini" "${KSP_DIR}/ReDefinitionProxy.ini"
            COMMENT "ReDefinitionProxy.ini created in ${KSP_DIR}")
    endif()
endif()
