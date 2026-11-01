using System;
using System.Collections;
using System.Collections.Generic;

namespace ReDefinition.Autopilot.Flight
{
    // Runs a coroutine with the coroutines it waits for as one, and reports an exception thrown anywhere in them instead of letting the game drop the whole
    // chain without a word. The coroutine that was running ends there; what it left (a flight, the executor of another mod) is for the caller to put right.
    internal static class SafeRun
    {
        // The root coroutine for the game to run. What a coroutine yields that is not another coroutine goes on to the game as it is.
        internal static IEnumerator Guard(IEnumerator root, Action<Exception> failed)
        {
            var running = new Stack<IEnumerator>();
            running.Push(root);
            try
            {
                while (running.Count > 0)
                {
                    IEnumerator top = running.Peek();
                    bool more;
                    try
                    {
                        more = top.MoveNext();
                    }
                    catch (Exception e)
                    {
                        failed(e);
                        yield break;
                    }
                    if (!more)
                    {
                        running.Pop();
                        continue;
                    }
                    var inner = top.Current as IEnumerator;
                    if (inner != null)
                    {
                        running.Push(inner);
                        continue;
                    }
                    yield return top.Current;
                }
            }
            finally
            {
                // A coroutine that is stopped from outside leaves the ones it waited for, which are told to end as theirs finally blocks say.
                while (running.Count > 0)
                {
                    var disposable = running.Pop() as IDisposable;
                    if (disposable != null) disposable.Dispose();
                }
            }
        }
    }
}
