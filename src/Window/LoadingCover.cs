using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // Covers other mods' IMGUI -- the Toolbar's buttons, MechJeb's menu tab and the
    // like -- while KSP shows its black screen between scenes. KSP shows it with a
    // camera of its own and hides its uGUI (LoadingBufferMask.Show: camera.enabled,
    // UIMasterController.HideUI; decompiled), which OnGUI does not follow: what the
    // mods draw there stayed on the black. Drawn over all IMGUI (GUI.depth) and
    // taking its clicks, in the screen's black; not where KSP writes on the black
    // screen (textInfo, a mission's loading).
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class LoadingCover : MonoBehaviour
    {
        private static readonly FieldInfo TextInfo = typeof(LoadingBufferMask).GetField("textInfo",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private LoadingBufferMask mask;
        private bool covering;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (mask == null) mask = FindObjectOfType<LoadingBufferMask>();
            bool cover = mask != null && mask.camera != null && mask.camera.enabled && !Writing();
            if (cover == covering) return;
            covering = cover;
            Debug.Log(Log.Tag + " Other mods' IMGUI " + (cover ? "covered on KSP's loading screen." : "shown again."));
        }

        private bool Writing()
        {
            TMPro.TextMeshPro text = TextInfo != null ? TextInfo.GetValue(mask) as TMPro.TextMeshPro : null;
            return text != null && text.gameObject.activeInHierarchy && !string.IsNullOrEmpty(text.text);
        }

        private void OnGUI()
        {
            if (!covering) return;
            GUI.depth = -10000;
            Rect screen = new Rect(0f, 0f, Screen.width, Screen.height);
            if (Event.current.type == EventType.Repaint)
            {
                Color was = GUI.color;
                GUI.color = Color.black;
                GUI.DrawTexture(screen, Texture2D.whiteTexture);
                GUI.color = was;
            }
            GUI.Button(screen, GUIContent.none, GUIStyle.none);
        }
    }
}
