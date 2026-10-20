# The proxy: a dxgi.dll that hands Unity a shared D3D11 texture as its backbuffer
# and presents through a D3D12 swapchain, with NVIDIA's DLSS or AMD's FSR frame
# generation, and runs DLSS and AMD's upscaler for the mod
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
set(REDEFINITION_RT_SHADER_DIR "${CMAKE_CURRENT_SOURCE_DIR}/RaytracingShaders")
# Each entry as file:entry point.
set(REDEFINITION_RT_ENTRIES
    Raytracing:SunVisibility Raytracing:AmbientOcclusion Raytracing:DiffuseLight Raytracing:SpecularLight
    Raytracing:PrimaryCheck Raytracing:ForwardLight
    Denoise:DenoisePrepare Denoise:DenoiseTemporal Denoise:DenoiseVariance Denoise:DenoiseAtrous
    Reconstruct:ReconstructGuides Reconstruct:FrameGuides
    GpuInstances:GpuInstances
    Regenerate:RegenerationGuides RegenerateAlbedo:RegenerationAlbedo)
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
    AmdUpscaler.cpp
    AmdUpscaler.h
    Log.cpp
    Log.h
    Config.cpp
    Config.h
    D3d12Compute.cpp
    D3d12Compute.h
    D3d12Util.h
    Dlss.cpp
    Dlss.h
    FileUtil.cpp
    FileUtil.h
    FidelityFx.cpp
    FidelityFx.h
    FrameGeneration.cpp
    FrameGeneration.h
    FrameGenerationCheck.cpp
    FrameGenerationContext.cpp
    FrameGenerationDump.cpp
    FrameGenerationInputs.cpp
    FrameGenerationDlss.cpp
    FrameGenerationPrepare.cpp
    FrameGenerationRate.cpp
    CpuSampler.cpp
    CpuSampler.h
    GpuProfiler.cpp
    GpuProfiler.h
    LoadMonitor.cpp
    LoadMonitor.h
    LowLatency.cpp
    LowLatency.h
    ManagedBridge.cpp
    ScreenRecorder.cpp
    ScreenRecorder.h
    NvidiaGpu.cpp
    NvidiaGpu.h
    ProjectIdentity.h
    Raytracing.cpp
    Raytracing.h
    RaytracingDenoise.cpp
    RaytracingDevice.cpp
    RaytracingInternal.h
    RaytracingReconstruct.cpp
    RaytracingRegenerate.cpp
    RaytracingScene.cpp
    Streamline.cpp
    Streamline.h
    SwapChainProxy.cpp
    SwapChainProxy.h
    SwapChainProxyBuffers.cpp
    SwapChainProxyForwarding.cpp
    SwapChainProxyPacing.cpp
    SwapChainProxyPresent.cpp
    SwapChainProxyState.h
    SwapChainProxyStreamline.cpp
    DxgiExports.cpp
    ReDefinitionProxy.def
    "${CMAKE_CURRENT_BINARY_DIR}/ReDefinitionProxy.rc"
    "${REDEFINITION_RT_RC}"
    ${REDEFINITION_RT_OUTPUTS}
)

# AMD's FidelityFX API headers, MIT, vendored under extern/. The runtime is a DLL
# loaded at run time.
target_include_directories(ReDefinitionProxy PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/extern")

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
add_executable(ProxyHarness ProxyHarness.cpp FileUtil.cpp FileUtil.h)
target_compile_definitions(ProxyHarness PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_compile_options(ProxyHarness PRIVATE /W4 /permissive-)
target_link_libraries(ProxyHarness PRIVATE d3d11 dxgi version)
add_dependencies(ProxyHarness ReDefinitionProxy)

# The raytracing harness (docs/development/raytracing.md): the proxy's raytracing
# without KSP, against the CPU.
add_executable(RaytracingHarness RaytracingHarness.cpp)
target_compile_definitions(RaytracingHarness PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_compile_options(RaytracingHarness PRIVATE /W4 /permissive-)
target_include_directories(RaytracingHarness PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/extern"
    "${CMAKE_CURRENT_SOURCE_DIR}/extern/Streamline/include")
target_link_libraries(RaytracingHarness PRIVATE d3d11 dxgi d3dcompiler)
add_dependencies(RaytracingHarness ReDefinitionProxy)

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
            -Fo "$<TARGET_FILE_DIR:ProxyHarness>/HarnessPass.cso" "${CMAKE_CURRENT_SOURCE_DIR}/HarnessPass.hlsl"
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
