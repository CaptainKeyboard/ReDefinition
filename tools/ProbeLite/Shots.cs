using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace ProbeLite
{
    // What a picture says of its colours: the mean of every sampled pixel and of the lowest quarter of the screen (the ground in all the poses),
    // 0 to 255 per channel, and the lowest quarter's luma.
    internal struct Colours
    {
        internal double AllR, AllG, AllB, LowR, LowG, LowB;
        // The standard deviation of the luma over the sampled pixels: a black or one-colour picture has none.
        internal double AllLumaStd;
        // The K-S2 box: the lower-left of the picture, x from 0 to LlWidthFrac of the width and the lowest LlHeightFrac of the height (pixels 0 to 845 across and the bottom
        // 216 rows of 1440 at 2560x1440). The box was fitted to the K-S2 capture_ab picture (its lower-left mean 65.5, 70.7, 73.1 against the 65.7, 71.5, 72.5 stated for it);
        // the lowest quarter stays as before.
        internal double LlR, LlG, LlB;
        // The haze: the mean luma of the strip HorizonStripFrac of the height just below the horizon line (NaN where the horizon is not in the picture), and the local contrast
        // of the ground: the mean standard deviation of the luma over 8 x 8 pixel blocks of the lowest quarter.
        internal double HorizonLuma, GroundContrast;

        internal double LlGreen
        {
            get { return LlG - 0.5 * (LlR + LlB); }
        }

        internal double LowLuma
        {
            get { return 0.2126 * LowR + 0.7152 * LowG + 0.0722 * LowB; }
        }

        // How far the lowest quarter leans to green over grey: G minus the mean of R and B.
        internal double LowGreen
        {
            get { return LowG - 0.5 * (LowR + LowB); }
        }
    }

    // One picture: wait the stated frames, then until the ground's colour stands still (or the limit), then write the PNG and one row of results.tsv.
    internal static class Shots
    {
        private const string Header = "name\tscene\theading\televation\tcam_agl_pqs_m\tcam_over_collider_m\tlooks_at_object\tlooks_at_material\tlooks_at_shader\tunder_object\t"
                                      + "sun_elev_deg\tall_r\tall_g\tall_b\tlow_r\tlow_g\tlow_b\tlow_green\tll_r\tll_g\tll_b\tll_green\thorizon_luma\tground_contrast\t"
                                      + "settle_s\tsettle\tframes\tsize\tverdict\tfile";

        internal const double LlWidthFrac = 0.33, LlHeightFrac = 0.15, HorizonStripFrac = 0.04;

        // horizonFromTop: the horizon's row as a fraction of the height from the top (NaN where not in the picture).
        internal static Colours Measure(Texture2D tex, double horizonFromTop)
        {
            int w = tex.width, h = tex.height;
            Color32[] px = tex.GetPixels32();
            double ar = 0, ag = 0, ab = 0, lr = 0, lg = 0, lb = 0, kr = 0, kg = 0, kb = 0, hs = 0;
            long na = 0, nl = 0, nk = 0, nh = 0;
            double ls = 0, lq = 0;
            int lowRows = h / 4, llRows = (int)(h * LlHeightFrac), llCols = (int)(w * LlWidthFrac);
            // Texture rows run from the bottom: the strip is the rows just under the horizon line on the screen.
            int stripTop = double.IsNaN(horizonFromTop) ? -1 : (int)((1.0 - horizonFromTop) * h), stripBottom = stripTop - (int)(h * HorizonStripFrac);
            for (int y = 0; y < h; y += 2)
            {
                for (int x = 0; x < w; x += 2)
                {
                    Color32 c = px[y * w + x];
                    ar += c.r; ag += c.g; ab += c.b; na++;
                    double luma = 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
                    ls += luma; lq += luma * luma;
                    if (y < lowRows) { lr += c.r; lg += c.g; lb += c.b; nl++; }
                    if (y < llRows && x < llCols) { kr += c.r; kg += c.g; kb += c.b; nk++; }
                    if (stripTop >= 0 && y < stripTop && y >= stripBottom && y >= 0) { hs += luma; nh++; }
                }
            }
            var m = new Colours();
            m.HorizonLuma = nh > 0 ? hs / nh : double.NaN;
            if (na > 0)
            {
                m.AllR = ar / na; m.AllG = ag / na; m.AllB = ab / na;
                m.AllLumaStd = Math.Sqrt(Math.Max(0.0, lq / na - (ls / na) * (ls / na)));
            }
            if (nl > 0) { m.LowR = lr / nl; m.LowG = lg / nl; m.LowB = lb / nl; }
            if (nk > 0) { m.LlR = kr / nk; m.LlG = kg / nk; m.LlB = kb / nk; }
            // The ground's local contrast: the standard deviation of the luma in each 8 x 8 block of the lowest quarter (every second pixel), averaged.
            double contrast = 0;
            long blocks = 0;
            for (int by = 0; by + 8 <= lowRows; by += 8)
            {
                for (int bx = 0; bx + 8 <= w; bx += 8)
                {
                    double s = 0, q = 0;
                    for (int y = by; y < by + 8; y += 2)
                        for (int x = bx; x < bx + 8; x += 2)
                        {
                            Color32 c = px[y * w + x];
                            double luma = 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
                            s += luma; q += luma * luma;
                        }
                    double mean = s / 16.0;
                    contrast += Math.Sqrt(Math.Max(0.0, q / 16.0 - mean * mean));
                    blocks++;
                }
            }
            m.GroundContrast = blocks > 0 ? contrast / blocks : double.NaN;
            return m;
        }

        // Writes one picture of the scene as it stands, named name. The yields are the caller's to pass on (foreach ... yield return).
        internal static IEnumerable Take(string name, PoseSpec pose, string scene, double minSettleS, double maxSettleS)
        {
            int frames = 0;
            // The interface away at the start of every picture: a mod's scene arrival may bring it back (ReDefinition's does, a few seconds into the space centre).
            GameEvents.onHideUI.Fire();
            while (frames < Lite.MinFrames)
            {
                yield return null;
                frames++;
            }
            GameEvents.onHideUI.Fire();
            double t0 = Time.realtimeSinceStartup;
            Texture2D tex = null;
            Colours m = new Colours();
            double previous = double.NaN;
            int stable = 0;
            string how = "limit";
            while (true)
            {
                yield return new WaitForEndOfFrame();
                frames++;
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = ScreenCapture.CaptureScreenshotAsTexture();
                m = Measure(tex, pose != null ? Rig.HorizonFromTop() : double.NaN);
                double elapsed = Time.realtimeSinceStartup - t0;
                stable = elapsed >= minSettleS && !double.IsNaN(previous) && Math.Abs(m.LowLuma - previous) < Lite.StableLuma ? stable + 1 : 0;
                previous = m.LowLuma;
                if (stable >= 2) { how = "stable"; break; }
                if (elapsed >= maxSettleS) break;
                yield return new WaitForSecondsRealtime(1f);
            }
            double settled = Time.realtimeSinceStartup - t0;

            // What the camera really is, read from the camera object after the settling.
            double az, el, agl, alt;
            Vector3d camPos;
            bool read = Rig.ReadBack(out az, out el, out agl, out alt, out camPos);
            GroundReading under = new GroundReading { Object = "", Material = "", Shader = "", AltAsl = double.NaN };
            GroundReading ahead = under;
            double over = double.NaN, sun = double.NaN;
            if (read)
            {
                under = Rig.RayDown(camPos);
                ahead = Rig.RayDown(Rig.Ahead(camPos));
                if (under.Hit) over = alt - under.AltAsl;
                sun = Lite.SunElevationDeg(camPos, Rig.Home());
            }
            string size = tex != null ? tex.width + "x" + tex.height : "none";
            string verdict = "";
            if (tex == null || tex.width != Screen.width || tex.height != Screen.height) verdict += " WRONG-SIZE(" + size + ", screen " + Screen.width + "x" + Screen.height + ")";
            if (m.AllLumaStd < 3.0 || (m.AllR + m.AllG + m.AllB) / 3.0 < 6.0) verdict += " BLACK-OR-UNIFORM(luma std " + Lite.F(m.AllLumaStd) + ")";
            string file = name + ".png";
            try
            {
                Directory.CreateDirectory(Lite.Shots);
                File.WriteAllBytes(Path.Combine(Lite.Shots, file), tex.EncodeToPNG());
            }
            catch (Exception e)
            {
                Lite.Warn(name + ": the picture could not be written (" + e.Message + ")");
                file = "none";
            }
            if (tex != null) UnityEngine.Object.Destroy(tex);

            // The pose is judged where there is one: the camera stands where it was put, and (low poses) not under the ground.
            if (pose != null && !read) verdict += " NO-CAMERA-READBACK";
            if (pose != null && read)
            {
                double wantAz = pose.HeadingDeg, wantEl = pose.ElevationDeg;
                double dAz = Math.Abs(((az - wantAz) % 360.0 + 540.0) % 360.0 - 180.0), dEl = Math.Abs(el - wantEl);
                if (dAz > 0.5 || dEl > 0.5) verdict += " AIM-OFF(az " + Lite.F(az) + ", el " + Lite.F(el) + ")";
                if (double.IsNaN(pose.AbsAltM) && Math.Abs(agl - pose.CamAglM) > 1.0) verdict += " HEIGHT-OFF(" + Lite.F(agl) + " m)";
                if (pose.CamAglM < 50.0 || !double.IsNaN(pose.AbsAltM))
                {
                    if (!under.Hit) verdict += " NO-COLLIDER-UNDER";
                    else if (over < 0.8) verdict += " UNDER-GROUND(" + Lite.F(over) + " m over the collider)";
                }
            }
            if (verdict.Length > 0) Lite.Warn(name + ":" + verdict);
            Lite.Say("picture " + name + ": " + scene + ", heading " + (pose != null ? Lite.F(pose.HeadingDeg) : "-") + ", camera " + Lite.F(agl) + " m over PQS, " + Lite.F(over)
                     + " m over the collider (" + under.Object + "), looks at " + ahead.Object + " / " + ahead.Material + " / " + ahead.Shader
                     + "; sun " + Lite.F(sun) + " deg; mean all " + Lite.F(m.AllR) + "," + Lite.F(m.AllG) + "," + Lite.F(m.AllB) + ", lowest quarter " + Lite.F(m.LowR) + ","
                     + Lite.F(m.LowG) + "," + Lite.F(m.LowB) + " (green " + Lite.F(m.LowGreen) + "); K-S2 box (lower-left) " + Lite.F(m.LlR) + "," + Lite.F(m.LlG) + "," + Lite.F(m.LlB) + " (green " + Lite.F(m.LlGreen)
                     + "); horizon strip luma " + Lite.F(m.HorizonLuma) + ", ground contrast " + Lite.F(m.GroundContrast) + "; " + frames + " frames, " + Lite.F(settled) + " s (" + how + ")" + verdict);

            string results = Path.Combine(Lite.Shots, "results.tsv");
            var row = new StringBuilder();
            if (!File.Exists(results)) row.AppendLine(Header);
            row.Append(name).Append('\t').Append(scene).Append('\t').Append(pose != null ? Lite.F(pose.HeadingDeg) : "-").Append('\t').Append(pose != null ? Lite.F(pose.ElevationDeg) : "-")
               .Append('\t').Append(Lite.F(agl)).Append('\t').Append(Lite.F(over)).Append('\t').Append(ahead.Object).Append('\t').Append(ahead.Material).Append('\t').Append(ahead.Shader)
               .Append('\t').Append(under.Object).Append('\t').Append(Lite.F(sun)).Append('\t').Append(Lite.F(m.AllR)).Append('\t').Append(Lite.F(m.AllG)).Append('\t').Append(Lite.F(m.AllB))
               .Append('\t').Append(Lite.F(m.LowR)).Append('\t').Append(Lite.F(m.LowG)).Append('\t').Append(Lite.F(m.LowB)).Append('\t').Append(Lite.F(m.LowGreen))
               .Append('\t').Append(Lite.F(m.LlR)).Append('\t').Append(Lite.F(m.LlG)).Append('\t').Append(Lite.F(m.LlB)).Append('\t').Append(Lite.F(m.LlGreen))
               .Append('\t').Append(Lite.F(m.HorizonLuma)).Append('\t').Append(Lite.F(m.GroundContrast))
               .Append('\t').Append(Lite.F(settled)).Append('\t').Append(how).Append('\t').Append(frames).Append('\t').Append(size).Append('\t').Append(verdict.Length > 0 ? verdict.Trim() : "ok").Append('\t').AppendLine(file);
            File.AppendAllText(results, row.ToString());
            LastUnder = under;
            LastAltAsl = alt;
        }

        // The ground collider under the camera of the last picture (the lawn poses are put over it).
        internal static GroundReading LastUnder;
        internal static double LastAltAsl;
    }
}
