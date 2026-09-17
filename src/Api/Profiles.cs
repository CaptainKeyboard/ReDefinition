using System;

namespace ReDefinition.Api
{
    /// <summary>
    /// The graphics profile the player chose in ReDefinition's window.
    /// </summary>
    public static class Profiles
    {
        /// <summary>
        /// The profile's name as its <c>REDEFINITION_PROFILE</c> node gives it -- <c>low</c>,
        /// <c>medium</c>, <c>high</c>, <c>ultra</c>, <c>max</c> -- or null while none is chosen, when
        /// ReDefinition's upscaler and frame generation are off.
        /// </summary>
        public static string Current
        {
            get { return SharedFrame.Profile; }
        }

        /// <summary>
        /// Calls <paramref name="handler"/> with the new name, or null, whenever the profile changes.
        /// </summary>
        /// <param name="handler">Called on the main thread; removed if it throws.</param>
        public static void RegisterChanged(Action<string> handler)
        {
            SharedFrame.ProfileChangedHandlers.Add(handler);
        }

        /// <summary>
        /// Removes a handler registered with <see cref="RegisterChanged"/>.
        /// </summary>
        /// <param name="handler">The handler registered.</param>
        public static void UnregisterChanged(Action<string> handler)
        {
            SharedFrame.ProfileChangedHandlers.Remove(handler);
        }
    }
}
