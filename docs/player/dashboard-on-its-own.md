# Use the dashboard without ReDefinition

ReDefinition Dashboard is the web dashboard of ReDefinition as a mod of its own. It serves
pages for a phone, a browser or a Corsair iCUE touch screen on port 8765 and sends them the
flight, the menu, the space centre and the editors. It needs neither HarmonyKSP nor ZTheme
nor ReDefinition. ReDefinition and ReDefinition Planner are recommended.

## Install it

1. Extract `ReDefinitionDashboard_<version>.zip` into the KSP folder, the one with
   `KSP_x64.exe`. `GameData/ReDefinition/Dashboard` and `GameData/ReDefinition/Common` go
   into `GameData`.
2. If you use CKAN, install *ReDefinition Dashboard* there. CKAN installs *ReDefinition
   Common* with it and suggests ReDefinition.

The full ReDefinition zip carries the same two folders. If you install ReDefinition, you do
not need this one.

## What it does without the others

| If this is missing | What changes |
|---|---|
| ReDefinition | The dashboard keeps two settings of its own and has a button of its own in the application launcher. Everything else of ReDefinition's interface is not there. |
| ReDefinition Planner | The pages show no planner rows: no guidance, no route keys and no planner in the editors. |
| HarmonyKSP, ZTheme | Nothing changes. |

## Open it

The dashboard listens on port 8765 of this computer from the start. Open
`http://localhost:8765` in a browser on this computer, or install `ksp-dashboard.icuewidget`
from `GameData/ReDefinition/Dashboard` in iCUE.

## Its two settings

The application launcher has a button with a phone on it, in the space centre, the editors,
the tracking station and flight. It opens a window with the settings and the QR code for a
phone. They are saved in the dashboard's own `PluginData/settings.cfg`.

| Setting | What it does |
|---|---|
| *Dashboard on other devices* | Phones and tablets in the same Wi-Fi may open the dashboard. Off, it opens on this computer only. |
| The key | A random key of eight characters, made at the first start. A device outside this computer needs it in the address. The QR code carries it. |

## Add ReDefinition later

Install ReDefinition as its [installing page](installing.md) says. The dashboard finds it at
the next start. Its two settings then move into ReDefinition's settings window, in the
*Devices* tab under *Dashboard*, and the launcher button disappears. The dashboard's own
settings file is not read any more and stays where it is.

ReDefinition does not need the dashboard either: without it, the *Devices* tab has no
*Dashboard* rows. The two share one small file, `ReDefinition.Common.dll`, in
`GameData/ReDefinition/Common`. If ReDefinition and the dashboard come from different
releases, update both to the same release. Extracting an older dashboard zip over a newer
ReDefinition replaces that one file with an older copy.
