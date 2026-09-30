using System.Collections;
using System.Collections.Generic;
using KSP.UI;
using KSP.UI.TooltipTypes;
using ReDefinition.Core;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // A button standing in the world, on a canvas of its own in world space that
    // the scene's camera draws with the rest (Canvas.renderMode WorldSpace, on the
    // layer of the vessel's parts): what stands before it, a vessel, a building,
    // the ground, hides it, and a click on it where a part stands before it goes
    // to the part (GraphicRaycaster.blockingObjects ThreeD). It turns to face the
    // camera and has a size in the world, so that it grows smaller as the camera
    // draws away; on the screen it is kept between MinPixels and MaxPixels, and
    // it goes where it would be smaller than MinPixels. Clicks are measured with a
    // camera of its own that follows the scene's and draws nothing (Eyes): the
    // upscaler has the scene's camera draw into a texture of the render size, and
    // a ray from the screen's pixels would miss there. It looks like one of the
    // space centre's facility buttons (KscButtons) where those have been seen,
    // else a symbol of ReDefinition's own on a round plate. It goes with the
    // add-on that made it (Destroy).
    internal sealed class WorldButton
    {
        // The canvas's units: the button's size in them, and its pixels on the
        // screen at KSP's UI scale of 1.
        internal const float Size = 40f;
        internal const float MinPixels = 18f;
        internal const float MaxPixels = 44f;
        private static readonly Color Plate = new Color(0.13f, 0.13f, 0.16f, 0.9f);

        private readonly GameObject root;
        private readonly Canvas canvas;
        private readonly float metres;
        private readonly RectTransform rect;
        private readonly Image face;
        private readonly Image glyph;
        private readonly Button button;
        private readonly TooltipController_Text tooltip;
        private string look;

        // `metres`: how large it stands in the world.
        internal WorldButton(string name, UnityAction click, float metres)
        {
            this.metres = metres;
            root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            root.layer = 0;
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            GraphicRaycaster raycaster = root.GetComponent<GraphicRaycaster>();
            raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.ThreeD;
            // Only a vessel's parts (layer 0) take the click before it: the editors'
            // scenery has colliders the eye does not see between the camera and
            // the pad, and they took every click.
            raycaster.blockingMask = 1 << 0;
            ((RectTransform)root.transform).sizeDelta = new Vector2(Size, Size);
            GameObject item = UiHygiene.Child("Button", root.transform, typeof(RectTransform), typeof(Image), typeof(Button));
            rect = (RectTransform)item.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(Size, Size);
            face = item.GetComponent<Image>();
            face.preserveAspect = true;
            button = item.GetComponent<Button>();
            button.targetGraphic = face;
            UiHygiene.MouseOnly(button);
            button.onClick.AddListener(click);
            tooltip = UiHygiene.Tooltip(item, "");
            if (tooltip != null) tooltip.continuousUpdate = true;

            GameObject symbol = UiHygiene.Child("Symbol", rect, typeof(RectTransform), typeof(Image));
            RectTransform symbolRect = (RectTransform)symbol.transform;
            symbolRect.anchorMin = symbolRect.anchorMax = symbolRect.pivot = new Vector2(0.5f, 0.5f);
            symbolRect.sizeDelta = new Vector2(Size - 14f, Size - 14f);
            glyph = symbol.GetComponent<Image>();
            glyph.preserveAspect = true;
            glyph.raycastTarget = false;
            root.SetActive(false);
        }

        internal string Tooltip
        {
            set { if (tooltip != null && tooltip.textString != value) tooltip.textString = value; }
        }

        // The space centre's button of that name (Buttons_VAB, ...) with its states,
        // else `fallback` on the round plate.
        internal void Look(string ksc, Sprite fallback)
        {
            if (look == ksc && face.sprite != null) return;
            KscButtons.Look states = KscButtons.Get(ksc);
            if (states != null)
            {
                face.sprite = states.Normal;
                face.color = Color.white;
                button.transition = Selectable.Transition.SpriteSwap;
                SpriteState swap = new SpriteState
                {
                    highlightedSprite = states.Highlighted,
                    pressedSprite = states.Pressed,
                    selectedSprite = states.Highlighted,
                    disabledSprite = states.Disabled,
                };
                button.spriteState = swap;
                glyph.enabled = false;
            }
            else
            {
                face.sprite = Disc();
                face.color = Plate;
                button.transition = Selectable.Transition.ColorTint;
                ColorBlock tint = button.colors;
                tint.highlightedColor = new Color(2f, 2f, 2.2f, 1f);
                tint.pressedColor = new Color(2.6f, 2.6f, 2.8f, 1f);
                tint.colorMultiplier = 1f;
                button.colors = tint;
                glyph.sprite = fallback;
                glyph.enabled = fallback != null;
            }
            look = ksc;
        }

        // At the world point, brought `pull` metres towards the camera -- a
        // building's middle lies inside it -- and facing it; false, and hidden,
        // where it is behind the camera or would be too small to see.
        internal bool Place(Vector3 world, Camera camera, float pull)
        {
            bool shown = false;
            if (camera != null)
            {
                Vector3 eye = camera.transform.position;
                Vector3 toEye = eye - world;
                float distance = toEye.magnitude;
                Vector3 at = distance > 0f ? world + toEye / distance * Mathf.Min(pull, distance * 0.5f) : world;
                float depth = Vector3.Dot(at - eye, camera.transform.forward);
                if (depth > 0f)
                {
                    // Metres per pixel at that depth.
                    float perPixel = 2f * depth * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
                    float ui = Mathf.Max(0.5f, GameSettings.UI_SCALE);
                    float pixels = metres / perPixel;
                    if (pixels >= MinPixels * ui)
                    {
                        float shownPixels = Mathf.Min(pixels, MaxPixels * ui);
                        root.transform.position = at;
                        root.transform.rotation = camera.transform.rotation;
                        root.transform.localScale = Vector3.one * (shownPixels * perPixel / Size);
                        canvas.worldCamera = Eyes(camera);
                        shown = true;
                    }
                }
            }
            if (root.activeSelf != shown) root.SetActive(shown);
            return shown;
        }

        internal void Hide()
        {
            if (root != null && root.activeSelf) root.SetActive(false);
        }

        private static Camera eyes;

        // A camera where `scene` stands, looking as it looks, on the screen, drawing
        // nothing: for the canvas's rays.
        private static Camera Eyes(Camera scene)
        {
            if (eyes == null)
            {
                GameObject holder = new GameObject("ReDefinitionWorldButtonEyes", typeof(Camera));
                Object.DontDestroyOnLoad(holder);
                eyes = holder.GetComponent<Camera>();
                eyes.enabled = false;
                eyes.cullingMask = 0;
                eyes.clearFlags = CameraClearFlags.Nothing;
            }
            eyes.transform.position = scene.transform.position;
            eyes.transform.rotation = scene.transform.rotation;
            eyes.fieldOfView = scene.fieldOfView;
            eyes.nearClipPlane = scene.nearClipPlane;
            eyes.farClipPlane = scene.farClipPlane;
            eyes.targetTexture = null;
            eyes.rect = new Rect(0f, 0f, 1f, 1f);
            return eyes;
        }

        internal void Destroy()
        {
            if (root != null) Object.Destroy(root);
        }

        // How far what a transform draws reaches from its middle.
        internal static float Reach(Transform place)
        {
            Renderer[] parts = place.GetComponentsInChildren<Renderer>(false);
            if (parts.Length == 0) return 0f;
            Bounds bounds = parts[0].bounds;
            foreach (Renderer part in parts) bounds.Encapsulate(part.bounds);
            return bounds.extents.magnitude;
        }

        private static Sprite disc;

        private static Sprite Disc()
        {
            if (disc != null) return disc;
            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color32[] pixels = new Color32[size * size];
            float c = (size - 1) / 2f, r = size / 2f - 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(r - d + 0.5f) * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            disc = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            return disc;
        }

        // The middle of what a transform draws, in the world; its own place where it
        // draws nothing.
        internal static Vector3 Middle(Transform place)
        {
            Renderer[] parts = place.GetComponentsInChildren<Renderer>(false);
            if (parts.Length == 0) return place.position;
            Bounds bounds = parts[0].bounds;
            foreach (Renderer part in parts) bounds.Encapsulate(part.bounds);
            return bounds.center;
        }
    }

    // The space centre's facility buttons (the column of buttons for the VAB, the
    // SPH, the launch pad, the runway and the rest), so that the buttons over the
    // buildings and in the editors look like them: KSP's own sprites, as their
    // Buttons show them and swap to (normal, highlighted, pressed, disabled),
    // held. They belong to the space centre's scene (Buttons_VAB_0 to _3 and so
    // on, sharedassets5) and are there only once it has been shown, which a game
    // loaded always is first; held here, Unity keeps them after it (it unloads
    // an asset only where nothing reaches it, static fields included:
    // Resources.UnloadUnusedAssets). A UI theme that replaces their textures
    // (HUDReplacer) shows here as well.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class KscButtons : MonoBehaviour
    {
        internal sealed class Look
        {
            internal Sprite Normal, Highlighted, Pressed, Disabled;
        }

        internal static readonly string[] Wanted = { "Buttons_VAB", "Buttons_SPH", "Buttons_Launchpad", "Buttons_Runway" };
        private static readonly Dictionary<string, Look> looks = new Dictionary<string, Look>();

        internal static Look Get(string name)
        {
            Look look;
            return looks.TryGetValue(name, out look) && look.Normal != null ? look : null;
        }

        // Taken a second and a half into the scene, where a theme has swapped the
        // pictures as the scene's UI stands; looked at again every second until all
        // four are there, for ten seconds.
        private float next;
        private float until;

        private void Awake()
        {
            next = Time.unscaledTime + 1.5f;
            until = next + 10f;
        }

        private void Update()
        {
            if (next <= 0f || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f;
            int found = 0;
            try
            {
                found = Capture();
            }
            catch (System.Exception e)
            {
                CompatibilityLog.Warn("ksc-buttons", "The space centre's facility buttons could not be taken (" + CompatibilityLog.Reason(e) + ").");
                next = 0f;
                return;
            }
            if (found < Wanted.Length && Time.unscaledTime < until) return;
            next = 0f;
            Debug.Log(Log.Tag + " The space centre's facility buttons taken for the buttons in the world: " + found + " of "
                      + Wanted.Length + ".");
        }

        private static int Capture()
        {
            Seen.Clear();
            foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
            {
                Image image = button != null ? button.targetGraphic as Image : null;
                if (image == null || image.sprite == null || !button.gameObject.scene.IsValid()) continue;
                string name = Base(image.sprite.name);
                if (name == null || Seen.Contains(name)) continue;
                Look look = new Look
                {
                    Normal = image.sprite,
                    Highlighted = button.spriteState.highlightedSprite,
                    Pressed = button.spriteState.pressedSprite,
                    Disabled = button.spriteState.disabledSprite,
                };
                if (look.Normal == null) continue;
                if (look.Highlighted == null) look.Highlighted = look.Normal;
                if (look.Pressed == null) look.Pressed = look.Highlighted;
                if (look.Disabled == null) look.Disabled = look.Normal;
                looks[name] = look;
                Seen.Add(name);
            }
            return Seen.Count;
        }

        private static readonly HashSet<string> Seen = new HashSet<string>();

        // "Buttons_VAB" for "Buttons_VAB_0"; null for another sprite.
        private static string Base(string sprite)
        {
            foreach (string wanted in Wanted)
                if (sprite.StartsWith(wanted + "_", System.StringComparison.Ordinal)) return wanted;
            return null;
        }
    }
}
