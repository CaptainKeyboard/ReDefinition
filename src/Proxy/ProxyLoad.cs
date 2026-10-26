using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Proxy
{
    // Whether the dxgi.dll next to KSP_x64.exe is the one the game loaded. Windows
    // may refuse it: Smart App Control and other code integrity policies block a DLL
    // without a trusted signature, and the game then runs on System32's dxgi.dll
    // without a word (seen with Smart App Control on, 2026-10-19: the
    // CodeIntegrity log named the proxy, and KSP loaded System32's).
    internal static class ProxyLoad
    {
        // A loaded module's handle by name or full path; zero where none of it is loaded.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        // The full path of a loaded module.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileName(IntPtr module, StringBuilder name, int size);

        // Whether Refused has found out, and what it found.
        private static bool asked;
        private static string refused;

        // Why the proxy beside the game is not the dxgi.dll it loaded, or null: where
        // it is, or where there is none.
        internal static string Refused
        {
            get
            {
                if (asked) return refused;
                // Where it cannot be found out -- off Unity's main thread, or outside the
                // game --, the message that the proxy is missing stands, and the next call
                // asks again.
                try
                {
                    refused = Ask();
                    asked = true;
                    if (refused != null) Debug.LogWarning(Log.Tag + " " + refused);
                }
                catch (Exception)
                {
                    refused = null;
                }
                return refused;
            }
        }

        // The refusal, put into words with what may have caused it; null where the proxy is the one loaded or
        // there is none beside the game.
        private static string Ask()
        {
            string game = Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));
            string beside = Path.Combine(game ?? "", ProxyDll.Name);
            if (!File.Exists(beside)) return null;
            // By its full path: the proxy loads System32's dxgi.dll itself, and with two
            // modules of one name loaded, which the name alone gives is not defined
            // (GetModuleHandle's documentation).
            if (GetModuleHandle(Path.GetFullPath(beside)) != IntPtr.Zero) return null;
            IntPtr module = GetModuleHandle(ProxyDll.Name);
            if (module == IntPtr.Zero) return null;
            StringBuilder name = new StringBuilder(1024);
            if (GetModuleFileName(module, name, name.Capacity) == 0) return null;
            string loaded = Path.GetFullPath(name.ToString());

            string why = SmartAppControlOn()
                ? "Smart App Control is on and blocks DLLs without a trusted signature, which the proxy has not. It is"
                  + " switched off in Windows Security, under App & browser control, Smart App Control settings."
                : "A code integrity policy or a virus scanner may have blocked it.";
            return "Windows did not load ReDefinition's dxgi.dll next to KSP_x64.exe, but " + loaded + ". " + why
                   + " Windows names a blocked DLL in the event log, under Applications and Services Logs, Microsoft,"
                   + " Windows, CodeIntegrity, Operational.";
        }

        // Smart App Control's state as Windows keeps it: 0 off, 1 on, 2 evaluation,
        // which only watches.
        private static bool SmartAppControlOn()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy"))
                {
                    object state = key == null ? null : key.GetValue("VerifiedAndReputablePolicyState");
                    return state is int && (int)state == 1;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
