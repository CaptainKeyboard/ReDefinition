namespace ReDefinition.Proxy
{
    // The library every call into the proxy is imported from. The proxy is called dxgi.dll because it stands
    // in for the system's, which is how the game loads it (ProxyLoad).
    internal static class ProxyDll
    {
        // The name the P/Invoke declarations and the loader use; the one place that spells it.
        internal const string Name = "dxgi.dll";
    }
}
