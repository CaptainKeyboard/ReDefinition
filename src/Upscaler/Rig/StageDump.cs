using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // The diagnostics window's screen recording also writes the frame's stages, for following a
    // fault through the pipeline away from the game. For a few consecutive frames from
    // the first one in which the camera turns by more than a degree, it writes the
    // image the upscaler or Ray Reconstruction takes and the image it gives. It also writes the
    // raytracing's signals traced in that frame (RtCamera: the sun's visibility with the kept view
    // depth in w, the diffuse light, the reflections, the light added without Deferred).
    // It also writes EVE's clouds where it draws them.
    // Written into ReDefinitionCaptures\<time>-stages beside the other dumps; meta.txt
    // describes them.
    //
    // The frames dumped are the ones the recording is about, so they cost no more than a
    // copy on the GPU each. The textures the copies go into are made while the dump waits
    // for the turn, and read back one a frame after the last copy, through one buffer
    // kept for all. A thread of its own writes that buffer out before the next is read. Read
    // back and copied into a new array each, seven textures a frame (some hundred
    // megabytes) went with frames slowing from 17 to 138 ms (input dump of 2026-10-01
    // 22:03).
    internal static class StageDump
    {
        // How many consecutive frames are dumped.
        private const int Frames = 4;
        // How far the camera must turn from one frame to the next, in degrees, for the dump to begin.
        private const float TurnDegrees = 1f;
        // Frames waited for a turn before the dump begins without one.
        private const int WaitFrames = 600;

        // A copy waiting to be read back, and the file it goes into.
        private sealed class Copy
        {
            // The .bin file's path, then the GPU copy, released once read back.
            internal string Path;
            internal RenderTexture Texture;
        }

        // The dump's folder while it is armed or taking frames; null otherwise.
        private static string folder;
        // Frames taken so far, and frames waited for the turn.
        private static int taken;
        private static int waited;
        // The camera's direction in the frame before, once there is one.
        private static bool haveForward;
        private static Vector3 lastForward;
        // meta.txt as it is being written, one line per frame taken.
        private static readonly StringBuilder meta = new StringBuilder();

        // Per frame to take and stage, the texture its copy goes into; made while waiting.
        private static readonly Dictionary<string, RenderTexture>[] prepared =
        {
            new Dictionary<string, RenderTexture>(), new Dictionary<string, RenderTexture>(),
            new Dictionary<string, RenderTexture>(), new Dictionary<string, RenderTexture>(),
        };
        private static readonly Queue<Copy> toRead = new Queue<Copy>();
        private static byte[] buffer;
        // 1 while a read-back is in flight or its bytes are not yet written.
        private static int busy;
        private static string metaPath;
        private static string metaText;

        // EVE's volumetric clouds as its reconstruction left them this frame, where EVE
        // draws them: the layer the halo and trail around the rocket may come from. EVE
        // binds them only while the scene camera renders and puts a white one back after
        // (CloudMotionVectors), so while a dump is armed a buffer on the scene camera
        // copies them at AfterForwardAlpha into cloudsNow, which the dump takes from.
        private static readonly int CloudsId = Shader.PropertyToID("scattererReconstructedCloud");
        private static readonly RenderTargetIdentifier CloudsSource = new RenderTargetIdentifier(CloudsId);
        private static RenderTexture cloudsNow;
        private static readonly AttachedBuffer cloudsCopy = new AttachedBuffer("ReDefinition.StageDump clouds", CameraEvent.AfterForwardAlpha);

        // This frame's copy of EVE's clouds, or null while no copy is recorded.
        private static RenderTexture Clouds()
        {
            return cloudsCopy.Buffer != null ? cloudsNow : null;
        }

        // The copy at the scene camera's size, while the dump is armed and EVE has bound
        // clouds at all.
        private static void FollowClouds(Camera cam, RenderTexture input)
        {
            bool created;
            if (folder == null || cam == null || input == null || !EveCloudMotion.Present
                || Shader.GetGlobalTexture(CloudsId) == null
                || !RenderTextures.Ensure(ref cloudsNow, new Vector2Int(input.width, input.height), RenderTextureFormat.ARGBHalf,
                                          "ReDefinition_StageDump_CloudsNow", out created))
            {
                DropClouds();
                return;
            }
            if (cloudsCopy.IsOn(cam) && !created) return;
            cloudsCopy.AttachTo(cam).Blit(CloudsSource, cloudsNow);
        }

        // Takes the clouds' copy buffer off its camera and releases it.
        private static void DetachClouds()
        {
            cloudsCopy.Detach();
        }

        // The clouds' copy buffer and its texture, both gone.
        private static void DropClouds()
        {
            DetachClouds();
            RenderTextures.Release(ref cloudsNow);
        }

        // Starts a dump as a screen recording with dumps starts: a new folder, waiting for the camera to turn.
        internal static void Arm()
        {
            ReleasePrepared();
            DropClouds();
            folder = Path.Combine(CaptureFolder.Root,
                                  System.DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-stages");
            taken = 0;
            waited = 0;
            haveForward = false;
            meta.Length = 0;
            meta.Append("# frame unityFrame reconstructing jitterX jitterY worldToCamera(16) projection(16, without jitter),"
                        + " Unity's [row, column] row by row; then each texture: name width height format\n");
        }

        // Test switch -redefinitionStageDumpOnSignal (the upscaler-input test, 2026-11-21): a dump begins in the frame in which the global
        // _ReDefinition_StageDumpSignal takes a new value above 0 (the turn probe sets it at the frames it wants), without waiting for a turn,
        // into ReDefinitionCaptures\<time>-stages-s<value>; the dump also takes the motion vectors and the depth the upscaler is given, and
        // writes its meta.txt as soon as its last frame is taken. Without the switch nothing of this runs.
        private static readonly bool SignalMode = CommandLine.Has("-redefinitionStageDumpOnSignal");
        private static readonly int SignalId = Shader.PropertyToID("_ReDefinition_StageDumpSignal");
        private static float lastSignal;
        private static bool signalled;

        // The signal's dump armed in this frame where the global took a new value.
        private static void TakeSignal()
        {
            float signal = Shader.GetGlobalFloat(SignalId);
            if (signal <= 0f || signal == lastSignal) return;
            lastSignal = signal;
            Arm();
            folder += "-s" + ((int)signal).ToString(CultureInfo.InvariantCulture);
            signalled = true;
            Debug.Log(Log.Tag + " Stage dump hooks: after-upscaling " + Api.Internal.SharedFrame.AfterUpscalingHooks.Count + " ["
                      + Api.Internal.SharedFrame.AfterUpscalingHooks.DescribeAll() + "], after-upscaling with depth "
                      + Api.Internal.SharedFrame.AfterUpscalingDepthHooks.Count + " ["
                      + Api.Internal.SharedFrame.AfterUpscalingDepthHooks.DescribeAll() + "], overlay "
                      + Api.Internal.SharedFrame.OverlayHooks.Count + " [" + Api.Internal.SharedFrame.OverlayHooks.DescribeAll()
                      + "], motion vectors " + Api.Internal.SharedFrame.MotionVectorHooks.Count + ", skip after-upscaling hooks "
                      + CommandLine.Has("-redefinitionSkipAfterUpscalingHooks") + ".");
            StartCameraLog(folder);
        }

        // Test switch -redefinitionStageDumpOnSignal only: every camera that renders from the signal's frame through the dump's last frame,
        // in render order, into <folder>\cameras.txt: frame, name, depth, target, its projection's [0,2] and [1,2] (where jitter sits)
        // jittered and without jitter, and whether its projection equals the scene camera's jittered or unjittered one. The presenter's
        // OnRenderImage is a line of its own, so the cameras after it are the lines below it.
        private static StringBuilder cameraLog;
        private static string cameraLogPath;
        private static int cameraLogUntil = -1;
        private static Camera sceneCamera;
        private static bool cameraHooked;

        private static void StartCameraLog(string into)
        {
            if (!cameraHooked)
            {
                Camera.onPreRender += LogCamera;
                cameraHooked = true;
            }
            FlushCameraLog();
            cameraLog = new StringBuilder("# frame name depth target proj02 proj12 nonJittered02 nonJittered12 cameraJittered"
                                          + " sameAsSceneJittered sameAsSceneUnjittered\n");
            cameraLogPath = Path.Combine(into, "cameras.txt");
            cameraLogUntil = Time.frameCount + Frames - 1;
        }

        private static void LogCamera(Camera cam)
        {
            if (cameraLog == null) return;
            if (Time.frameCount > cameraLogUntil)
            {
                FlushCameraLog();
                return;
            }
            Matrix4x4 p = cam.projectionMatrix, u = cam.nonJitteredProjectionMatrix;
            bool jittered = Mathf.Abs(p[0, 2] - u[0, 2]) > 1e-7f || Mathf.Abs(p[1, 2] - u[1, 2]) > 1e-7f;
            string sameJ = "-", sameU = "-";
            if (sceneCamera != null)
            {
                sameJ = Same(p, sceneCamera.projectionMatrix) ? "yes" : "no";
                sameU = Same(p, sceneCamera.nonJitteredProjectionMatrix) ? "yes" : "no";
            }
            RenderTexture target = cam.targetTexture;
            cameraLog.Append(Time.frameCount).Append(' ').Append(cam.name.Replace(' ', '_')).Append(' ')
                .Append(cam.depth.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(target == null ? "framebuffer" : target.name.Replace(' ', '_') + ":" + target.width + "x" + target.height).Append(' ')
                .Append(p[0, 2].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(p[1, 2].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(u[0, 2].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(u[1, 2].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(jittered ? "yes" : "no").Append(' ').Append(sameJ).Append(' ').Append(sameU).Append('\n');
        }

        // Whether two projections agree to a millionth in every value.
        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > 1e-6f * Mathf.Max(1f, Mathf.Abs(b[i]))) return false;
            return true;
        }

        // The presenter's line: its OnRenderImage runs now, with the scene camera's projection as it is now.
        private static void LogPresenter(Camera cam)
        {
            if (cameraLog == null || Time.frameCount > cameraLogUntil) return;
            sceneCamera = cam;
            Matrix4x4 p = cam.projectionMatrix;
            cameraLog.Append(Time.frameCount).Append(" PRESENTER_OnRenderImage scene=").Append(cam.name.Replace(' ', '_'))
                .Append(" sceneProj02 ").Append(p[0, 2].ToString("R", CultureInfo.InvariantCulture))
                .Append(" sceneProj12 ").Append(p[1, 2].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        private static void FlushCameraLog()
        {
            if (cameraLog == null) return;
            byte[] text = Encoding.ASCII.GetBytes(cameraLog.ToString());
            string path = cameraLogPath;
            cameraLog = null;
            ThreadPool.QueueUserWorkItem(_ => TryWrite(path, text, text.Length));
        }

        // After the upscaler's dispatch: input the image it took, output the image it gave; motion and depth the motion vectors and depth it
        // was given (taken only with -redefinitionStageDumpOnSignal).
        internal static void AfterDispatch(Camera cam, Matrix4x4 unjittered, Vector2 jitter, bool reconstructing,
                                           RenderTexture input, RenderTexture output, RenderTexture motion = null, RenderTexture depth = null)
        {
            ReadNext();
            if (SignalMode)
            {
                if (sceneCamera == null) sceneCamera = cam;
                TakeSignal();
                LogPresenter(cam);
            }
            FollowClouds(cam, input);
            if (folder == null || cam == null) return;
            RtCamera raytraced = RtCamera.Current;
            if (raytraced != null && raytraced.Camera != cam) raytraced = null;
            Vector3 forward = cam.transform.forward;
            if (taken == 0)
            {
                bool turning = signalled || (haveForward && Vector3.Angle(forward, lastForward) > TurnDegrees);
                lastForward = forward;
                haveForward = true;
                if (!turning && ++waited < WaitFrames)
                {
                    Prepare(input, output, raytraced);
                    return;
                }
                Directory.CreateDirectory(folder);
                Debug.Log(Log.Tag + " Stage dump: " + (turning ? "the camera turns" : "no turn within " + WaitFrames
                                                                                  + " frames") + ", " + Frames
                          + " frames into " + folder + ".");
            }

            int n = taken++;
            if (signalled)
            {
                // The later stages of this frame (AfterStage) go into the same folder under the same frame number.
                stageFolder = folder;
                stageN = n;
                stageFrame = Time.frameCount;
            }
            meta.Append(n).Append(' ').Append(Time.frameCount).Append(' ').Append(reconstructing ? 1 : 0).Append(' ')
                .Append(jitter.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(jitter.y.ToString("R", CultureInfo.InvariantCulture));
            Append(cam.worldToCameraMatrix);
            Append(unjittered);
            Take(n, "input", input);
            Take(n, "output", output);
            Take(n, "clouds", Clouds());
            if (signalled)
            {
                Take(n, "motion", motion);
                Take(n, "depthcopy", depth);
            }
            if (raytraced != null)
            {
                Take(n, "sun", raytraced.SunTexture);
                Take(n, "diffuse", raytraced.DiffuseTexture);
                Take(n, "specular", raytraced.SpecularTexture);
                Take(n, "forward", raytraced.ForwardTexture);
                Take(n, "depth", raytraced.DepthTexture);
            }
            meta.Append('\n');
            if (taken < Frames) return;

            if (signalled)
            {
                // A signal's dump may follow another before the copies are read: its meta.txt goes out now.
                string signalMeta = Path.Combine(folder, "meta.txt");
                byte[] signalText = Encoding.ASCII.GetBytes(meta.ToString());
                ThreadPool.QueueUserWorkItem(_ => TryWrite(signalMeta, signalText, signalText.Length));
                signalled = false;
                folder = null;
                ReleasePrepared();
                DropClouds();
                return;
            }
            // Written after every texture: the folder is complete once meta.txt is there.
            metaPath = Path.Combine(folder, "meta.txt");
            metaText = meta.ToString();
            folder = null;
            ReleasePrepared();
            DropClouds();
        }

        // While waiting: the copy textures for every frame to take and every stage there is now.
        private static void Prepare(RenderTexture input, RenderTexture output, RtCamera raytraced)
        {
            for (int n = 0; n < Frames; n++)
            {
                Prepare(n, "input", input);
                Prepare(n, "output", output);
                Prepare(n, "clouds", Clouds());
                if (raytraced == null) continue;
                Prepare(n, "sun", raytraced.SunTexture);
                Prepare(n, "diffuse", raytraced.DiffuseTexture);
                Prepare(n, "specular", raytraced.SpecularTexture);
                Prepare(n, "forward", raytraced.ForwardTexture);
                Prepare(n, "depth", raytraced.DepthTexture);
            }
        }

        // Whether a prepared copy is created and has the source's size and format.
        private static bool Fits(RenderTexture copy, RenderTexture source)
        {
            return copy != null && copy.IsCreated() && copy.width == source.width && copy.height == source.height
                   && copy.format == source.format;
        }

        // One stage's copy texture for frame n, made anew where the source's size or format changed, and the read-back
        // buffer grown to hold it.
        private static void Prepare(int n, string name, RenderTexture source)
        {
            if (source == null || !source.IsCreated()) return;
            RenderTexture copy;
            prepared[n].TryGetValue(name, out copy);
            if (Fits(copy, source)) return;
            RenderTextures.Release(ref copy);
            RenderTextureDescriptor desc = source.descriptor;
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;
            desc.enableRandomWrite = false;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;
            copy = new RenderTexture(desc) { name = "ReDefinition_StageDump_" + name };
            copy.Create();
            prepared[n][name] = copy;
            int bytes = source.width * source.height * 16;
            if (buffer == null || buffer.Length < bytes) buffer = new byte[bytes];
        }

        // Copies one stage of frame n on the GPU into its prepared texture, notes it in meta.txt and queues it to be read.
        // Test switch -redefinitionStageDumpOnSignal only: a later stage of the frame a signal's dump is taking (the image after the after-upscaling
        // hooks, the presenter's destination after TUFX or the Blit), copied into the same folder as <name>-<nn>.bin with <name>-<nn>.txt (width
        // height format). Nothing in any other frame or without the switch; a null texture (the frame buffer itself) is noted as such.
        private static string stageFolder;
        private static int stageN = -1, stageFrame = -1;

        internal static void AfterStage(string name, RenderTexture source)
        {
            if (!SignalMode || stageFolder == null || stageFrame != Time.frameCount) return;
            string stem = Path.Combine(stageFolder, name + "-" + stageN.ToString("00", CultureInfo.InvariantCulture));
            byte[] info = Encoding.ASCII.GetBytes(source == null ? "null (the frame buffer)\n"
                                                                 : source.width + " " + source.height + " " + source.format + "\n");
            ThreadPool.QueueUserWorkItem(_ => TryWrite(stem + ".txt", info, info.Length));
            if (source == null || !source.IsCreated()) return;
            RenderTexture copy;
            prepared[stageN].TryGetValue(name, out copy);
            if (!Fits(copy, source))
            {
                Prepare(stageN, name, source);
                prepared[stageN].TryGetValue(name, out copy);
            }
            prepared[stageN].Remove(name);
            if (!Fits(copy, source)) return;
            Graphics.CopyTexture(source, copy);
            toRead.Enqueue(new Copy { Path = stem + ".bin", Texture = copy });
        }

        private static void Take(int n, string name, RenderTexture source)
        {
            if (source == null || !source.IsCreated()) return;
            RenderTexture copy;
            prepared[n].TryGetValue(name, out copy);
            // Not prepared -- the camera turned in the first frame waited -- made now.
            if (!Fits(copy, source))
            {
                Prepare(n, name, source);
                prepared[n].TryGetValue(name, out copy);
            }
            prepared[n].Remove(name);
            if (!Fits(copy, source)) return;
            Graphics.CopyTexture(source, copy);
            meta.Append(' ').Append(name).Append(' ').Append(source.width).Append(' ').Append(source.height).Append(' ')
                .Append(source.format);
            toRead.Enqueue(new Copy
            {
                Path = Path.Combine(folder, name + "-" + n.ToString("00", CultureInfo.InvariantCulture) + ".bin"),
                Texture = copy,
            });
        }

        // One copy read back at a time, once the one before is written; meta.txt last.
        private static void ReadNext()
        {
            if (Volatile.Read(ref busy) != 0) return;
            if (toRead.Count == 0)
            {
                if (metaPath == null) return;
                string path = metaPath;
                byte[] text = Encoding.ASCII.GetBytes(metaText);
                metaPath = null;
                metaText = null;
                ThreadPool.QueueUserWorkItem(_ => TryWrite(path, text, text.Length));
                return;
            }
            Copy next = toRead.Dequeue();
            Volatile.Write(ref busy, 1);
            AsyncGPUReadback.Request(next.Texture, 0, request =>
            {
                RenderTexture texture = next.Texture;
                RenderTextures.Release(ref texture);
                if (request.hasError)
                {
                    Volatile.Write(ref busy, 0);
                    return;
                }
                NativeArray<byte> data = request.GetData<byte>();
                int length = data.Length;
                if (buffer == null || buffer.Length < length) buffer = new byte[length];
                byte[] bytes = buffer;
                NativeArray<byte>.Copy(data, bytes, length);
                string path = next.Path;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    TryWrite(path, bytes, length);
                    Volatile.Write(ref busy, 0);
                });
            });
        }

        // Writes the first length bytes into a file, on the writing thread; a failure only leaves the file missing.
        private static void TryWrite(string path, byte[] bytes, int length)
        {
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write))
                    file.Write(bytes, 0, length);
            }
            catch (System.Exception)
            {
                // A diagnostic: a file not written is a file missing from the folder.
            }
        }

        // A matrix's sixteen values into meta.txt, row by row, round-trip exact.
        private static void Append(Matrix4x4 m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    meta.Append(' ').Append(m[r, c].ToString("R", CultureInfo.InvariantCulture));
        }

        // Releases the copy textures that were prepared and not taken.
        private static void ReleasePrepared()
        {
            foreach (Dictionary<string, RenderTexture> frame in prepared)
            {
                foreach (RenderTexture texture in frame.Values)
                {
                    RenderTexture copy = texture;
                    RenderTextures.Release(ref copy);
                }
                frame.Clear();
            }
        }
    }
}
