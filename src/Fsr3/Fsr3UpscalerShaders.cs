// Copyright (c) 2024 Nico de Poel
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using UnityEngine;

namespace FidelityFX.FSR3
{
    /// <summary>
    /// All the compute shaders used by the FSR3 Upscaler.
    /// </summary>
    [System.Serializable]
    public class Fsr3UpscalerShaders
    {
        /// <summary>
        /// The compute shader used by the prepare inputs pass.
        /// </summary>
        public ComputeShader prepareInputsPass;
        
        /// <summary>
        /// The compute shader used by the luminance pyramid computation pass.
        /// </summary>
        public ComputeShader lumaPyramidPass;

        /// <summary>
        /// The compute shader used by the shading change pyramid pass.
        /// </summary>
        public ComputeShader shadingChangePyramidPass;

        /// <summary>
        /// The compute shader used by the shading change pass.
        /// </summary>
        public ComputeShader shadingChangePass;

        /// <summary>
        /// The compute shader used by the prepare reactivity pass.
        /// </summary>
        public ComputeShader prepareReactivityPass;
        
        /// <summary>
        /// The compute shader used by the luma instability pass.
        /// </summary>
        public ComputeShader lumaInstabilityPass;

        /// <summary>
        /// The compute shader used by the accumulation pass.
        /// </summary>
        public ComputeShader accumulatePass;

        /// <summary>
        /// The compute shader used by the RCAS sharpening pass.
        /// </summary>
        public ComputeShader sharpenPass;

        /// <summary>
        /// The compute shader used to auto-generate a reactive mask.
        /// </summary>
        public ComputeShader autoGenReactivePass;

        /// <summary>
        /// The compute shader used to auto-generate a transparency &amp; composition mask.
        /// </summary>
        public ComputeShader tcrAutoGenPass;

        /// <summary>
        /// The compute shader used to display a debug view.
        /// </summary>
        public ComputeShader debugViewPass;
    }
}
