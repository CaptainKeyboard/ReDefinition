using UnityEngine;

namespace ReDefinition.Dashboard
{
    // What the settings window's Connect button shows (OurModules.Dialogs): the QR code of the
    // dashboard's address, so that a phone on the same network opens it by scanning.
    internal static class ConnectDialog
    {
        // The last picture made; one dialog stands at a time, so the one before goes.
        private static Texture2D picture;

        // The dialog's rows: the QR code with the address under it, or a line saying why there is none.
        internal static DialogGUIBase[] Content()
        {
            OwnSettings settings = DashboardServer.Settings;
            if (settings == null || !settings.DashboardNetwork)
                return new DialogGUIBase[] { new DialogGUILabel("Opening the dashboard from other devices is switched off.", true) };
            // The address is looked up afresh each time: the computer may have joined another network.
            string url = DashboardServer.PhoneUrl;
            if (picture != null) Object.Destroy(picture);
            picture = url != null ? Picture(new QrCode(url)) : null;
            if (picture == null) return new DialogGUIBase[] { new DialogGUILabel("No network address found for this computer.", true) };
            return new DialogGUIBase[]
            {
                new DialogGUILabel("Scan with the phone's camera, in the same Wi-Fi:", true),
                new DialogGUIHorizontalLayout(new DialogGUIFlexibleSpace(), new DialogGUIImage(new Vector2(300, 300), Vector2.zero, Color.white, picture),
                    new DialogGUIFlexibleSpace()),
                new DialogGUILabel(url, true),
            };
        }

        // Black on white with a four-module quiet zone, scaled up without smoothing.
        private static Texture2D Picture(QrCode code)
        {
            const int quiet = 4, scale = 8;
            int n = (code.Size + 2 * quiet) * scale;
            var tex = new Texture2D(n, n, TextureFormat.RGB24, false) { filterMode = FilterMode.Point };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int mx = x / scale - quiet, my = code.Size - 1 - (y / scale - quiet); // textures run bottom up
                    bool dark = mx >= 0 && my >= 0 && mx < code.Size && my < code.Size && code[mx, my];
                    px[y * n + x] = dark ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
                }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
