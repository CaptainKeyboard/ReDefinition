using System;
using UnityEngine;
using ReDefinition.Bridges;

namespace ReDefinition.Vendor
{
    // The classes of graphics card the profiles' values differ by: what the card
    // can run, not a model. A profile line "K (Rtx20 Rtx30), L" gives K to the
    // first two and L to the rest (ProfileValue).
    internal enum GpuClass
    {
        // Not asked yet, or the adapter did not say.
        Unknown,
        // GeForce RTX 20 (Turing) and the cards of that generation.
        Rtx20,
        // GeForce RTX 30 and RTX A (Ampere).
        Rtx30,
        // GeForce RTX 40 and RTX Ada (Ada Lovelace).
        Rtx40,
        // GeForce RTX 50 (Blackwell).
        Rtx50,
        // An NVIDIA card without DLSS: GeForce GTX, or older.
        NvidiaOlder,
        // Radeon RX 7000 (RDNA 3) and newer but the RX 9000: FSR 4 where AMD's DLL has it.
        Radeon7000,
        // Radeon RX 9000 (RDNA 4): FSR 4 and FSR Ray Regeneration.
        Radeon9000,
        // A Radeon before the RX 7000, or an APU graphics core.
        RadeonOlder,
        // Intel Arc and the other Intel adapters.
        Intel,
        // Anything else: an adapter of another vendor, or an AMD one that is no Radeon.
        Other,
    }

    // The GpuClass of the card KSP renders on, and what the profiles and settings ask of one.
    internal static class GpuClasses
    {
        // The PCI vendor ids of NVIDIA, AMD and Intel.
        private const int Nvidia = 0x10DE;
        private const int Amd = 0x1002;
        private const int IntelVendor = 0x8086;

        // NVAPI's architecture ids (nvapi.h): Turing 0x160, Ampere 0x170, Ada 0x190,
        // Blackwell 0x1B0.
        private const uint Ampere = 0x170;
        private const uint Blackwell = 0x1B0;

        // The class once it is certain; Unknown until then, so that Current asks again.
        private static GpuClass current = GpuClass.Unknown;

        // The card KSP renders on. Asked once the proxy has answered (NVAPI's architecture),
        // by the adapter's name before.
        public static GpuClass Current
        {
            get
            {
                if (current != GpuClass.Unknown) return current;
                try
                {
                    uint architecture = 0, implementation = 0;
                    bool known = FrameGenerationBridge.NvidiaGpu(out architecture, out implementation);
                    // The package for an RTX 30 or 20 (dlssg_for_sm86) makes NVAPI answer as a Blackwell card
                    // (0x1B0 for an RTX A4500, 0x170 without it, in the proxy's log), so that DLSS frame
                    // generation runs; the adapter's name is the card then.
                    if (known && Spoofed()) known = false;
                    GpuClass found = Of(SystemInfo.graphicsDeviceVendorID, SystemInfo.graphicsDeviceName,
                                        known ? architecture : 0, known ? implementation : 0);
                    // A name alone for an NVIDIA card may be wrong for a new model, so it is asked again
                    // until NVAPI has answered.
                    if (found != GpuClass.Unknown && (known || SystemInfo.graphicsDeviceVendorID != Nvidia || Spoofed())) current = found;
                    return found;
                }
                catch (Exception)
                {
                    // Outside the game, in the tests, Unity's SystemInfo does not answer.
                    return GpuClass.Unknown;
                }
            }
        }

