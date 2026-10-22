namespace ReDefinition.Settings
{
    // The low latency row: Auto, or Off. Auto is what the proxy does by default -- the
    // latency brake, and the card's own technique where it lowers the latency, which
    // runs alone (XeLL on an Intel adapter, Anti-Lag 2 on a Radeon once it has been
    // tried there); Off writes the switches off into the proxy's ini.
    internal static class LowLatencySetting
    {
        public const string Auto = "Auto";
        public const string Off = "Off";

        public static readonly string[] All = { Auto, Off };

        public static bool Valid(string text)
        {
            return text == Auto || text == Off;
        }

        // The proxy's keys for a choice: lowLatency (Reflex, XeLL), latencyBrake. Anti-Lag 2
        // (antiLag2) is left to its own test on a Radeon and written off with Off only.
        public static void Write(string choice)
        {
            string value = choice == Off ? "0" : "1";
            ProxyIni.Set("lowLatency", value);
            ProxyIni.Set("latencyBrake", value);
            if (choice == Off) ProxyIni.Set("antiLag2", "0");
        }
    }
}
