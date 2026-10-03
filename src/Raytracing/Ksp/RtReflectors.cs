using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The bodies the sun lights, as the raytracing's lights (docs/development/
    // raytracing.md, "Light sources"): the planet the camera is at -- the main body, or
    // the planet it orbits -- and that planet's moons; farther planets are left out.
    // Each one's light at the camera by its albedo, its phase, its size and its distance
    // (RtReflectorLight), in the colour of its surface and of the star; the two brightest
    // go to the proxy, each a disc of its angular size, which the rays sample with its
    // shadow -- the Mun lights only where it stands in the sky and nothing lies between.
    // The main body is one of them as the ground under an orbit, its own sphere not
    // shading it (RtCommon.hlsli, ReflectedLightAt) -- only where the rays that miss do
    // not see it already, in the reflection probe, and above the terrain the rays hit.
    //
    // A body's colour and albedo are its scaled-space texture's average, linear: read
    // once per body, rendered down to a few pixels, as its texture is not readable.
    internal static class RtReflectors
    {
        // Where a body has no texture to read.
        private static readonly Color Grey = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Dictionary<CelestialBody, Color> colours = new Dictionary<CelestialBody, Color>();
        private static readonly Dictionary<CelestialBody, float> retryAt = new Dictionary<CelestialBody, float>();

        private struct Candidate
        {
            internal Vector3 Towards;
            internal float Radius;
            internal Color Colour;
            internal float Reach;
            internal float Luminance;
        }

        private static readonly List<Candidate> candidates = new List<Candidate>();
        private static readonly List<CelestialBody> system = new List<CelestialBody>();

        // Fills lights as RtCamera.Lights asks, for a camera at `eye` (world), the
        // player's percentage, whether the main body may be one; the number of lights.
        internal static int Fill(float[] lights, Vector3d eye, CelestialBody main, float percent, bool mainLight)
        {
            lastCount = 0;
            if (percent <= 0f || main == null) return 0;
            CelestialBody star = RtKspMods.StarBody();
            if (star == null) return 0;
            // The star's colour as the bodies get it: the scene's light is tinted by the
            // atmosphere (Scatterer), and darkened at night (measured, KSP.log: an orange
            // moonlight, and none with the Mun high).
            Color tint = RtKspMods.StarColour().linear;
            float tintLuminance = Luminance(tint);
            tint = tintLuminance > 0f ? tint / tintLuminance : Color.white;

            candidates.Clear();
            Vector3d sun = star.position;
            double sunToEye = (sun - eye).magnitude;
            CelestialBody planet = main;
            while (planet != null && planet.referenceBody != null && planet.referenceBody != star && planet.referenceBody != planet)
                planet = planet.referenceBody;
            if (planet == null || planet == star) return 0;
            system.Clear();
            system.Add(planet);
            if (planet.orbitingBodies != null) system.AddRange(planet.orbitingBodies);
            foreach (CelestialBody body in system)
            {
                if (body == null || body == star || body.scaledBody == null) continue;
                Vector3d toBody = body.position - eye;
                double distance = toBody.magnitude;
                if (distance <= body.Radius) continue;
                // Below that the rays meet the ground under the camera as terrain.
                if (body == main && (!mainLight || distance - body.Radius < RtTerrainSource.Range)) continue;
                Vector3d bodyToSun = sun - body.position;
                double angle = Vector3d.Angle(bodyToSun, -toBody) * Mathf.Deg2Rad;
                Color colour = ColourOf(body);
                double share = RtReflectorLight.Share(Luminance(colour), angle, body.Radius, distance, sunToEye,
                                                      bodyToSun.magnitude);
                double brightness = RtReflectorLight.Brightness(share, percent);
                if (brightness <= 1e-5) continue;
                float albedo = Mathf.Max(Luminance(colour), 1e-4f);
                Color light = tint * (colour / albedo) * (float)brightness;
                candidates.Add(new Candidate
                {
                    Towards = ((Vector3)(toBody / distance)),
                    Radius = Mathf.Asin(Mathf.Clamp01((float)(body.Radius / distance))),
                    Colour = light,
                    // To its surface; negative for the main body, which its own sphere
                    // does not shade.
                    Reach = (body == main ? -1f : 1f) * (float)(distance - body.Radius),
                    Luminance = Luminance(light),
                });
            }
            candidates.Sort((a, b) => b.Luminance.CompareTo(a.Luminance));
            int count = Mathf.Min(2, candidates.Count);
            for (int i = 0; i < count; i++)
            {
                Candidate c = candidates[i];
                lights[i * 8 + 0] = c.Towards.x;
                lights[i * 8 + 1] = c.Towards.y;
                lights[i * 8 + 2] = c.Towards.z;
                lights[i * 8 + 3] = c.Radius;
                lights[i * 8 + 4] = c.Colour.r;
                lights[i * 8 + 5] = c.Colour.g;
                lights[i * 8 + 6] = c.Colour.b;
                lights[i * 8 + 7] = c.Reach;
                lastLuminance[i] = c.Luminance;
                lastMain[i] = c.Reach < 0f;
            }
            lastCount = count;
            return count;
        }

        // What Fill handed over last, for the diagnostics log (RtKsp.Report).
        private static int lastCount;
        private static readonly float[] lastLuminance = new float[2];
        private static readonly bool[] lastMain = new bool[2];

        internal static string Describe()
        {
            if (lastCount == 0) return "no moonlight";
            var text = new System.Text.StringBuilder("moonlight");
            for (int i = 0; i < lastCount; i++)
                text.Append(i > 0 ? ", " : " ").Append(lastLuminance[i].ToString("0.0000"))
                    .Append(lastMain[i] ? " (the main body)" : "");
            return text.ToString();
        }

        private static float Luminance(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        // Where the scaled-space shaders keep the surface's colour: Parallax's in
        // _ColorMap, its _MainTex white (ParallaxScaled.shader); KSP's and Kopernicus's in
        // _MainTex.
        private static readonly string[] ColourMaps = { "_ColorMap", "_MainTex" };

        // The body's scaled-space texture, averaged and linear, once it is there: Parallax
        // loads it when the body comes into view (ScaledOnDemandComponent), a white
        // placeholder until then. Grey meanwhile, tried again every ten seconds.
        private static Color ColourOf(CelestialBody body)
        {
            Color colour;
            if (colours.TryGetValue(body, out colour)) return colour;
            float due;
            if (retryAt.TryGetValue(body, out due) && Time.unscaledTime < due) return Grey;
            bool read = false;
            colour = Grey;
            try
            {
                Renderer renderer = body.scaledBody.GetComponent<Renderer>();
                Material material = renderer != null ? renderer.sharedMaterial : null;
                foreach (string property in ColourMaps)
                {
                    Texture texture = material != null && material.HasProperty(property) ? material.GetTexture(property) : null;
                    if (texture == null || texture == Texture2D.whiteTexture) continue;
                    Color average = Average(texture);
                    // A placeholder: Parallax's scaled shader leaves _MainTex white.
                    if (average.r >= 0.999f && average.g >= 0.999f && average.b >= 0.999f) continue;
                    colour = average;
                    read = true;
                    break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(Core.Log.Tag + " " + body.bodyName + "'s colour for its moonlight could not be read: " + e.Message);
            }
            if (!read)
            {
                retryAt[body] = Time.unscaledTime + 10f;
                return Grey;
            }
            colours[body] = colour;
            Debug.Log(Core.Log.Tag + " Raytracing: " + body.bodyName + "'s light colour " + colour.r.ToString("0.000") + ", "
                      + colour.g.ToString("0.000") + ", " + colour.b.ToString("0.000") + " (linear).");
            return colour;
        }

        // Rendered down to 16 by 16 and read back: the bytes are gamma-space values in
        // gamma space, sRGB codes in linear space, linear either way once decoded.
        private static Color Average(Texture texture)
        {
            RenderTexture small = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture before = RenderTexture.active;
            Texture2D read = new Texture2D(16, 16, TextureFormat.RGBA32, false, false);
            try
            {
                Graphics.Blit(texture, small);
                RenderTexture.active = small;
                read.ReadPixels(new Rect(0, 0, 16, 16), 0, 0, false);
                Color32[] pixels = read.GetPixels32();
                double r = 0, g = 0, b = 0;
                foreach (Color32 p in pixels)
                {
                    r += Mathf.GammaToLinearSpace(p.r / 255f);
                    g += Mathf.GammaToLinearSpace(p.g / 255f);
                    b += Mathf.GammaToLinearSpace(p.b / 255f);
                }
                int n = pixels.Length;
                return new Color((float)(r / n), (float)(g / n), (float)(b / n));
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(small);
                Object.Destroy(read);
            }
        }
    }
}
