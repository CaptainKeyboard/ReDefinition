using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Api
{
    /// <summary>
    /// The Direct3D 12 device and queue of ReDefinition's <c>dxgi.dll</c>, for compute passes on
    /// Unity's textures. Unity keeps rendering in Direct3D 11: a dispatch copies its textures into
    /// textures both devices share, runs the pass on the Direct3D 12 queue, and copies the write
    /// textures back. Reference: docs/modders/shared-foundation.md, "Direct3D 12".
    /// </summary>
    public static class D3D12
    {
        /// <summary>
        /// Whether ReDefinition's <c>dxgi.dll</c> presents through Direct3D 12, so that passes run.
        /// </summary>
        public static bool Available
        {
            get { return D3d12Bridge.Problem == null; }
        }

        /// <summary>
        /// Why Direct3D 12 is not <see cref="Available"/>, or null when it is.
        /// </summary>
        public static string Problem
        {
            get { return D3d12Bridge.Problem; }
        }

        /// <summary>
        /// Why the last <see cref="CreateComputePass"/>, <see cref="CreateComputePassFromSource"/>,
        /// <see cref="CreateComputePassFromFile"/>, <see cref="Dispatch"/> or <see cref="DispatchInto"/>
        /// returned 0 or false; null after one that went through.
        /// </summary>
        public static string LastRefusal { get; private set; }

        /// <summary>
        /// What the compiler said at the last <see cref="CreateComputePassFromSource"/> or
        /// <see cref="CreateComputePassFromFile"/>: its errors, or its warnings after a success; null
        /// when it said nothing.
        /// </summary>
        public static string LastCompilerMessages { get; private set; }

        /// <summary>
        /// The device's highest <c>D3D_FEATURE_LEVEL</c>, such as <c>0xC100</c> for 12.1; 0 while not
        /// <see cref="Available"/>.
        /// </summary>
        public static int FeatureLevel
        {
            get { return Capabilities().FeatureLevel; }
        }

        /// <summary>
        /// The device's highest <c>D3D_SHADER_MODEL</c>, such as <c>0x66</c> for 6.6; 0 while not
        /// <see cref="Available"/>.
        /// </summary>
        public static int ShaderModel
        {
            get { return Capabilities().ShaderModel; }
        }

        /// <summary>
        /// The device's <c>D3D12_RAYTRACING_TIER</c>: 0 none, 10 for 1.0, 11 for 1.1.
        /// </summary>
        public static int RaytracingTier
        {
            get { return Capabilities().RaytracingTier; }
        }

        /// <summary>
        /// The device's <c>D3D12_MESH_SHADER_TIER</c>: 0 none, 10 for 1.0.
        /// </summary>
        public static int MeshShaderTier
        {
            get { return Capabilities().MeshShaderTier; }
        }

        /// <summary>
        /// The device's <c>D3D12_VARIABLE_SHADING_RATE_TIER</c>: 0 none, 1 or 2.
        /// </summary>
        public static int VariableShadingRateTier
        {
            get { return Capabilities().VariableShadingRateTier; }
        }

        /// <summary>
        /// A compute pass from shader bytecode -- DXIL signed by <c>dxc</c>, or DXBC -- against the
        /// root signature every pass shares: <c>Texture2D</c> t0-t7, <c>RWTexture2D</c> u0-u7, a
        /// <c>cbuffer</c> b0 of up to 256 bytes, samplers s0 (point, clamp) and s1 (linear, clamp).
        /// The pipeline is built on the render thread; <see cref="ComputePassState"/> says whether it
        /// was.
        /// </summary>
        /// <param name="name">A name for the log and the status.</param>
        /// <param name="bytecode">The compiled compute shader.</param>
        /// <returns>A handle above 0, or 0 when the pass cannot be sent (<see cref="LastRefusal"/>).</returns>
        public static int CreateComputePass(string name, byte[] bytecode)
        {
            string refused;
            int pass = D3d12Bridge.CreateComputePass(name, bytecode, out refused);
            LastRefusal = refused;
            return pass;
        }

        /// <summary>
        /// A compute pass from HLSL source, compiled with Windows' <c>d3dcompiler_47.dll</c> to DXBC
        /// (<c>cs_5_0</c>) against the root signature of <see cref="CreateComputePass"/>.
        /// </summary>
        /// <param name="name">A name for the log and the status.</param>
        /// <param name="hlsl">The shader's source.</param>
        /// <param name="entryPoint">The kernel's function; <c>main</c> when null.</param>
        /// <returns>A handle above 0, or 0 when it does not compile (<see cref="LastCompilerMessages"/>) or
        /// cannot be sent (<see cref="LastRefusal"/>).</returns>
        public static int CreateComputePassFromSource(string name, string hlsl, string entryPoint)
        {
            if (string.IsNullOrEmpty(hlsl))
            {
                LastCompilerMessages = null;
                LastRefusal = "no source given";
                return 0;
            }
            return FromCompiled(name, hlsl, null, entryPoint);
        }

        /// <summary>
        /// A compute pass from an HLSL file, compiled like <see cref="CreateComputePassFromSource"/>;
        /// its <c>#include</c> lines are resolved relative to the file.
        /// </summary>
        /// <param name="name">A name for the log and the status.</param>
        /// <param name="path">The file, absolute or relative to the KSP folder.</param>
        /// <param name="entryPoint">The kernel's function; <c>main</c> when null.</param>
        /// <returns>A handle above 0, or 0 when it does not compile (<see cref="LastCompilerMessages"/>) or
        /// cannot be sent (<see cref="LastRefusal"/>).</returns>
        public static int CreateComputePassFromFile(string name, string path, string entryPoint)
        {
            if (string.IsNullOrEmpty(path))
            {
                LastCompilerMessages = null;
                LastRefusal = "no file given";
                return 0;
            }
            string fullPath;
            try
            {
                fullPath = System.IO.Path.GetFullPath(path);
            }
            catch (System.Exception e)
            {
                LastCompilerMessages = null;
                LastRefusal = "'" + path + "' is not a usable path (" + e.Message + ")";
                return 0;
            }
            if (!System.IO.File.Exists(fullPath))
            {
                LastCompilerMessages = null;
                LastRefusal = "no file at '" + fullPath + "'";
                return 0;
            }
            return FromCompiled(name, null, fullPath, entryPoint);
        }

        private static int FromCompiled(string name, string source, string path, string entryPoint)
        {
            byte[] bytecode;
            string messages;
            int result = D3d12Bridge.Compile(source, path, entryPoint, out bytecode, out messages);
            if (result == D3d12Bridge.NotCompiled)
            {
                LastCompilerMessages = null;
                LastRefusal = messages;
                return 0;
            }
            LastCompilerMessages = messages;
            if (result == D3d12Bridge.CompilerErrors)
            {
                LastRefusal = "'" + name + "' did not compile: " + messages;
                return 0;
            }
            return CreateComputePass(name, bytecode);
        }

        /// <summary>
        /// Whether a pass was built on the render thread.
        /// </summary>
        /// <param name="pass">A handle from one of the <c>CreateComputePass</c> methods.</param>
        /// <param name="status">What happened, in words.</param>
        /// <returns>1 built, 0 not yet, -1 failed.</returns>
        public static int ComputePassState(int pass, out string status)
        {
            return D3d12Bridge.PassState(pass, out status);
        }

        /// <summary>
        /// One dispatch of a pass, executed at once. Call it where the textures read hold this frame's
        /// image -- from <c>OnRenderImage</c>, or later: a render event issued from a camera's own
        /// command buffer reads the frame before (measured in Unity 2019.4 with KSP's graphics jobs).
        /// The write textures hold the result when it returns, or with <paramref name="nextFrame"/> at
        /// the pass's next dispatch, which lets the pass run beside the rest of the frame. At most 32
        /// dispatches a frame.
        /// </summary>
        /// <param name="pass">A handle from one of the <c>CreateComputePass</c> methods.</param>
        /// <param name="read">Up to 8 textures for t0-t7: created RenderTextures without mipmaps or
        /// multisampling.</param>
        /// <param name="write">Up to 8 textures for u0-u7, none also in <paramref name="read"/> and none
        /// twice.</param>
        /// <param name="constants">Up to 256 bytes for b0, or null.</param>
        /// <param name="groupsX">Thread groups in x, at least 1.</param>
        /// <param name="groupsY">Thread groups in y, at least 1.</param>
        /// <param name="groupsZ">Thread groups in z, at least 1.</param>
        /// <param name="nextFrame">The result at the pass's next dispatch instead of at once.</param>
        /// <returns>False when the dispatch is not sent (<see cref="LastRefusal"/>); what goes wrong on the
        /// render thread <see cref="ComputePassState"/> says.</returns>
        public static bool Dispatch(int pass, RenderTexture[] read, RenderTexture[] write, byte[] constants,
                                    int groupsX, int groupsY, int groupsZ, bool nextFrame)
        {
            string refused;
            bool sent = D3d12Bridge.Dispatch(null, pass, read, write, constants, groupsX, groupsY, groupsZ, nextFrame,
                                             out refused);
            LastRefusal = refused;
            return sent;
        }

        /// <summary>
        /// A dispatch like <see cref="Dispatch"/>, recorded into <paramref name="buffer"/>. Execute the
        /// buffer with <c>Graphics.ExecuteCommandBuffer</c> once, in the frame it was recorded in: the
        /// event it records points at a packet a later dispatch reuses.
        /// </summary>
        /// <param name="buffer">The buffer to record into.</param>
        /// <param name="pass">A handle from one of the <c>CreateComputePass</c> methods.</param>
        /// <param name="read">As for <see cref="Dispatch"/>.</param>
        /// <param name="write">As for <see cref="Dispatch"/>.</param>
        /// <param name="constants">As for <see cref="Dispatch"/>.</param>
        /// <param name="groupsX">Thread groups in x, at least 1.</param>
        /// <param name="groupsY">Thread groups in y, at least 1.</param>
        /// <param name="groupsZ">Thread groups in z, at least 1.</param>
        /// <param name="nextFrame">As for <see cref="Dispatch"/>.</param>
        /// <returns>False when the dispatch is not recorded (<see cref="LastRefusal"/>).</returns>
        public static bool DispatchInto(CommandBuffer buffer, int pass, RenderTexture[] read, RenderTexture[] write,
                                        byte[] constants, int groupsX, int groupsY, int groupsZ, bool nextFrame)
        {
            string refused = "no command buffer";
            bool sent = buffer != null
                        && D3d12Bridge.Dispatch(buffer, pass, read, write, constants, groupsX, groupsY, groupsZ,
                                                nextFrame, out refused);
            LastRefusal = refused;
            return sent;
        }

        /// <summary>
        /// Destroys a pass; its handle means nothing afterwards.
        /// </summary>
        /// <param name="pass">A handle from one of the <c>CreateComputePass</c> methods.</param>
        public static void DestroyComputePass(int pass)
        {
            D3d12Bridge.DestroyComputePass(pass);
        }

        /// <summary>
        /// Frees the shared copy of a texture, before the mod releases the texture. Copies not used for
        /// ten seconds are freed anyway.
        /// </summary>
        /// <param name="texture">A texture a dispatch read or wrote.</param>
        public static void ReleaseTexture(RenderTexture texture)
        {
            D3d12Bridge.ReleaseTexture(texture);
        }

        private static D3d12Bridge.Capabilities Capabilities()
        {
            D3d12Bridge.Capabilities capabilities;
            D3d12Bridge.Query(out capabilities);
            return capabilities;
        }
    }
}
