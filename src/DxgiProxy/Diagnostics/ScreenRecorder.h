#pragma once

#include <cstdint>
#include <string>

// Declared only: the recorder takes them as pointers.
struct ID3D11DeviceContext;
struct ID3D11Texture2D;

namespace redefinition
{
    // Records what the monitor showed, frame by frame, generated frames included. Frame generation's
    // frames are presented by its own swapchain and never pass the game. Only the desktop's own copy
    // of the screen holds them (DXGI desktop duplication). The recording is a cut-out around the
    // middle of the game window, as PNG files in a folder of their own under folder.
    //
    // Runs in a thread of its own; false where a recording already runs. Where Windows refuses the
    // desktop's copy, the game's own frames are recorded instead (RecordBackBuffer).
    bool StartScreenRecording(int frames, const std::wstring& folder);

    // At each Present, with the game's device context and backbuffer: takes the frame when a
    // recording of the game's frames runs, and reads the taken ones back, one a Present, once all
    // are taken. Windows can refuse the desktop's copy, as in an exclusive fullscreen mode at
    // another size than the desktop's. The frames frame generation makes are then not among the
    // recorded ones.
    void RecordBackBuffer(ID3D11DeviceContext* context, ID3D11Texture2D* backBuffer);

    // At each Present with frame generation attached, while a recording runs. It notes the time and
    // which of the game's frames is presented, by Unity's frame count, as the packet and every dump
    // give it. The file frames.txt gives each frame's present time and how many frames the desktop
    // composed since the one before. It names for each recorded frame the game's frame presented
    // last before it. A frame frame generation made between two of the game's frames is shown after
    // the newer one's Present, so it carries the newer one's number too. The file presents.txt
    // lists every Present.
    void NoteGamePresent(uint32_t gameFrame);

    // The last recording's outcome for the log and the diagnostics window: "running",
    // "done: <folder>", or why it failed.
    std::string ScreenRecordingState();
}
