using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition
{
    // Direct3D 12 for other mods (ReDefinition.Api.D3D12): the dxgi.dll proxy's
    // D3d12Compute, reached like the upscalers there (NativeUpscalerLink) -- packets for
    // render events on Unity's render thread, the status read from the main thread.
    // Dispatches go through a ring, within a cap per frame; creates, destroys and
    // releases, of which a mod may send any number in one frame, each in memory of its
    // own, freed once the render thread is past it. docs/development/shared-foundation.md,
    // "Stage 2".
    internal static class D3d12Bridge
    {
        private const string Proxy = "dxgi.dll";

        internal const int CreateEvent = 20;
        internal const int DestroyEvent = 21;
        internal const int DispatchEvent = 22;
        internal const int ReleaseEvent = 23;

        internal const int MaxTextures = 8;
        internal const int MaxConstants = 256;

        // Dispatches a frame; the ring holds eight frames of them, more than the main
        // thread runs ahead of the render thread (PacketRing).
        internal const int MaxDispatchesPerFrame = 32;

        // Frames after which the render thread has read a packet.
        private const int FramesBehind = 16;

        private const uint CreateMagic = 0x4B534443u;     // 'KSDC'
        private const uint DispatchMagic = 0x4B534444u;   // 'KSDD'
        private const uint HandleMagic = 0x4B534448u;     // 'KSDH'
        private const uint FlagNextFrame = 1u << 0;

        // As D3d12Compute.h lays them out.
        [StructLayout(LayoutKind.Sequential)]
        internal struct CreatePacket
        {
            public uint Size;
            public uint Magic;
            public int Pass;
            public uint BytecodeSize;
            public IntPtr Bytecode;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DispatchPacket
        {
            public uint Size;
            public uint Magic;
            public int Pass;
            public uint Flags;
            public uint GroupsX;
            public uint GroupsY;
            public uint GroupsZ;
            public uint ReadCount;
            public uint WriteCount;
            public uint ConstantsSize;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxTextures)] public IntPtr[] Read;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxTextures)] public IntPtr[] Write;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxConstants)] public byte[] Constants;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HandlePacket
        {
            public uint Size;
            public uint Magic;
            public int Pass;
            public uint Reserved;
            public IntPtr Texture;
        }

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr KspFgGetRenderEventFunc();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspD3d12Capabilities(out int featureLevel, out int shaderModel, out int raytracingTier,
                                                       out int meshShaderTier, out int variableShadingRateTier);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint KspD3d12CreatePacketSize();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint KspD3d12DispatchPacketSize();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int KspD3d12PassStatus(int pass, StringBuilder buffer, int size);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int KspD3d12Compile(byte[] source, int sourceLength,
                                                  [MarshalAs(UnmanagedType.LPWStr)] string path, string entry,
                                                  byte[] bytecode, int capacity, out int written,
                                                  StringBuilder messages, int messagesSize);

        // A compute shader's DXBC is rarely larger; a larger one is compiled again at its size.
        private const int BytecodeCapacity = 256 * 1024;
        private const int MessagesCapacity = 16 * 1024;

        private static bool probed;
        private static string problem;
        private static IntPtr renderEvent = IntPtr.Zero;
        private static PacketRing dispatchRing;

        private static int nextPass = 1;
        private static readonly Dictionary<int, IntPtr> bytecodes = new Dictionary<int, IntPtr>();
        // Memory the render thread may still read -- bytecode of destroyed passes, the
        // packets of creates, destroys and releases --, with the frame it was last
        // handed over in, freed once the render thread is past it.
        private static readonly List<KeyValuePair<int, IntPtr>> retired = new List<KeyValuePair<int, IntPtr>>();

        private static int countedFrame = -1;
        private static int dispatchesThisFrame;

        // The packet's arrays, reused: dispatches come from the main thread only.
        private static readonly IntPtr[] readTextures = new IntPtr[MaxTextures];
        private static readonly IntPtr[] writeTextures = new IntPtr[MaxTextures];
        private static readonly byte[] constantBytes = new byte[MaxConstants];
        private static readonly StringBuilder statusBuffer = new StringBuilder(256);

        // Whether the proxy has Direct3D 12 for mods in this build's form; asked once.
        internal static bool Ready
        {
            get
            {
                if (probed) return problem == null;
                probed = true;
                try
                {
                    uint createSize = KspD3d12CreatePacketSize();
                    uint dispatchSize = KspD3d12DispatchPacketSize();
                    renderEvent = KspFgGetRenderEventFunc();
                    if (createSize != (uint)Marshal.SizeOf(typeof(CreatePacket))
                        || dispatchSize != (uint)Marshal.SizeOf(typeof(DispatchPacket)) || renderEvent == IntPtr.Zero)
                    {
                        problem = "The dxgi.dll proxy and ReDefinition disagree on the Direct3D 12 packets -- install both"
                                  + " from the same release.";
                        return false;
                    }
                }
                // Without the proxy the name resolves to the system's dxgi.dll, which
                // has none of these exports.
                catch (DllNotFoundException)
                {
                    problem = NotInstalled;
                    return false;
                }
                catch (EntryPointNotFoundException)
                {
                    problem = NotInstalled;
                    return false;
                }
                catch (Exception e)
                {
                    problem = "The dxgi.dll proxy could not be asked for Direct3D 12 (" + e.GetType().Name + ").";
                    return false;
                }

                dispatchRing = new PacketRing(Marshal.SizeOf(typeof(DispatchPacket)), 8 * MaxDispatchesPerFrame);
                problem = null;
                return true;
            }
        }

        private const string NotInstalled =
            "Direct3D 12 needs ReDefinition's dxgi.dll proxy next to KSP_x64.exe. The dxgi.dll the game loaded has none:"
            + " the proxy is not installed, or it is older than ReDefinition.";

        // Why Direct3D 12 is not available, or null.
        internal static string Problem
        {
            get
            {
                if (!Ready) return problem;
                Capabilities capabilities;
                return Query(out capabilities)
                    ? null
                    : "The dxgi.dll proxy does not present through Direct3D 12: it is switched off (enabled=0) or only"
                      + " measures (measureOnly=1) in ReDefinitionProxy.ini, or its swapchain could not be made"
                      + " (ReDefinitionProxy.log).";
            }
        }

        internal struct Capabilities
        {
            public int FeatureLevel;
            public int ShaderModel;
            public int RaytracingTier;
            public int MeshShaderTier;
            public int VariableShadingRateTier;
        }

        internal static bool Query(out Capabilities capabilities)
        {
            capabilities = new Capabilities();
            if (!Ready) return false;
            try
            {
                return KspD3d12Capabilities(out capabilities.FeatureLevel, out capabilities.ShaderModel,
                                            out capabilities.RaytracingTier, out capabilities.MeshShaderTier,
                                            out capabilities.VariableShadingRateTier) == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal const int Compiled = 1;
        internal const int CompilerErrors = 0;
        internal const int NotCompiled = -1;

        // HLSL to DXBC in the proxy (CompileComputeShader in D3d12Compute.h): from source, or
        // from the file at path when source is null. Compiled with the bytecode, and messages
        // the compiler's warnings; CompilerErrors with its errors in messages; NotCompiled when
        // no compiler was reached -- no proxy, no d3dcompiler_47.dll --, with the reason.
        internal static int Compile(string source, string path, string entry, out byte[] bytecode, out string messages)
        {
            bytecode = null;
            messages = null;
            if (!Ready)
            {
                messages = problem;
                return NotCompiled;
            }
            byte[] sourceBytes = source != null ? Encoding.UTF8.GetBytes(source) : null;
            StringBuilder output = new StringBuilder(MessagesCapacity);
            byte[] buffer = new byte[BytecodeCapacity];
            int written;
            int result;
            try
            {
                result = KspD3d12Compile(sourceBytes, sourceBytes != null ? sourceBytes.Length : 0, path, entry, buffer,
                                         buffer.Length, out written, output, output.Capacity);
                if (result == -1)
                {
                    buffer = new byte[written];
                    output.Length = 0;
                    result = KspD3d12Compile(sourceBytes, sourceBytes != null ? sourceBytes.Length : 0, path, entry,
                                             buffer, buffer.Length, out written, output, output.Capacity);
                }
            }
            catch (EntryPointNotFoundException)
            {
                messages = NotInstalled;
                return NotCompiled;
            }
            messages = output.Length > 0 ? output.ToString() : null;
            if (result == -2) return NotCompiled;
            if (result != 1) return CompilerErrors;
            bytecode = new byte[written];
            Buffer.BlockCopy(buffer, 0, bytecode, 0, written);
            return Compiled;
        }

        // A handle above 0, or 0 when the pass cannot be sent. The pipeline is built
        // on the render thread, or at the first dispatch once the proxy has its device.
        internal static int CreateComputePass(string name, byte[] bytecode, out string refused)
        {
            refused = Problem;
            if (!Ready) return 0;
            if (bytecode == null || bytecode.Length == 0)
            {
                refused = "no shader bytecode";
                return 0;
            }
            FreeRetired();

            int pass = nextPass++;
            IntPtr memory = Marshal.AllocHGlobal(bytecode.Length);
            Marshal.Copy(bytecode, 0, memory, bytecode.Length);
            bytecodes[pass] = memory;

            byte[] nameBytes = new byte[64];
            if (!string.IsNullOrEmpty(name))
                Encoding.ASCII.GetBytes(name, 0, Math.Min(name.Length, 63), nameBytes, 0);
            CreatePacket packet = new CreatePacket
            {
                Size = (uint)Marshal.SizeOf(typeof(CreatePacket)),
                Magic = CreateMagic,
                Pass = pass,
                BytecodeSize = (uint)bytecode.Length,
                Bytecode = memory,
                Name = nameBytes,
            };
            ExecuteOwned(CreateEvent, ref packet);
            refused = null;
            return pass;
        }

        // 1 built, 0 not yet -- the render thread has not come to it, or the proxy has
        // no device yet --, -1 failed or unknown.
        internal static int PassState(int pass, out string status)
        {
            if (!Ready)
            {
                status = problem;
                return -1;
            }
            try
            {
                statusBuffer.Length = 0;
                int state = KspD3d12PassStatus(pass, statusBuffer, statusBuffer.Capacity);
                status = statusBuffer.ToString();
                // Sent, but not yet read on the render thread.
                if (state == -1 && bytecodes.ContainsKey(pass) && status == "no pass of that handle")
                {
                    status = "not yet built";
                    return 0;
                }
                return state;
            }
            catch (Exception e)
            {
                status = "the proxy's status could not be read (" + e.GetType().Name + ")";
                return -1;
            }
        }

        internal static bool Dispatch(CommandBuffer buffer, int pass, RenderTexture[] read, RenderTexture[] write,
                                      byte[] constants, int groupsX, int groupsY, int groupsZ, bool nextFrame,
                                      out string refused)
        {
            refused = Problem;
            if (refused != null) return false;
            if (!bytecodes.ContainsKey(pass))
            {
                refused = "no compute pass of handle " + pass;
                return false;
            }
            int readCount = read != null ? read.Length : 0;
            int writeCount = write != null ? write.Length : 0;
            if (readCount > MaxTextures || writeCount > MaxTextures || (constants != null && constants.Length > MaxConstants)
                || groupsX < 1 || groupsY < 1 || groupsZ < 1)
            {
                refused = "at most 8 read and 8 write textures, 256 bytes of constants, and at least one thread group in"
                          + " each dimension";
                return false;
            }
            if (countedFrame != Time.frameCount)
            {
                countedFrame = Time.frameCount;
                dispatchesThisFrame = 0;
            }
            if (dispatchesThisFrame >= MaxDispatchesPerFrame)
            {
                refused = "more than " + MaxDispatchesPerFrame + " dispatches in one frame";
                return false;
            }

            Array.Clear(readTextures, 0, MaxTextures);
            Array.Clear(writeTextures, 0, MaxTextures);
            for (int i = 0; i < readCount; i++)
                if (!Native(read[i], "read texture t" + i, ref readTextures[i], out refused)) return false;
            for (int i = 0; i < writeCount; i++)
                if (!Native(write[i], "write texture u" + i, ref writeTextures[i], out refused)) return false;

            Array.Clear(constantBytes, 0, MaxConstants);
            int constantsSize = constants != null ? constants.Length : 0;
            if (constantsSize > 0) Buffer.BlockCopy(constants, 0, constantBytes, 0, constantsSize);

            DispatchPacket packet = new DispatchPacket
            {
                Size = (uint)Marshal.SizeOf(typeof(DispatchPacket)),
                Magic = DispatchMagic,
                Pass = pass,
                Flags = nextFrame ? FlagNextFrame : 0u,
                GroupsX = (uint)groupsX,
                GroupsY = (uint)groupsY,
                GroupsZ = (uint)groupsZ,
                ReadCount = (uint)readCount,
                WriteCount = (uint)writeCount,
                ConstantsSize = (uint)constantsSize,
                Read = readTextures,
                Write = writeTextures,
                Constants = constantBytes,
            };
            dispatchesThisFrame++;
            if (buffer != null) return dispatchRing.Issue(buffer, renderEvent, DispatchEvent, ref packet);
            CommandBuffer own = new CommandBuffer { name = "ReDefinition.D3D12 dispatch" };
            bool issued = dispatchRing.Issue(own, renderEvent, DispatchEvent, ref packet);
            if (issued) Graphics.ExecuteCommandBuffer(own);
            own.Release();
            return issued;
        }

        internal static void DestroyComputePass(int pass)
        {
            IntPtr memory;
            if (!Ready || !bytecodes.TryGetValue(pass, out memory)) return;
            bytecodes.Remove(pass);
            HandlePacket packet = new HandlePacket
            {
                Size = (uint)Marshal.SizeOf(typeof(HandlePacket)),
                Magic = HandleMagic,
                Pass = pass,
            };
            ExecuteOwned(DestroyEvent, ref packet);
            retired.Add(new KeyValuePair<int, IntPtr>(Time.frameCount, memory));
        }

        internal static void ReleaseTexture(RenderTexture texture)
        {
            if (!Ready || texture == null || !texture.IsCreated()) return;
            HandlePacket packet = new HandlePacket
            {
                Size = (uint)Marshal.SizeOf(typeof(HandlePacket)),
                Magic = HandleMagic,
                Texture = texture.GetNativeTexturePtr(),
            };
            ExecuteOwned(ReleaseEvent, ref packet);
        }

        private static bool Native(RenderTexture texture, string what, ref IntPtr native, out string refused)
        {
            if (texture == null || !texture.IsCreated())
            {
                refused = what + " is not a created RenderTexture";
                return false;
            }
            native = texture.GetNativeTexturePtr();
            refused = native != IntPtr.Zero ? null : what + " has no native texture";
            return refused == null;
        }

        // A packet in memory of its own, its event executed at once.
        private static void ExecuteOwned<T>(int eventId, ref T packet) where T : struct
        {
            FreeRetired();
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(T)));
            Marshal.StructureToPtr(packet, memory, false);
            CommandBuffer buffer = new CommandBuffer { name = "ReDefinition.D3D12 event " + eventId };
            buffer.IssuePluginEventAndData(renderEvent, eventId, memory);
            Graphics.ExecuteCommandBuffer(buffer);
            buffer.Release();
            retired.Add(new KeyValuePair<int, IntPtr>(Time.frameCount, memory));
        }

        private static void FreeRetired()
        {
            for (int i = retired.Count - 1; i >= 0; i--)
            {
                if (Time.frameCount - retired[i].Key < FramesBehind) continue;
                Marshal.FreeHGlobal(retired[i].Value);
                retired.RemoveAt(i);
            }
        }
    }
}
