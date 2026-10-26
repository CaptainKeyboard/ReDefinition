using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System;
using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Scatterer's TAA and what it leaves behind (HostStack).
    internal static partial class HostStack
    {
        // Scatterer adds its TAA to the cameras in Init at every scene change
        // (Scatterer.cs, AddComponent<TemporalAntiAliasing>, decompiled): a postfix on its
        // Awake switches each one off as it comes while the upscaler runs, rather than up
        // to ReassertInterval later. Reassert stays as the net under it.
        private static bool taaHookTried;

        // Hooks the TAA's Awake and RegisterVesselRenderers, once, each on its own.
        private static void HookScattererTaa(Type taaType)
        {
            if (taaHookTried || taaType == null) return;
            taaHookTried = true;
            MethodInfo awake = taaType.GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                                 null, Type.EmptyTypes, null);
            try
            {
                HarmonyHooks.Install("ReDefinition.ScattererTaa", typeof(HostStack), new[]
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
                HarmonyHooks.Install("ReDefinition.ScattererTaaVessels", typeof(HostStack), new[]
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
        // Also from the TAA's own Awake, for the vessels loaded then, just before its
        // postfix above switches it off.
        private static void ScattererTaaRegisterPostfix(Vessel __0)
        {
            if (!Applied || __0 == null) return;
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
            if (!Applied || taa == null) return;
            HarmonyHooks.Safely("scatterer-taa", "Scatterer's new TAA could not be switched off as it came", () =>
            {
                if (!HasBackup(taa)) taaBackups.Add(new TaaBackup { Component = taa, Enabled = IsEnabled(taa) });
                Disable(taa);
                // Awake installed its motion vector shader and floating origin globals.
                NeutraliseFloatingOrigin(true);
                RestoreBuiltinMotionVectorShader(true);
            });
        }

        // The floating origin switch as the first neutralising found it, null while none is saved.
        // Undone, the switch goes back to it; a TAA switched on
        // again sets it itself as it renders (TemporalAntiAliasing.OnPreCull, decompiled).
        private static int? savedUseFloatingOrigin;

        // Sets Scatterer's floating origin globals to no correction, saving the switch the first time; false puts the
        // saved switch back. Switching the TAA component off is not enough on its own: the component is what feeds
        // the replacement motion vector shader TAA_UseFloatingOriginCameraMotion and TAA_PreviousFrameTransform every
        // frame. Disabled, those globals freeze at their last value. From then on the shader folds a stale floating
        // origin correction into every motion vector, every frame, at every mode, AA only included.
        private static void NeutraliseFloatingOrigin(bool neutralise)
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
        private static void RestoreBuiltinMotionVectorShader(bool builtin)
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
        // changes (RegisterVesselRenderers, UpdateVesselRendererMotionModes,
        // decompiled). Disable the component and they freeze on ForceNoMotion: they
        // stop writing object motion while still moving in the image, and smear.
        // Its GameEvents registration survives the disable too, so newly loaded
        // vessels keep arriving on ForceNoMotion: freed as they come
        // (ScattererTaaRegisterPostfix), and the sweep repeats. Only the
        // renderers of vessels, the ones Scatterer sets: ForceNoMotion elsewhere is
        // another mod's choice and stands.
        private static int ReleaseMotionVectorModes(bool activeVesselOnly)
        {
            List<Renderer> renderers;
            if (activeVesselOnly)
            {
                Vessel vessel = ActiveVessel();
                if (vessel == null) return 0;
                renderers = VesselRenderers(vessel);
            }
            else
            {
                renderers = new List<Renderer>();
                List<Vessel> loaded = FlightGlobals.fetch != null ? FlightGlobals.VesselsLoaded : null;
                if (loaded != null)
                    foreach (Vessel vessel in loaded)
                        if (vessel != null) renderers.AddRange(VesselRenderers(vessel));
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

        // A vessel's renderers. In flight its parts are not children of its
        // transform: GetComponentsInChildren on the vessel finds the root part's
        // alone (17 of 382 renderers on a Kerbal X).
        private static List<Renderer> VesselRenderers(Vessel vessel)
        {
            List<Renderer> renderers = new List<Renderer>();
            foreach (Part part in vessel.parts)
                if (part != null) renderers.AddRange(part.GetComponentsInChildren<Renderer>(true));
            return renderers;
        }

        // BuiltinMotionVectorShader's cache, null until first asked.
        private static Shader builtinMotionVectorShader;

        // Unity's own motion vector shader (Hidden/Internal-MotionVectors), looked up once.
        private static Shader BuiltinMotionVectorShader()
        {
            if (builtinMotionVectorShader == null)
                builtinMotionVectorShader = Shader.Find("Hidden/Internal-MotionVectors");
            return builtinMotionVectorShader;
        }

        // Whether a component is on; one that is no Behaviour has no switch and counts as on.
        private static bool IsEnabled(Component component)
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

        // Whether taaBackups already holds the component.
        private static bool HasBackup(Component component)
        {
            for (int i = 0; i < taaBackups.Count; i++)
                if (ReferenceEquals(taaBackups[i].Component, component)) return true;
            return false;
        }

        // Scatterer's TAA components on the cameras (OnCameras); none without Scatterer.
        private static List<Component> ScattererTaaComponents()
        {
            List<Component> found = new List<Component>();
            Type type = TypeLookup.Find(ScattererTaaTypeName);
            if (type == null) return found;

            found.AddRange(OnCameras(type));
            return found;
        }
    }
}
