using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReDefinition.Proxy
{
    // The proxy's screen recording (ScreenRecorder.h): what the monitor shows, frame by
    // frame, the frames frame generation makes included, into ReDefinitionCaptures beside
    // KSP_x64.exe. A proxy without the exports makes both calls report that.
    internal static class ScreenRecordingBridge
    {
        // The proxy's file name, as the DllImports below resolve it in this process.
        private const string Proxy = ProxyDll.Name;

        // Starts a recording of that many frames, with dumps not 0 the input dump too; 0 where one already runs.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspRecordScreen(int frames, int dumps);

        // The recording's state as text; its length.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspRecordScreenState(StringBuilder buffer, int size);

        // False where a recording runs or the proxy has none. With dumps, frame
        // generation's input dump beside it.
        internal static bool Start(int frames, bool dumps)
        {
            try
            {
                return KspRecordScreen(frames, dumps ? 1 : 0) == 1;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                return false;
            }
        }

        // What the recording is doing, as the proxy says, or that the proxy has no recording.
        internal static string State()
        {
            try
            {
                StringBuilder buffer = new StringBuilder(512);
                KspRecordScreenState(buffer, buffer.Capacity);
                return buffer.ToString();
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                return "the installed dxgi.dll has no screen recording";
            }
        }
    }
}
