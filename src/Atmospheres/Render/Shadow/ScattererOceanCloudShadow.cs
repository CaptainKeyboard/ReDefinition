using System;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The cloud shadow on Scatterer's ocean (master plan 46.1; owner 2026-11-25: under a closed deck the sun must not glitter on the sea).
    // Scatterer 0.908's ocean takes its cloud shadow from a delegate of its EVE handler (EVEReflectionHandler.EVEOceanShadowsMethodDelegate,
    // a Func<CommandBuffer, width, height, ocean depth id, bool>), which it calls while it records its ocean buffer
    // (OceanCommandBuffer.RecreateCommandBuffers) and which EVE's AddOceanShadowCommands fills when EVE draws the clouds. While the own clouds draw,
    // the delegate is ours (CloudShadowRenderer.RecordOceanShadows); the one it held before (EVE's or none) comes back when they stop. Scatterer
    // records its ocean buffer only when the screen size changes, so each change of ours zeroes its buffers' width to make it record again.
    [AtmospheresAssumptions]
    internal static class AssumptionsOceanShadow
    {
        public static readonly ForeignAssumption OceanCloudShadow =
            new ForeignAssumption("atmo-scatterer-ocean-cloud-shadow", "Scatterer", "the own clouds' shadow on Scatterer's ocean")
                .Type("Scatterer.Scatterer").Type("Scatterer.EVEReflectionHandler").Type("Scatterer.OceanCommandBuffer")
                .Field("Scatterer.Scatterer", "EveReflectionHandler", "Scatterer.EVEReflectionHandler")
                .Field("Scatterer.EVEReflectionHandler", "EVEOceanShadowsMethodDelegate")
                .Field("Scatterer.OceanCommandBuffer", "width", "System.Int32")
                .Literal("Scatterer.OceanCommandBuffer", CloudShadowRenderer.OceanShadowsName, CloudShadowRenderer.OceanDepthName)
                .TestedVersions("0.908.0.0")
                .OnChange(WhenChanged.Fallback, "the sea glitters under the own clouds");
    }

    internal sealed class ScattererOceanCloudShadow
    {
        private readonly CloudShadowRenderer renderer;
        private readonly Func<bool> enabled;
        private readonly Func<CommandBuffer, int, int, int, bool> ours;
        private object handler;
        private FieldInfo field;
        private object previous;
        private bool installed, lastEnabled;

        internal ScattererOceanCloudShadow(CloudShadowRenderer renderer, Func<bool> enabled)
        {
            this.renderer = renderer;
            this.enabled = enabled;
            ours = Record;
        }

        // What Scatterer's ocean buffer records: the own cloud shadow where it is on, else nothing (Scatterer then clears its texture to white).
        private bool Record(CommandBuffer cb, int width, int height, int oceanDepthId)
        {
            bool on = cb != null && renderer.Ready && enabled();
            // Each record is rare (a size change or RecordAgain): the log says what the ocean got (PRUEFPROTOKOLL 281).
            AtmospheresLog.Say("own clouds: Scatterer's ocean asked for the cloud shadow (" + width + " x " + height + "): " + (on ? "recorded" : "none, the shadow is off"));
            if (!on) return false;
            renderer.RecordOceanShadows(cb, width, height, new RenderTargetIdentifier(oceanDepthId));
            return true;
        }

        // Puts the delegate in (once Scatterer's handler exists) and makes the ocean record again when the shadow turns on or off. Called each frame.
        internal void Update()
        {
            if (!AssumptionsOceanShadow.OceanCloudShadow.Holds) return;
            // Re-asserted each frame (PRUEFPROTOKOLL 284): EVE puts its own delegate back when its clouds are applied again (EVEReflectionHandler.MapEVEClouds),
            // and a new Scatterer instance (a scene) has a new handler; either way ours is set again and the ocean records again.
            if (installed && !StillOurs()) installed = false;
            if (!installed && !Install()) return;
            bool on = enabled() && renderer.Ready;
            if (on != lastEnabled)
            {
                lastEnabled = on;
                RecordAgain();
            }
        }

        // Whether Scatterer's current handler still holds our delegate.
        private bool StillOurs()
        {
            try
            {
                Type scattererType = TypeLookup.Find("Scatterer.Scatterer");
                object scatterer = scattererType?.GetProperty("Instance", TypeLookup.Any)?.GetValue(null, null);
                object h = scatterer != null ? scattererType.GetField("EveReflectionHandler", TypeLookup.Any)?.GetValue(scatterer) : null;
                return h != null && ReferenceEquals(h, handler) && ReferenceEquals(field.GetValue(h), ours);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool Install()
        {
            try
            {
                Type scattererType = TypeLookup.Find("Scatterer.Scatterer");
                object scatterer = scattererType?.GetProperty("Instance", TypeLookup.Any)?.GetValue(null, null);
                object h = scatterer != null ? scattererType.GetField("EveReflectionHandler", TypeLookup.Any)?.GetValue(scatterer) : null;
                FieldInfo f = h?.GetType().GetField("EVEOceanShadowsMethodDelegate", TypeLookup.Any);
                if (f == null || !f.FieldType.IsAssignableFrom(typeof(Func<CommandBuffer, int, int, int, bool>))) return false;
                handler = h;
                field = f;
                previous = f.GetValue(h);
                f.SetValue(h, ours);
                installed = true;
                AtmospheresLog.Say("own clouds: the cloud shadow is Scatterer's ocean shadow source (before: " + (previous == null ? "none" : "EVE's") + ")");
                lastEnabled = false;
                RecordAgain();
                return true;
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("own clouds: the cloud shadow on Scatterer's ocean is not available (" + e.Message + ")");
                return false;
            }
        }

        // The delegate it held before comes back (the own clouds stop).
        internal void Uninstall()
        {
            if (!installed) return;
            try
            {
                if (ReferenceEquals(field.GetValue(handler), ours)) field.SetValue(handler, previous);
                RecordAgain();
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("own clouds: Scatterer's ocean delegate could not be put back (" + e.Message + ")");
            }
            installed = false;
            handler = null;
            field = null;
            previous = null;
        }

        // Scatterer records an ocean buffer again when its width differs from the screen's (OceanCommandBuffer.EnableForThisFrame).
        private static void RecordAgain()
        {
            Type buffers = TypeLookup.Find("Scatterer.OceanCommandBuffer");
            FieldInfo width = buffers?.GetField("width", TypeLookup.Any);
            if (buffers == null || width == null) return;
            foreach (UnityEngine.Object buffer in UnityEngine.Object.FindObjectsOfType(buffers))
                width.SetValue(buffer, 0);
        }
    }
}
