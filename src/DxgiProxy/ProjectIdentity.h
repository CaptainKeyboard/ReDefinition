#pragma once

namespace redefinition
{
    // What ReDefinition tells NVIDIA's libraries it is, DLSS (Dlss.cpp) and DLSS
    // frame generation (Streamline.cpp) alike: a custom engine with a project ID
    // of its own -- GUID-like, as NGX requires of a CUSTOM engine's project ID
    // (guide 5.2.1) -- and the Unity version KSP runs on.
    constexpr const char* kNvidiaProjectId = "c7f3a2d1-5e84-4b6a-9f02-7d1e3b8a4c65";
    constexpr const char* kEngineVersion = "2019.4.18f1";
}
