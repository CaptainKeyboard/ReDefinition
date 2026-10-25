using System;
using System.Collections.Generic;

namespace ReDefinition.Shared
{
    // Handlers other mods register with ReDefinition.Api. Each runs on its own: one
    // that throws is removed and reported once, and the others still run.
    internal sealed class HookList<T> where T : class
    {
        // The hook's name and the log the removal of a throwing handler goes to; the handlers in the order they
        // registered, the copy the outermost Invoke runs over, and how deep Invoke is nested.
        private readonly string name;
        private readonly Action<string> report;
        private readonly List<T> handlers = new List<T>();
        private readonly List<T> snapshot = new List<T>();
        private int invoking;

        // `name` is the hook as the log names it; `report` writes the line when a handler is removed.
        internal HookList(string name, Action<string> report)
        {
            this.name = name;
            this.report = report;
        }

        // Whether any handler is registered: callers skip work no one would see.
        internal bool Any
        {
            get { return handlers.Count > 0; }
        }

        // How many handlers are registered, for the log.
        internal int Count
        {
            get { return handlers.Count; }
        }

        // Registers a handler once; null and a second registration of the same one are ignored.
        internal void Add(T handler)
        {
            if (handler != null && !handlers.Contains(handler)) handlers.Add(handler);
        }

        // Removes a handler; one that is not registered is ignored. An Invoke running still calls it this time.
        internal void Remove(T handler)
        {
            if (handler != null) handlers.Remove(handler);
        }

        // Over a copy: a handler may register or remove handlers. A handler that
        // invokes the same list again gets a copy of its own.
        internal void Invoke(Action<T> call)
        {
            if (handlers.Count == 0) return;
            List<T> copy = invoking == 0 ? snapshot : new List<T>(handlers.Count);
            copy.Clear();
            // One by one: Mono's AddRange copies through a new array.
            for (int i = 0; i < handlers.Count; i++) copy.Add(handlers[i]);
            invoking++;
            try
            {
                for (int i = 0; i < copy.Count; i++)
                {
                    T handler = copy[i];
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
            finally
            {
                invoking--;
            }
        }

        // The handler's method with its type and assembly, for the log; "unknown" for anything but a delegate.
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
