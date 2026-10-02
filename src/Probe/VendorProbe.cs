using System;
using System.Collections;
using System.Runtime.InteropServices;
using ReDefinition.Bridges;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // The vendors' own checks of the inputs, driven by the scene probe
    // (-redefinitionProbeVendor): their debug visualizations answer hotkeys, and
    // draw into the frames that go to the screen, which the proxy's desktop
    // recording takes. Needs NVIDIA's development DLLs in the proxy's
    // streamlineDirectory and dlssDirectory, and vendorDiagnostics=1.
    //
    // DLSS frame generation's visualization: ctrl+shift+v on and off, F5 to the next
    // mode -- application inputs, motion vector resampling, clip to previous clip
    // matrix, depth and motion vector alignment, UI hints (DLSS-FG Programming Guide,
    // Debug Visualization). DLSS's and Ray Reconstruction's overlay of their inputs:
    // ctrl+alt+F12 through its layers (DLSS Programming Guide 8.2, DLSS-RR
    // Integration Guide 8.1). Streamline's buffer visualizer: ctrl+shift+Insert on and
    // off, ctrl+shift+End to the next view (Debugging - SL ImGUI).
    internal static class VendorProbe
    {
        internal const string Argument = "-redefinitionProbeVendor";

        private const ushort Control = 0x11;
        private const ushort Shift = 0x10;
        private const ushort Alt = 0x12;
        private const ushort KeyV = 0x56;
        private const ushort F5 = 0x74;
        private const ushort F12 = 0x7B;
        private const ushort Insert = 0x2D;
        private const ushort End = 0x23;

        // Modes cycled, with room for more than each guide lists.
        private const int FrameGenerationModes = 8;
        private const int OverlayLayers = 11;
        private const int StreamlineViews = 4;
        private const int RecordedFrames = 40;

        internal static bool Requested
        {
            get
            {
                foreach (string arg in Environment.GetCommandLineArgs())
                    if (string.Equals(arg, Argument, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        // Each state shown: recorded while the camera moves (the motion vector and
        // matrix tests need motion), then the next.
        internal static IEnumerator Run(Func<string, IEnumerator> move)
        {
            // KSP's own use of the same keys, off for the run and never saved: F5 is
            // its quicksave, and alt+F12, inside ctrl+alt+F12, its debug console.
            KeyCodeExtended quicksave = GameSettings.QUICKSAVE.primary;
            KeyCodeExtended quicksaveAlternative = GameSettings.QUICKSAVE.secondary;
            GameSettings.QUICKSAVE.primary = new KeyCodeExtended(KeyCode.None);
            GameSettings.QUICKSAVE.secondary = new KeyCodeExtended(KeyCode.None);
            try
            {
                yield return Checks(move);
            }
            finally
            {
                GameSettings.QUICKSAVE.primary = quicksave;
                GameSettings.QUICKSAVE.secondary = quicksaveAlternative;
            }
        }

        private static IEnumerator Checks(Func<string, IEnumerator> move)
        {
            yield return Record("plain", move);

            Press(Control, Shift, KeyV);
            for (int mode = 0; mode < FrameGenerationModes; mode++)
            {
                yield return Record("dlssg-mode" + mode, move);
                Press(F5);
            }
            Press(Control, Shift, KeyV);

            for (int layer = 0; layer < OverlayLayers; layer++)
            {
                Press(Control, Alt, F12);
                // KSP's console opened with it; alt+F12 alone closes it again and
                // leaves DLSS's overlay where it is.
                yield return null;
                Press(Alt, F12);
                yield return Record("dlss-overlay" + layer, move);
            }

            Press(Control, Shift, Insert);
            for (int view = 0; view < StreamlineViews; view++)
            {
                yield return Record("sl-view" + view, move);
                Press(Control, Shift, End);
            }
            Press(Control, Shift, Insert);
        }

        private static IEnumerator Record(string name, Func<string, IEnumerator> move)
        {
            // Settle a moment on the new state, then record during the move.
            for (int i = 0; i < 30; i++) yield return null;
            bool started = ScreenRecordingBridge.Start(RecordedFrames, false);
            Debug.Log(Log.Tag + " Probe: vendor " + name + (started ? ", recording." : ", not recorded: "
                                                                     + ScreenRecordingBridge.State()));
            yield return move(name);
            // The recording writes in a thread of its own; the next waits for it.
            float until = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < until)
            {
                string state = ScreenRecordingBridge.State();
                if (!state.StartsWith("recording", StringComparison.Ordinal)
                    && !state.StartsWith("saving", StringComparison.Ordinal)) break;
                yield return null;
            }
            Debug.Log(Log.Tag + " Probe: vendor " + name + ": " + ScreenRecordingBridge.State());
        }

        // A key combination, pressed and let go, through Windows' input queue: the
        // DLLs read it as the player's.
        private static void Press(params ushort[] keys)
        {
            Input[] inputs = new Input[keys.Length * 2];
            for (int i = 0; i < keys.Length; i++)
            {
                inputs[i] = Key(keys[i], false);
                inputs[keys.Length * 2 - 1 - i] = Key(keys[i], true);
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
        }

        private static Input Key(ushort key, bool up)
        {
            return new Input
            {
                Type = 1,
                Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u },
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort Scan;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        // INPUT with the union as large as its largest member, MOUSEINPUT.
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public KeyboardInput Keyboard;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);
    }
}
