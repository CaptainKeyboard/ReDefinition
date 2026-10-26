using System;
using System.Collections.Generic;
using KSP.Localization;
using ReDefinition.Modules;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Vendor
{
    // What choosing a technique whose files are missing does: it opens, one after the other, the steps for the
    // RTX 30 package, the download of NVIDIA's files, the download of Intel's, the steps for AMD's DLL -- and
    // chooses the technique only once they are in place. A dialog the player cancels leaves the choice
    // as it was.
    internal static class TechniqueFiles
    {
        // The release page of AMD's FidelityFX SDK, where its signed DLL is.
        private const string AmdReleasesUrl = "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases";

        // Null where nothing is missing.
        public static FilesNeeded For(TechniqueAvailability.Needs needs)
        {
            if (needs == TechniqueAvailability.Needs.None) return null;
            // The tooltip's last line: what choosing it does first.
            string text;
            if ((needs & TechniqueAvailability.Needs.AmdDll) != 0)
                text = "AMD's DLL is not there: choosing it says where to get it.";
            else if ((needs & TechniqueAvailability.Needs.AmperePackage) != 0)
                text = "An RTX 30 or 20 needs the dlssg_for_sm86 package: choosing it installs it first.";
            else
            {
                List<string> parts = new List<string>();
                if ((needs & TechniqueAvailability.Needs.NvidiaFiles) != 0) parts.Add("NVIDIA's");
                if ((needs & TechniqueAvailability.Needs.IntelFiles) != 0) parts.Add("Intel's");
                text = string.Join(" and ", parts.ToArray()) + " files download when chosen.";
            }
            return new FilesNeeded
            {
                Text = text,
                Begin = done => Next(needs, done),
            };
        }

        // Opens the next step that is still missing -- the package, NVIDIA's files, Intel's files, AMD's DLL -- each one's
        // answer leading to the next; the continuation once nothing is left.
        private static void Next(TechniqueAvailability.Needs needs, Action done)
        {
            // The package first: once it is in place, NVIDIA's files for DLSS-G are among the missing
            // (NvidiaDownloader.Needed), which the next step then fetches.
            if ((needs & TechniqueAvailability.Needs.AmperePackage) != 0)
            {
                TechniqueAvailability.Needs rest = needs & ~TechniqueAvailability.Needs.AmperePackage;
                if (AmpereFrameGeneration.Installed()) Next(rest, done);
                else AmpereFrameGeneration.Show(() => Next(rest, done));
                return;
            }
            if ((needs & TechniqueAvailability.Needs.NvidiaFiles) != 0)
            {
                TechniqueAvailability.Needs rest = needs & ~TechniqueAvailability.Needs.NvidiaFiles;
                List<NvidiaFiles.Entry> missing = NvidiaDownloader.Needed();
                if (missing.Count == 0 || NvidiaDownloader.Running) Next(rest, done);
                else NvidiaDownloader.Confirm(missing, () => Next(rest, done));
                return;
            }
            if ((needs & TechniqueAvailability.Needs.IntelFiles) != 0)
            {
                TechniqueAvailability.Needs rest = needs & ~TechniqueAvailability.Needs.IntelFiles;
                IntelDownloader.Refresh();
                List<IntelFiles.Item> missing = IntelDownloader.Needed();
                if (missing.Count == 0 || IntelDownloader.Running) Next(rest, done);
                else IntelDownloader.Confirm(missing, () => Next(rest, done));
                return;
            }
            if ((needs & TechniqueAvailability.Needs.AmdDll) != 0)
            {
                // Not fetched by ReDefinition: the player puts it there, and chooses again.
                ShowAmdDll();
                return;
            }
            if (done != null) done();
        }

        // Says where AMD's DLL comes from and where it goes, with a link to the release page.
        private static void ShowAmdDll()
        {
            string message =
                "FSR 4 runs AMD's amd_fidelityfx_upscaler_dx12.dll, which ReDefinition does not ship: it is AMD's, from the"
                + " binaries of AMD's FidelityFX SDK (signedbin), and it is loaded by the proxy from next to KSP_x64.exe or from"
                + " the folder amdUpscalerDirectory names in ReDefinitionProxy.ini.\n\n"
                + "Put it there, then choose FSR 4 again. It runs FSR 4 on a Radeon RX 7000 or RX 9000, with AMD's"
                + " licence for the DLL.\n\n"
                + "Folder: " + NvidiaDownloader.GameFolder;
            DialogGUIBase[] rows =
            {
                new DialogGUIHorizontalLayout(
                    new DialogGUIFlexibleSpace(),
                    DialogOverWindow.Link("AMD FidelityFX SDK releases", () => Application.OpenURL(AmdReleasesUrl), 200f),
                    new DialogGUIFlexibleSpace()),
                new DialogGUIHorizontalLayout(
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton(Localizer.Format("#autoLOC_174783"), () => { }, 120f, 30f, true),
                    new DialogGUIFlexibleSpace()),
            };
            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionAmdDll", message, "AMD's FSR 4 DLL", HighLogic.UISkin,
                520f, rows);
            UnityMouseEvents.Shield(DialogOverWindow.Raise(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin, false)));
        }
    }
}
