// ReDefinition: the constants of FSR3Unity's runtime (MIT, see LICENSE.txt) on
// Unity 2019.4.
//
// Unity 2019.4 has no CommandBuffer.SetComputeConstantBufferParam (it came with
// 2020.1), so an explicitly registered cbuffer cannot be filled. Global uniforms
// set with SetComputeIntParams/SetComputeFloatParams do not fit either: those
// calls write raw into the constant buffer following HLSL's packing, which aligns
// elements to float4, so an int2 takes 16 bytes instead of 8, every later field
// is shifted, and FSR computes with an image of 0x0.
//
// A StructuredBuffer carries the struct as a whole: SetComputeBufferParam exists
// in 2019.4, and FSR3Unity's C# structs are laid out so that no member crosses a
// 16 byte boundary, so both sides see the same layout.

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FidelityFX.FSR3
{
    /// <summary>
    /// Binds a set of constants to a compute shader as a StructuredBuffer.
    /// </summary>
    internal interface IFsr3ConstantBinder : IDisposable
    {
        void Bind(CommandBuffer commandBuffer, ComputeShader shader, int kernelIndex);
    }

    /// <summary>
    /// Holds exactly one set of constants and uploads it as a StructuredBuffer.
    /// The value lives in an array of length 1, which FSR3Unity's code modifies
    /// by ref.
    /// </summary>
    internal sealed class Fsr3ConstantBinder<T> : IFsr3ConstantBinder where T : struct
    {
        private readonly T[] _values;
        private readonly int _nameId;
        private ComputeBuffer _buffer;

        /// <param name="values">Array of length 1 holding the current values.</param>
        /// <param name="bufferName">
        /// Name of the StructuredBuffer in the shader. Has to match what
        /// tools/port_fsr3_shaders.py generates: "KspCbBuf_" plus the cbuffer's
        /// name in FSR3Unity's shaders.
        /// </param>
        public Fsr3ConstantBinder(T[] values, string bufferName)
        {
            _values = values;
            _nameId = Shader.PropertyToID(bufferName);
            _buffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(T)), ComputeBufferType.Structured);
        }

        public void Bind(CommandBuffer commandBuffer, ComputeShader shader, int kernelIndex)
        {
            if (_buffer == null) return;

            // Uploaded at once, not through the CommandBuffer, whose
            // SetComputeBufferData Unity 2019.4 does not have. The values are
            // final when the buffer is recorded, and it runs in the same frame.
            _buffer.SetData(_values);
            commandBuffer.SetComputeBufferParam(shader, kernelIndex, _nameId, _buffer);
        }

        public void Dispose()
        {
            if (_buffer == null) return;
            _buffer.Release();
            _buffer = null;
        }
    }

    /// <summary>
    /// The names of the StructuredBuffers in the ported shaders, in one place: a
    /// wrong name shows only as a black image.
    /// </summary>
    internal static class Fsr3ConstantBuffers
    {
        public const string Upscaler = "KspCbBuf_cbFSR3Upscaler";
        public const string Spd = "KspCbBuf_cbSPD";
        public const string Rcas = "KspCbBuf_cbRCAS";
        public const string GenerateReactive = "KspCbBuf_cbGenerateReactive";
    }
}
