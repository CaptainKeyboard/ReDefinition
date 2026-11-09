using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System;
using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Scatterer's TAA and what it leaves behind: its components, the motion vector shader and floating origin
    // globals, and the motion modes of the vessel renderers (HostStack).
    internal sealed class ScattererTaaControl : IHostFilter
    {
        // The state found is remembered, so a restore puts back that state rather
        // than defaults.
        private sealed class TaaBackup
        {
            // The component and whether it was enabled when found.
            public Component Component;
            public bool Enabled;
        }

        // Scatterer's TAA component, by full type name.
        internal const string TypeName = "Scatterer.TemporalAntiAliasing";
        private const string MotionVectorShader = "Scatterer/Internal-MotionVectors";

        // Globals that Scatterer's TemporalAntiAliasing feeds to its replacement
        // motion vector shader every frame.
        private static readonly int UseFloatingOriginId =
            Shader.PropertyToID("TAA_UseFloatingOriginCameraMotion");
        private static readonly int PreviousFrameTransformId =
            Shader.PropertyToID("TAA_PreviousFrameTransform");

        // Every Scatterer TAA component switched off here, with its state as found.
        private readonly List<TaaBackup> backups = new List<TaaBackup>();

        // Scatterer adds its TAA to the cameras in Init at every scene change
        // (Scatterer.cs, AddComponent<TemporalAntiAliasing>): a postfix on its
        // Awake switches each one off as it comes while the upscaler runs, rather than up
        // to ReassertInterval later. Reassert stays as the net under it.
        private bool hookTried;

        // The floating origin switch as the first neutralising found it, null while none is saved.
        // Undone, the switch goes back to it; a TAA switched on
        // again sets it itself as it renders (TemporalAntiAliasing.OnPreCull).
        private int? savedUseFloatingOrigin;

        // The motion vector shader's mode and custom shader before RestoreBuiltinMotionVectorShader set Unity's own; null
        // while nothing is saved.
        private BuiltinShaderMode? savedShaderMode;
        private Shader savedCustomShader;

        // The renderers the sweeps and the report list, and one part's, both kept between passes so
        // that each makes no list or array. One user at a time: the passes run on the main thread and none nests.
        private readonly List<Renderer> renderScratch = new List<Renderer>();
        private readonly List<Renderer> partRenderers = new List<Renderer>();

        // The scattered TAA components, kept for the same reason; a caller reads the list before the next fills it.
        private readonly List<Component> taaScratch = new List<Component>();

        // BuiltinMotionVectorShader's cache, null until first asked.
        private Shader builtinMotionVectorShader;

        // How many TAA components ran when AddLines last looked; the motion vector row judges by it.
        private int running;

        // The name in the notes of a step that failed.
        public string Label
        {
            get { return "Scatterer's TAA"; }
        }

        // Fills `found` (emptied first) with Scatterer's TAA components on the cameras (HostCameras.OnCameras); none
        // without Scatterer. Returns it.
        internal List<Component> Components(List<Component> found)
        {
            HostCameras.OnCameras(TypeLookup.Find(TypeName), found);
            return found;
        }

        // Whether a component is on; one that is no Behaviour has no switch and counts as on.
        internal static bool IsEnabled(Component component)
        {
            Behaviour behaviour = component as Behaviour;
            return behaviour == null || behaviour.enabled;
        }

        // Switches a Behaviour off; anything else is left alone.
        private static void Disable(Component component)
        {
            Behaviour behaviour = component as Behaviour;
            if (behaviour != null) behaviour.enabled = false;
        }

        // Whether backups already holds the component.
        private bool HasBackup(Component component)
        {
            for (int i = 0; i < backups.Count; i++)
                if (ReferenceEquals(backups[i].Component, component)) return true;
            return false;
        }

        // FlightGlobals' getter throws while the flight scene is torn down -- when
        // Restore runs from the add-on's OnDestroy (KSP.log).
        private static Vessel ActiveVessel()
        {
            return FlightGlobals.fetch != null ? FlightGlobals.ActiveVessel : null;
        }

        // Switches off every Scatterer TAA component and what it leaves behind; remembers what it found for Restore.
        public void Apply(StringBuilder report)
        {
            Type taaType = TypeLookup.Find(TypeName);
            List<Component> taa = Components(taaScratch);
            HookScattererTaa(taaType);

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
                    backups.Add(new TaaBackup { Component = taa[i], Enabled = IsEnabled(taa[i]) });
                    Disable(taa[i]);
                    changed++;
                }
                report.Append(changed).Append(" Scatterer TAA switched off. ");
            }
        }

        // Scatterer rebuilds its TAA components in Init on every scene change,
        // fresh on JitterAndResolve with spread 0.8. TUFX creates new
        // PostProcessLayers. Without reasserting, the setting would be gone after a
        // scene change, and the stored backups would point at destroyed objects.
        public void Reassert()
        {
            // Drop backups of destroyed components, otherwise the list grows
            // across the play session.
            for (int i = backups.Count - 1; i >= 0; i--)
                if (backups[i].Component == null) backups.RemoveAt(i);

            Type taaType = TypeLookup.Find(TypeName);
            if (taaType != null)
            {
                List<Component> taa = Components(taaScratch);
                for (int i = 0; i < taa.Count; i++)
                {
                    if (!IsEnabled(taa[i])) continue;
                    if (!HasBackup(taa[i]))
                        backups.Add(new TaaBackup { Component = taa[i], Enabled = true });
                    Disable(taa[i]);
                }
            }

            NeutraliseFloatingOrigin(true);
            RestoreBuiltinMotionVectorShader(true);
            // Active vessel only: a full scene scan once a second costs too much,
            // and newly loaded vessels are the ones that arrive on ForceNoMotion.
            ReleaseMotionVectorModes(true);
        }

        // Switches the Scatterer TAA components back on that were on when found, and puts back its floating origin
        // switch and motion vector shader.
        public void Restore(StringBuilder report)
        {
            Type taaType = TypeLookup.Find(TypeName);
            int restored = 0;
            for (int i = 0; i < backups.Count; i++)
            {
                TaaBackup backup = backups[i];
                if (backup.Component == null || taaType == null) continue;
                // Counted only where something is handed back: a component that is
                // no Behaviour has no switch.
                Behaviour behaviour = backup.Component as Behaviour;
                if (behaviour == null || behaviour.enabled) continue;
                behaviour.enabled = backup.Enabled;
                restored++;
            }
            backups.Clear();
            NeutraliseFloatingOrigin(false);
            RestoreBuiltinMotionVectorShader(false);
            report.Append(restored).Append(" Scatterer TAA restored. ");
        }

        // The row for Scatterer's TAA, and the count of the components that run, for AddMotionLines.
        public void AddLines(List<HostStack.Line> lines)
        {
            Type taaType = TypeLookup.Find(TypeName);
            List<Component> taa = Components(taaScratch);
            // Switched off counts as off: FindObjectsOfType finds disabled ones too,
            // among them the ones Apply switched off.
            int switchedOff = taa.RemoveAll(component => !IsEnabled(component));
            running = taa.Count;
            if (taaType == null)
            {
                lines.Add(HostStack.Line.Missing("Scatterer"));
            }
            else if (taa.Count == 0)
            {
                // None running is the state the upscaler needs -- whether Scatterer's file has
                // useTemporalAntiAliasing = False or its components are switched off.
                lines.Add(HostStack.Line.Info("Scatterer TAA", switchedOff > 0 ? "off (" + switchedOff + " switched off)" : "off"));
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
                    ForeignReflection.TryGet(taa[i], taaType, "role", out role);
                    ForeignReflection.TryGet(taa[i], taaType, "jitterSpread", out spread);
                    float spreadValue = spread is float ? (float)spread : -1f;

                    if (role != null && role.ToString() == "JitterOnly") jitterOnly++;
                    if (spreadValue == 0f) zeroSpread++;

                    if (detail.Length > 0) detail.Append(", ");
                    detail.Append(taa[i].gameObject.name).Append(": ")
                          .Append(role == null ? "?" : role.ToString())
                          .Append(" / ").Append(spreadValue.ToString("0.##"));
                }

                lines.Add(new HostStack.Line
                {
                    Label = "Scatterer TAA",
                    Value = taa.Count + " components -- " + detail,
                    Ok = jitterOnly == taa.Count && zeroSpread == taa.Count,
                    Present = true,
                });
            }
        }

        // The rows for the active vessel's motion modes and for the motion vector shader, judged by the TAA components
        // AddLines counted.
        internal void AddMotionLines(List<HostStack.Line> lines)
        {
            // Renderers frozen on ForceNoMotion write no object motion while
            // still moving in the image, and smear.
            Vessel vessel = ActiveVessel();
            if (vessel != null)
            {
                int frozen = 0;
                List<Renderer> renderers = renderScratch;
                VesselRenderers(vessel, renderers);
                for (int i = 0; i < renderers.Count; i++)
                    if (renderers[i] != null
                        && renderers[i].motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion)
                        frozen++;

                lines.Add(new HostStack.Line
                {
                    Label = "Vessel renderers",
                    Value = frozen == 0
                        ? renderers.Count + " renderers, none on ForceNoMotion"
                        : frozen + " of " + renderers.Count + " on ForceNoMotion -- these will smear",
                    Ok = frozen == 0,
                    Present = true,
                });
            }

            lines.Add(MotionVectorLine(running > 0));
        }

        // Judged by the state, not by whether Apply has run. Unity's own shader is
        // right wherever Scatterer's TAA does not run: Scatterer's replacement
        // depends on globals only that component feeds, and a frozen correction
        // adds error. Scatterer's replacement is right only while its TAA
        // runs and feeds it.
        private static HostStack.Line MotionVectorLine(bool scattererTaaRuns)
        {
            BuiltinShaderMode mode = GraphicsSettings.GetShaderMode(BuiltinShaderType.MotionVectors);
            if (mode != BuiltinShaderMode.UseCustom)
            {
                return new HostStack.Line
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
            bool scatterers = shader != null && name == MotionVectorShader;
            return new HostStack.Line
            {
                Label = "Motion vector shader",
                Value = name + (scatterers && !scattererTaaRuns ? " -- its TAA is off, the correction is frozen" : ""),
                Ok = scatterers && scattererTaaRuns,
                Present = true,
            };
        }

        // Hooks the TAA's Awake and RegisterVesselRenderers, once, each on its own.
        private void HookScattererTaa(Type taaType)
        {
            if (hookTried || taaType == null) return;
            hookTried = true;
            MethodInfo awake = taaType.GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                                 null, Type.EmptyTypes, null);
            try
            {
                HarmonyHooks.Install("ReDefinition.ScattererTaa", typeof(ScattererTaaControl), new[]
                {
                    new HarmonyHook(awake, null, nameof(ScattererTaaAwakePostfix), null),
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Scatterer's TAA is taken off once a second instead of as it comes: " + e.Message);
            }

            // On its own: a build without the method still has the hook above.
            MethodInfo register = taaType.GetMethod("RegisterVesselRenderers", BindingFlags.NonPublic | BindingFlags.Instance,
                                                    null, new[] { typeof(Vessel) }, null);
            try
            {
                HarmonyHooks.Install("ReDefinition.ScattererTaaVessels", typeof(ScattererTaaControl), new[]
                {
                    new HarmonyHook(register, null, nameof(ScattererTaaRegisterPostfix), null),
                });
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("scatterer-taa-vessels", "Scatterer's TAA's hold on a changed vessel's renderers is"
                                                               + " taken off once a second, for the active vessel only ("
                                                               + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The TAA's GameEvents registration outlives its disable
        // (TemporalAntiAliasing.Awake: onVesselLoaded, onVesselWasModified,
        // onVesselPartCountChanged). Each vessel loaded or changed (at staging the
        // vessel and what it drops) has every renderer of its parts set to
        // ForceNoMotion (RegisterVesselRenderers, Part.FindModelComponents), which the
        // disabled TAA never corrects. Taken off again as it is set while the upscaler
        // runs, rather than up to ReassertInterval later and for the active vessel only.
        // Also from the TAA's own Awake, for the vessels loaded then, just before
        // ScattererTaaAwakePostfix switches it off.
        private static void ScattererTaaRegisterPostfix(Vessel __0)
        {
            if (!HostStack.Applied || __0 == null) return;
            HarmonyHooks.Safely("scatterer-taa-vessels", "A vessel's renderers could not be freed from Scatterer's ForceNoMotion", () =>
            {
                foreach (Part part in __0.parts)
                {
                    if (part == null) continue;
                    foreach (Renderer renderer in part.FindModelComponents<Renderer>())
                        if (renderer != null && renderer.motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion)
                            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                }
            });
        }

        // A new TAA component switched off as it wakes while the upscaler runs, with the shader and globals its Awake set.
        private static void ScattererTaaAwakePostfix(object __instance)
        {
            Component taa = __instance as Component;
            if (!HostStack.Applied || taa == null) return;
            ScattererTaaControl self = HostStack.ScattererTaa;
            HarmonyHooks.Safely("scatterer-taa", "Scatterer's new TAA could not be switched off as it came", () =>
            {
                if (!self.HasBackup(taa)) self.backups.Add(new TaaBackup { Component = taa, Enabled = IsEnabled(taa) });
                Disable(taa);
                // Awake installed its motion vector shader and floating origin globals.
                self.NeutraliseFloatingOrigin(true);
                self.RestoreBuiltinMotionVectorShader(true);
            });
        }

        // Sets Scatterer's floating origin globals to no correction, saving the switch the first time; false puts the
        // saved switch back. Switching the TAA component off is not enough on its own: the component is what feeds
        // the replacement motion vector shader TAA_UseFloatingOriginCameraMotion and TAA_PreviousFrameTransform every
        // frame. Disabled, those globals freeze at their last value. From then on the shader folds a stale floating
        // origin correction into every motion vector, every frame, at every mode, AA only included.
        private void NeutraliseFloatingOrigin(bool neutralise)
        {
            if (neutralise)
            {
                if (savedUseFloatingOrigin == null) savedUseFloatingOrigin = Shader.GetGlobalInt(UseFloatingOriginId);
                Shader.SetGlobalInt(UseFloatingOriginId, 0);
                Shader.SetGlobalMatrix(PreviousFrameTransformId, Matrix4x4.identity);
                return;
            }
            if (savedUseFloatingOrigin == null) return;
            Shader.SetGlobalInt(UseFloatingOriginId, savedUseFloatingOrigin.Value);
            savedUseFloatingOrigin = null;
        }

        // Neutralising the globals is not enough either -- the replacement shader
        // itself has to go.
        //
        // Scatterer installs it globally for BuiltinShaderType.MotionVectors via
        // GraphicsSettings.SetCustomShader at initialisation and never takes it back, but
        // only inside its "if (useTemporalAntiAliasing)" block. Switching its TAA
        // off in the config therefore never installs it at all, which is why that
        // route behaves differently from disabling the component at runtime.
        // Putting the built-in shader back reproduces the config route exactly.
        private void RestoreBuiltinMotionVectorShader(bool builtin)
        {
            if (builtin)
            {
                // Saved at the first Apply, and again whenever a mod has installed
                // an override since -- Scatterer does as its TAA starts in a later
                // scene: that one is what Restore puts back, with the TAA it goes
                // with.
                BuiltinShaderMode now = GraphicsSettings.GetShaderMode(BuiltinShaderType.MotionVectors);
                Shader custom = GraphicsSettings.GetCustomShader(BuiltinShaderType.MotionVectors);
                Shader own = BuiltinMotionVectorShader();
                if (savedShaderMode == null || now != BuiltinShaderMode.UseBuiltin
                    || (custom != null && custom != own))
                {
                    savedShaderMode = now;
                    savedCustomShader = custom;
                }
                GraphicsSettings.SetShaderMode(BuiltinShaderType.MotionVectors, BuiltinShaderMode.UseBuiltin);
                // The mode alone does not do it: with UseBuiltin set and Scatterer's
                // shader left in the custom slot, RenderDoc still showed Scatterer's
                // shader drawing the motion vector pass. With Unity's own shader in
                // the slot, Unity's draws it.
                if (own != null) GraphicsSettings.SetCustomShader(BuiltinShaderType.MotionVectors, own);
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
        // changes (RegisterVesselRenderers, UpdateVesselRendererMotionModes).
        // Disable the component and they freeze on ForceNoMotion: they
        // stop writing object motion while still moving in the image, and smear.
        // Its GameEvents registration survives the disable too, so newly loaded
        // vessels keep arriving on ForceNoMotion: freed as they come
        // (ScattererTaaRegisterPostfix), and the sweep repeats. Only the
        // renderers of vessels, the ones Scatterer sets: ForceNoMotion elsewhere is
        // another mod's choice and stands.
        private int ReleaseMotionVectorModes(bool activeVesselOnly)
        {
            List<Renderer> renderers = renderScratch;
            renderers.Clear();
            if (activeVesselOnly)
            {
                Vessel vessel = ActiveVessel();
                if (vessel == null) return 0;
                VesselRenderers(vessel, renderers);
            }
            else
            {
                List<Vessel> loaded = FlightGlobals.fetch != null ? FlightGlobals.VesselsLoaded : null;
                if (loaded != null)
                    foreach (Vessel vessel in loaded)
                        if (vessel != null) AddVesselRenderers(vessel, renderers);
            }

            int freed = 0;
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                if (renderer.motionVectorGenerationMode != MotionVectorGenerationMode.ForceNoMotion) continue;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                freed++;
            }
            return freed;
        }

        // Fills `into` (emptied first) with a vessel's renderers. In flight its parts are not children of its
        // transform: GetComponentsInChildren on the vessel finds the root part's
        // alone (17 of 382 renderers on a Kerbal X).
        private void VesselRenderers(Vessel vessel, List<Renderer> into)
        {
            into.Clear();
            AddVesselRenderers(vessel, into);
        }

        // Appends a vessel's renderers to `into`.
        private void AddVesselRenderers(Vessel vessel, List<Renderer> into)
        {
            foreach (Part part in vessel.parts)
            {
                if (part == null) continue;
                part.GetComponentsInChildren(true, partRenderers);
                into.AddRange(partRenderers);
            }
            partRenderers.Clear();
        }

        // Unity's own motion vector shader (Hidden/Internal-MotionVectors), looked up once.
        private Shader BuiltinMotionVectorShader()
        {
            if (builtinMotionVectorShader == null)
                builtinMotionVectorShader = Shader.Find("Hidden/Internal-MotionVectors");
            return builtinMotionVectorShader;
        }
    }
}
