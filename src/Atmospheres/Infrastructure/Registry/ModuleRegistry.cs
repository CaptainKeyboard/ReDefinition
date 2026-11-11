using System;
using System.Collections.Generic;
using System.Reflection;

namespace ReDefinition.Atmospheres
{
    // A part of the atmospheres that starts, runs and stops on its own. A package adds its module as a class with the attribute
    // AtmospheresModule and never edits a shared start-up file; ModuleRegistry finds it by reflection and isolates it: an exception in
    // one module does not reach the others.
    internal interface IAtmospheresModule
    {
        // Called once when the module starts; may be called again after a failure (the registry retries a few times).
        void Init();
        // The active body changed (BodyWatcher, after its hysteresis). Either body may be null.
        void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody);
        // Called every frame with the frame time in seconds while the module runs.
        void Tick(float dt);
        // Called once when the registry stops.
        void Shutdown();
    }

    // Marks a class as a module of the atmospheres. Lower orders start and tick first.
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class AtmospheresModuleAttribute : Attribute
    {
        internal AtmospheresModuleAttribute(int order)
        {
            Order = order;
        }

        internal int Order { get; }
    }

    // Where a module is in its life.
    internal enum ModuleState
    {
        // Not started yet, or waiting for the next try of Init.
        Waiting,
        Running,
        // Init failed every time, or Tick failed too often in a row: the module is off for the rest of the session.
        Failed,
        // Shut down in order.
        Stopped
    }

    // The modules of the atmospheres, started in order of their attribute, each in its own try/catch. A module whose Init throws is tried
    // again a few times (RetryGate); one whose Tick throws too many times in a row (FailureStreak) is switched off and told to shut down.
    internal sealed class ModuleRegistry
    {
        internal const float InitRetrySeconds = 2f;
        internal const int InitTries = 3;
        internal const int TickFailureLimit = 5;

        private sealed class Entry
        {
            internal IAtmospheresModule Module;
            internal int Order;
            internal string Name;
            internal ModuleState State;
            internal RetryGate InitGate;
            internal FailureStreak Streak;
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly Action<string> warn;

        // warn receives a line for every failure; the default writes a warning to the log.
        internal ModuleRegistry(Action<string> warn = null)
        {
            this.warn = warn ?? AtmospheresLog.WarnOnce;
        }

        // The number of modules.
        internal int Count
        {
            get { return entries.Count; }
        }

        // Adds a module with its order; equal orders keep the order of adding.
        internal void Add(IAtmospheresModule module, int order)
        {
            if (module == null) throw new ArgumentNullException("module");
            var entry = new Entry
            {
                Module = module,
                Order = order,
                Name = module.GetType().Name,
                State = ModuleState.Waiting,
                InitGate = new RetryGate(InitRetrySeconds, InitTries),
                Streak = new FailureStreak(TickFailureLimit)
            };
            int at = entries.Count;
            while (at > 0 && entries[at - 1].Order > order) at--;
            entries.Insert(at, entry);
        }

        // The state of a module.
        internal ModuleState StateOf(IAtmospheresModule module)
        {
            foreach (Entry entry in entries)
            {
                if (ReferenceEquals(entry.Module, module)) return entry.State;
            }
            throw new ArgumentException("The module is not registered.");
        }

        // The types in an assembly that are modules, in the order they start: a non-abstract class that implements IAtmospheresModule and
        // carries the attribute, by order and then by name.
        internal static List<Type> Discover(Assembly assembly)
        {
            var found = new List<KeyValuePair<int, Type>>();
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = Array.FindAll(e.Types, t => t != null);
            }
            foreach (Type type in types)
            {
                if (type.IsAbstract || !type.IsClass || !typeof(IAtmospheresModule).IsAssignableFrom(type)) continue;
                var attribute = (AtmospheresModuleAttribute)Attribute.GetCustomAttribute(type, typeof(AtmospheresModuleAttribute), false);
                if (attribute == null) continue;
                found.Add(new KeyValuePair<int, Type>(attribute.Order, type));
            }
            found.Sort((a, b) =>
            {
                int c = a.Key.CompareTo(b.Key);
                return c != 0 ? c : string.CompareOrdinal(a.Value.FullName, b.Value.FullName);
            });
            return found.ConvertAll(pair => pair.Value);
        }

        // A registry with an instance of every module of the assembly. A module that cannot be made is reported and left out.
        internal static ModuleRegistry FromAssembly(Assembly assembly, Action<string> warn = null)
        {
            var registry = new ModuleRegistry(warn);
            foreach (Type type in Discover(assembly))
            {
                try
                {
                    var module = (IAtmospheresModule)Activator.CreateInstance(type, true);
                    var attribute = (AtmospheresModuleAttribute)Attribute.GetCustomAttribute(type, typeof(AtmospheresModuleAttribute), false);
                    registry.Add(module, attribute.Order);
                }
                catch (Exception e)
                {
                    registry.warn("module " + type.Name + " could not be made (" + Reason(e) + ")");
                }
            }
            return registry;
        }

        // Starts the modules that are waiting and ticks those that run. now is the real time in seconds, dt the frame time.
        internal void Tick(float now, float dt)
        {
            foreach (Entry entry in entries)
            {
                if (entry.State == ModuleState.Waiting) TryInit(entry, now);
                if (entry.State != ModuleState.Running) continue;
                try
                {
                    entry.Module.Tick(dt);
                    entry.Streak.Succeeded();
                }
                catch (Exception e)
                {
                    Failed(entry, "Tick", e);
                }
            }
        }

        // Tells the running modules that the active body changed.
        internal void BodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            foreach (Entry entry in entries)
            {
                if (entry.State != ModuleState.Running) continue;
                try
                {
                    entry.Module.OnBodyChanged(oldBody, newBody);
                }
                catch (Exception e)
                {
                    Failed(entry, "OnBodyChanged", e);
                }
            }
        }

        // Shuts down the running modules in the reverse order of their start.
        internal void Shutdown()
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry entry = entries[i];
                if (entry.State != ModuleState.Running) continue;
                entry.State = ModuleState.Stopped;
                try
                {
                    entry.Module.Shutdown();
                }
                catch (Exception e)
                {
                    warn("module " + entry.Name + " failed in Shutdown (" + Reason(e) + ")");
                }
            }
        }

        private void TryInit(Entry entry, float now)
        {
            if (!entry.InitGate.Due(now)) return;
            try
            {
                entry.Module.Init();
                entry.State = ModuleState.Running;
            }
            catch (Exception e)
            {
                warn("module " + entry.Name + " failed in Init, try " + entry.InitGate.Tries + " of " + InitTries + " (" + Reason(e) + ")");
                if (entry.InitGate.GaveUp) entry.State = ModuleState.Failed;
            }
        }

        private void Failed(Entry entry, string where, Exception e)
        {
            bool limit = entry.Streak.Failed();
            warn("module " + entry.Name + " failed in " + where + " (" + Reason(e) + ")" + (limit ? "; it is switched off" : ""));
            if (!limit) return;
            entry.State = ModuleState.Failed;
            try
            {
                entry.Module.Shutdown();
            }
            catch (Exception)
            {
                // A module that failed is not asked twice.
            }
        }

        private static string Reason(Exception e)
        {
            Exception x = e.InnerException ?? e;
            string message = x.Message;
            return string.IsNullOrEmpty(message) ? x.GetType().Name : message;
        }
    }
}
