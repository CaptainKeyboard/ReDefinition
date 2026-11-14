using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The driver of the layer pass with the temporal reconstruction (package T12): sets up the camera and the layer frame, keeps one
    // LayerReconstructor for the player's view and one per probe camera, resets the history on cuts and records the march and the composite into
    // a command buffer. It uses nothing of the game but the camera, so the weather player links it as it is; the integration package (T09) calls
    // it from the module that owns the layer stack, with the counters of the game (see Cuts).
    //
    // The policy of the cameras (T05, P61): the player's view keeps one reconstructor across frames; a probe camera (a reflection probe, a map or a
    // screenshot camera: PlayerCamera is not it) renders rarely and from poses that jump, so it has no history to keep: it gets a reconstructor of
    // its own that is reset before every use and marches once at the reduced size (ReconSettings.ForProbe).
    internal sealed class LayerReconstructDriver
    {
        private readonly ReconCutDetector cuts = new ReconCutDetector();
        private readonly Dictionary<int, LayerReconstructor> probes = new Dictionary<int, LayerReconstructor>();
        private LayerReconstructor player;
        private Shader reconShader, edgeShader;
        private ReconSettings playerSettings = new ReconSettings();

        // The camera whose view the player sees; any other camera is a probe.
        internal Camera PlayerCamera;
        // The reason of the last reset of the player's history (for the log and the tests).
        internal string LastCutReason { get; private set; }
        internal int CutCount { get; private set; }

        internal LayerReconstructor Player { get { return player; } }

        internal bool Init(Shader recon, Shader edge)
        {
            reconShader = recon;
            edgeShader = edge;
            player = Make();
            return player != null;
        }

        private LayerReconstructor Make()
        {
            var r = new LayerReconstructor();
            if (!r.Init(reconShader)) return null;
            if (edgeShader != null) r.InitEdge(edgeShader);
            return r;
        }

        // The settings of this frame from the tuning keys (reconstructorEnabled, edgeAdaptive, edgeMaskDilation, edgeThreshold, edgePersist) over the mode and
        // block the options hold. Off, the reconstructor does not run and the caller draws the layer pass as it did (LayerRenderer.Prepare).
        internal bool Configure(Func<string, float, float> tuning, ReconSettings options)
        {
            bool on = EdgeSettings.ReconstructorOn(tuning) || options.Mode != ReconMode.Full;
            if (!on) return false;
            playerSettings = options.Clone();
            if (options.Mode == ReconMode.Full)
            {
                playerSettings.Mode = ReconMode.Block;
                playerSettings.Block = EdgeSettings.CoarseBlock;
            }
            playerSettings.Edge = EdgeSettings.From(tuning);
            return true;
        }

        // The history of the player's view is dropped at the next Record (a request of the game: a teleport the detector cannot see, a mod's cut).
        internal void Reset(string reason)
        {
            if (player != null) player.Reset();
            cuts.Forget();
            LastCutReason = reason;
            CutCount++;
        }

        // Records the layer pass of a camera into the buffer: the march, the reconstruction and the composite over the camera target (or
        // destination). position is the camera's planet-relative position; the counters are those of the game (see ReconCutDetector.Observe).
        // Returns false where the pass cannot run.
        internal bool Record(CommandBuffer cb, LayerRenderer renderer, LayerFrame frame, Camera camera, RenderTargetIdentifier sceneDepth,
                             RenderTargetIdentifier destination, int frameNumber, int sceneId, int apiResetCount, int weatherCutCount, int stackVersion)
        {
            if (cb == null || renderer == null || !renderer.Ready || camera == null || player == null) return false;
            cb.SetGlobalTexture(Shader.PropertyToID("_SceneDepth"), sceneDepth);
            if (camera != PlayerCamera)
            {
                LayerReconstructor probe = ProbeFor(camera);
                if (probe == null) return false;
                probe.Reset();
                probe.Record(cb, renderer, frame, camera, ReconSettings.ForProbe(), frameNumber, null, null, null);
                probe.Composite(cb, renderer, destination, null);
                return true;
            }
            Vector3 p = camera.transform.position - frame.PlanetCenter;
            string reason = cuts.Observe(p.x, p.y, p.z, sceneId, apiResetCount, weatherCutCount, stackVersion);
            if (reason != null)
            {
                player.Reset();
                LastCutReason = reason;
                CutCount++;
            }
            player.Record(cb, renderer, frame, camera, playerSettings, frameNumber, null, null, null);
            player.Composite(cb, renderer, destination, null);
            return true;
        }

        private LayerReconstructor ProbeFor(Camera camera)
        {
            int id = camera.GetInstanceID();
            LayerReconstructor r;
            if (probes.TryGetValue(id, out r)) return r;
            r = Make();
            if (r != null) probes[id] = r;
            return r;
        }

        // Frees the reconstructors of probe cameras that are gone.
        internal void Prune(HashSet<int> liveCameraIds)
        {
            var gone = new List<int>();
            foreach (KeyValuePair<int, LayerReconstructor> kv in probes)
                if (!liveCameraIds.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (int id in gone)
            {
                probes[id].Dispose();
                probes.Remove(id);
            }
        }

        internal int ProbeCount { get { return probes.Count; } }

        internal void Dispose()
        {
            if (player != null) player.Dispose();
            foreach (LayerReconstructor r in probes.Values) r.Dispose();
            probes.Clear();
            player = null;
        }
    }
}
