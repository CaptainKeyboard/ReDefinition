using System;

namespace ReDefinition.Atmospheres
{
    // A subscription to game events that may be tried before the game has made them. KSP creates some of its event objects late
    // (K-W3a: the module Init in the main menu met a null event and threw), so a module that subscribes once in Init stays deaf for
    // the whole session. Ensure is called from every tick until the subscription holds: the subscribe function returns false while
    // an event is not there yet (it must add nothing in that case), or throws; the failure is logged once per kind and the next tick
    // tries again. Release takes the handlers out again.
    internal sealed class LateSubscription
    {
        private readonly string kind;
        private readonly string what;
        private readonly Func<bool> subscribe;
        private readonly Action unsubscribe;

        private readonly Action<string> say;
        private readonly Action<string, string, Exception> failed;

        internal LateSubscription(string kind, string what, Func<bool> subscribe, Action unsubscribe)
            : this(kind, what, subscribe, unsubscribe, AtmospheresLog.Say, AtmospheresLog.Failed)
        {
        }

        // The log functions are parameters so that the unit tests need no game.
        internal LateSubscription(string kind, string what, Func<bool> subscribe, Action unsubscribe, Action<string> say, Action<string, string, Exception> failed)
        {
            this.kind = kind;
            this.what = what;
            this.subscribe = subscribe;
            this.unsubscribe = unsubscribe;
            this.say = say;
            this.failed = failed;
        }

        // Whether the handlers are in.
        internal bool Hooked { get; private set; }

        // How many times the subscription was tried.
        internal int Attempts { get; private set; }

        // Tries to subscribe unless it holds; true when the handlers are in.
        internal bool Ensure()
        {
            if (Hooked) return true;
            Attempts++;
            try
            {
                Hooked = subscribe();
            }
            catch (Exception e)
            {
                failed(kind, what + " yet and tries again", e);
                return false;
            }
            if (Hooked && Attempts > 1) say(what + ": subscribed at attempt " + Attempts);
            return Hooked;
        }

        // Takes the handlers out; safe when they never went in or the game is gone.
        internal void Release()
        {
            if (!Hooked) return;
            Hooked = false;
            try
            {
                unsubscribe();
            }
            catch (Exception)
            {
                // The events are gone with the game.
            }
        }
    }
}
