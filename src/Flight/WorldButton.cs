using KSP.UI;
using KSP.UI.TooltipTypes;
using ReDefinition.Core;
using ReDefinition.Window;
using TMPro;
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
    // space centre's facility buttons (KscButtons) where those are held, else a
    // short name in KSP's font on a round plate. It goes with the add-on that made
    // it (Destroy).
    internal sealed class WorldButton
    {
        // The canvas's units: the button's size in them, and its pixels on the
        // screen at KSP's UI scale of 1.
        internal const float Size = 40f;
        internal const float MinPixels = 18f;
        internal const float MaxPixels = 44f;
        private static readonly Color Plate = new Color(0.13f, 0.13f, 0.16f, 0.9f);

        // GraphicRaycaster keeps its mask in a field of its own, without a setter
        // in this Unity (UnityEngine.UI.dll, decompiled).
        private static readonly System.Reflection.FieldInfo BlockingMask = typeof(GraphicRaycaster).GetField("m_BlockingMask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // The canvas in the world and its object; its size in metres; the button's rectangle, picture, fallback label,
        // Button and tooltip; its name, for the log; the space centre's button it was asked to look like, and whether it
        // shows that one rather than the fallback.
        private readonly GameObject root;
        private readonly Canvas canvas;
        private readonly float metres;
        private readonly RectTransform rect;
        private readonly Image face;
        private readonly TextMeshProUGUI label;
        private readonly Button button;
        private readonly TooltipController_Text tooltip;
        private readonly string name;
        private string look;
        private bool kscLook;

        // `metres`: how large it stands in the world.
        internal WorldButton(string name, UnityAction click, float metres)
        {
            this.metres = metres;
            this.name = name;
            root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            root.layer = 0;
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            GraphicRaycaster raycaster = root.GetComponent<GraphicRaycaster>();
            raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.ThreeD;
            // Only a vessel's parts (layer 0) take the click before it: the editors'
            // scenery has colliders the eye does not see between the camera and
            // the pad, and they took every click.
            if (BlockingMask != null) BlockingMask.SetValue(raycaster, (LayerMask)(1 << 0));
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

            GameObject text = UiHygiene.Child("Label", rect, typeof(RectTransform), typeof(TextMeshProUGUI));
            RectTransform textRect = (RectTransform)text.transform;
            textRect.anchorMin = textRect.anchorMax = textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.sizeDelta = new Vector2(Size - 8f, Size - 8f);
            label = text.GetComponent<TextMeshProUGUI>();
            TMP_FontAsset font = KspFont();
            if (font != null) label.font = font;
            label.enableAutoSizing = true;
            label.fontSizeMax = 13f;
            label.fontSizeMin = 6f;
            label.enableWordWrapping = false;
            label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(0.9f, 0.9f, 0.91f, 1f);
            label.raycastTarget = false;
            label.enabled = false;
            root.SetActive(false);
        }

        // KSP's interface font; null before its skin manager stands.
        private static TMP_FontAsset KspFont()
        {
            try
            {
                return UISkinManager.TMPFont;
            }
            catch (System.NullReferenceException)
            {
                return null;
            }
        }

        // The tooltip's text, set only where it changes.
        internal string Tooltip
        {
            set { if (tooltip != null && tooltip.textString != value) tooltip.textString = value; }
        }

        // The space centre's button of that name (Buttons_VAB, ...) with its states,
        // else `fallback`, a short name, on the round plate. What it took is logged
        // where that changes.
        internal void Look(string ksc, string fallback)
        {
            // Again once the space centre's button is held, where the fallback
            // stood till then.
            KscButtons.Look states = ksc != null ? KscButtons.Get(ksc) : null;
            if (look == ksc && face.sprite != null && (states == null || kscLook)) return;
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
                label.enabled = false;
                kscLook = true;
                Texture2D sheet = states.Normal.texture;
                Debug.Log(Log.Tag + " World button " + name + " shows KSP's sprite " + states.Normal.name + " (texture "
                          + (sheet != null ? sheet.name + ", " + sheet.width + "x" + sheet.height : "none") + ").");
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
                label.text = fallback ?? "";
                label.enabled = !string.IsNullOrEmpty(fallback);
                kscLook = false;
                Debug.Log(Log.Tag + " World button " + name + " shows the text \"" + fallback + "\": KSP's " + ksc
                          + " is not loaded (the space centre not shown since the game started).");
            }
            look = ksc;
        }

        // At the world point, brought `pull` metres towards the camera -- a
        // building's middle lies inside it -- and facing it; false, and hidden,
        // where it is behind the camera or would be too small to see. `standing`:
        // the point is under its lower edge, not its middle, so that it stands on
        // what is there rather than halfway in it. `solid`: colliders it must stand
        // before as a whole -- its middle and its four corners are looked at from
        // the camera, and it is brought nearer until none of them lies behind one;
        // its size on the screen stays. Hidden too while KSP's interface is (F2, or a
        // mod that hides it) -- it is interface, though it stands in the world --,
        // and where the player switched these buttons off.
        internal bool Place(Vector3 world, Camera camera, float pull, bool standing = false, Collider[] solid = null)
        {
            bool shown = false;
            if (camera != null && InterfaceShows())
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
                        float across = shownPixels * perPixel;
                        Vector3 middle = standing ? at + camera.transform.up * (across * 0.5f) : at;
                        float nearer = Before(solid, eye, middle, camera.transform, across * 0.5f);
                        middle = eye + (middle - eye) * nearer;
                        across *= nearer;
                        root.transform.position = middle;
                        root.transform.rotation = camera.transform.rotation;
                        root.transform.localScale = Vector3.one * (across / Size);
                        canvas.worldCamera = Eyes(camera);
                        shown = true;
                    }
                }
            }
            if (root.activeSelf != shown) root.SetActive(shown);
            return shown;
        }

        // KSP's own flag for whether its interface shows (UIMasterController
        // .isUIShowing), which F2 and onHideUI/onShowUI set and TUFX reads the same
        // way; where there is no controller, it shows. And the player's setting for
        // these buttons (OwnSettings.WorldButtons).
        private static bool InterfaceShows()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null && !addon.Live.WorldButtons) return false;
            UIMasterController ui = UIMasterController.Instance;
            return ui == null || ui.IsUIShowing;
        }

        // The camera inside the vessel (CameraManager: IVA, or Internal for a
        // part's interior): the main camera still draws the world through the
        // windows there, following the inside camera (CameraManager.SetCameraIVA
        // keeps FlightCamera enabled; decompiled), so a button in the world would
        // show and take clicks. The flight's buttons stand only in flight view.
        internal static bool FromInside()
        {
            CameraManager manager = CameraManager.Instance;
            if (manager == null) return false;
            CameraManager.CameraMode mode = manager.currentCameraMode;
            return mode == CameraManager.CameraMode.IVA || mode == CameraManager.CameraMode.Internal;
        }

        // How far along from the eye towards the button its middle and corners are
        // clear of `solid`, as a share of the way, with a margin; 1 where nothing
        // stands before them.
        private static float Before(Collider[] solid, Vector3 eye, Vector3 middle, Transform view, float half)
        {
            if (solid == null) return 1f;
            float share = 1f;
            for (int i = 0; i < 5; i++)
            {
                Vector3 point = middle;
                if (i > 0) point += view.right * (i % 2 == 0 ? half : -half) + view.up * (i < 3 ? half : -half);
                Vector3 way = point - eye;
                float length = way.magnitude;
                if (length <= 0f) continue;
                Ray look = new Ray(eye, way / length);
                foreach (Collider part in solid)
                {
                    RaycastHit hit;
                    if (part != null && !part.isTrigger && part.Raycast(look, out hit, length))
                        share = Mathf.Min(share, hit.distance / length);
                }
            }
            return share < 1f ? share * 0.97f : 1f;
        }

        // Hidden until it is placed again.
        internal void Hide()
        {
            if (root != null && root.activeSelf) root.SetActive(false);
        }

        // The camera that measures the clicks, one for every button (Eyes).
        private static Camera eyes;

        // A camera where `scene` stands, looking as it looks, on the screen, drawing
        // nothing: for the canvas's rays. It takes the scene camera's projection
        // too: KSP sets an off-centre one in the editors (CameraOffCenter, every
        // LateUpdate), and rays with a centred one missed the button to the side.
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
            eyes.projectionMatrix = scene.projectionMatrix;
            return eyes;
        }

        // The button goes with the add-on that made it.
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

        // The round plate of the fallback look, made once.
        private static Sprite disc;

        // A white disc, its edge smoothed over a texel.
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
}
