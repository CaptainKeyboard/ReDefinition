using UnityEngine;

namespace ReDefinition.Atmospheres.Audio
{
    // Sits on the object of the active AudioListener and reads the final mix in its OnAudioFilterRead (the data are only read). The meter does
    // the sums; the tap only forwards. Added by the series probe, never in play.
    internal sealed class AudioProbeTap : MonoBehaviour
    {
        internal volatile ProbeMeter Meter;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            ProbeMeter meter = Meter;
            if (meter != null) meter.Feed(data, channels);
        }
    }
}
