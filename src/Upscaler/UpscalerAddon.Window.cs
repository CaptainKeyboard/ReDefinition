using System;
using System.Collections.Generic;
using FidelityFX.FSR3;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition
{
    // The add-on's part for its diagnostics window (IMGUI): General and Debug.
    public partial class UpscalerAddon
    {
        private bool windowVisible;

        private const string ContextFailedText =
            "frame generation's context could not be made (see ReDefinitionProxy.log)";

        // ContextFailedText, with the way out once the proxy no longer tries.
        private static string ContextFailedNote()
        {
            return ContextFailedText + (FrameGenerationBridge.ContextFailureStands
                ? "; switch frame generation off and on to try again"
                : "");
        }

        // Closed by default: the motion and depth previews read the image back
        // from the GPU and cost frame rate while they are shown.
        private bool previewVisible;
        private bool registrationsVisible;
        private Vector2 registrationsScroll;
        private const float RegistrationsHeight = 260f;
        private UpscalerRig.PreviewMode previewMode = UpscalerRig.PreviewMode.MotionVectors;

        private enum Tab { General, Debug }

        private Tab tab = Tab.General;
        // Closed by default: General shows the verdict, this the lines behind it.
        private bool hostStackVisible;
        private string hostStackMessage;

        private Rect window = new Rect(60f, 60f, 360f, 0f);
        private int windowId;
        private GUIStyle valueStyle;

        private void ToggleWindow()
        {
            windowVisible = !windowVisible;
        }

        // From the settings window's Diagnostics button.
        internal void ShowDiagnostics()
        {
            windowVisible = true;
            tab = Tab.Debug;
        }

        // Drawn with KSP's own IMGUI skin, as RemoteTech, Parallax and TUFX draw
        // their windows. HUDReplacer patches that skin's getter and replaces its
        // textures by name -- window, button, toggle, label, slider, scrollbars
        // (Patches/HighLogic_Skin.cs there) -- so a UI theme such as ZTheme reaches
        // this window for every texture it ships under those names. Unity resets
        // GUI.skin to its default before every OnGUI (GUIUtility.BeginGUI) and hands
        // the skin current at GUILayout.Window to the window function, so setting it
        // here is enough. The cached styles copy HighLogic.Skin, never Unity's
        // default.
        private void OnGUI()
        {
            if (!windowVisible)
            {
                UnityMouseEvents.ImguiWindow = Rect.zero;
                return;
            }

            GUI.skin = HighLogic.Skin;
            if (valueStyle == null)
            {
                valueStyle = new GUIStyle(HighLogic.Skin.label);
                valueStyle.alignment = TextAnchor.MiddleRight;
            }

            window = GUILayout.Window(windowId, window, DrawWindow, "ReDefinition");
            // Not click-through: what lies behind it is kept from the click
            // (UnityMouseEvents), as behind the settings window.
            UnityMouseEvents.ImguiWindow = window;
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            bool active = rig != null && rig.Ready;
            GUILayout.Label(active
                ? "Active: " + rig.RenderSize.x + "x" + rig.RenderSize.y + "  ->  "
                  + rig.DisplaySize.x + "x" + rig.DisplaySize.y
                  // The capture goes on for the proxy's attempts, which need its
                  // packets; the player sees that nothing is generated.
                  + (frameGeneration && FrameGenerationBridge.ContextFailing ? "  -- " + ContextFailedNote() : "")
                : rig != null && rig.PassThrough
                    // What stopped the upscaler follows below.
                    ? (wantEnabled ? "Upscaler not set up" : "Upscaler off")
                      + (FrameGenerationBridge.ContextFailing
                          ? " -- " + ContextFailedNote()
                          : " -- frame generation runs")
                : !wantEnabled
                    ? (!ProfileChosen() ? "Off -- choose a graphics profile in ReDefinition's window"
                       : frameGeneration && FrameGenerationBridge.ContextFailureStands
                           ? "Off -- " + ContextFailedNote()
                       : "Off")
                : OnlySettingsHere() ? "On -- takes effect in flight and in the editors"
                : "Setting up ...");

            GUILayout.Label(frameRateText);

            if (!string.IsNullOrEmpty(problem))
                GUILayout.Label(problem);

            GUILayout.Space(4f);

            // Switching on needs a graphics profile (SetEnabled).
            bool buttonsEnabled = GUI.enabled;
            GUI.enabled = buttonsEnabled && (wantEnabled || ProfileChosen());
            if (GUILayout.Button(wantEnabled ? "Turn upscaler off" : "Turn upscaler on"))
                SetEnabled(!wantEnabled);
            GUI.enabled = buttonsEnabled;

            GUILayout.Space(4f);

            // General for the settings, Debug for the instruments.
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(tab == Tab.General, "General", GUI.skin.button) && tab != Tab.General)
                SwitchTab(Tab.General);
            if (GUILayout.Toggle(tab == Tab.Debug, "Debug", GUI.skin.button) && tab != Tab.Debug)
                SwitchTab(Tab.Debug);
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);

            if (tab == Tab.General) DrawGeneral();
            else DrawDebug();

            GUILayout.Space(6f);
            if (GUILayout.Button("Close")) ToggleWindow();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        // Every mode, as in KSP's settings dialog: the same names, and the same
        // direction -- AA only at the right end, smaller render sizes to its left.
        // A mode set in either view shows in the other.
        private void DrawGeneral()
        {
            // ReDefinition's settings belong to a graphics profile: without one they
            // wait for it, as in ReDefinition's window and KSP's dialog.
            bool generalEnabled = GUI.enabled;
            GUI.enabled = generalEnabled && ProfileChosen();
            // Read before any control: the technique's switch forgets failures
            // within this event, and the rows must stay the ones the layout pass
            // counted. (The rig itself is rebuilt by Update, see Reattach.)
            bool fallingBack = rig != null && rig.Backend != backend && fallbackReason != null;
            bool canRetry = fallingBack && nativeFailures.ContainsKey(backend);
            string fallbackText = fallingBack
                ? UpscalerBackends.Name(backend) + " cannot run, FSR 3 runs instead: " + fallbackReason
                : null;
            string retryText = canRetry ? "Try " + UpscalerBackends.Name(backend) + " again" : null;

            if (SwitchRow("Technique", UpscalerBackends.Name(backend)))
                SetBackend(UpscalerBackends.Next(backend));

            Row("Mode", KspSettingsSection.ModeName(quality),
                out bool smaller, out bool larger, "<", ">");
            if (smaller) SetQuality(Step(quality, +1));
            if (larger) SetQuality(Step(quality, -1));

            GUILayout.Label(rig != null && rig.PassThrough
                ? (wantEnabled ? "The upscaler could not be set up" : "Upscaler off")
                  + ": frame generation runs, without FSR's antialiasing"
                  + (QualitySettings.antiAliasing > 1 ? ", and KSP's MSAA does not reach the 3D scene." : ".")
                : quality == Fsr3Upscaler.QualityMode.NativeAA
                ? "Full resolution. " + UpscalerBackends.Name(rig != null ? rig.Backend : backend) + " does the antialiasing."
                : "Renders smaller and reconstructs.");
            if (fallingBack)
            {
                GUILayout.Label(fallbackText);
                if (canRetry && GUILayout.Button(retryText))
                {
                    ForgetNativeFailures(true);
                    Reattach();
                }
            }

            // With any technique, so it is set before DLSS runs.
            if (SwitchRow("DLSS preset", dlssPreset == DlssPreset.Default ? "Default" : "Preset " + dlssPreset))
                SetDlssPreset(DlssBridge.NextPreset(dlssPreset));

            GUILayout.Space(6f);

            Row("Sharpness", sharpness.ToString("0.0"),
                out bool sharpDown, out bool sharpUp, "-", "+");
            if (sharpDown || sharpUp)
                SetSharpness(sharpness + (sharpUp ? 0.1f : -0.1f));
            GUI.enabled = generalEnabled;

            GUILayout.Space(6f);

            bool buttonsEnabled = GUI.enabled;
            GUI.enabled = buttonsEnabled && (frameGeneration || ProfileChosen());
            if (GUILayout.Button("Frame generation: " + (frameGeneration ? "on" : "off")))
                SetFrameGeneration(!frameGeneration);
            GUI.enabled = buttonsEnabled;

            GUILayout.Space(6f);

            DrawHostStackSummary();
        }

        // Other visual mods run temporal filters of their own, which conflict with
        // the upscaler (HostStack). General shows the verdict; Debug the lines
        // behind it.
        private void DrawHostStackSummary()
        {
            if (Event.current.type == EventType.Layout) HostStack.Refresh(false);

            int problems = 0;
            List<HostStack.Line> lines = HostStack.Lines;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Present && !lines[i].Ok) problems++;

            Color previous = GUI.color;
            if (problems > 0) GUI.color = new Color(1f, 0.75f, 0.3f);
            GUILayout.Label(problems == 0
                ? "Other visual mods: nothing conflicting with FSR."
                : problems + " setting(s) in other mods fight FSR.");
            GUI.color = previous;

            if (!string.IsNullOrEmpty(hostStackMessage)) GUILayout.Label(hostStackMessage);
        }

        // The instruments: measurements first, then the switches for what the
        // upscaler receives and what the game renders around it. A switch that does
        // not reach the technique running is greyed out, unless it is away from its
        // default and may have to be put back.
        private void DrawDebug()
        {
            UpscalerBackend running = rig != null ? rig.Backend : backend;
            bool enabled = GUI.enabled;

            Heading("Measure");

            GUILayout.Label(loadText);
            GUILayout.Label(FrameRateComparison());

            if (GUILayout.Button("Write diagnostics to log"))
            {
                if (rig != null) rig.LogDiagnostics();
                else Debug.Log(UpscalerProbe.Tag + " Diagnostics: upscaler is not active.");
            }

            // The rendered picture as it is, through the same redirect: what the
            // upscaler starts from.
            if (SwitchRow("Without upscaler (bypass)", OnOff(bypass)))
            {
                bypass = !bypass;
                Reattach();
            }

            // Needs frame generation, whose inputs the check reads.
            GUI.enabled = enabled && (frameGeneration || recordFastTurns);
            if (SwitchRow("Motion vector check on fast turns", OnOff(recordFastTurns)))
            {
                recordFastTurns = !recordFastTurns;
                if (rig != null) rig.RecordFastTurns = recordFastTurns;
                Debug.Log(UpscalerProbe.Tag + " Motion vector check on fast turns " + (recordFastTurns ? "on" : "off") + ".");
            }
            GUI.enabled = enabled;

            Heading("What the upscaler receives");

            if (SwitchRow("Jitter", OnOff(jitter)))
                SetJitter(!jitter);

            // Not saved: EVE's clouds with the jitter and in the motion vectors
            // (EveCloudMotion, CloudMotionVectors), for a look with and without.
            GUI.enabled = enabled && EveCloudMotion.Present;
            if (SwitchRow("EVE clouds: jitter and motion vectors", OnOff(EveCloudMotion.Enabled)))
            {
                EveCloudMotion.Enabled = !EveCloudMotion.Enabled;
                Debug.Log(UpscalerProbe.Tag + " EVE clouds with jitter and motion vectors "
                          + (EveCloudMotion.Enabled ? "on" : "off") + ".");
            }
            GUI.enabled = enabled;

            if (SwitchRow("Mipmap bias", OnOff(mipmapBias)))
                SetMipmapBias(!mipmapBias);

            if (SwitchRow("Skinned motion vectors", skinnedMotionVectors ? "forced" : "as set"))
                SetSkinnedMotionVectors(!skinnedMotionVectors);

            if (SwitchRow("TUFX after upscaling", OnOff(tufxAfterUpscaling)))
                SetTufxAfterUpscaling(!tufxAfterUpscaling);

            // DLSS always measures its exposure itself; neither mask reaches DLSS
            // or AMD's DLL (their packets have no field for one).
            GUI.enabled = enabled && (running != UpscalerBackend.Dlss || !autoExposure);
            if (SwitchRow("Auto exposure (FSR 3, AMD)", OnOff(autoExposure)))
                SetAutoExposure(!autoExposure);

            GUI.enabled = enabled && (running == UpscalerBackend.Fsr3 || transparencyMask);
            if (SwitchRow("Transparency mask (FSR 3)", OnOff(transparencyMask)))
                SetTransparencyMask(!transparencyMask);

            GUI.enabled = enabled && (running == UpscalerBackend.Fsr3 || reactiveMask != UpscalerMasks.ReactiveSource.Off);
            if (SwitchRow("Reactive mask (FSR 3)", ReactiveLabel(reactiveMask)))
                SetReactiveMask((UpscalerMasks.ReactiveSource)(((int)reactiveMask + 1) % 3));
            GUI.enabled = enabled;

            Heading("Game settings while upscaling");

            // All three take effect at once: a rebuild would reset the upscaler's
            // history.
            if (SwitchRow("LOD bias compensation", OnOff(compensateLodBias)))
                SetCompensateLodBias(!compensateLodBias);

            if (SwitchRow("MSAA forced off", OnOff(disableMsaa)))
                SetDisableMsaa(!disableMsaa);

            if (SwitchRow("Anisotropic filtering forced", OnOff(forceAnisotropic)))
                SetForceAnisotropic(!forceAnisotropic);

#if DEVELOPMENT_BUILD
            // FSR's debug view pass is compiled into development builds only.
            Heading("FSR 3 internals");

            GUI.enabled = enabled && (running == UpscalerBackend.Fsr3 || debugView);
            if (SwitchRow("FSR's debug view", OnOff(debugView)))
            {
                debugView = !debugView;
                if (rig != null) rig.DebugView = debugView;
                Debug.Log(UpscalerProbe.Tag + " FSR debug view " + (debugView ? "on" : "off"));
            }
            GUI.enabled = enabled;
#endif

            GUILayout.Space(6f);

            DrawHostStack();

            GUILayout.Space(6f);

            DrawRegistrations();

            GUILayout.Space(6f);

            DrawPreview();
        }

        // What the mod registrations built (docs/modders/registering-a-mod.md): the
        // mods bundled, each with how many settings; a mod that is loaded and not
        // bundled, with why; and every problem the registrations had.
        private void DrawRegistrations()
        {
            if (GUILayout.Button(registrationsVisible ? "Hide mod registrations" : "Show mod registrations"))
                registrationsVisible = !registrationsVisible;

            if (!registrationsVisible) return;

            if (!BundledSettings.Built)
            {
                GUILayout.Label("Not read yet: the registrations are read from the main menu on.");
                return;
            }

            List<string> bundled = new List<string>();
            List<string> notBundled = new List<string>();
            foreach (IBundledMod mod in BundledSettings.Mods)
            {
                if (mod.IsInstalled)
                {
                    bundled.Add(mod.ModName + " (" + mod.Settings.Count + ")");
                    continue;
                }
                RegisteredMod registered = mod as RegisteredMod;
                if (registered != null && (registered.Detected || registered.DroppedMembers.Count > 0))
                    notBundled.Add(mod.ModName + ": " + NotBundledReason(mod));
            }
            GUILayout.Label("Bundled: " + (bundled.Count > 0 ? string.Join(", ", bundled.ToArray()) : "none"));

            IList<string> problems = BundledSettings.RegistrationProblems;
            Color previous = GUI.color;
            GUI.color = new Color(1f, 0.75f, 0.3f);
            // Past a fixed height the lines scroll, so the controls below stay on
            // the screen.
            bool scroll = ListHeight(notBundled, problems) > RegistrationsHeight;
            if (scroll)
                registrationsScroll = GUILayout.BeginScrollView(registrationsScroll, GUILayout.Height(RegistrationsHeight));
            foreach (string line in notBundled) GUILayout.Label("Not bundled: " + line);
            foreach (string problem in problems) GUILayout.Label(problem);
            if (scroll) GUILayout.EndScrollView();
            GUI.color = previous;
            if (problems.Count == 0) GUILayout.Label("No problems in the registrations.");
        }

        // How high the lines stand, wrapped by the label style at the window's width.
        private float ListHeight(List<string> notBundled, IList<string> problems)
        {
            GUIStyle style = GUI.skin.label;
            float width = Mathf.Max(100f, window.width - 30f);
            float height = 0f;
            foreach (string line in notBundled)
                height += style.CalcHeight(new GUIContent("Not bundled: " + line), width) + style.margin.vertical;
            foreach (string problem in problems)
                height += style.CalcHeight(new GUIContent(problem), width) + style.margin.vertical;
            return height;
        }

        // Why a loaded mod is not bundled: what took the whole mod with it, where
        // that was said, otherwise the first thing it left out.
        private static string NotBundledReason(IBundledMod mod)
        {
            string wholeMod = RegisteredMod.WholeMod + RegisteredMod.DropSeparator;
            foreach (string line in mod.DroppedMembers)
                if (line.StartsWith(wholeMod, StringComparison.Ordinal)) return line.Substring(wholeMod.Length);
            return mod.DroppedMembers.Count > 0 ? mod.DroppedMembers[0] : "this build offers none of its settings";
        }

        // The other mods' state that decides the image (HostStack), line by line.
        private void DrawHostStack()
        {
            if (GUILayout.Button(hostStackVisible ? "Hide host stack" : "Show host stack"))
                hostStackVisible = !hostStackVisible;

            if (!hostStackVisible) return;

            // Refreshed only on the Layout event: OnGUI runs several times per
            // frame, and the number of controls must not change between Layout and
            // Repaint.
            if (Event.current.type == EventType.Layout) HostStack.Refresh(false);

            List<HostStack.Line> lines = HostStack.Lines;
            Color previous = GUI.color;
            for (int i = 0; i < lines.Count; i++)
            {
                HostStack.Line line = lines[i];
                if (!line.Present) GUI.color = Color.gray;
                else if (!line.Ok) GUI.color = new Color(1f, 0.75f, 0.3f);
                else GUI.color = previous;
                GUILayout.Label(line.Label + ": " + line.Value);
            }
            GUI.color = previous;

            if (!string.IsNullOrEmpty(hostStackMessage)) GUILayout.Label(hostStackMessage);
        }

        // A preview of what the upscaler receives and what it returns.
        private void DrawPreview()
        {
            if (GUILayout.Button(previewVisible ? "Hide preview" : "Show preview"))
                previewVisible = !previewVisible;

            if (!previewVisible) return;

            GUILayout.BeginHorizontal();
            if (Mode("Motion", UpscalerRig.PreviewMode.MotionVectors)) previewMode = UpscalerRig.PreviewMode.MotionVectors;
            if (Mode("Depth", UpscalerRig.PreviewMode.Depth)) previewMode = UpscalerRig.PreviewMode.Depth;
            if (Mode("Low res", UpscalerRig.PreviewMode.LowRes)) previewMode = UpscalerRig.PreviewMode.LowRes;
            if (Mode("Result", UpscalerRig.PreviewMode.Upscaled)) previewMode = UpscalerRig.PreviewMode.Upscaled;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (Mode("Transparency mask", UpscalerRig.PreviewMode.TransparencyMask)) previewMode = UpscalerRig.PreviewMode.TransparencyMask;
            if (Mode("Reactive mask", UpscalerRig.PreviewMode.ReactiveMask)) previewMode = UpscalerRig.PreviewMode.ReactiveMask;
            GUILayout.EndHorizontal();

            Texture texture = rig != null && rig.Ready ? rig.GetPreview(previewMode) : null;

            Rect area = GUILayoutUtility.GetRect(UpscalerRig.PreviewWidth, UpscalerRig.PreviewHeight);
            if (texture != null) GUI.DrawTexture(area, texture, ScaleMode.ScaleToFit, false);
            else GUI.Label(area, "  (upscaler is off)");

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
            return GUILayout.Toggle(active, label, GUI.skin.button) && !active;
        }

        // GUILayout.Window measures only a rectangle whose height is zero. The one
        // it returns is stored, so the window grows with its content but does not
        // shrink again; zeroing the height makes it measure afresh on the next
        // frame.
        private void SwitchTab(Tab wanted)
        {
            tab = wanted;
            window.height = 0f;
        }

        private static void Heading(string text)
        {
            GUILayout.Space(6f);
            GUILayout.Label(text, EditorHeading);
            GUILayout.Space(2f);
        }

        private static GUIStyle headingStyle;

        private static GUIStyle EditorHeading
        {
            get
            {
                if (headingStyle == null)
                {
                    headingStyle = new GUIStyle(HighLogic.Skin.label);
                    headingStyle.fontStyle = FontStyle.Bold;
                }
                return headingStyle;
            }
        }

        private const float SwitchWidth = 120f;
        // Measures a switch's value; one for all, as OnGUI runs several times a frame.
        private static readonly GUIContent switchValue = new GUIContent();

        // A switch as one row: what it is on the left, its state on the button.
        private static bool SwitchRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label);
            // As wide as its value needs: a minimum width alone lets the layout
            // squeeze the button back to it.
            switchValue.text = value;
            float width = Mathf.Max(SwitchWidth, GUI.skin.button.CalcSize(switchValue).x);
            bool clicked = GUILayout.Button(value, GUILayout.Width(width));
            GUILayout.EndHorizontal();
            return clicked;
        }

        private static string OnOff(bool value)
        {
            return value ? "on" : "off";
        }

        private void Row(string label, string value, out bool left, out bool right,
                         string leftText, string rightText)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70f));
            left = GUILayout.Button(leftText, GUILayout.Width(80f));
            GUILayout.Label(value, valueStyle, GUILayout.MinWidth(90f));
            right = GUILayout.Button(rightText, GUILayout.Width(80f));
            GUILayout.EndHorizontal();
        }
    }
}
