using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The weather player draws as the game does at Medium, the tier of the A4500 the game runs are measured on: the fast cloud path on (CloudFastPath; the game's
    // profile module installs the tier's answer, which the player has no profile for). A job that wants the full path sets the keys of the path by hand.
    internal static class PlayerFastPath
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            CloudFastPath.Source = () => true;
        }
    }
}