        // Whether the package that makes an RTX 30 or 20 pass for a newer card lies next to KSP_x64.exe.
        private static bool Spoofed()
        {
            try
            {
                return AmperePackage.Installed(System.IO.Path.GetDirectoryName(Application.dataPath));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The class of an adapter. For NVIDIA the architecture NVAPI gave, if it did (0 if not);
        // otherwise the name.
        internal static GpuClass Of(int vendor, string name, uint architecture, uint implementation)
        {
            name = name ?? "";
            if (vendor == Nvidia) return OfNvidia(name, architecture, implementation);
            if (vendor == Amd) return OfAmd(name);
            if (vendor == IntelVendor) return GpuClass.Intel;
            return vendor == 0 ? GpuClass.Unknown : GpuClass.Other;
        }

        // An NVIDIA card's class: by NVAPI's architecture where it answered, else by the RTX and model number in
        // its name; Unknown for a name it cannot place.
        private static GpuClass OfNvidia(string name, uint architecture, uint implementation)
        {
            if (architecture != 0)
            {
                if (!NvidiaFiles.DlssRuns(architecture, implementation)) return GpuClass.NvidiaOlder;
                if (architecture >= Blackwell) return GpuClass.Rtx50;
                if (architecture >= NvidiaFiles.Ada) return GpuClass.Rtx40;
                if (architecture >= Ampere) return GpuClass.Rtx30;
                return GpuClass.Rtx20;
            }
            string upper = name.ToUpperInvariant();
            if (upper.IndexOf("RTX", StringComparison.Ordinal) < 0) return GpuClass.NvidiaOlder;
            // "RTX A4500", "RTX A6000": Ampere. "RTX 4000 Ada", "RTX 6000 Ada": Ada. Neither carries a
            // generation number the way GeForce's four digits do.
            if (upper.IndexOf(" ADA", StringComparison.Ordinal) >= 0) return GpuClass.Rtx40;
            if (upper.IndexOf("RTX A", StringComparison.Ordinal) >= 0) return GpuClass.Rtx30;
            if (upper.IndexOf("PRO 6000", StringComparison.Ordinal) >= 0
                || upper.IndexOf("BLACKWELL", StringComparison.Ordinal) >= 0) return GpuClass.Rtx50;
            if (upper.IndexOf("QUADRO RTX", StringComparison.Ordinal) >= 0 || upper.IndexOf("TITAN RTX", StringComparison.Ordinal) >= 0)
                return GpuClass.Rtx20;
            // The first digit pair of the model number: 2060 is 20, 3090 30, 4080 40, 5090 50.
            int at = upper.IndexOf("RTX ", StringComparison.Ordinal) + 4;
            int generation = 0;
            for (int i = 0; i < 2 && at + i < upper.Length && char.IsDigit(upper[at + i]); i++)
                generation = generation * 10 + (upper[at + i] - '0');
            if (generation >= 50) return GpuClass.Rtx50;
            if (generation >= 40) return GpuClass.Rtx40;
            if (generation >= 30) return GpuClass.Rtx30;
            if (generation >= 20) return GpuClass.Rtx20;
            return GpuClass.Unknown;
        }

        // A Radeon's class by the first digit of its RX model number; RadeonOlder for a Radeon without one.
        private static GpuClass OfAmd(string name)
        {
            string upper = name.ToUpperInvariant();
            int rx = upper.IndexOf("RX ", StringComparison.Ordinal);
            if (rx < 0) return upper.IndexOf("RADEON", StringComparison.Ordinal) >= 0 ? GpuClass.RadeonOlder : GpuClass.Other;
            int at = rx + 3;
            // "RX 9070 XT", "RX 7900 XTX", "RX 6800": the first digit of the model number.
            if (at < upper.Length && upper[at] == '9') return GpuClass.Radeon9000;
            if (at < upper.Length && upper[at] == '7') return GpuClass.Radeon7000;
            return GpuClass.RadeonOlder;
        }

        // Whether the card is a GeForce RTX or an NVIDIA RTX workstation card, Turing or newer.
        public static bool IsRtx(GpuClass gpu)
        {
            return gpu == GpuClass.Rtx20 || gpu == GpuClass.Rtx30 || gpu == GpuClass.Rtx40 || gpu == GpuClass.Rtx50;
        }

        // Whether the card is one of an RTX 20 or 30: the generations NVIDIA gives neither
        // multi frame generation nor Dynamic.
        public static bool IsRtx20Or30(GpuClass gpu)
        {
            return gpu == GpuClass.Rtx20 || gpu == GpuClass.Rtx30;
        }

        // The class by the name a profile line writes, or Unknown.
        public static GpuClass Parse(string text)
        {
            GpuClass gpu;
            return Enum.TryParse(text, true, out gpu) && Enum.IsDefined(typeof(GpuClass), gpu) ? gpu : GpuClass.Unknown;
        }
    }
}
