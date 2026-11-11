using System;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Drives the ModuleRegistry from the game's frame: builds it from this assembly, ticks it every frame, passes on the changes of the
    // active body and shuts it down when the game quits. AtmospheresStartup adds it once; it lives across scenes.
    internal sealed class ModuleRunner : MonoBehaviour
    {
        private static ModuleRegistry registry;

        // The registry, once the runner has started.
        internal static ModuleRegistry Registry
        {
            get { return registry; }
        }

        private void Awake()
        {
            try
            {
                registry = ModuleRegistry.FromAssembly(Assembly.GetExecutingAssembly());
                BodyWatcher.Changed += OnBodyChanged;
                AtmospheresLog.Say("modules: " + registry.Count);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("modules", "the modules could not be started", e);
            }
        }

        private void Update()
        {
            if (registry == null) return;
            try
            {
                Tuning.BeginFrame(Time.frameCount);
                registry.Tick(Time.unscaledTime, Time.unscaledDeltaTime);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("modules", "the module loop failed", e);
            }
        }

        private void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            if (registry != null) registry.BodyChanged(oldBody, newBody);
        }

        private void OnDestroy()
        {
            BodyWatcher.Changed -= OnBodyChanged;
            if (registry != null) registry.Shutdown();
        }
    }
}
