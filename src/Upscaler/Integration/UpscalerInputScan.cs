using System;
using ReDefinition.Core;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // A test run's check of the upscaler's inputs (command line switch -redefinitionUpscalerScan; K-T2 2026-11-17: a GPU page fault inside DLSS in the first frames after a camera cut, only
    // with the own clouds drawn): for the frames after every cut the colour at render size and the motion vectors are read back without waiting, and their values that are not finite (half
    // floats with the exponent all ones: Inf and NaN) are counted and logged. Off without the switch; nothing is read then.
    internal static class UpscalerInputScan
    {
        // How many frames after a cut are read.
        private const int FramesAfterCut = 90;
        // How many frames after a cut are logged whatever they hold (the fault came within some ten frames of the cut).
        private const int LoggedFramesAfterCut = 12;

        private static readonly bool On = Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionUpscalerScan") >= 0;
        private static int framesLeft;
        private static int frameOfCut;
        private static int pending;

        // From the integration's Present, before the dispatch: the frame's colour and motion vectors, and whether the frame is a cut.
        internal static void Observe(RenderTexture colour, RenderTexture motion, bool cut)
        {
            if (!On) return;
            if (cut)
            {
                framesLeft = FramesAfterCut;
                frameOfCut = Time.frameCount;
            }
            if (framesLeft <= 0 || pending > 8) return;
            framesLeft--;
            int frame = Time.frameCount, since = frame - frameOfCut;
            Request(colour, "colour", frame, since);
            Request(motion, "motion", frame, since);
        }

        private static void Request(RenderTexture texture, string what, int frame, int since)
        {
            if (texture == null || !texture.IsCreated()) return;
            pending++;
            AsyncGPUReadback.Request(texture, 0, request =>
            {
                pending--;
                if (request.hasError) return;
                NativeArray<ushort> data = request.GetData<ushort>();
                int bad = 0, large = 0;
                ushort top = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    ushort h = data[i];
                    if ((h & 0x7C00) == 0x7C00)
                    {
                        bad++;
                        continue;
                    }
                    // The magnitude bits of a finite half: they order as the values do. 0x3C00 is 1.0, a whole screen of motion.
                    ushort m = (ushort)(h & 0x7FFF);
                    if (m > top) top = m;
                    if (m > 0x3C00) large++;
                }
                // The colour's pixels (four halves, RGBA) with a channel below zero, and those black in all three: DLSS's auto exposure takes the log of the luminance, which a
                // negative or zero luminance has no finite value for.
                int negative = 0, black = 0;
                ushort lowest = 0;
                if (texture.format == RenderTextureFormat.ARGBHalf)
                {
                    for (int i = 0; i + 3 < data.Length; i += 4)
                    {
                        bool below = false, zero = true;
                        for (int c = 0; c < 3; c++)
                        {
                            ushort h = data[i + c];
                            ushort m = (ushort)(h & 0x7FFF);
                            if (m != 0) zero = false;
                            if ((h & 0x8000) != 0 && m != 0 && (h & 0x7C00) != 0x7C00)
                            {
                                below = true;
                                if (m > lowest) lowest = m;
                            }
                        }
                        if (below) negative++;
                        if (zero) black++;
                    }
                }
                if (bad > 0 || negative > 0 || since <= LoggedFramesAfterCut)
                    Debug.Log(Log.Tag + " Upscaler input scan: frame " + frame + " (" + since + " after the cut), " + what + " " + texture.width + "x" + texture.height + " "
                              + texture.format + ": " + bad + " of " + data.Length + " half values not finite, largest finite magnitude " + HalfToFloat(top).ToString("G4")
                              + ", " + large + " above 1"
                              + (texture.format == RenderTextureFormat.ARGBHalf
                                  ? "; pixels with a channel below 0: " + negative + " (lowest -" + HalfToFloat(lowest).ToString("G4") + "), black: " + black
                                  : ""));
            });
        }

        // A finite half float's value from its magnitude bits.
        private static float HalfToFloat(ushort m)
        {
            int exponent = (m >> 10) & 0x1F, mantissa = m & 0x3FF;
            return exponent == 0 ? mantissa * Mathf.Pow(2f, -24f) : (1f + mantissa / 1024f) * Mathf.Pow(2f, exponent - 15);
        }
    }
}
