using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres.Audio
{
    // The Unity side of the weather sound: one object with a 2D looping source on a stream clip (output path B of RECON P5), and a 3D source
    // for the footsteps with a second one for the thump. Created when the master switch is on in a flight scene, destroyed with the scene or
    // when the switch goes off; with the switch off none of this exists.
    internal sealed class WeatherAudioObjects
    {
        internal const string ObjectName = "ReDefinition Weather Audio";

        private readonly SharedAudioState shared;
        private readonly Mixer mixer;
        private readonly WeatherAudioStream stream;
        private readonly int sampleRate;
        private GameObject root;
        private AudioSource bed;
        private AudioClip streamClip;
        // One source per foot: a step is played with source.clip set (RSE's routing is keyed by the clip a source carries, which
        // PlayOneShot would leave empty) and the two feet let the tail of one step ring under the next.
        private AudioSource stepLeft;
        private AudioSource stepRight;
        private AudioSource thump;
        private AudioLowPassFilter stepFilterLeft;
        private AudioLowPassFilter stepFilterRight;
        private readonly AudioClip[] thumpClips = new AudioClip[3];

        // A mixer for this output rate; the seed fixes the noise so that a scenario repeats.
        internal WeatherAudioObjects(SharedAudioState shared, int sampleRate, uint seed)
        {
            this.shared = shared;
            this.sampleRate = sampleRate;
            mixer = new Mixer(sampleRate, seed);
            stream = new WeatherAudioStream(shared, mixer, sampleRate);
        }

        internal Mixer Mixer { get { return mixer; } }
        internal int SampleRate { get { return sampleRate; } }
        internal GameObject Root { get { return root; } }
        internal bool Alive { get { return root != null && bed != null; } }

        // Makes the objects and starts the stream. The recorded bed, if any, is set before the first block.
        internal void Create(float[] recordedBedMono, int recordedBedRate)
        {
            if (recordedBedMono != null) mixer.SetRecordedBed(recordedBedMono, recordedBedRate);
            root = new GameObject(ObjectName);
            streamClip = AudioClip.Create("ReDefinition weather stream", sampleRate, 2, sampleRate, true, OnRead);
            bed = root.AddComponent<AudioSource>();
            bed.clip = streamClip;
            bed.loop = true;
            bed.spatialBlend = 0f;
            bed.volume = 1f;
            bed.playOnAwake = false;
            bed.bypassEffects = false;
            bed.ignoreListenerPause = false;
            bed.dopplerLevel = 0f;
            bed.Play();

            var leftObject = new GameObject("step left");
            leftObject.transform.SetParent(root.transform, false);
            stepLeft = AddOneShotSource(leftObject);
            stepFilterLeft = leftObject.AddComponent<AudioLowPassFilter>();
            stepFilterLeft.cutoffFrequency = 22000f;
            var rightObject = new GameObject("step right");
            rightObject.transform.SetParent(root.transform, false);
            stepRight = AddOneShotSource(rightObject);
            stepFilterRight = rightObject.AddComponent<AudioLowPassFilter>();
            stepFilterRight.cutoffFrequency = 22000f;
            var thumpObject = new GameObject("thump");
            thumpObject.transform.SetParent(root.transform, false);
            thump = AddOneShotSource(thumpObject);
            thumpClips[0] = MakeThump(sampleRate, 80f, 0.20f);
            thumpClips[1] = MakeThump(sampleRate, 62f, 0.24f);
            thumpClips[2] = MakeThump(sampleRate, 48f, 0.30f);
            root.AddComponent<WeatherAudioBehaviour>();
        }

        private static AudioSource AddOneShotSource(GameObject go)
        {
            AudioSource s = go.AddComponent<AudioSource>();
            s.loop = false;
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 1f;
            s.maxDistance = 40f;
            s.dopplerLevel = 0f;
            return s;
        }

        // The callback of the stream: the audio thread calls it. No Unity API is touched in here.
        private void OnRead(float[] data)
        {
            stream.Fill(data, 2);
        }

        // A short decaying sine, the low part of a step: the body weight (the clip is made once, its pitch and gain follow the kerbal).
        private static AudioClip MakeThump(int rate, float hz, float seconds)
        {
            int n = (int)(rate * seconds);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double env = Math.Exp(-t * 22.0) * Math.Min(1.0, t * 400.0);
                data[i] = (float)(Math.Sin(2.0 * Math.PI * hz * t) * env);
            }
            AudioClip clip = AudioClip.Create("ReDefinition thump", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // Plays one step: the sampled crunch under the procedural thump, at the foot position.
        internal void PlayStep(AudioClip crunch, StepVoice voice, float volume, Vector3 position, bool leftFoot)
        {
            if (!Alive) return;
            thump.transform.position = position;
            if (crunch != null)
            {
                AudioSource foot = leftFoot ? stepLeft : stepRight;
                foot.transform.position = position;
                foot.Stop();
                foot.clip = crunch;
                foot.pitch = voice.Pitch;
                foot.volume = Mathf.Clamp01(voice.Gain * volume);
                foot.Play();
            }
            int t = voice.ThumpPitch > 0.85f ? 0 : voice.ThumpPitch > 0.7f ? 1 : 2;
            thump.pitch = 1f;
            thump.PlayOneShot(thumpClips[t], voice.ThumpGain * volume);
        }

        // The mixer group a step source is routed to ("" when none): RSE's routing shows here once it has taken the clip.
        internal string StepMixerGroup
        {
            get
            {
                if (stepLeft != null && stepLeft.outputAudioMixerGroup != null) return stepLeft.outputAudioMixerGroup.name;
                if (stepRight != null && stepRight.outputAudioMixerGroup != null) return stepRight.outputAudioMixerGroup.name;
                return "";
            }
        }

        // Takes the step sources out of any mixer group another mod gave them (audioMuffling Own: the own low-pass is the one muffler).
        internal void DetachStepMixerGroup()
        {
            if (stepLeft != null && stepLeft.outputAudioMixerGroup != null) stepLeft.outputAudioMixerGroup = null;
            if (stepRight != null && stepRight.outputAudioMixerGroup != null) stepRight.outputAudioMixerGroup = null;
        }

        // Stops the steps and the thump that are still sounding (a pause does not end a one-shot by itself).
        internal void StopSteps()
        {
            if (stepLeft != null) stepLeft.Stop();
            if (stepRight != null) stepRight.Stop();
            if (thump != null) thump.Stop();
        }

        // Muffles the sampled clips (the own low-pass on exactly the sources RSE does not carry).
        internal void SetStepLowPass(float hz)
        {
            if (stepFilterLeft != null) stepFilterLeft.cutoffFrequency = hz;
            if (stepFilterRight != null) stepFilterRight.cutoffFrequency = hz;
        }

        // Stops and destroys everything. The stream callback may still run once; it holds no Unity object.
        internal void Destroy()
        {
            try
            {
                if (bed != null) bed.Stop();
                StopSteps();
            }
            catch (Exception)
            {
            }
            if (root != null) UnityEngine.Object.Destroy(root);
            if (streamClip != null) UnityEngine.Object.Destroy(streamClip);
            for (int i = 0; i < thumpClips.Length; i++)
            {
                if (thumpClips[i] != null) UnityEngine.Object.Destroy(thumpClips[i]);
                thumpClips[i] = null;
            }
            root = null;
            bed = null;
            stepLeft = null;
            stepRight = null;
            stepFilterLeft = null;
            stepFilterRight = null;
            thump = null;
            streamClip = null;
            shared.Running = false;
        }

        // The output rate of the game.
        internal static int OutputRate()
        {
            int rate = AudioSettings.outputSampleRate;
            return rate >= 8000 ? rate : 48000;
        }
    }

    // Runs after every other script of the frame (LateUpdate), so that a write to EVE's source (policy Ours) comes after EVE's own update.
    [DefaultExecutionOrder(1000)]
    internal sealed class WeatherAudioBehaviour : MonoBehaviour
    {
        private void LateUpdate()
        {
            WeatherAudioModule module = WeatherAudioModule.Instance;
            if (module != null) module.LateTick();
        }
    }
}
