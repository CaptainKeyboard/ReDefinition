using System;

namespace ReDefinition.Proxy
{
    // The one way the bridges call an export of the dxgi.dll proxy.
    //
    // DllImport resolution failures surface as exceptions on first use, and
    // there is more than one shape of them: no file, no export, a 32 bit
    // library. All mean the same thing here: the native side is not there in
    // the form this build expects. What that means is the caller's business:
    // frame generation switches itself off, the counters and the load only
    // go quiet.
    internal static class ProxyCall
    {
        // True when the call went through; false when the native side is not there.
        public static bool TryNative(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            return false;
        }

        // TryNative, for an export that answers with a number.
        public static bool TryNative(Func<int> call, out int result)
        {
            result = 0;
            try
            {
                result = call();
                return true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            return false;
        }
    }
}
