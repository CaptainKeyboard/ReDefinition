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
        // eRENDERDOC_API_Version_1_1_2, and the entries' places in the table it hands out (counted from 0).
        private const int Version112 = 10102;
        private const int SetCaptureFilePathTemplateIndex = 11;
        private const int TriggerCaptureIndex = 15;
        private const int TriggerMultiFrameCaptureIndex = 22;

        // RENDERDOC_GetAPI: 1 where the version is served, with the table in api.
        private delegate int GetApi(int version, out IntPtr api);
        // SetCaptureFilePathTemplate: where the captures go, the path and file name's start.
        private delegate void SetPathTemplate([MarshalAs(UnmanagedType.LPStr)] string pathTemplate);
        // TriggerCapture: the next frame captured.
        private delegate void Trigger();
        // TriggerMultiFrameCapture: the next frames captured, this many.
        private delegate void TriggerMulti(uint frames);

        // The two capture entries, null where RenderDoc is not in the process; and whether Look has run.
        private static Trigger trigger;
        private static TriggerMulti triggerMulti;
        private static bool looked;

        // Whether RenderDoc is in the process and serves the API.
        internal static bool Available
        {
            get
            {
                Look();
                return trigger != null;
            }
        }

        // Finds renderdoc.dll in the process once, takes the capture entries from its API and points its captures
        // into the run's capture folder.
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

        // The handle of a DLL already loaded into the process, zero where it is not.
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string name);

        // The address of a DLL's exported function.
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
