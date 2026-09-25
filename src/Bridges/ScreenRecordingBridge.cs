using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReDefinition.Bridges
{
    // The proxy's screen recording (ScreenRecorder.h): what the monitor shows, frame by
    // frame, the frames frame generation makes included, into ReDefinitionCaptures beside
    // KSP_x64.exe. A proxy without the exports makes both calls report that.
    internal static class ScreenRecordingBridge
    {
        private const string Proxy = "dxgi.dll";

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspRecordScreen(int frames);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspRecordScreenState(StringBuilder buffer, int size);

        // False where a recording runs or the proxy has none.
        internal static bool Start(int frames)
        {
            try
            {
                return KspRecordScreen(frames) == 1;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                return false;
            }
        }

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
