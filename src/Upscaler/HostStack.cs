using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System;
using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Reads and configures the temporal and spatial filters of other mods.
    //
    // An upscaler is itself a temporal filter. Another one in front of it
    // averages an already time-averaged image a second time, while both jitter
    // the same projection matrix; with TUFX's Default-Flight profile and
    // Scatterer both running TAA the image smears.
    //
    // Switching Scatterer's TAA off is not enough on its own: three of its
    // effects outlive the component, and two of them the scene:
    //
    //   The replacement motion vector shader. Scatterer installs it globally for
    //       BuiltinShaderType.MotionVectors and never reverts it, so it survives
    //       a scene change and a config change alike -- only a restart clears it.
    //       It then keeps reading globals nobody feeds any more.
    //   TAA_UseFloatingOriginCameraMotion and TAA_PreviousFrameTransform. Set via
    //       Shader.SetGlobal, so likewise process-wide, and frozen at their last
    //       value once the component stops updating them.
    //   motionVectorGenerationMode per vessel renderer. Scatterer sets them all
    //       to ForceNoMotion on load and corrects that every frame; disabled, they
    //       freeze there and stop writing object motion while still moving in
    //       the image, and smear.
    //
    // All three are cleaned up here, so taking Scatterer's TAA in the middle of a
    // session needs no restart (docs/development/upscaler.md).
    //
    // Everything goes through reflection: ReDefinition loads without TUFX and
    // Scatterer.
    internal static class HostStack
    {
        public struct Line
        {
            public string Label;
            public string Value;
            public bool Ok;        // is this state suitable for FSR?
            public bool Present;   // false: mod not installed at all
        }

        private const string ScattererTaaTypeName = "Scatterer.TemporalAntiAliasing";
        private const string ScattererSmaaTypeName = "Scatterer.SubpixelMorphologicalAntialiasing";

        // Deferred brings an SMAA of its own, added only in the editors, to the
        // editor's Main Camera, when useSmaaInEditors is set -- which the
        // shipped Deferred.cfg does (EditorLighting.HandleSMAA). In the VAB and
        // the SPH the rig sits on that camera. Deferred adds a fresh copy on every
        // editor scene load, so Reassert's once-a-second pass keeps it off.
        private const string DeferredSmaaTypeName = "Deferred.SubpixelMorphologicalAntialiasing";
        private const string TufxLoaderTypeName = "TUFX.TexturesUnlimitedFXLoader";

        // Kerbal Frame Generator (MangoTechKSP, MIT) blends each frame with the
        // one before in OnRenderImage (InterpolationEffect.cs) -- in flight on
        // the camera KSP's GalaxyCameraControl belongs to, elsewhere on
        // Camera.main (KFG_UI_Controller.LateUpdate), the camera tagged
        // MainCamera, which in the editors the scene decides. On top of the
        // upscaler the blend is ghosting the upscaler cannot take out: it has no
        // motion vectors. The switch is global, a
        // public static field it reads every frame and never saves
        // (KSR2_Settings.cs), so what is set here lasts for this run only,
        // whichever camera the effect sits on.
        private const string KfgSettingsTypeName = "KFG_Settings";
        private const string KfgSwitchField = "effectEnabled";
        private static bool? kfgFound;   // as first found, or on once found on; null: untouched
        private const string ScattererMotionVectorShader = "Scatterer/Internal-MotionVectors";

        // Globals that Scatterer's TemporalAntiAliasing feeds to its replacement
        // motion vector shader every frame.
        private static readonly int UseFloatingOriginId =
            Shader.PropertyToID("TAA_UseFloatingOriginCameraMotion");
        private static readonly int PreviousFrameTransformId =
            Shader.PropertyToID("TAA_PreviousFrameTransform");

        // Also the compatibility classes' lookups (EveCompatibility, ScattererCompatibility).
        internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.Public | BindingFlags.NonPublic;

        // The state found is remembered, so a restore puts back that state rather
        // than defaults.
        private sealed class TaaBackup
        {
            public Component Component;
            public bool Enabled;
        }

        private sealed class TufxBackup
        {
            public object Primary;
            public object Secondary;

            // The value written here. Restore compares against it: a profile that
            // holds another value was changed since, and that choice stands.
            public object Applied;
        }

        private static readonly List<TaaBackup> taaBackups = new List<TaaBackup>();

        // Keyed by profile object rather than by name: TUFX keeps its profiles
        // alive in a dictionary, so the reference stays valid. And a different
        // profile can be active per scene.
        private static readonly Dictionary<object, TufxBackup> tufxBackups =
            new Dictionary<object, TufxBackup>();

        // What each PostProcessLayer's antialiasing was before ApplyTufx first set
        // None on it, for the layers TUFX does not hand theirs out to again.
        private static readonly Dictionary<Component, object> liveAntialiasing = new Dictionary<Component, object>();
        private static readonly List<Component> destroyedLayers = new List<Component>();

        // Components switched off here, so they can be switched back on.
        private static readonly List<Behaviour> disabledComponents = new List<Behaviour>();

        private static BuiltinShaderMode? savedShaderMode;
        private static Shader savedCustomShader;

        private static float nextReassert;

        public static bool Applied { get; private set; }

        public static string LastMessage { get; private set; }

        // ------------------------------------------------------------------
        // Reading the state
        // ------------------------------------------------------------------

        // The report is cached: FindObjectsOfType walks the whole scene, and OnGUI
        // runs several times per frame.
        // A snapshot also keeps the number of controls the same between the Layout
        // and Repaint events, as IMGUI requires.
        private const float RefreshInterval = 0.5f;
        private const float ReassertInterval = 1.0f;

        private static readonly List<Line> cachedLines = new List<Line>();
        private static float nextRefresh;
        private static bool everBuilt;

        public static List<Line> Lines { get { return cachedLines; } }

        public static void Refresh(bool force)
        {
            if (!force && everBuilt && Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshInterval;
            everBuilt = true;

            cachedLines.Clear();
            Build(cachedLines);
        }

        // FlightGlobals' getter throws while the flight scene is torn down -- when
        // Restore runs from the add-on's OnDestroy (KSP.log).
        private static Vessel ActiveVessel()
        {
            return FlightGlobals.fetch != null ? FlightGlobals.ActiveVessel : null;
        }

        private static void Build(List<Line> lines)
        {
            Type profileType;
            object profile = TufxProfile(out profileType);
            if (profile == null)
            {
                lines.Add(Missing("TUFX"));
            }
            else
            {
                object name;
                TryGet(profile, profileType, "ProfileName", out name);
                lines.Add(Info("TUFX profile", name == null ? "?" : name.ToString()));
                lines.Add(AaLine(profile, profileType, "Antialiasing", "TUFX AA primary"));
                lines.Add(AaLine(profile, profileType, "SecondaryCameraAntialiasing", "TUFX AA secondary"));
            }

            Type taaType = TypeLookup.Find(ScattererTaaTypeName);
            List<Component> taa = ScattererTaaComponents();
            // Switched off counts as off: FindObjectsOfType finds disabled ones too,
            // among them the ones Apply switched off.
            int switchedOff = taa.RemoveAll(component => !IsEnabled(component));
            if (taaType == null)
            {
                lines.Add(Missing("Scatterer"));
            }
            else if (taa.Count == 0)
            {
                // None running is the state the upscaler needs -- whether Scatterer's file has
                // useTemporalAntiAliasing = False, as every profile sets it, or its
                // components are switched off.
                lines.Add(Info("Scatterer TAA", switchedOff > 0 ? "off (" + switchedOff + " switched off)" : "off"));
            }
            else
            {
                int jitterOnly = 0;
                int zeroSpread = 0;
                StringBuilder detail = new StringBuilder();
                for (int i = 0; i < taa.Count; i++)
                {
                    object role;
                    object spread;
                    TryGet(taa[i], taaType, "role", out role);
                    TryGet(taa[i], taaType, "jitterSpread", out spread);
                    float spreadValue = spread is float ? (float)spread : -1f;

                    if (role != null && role.ToString() == "JitterOnly") jitterOnly++;
                    if (spreadValue == 0f) zeroSpread++;

                    if (detail.Length > 0) detail.Append(", ");
                    detail.Append(taa[i].gameObject.name).Append(": ")
                          .Append(role == null ? "?" : role.ToString())
                          .Append(" / ").Append(spreadValue.ToString("0.##"));
                }

                lines.Add(new Line
                {
                    Label = "Scatterer TAA",
                    Value = taa.Count + " components -- " + detail,
                    Ok = jitterOnly == taa.Count && zeroSpread == taa.Count,
                    Present = true,
                });
            }

            // SMAA sits at AfterForwardAlpha, before the rig's capture -- an image
            // smoothed there has lost the aliasing the upscaler reconstructs from.
            AddSmaaLine(lines, ScattererSmaaTypeName, ScattererSmaaBufferPrefix, "Scatterer SMAA");
            if (HighLogic.LoadedSceneIsEditor)
                AddSmaaLine(lines, DeferredSmaaTypeName, DeferredSmaaBufferPrefix, "Deferred SMAA (editors)");

            Type kfgType = TypeLookup.Find(KfgSettingsTypeName);
            if (kfgType != null)
            {
                object blend;
                bool readable = TryGetStatic(kfgType, KfgSwitchField, out blend) && blend is bool;
                lines.Add(new Line
                {
                    Label = "Kerbal Frame Generator",
                    Value = !readable ? "not readable"
                          : (bool)blend ? "switch on -- blends frames wherever its effect runs" : "switch off",
                    Ok = readable && !(bool)blend,
                    Present = true,
                });
            }

            // Renderers frozen on ForceNoMotion write no object motion while
            // still moving in the image, and smear.
            Vessel vessel = ActiveVessel();
            if (vessel != null)
            {
                int frozen = 0;
                Renderer[] renderers = vessel.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] != null
                        && renderers[i].motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion)
                        frozen++;

                lines.Add(new Line
                {
                    Label = "Vessel renderers",
                    Value = frozen == 0
                        ? renderers.Length + " renderers, none on ForceNoMotion"
                        : frozen + " of " + renderers.Length + " on ForceNoMotion -- these will smear",
                    Ok = frozen == 0,
                    Present = true,
                });
            }

            lines.Add(MotionVectorLine(taa.Count > 0));
        }

        private static Line AaLine(object profile, Type profileType, string field, string label)
        {
            object mode;
            Type modeType;
            if (!TryGetAaMode(profile, profileType, field, out mode, out modeType))
                return Info(label, "not readable");

            string text = mode == null ? "?" : mode.ToString();
            return new Line { Label = label, Value = text, Ok = text == "None", Present = true };
        }

        // Judged by the state, not by whether Apply has run. Unity's own shader is
        // right wherever Scatterer's TAA does not run: Scatterer's replacement
        // depends on globals only that component feeds, and a frozen correction
        // adds error. Scatterer's replacement is right only while its TAA
        // runs and feeds it.
        private static Line MotionVectorLine(bool scattererTaaRuns)
        {
            BuiltinShaderMode mode = GraphicsSettings.GetShaderMode(BuiltinShaderType.MotionVectors);
            if (mode != BuiltinShaderMode.UseCustom)
            {
                return new Line
                {
                    Label = "Motion vector shader",
                    Value = "Unity stock" + (scattererTaaRuns
                        ? " -- Scatterer's TAA runs without its floating origin correction"
                        : ""),
                    Ok = !scattererTaaRuns,
                    Present = true,
                };
            }

            Shader shader = GraphicsSettings.GetCustomShader(BuiltinShaderType.MotionVectors);
            string name = shader == null ? "custom, but not assigned" : shader.name;
            bool scatterers = shader != null && name == ScattererMotionVectorShader;
            return new Line
            {
                Label = "Motion vector shader",
                Value = name + (scatterers && !scattererTaaRuns ? " -- its TAA is off, the correction is frozen" : ""),
                Ok = scatterers && scattererTaaRuns,
                Present = true,
            };
        }

        // ------------------------------------------------------------------
        // Setting the state
        // ------------------------------------------------------------------

        public static string Apply()
        {
            StringBuilder report = new StringBuilder();

            Type taaType = TypeLookup.Find(ScattererTaaTypeName);
            List<Component> taa = ScattererTaaComponents();

            // Unconditionally, not only when components are found.
            //
            // Once Scatterer's TAA has run in this process, two things outlive it and
            // no scene change clears them: the motion vector shader override in
            // GraphicsSettings, which Scatterer installs and never reverts, and the
            // TAA_ globals from Shader.SetGlobal. With useTemporalAntiAliasing set to
            // False and a reload, Init only skips the install block -- the shader
            // stays, still reading globals nobody feeds any more.
            //
            // Cleaned up here, Scatterer's TAA switched off mid-session leaves the
            // same state as a game started with it off.
            NeutraliseFloatingOrigin(true);
            RestoreBuiltinMotionVectorShader(true);
            report.Append(ReleaseMotionVectorModes(false))
                  .Append(" renderers freed from ForceNoMotion, ")
                  .Append("motion vector shader back to built-in. ");

            if (taaType == null)
            {
                report.Append("Scatterer not installed. ");
            }
            else if (taa.Count == 0)
            {
                report.Append("Scatterer has no TAA components. ");
            }
            else
            {
                int changed = 0;
                for (int i = 0; i < taa.Count; i++)
                {
                    taaBackups.Add(new TaaBackup { Component = taa[i], Enabled = IsEnabled(taa[i]) });
                    Disable(taa[i]);
                    changed++;
                }
                report.Append(changed).Append(" Scatterer TAA switched off. ");
            }

            report.Append(DisableSmaa()).Append(' ');
            report.Append(DisableKfg()).Append(' ');
            ApplyTufx(report);

            Applied = true;
            LastMessage = report.ToString();
            Debug.Log(Log.Tag + " Host stack configured: " + LastMessage);
            Refresh(true);
            return LastMessage;
        }

        public static string Restore()
        {
            StringBuilder report = new StringBuilder();

            Type taaType = TypeLookup.Find(ScattererTaaTypeName);
            int restored = 0;
            for (int i = 0; i < taaBackups.Count; i++)
            {
                TaaBackup backup = taaBackups[i];
                if (backup.Component == null || taaType == null) continue;
                Behaviour behaviour = backup.Component as Behaviour;
                if (behaviour != null && behaviour.enabled) continue;
                if (behaviour != null) behaviour.enabled = backup.Enabled;
                restored++;
            }
            taaBackups.Clear();
            NeutraliseFloatingOrigin(false);
            RestoreBuiltinMotionVectorShader(false);
            report.Append(restored).Append(" Scatterer TAA restored. ");

            // Every profile changed here, not just the active one: across a scene
            // change there may have been several.
            int profiles = 0;
            int changedElsewhere = 0;
            foreach (KeyValuePair<object, TufxBackup> entry in tufxBackups)
            {
                object profile = entry.Key;
                if (profile == null) continue;
                Type profileType = profile.GetType();

                object current;
                Type currentType;
                if (!TryGetAaMode(profile, profileType, "Antialiasing", out current, out currentType))
                    continue;
                if (!Equals(current, entry.Value.Applied))
                {
                    changedElsewhere++;
                    continue;
                }

                TrySetAaMode(profile, profileType, "Antialiasing", entry.Value.Primary);
                TrySetAaMode(profile, profileType, "SecondaryCameraAntialiasing", entry.Value.Secondary);
                profiles++;
            }
            // Every layer set to None its own antialiasing again, where it still has
            // None; then TUFX's cameras theirs from the profile just restored -- the
            // primary camera the profile's, the others its
            // SecondaryCameraAntialiasing -- as TUFX itself hands them out
            // (TexturesUnlimitedFXLoader.RefreshCameras, ApplyProfileToCamera).
            RestoreLiveAntialiasing();
            if (profiles > 0)
            {
                RefreshTufxCameras();
                report.Append(profiles).Append(" TUFX profile(s) restored.");
            }
            if (changedElsewhere > 0)
                report.Append(' ').Append(changedElsewhere)
                      .Append(" TUFX profile(s) left alone, changed since they were set here.");
            tufxBackups.Clear();

            // Same rule for the components switched off here: only what is still
            // off goes back on. One re-enabled meanwhile keeps that state.
            int reEnabled = 0;
            for (int i = 0; i < disabledComponents.Count; i++)
            {
                Behaviour component = disabledComponents[i];
                if (component == null || component.enabled) continue;
                component.enabled = true;
                reEnabled++;
            }
            report.Append(' ').Append(reEnabled).Append(" component(s) re-enabled.");
            disabledComponents.Clear();

            // Each SMAA back as it was found. One that was running is switched
            // on again with its "initialized" reset, so it builds and attaches
            // its buffer itself on its next frame -- once, for the camera as it
            // is then; any copy of that buffer still on the camera is taken off
            // first. One that was off but still had its buffer on the camera
            // gets the buffer back by hand, since it would never attach it
            // itself; so does a running one that had it attached, in a version
            // without a bool "initialized".
            // Only where the owner still lives: a destroyed one took its buffer
            // off in OnDestroy and cleared it (Deferred) or released it
            // (Scatterer).
            //
            // Unity does not document the order of buffers at one camera event
            // (Camera.AddCommandBuffer); if it follows insertion, a buffer attached
            // anew runs behind passes added after it -- Firefly's re-entry pass on
            // the flight camera, say -- until a restart.
            int handedBack = 0;
            for (int i = 0; i < smaaStates.Count; i++)
            {
                SmaaState state = smaaStates[i];
                if (state.Owner == null || state.Camera == null) continue;
                try
                {
                    state.Camera.RemoveCommandBuffer(state.Event, state.Buffer);
                    if (state.WasEnabled)
                    {
                        bool reset = ResetInitialized(state.Owner);
                        state.Owner.enabled = true;
                        if (!reset && state.WasAttached)
                            state.Camera.AddCommandBuffer(state.Event, state.Buffer);
                    }
                    else if (state.WasAttached)
                    {
                        state.Camera.AddCommandBuffer(state.Event, state.Buffer);
                    }
                    handedBack++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " SMAA not handed back: " + e);
                }
            }
            smaaStates.Clear();
            report.Append(' ').Append(handedBack).Append(" SMAA back as found.");
            report.Append(RestoreKfg());

            Applied = false;
            LastMessage = report.ToString();
            Debug.Log(Log.Tag + " Host stack restored: " + LastMessage);
            Refresh(true);
            return LastMessage;
        }

        // At once rather than at the next interval: TUFX has just applied a
        // profile whose antialiasing is not taken yet (TufxBehaviour).
        internal static void ReassertNow()
        {
            nextReassert = 0f;
            Reassert();
        }

        // Scatterer rebuilds its TAA components in Init on every scene change,
        // fresh on JitterAndResolve with spread 0.8. TUFX creates new
        // PostProcessLayers. Without reasserting, the setting would be gone after a
        // scene change, and the stored backups would point at destroyed objects.
        public static void Reassert()
        {
            if (!Applied) return;
            if (Time.unscaledTime < nextReassert) return;
            nextReassert = Time.unscaledTime + ReassertInterval;

            // Drop backups of destroyed components, otherwise the list grows
            // across the play session.
            for (int i = taaBackups.Count - 1; i >= 0; i--)
                if (taaBackups[i].Component == null) taaBackups.RemoveAt(i);

            Type taaType = TypeLookup.Find(ScattererTaaTypeName);
            if (taaType != null)
            {
                List<Component> taa = ScattererTaaComponents();
                for (int i = 0; i < taa.Count; i++)
                {
                    if (!IsEnabled(taa[i])) continue;
                    if (!HasBackup(taa[i]))
                        taaBackups.Add(new TaaBackup { Component = taa[i], Enabled = true });
                    Disable(taa[i]);
                }
            }

            NeutraliseFloatingOrigin(true);
            RestoreBuiltinMotionVectorShader(true);
            // Active vessel only: a full scene scan once a second costs too much,
            // and newly loaded vessels are the ones that arrive on ForceNoMotion.
            ReleaseMotionVectorModes(true);
            DisableSmaa();
            DisableKfg();
            ApplyTufx(null);
        }

        // Scatterer's SMAA hangs a CommandBuffer on Camera 00 at
        // AfterForwardAlpha -- before the rig's capture at BeforeImageEffects. The
        // upscaler would receive an image whose edges are already smoothed, and
        // edge aliasing is what it reconstructs from.
        //
        // Switching the component off rather than the setting -- the setting is
        // only read when Scatterer builds its camera stack at scene load -- and
        // taking its CommandBuffer off the camera as well (DetachOwnBuffer).
        private static string DisableSmaa()
        {
            smaaStates.RemoveAll(state => state.Owner == null || state.Camera == null);

            string scatterer = DisableSmaa(ScattererSmaaTypeName, "Scatterer SMAA");
            // Deferred adds its SMAA only in the editors (EditorLighting, an
            // EditorAny add-on): no scene-wide scan for it anywhere else.
            if (!HighLogic.LoadedSceneIsEditor) return scatterer;
            return scatterer + " " + DisableSmaa(DeferredSmaaTypeName, "Deferred SMAA");
        }

        // Switching the component off is not enough. Scatterer's and Deferred's
        // SMAA attach their CommandBuffer to their camera in OnPreCull while
        // their private "initialized" is false -- Scatterer again whenever the
        // camera's HDR setting changes, without taking the old one off -- and
        // take it away only in OnDestroy (SubpixelMorphologicalAntialiasing.cs
        // in Scatterer, SubpixelMorphologicalAntiAliasing.cs in Deferred). A
        // disabled component attaches nothing new, but the buffer already on
        // the camera keeps running. So the component's own buffer, read from
        // its SMAACommandBuffer field, is taken off its camera as well.
        // Both create that buffer once, in Awake, and rebuild it in place, so
        // the field names the very buffer their camera runs; RemoveCommandBuffer
        // takes it with every copy ("all occurrences of it will be removed",
        // Unity's scripting reference). Without the field -- another version --
        // the component is only switched off, and the report counts those.
        private sealed class SmaaState
        {
            public Behaviour Owner;
            public Camera Camera;
            public CommandBuffer Buffer;
            public CameraEvent Event;
            public bool WasEnabled;
            public bool WasAttached;
        }

        private static readonly List<SmaaState> smaaStates = new List<SmaaState>();

        // Where each mod attaches: its own static SMAACameraEvent, read from the
        // mod -- AfterForwardAlpha in both, with the same comment in both:
        // "BeforeImageEffects doesn't work well".
        private const CameraEvent DefaultSmaaEvent = CameraEvent.AfterForwardAlpha;

        private static CameraEvent SmaaEventOf(Type type)
        {
            object value;
            return TryGetStatic(type, "SMAACameraEvent", out value) && value is CameraEvent
                ? (CameraEvent)value : DefaultSmaaEvent;
        }

        private static string DisableSmaa(string typeName, string label)
        {
            Type type = TypeLookup.Find(typeName);
            if (type == null) return label + " not present.";

            CameraEvent evt = SmaaEventOf(type);
            int turnedOff = 0;
            int unreadable = 0;
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(type);
            for (int i = 0; i < all.Length; i++)
            {
                Behaviour owner = all[i] as Behaviour;
                if (owner == null) continue;

                SmaaState known = FindSmaaState(owner);
                if (known != null)
                {
                    // Already switched off here. Work only if something switched it back on
                    // meanwhile: then off again, and its buffer off again --
                    // remembered as running, so Restore gives it back running, as
                    // whoever switched it on wanted.
                    if (!owner.enabled) continue;
                    known.WasEnabled = true;
                    owner.enabled = false;
                    DetachOwnBuffer(known);
                    turnedOff++;
                    continue;
                }

                bool readable;
                CommandBuffer buffer = OwnSmaaBuffer(owner, out readable);
                Camera camera = owner.GetComponent<Camera>();
                if (buffer == null || camera == null)
                {
                    if (!owner.enabled) continue;
                    owner.enabled = false;
                    if (!disabledComponents.Contains(owner)) disabledComponents.Add(owner);
                    if (!readable) unreadable++;
                    turnedOff++;
                    continue;
                }

                SmaaState state = new SmaaState
                {
                    Owner = owner,
                    Camera = camera,
                    Buffer = buffer,
                    Event = evt,
                    WasEnabled = owner.enabled,
                };
                state.WasAttached = DetachOwnBuffer(state);
                if (!state.WasEnabled && !state.WasAttached) continue;   // nothing running

                owner.enabled = false;
                smaaStates.Add(state);
                turnedOff++;
            }

            string result = turnedOff + " " + label + " switched off.";
            if (unreadable > 0)
                result += " " + unreadable + " of them without a readable buffer, which may keep running.";
            return result;
        }

        private static SmaaState FindSmaaState(Behaviour owner)
        {
            for (int i = 0; i < smaaStates.Count; i++)
                if (smaaStates[i].Owner == owner) return smaaStates[i];
            return null;
        }

        private static CommandBuffer OwnSmaaBuffer(Behaviour owner, out bool readable)
        {
            object value;
            readable = TryGet(owner, owner.GetType(), "SMAACommandBuffer", out value);
            return value as CommandBuffer;
        }

        // Whether the buffer was on the camera: the buffers at the event before
        // and after taking it off.
        private static bool DetachOwnBuffer(SmaaState state)
        {
            int before = state.Camera.GetCommandBuffers(state.Event).Length;
            state.Camera.RemoveCommandBuffer(state.Event, state.Buffer);
            return state.Camera.GetCommandBuffers(state.Event).Length < before;
        }

        // Only a bool field of that name, and set before the component runs
        // again; for anything else the buffer goes back by hand.
        private static bool ResetInitialized(Behaviour owner)
        {
            FieldInfo field = owner.GetType().GetField("initialized",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(bool)) return false;
            field.SetValue(owner, false);
            return true;
        }

        // The status line only reads. What renders is a buffer on a camera, so
        // it counts buffers by the names both mods give them, once per camera
        // -- two SMAA components on one camera can share a name -- plus every
        // enabled component without one yet, which will attach one.
        private const string ScattererSmaaBufferPrefix = "Scatterer SMAA CommandBuffer";
        private const string DeferredSmaaBufferPrefix = "Deferred editor SMAA CommandBuffer";

        private static void AddSmaaLine(List<Line> lines, string typeName, string bufferPrefix, string label)
        {
            Type type = TypeLookup.Find(typeName);
            if (type == null) return;

            CameraEvent evt = SmaaEventOf(type);
            int active = 0;
            HashSet<Camera> counted = new HashSet<Camera>();
            UnityEngine.Object[] smaa = UnityEngine.Object.FindObjectsOfType(type);
            for (int i = 0; i < smaa.Length; i++)
            {
                Behaviour behaviour = smaa[i] as Behaviour;
                if (behaviour == null) continue;

                Camera camera = behaviour.GetComponent<Camera>();
                int onCamera = camera != null ? CountSmaaBuffers(camera, bufferPrefix, evt) : 0;
                if (camera != null && counted.Add(camera)) active += onCamera;
                if (behaviour.enabled && onCamera == 0) active++;
            }
            lines.Add(new Line
            {
                Label = label,
                Value = active == 0 ? "off" : active + " active -- smooths before FSR sees the image",
                Ok = active == 0,
                Present = true,
            });
        }

        private static int CountSmaaBuffers(Camera camera, string prefix, CameraEvent evt)
        {
            int count = 0;
            CommandBuffer[] buffers = camera.GetCommandBuffers(evt);
            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] != null && buffers[i].name != null
                    && buffers[i].name.StartsWith(prefix, StringComparison.Ordinal)) count++;
            }
            return count;
        }

        private static string DisableKfg()
        {
            Type type = TypeLookup.Find(KfgSettingsTypeName);
            if (type == null) return "Kerbal Frame Generator not present.";

            object value;
            if (!TryGetStatic(type, KfgSwitchField, out value) || !(value is bool))
                return "Kerbal Frame Generator not readable.";
            // As first found -- or on, once it is found on: it was turned on after
            // it was switched off here, and Restore gives it back on.
            if (kfgFound == null || (bool)value) kfgFound = (bool)value;
            if (!(bool)value) return "Kerbal Frame Generator blend off.";
            TrySetStatic(type, KfgSwitchField, false);
            return "Kerbal Frame Generator blend switched off.";
        }

        // Back on only if it was found on at any point (kfgFound) and still
        // holds the off written here -- the switch is written only by KFG's initializer and
        // the player's toggle (KSR2_UI_Controller.cs), so that follows the
        // player.
        private static string RestoreKfg()
        {
            bool? found = kfgFound;
            kfgFound = null;
            if (found != true) return "";

            Type type = TypeLookup.Find(KfgSettingsTypeName);
            object value;
            if (type == null || !TryGetStatic(type, KfgSwitchField, out value)
                || !(value is bool) || (bool)value) return "";
            TrySetStatic(type, KfgSwitchField, true);
            return " Kerbal Frame Generator blend back on.";
        }

        // Switching the TAA component off is not enough on its own.
        //
        // Scatterer installs its replacement motion vector shader globally via
        // GraphicsSettings.SetCustomShader at initialisation and never takes it
        // back. The component is what feeds that shader
        // TAA_UseFloatingOriginCameraMotion and TAA_PreviousFrameTransform every
        // frame. Disable the component and those globals freeze at their last
        // value -- from then on the shader folds a stale floating origin
        // correction into every motion vector, every frame, at every mode, AA only
        // included.
        //
        // Turning Scatterer's TAA off in its config does not have this problem:
        // the shader is then never installed in the first place. Disabling the
        // component at runtime has to neutralise the globals by hand.
        private static void NeutraliseFloatingOrigin(bool neutralise)
        {
            Shader.SetGlobalInt(UseFloatingOriginId, neutralise ? 0 : 1);
            if (neutralise) Shader.SetGlobalMatrix(PreviousFrameTransformId, Matrix4x4.identity);
        }

        // Neutralising the globals is not enough either -- the replacement shader
        // itself has to go.
        //
        // Scatterer installs it globally for BuiltinShaderType.MotionVectors, but
        // only inside its "if (useTemporalAntiAliasing)" block. Switching its TAA
        // off in the config therefore never installs it at all, which is why that
        // route behaves differently from disabling the component at runtime.
        // Putting the built-in shader back reproduces the config route exactly.
        private static void RestoreBuiltinMotionVectorShader(bool builtin)
        {
            if (builtin)
            {
                // Saved at the first Apply, and again whenever a mod has installed
                // an override since -- Scatterer does as its TAA starts in a later
                // scene: that one is what Restore puts back, with the TAA it goes
                // with.
                BuiltinShaderMode now = GraphicsSettings.GetShaderMode(BuiltinShaderType.MotionVectors);
                if (savedShaderMode == null || now != BuiltinShaderMode.UseBuiltin)
                {
                    savedShaderMode = now;
                    savedCustomShader = GraphicsSettings.GetCustomShader(BuiltinShaderType.MotionVectors);
                }
                GraphicsSettings.SetShaderMode(BuiltinShaderType.MotionVectors, BuiltinShaderMode.UseBuiltin);
                return;
            }

            if (savedShaderMode == null) return;

            // Only if it is still the built-in one set here. An override another mod
            // installed since stands.
            if (GraphicsSettings.GetShaderMode(BuiltinShaderType.MotionVectors)
                == BuiltinShaderMode.UseBuiltin)
            {
                if (savedCustomShader != null)
                    GraphicsSettings.SetCustomShader(BuiltinShaderType.MotionVectors, savedCustomShader);
                GraphicsSettings.SetShaderMode(BuiltinShaderType.MotionVectors, savedShaderMode.Value);
            }
            savedShaderMode = null;
            savedCustomShader = null;
        }

        // And the per-renderer modes have to be handed back to Unity.
        //
        // Scatterer's TAA sets every vessel renderer to ForceNoMotion on load and
        // corrects that to Object each frame in OnPreCull once the model matrix
        // changes. Disable the component and they freeze on ForceNoMotion: they
        // stop writing object motion while still moving in the image, and smear.
        // Its GameEvents registration survives the disable too, so newly loaded
        // vessels keep arriving on ForceNoMotion, and the sweep repeats.
        private static int ReleaseMotionVectorModes(bool activeVesselOnly)
        {
            Renderer[] renderers;
            if (activeVesselOnly)
            {
                Vessel vessel = ActiveVessel();
                if (vessel == null) return 0;
                renderers = vessel.GetComponentsInChildren<Renderer>(true);
            }
            else
            {
                renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            }

            int freed = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                if (renderer.motionVectorGenerationMode != MotionVectorGenerationMode.ForceNoMotion) continue;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                freed++;
            }
            return freed;
        }

        private static bool IsEnabled(Component component)
        {
            Behaviour behaviour = component as Behaviour;
            return behaviour == null || behaviour.enabled;
        }

        private static void Disable(Component component)
        {
            Behaviour behaviour = component as Behaviour;
            if (behaviour != null) behaviour.enabled = false;
        }

        private static bool HasBackup(Component component)
        {
            for (int i = 0; i < taaBackups.Count; i++)
                if (ReferenceEquals(taaBackups[i].Component, component)) return true;
            return false;
        }

        // report may be null -- when reasserting, nobody reads the text.
        private static void ApplyTufx(StringBuilder report)
        {
            Type profileType;
            object profile = TufxProfile(out profileType);
            if (profile == null)
            {
                if (report != null) report.Append("TUFX not installed.");
                return;
            }

            object mode;
            Type modeType;
            if (!TryGetAaMode(profile, profileType, "Antialiasing", out mode, out modeType))
            {
                if (report != null) report.Append("TUFX profile not readable.");
                return;
            }

            if (!tufxBackups.ContainsKey(profile))
            {
                object secondary;
                Type secondaryType;
                TryGetAaMode(profile, profileType, "SecondaryCameraAntialiasing",
                             out secondary, out secondaryType);
                tufxBackups[profile] = new TufxBackup { Primary = mode, Secondary = secondary };
            }

            object none = Enum.Parse(modeType, "None");
            tufxBackups[profile].Applied = none;
            TrySetAaMode(profile, profileType, "Antialiasing", none);
            TrySetAaMode(profile, profileType, "SecondaryCameraAntialiasing", none);

            // The profile alone is not enough: TUFX already copied it onto the
            // cameras' PostProcessLayers at scene start. Those have to be
            // touched as well, otherwise the change only takes effect after the
            // next scene change.
            int layers = SetLiveAntialiasingNone(none);
            if (report != null)
                report.Append("TUFX AA set to None (profile + ").Append(layers).Append(" layers).");
        }

        // ------------------------------------------------------------------
        // Reflection plumbing
        // ------------------------------------------------------------------

        // TUFX's own pass over its cameras and the settings its profile implies
        // (TexturesUnlimitedFXLoader.RefreshCameras). Nothing where TUFX or the
        // method is not there: the layers have their own back already
        // (RestoreLiveAntialiasing).
        private static void RefreshTufxCameras()
        {
            Type loaderType = TypeLookup.Find(TufxLoaderTypeName);
            if (loaderType == null) return;
            object loader;
            if (!TryGet(null, loaderType, "INSTANCE", out loader) || loader == null) return;
            MethodInfo refresh = loaderType.GetMethod("RefreshCameras", BindingFlags.Instance | BindingFlags.Public
                                                                        | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (refresh == null) return;
            try
            {
                refresh.Invoke(loader, null);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("tufx-refresh", "TUFX could not hand its cameras their antialiasing back ("
                                                      + CompatibilityLog.Reason(e) + ").");
            }
        }

        private static int SetLiveAntialiasingNone(object none)
        {
            if (none == null) return 0;
            // Layers of scenes since gone -- TUFX makes new ones with each -- let go.
            ForgetDestroyedLayers();

            int count = 0;
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                Component[] components = cameras[i].GetComponents<Component>();
                for (int j = 0; j < components.Length; j++)
                {
                    Component component = components[j];
                    // Missing scripts show up as null here.
                    if (component == null) continue;
                    if (component.GetType().Name != "PostProcessLayer") continue;
                    object before;
                    if (!liveAntialiasing.ContainsKey(component)
                        && TryGet(component, component.GetType(), "antialiasingMode", out before))
                        liveAntialiasing[component] = before;
                    if (TrySet(component, component.GetType(), "antialiasingMode", none)) count++;
                }
            }
            return count;
        }

        private static void ForgetDestroyedLayers()
        {
            foreach (Component layer in liveAntialiasing.Keys)
                if (layer == null) destroyedLayers.Add(layer);
            foreach (Component layer in destroyedLayers) liveAntialiasing.Remove(layer);
            destroyedLayers.Clear();
        }

        // What still has None, of the layers that are still there.
        private static void RestoreLiveAntialiasing()
        {
            foreach (KeyValuePair<Component, object> entry in liveAntialiasing)
            {
                Component layer = entry.Key;
                object now;
                if (layer == null || entry.Value == null
                    || !TryGet(layer, layer.GetType(), "antialiasingMode", out now)
                    || now == null || now.ToString() != "None")
                    continue;
                TrySet(layer, layer.GetType(), "antialiasingMode", entry.Value);
            }
            liveAntialiasing.Clear();
        }

        private static List<Component> ScattererTaaComponents()
        {
            List<Component> found = new List<Component>();
            Type type = TypeLookup.Find(ScattererTaaTypeName);
            if (type == null) return found;

            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(type);
            for (int i = 0; i < all.Length; i++)
            {
                Component component = all[i] as Component;
                if (component != null) found.Add(component);
            }
            return found;
        }

        private static object TufxProfile(out Type profileType)
        {
            profileType = null;
            Type loaderType = TypeLookup.Find(TufxLoaderTypeName);
            if (loaderType == null) return null;

            object loader;
            if (!TryGet(null, loaderType, "INSTANCE", out loader) || loader == null) return null;

            object profile;
            if (!TryGet(loader, loaderType, "CurrentProfile", out profile) || profile == null) return null;

            profileType = profile.GetType();
            return profile;
        }

        // Antialiasing is a struct field on the profile. A write goes through the
        // box and back again; otherwise only a copy changes.
        private static bool TryGetAaMode(object profile, Type profileType, string fieldName,
                                         out object mode, out Type modeType)
        {
            mode = null;
            modeType = null;
            FieldInfo aaField = profileType.GetField(fieldName, Any);
            if (aaField == null) return false;
            object aa = aaField.GetValue(profile);
            if (aa == null) return false;
            FieldInfo modeField = aa.GetType().GetField("Mode", Any);
            if (modeField == null) return false;
            mode = modeField.GetValue(aa);
            modeType = modeField.FieldType;
            return true;
        }

        private static bool TrySetAaMode(object profile, Type profileType, string fieldName, object mode)
        {
            if (mode == null) return false;
            FieldInfo aaField = profileType.GetField(fieldName, Any);
            if (aaField == null) return false;
            object aa = aaField.GetValue(profile);
            if (aa == null) return false;
            FieldInfo modeField = aa.GetType().GetField("Mode", Any);
            if (modeField == null) return false;
            modeField.SetValue(aa, mode);
            aaField.SetValue(profile, aa);
            return true;
        }

        private static bool TryGet(object target, Type type, string name, out object value)
        {
            FieldInfo field = type.GetField(name, Any);
            if (field != null) { value = field.GetValue(target); return true; }
            PropertyInfo property = type.GetProperty(name, Any);
            if (property != null && property.CanRead) { value = property.GetValue(target, null); return true; }
            value = null;
            return false;
        }

        private static bool TrySet(object target, Type type, string name, object value)
        {
            FieldInfo field = type.GetField(name, Any);
            if (field != null) { field.SetValue(target, value); return true; }
            PropertyInfo property = type.GetProperty(name, Any);
            if (property != null && property.CanWrite) { property.SetValue(target, value, null); return true; }
            return false;
        }

        // Static fields only: a lookup that could also find an instance member
        // would throw on the null target. The type is checked before writing.
        private static bool TryGetStatic(Type type, string name, out object value)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) { value = field.GetValue(null); return true; }
            value = null;
            return false;
        }

        private static bool TrySetStatic(Type type, string name, object value)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || value == null || !field.FieldType.IsInstanceOfType(value)) return false;
            field.SetValue(null, value);
            return true;
        }

        private static Line Info(string label, string value)
        {
            return new Line { Label = label, Value = value, Ok = true, Present = true };
        }

        private static Line Missing(string label)
        {
            return new Line { Label = label, Value = "not installed", Ok = true, Present = false };
        }
    }
}
