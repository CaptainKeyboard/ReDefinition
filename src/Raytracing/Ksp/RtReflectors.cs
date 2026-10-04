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
    // The main body is none of them: rays that miss the scene and meet its sphere take
    // its light there (RtCommon.hlsli, PlanetRadiance), from its albedo (PlanetAlbedo).
    //
    // The lights are tinted cool: at night the eye sees moonlight bluish, though it is
    // nearly the sun's colour (the Purkinje effect), and a night lit in the sun's colour
    // looks warm (the player, 2026-10-03: "not cool enough").
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
            internal Color Surface;
            // The brightest at 100 %, whatever the player's percentage.
            internal float Order;
        }

        private static readonly List<Candidate> candidates = new List<Candidate>();
        private static readonly List<CelestialBody> system = new List<CelestialBody>();
        // Luminance 1: 0.2126 * 0.83 + 0.7152 * 1.01 + 0.0722 * 1.42.
        private static readonly Color NightTint = new Color(0.83f, 1.01f, 1.42f);

        // Fills lights as RtCamera.Lights asks, for a camera at `eye` (world) over `main`,
        // the player's percentage; the number of lights.
        // surfaces: per light its body's albedo times the star's colour, linear, which a
        // reflected ray sees on its disc, at the player's percentage `mirrored`.
        internal static int Fill(float[] lights, float[] surfaces, Vector3d eye, CelestialBody main, float percent,
                                 float mirrored)
        {
            lastCount = 0;
            // Also at 0 %: a reflected ray still sees the moons' discs (surfaces).
            if (main == null) return 0;
            CelestialBody star = RtKspMods.StarBody();
            if (star == null) return 0;
            // The star's colour as the bodies get it: the scene's light is tinted by the
            // atmosphere (Scatterer), and darkened at night (measured, KSP.log: an orange
            // moonlight, and none with the Mun high).
            Color starColour = RtKspMods.StarColour().linear;
            float tintLuminance = Luminance(starColour);
            starColour = tintLuminance > 0f ? starColour / tintLuminance : Color.white;
            Color tint = starColour * NightTint;

            // The discs as the eye sees them at night, brighter than their albedo makes them
            // (NightAdaptation): what water mirrors of them.
            float discFactor = NightAdaptation.DiscFactor(eye, main, star) * Mathf.Max(mirrored, 0f) / 100f;

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
                if (distance <= body.Radius || body == main) continue;
                Vector3d bodyToSun = sun - body.position;
                double angle = Vector3d.Angle(bodyToSun, -toBody) * Mathf.Deg2Rad;
                Color colour = ColourOf(body);
                double share = RtReflectorLight.Share(Luminance(colour), angle, body.Radius, distance, sunToEye,
                                                      bodyToSun.magnitude);
                if (share <= 0.0) continue;
                double brightness = RtReflectorLight.Brightness(share, percent);
                float albedo = Mathf.Max(Luminance(colour), 1e-4f);
                Color light = tint * (colour / albedo) * (float)brightness;
                candidates.Add(new Candidate
                {
                    Towards = ((Vector3)(toBody / distance)),
                    Radius = Mathf.Asin(Mathf.Clamp01((float)(body.Radius / distance))),
                    Colour = light,
                    // To its surface.
                    Reach = (float)(distance - body.Radius),
                    Luminance = Luminance(light),
                    Surface = colour * starColour * discFactor,
                    Order = (float)RtReflectorLight.Brightness(share, 100.0),
                });
            }
            candidates.Sort((a, b) => b.Order.CompareTo(a.Order));
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
                surfaces[i * 4 + 0] = c.Surface.r;
                surfaces[i * 4 + 1] = c.Surface.g;
                surfaces[i * 4 + 2] = c.Surface.b;
                lastLuminance[i] = c.Luminance;
            }
            lastCount = count;
            return count;
        }

        // What Fill handed over last, for the diagnostics log (RtKsp.Report).
        private static int lastCount;
        private static readonly float[] lastLuminance = new float[2];

        internal static string Describe()
        {
            if (lastCount == 0) return "no moonlight";
            var text = new System.Text.StringBuilder("moonlight");
            for (int i = 0; i < lastCount; i++)
                text.Append(i > 0 ? ", " : " ").Append(lastLuminance[i].ToString("0.0000"));
            return text.ToString();
        }

        // The main body's albedo, linear, for the rays that meet its sphere.
        internal static Color PlanetAlbedo(CelestialBody main)
        {
            return main != null && main.scaledBody != null ? ColourOf(main) : Grey;
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
