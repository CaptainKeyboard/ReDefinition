using System;
using System.Collections.Generic;

namespace ReDefinition.Shared
{
    // Handlers other mods register with ReDefinition.Api. Each runs on its own: one
    // that throws is removed and reported once, and the others still run.
    internal sealed class HookList<T> where T : class
    {
        private readonly string name;
        private readonly Action<string> report;
        private readonly List<T> handlers = new List<T>();
        private readonly List<T> snapshot = new List<T>();

        internal HookList(string name, Action<string> report)
        {
            this.name = name;
            this.report = report;
        }

        internal bool Any
        {
            get { return handlers.Count > 0; }
        }

        internal int Count
        {
            get { return handlers.Count; }
        }

        internal void Add(T handler)
        {
            if (handler != null && !handlers.Contains(handler)) handlers.Add(handler);
        }

        internal void Remove(T handler)
        {
            if (handler != null) handlers.Remove(handler);
        }

        // Over a copy: a handler may register or remove handlers.
        internal void Invoke(Action<T> call)
        {
            if (handlers.Count == 0) return;
            snapshot.Clear();
            snapshot.AddRange(handlers);
            foreach (T handler in snapshot)
            {
                try
                {
                    call(handler);
                }
                catch (Exception e)
                {
                    handlers.Remove(handler);
                    report("A handler registered for " + name + " threw and is removed: " + Describe(handler) + " ("
                           + e.GetType().Name + ": " + e.Message + ").");
                }
            }
        }

        private static string Describe(T handler)
        {
            Delegate method = handler as Delegate;
            if (method == null || method.Method == null) return "unknown";
            Type owner = method.Method.DeclaringType;
            return (owner != null ? owner.FullName + "." : "") + method.Method.Name
                   + (owner != null ? " in " + owner.Assembly.GetName().Name : "");
        }
    }
}
