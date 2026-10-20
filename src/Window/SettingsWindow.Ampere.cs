using System.Collections.Generic;
using ReDefinition.Bridges;
using UnityEngine;

namespace ReDefinition.Window
{
    internal static partial class SettingsWindow
    {
        private const float AmpereValueWidth = 90f;

        // The package's ini choices (AmpereOptions) under the DLSS-G on RTX 30/20 row: one
        // step through the values each, written into dlssg_sm86.ini where the package reads
        // them when KSP starts. Super resolution's has the button that takes its files
        // out of the zip beside it; without them the choice stays Off.
        private static List<DialogGUIBase> AmpereOptionRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            foreach (AmpereOptions.Option option in AmpereOptions.All)
            {
                bool superResolution = option == AmpereOptions.SuperResolution;
                AmpereOptions.Option choice = option;
                DialogGUISlider slider = new DialogGUISlider(() => AmpereFrameGeneration.OptionIndex(choice), 0f,
                    choice.Values.Length - 1, true, ControlWidth, -1f, f =>
                    {
                        // The slider is set from the row in every frame: only a move away from
                        // what the file holds is the player choosing.
                        int index = Mathf.Clamp(Mathf.RoundToInt(f), 0, choice.Values.Length - 1);
                        AmpereFrameGeneration.SetOption(choice, index);
                    });
                slider.tooltipText = TooltipText.Wrap(choice.Tooltip);
                slider.OptionInteractableCondition = () => AmpereFrameGeneration.Installed()
                                                           && (!superResolution || AmpereFrameGeneration.SuperResolutionInstalled());

                List<DialogGUIBase> row = new List<DialogGUIBase>
                {
                    new DialogGUILabel(choice.Title, NameWidth),
                    slider,
                    new DialogGUISpace(10f),
                    new DialogGUILabel(() => AmpereFrameGeneration.Installed()
                        ? AmpereFrameGeneration.OptionLabel(choice) : "<color=#9a9a9a>-</color>", AmpereValueWidth),
                };
                if (superResolution)
                {
                    DialogGUIButton files = new DialogGUIButton(
                        () => AmpereFrameGeneration.SuperResolutionInstalled() ? "SR files in place" : "Install SR files",
                        AmpereFrameGeneration.StartSuperResolution, 120f, 18f, false);
                    files.tooltipText = TooltipText.Wrap("Takes " + AmperePackage.FolderName + "\\sr\\nvngx_dlss.dll and sr_kernels.dll, about 180 MB,\n"
                                                         + "out of the " + AmperePackage.ZipPattern + " in KSP's folder.\n"
                                                         + "The choice can be set once they are in place.");
                    files.OptionInteractableCondition = () => AmpereFrameGeneration.Installed() && !AmpereFrameGeneration.Running
                                                              && AmpereFrameGeneration.Zip() != null
                                                              && !AmpereFrameGeneration.SuperResolutionInstalled();
                    row.Add(files);
                }
                rows.Add(new DialogGUIHorizontalLayout(0f, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft, row.ToArray()));
            }
            rows.Add(new DialogGUILabel(AmpereOptionsNote, true));
            return rows;
        }

        // Below the choices: that they are the package's ini, and once one was changed, that it applies
        // after a restart.
        private static string AmpereOptionsNote()
        {
            return AmpereFrameGeneration.OptionsChanged
                ? "<color=#ffd966>Changed: dlssg_sm86.ini is read when KSP starts, so this applies after a restart of KSP.</color>"
                : "<color=#9a9a9a>These are dlssg_sm86.ini's; the package reads it when KSP starts, so a change applies after a restart.</color>";
        }
    }
}
