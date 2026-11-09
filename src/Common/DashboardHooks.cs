using System;

namespace ReDefinition.Core
{
    // Where the dashboard offers itself to the host. Null means the dashboard is not installed.
    // The game keeps one copy of Common, so these are shared by every mod that uses it. The dashboard assigns them at the start of the game.
    internal static class DashboardHooks
    {
        // The rows of the dialog that shows the dashboard's address for a phone (the QR code and the address), made afresh each time it opens.
        internal static Func<DialogGUIBase[]> ConnectContent;
    }
}
