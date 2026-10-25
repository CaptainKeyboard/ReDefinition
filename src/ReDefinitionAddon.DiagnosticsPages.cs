using System.Collections.Generic;
using FidelityFX.FSR3;
using ReDefinition.Bridges;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition
{
    // The diagnostics window's pages.
    public partial class ReDefinitionAddon
    {
        // A page's heading, with a little space under it.
        private void PageHeading(string text)
        {
            GUILayout.Label(text, styles.Heading);
            GUILayout.Space(2f);
        }

        // The options that pass the search and the "Changed" filter, whatever page they are on,
        // under the heading of their page.
        private void DrawResults()
        {
            List<DebugOption> found = Found();
            string what = found.Count + (found.Count == 1 ? " option" : " options")
                          + (query.Trim().Length > 0 ? " matching \"" + query.Trim() + "\"" : "")
                          + (changedOnly ? (query.Trim().Length > 0 ? ", " : " ") + "away from their default" : "");
            PageHeading(what);
            if (found.Count == 0)
            {
                GUILayout.Label(changedOnly && query.Trim().Length == 0
                    ? "Every option of this window is at its default."
                    : "Nothing matches.", styles.Dim);
                return;
            }
            foreach (DebugCategory page in Pages)
            {
                List<DebugOption> onPage = FoundOn(page);
                if (onPage.Count == 0) continue;
                GUILayout.Space(6f);
                GUILayout.Label(DebugOptionSet.Title(page), styles.Heading);
                GUILayout.Space(2f);
                DrawOptions(onPage, false);
            }
        }

        // The verdict, the main switches, the technique, mode and sharpness, the fallback
        // with its retry, the other mods' verdict and the frame rate with the upscaler on
        // against off.
        private void DrawOverview()
        {
            bool active = rig != null && rig.Ready;
            GUILayout.Label(active
                ? "Active: " + rig.RenderSize.x + "x" + rig.RenderSize.y + "  ->  "
                  + rig.DisplaySize.x + "x" + rig.DisplaySize.y
                  // The capture goes on for the proxy's attempts, which need its
                  // packets; the player sees that nothing is generated.
                  + (contextFailing ? "  -- " + ContextFailedNote() : "")
                : rig != null && rig.PassThrough
                    // What stopped the upscaler follows below.
                    ? (wantEnabled ? "Upscaler not set up" : "Upscaler off")
                      + (contextFailingAny
                          ? " -- " + ContextFailedNote()
                          : " -- frame generation runs")
                : !wantEnabled
                    ? (!ProfileChosen() ? "Off -- choose a graphics profile in ReDefinition's window"
                       : frameGeneration && FrameGenerationBridge.ContextFailureStands
                           ? "Off -- " + ContextFailedNote()
                       : "Off")
                : OnlySettingsHere() ? "On -- takes effect in flight and in the editors"
                : "Setting up ...", styles.Strip);

            if (!string.IsNullOrEmpty(problem)) WarningLabel(problem);

            GUILayout.Space(6f);
            DrawOptions(OptionsOf(DebugCategory.Overview));
            GUILayout.Space(6f);

            DrawTechniqueRows();

            GUILayout.Space(8f);
            DrawHostStackSummary();

            GUILayout.Space(8f);
            GUILayout.Label(FrameRateComparison(), styles.Dim);
        }

        // Every mode, as in KSP's settings dialog: the same names, and the same
        // direction -- AA only at the right end, smaller render sizes to its left.
        // A mode set in either view shows in the other.
        private void DrawTechniqueRows()
        {
            // ReDefinition's settings belong to a graphics profile: without one they
            // wait for it, as in ReDefinition's window and KSP's dialog.
            bool outer = GUI.enabled;
            bool profile = ProfileChosen();
            GUI.enabled = outer && profile;
            // Read before any control: the technique's switch forgets failures
            // within this event, and the rows must stay the ones the layout pass
            // counted. (The rig itself is rebuilt by Update, see Reattach.)
            bool fallingBack = rig != null && rig.Backend != backend && fallbackReason != null;
            bool canRetry = fallingBack && nativeFailures.ContainsKey(backend);
            string fallbackText = fallingBack
                ? UpscalerBackends.Name(backend) + " cannot run, FSR 3 runs instead: " + fallbackReason
                : null;
            string retryText = canRetry ? "Try " + UpscalerBackends.Name(backend) + " again" : null;

            // The techniques that can run here, and the one chosen whatever it can.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Technique", styles.Row);
            GUILayout.FlexibleSpace();
            int techniqueAt = offeredBackends.IndexOf(backend);
            int pickedTechnique = Segments(offeredTechniqueNames, techniqueAt);
            if (pickedTechnique != techniqueAt)
            {
                // Picked by hand: no longer Auto's.
                settings.BackendAuto = false;
                resolvedFor = null;
                SetBackend(offeredBackends[pickedTechnique]);
            }
            RowTail();
            GUILayout.EndHorizontal();

            // Smaller render sizes to the left, AA only at the right end.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode", styles.Row);
            GUILayout.FlexibleSpace();
            int modeAt = ModeCount - 1 - (int)quality;
            int pickedMode = Segments(modeNames ?? (modeNames = MakeModeNames()), modeAt);
            if (pickedMode != modeAt)
            {
                settings.QualityAuto = false;
                resolvedFor = null;
                SetQuality((Fsr3Upscaler.QualityMode)(ModeCount - 1 - pickedMode));
            }
            RowTail();
            GUILayout.EndHorizontal();

            GUILayout.Label(rig != null && rig.PassThrough
                ? (wantEnabled ? "The upscaler could not be set up" : "Upscaler off")
                  + ": frame generation runs, without FSR's antialiasing"
                  + (QualitySettings.antiAliasing > 1 ? ", and KSP's MSAA does not reach the 3D scene." : ".")
                : quality == Fsr3Upscaler.QualityMode.NativeAA
                ? "Full resolution. " + UpscalerBackends.Name(rig != null ? rig.Backend : backend) + " does the antialiasing."
                : "Renders smaller and reconstructs.", styles.Dim);
            if (fallingBack)
            {
                GUILayout.Label(fallbackText);
                if (canRetry && GUILayout.Button(retryText))
                {
                    ForgetNativeFailures(true);
                    Reattach();
                }
            }

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Sharpness", styles.Row);
            GUILayout.FlexibleSpace();
            bool down = GUILayout.Button("-", styles.Segment, GUILayout.Width(30f));
            GUILayout.Label(sharpness.ToString("0.0"), styles.Value, GUILayout.Width(50f));
            bool up = GUILayout.Button("+", styles.Segment, GUILayout.Width(30f));
            RowTail();
            GUILayout.EndHorizontal();
            if (down || up) SetSharpness(sharpness + (up ? 0.1f : -0.1f));
            GUI.enabled = outer;

            if (!profile) GUILayout.Label(NeedsProfileText, styles.Reason);
        }

        // The preview's texture, taken at the layout pass; the number of upscaler modes; the techniques the overview offers
        // and their names; and every technique there is.
        private Texture previewTexture;
        private const int ModeCount = 6;
        private readonly List<UpscalerBackend> offeredBackends = new List<UpscalerBackend>();
        private string[] offeredTechniqueNames = new string[0];
        private static readonly UpscalerBackend[] AllBackends = (UpscalerBackend[])System.Enum.GetValues(typeof(UpscalerBackend));

        // The modes, smaller render sizes to the left and AA at the right end; made when first drawn.
        private static string[] modeNames;

        // The mode names from Ultra Performance on the left to native AA on the right.
        private static string[] MakeModeNames()
        {
            string[] names = new string[ModeCount];
            for (int i = 0; i < ModeCount; i++)
                names[i] = KspSettingsSection.ModeName((Fsr3Upscaler.QualityMode)(ModeCount - 1 - i));
            return names;
        }

        // At the start of a layout pass: the techniques that can run here, and their names
        // (made again only when the techniques are others).
        private void RefreshOfferedBackends()
        {
            offeredBackends.Clear();
            foreach (UpscalerBackend candidate in AllBackends)
                if (candidate == backend || UpscalerBackends.WhyNotOffered(candidate) == null)
                    offeredBackends.Add(candidate);
            bool same = offeredTechniqueNames.Length == offeredBackends.Count;
            for (int i = 0; same && i < offeredBackends.Count; i++)
                same = offeredTechniqueNames[i] == UpscalerBackends.Name(offeredBackends[i]);
            if (same) return;
            offeredTechniqueNames = new string[offeredBackends.Count];
            for (int i = 0; i < offeredTechniqueNames.Length; i++)
                offeredTechniqueNames[i] = UpscalerBackends.Name(offeredBackends[i]);
        }

        // Other visual mods run temporal filters of their own, which conflict with
        // the upscaler (HostStack). The overview shows the verdict; Other mods the lines
        // behind it. With the upscaler off their antialiasing is theirs
        // (SyncHostStack), and there is nothing to judge.
        private void DrawHostStackSummary()
        {
            if (!ProfileChosen() || PassThroughWanted)
            {
                GUILayout.Label("Other visual mods: their own antialiasing, while the upscaler is off.");
                return;
            }
            if (Event.current.type == EventType.Layout) HostStack.Refresh(false);

            int problems = 0;
            List<HostStack.Line> lines = HostStack.Lines;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Present && !lines[i].Ok) problems++;

            if (problems > 0) WarningLabel(problems + " setting(s) in other mods fight FSR.");
            else GUILayout.Label("Other visual mods: nothing conflicting with FSR.");

            if (!string.IsNullOrEmpty(hostStackMessage)) GUILayout.Label(hostStackMessage);
        }

        // The page of the upscaler's inputs: its options.
        private void DrawUpscalerInputs()
        {
            
            DrawOptions(OptionsOf(DebugCategory.UpscalerInputs));
        }

        // The frame generation page: its options, then what runs now and a failed context's note.
        private void DrawFrameGeneration()
        {
            
            DrawOptions(OptionsOf(DebugCategory.FrameGeneration));

            Heading("Running now");
            InfoRow("Frame generation", fgTechniqueText);
            InfoRow("Low latency", lowLatencyDescription);
            InfoRow("Frames", rateRatioText);
            if (contextFailing) WarningLabel(ContextFailedNote());
        }

        // The page of the game settings the upscaler changes: its options.
        private void DrawGameSettings()
        {
            
            DrawOptions(OptionsOf(DebugCategory.GameSettings));
        }

        // The capture page: its options, and the state of the last screen recording.
        private void DrawCapture()
        {
            
            DrawOptions(OptionsOf(DebugCategory.Capture));
            GUILayout.Space(4f);
            InfoRow("Last recording", recordingState);
        }

        // A preview of what the upscaler receives and what it returns.
        private void DrawPreview()
        {
            

            GUILayout.BeginHorizontal();
            if (Mode("Motion", UpscalerRig.PreviewMode.MotionVectors)) previewMode = UpscalerRig.PreviewMode.MotionVectors;
            if (Mode("Depth", UpscalerRig.PreviewMode.Depth)) previewMode = UpscalerRig.PreviewMode.Depth;
            if (Mode("Low res", UpscalerRig.PreviewMode.LowRes)) previewMode = UpscalerRig.PreviewMode.LowRes;
            if (Mode("Result", UpscalerRig.PreviewMode.Upscaled)) previewMode = UpscalerRig.PreviewMode.Upscaled;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (Mode("Transparency mask", UpscalerRig.PreviewMode.TransparencyMask)) previewMode = UpscalerRig.PreviewMode.TransparencyMask;
            if (Mode("Reactive mask", UpscalerRig.PreviewMode.ReactiveMask)) previewMode = UpscalerRig.PreviewMode.ReactiveMask;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Read only while this page is shown, the previews cost frame rate -- and in the layout
            // pass: the read-back changes the render target, which would spoil what is drawn after
            // it in a repaint.
            if (Event.current.type == EventType.Layout)
                previewTexture = rig != null && rig.Ready ? rig.GetPreview(previewMode) : null;

            Rect area = GUILayoutUtility.GetRect(UpscalerRig.PreviewWidth, UpscalerRig.PreviewHeight);
            if (previewTexture != null) GUI.DrawTexture(area, previewTexture, ScaleMode.ScaleToFit, false);
            else if (Event.current.type == EventType.Repaint)
                GUI.Label(area, rig != null && rig.Ready ? "" : "  (upscaler is off)");

            switch (previewMode)
            {
                case UpscalerRig.PreviewMode.MotionVectors:
                    GUILayout.Label("Red is rightwards, green is upwards, grey is still."
                                    + (rig != null && rig.Ready
                                       ? "  Scale " + rig.PreviewGain.ToString("0.00") + " px" : ""));
                    break;
                case UpscalerRig.PreviewMode.Depth:
                    GUILayout.Label("Bright is near, dark is far.");
                    break;
                case UpscalerRig.PreviewMode.LowRes:
                    GUILayout.Label("The image FSR receives -- at render resolution.");
                    break;
                case UpscalerRig.PreviewMode.TransparencyMask:
                    GUILayout.Label("White where FSR loosens its history: EVE's clouds and Scatterer's ocean.");
                    break;
                case UpscalerRig.PreviewMode.ReactiveMask:
                    GUILayout.Label("White where the current frame counts more: particles, plumes, re-entry.");
                    break;
                default:
                    GUILayout.Label("The finished image that goes to the screen.");
                    break;
            }
        }

        // The active choice stays pressed, so it is visible what is shown.
        private bool Mode(string label, UpscalerRig.PreviewMode mode)
        {
            bool active = previewMode == mode;
            return GUILayout.Toggle(active, label, styles.Segment) && !active;
        }
    }
}
