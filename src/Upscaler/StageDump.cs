using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // The Debug tab's screen recording also writes the frame's stages, for following a
    // fault through the pipeline away from the game: for a few consecutive frames from
    // the first one in which the camera turns by more than a degree, the image the
    // upscaler or Ray Reconstruction takes, the image it gives, and the raytracing's
    // signals traced in that frame (RtCamera: the sun's visibility with the kept view
    // depth in w, the diffuse light, the reflections, the light added without Deferred).
    // Read back without stalling the frame and written in a thread of their own, into
    // ReDefinitionCaptures\<time>-stages beside the other dumps; meta.txt describes them.
    internal static class StageDump
    {
        private const int Frames = 4;
        private const float TurnDegrees = 1f;
        private const int WaitFrames = 600;

        private static string folder;
        private static int taken;
        private static int waited;
        private static bool haveForward;
        private static Vector3 lastForward;
        private static readonly StringBuilder meta = new StringBuilder();
        private static int pending;

        internal static void Arm()
        {
            folder = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "ReDefinitionCaptures"),
                                  System.DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-stages");
            taken = 0;
            waited = 0;
            haveForward = false;
            meta.Length = 0;
            meta.Append("# frame unityFrame reconstructing jitterX jitterY worldToCamera(16) projection(16, without jitter),"
                        + " Unity's [row, column] row by row; then each texture: name width height format\n");
        }

        // After the upscaler's dispatch: input the image it took, output the image it gave.
        internal static void AfterDispatch(Camera cam, Matrix4x4 unjittered, Vector2 jitter, bool reconstructing,
                                           RenderTexture input, RenderTexture output)
        {
            if (folder == null || cam == null) return;
            Vector3 forward = cam.transform.forward;
            if (taken == 0)
            {
                bool turning = haveForward && Vector3.Angle(forward, lastForward) > TurnDegrees;
                lastForward = forward;
                haveForward = true;
                if (!turning && ++waited < WaitFrames) return;
                Directory.CreateDirectory(folder);
                Debug.Log(Log.Tag + " Stage dump: " + (turning ? "the camera turns" : "no turn within " + WaitFrames
                                                                                  + " frames") + ", " + Frames
                          + " frames into " + folder + ".");
            }

            int n = taken++;
            meta.Append(n).Append(' ').Append(Time.frameCount).Append(' ').Append(reconstructing ? 1 : 0).Append(' ')
                .Append(jitter.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(jitter.y.ToString("R", CultureInfo.InvariantCulture));
            Append(cam.worldToCameraMatrix);
            Append(unjittered);
            Read(n, "input", input);
            Read(n, "output", output);
            RtCamera raytraced = RtCamera.Current;
            if (raytraced != null && raytraced.Camera == cam)
            {
                Read(n, "sun", raytraced.SunTexture);
                Read(n, "diffuse", raytraced.DiffuseTexture);
                Read(n, "specular", raytraced.SpecularTexture);
                Read(n, "forward", raytraced.ForwardTexture);
                Read(n, "depth", raytraced.DepthTexture);
            }
            meta.Append('\n');
            if (taken < Frames) return;

            string done = folder;
            string text = meta.ToString();
            folder = null;
            Write(Path.Combine(done, "meta.txt"), Encoding.ASCII.GetBytes(text));
        }

        private static void Append(Matrix4x4 m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    meta.Append(' ').Append(m[r, c].ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Read(int n, string name, RenderTexture texture)
        {
            if (texture == null || !texture.IsCreated()) return;
            meta.Append(' ').Append(name).Append(' ').Append(texture.width).Append(' ').Append(texture.height).Append(' ')
                .Append(texture.format);
            string path = Path.Combine(folder, name + "-" + n.ToString("00", CultureInfo.InvariantCulture) + ".bin");
            Interlocked.Increment(ref pending);
            AsyncGPUReadback.Request(texture, 0, request =>
            {
                Interlocked.Decrement(ref pending);
                if (request.hasError) return;
                Write(path, request.GetData<byte>().ToArray());
            });
        }

        private static void Write(string path, byte[] bytes)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    File.WriteAllBytes(path, bytes);
                }
                catch (System.Exception)
                {
                    // A diagnostic: a file not written is a file missing from the folder.
                }
            });
        }
    }
}
