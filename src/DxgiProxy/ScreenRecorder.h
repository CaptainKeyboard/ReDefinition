#pragma once

#include <string>

namespace redefinition
{
    // What the monitor showed, frame by frame, generated frames included: frame
    // generation's frames are presented by its own swapchain and never pass the game,
    // so only the desktop's own copy of the screen holds them (DXGI desktop
    // duplication). A cut-out around the middle of the game window, into PNG files in
    // a folder of their own under folder, with frames.txt giving each frame's present
    // time and how many frames the desktop composed since the one before.
    //
    // Runs in a thread of its own; false where a recording already runs.
    bool StartScreenRecording(int frames, const std::wstring& folder);

    // The last recording's outcome for the log and the Debug tab: "running",
    // "done: <folder>", or why it failed.
    std::string ScreenRecordingState();
}
