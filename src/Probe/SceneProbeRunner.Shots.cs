using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Ui;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // The single shots of the probe's flight (SceneProbeRunner): the sky's faces, the stages at a renderer, the light buffer,
    // the diagnostics window, the proxy's ini settings and the raytraced light's values.
    internal sealed partial class SceneProbeRunner
    {
        // The reflection probe the rays see the sky in (RtKsp, Environment), its six faces
        // into `folder` as <shot>-env<face>.ppm, clamped to 0..1.
        internal static void EnvironmentFaces(string folder, string shot)
        {
            ReflectionProbe found = null;
            foreach (ReflectionProbe p in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>())
                if (p.isActiveAndEnabled && p.texture != null)
                {
                    found = p;
                    break;
                }
            var cube = found != null ? found.texture as RenderTexture : null;
            if (cube == null)
            {
                Debug.Log(Log.Tag + " Probe: no reflection probe render texture to dump.");
                return;
            }
            var face = new RenderTexture(cube.width, cube.height, 0, cube.format);
            face.Create();
            var read = new Texture2D(cube.width, cube.height, TextureFormat.RGBA32, false);
            RenderTexture active = RenderTexture.active;
            for (int i = 0; i < 6; i++)
            {
                Graphics.CopyTexture(cube, i, 0, face, 0, 0);
                RenderTexture.active = face;
                read.ReadPixels(new Rect(0, 0, cube.width, cube.height), 0, 0);
                Color32[] pixels = read.GetPixels32();
                var bytes = new List<byte>(Encoding.ASCII.GetBytes("P6\n" + cube.width + " " + cube.height + "\n255\n"));
                for (int y = cube.height - 1; y >= 0; y--)
                    for (int x = 0; x < cube.width; x++)
                    {
                        Color32 c = pixels[y * cube.width + x];
                        bytes.Add(c.r);
                        bytes.Add(c.g);
                        bytes.Add(c.b);
                    }
                File.WriteAllBytes(Path.Combine(folder, shot + "-env" + i + ".ppm"), bytes.ToArray());
            }
            RenderTexture.active = active;
            UnityEngine.Object.Destroy(read);
            face.Release();
            UnityEngine.Object.Destroy(face);
            Debug.Log(Log.Tag + " Probe: reflection probe " + found.name + " (" + cube.width + ", " + cube.format + ") dumped.");
        }

        // The camera events at whose end Stages reads the camera's target.
        private static readonly CameraEvent[] StageEvents =
        {
            CameraEvent.AfterGBuffer, CameraEvent.BeforeLighting, CameraEvent.AfterLighting, CameraEvent.AfterForwardOpaque,
            CameraEvent.AfterSkybox, CameraEvent.AfterForwardAlpha, CameraEvent.BeforeImageEffects, CameraEvent.AfterImageEffects,
            CameraEvent.AfterEverything,
        };

        // At the nearest visible renderer so named (<name>@<parent's name>), a 32-pixel window of Camera 00's target
        // at each of StageEvents: the brightest pixel and the mean into the log.
        private static IEnumerator Stages(string rendererName)
        {
            Camera scene = ProbeSteps.SceneCamera();
            // <name>@<parent's name>: only under a parent so named.
            string[] parts = rendererName.Split('@');
            string name = parts[0], parentName = parts.Length > 1 ? parts[1] : null;
            Renderer target = null;
            float nearest = float.MaxValue;
            if (scene != null)
                foreach (Renderer r in FindObjectsOfType<Renderer>())
                {
                    if (r.name != name || (parentName != null && (r.transform.parent == null || r.transform.parent.name != parentName))
                        || !r.isVisible)
                        continue;
                    float d = Vector3.Distance(scene.transform.position, r.bounds.center);
                    if (d < nearest) { nearest = d; target = r; }
                }
            if (target == null)
            {
                Debug.Log(Log.Tag + " Probe: stages, no visible renderer " + rendererName + " or no Camera 00.");
                yield break;
            }
            // In the camera's pixels: its screen points are the screen's. A window around the
            // middle of its bounds, which may lie off the renderer's own pixels.
            const int Window = 32;
            Vector3 at = scene.WorldToViewportPoint(target.bounds.center);
            int px = Mathf.Clamp(Mathf.RoundToInt(at.x * scene.pixelWidth), Window / 2, scene.pixelWidth - Window / 2);
            int py = Mathf.Clamp(Mathf.RoundToInt(at.y * scene.pixelHeight), Window / 2, scene.pixelHeight - Window / 2);
            Vector2 scale = new Vector2((float)Window / scene.pixelWidth, (float)Window / scene.pixelHeight);
            Vector2 offset = new Vector2((float)(px - Window / 2) / scene.pixelWidth, (float)(py - Window / 2) / scene.pixelHeight);
            var buffers = new List<KeyValuePair<CameraEvent, CommandBuffer>>();
            var crops = new List<KeyValuePair<string, RenderTexture>>();
            foreach (CameraEvent e in StageEvents)
            {
                var cb = new CommandBuffer { name = "ReDefinition probe stages " + e };
                RenderTexture crop = new RenderTexture(Window, Window, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                crop.Create();
                cb.Blit(BuiltinRenderTextureType.CameraTarget, crop, scale, offset);
                crops.Add(new KeyValuePair<string, RenderTexture>(e.ToString(), crop));
                scene.AddCommandBuffer(e, cb);
                buffers.Add(new KeyValuePair<CameraEvent, CommandBuffer>(e, cb));
            }
            yield return new WaitForEndOfFrame();
            foreach (KeyValuePair<CameraEvent, CommandBuffer> b in buffers)
                scene.RemoveCommandBuffer(b.Key, b.Value);
            var line = new StringBuilder(Log.Tag + " Probe: stages at " + rendererName + " under "
                                         + (target.transform.parent != null ? target.transform.parent.name : "nothing") + ", "
                                         + nearest.ToString("0.0", CultureInfo.InvariantCulture) + " m, pixel " + px + "," + py
                                         + " of " + scene.pixelWidth + "x" + scene.pixelHeight + ", the window's brightest pixel and mean:");
            Texture2D read = new Texture2D(Window, Window, TextureFormat.RGBAFloat, false, true);
            RenderTexture active = RenderTexture.active;
            foreach (KeyValuePair<string, RenderTexture> crop in crops)
            {
                RenderTexture.active = crop.Value;
                read.ReadPixels(new Rect(0, 0, Window, Window), 0, 0);
                Color sum = Color.clear, most = Color.clear;
                foreach (Color c in read.GetPixels())
                {
                    sum += c;
                    if (c.r + c.g + c.b > most.r + most.g + most.b) most = c;
                }
                sum /= Window * Window;
                line.Append(System.Environment.NewLine).Append("  ").Append(crop.Key).Append(' ').Append(Rgb(most)).Append(", mean ").Append(Rgb(sum));
                crop.Value.Release();
            }
            RenderTexture.active = active;
            Destroy(read);
            Debug.Log(line.ToString());
        }

        // Camera 00's target after the G-buffer and before the lighting, each as <shot>-<event>.bin: width, height,
        // then RGBA floats.
        private static IEnumerator LightBuffer(string folder, string shot)
        {
            Camera scene = ProbeSteps.SceneCamera();
            if (scene == null) yield break;
            var events = new[] { CameraEvent.AfterGBuffer, CameraEvent.BeforeLighting };
            var buffers = new CommandBuffer[events.Length];
            var targets = new RenderTexture[events.Length];
            for (int i = 0; i < events.Length; i++)
            {
                targets[i] = new RenderTexture(scene.pixelWidth, scene.pixelHeight, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                targets[i].Create();
                buffers[i] = new CommandBuffer { name = "ReDefinition probe light buffer " + events[i] };
                buffers[i].Blit(BuiltinRenderTextureType.CameraTarget, targets[i]);
                scene.AddCommandBuffer(events[i], buffers[i]);
            }
            yield return new WaitForEndOfFrame();
            RenderTexture active = RenderTexture.active;
            for (int i = 0; i < events.Length; i++)
            {
                scene.RemoveCommandBuffer(events[i], buffers[i]);
                Texture2D read = new Texture2D(targets[i].width, targets[i].height, TextureFormat.RGBAFloat, false, true);
                RenderTexture.active = targets[i];
                read.ReadPixels(new Rect(0, 0, targets[i].width, targets[i].height), 0, 0, false);
                string name = events[i] == CameraEvent.AfterGBuffer ? "afterGBuffer" : "beforeLighting";
                using (var file = new BinaryWriter(File.Create(Path.Combine(folder, shot + "-" + name + ".bin"))))
                {
                    file.Write(read.width);
                    file.Write(read.height);
                    foreach (Color c in read.GetPixels())
                    {
                        file.Write(c.r);
                        file.Write(c.g);
                        file.Write(c.b);
                        file.Write(c.a);
                    }
                }
                Destroy(read);
                targets[i].Release();
            }
            RenderTexture.active = active;
            Debug.Log(Log.Tag + " Probe: light buffer of Camera 00 written.");
        }

        // Windows' ini writer, for ReDefinitionProxy.ini's keys.
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool WritePrivateProfileString(string section, string key, string value, string file);

        // Windows' ini reader: a key's value, or fallback where it is missing.
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint GetPrivateProfileString(string section, string key, string fallback, StringBuilder value, uint size,
                                                           string file);

        // Shoots the diagnostics window, step by step. Each step's commands are applied, then 2 s pass for the
        // window and its strip to settle, then the frame is shot as the screen shows it, window included. It waits
        // 20 s first, for the upscaler and the frame generation to be set up.
        private static IEnumerator DiagnosticsShots(string steps)
        {
            yield return new WaitForSeconds(20f);
            string folder = CaptureFolder.Run;
            foreach (string step in steps.Split('|'))
            {
                string[] parts = step.Split(new[] { '@' }, 2);
                string name = parts[0].Trim();
                if (ReDefinitionAddon.Instance == null) yield break;
                string done = ReDefinitionAddon.Instance.ProbeDiagnostics("show" + (parts.Length > 1 && parts[1].Length > 0 ? "," + parts[1] : ""));
                yield return new WaitForSeconds(2f);
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "diagnostics-" + name + ".png"));
                Debug.Log(Log.Tag + " Probe: diagnostics shot " + name + " (" + done.Trim() + ").");
                yield return new WaitForSeconds(1f);
            }
        }

        // A key mod.<key> is one of ReDefinition's own settings (OwnSettings, its key in
        // settings.cfg: mod.technique=Dlss, mod.raytracing=Full, mod.rayReconstruction=False),
        // applied as the settings window applies it; every other key the proxy's ini.
        private static IEnumerator IniShots(string folder, string shot, string sets)
        {
            string ini = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)) ?? "", "ReDefinitionProxy.ini");
            var before = new Dictionary<string, string>();
            var modBefore = new Dictionary<string, string>();
            int n = 0;
            foreach (string set in sets.Split(';'))
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                OwnSettings current = addon != null ? addon.Current() : null;
                OwnSettings wanted = current != null ? current.Clone() : null;
                foreach (string pair in set.Split(','))
                {
                    int equals = pair.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = pair.Substring(0, equals).Trim();
                    string text = pair.Substring(equals + 1).Trim();
                    if (key.StartsWith("mod.", StringComparison.Ordinal))
                    {
                        string modKey = key.Substring(4);
                        if (wanted == null) continue;
                        if (!modBefore.ContainsKey(modKey)) modBefore[modKey] = current.ValueOf(modKey);
                        if (!wanted.SetValue(modKey, text))
                            Debug.LogWarning(Log.Tag + " Probe: no setting " + modKey + " takes " + text + ".");
                        continue;
                    }
                    if (!before.ContainsKey(key))
                    {
                        var value = new StringBuilder(260);
                        GetPrivateProfileString("Proxy", key, "", value, (uint)value.Capacity, ini);
                        before[key] = value.ToString();
                    }
                    WritePrivateProfileString("Proxy", key, text, ini);
                }
                if (wanted != null && modBefore.Count > 0) addon.Apply(current, wanted);
                Debug.Log(Log.Tag + " Probe: ReDefinitionProxy.ini set to " + set + ".");
                yield return new WaitForSecondsRealtime(12f);
                // -redefinitionProbeSettingsWindow <tab>: the settings window on that tab in the shot,
                // built anew after the set so that it shows it.
                SettingCategory windowTab;
                string windowArgument = CommandLine.After("-redefinitionProbeSettingsWindow");
                if (windowArgument != null && Enum.TryParse(windowArgument, true, out windowTab))
                {
                    // -redefinitionProbeSettingsWindowScroll <0 to 1>: the page scrolled, 1 the top, 0 the bottom.
                    float scrollAt;
                    string scrollArgument = CommandLine.After("-redefinitionProbeSettingsWindowScroll");
                    TabScrollList.ProbePosition =
                        scrollArgument != null && float.TryParse(scrollArgument, NumberStyles.Float, CultureInfo.InvariantCulture, out scrollAt)
                            ? scrollAt : (float?)null;
                    ReDefinition.Window.SettingsWindow.OpenOn(windowTab);
                    yield return new WaitForSecondsRealtime(2f);
                    yield return new WaitForEndOfFrame();
                }
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-ini" + n + ".png"));
                Debug.Log(Log.Tag + " Probe: shot " + shot + "-ini" + n + " with " + set + ".");
                n++;
                yield return null;
            }
            foreach (KeyValuePair<string, string> pair in before)
                WritePrivateProfileString("Proxy", pair.Key, pair.Value, ini);
            ReDefinitionAddon restoring = ReDefinitionAddon.Instance;
            if (restoring != null && modBefore.Count > 0)
            {
                OwnSettings now = restoring.Current();
                OwnSettings back = now.Clone();
                foreach (KeyValuePair<string, string> pair in modBefore)
                {
                    if (pair.Value != null) back.SetValue(pair.Key, pair.Value);
                }
                restoring.Apply(now, back);
            }
            Debug.Log(Log.Tag + " Probe: ReDefinitionProxy.ini's values set back.");
        }

        // A colour's red, green and blue for the log, four decimals, space-separated.
        private static string Rgb(Color c)
        {
            return c.r.ToString("0.0000", CultureInfo.InvariantCulture) + " " + c.g.ToString("0.0000", CultureInfo.InvariantCulture) + " "
                   + c.b.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        // Logs the raytraced camera's light at a few places of the pad's view. It logs the
        // traced diffuse light (linear), the sun's visibility, the forward ratio (k, w),
        // Unity's ambient probe up / side / down and the sun's colour.
        private static IEnumerator LightValues()
        {
            Raytracing.RtCamera rt = Raytracing.RtCamera.Current;
            if (rt == null) yield break;
            yield return DumpShadowMask(rt);
            yield return LogTextureValues(rt);
            LogCellsAndLights(rt);
            LogAmbient(rt);
        }

        // The screen-space shadow mask Unity collected for the sun this frame, copied after
        // it is made, into the session's capture folder, shadowmask.pgm.
        private static IEnumerator DumpShadowMask(Raytracing.RtCamera rt)
        {
            Light masked = rt.SunLightNow;
            if (masked != null)
            {
                var copy = new RenderTexture(rt.Camera.pixelWidth, rt.Camera.pixelHeight, 0, RenderTextureFormat.ARGB32);
                var grab = new CommandBuffer { name = "ReDefinition probe shadow mask" };
                grab.Blit(BuiltinRenderTextureType.CurrentActive, copy);
                masked.AddCommandBuffer(LightEvent.AfterScreenspaceMask, grab);
                yield return new WaitForEndOfFrame();
                yield return null;
                masked.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, grab);
                AsyncGPUReadbackRequest maskRead = AsyncGPUReadback.Request(copy, 0, TextureFormat.RGBA32);
                while (!maskRead.done) yield return null;
                if (!maskRead.hasError)
                {
                    NativeArray<Color32> m = maskRead.GetData<Color32>();
                    int mw = copy.width, mh = copy.height;
                    byte[] head = Encoding.ASCII.GetBytes("P5\n" + mw + " " + mh + "\n255\n");
                    var mb = new byte[head.Length + mw * mh];
                    Array.Copy(head, mb, head.Length);
                    for (int y = 0; y < mh; y++)
                        for (int x = 0; x < mw; x++)
                            mb[head.Length + y * mw + x] = m[(mh - 1 - y) * mw + x].r;
                    File.WriteAllBytes(Path.Combine(CaptureFolder.Run, "shadowmask.pgm"), mb);
                }
                grab.Release();
                copy.Release();
            }
        }

        // The raytraced textures (diffuse, sun, ratio, forward, specular, ocean) read back: some as pictures
        // (PGM), each as its mean at a few places of the view in the log.
        private static IEnumerator LogTextureValues(Raytracing.RtCamera rt)
        {
            var places = new[] { new KeyValuePair<string, Rect>("lawn", new Rect(0.02f, 0.57f, 0.18f, 0.12f)),
                                 new KeyValuePair<string, Rect>("pad", new Rect(0.16f, 0.80f, 0.15f, 0.07f)),
                                 new KeyValuePair<string, Rect>("rocket", new Rect(0.485f, 0.35f, 0.022f, 0.14f)),
                                 new KeyValuePair<string, Rect>("far", new Rect(0.06f + 0.88f * 21f / 22f, 0.08f + 0.64f * 5f / 12f,
                                                                                0.88f / 22f, 0.64f / 12f)) };
            foreach (var texture in new[] { new KeyValuePair<string, RenderTexture>("diffuse", rt.DiffuseTexture),
                                            new KeyValuePair<string, RenderTexture>("sun", rt.SunTexture),
                                            new KeyValuePair<string, RenderTexture>("ratio", rt.ForwardRatioTexture),
                                            new KeyValuePair<string, RenderTexture>("forward", rt.ForwardTexture),
                                            new KeyValuePair<string, RenderTexture>("specular", rt.SpecularTexture),
                                            new KeyValuePair<string, RenderTexture>("ocean", rt.OceanTracedTexture) })
            {
                if (texture.Value == null) continue;
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture.Value, 0, TextureFormat.RGBAFloat);
                while (!request.done) yield return null;
                if (request.hasError) continue;
                NativeArray<Color> data = request.GetData<Color>();
                int w = texture.Value.width, h = texture.Value.height;
                // The forward ratio's w and the sun's visibility as pictures (PGM, top row
                // first): black where the rays held no surface, or saw no sun.
                if (texture.Key == "ratio" || texture.Key == "sun" || texture.Key == "diffuse" || texture.Key == "ocean")
                {
                    byte[] header = Encoding.ASCII.GetBytes("P5\n" + w + " " + h + "\n255\n");
                    var bytes = new byte[header.Length + w * h];
                    Array.Copy(header, bytes, header.Length);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            Color texel = data[(h - 1 - y) * w + x];
                            bytes[header.Length + y * w + x] = (byte)Mathf.Clamp((texture.Key == "sun" ? texel.r : texture.Key == "diffuse" ? texel.g * 4f : texture.Key == "ocean" ? (texel.a > 0f ? 0.5f + texel.g : 0f) : texel.g * 0.5f + 0.5f) * 255f, 0f, 255f);
                        }
                    string maskFolder = CaptureFolder.Run;
                    File.WriteAllBytes(Path.Combine(maskFolder, texture.Key + ".pgm"), bytes);
                }
                var line = new StringBuilder(Log.Tag + " Probe: light values, " + texture.Key + ":");
                foreach (var place in places)
                {
                    Color sum = Color.clear;
                    int n = 0;
                    // Rows from the bottom in the texture, the places from the top.
                    for (int y = (int)((1f - place.Value.yMax) * h); y < (int)((1f - place.Value.yMin) * h); y++)
                        for (int x = (int)(place.Value.xMin * w); x < (int)(place.Value.xMax * w); x++)
                        {
                            sum += data[y * w + x];
                            n++;
                        }
                    if (n > 0) sum /= n;
                    line.Append(' ').Append(place.Key).Append(' ').Append(sum.r.ToString("0.000", CultureInfo.InvariantCulture))
                        .Append(", ").Append(sum.g.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ")
                        .Append(sum.b.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ")
                        .Append(sum.a.ToString("0.000", CultureInfo.InvariantCulture)).Append(';');
                }
                Debug.Log(line.ToString());
            }
        }

        // What lies under a few pixels of the view (a physics ray), and every light in the scene.
        private static void LogCellsAndLights(Raytracing.RtCamera rt)
        {
            // What lies at a few cells of tools' grid comparison (22 by 12 over x 6-94 %, y 8-72 %).
            // And at pixels of the 2560 by 1440 shot: the lawn before the runway, the runway, the lawn beyond.
            foreach (Vector2Int cell in new[] { new Vector2Int(21, 5), new Vector2Int(18, 5), new Vector2Int(2, 6),
                                                new Vector2Int(10, 7), new Vector2Int(14, 9), new Vector2Int(-500, 545),
                                                new Vector2Int(-500, 528), new Vector2Int(-500, 570), new Vector2Int(-500, 600) })
            {
                float vx = cell.x < 0 ? -cell.x / 2560f : 0.06f + 0.88f * (cell.x + 0.5f) / 22f;
                float vy = cell.x < 0 ? 1f - cell.y / 1440f : 1f - (0.08f + 0.64f * (cell.y + 0.5f) / 12f);
                Ray ray = rt.Camera.ViewportPointToRay(new Vector3(vx, vy, 0f));
                RaycastHit hit;
                string what = "nothing";
                if (Physics.Raycast(ray, out hit, 1e6f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Renderer renderer = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
                    Material material = renderer != null ? renderer.sharedMaterial : null;
                    what = hit.collider.name + " at " + hit.distance.ToString("0") + " m, "
                           + (material != null ? material.name + " (" + material.shader.name + ")" : "no renderer");
                }
                Debug.Log(Log.Tag + " Probe: light values, cell " + cell.x + "," + cell.y + ": " + what + ".");
            }
            foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                if (light.isActiveAndEnabled && light.intensity > 0f)
                    Debug.Log(Log.Tag + " Probe: light values, light '" + light.name + "' " + light.type + " intensity "
                              + light.intensity.ToString("0.00", CultureInfo.InvariantCulture) + " colour " + light.color
                              + " shadows " + light.shadows + " strength " + light.shadowStrength.ToString("0.00", CultureInfo.InvariantCulture)
                              + " mask " + light.cullingMask + " at " + light.transform.position + " forward " + light.transform.forward
                              + " parent " + (light.transform.parent != null ? light.transform.parent.name : "none")
                              + (light == rt.SunLightNow ? " (the raytracing's sun)" : "") + ".");
        }

        // Unity's ambient light up, to the side and down, and the sun and path of the raytraced camera.
        private static void LogAmbient(Raytracing.RtCamera rt)
        {
            var directions = new[] { Vector3.up, Vector3.right, Vector3.down };
            var ambient = new Color[3];
            UnityEngine.Rendering.SphericalHarmonicsL2 probe;
            LightProbes.GetInterpolatedProbe(rt.transform.position, null, out probe);
            probe.Evaluate(directions, ambient);
            Debug.Log(Log.Tag + " Probe: light values, ambient up " + ambient[0] + " side " + ambient[1] + " down " + ambient[2]
                      + ", RenderSettings.ambientLight " + RenderSettings.ambientLight + ", mode " + RenderSettings.ambientMode
                      + ", sun " + rt.TracedSunColour + ", path " + rt.Camera.actualRenderingPath + ".");
        }
    }
}
