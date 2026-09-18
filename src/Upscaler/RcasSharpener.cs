using FidelityFX;
using FidelityFX.FSR3;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // FSR 3's RCAS pass on its own, after DLSS. DLSS sharpens nothing itself -- its
    // guide calls the sharpening of earlier versions deprecated and points
    // developers who want a sharper picture to a filter with a slider for the
    // player (programming guide 3.11) -- while FSR 3 and AMD's DLL run RCAS after
    // reconstruction. So the slider reaches DLSS too: the same shader, with the
    // constants FSR 3's own dispatch computes (Fsr3UpscalerContext.RcasConstantsFor).
    //
    // RCAS reads one texture and writes another. DLSS writes into Input, which this
    // owns, and RCAS from there into the rig's output -- no copy, and a frame DLSS
    // does not write leaves the last unsharpened one in Input, never a sharpened one
    // to be sharpened again. Input is made like the output, with random write, as
    // DLSS writes it.
    //
    // Not quite the same strength as FSR 3's own RCAS at the same slider value: that
    // one scales the colour by FSR's auto exposure before its limiter, which works
    // against a fixed peak of 1. Here the exposure is an empty 1x1 texture, which
    // the pass takes for 1 (ffx_fsr3upscaler_callbacks_hlsl.h, Exposure), so dark
    // scenes sharpen a little less and HDR highlights above 1 not at all.
    internal sealed class RcasSharpener
    {
        // RCAS works in 16x16 blocks (ffx_fsr3upscaler_rcas.h), as
        // Fsr3UpscalerContext dispatches it.
        private const int Block = 16;

        private readonly ComputeShader shader;
        private readonly int kernel;
        private readonly Fsr3Upscaler.RcasConstants[] rcas = new Fsr3Upscaler.RcasConstants[1];
        private readonly Fsr3Upscaler.UpscalerConstants[] upscaler = new Fsr3Upscaler.UpscalerConstants[1];
        private Fsr3ConstantBinder<Fsr3Upscaler.RcasConstants> rcasBinder;
        private Fsr3ConstantBinder<Fsr3Upscaler.UpscalerConstants> upscalerBinder;
        private Texture2D exposure;
        private readonly RenderTexture output;

        // What DLSS writes into; check IsCreated after construction.
        public RenderTexture Input { get; private set; }

        public RcasSharpener(ComputeShader sharpenPass, RenderTexture output)
        {
            this.output = output;
            shader = sharpenPass;
            kernel = shader.FindKernel("CS");
            rcasBinder = new Fsr3ConstantBinder<Fsr3Upscaler.RcasConstants>(rcas, Fsr3ConstantBuffers.Rcas);
            // The pass declares FSR 3's upscaler constants as well
            // (ffx_fsr3upscaler_rcas_pass.hlsl), and FSR 3's own dispatch binds them.
            // Whether the compiled pass still reads the buffer is not visible from
            // here, and an unbound one it reads stops the dispatch; one all-zero
            // struct a frame is cheap.
            upscalerBinder = new Fsr3ConstantBinder<Fsr3Upscaler.UpscalerConstants>(upscaler, Fsr3ConstantBuffers.Upscaler);

            exposure = new Texture2D(1, 1, TextureFormat.RGFloat, false, true) { name = "ReDefinition_RcasExposure" };
            exposure.SetPixel(0, 0, Color.clear);
            exposure.Apply(false, true);

            RenderTextureDescriptor descriptor = output.descriptor;
            descriptor.depthBufferBits = 0;
            descriptor.msaaSamples = 1;
            Input = new RenderTexture(descriptor) { name = "ReDefinition_DlssOutput" };
            Input.Create();
        }

        public void Schedule(CommandBuffer buffer, float sharpness)
        {
            rcas[0] = Fsr3UpscalerContext.RcasConstantsFor(sharpness);
            buffer.SetComputeTextureParam(shader, kernel, Fsr3ShaderIDs.SrvInputExposure, exposure);
            buffer.SetComputeTextureParam(shader, kernel, Fsr3ShaderIDs.SrvRcasInput, Input);
            buffer.SetComputeTextureParam(shader, kernel, Fsr3ShaderIDs.UavUpscaledOutput, output);
            upscalerBinder.Bind(buffer, shader, kernel);
            rcasBinder.Bind(buffer, shader, kernel);
            buffer.DispatchCompute(shader, kernel, (output.width + Block - 1) / Block, (output.height + Block - 1) / Block, 1);
        }

        public void Release()
        {
            if (rcasBinder != null)
            {
                rcasBinder.Dispose();
                rcasBinder = null;
            }
            if (upscalerBinder != null)
            {
                upscalerBinder.Dispose();
                upscalerBinder = null;
            }
            if (Input != null)
            {
                Input.Release();
                Object.Destroy(Input);
                Input = null;
            }
            if (exposure != null)
            {
                Object.Destroy(exposure);
                exposure = null;
            }
        }
    }
}
