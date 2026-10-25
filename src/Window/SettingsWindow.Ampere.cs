using System.Collections.Generic;
using ReDefinition.Bridges;
using UnityEngine;

namespace ReDefinition.Window
{
    internal static partial class SettingsWindow
    {
        // The width of the preset row's value column.
        private const float AmpereValueWidth = 90f;

        // The package's preset (AmpereOptions) under the DLSS-G on RTX 30/20 row: one step through the values,
        // written into dlssg_sm86.ini where the package reads it when KSP starts. The rest of the package's
        // ini -- the multiplier ceiling, super resolution, frame timing -- is not the player's to choose:
        // the install sets what ReDefinition needs.
        private static List<DialogGUIBase> AmpereOptionRows()
        {
            List<DialogGUIBase> rows = new List<DialogGUIBase>();
            AmpereOptions.Option choice = AmpereOptions.Preset;
            DialogGUISlider slider = new DialogGUISlider(() => AmpereFrameGeneration.OptionIndex(choice), 0f,
                choice.Values.Length - 1, true, ControlWidth, -1f, f =>
                {
                    // The slider is set from the row in every frame: only a move away from
                    // what the file holds is the player choosing.
                    int index = Mathf.Clamp(Mathf.RoundToInt(f), 0, choice.Values.Length - 1);
                    AmpereFrameGeneration.SetOption(choice, index);
                });
            slider.tooltipText = TooltipText.Wrap(choice.Tooltip);
            slider.OptionInteractableCondition = () => AmpereFrameGeneration.Installed();

            rows.Add(new DialogGUIHorizontalLayout(0f, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                new DialogGUILabel(choice.Title, NameWidth),
                slider,
                new DialogGUISpace(10f),
                new DialogGUILabel(() => AmpereFrameGeneration.OptionLabel(choice), AmpereValueWidth)));
            // A change waits for a restart like every other (NoteRestart): the line under the window says so.
            return rows;
        }
    }
}
