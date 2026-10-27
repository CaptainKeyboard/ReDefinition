using ReDefinition.Proxy;

namespace ReDefinition.Settings
{
    // The frame generation row's technique: Off, or the technique the proxy makes its swapchain for --
    // Auto (DLSS where it runs, XeSS on an Intel GPU where its files are, FSR 3 otherwise), DLSS, FSR 3
    // or XeSS. The proxy takes it when the swapchain is made (ReDefinitionProxy.ini's
    // frameGenerationTechnique), so a change to another technique applies after a restart of KSP.
    internal static class FrameGenerationTechniqueSetting
    {
        // The row's values as the settings file holds them, Off besides the techniques.
        public const string Off = "Off";
        public const string Auto = "Auto";
        public const string Dlss = "Dlss";
        public const string Fsr = "Fsr";
        public const string Xess = "Xess";

        // What the settings file holds: the technique; Off is the frame generation switch's.
        public static readonly string[] Techniques = { Auto, Dlss, Fsr, Xess };

        // The row's buttons, in the order they stand.
        public static readonly string[] All = { Off, Auto, Dlss, Fsr, Xess };

        // Whether the text is one of the techniques the settings file may hold.
        public static bool Valid(string text)
        {
            return System.Array.IndexOf(Techniques, text) >= 0;
        }

        // The numbers the proxy gives the technique its swapchain runs (KspFgTechnique); 0 is none.
        public const int KindNone = 0;
        public const int KindFsr = 1;
        public const int KindDlss = 2;
        public const int KindXess = 3;

        // The proxy's number for a technique that is chosen or resolved: FSR 3 is 1 and DLSS is 2; anything
        // else counts as XeSS, the last technique a resolved choice can be.
        public static int KindOf(string technique)
        {
            return technique == Fsr ? KindFsr : technique == Dlss ? KindDlss : KindXess;
        }

        // The proxy's number for a technique by its name, or none for a value that is no technique (Auto, Off).
        public static int KindOfNamed(string technique)
        {
            return technique == Fsr ? KindFsr : technique == Dlss ? KindDlss : technique == Xess ? KindXess : KindNone;
        }

        // The button's text and the value column's.
        public static string Label(string value)
        {
            switch (value)
            {
                case Dlss: return "DLSS";
                case Fsr: return "FSR 3";
                case Xess: return "XeSS";
                default: return value;
            }
        }

        // The proxy's ini value: auto, dlss, fsr or xess.
        public static void Write(string technique)
        {
            if (Valid(technique)) ProxyIni.Set("frameGenerationTechnique", technique.ToLowerInvariant());
        }
    }
}
