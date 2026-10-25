using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace ReDefinition.Settings
{
    // ReDefinitionProxy.ini beside KSP_x64.exe, whose [Proxy] section the proxy reads: at
    // the start for some keys, within a second of the file being saved for the others
    // (docs/reference/proxy-settings.md). The profiles' settings that have no row of their own
    // go in through here.
    internal static class ProxyIni
    {
        // The section the proxy reads.
        private const string Section = "Proxy";

        // Windows' own reading and writing of ini files, as the proxy reads the file.
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileString(string section, string key, string fallback, StringBuilder buffer,
                                                           uint size, string file);

        // Writes the key's value into the section, the file made where it is missing.
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern bool WritePrivateProfileString(string section, string key, string value, string file);

        // ReDefinitionProxy.ini's full path, beside KSP_x64.exe.
        public static string Path
        {
            get
            {
                return System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Application.dataPath)) ?? "",
                    "ReDefinitionProxy.ini");
            }
        }

        // The value of a key; null where the file or the key is not there.
        public static string Get(string key)
        {
            string file = Path;
            if (!File.Exists(file)) return null;
            StringBuilder value = new StringBuilder(260);
            GetPrivateProfileString(Section, key, "\u0001", value, (uint)value.Capacity, file);
            return value.ToString() == "\u0001" ? null : value.ToString();
        }

        // Writes the key where the file is there (the proxy writes it at its first start);
        // true where it holds the value afterwards. A value it holds already is not written
        // again, since the proxy rereads the file for each write.
        public static bool Set(string key, string value)
        {
            string file = Path;
            if (!File.Exists(file)) return false;
            if (Get(key) == value) return true;
            return WritePrivateProfileString(Section, key, value, file);
        }
    }
}
