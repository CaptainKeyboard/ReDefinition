#pragma once

#include <cstdint>
#include <string>

struct ID3D11DeviceContext;
struct ID3D11Texture2D;

namespace redefinition
{
    // What the monitor showed, frame by frame, generated frames included: frame
    // generation's frames are presented by its own swapchain and never pass the game,
    // so only the desktop's own copy of the screen holds them (DXGI desktop
    // duplication). A cut-out around the middle of the game window, into PNG files in
    // a folder of their own under folder, with frames.txt giving each frame's present
    // time and how many frames the desktop composed since the one before.
    //
    // Runs in a thread of its own; false where a recording already runs. Where Windows
    // refuses the desktop's copy -- an exclusive fullscreen mode at another size than the
    // desktop's, say -- the game's own frames are recorded instead, as Present gets them
    // (RecordBackBuffer): the frames frame generation makes are then not among them.
    bool StartScreenRecording(int frames, const std::wstring& folder);

    // At each Present, with the game's device context and backbuffer: takes the frame
    // when a recording of the game's frames runs, and reads the taken ones back, one a
    // Present, once all are taken.
    void RecordBackBuffer(ID3D11DeviceContext* context, ID3D11Texture2D* backBuffer);

    // At each Present with frame generation attached, while a recording runs: when, and
    // which of the game's frames (Unity's frame count, as the packet and every dump give
    // it). frames.txt then names for each recorded frame the game's frame presented last
    // before it -- a frame frame generation made between two of the game's frames is
    // shown after the newer one's Present, so it carries the newer one's number too --
    // and presents.txt lists every Present.
    void NoteGamePresent(uint32_t gameFrame);

    // The last recording's outcome for the log and the Debug tab: "running",
    // "done: <folder>", or why it failed.
    std::string ScreenRecordingState();
}
