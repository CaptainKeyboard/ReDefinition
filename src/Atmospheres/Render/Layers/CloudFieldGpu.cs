using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // CLOUD FIELD: the field's texture (CloudField.Build) and its uniforms as shader globals, read by the layer pass and the cloud shadow pass alike. The texture is built once, on the first frame
    // the field is on, on the main thread (two 1024 x 1024 FFTs, two sorts of a million values and a blur: estimated 2 to 4 s, not measured; the game run's log gives the number).
    internal static class CloudFieldGpu
    {
        private static readonly int IdField = Shader.PropertyToID("_CloudField"), IdP = Shader.PropertyToID("_FieldP"), IdC = Shader.PropertyToID("_FieldC"),
            IdV = Shader.PropertyToID("_FieldV");
        private static Texture2D texture;
        private static float[] data;
        // The field's spread inside a texel of each mip level (CloudField.MipSpread; the shader's _FieldV).
        private static float[] spread;
        private static readonly Vector4[] classes = new Vector4[CloudField.ClassCount];

        // The channels of the field on the CPU (the twins' lookup); null until built.
        internal static float[] Data { get { return data; } }

        // Writes the globals for this frame; builds the texture when there is none.
        internal static void Apply()
        {
            if (texture == null)
            {
                data = CloudField.Build();
                // With mip levels: the shaders read the field at the march's spacing (FieldCloud), so a far sample takes the field's mean over its spacing instead of one texel.
                texture = new Texture2D(CloudField.Size, CloudField.Size, TextureFormat.RGBAFloat, true, true)
                {
                    name = "cloud field", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat
                };
                texture.SetPixelData(data, 0);
                texture.Apply(true, true);
                spread = CloudField.MipSpread(data, CloudField.Size, 0);
            }
            double a = CloudLookTuning.FieldWindDeg * System.Math.PI / 180.0;
            // x on, yz the street direction, w the smallest feature the field draws in m (a pixel footprint between an eighth and a quarter of it hands over to the field's
            // filtered share: FieldCloud's far view).
            Shader.SetGlobalVector(IdP, new Vector4(texture != null ? 1f : 0f, (float)System.Math.Cos(a), (float)System.Math.Sin(a), 1500f));
            if (texture != null)
            {
                Shader.SetGlobalTexture(IdField, texture);
                for (int i = 0; i < classes.Length; i++)
                {
                    float[] c = CloudField.Classes[i];
                    classes[i] = new Vector4(c[0], c[1], c[2], c[3]);
                }
                Shader.SetGlobalVectorArray(IdC, classes);
                if (spread != null) Shader.SetGlobalFloatArray(IdV, spread);
            }
        }
    }
}
