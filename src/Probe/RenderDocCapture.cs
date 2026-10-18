using System;
using System.Runtime.InteropServices;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // RenderDoc's in-application API, where KSP was started under RenderDoc
    // (tools/renderdoc_probe.ps1): the scene probe takes a frame capture in the middle
    // of its camera move, to look at Unity's motion vector pass draw by draw. Nothing
    // where renderdoc.dll is not in the process.
    //
    // renderdoc_app.h (RenderDoc 1.x): RENDERDOC_GetAPI(eRENDERDOC_API_Version_1_1_2,
    // &api) hands out a table of function pointers, of which SetCaptureFilePathTemplate
    // is the twelfth, TriggerCapture the sixteenth and TriggerMultiFrameCapture the
    // twenty-third.
    internal static class RenderDocCapture
    {
        private const int Version112 = 10102;
        private const int SetCaptureFilePathTemplateIndex = 11;
        private const int TriggerCaptureIndex = 15;
        private const int TriggerMultiFrameCaptureIndex = 22;

        private delegate int GetApi(int version, out IntPtr api);
        private delegate void SetPathTemplate([MarshalAs(UnmanagedType.LPStr)] string pathTemplate);
        private delegate void Trigger();
        private delegate void TriggerMulti(uint frames);

        private static Trigger trigger;
        private static TriggerMulti triggerMulti;
        private static bool looked;

        internal static bool Available
        {
            get
            {
                Look();
                return trigger != null;
            }
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            IntPtr module = GetModuleHandle("renderdoc.dll");
            if (module == IntPtr.Zero) return;
            IntPtr entry = GetProcAddress(module, "RENDERDOC_GetAPI");
            if (entry == IntPtr.Zero) return;
            GetApi getApi = (GetApi)Marshal.GetDelegateForFunctionPointer(entry, typeof(GetApi));
            IntPtr api;
            if (getApi(Version112, out api) != 1 || api == IntPtr.Zero) return;
            trigger = (Trigger)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(api, TriggerCaptureIndex * IntPtr.Size), typeof(Trigger));
            triggerMulti = (TriggerMulti)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(api, TriggerMultiFrameCaptureIndex * IntPtr.Size), typeof(TriggerMulti));
            SetPathTemplate setPath = (SetPathTemplate)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(api, SetCaptureFilePathTemplateIndex * IntPtr.Size), typeof(SetPathTemplate));
            string folder = CaptureFolder.Run;
            setPath(System.IO.Path.Combine(folder, "renderdoc"));
            Debug.Log(Log.Tag + " Probe: RenderDoc in the process, captures into " + folder + ".");
        }

        // The next frame, captured whole.
        internal static void Capture(string why)
        {
            Look();
            if (trigger == null) return;
            trigger();
            Debug.Log(Log.Tag + " Probe: RenderDoc capture of frame " + Time.frameCount + " (" + why + ").");
        }

        // The next frames, each captured whole: which frame a single trigger takes is
        // the next one RenderDoc starts, one either side of the caller's.
        internal static void CaptureFrames(uint frames, string why)
        {
            Look();
            if (triggerMulti == null) return;
            triggerMulti(frames);
            Debug.Log(Log.Tag + " Probe: RenderDoc capture of " + frames + " frames from " + Time.frameCount + " (" + why + ").");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
