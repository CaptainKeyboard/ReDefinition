using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // P5: the audio path for the weather sound. Two ways to make a sound the plan weighs: A, a component beside an AudioSource whose
    // OnAudioFilterRead writes the samples, and B, an AudioClip made with AudioClip.Create that streams through a callback. Each is placed
    // left and right of the listener; the output's left and right levels (AudioListener.GetOutputData) say whether the 3D pan works.
    // Then the game is paused the way KSP pauses it (FlightDriver.SetPause) and the output is read again. The volume settings, the mixer
    // groups of the scene's sources and the audio members of KerbalEVA are listed. The audio module is reached by reflection: the
    // assembly does not reference it.
    internal sealed partial class AtmoRecon
    {
        // A stream for path B: the engine calls Generate on the audio thread.
        private sealed class StreamTone
        {
            internal volatile bool On;
            internal volatile int Calls, CallsWithTone;
            private double phase;

            public void Generate(float[] data)
            {
                Calls++;
                if (On) CallsWithTone++;
                double step = 2.0 * Math.PI * 440.0 / 48000.0;
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = On ? (float)(Math.Sin(phase) * 0.5) : 0f;
                    phase += step;
                    if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                }
            }
        }

        private Type audioListenerType, audioSourceType, audioClipType;

        private IEnumerable<ProbeWait> AudioPaths()
        {
            audioListenerType = ReconDyn.Type("UnityEngine.AudioListener");
            audioSourceType = ReconDyn.Type("UnityEngine.AudioSource");
            audioClipType = ReconDyn.Type("UnityEngine.AudioClip");
            if (audioListenerType == null || audioSourceType == null || audioClipType == null)
            {
                EmitText("P5.error", "the audio module is not loaded");
                yield break;
            }
            ListAudioFacts();

            Camera camera = FlightCamera.fetch.mainCamera;
            var listener = camera.GetComponent(audioListenerType);
            Emit("P5.listener_on_main_camera", listener != null);
            // Run1 placed the sources by the transform of "a" listener, and the output said both sources were heard from the left: the
            // listener the engine uses is the ACTIVE ENABLED one, so the scene's listeners are listed and that one is taken.
            var listenerList = new StringBuilder();
            Component active = null;
            foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsOfType(audioListenerType))
            {
                var component = found as Component;
                if (component == null) continue;
                object enabledValue = ReconDyn.Get(found, "enabled");
                bool isEnabled = enabledValue is bool && (bool)enabledValue;
                bool isActive = component.gameObject.activeInHierarchy && isEnabled;
                listenerList.Append(component.gameObject.name).Append(isActive ? " (active)" : " (inactive)").Append("; ");
                if (isActive && active == null) active = component;
            }
            EmitText("P5.listeners", listenerList.ToString().Trim());
            if (active != null) listener = active;
            if (listener == null)
            {
                EmitText("P5.error", "no AudioListener in the flight scene");
                yield break;
            }
            Transform at = listener.transform;
            EmitText("P5.listener_used", listener.gameObject.name);

            // Path A.
            GameObject goA = ReconMesh.Track(new GameObject("ReDefinition recon audio A"));
            object clipA = ReconDyn.CallStatic(audioClipType, "Create", "recon A", 4800, 1, 48000, false);
            object sourceA = goA.AddComponent(audioSourceType);
            ReconTone tone = goA.AddComponent<ReconTone>();
            ConfigureSource(sourceA, clipA);

            // Path B.
            GameObject goB = ReconMesh.Track(new GameObject("ReDefinition recon audio B"));
            var stream = new StreamTone();
            Type callbackType = audioClipType.GetNestedType("PCMReaderCallback");
            object sourceB = goB.AddComponent(audioSourceType);
            if (callbackType != null)
            {
                Delegate callback = Delegate.CreateDelegate(callbackType, stream, "Generate");
                object clipB = ReconDyn.CallStatic(audioClipType, "Create", "recon B", 48000, 1, 48000, true, callback);
                ConfigureSource(sourceB, clipB);
            }
            else
            {
                EmitText("P5.error", "AudioClip.PCMReaderCallback not found");
            }

            // Run1's baseline carried the game's own sounds (left 0.11, right 0.60), louder than the test tones: every other source is
            // muted for the step and unmuted at its end (and by the clean-up), so that the output is the tones.
            Emit("P5.sources_muted", MuteOtherSources(goA, goB));
            yield return ProbeWait.Frames(10);

            double baseL = 0, baseR = 0;
            foreach (ProbeWait w in ReadOutput(6)) yield return w;
            baseL = outputL;
            baseR = outputR;
            Emit("P5.baseline_rms_left", baseL);
            Emit("P5.baseline_rms_right", baseR);

            double[] panA = new double[2], panB = new double[2];
            for (int side = 0; side < 2; side++)
            {
                // Left of the listener first, then right.
                Vector3 place = at.position + at.right * (side == 0 ? -5f : 5f);
                goA.transform.position = place;
                goB.transform.position = place;

                tone.On = true;
                yield return ProbeWait.Frames(15);
                foreach (ProbeWait w in ReadOutput(6)) yield return w;
                panA[side] = ReconMath.PanIndex(ReconMath.AddedRms(outputL, baseL), ReconMath.AddedRms(outputR, baseR));
                Emit("P5.path_a_added_left_" + (side == 0 ? "src_left" : "src_right"), ReconMath.AddedRms(outputL, baseL));
                Emit("P5.path_a_added_right_" + (side == 0 ? "src_left" : "src_right"), ReconMath.AddedRms(outputR, baseR));
                tone.On = false;

                stream.On = true;
                yield return ProbeWait.Frames(15);
                foreach (ProbeWait w in ReadOutput(6)) yield return w;
                panB[side] = ReconMath.PanIndex(ReconMath.AddedRms(outputL, baseL), ReconMath.AddedRms(outputR, baseR));
                Emit("P5.path_b_added_left_" + (side == 0 ? "src_left" : "src_right"), ReconMath.AddedRms(outputL, baseL));
                Emit("P5.path_b_added_right_" + (side == 0 ? "src_left" : "src_right"), ReconMath.AddedRms(outputR, baseR));
                stream.On = false;
            }
            // Run1: path B added nothing on any channel. Whether the stream was asked for samples and whether the source plays says if the
            // stream path is dead or the probe's setup was.
            Emit("P5.path_b_generate_calls", stream.Calls);
            Emit("P5.path_b_calls_with_tone_on", stream.CallsWithTone);
            object playingB = ReconDyn.Get(sourceB, "isPlaying");
            Emit("P5.path_b_is_playing", playingB is bool && (bool)playingB);
            object playingA = ReconDyn.Get(sourceA, "isPlaying");
            Emit("P5.path_a_is_playing", playingA is bool && (bool)playingA);
            Emit("P5.pan_a", panA[0] - panA[1]);
            Emit("P5.pan_b", panB[0] - panB[1]);
            Emit("P5.output_alive", ReconMath.AddedRms(outputL, 0) + ReconMath.AddedRms(outputR, 0) > 0 || baseL + baseR > 0);

            // Pause, the way the game does it, with both tones on at the left.
            Vector3 left = at.position - at.right * 5f;
            goA.transform.position = left;
            goB.transform.position = left;
            tone.On = true;
            stream.On = true;
            yield return ProbeWait.Frames(15);
            foreach (ProbeWait w in ReadOutput(6)) yield return w;
            double runningRms = Math.Max(outputL, outputR);
            Emit("P5.running_rms_with_tones", runningRms);
            bool paused = false;
            try
            {
                FlightDriver.SetPause(true, false);
                paused = true;
                pausedByProbe = true;
            }
            catch (Exception e)
            {
                EmitText("P5.error", "FlightDriver.SetPause failed: " + e.Message);
            }
            yield return ProbeWait.Frames(15);
            object pausedFlag = ReconDyn.GetStatic(audioListenerType, "pause");
            Emit("P5.listener_pause_flag", pausedFlag is bool && (bool)pausedFlag);
            Emit("P5.time_scale_paused", Time.timeScale);
            foreach (ProbeWait w in ReadOutput(6)) yield return w;
            Emit("P5.pause_rms", Math.Max(outputL, outputR));
            if (paused)
            {
                FlightDriver.SetPause(false, false);
                pausedByProbe = false;
            }
            // Run1: the game's pause (Time.timeScale 0) did not silence the filter-read path and AudioListener.pause stayed false. The
            // listener's own pause flag is the mechanism the plan names: set it, read the output, put it back.
            PropertyInfo pauseProperty = audioListenerType.GetProperty("pause", BindingFlags.Public | BindingFlags.Static);
            if (pauseProperty != null)
            {
                yield return ProbeWait.Frames(10);
                foreach (ProbeWait w in ReadOutput(6)) yield return w;
                Emit("P5.running_rms_before_listener_pause", Math.Max(outputL, outputR));
                try
                {
                    pauseProperty.SetValue(null, true, null);
                }
                catch (Exception e)
                {
                    EmitText("P5.error", "AudioListener.pause could not be set: " + e.Message);
                }
                yield return ProbeWait.Frames(15);
                foreach (ProbeWait w in ReadOutput(6)) yield return w;
                Emit("P5.listener_pause_rms", Math.Max(outputL, outputR));
                try
                {
                    pauseProperty.SetValue(null, false, null);
                }
                catch (Exception)
                {
                }
                yield return ProbeWait.Frames(10);
                object after = pauseProperty.GetValue(null, null);
                Emit("P5.listener_pause_restored", after is bool && !(bool)after);
            }
            tone.On = false;
            stream.On = false;
            yield return ProbeWait.Frames(10);
            Emit("P5.time_scale_after", Time.timeScale);

            RestoreMutedSources();
            UnityEngine.Object.Destroy(goA);
            UnityEngine.Object.Destroy(goB);
        }

        // The sources the probe muted, with their mute flag before.
        private readonly List<UnityEngine.Object> mutedByProbe = new List<UnityEngine.Object>();

        private int MuteOtherSources(GameObject keepA, GameObject keepB)
        {
            int n = 0;
            foreach (UnityEngine.Object source in UnityEngine.Object.FindObjectsOfType(audioSourceType))
            {
                var component = source as Component;
                if (component == null || component.gameObject == keepA || component.gameObject == keepB) continue;
                object muted = ReconDyn.Get(source, "mute");
                if (muted is bool && (bool)muted) continue;
                if (ReconDyn.Set(source, "mute", true))
                {
                    mutedByProbe.Add(source);
                    n++;
                }
            }
            return n;
        }

        private void RestoreMutedSources()
        {
            foreach (UnityEngine.Object source in mutedByProbe)
            {
                if (source != null) ReconDyn.Set(source, "mute", false);
            }
            mutedByProbe.Clear();
        }

        private void ConfigureSource(object source, object clip)
        {
            ReconDyn.Set(source, "clip", clip);
            ReconDyn.Set(source, "loop", true);
            ReconDyn.Set(source, "spatialBlend", 1f);
            ReconDyn.Set(source, "minDistance", 5f);
            ReconDyn.Set(source, "maxDistance", 100f);
            ReconDyn.Set(source, "volume", 1f);
            ReconDyn.Call(source, "Play");
        }

        // Averages the listener's output level over several frames into outputL and outputR (RMS of the latest block of each channel).
        private double outputL, outputR;

        private IEnumerable<ProbeWait> ReadOutput(int frames)
        {
            double sumL = 0, sumR = 0;
            var block = new float[1024];
            for (int i = 0; i < frames; i++)
            {
                yield return ProbeWait.Frames(1);
                sumL += Rms(block, 0);
                sumR += Rms(block, 1);
            }
            outputL = sumL / frames;
            outputR = sumR / frames;
        }

        private double Rms(float[] block, int channel)
        {
            ReconDyn.CallStatic(audioListenerType, "GetOutputData", block, channel);
            double sum = 0;
            for (int i = 0; i < block.Length; i++) sum += block[i] * block[i];
            return Math.Sqrt(sum / block.Length);
        }

        // The settings, the mixer groups of the sources in the scene, and what KerbalEVA offers for the footsteps.
        private void ListAudioFacts()
        {
            var volumes = new StringBuilder();
            int volumeFields = 0;
            foreach (FieldInfo f in typeof(GameSettings).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.Name.EndsWith("_VOLUME"))
                {
                    volumeFields++;
                    volumes.Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(null), System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
                }
            }
            Emit("P5.volume_fields", volumeFields);
            EmitText("P5.volume_values", volumes.ToString().Trim());

            var groups = new SortedDictionary<string, int>();
            int sources = 0;
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(audioSourceType);
            foreach (UnityEngine.Object source in all)
            {
                sources++;
                object group = ReconDyn.Get(source, "outputAudioMixerGroup");
                string name = group != null ? Convert.ToString(ReconDyn.Get(group, "name")) : "(none)";
                int n;
                groups[name] = groups.TryGetValue(name, out n) ? n + 1 : 1;
            }
            var groupText = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in groups) groupText.Append(pair.Key).Append(" x").Append(pair.Value).Append("; ");
            Emit("P5.audio_sources", sources);
            Emit("P5.mixer_groups", groups.Count);
            EmitText("P5.mixer_group_list", groupText.ToString().Trim());

            var eva = new StringBuilder();
            foreach (string member in ReconDyn.MembersOfType(typeof(KerbalEVA), "Audio")) eva.Append(member).Append("; ");
            int states = 0;
            var movement = new StringBuilder();
            foreach (FieldInfo f in typeof(KerbalEVA).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.Name.StartsWith("st_")) states++;
                if (f.Name.IndexOf("Step", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) >= 0
                    || f.Name.IndexOf("OnGround", StringComparison.OrdinalIgnoreCase) >= 0) movement.Append(f.Name).Append(':').Append(f.FieldType.Name).Append("; ");
            }
            foreach (PropertyInfo p in typeof(KerbalEVA).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (p.Name.IndexOf("OnGround", StringComparison.OrdinalIgnoreCase) >= 0 || p.Name.IndexOf("Grounded", StringComparison.OrdinalIgnoreCase) >= 0)
                    movement.Append(p.Name).Append(':').Append(p.PropertyType.Name).Append("; ");
            }
            Emit("P5.eva_audio_member_count", ReconDyn.MembersOfType(typeof(KerbalEVA), "Audio").Count);
            EmitText("P5.eva_audio_members", eva.ToString().Trim());
            Emit("P5.eva_fsm_states", states);
            EmitText("P5.eva_movement_members", movement.ToString().Trim());
            Say("P5: " + volumeFields + " volume fields, " + sources + " sources in " + groups.Count + " groups, KerbalEVA audio members: " + eva);
        }
    }
}
