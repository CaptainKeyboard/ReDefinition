using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // The bodies the sun lights, as the raytracing's lights. They are the planet the camera is at, the main body or
    // the planet it orbits, and that planet's moons. Farther planets are left out.
    // Each one's light at the camera follows from its albedo, its phase, its size and its distance
    // (RtReflectorLight), in the colour of its surface and of the star. The two brightest
    // go to the proxy, each a disc of its angular size. The rays sample the disc with its
    // shadow, so the Mun lights only where it stands in the sky and nothing lies between.
    // The main body is none of them: rays that miss the scene and meet its sphere take
    // its light there (RtCommon.hlsli, PlanetRadiance), from its albedo (PlanetAlbedo).
    //
    // The lights are tinted cool. At night the eye sees moonlight bluish, though it is
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
        // Textures read as white placeholders, with their size and update count then: not
        // read again while those stay, as each read waits for the GPU (ReadPixels). The
        // body's own texture, once loaded, is another texture or the same one filled.
        private static readonly Dictionary<Texture, Vector3Int> placeholders = new Dictionary<Texture, Vector3Int>();

        // A texture's size and update count, which change when it is filled or replaced.
        private static Vector3Int Shape(Texture texture)
        {
            return new Vector3Int(texture.width, texture.height, (int)texture.updateCount);
        }

        // Whether the texture was read as a white placeholder and has not changed since.
        private static bool Placeholder(Texture texture)
        {
            Vector3Int shape;
            return placeholders.TryGetValue(texture, out shape) && shape == Shape(texture);
        }

        // A body's light as Fill weighs it, before the two brightest are handed over.
        private struct Candidate
        {
            // The direction from the eye to the body, its angular radius, its light, the distance to its surface, the
            // light's luminance, and its disc's colour as a reflected ray sees it.
            internal Vector3 Towards;
            internal float Radius;
            internal Color Colour;
            internal float Reach;
            internal float Luminance;
            internal Color Surface;
            // The brightest at 100 %, whatever the player's percentage.
            internal float Order;
            internal CelestialBody Body;
        }

        // The frame's candidates, brightest first after sorting, and the bodies of the camera's planet's system.
        private static readonly List<Candidate> candidates = new List<Candidate>();
        private static readonly List<CelestialBody> system = new List<CelestialBody>();
        // Luminance 1: 0.2126 * 0.83 + 0.7152 * 1.01 + 0.0722 * 1.42.
        private static readonly Color NightTint = new Color(0.83f, 1.01f, 1.42f);

        // The planet `main` belongs to -- itself, or the one it circles -- and its moons,
        // into `bodies`; false where there is none, as at the star itself.
        internal static bool SystemOf(CelestialBody main, CelestialBody star, List<CelestialBody> bodies)
        {
            bodies.Clear();
            CelestialBody planet = main;
            while (planet != null && planet.referenceBody != null && planet.referenceBody != star && planet.referenceBody != planet)
                planet = planet.referenceBody;
            if (planet == null || planet == star) return false;
            bodies.Add(planet);
            // One by one: Mono's AddRange copies through a new array, every frame here
            // (MoonGlow, Fill).
            List<CelestialBody> moons = planet.orbitingBodies;
            if (moons != null)
                for (int i = 0; i < moons.Count; i++) bodies.Add(moons[i]);
            return true;
        }

        // Fills lights as RtCamera.Lights asks, for a camera at `eye` (world) over `main`,
        // at the player's percentage `percent`. Returns the number of lights.
        // `surfaces` gets, per light, its body's albedo times the star's colour, linear, which a
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
            if (!SystemOf(main, star, system)) return 0;
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
                    Body = body,
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
            if (count > 0)
            {
                brightest = candidates[0];
                brightestFrame = Time.frameCount;
                brightestMain = main;
                brightestEye = eye;
                // Every body's light together, for EVE's clouds' one light.
                Vector3 weighted = Vector3.zero;
                Color sum = Color.black;
                foreach (Candidate c in candidates)
                {
                    weighted += c.Towards * c.Luminance;
                    sum += c.Colour;
                }
                combinedTowards = weighted.sqrMagnitude > 0f ? weighted.normalized : brightest.Towards;
                combinedColour = sum;
            }
            return count;
        }

        // What Fill handed over last, for the diagnostics log (RtKsp.Report).
        private static int lastCount;
        private static readonly float[] lastLuminance = new float[2];
        // The brightest light Fill found last, the frame and the main body it was for: the
        // light EVE's clouds take at night (EveCloudMoonlight).
        private static Candidate brightest;
        private static int brightestFrame = -100;
        private static CelestialBody brightestMain;
        private static Vector3d brightestEye;
        private static Vector3 combinedTowards;
        private static Color combinedColour;

        // The bodies' light over `main` as the raytracing takes it this frame or the last
        // few, as one. It gives the brightest body, the camera (world), the direction from it the
        // bodies' light comes from, each weighted by its brightness, and their colours
        // together, in units of the sunlight, linear, at the player's percentage. So a
        // moon as small as Minmus counts for as little as it lights, and the light does not
        // jump from one body to another as their order changes. It is false where the raytracing
        // has handed over none since, as while it is off.
        internal static bool Combined(CelestialBody main, out CelestialBody body, out Vector3d eye, out Vector3 towards,
                                      out Color colour)
        {
            bool recent = Time.frameCount - brightestFrame <= 3 && main != null && main == brightestMain
                          && brightest.Body != null;
            body = recent ? brightest.Body : null;
            eye = recent ? brightestEye : Vector3d.zero;
            towards = recent ? combinedTowards : Vector3.zero;
            colour = recent ? combinedColour : Color.black;
            return recent;
        }

        // The lights Fill handed over last, by their luminance, for the diagnostics log (RtKsp.Report).
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

        // Rec. 709 luminance of a linear colour.
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
                    if (texture == null || texture == Texture2D.whiteTexture || Placeholder(texture)) continue;
                    Color average = Average(texture);
                    // A placeholder: Parallax's scaled shader leaves _MainTex white.
                    if (average.r >= 0.999f && average.g >= 0.999f && average.b >= 0.999f)
                    {
                        placeholders[texture] = Shape(texture);
                        continue;
                    }
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
