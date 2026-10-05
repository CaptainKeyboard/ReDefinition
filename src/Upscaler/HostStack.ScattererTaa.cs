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
        }

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
        // component at runtime has to neutralise the globals by hand. Undone, the
        // switch goes back to what the first neutralising found; a TAA switched on
        // again sets it itself as it renders (TemporalAntiAliasing.OnPreCull, decompiled).
        private static int? savedUseFloatingOrigin;

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
        // vessels keep arriving on ForceNoMotion, and the sweep repeats. Only the
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

        private static Shader builtinMotionVectorShader;

        private static Shader BuiltinMotionVectorShader()
        {
            if (builtinMotionVectorShader == null)
                builtinMotionVectorShader = Shader.Find("Hidden/Internal-MotionVectors");
            return builtinMotionVectorShader;
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
