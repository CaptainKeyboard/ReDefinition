# Dashboard

A web dashboard for a phone, a browser or a Corsair iCUE touch screen: the mod serves its pages
and one WebSocket, sends the values the page asks for and runs the commands it sends. It is a
mod of its own, `ReDefinition.Dashboard.dll` in `GameData/ReDefinition/Dashboard`, that works
with or without ReDefinition and the planner
([docs/player/dashboard-on-its-own.md](../../docs/player/dashboard-on-its-own.md)).

## Start here

| File | Read it for |
|---|---|
| `src/Dashboard/DashboardServer.cs` | The add-on: starting the server, queued messages, and the per-client update loop. |
| `src/Dashboard/Telemetry.cs` | The keys a page can read and the commands it can run, by name. |
| `src/Dashboard/WebSocketServer.cs` | The WebSocket and HTTP server, the handshake and who is let in. |
| `src/Dashboard/DashboardSettings.cs` | The network switch and the key: the host's through `HostSeams`, or the dashboard's own file. |
| `src/Dashboard/DashboardStartup.cs` | The hook for ReDefinition's *Connect* button, and the launcher button where ReDefinition is absent. |
| `GameData/ReDefinition/Dashboard/index.html` | The page, and the order its scripts load in. |
| `GameData/ReDefinition/Dashboard/scripts/widget.js` | The flight panels, the connection to the mod and `window.Dashboard`. |
| `src/Dashboard/FlyByWire.cs` | Steering the active vessel from the page. |

## What it does

- `DashboardServer` listens on port 8765, on this computer or on the network, and sends each
  client the keys that changed.
- `Telemetry` holds the flight readers and commands. `GameTelemetry`, `MenuTelemetry`,
  `KscTelemetry`, `EditorTelemetry` and `FuelTelemetry` add the keys of the game, the menu, the
  space centre, the editors and the resource tapes.
- `WebSocketServer` also serves the files in `GameData/ReDefinition/Dashboard` over HTTP.
- `Json` writes and reads the little JSON the dashboard needs. `QrCode` and `ConnectDialog` show
  the address for a phone, in ReDefinition's settings window or in the dashboard's own dialog.
- `DashboardSettings` answers the switch and the key. With ReDefinition they are its settings;
  without it they are in `PluginData/settings.cfg` of the dashboard's folder.
- `FlyByWire` turns rates and a target attitude from the page into the vessel's controls.
- In the browser, `widget.js` shows the flight panels and `navball.js` draws the navball. The
  scene pages `menu.js`, `ksc.js` and `editor.js` share the helpers of `pages.js`.
- `manifest.json` describes the dashboard to iCUE and names the port it may talk to.

The path of a value and of a command is in
[The dashboard](../../docs/development/flow-dashboard.md).

## What it uses and what uses it

It uses Core and Ui, both in `ReDefinition.Common.dll`, and nothing else of ReDefinition. It
reads the planner only through `PlannerHooks`. ReDefinition reads it only through
`DashboardHooks.ConnectContent` and the seams. `ArchitectureTests` holds this.

## Rules of the area

- Network threads only queue messages. Everything that touches the game runs in
  `DashboardServer.Update` on the main thread (`src/Dashboard/DashboardServer.cs`).
- The port is fixed at `DashboardServer.Port`, because `manifest.json` has to name it.
- Where a key exists in both, the name and the bracket syntax are Telemachus'. The dashboard
  has 131 keys and Telemachus about 465, with 51 in common (`src/Dashboard/Telemetry.cs`).
- A client without the dashboard key gets in only from this computer, and only with no origin,
  the origin `null` or a `file://` origin (`WebSocketServer.OriginAllowed`).
- A rate from the page holds for `FlyByWire.HoldSeconds`, so a dropped connection never leaves the
  controls deflected (`src/Dashboard/FlyByWire.cs`).
- In `index.html`, `pages.js` loads before `menu.js`, `ksc.js` and `editor.js`, and `widget.js`
  loads last (`GameData/ReDefinition/Dashboard/index.html`).
- No type of `ReDefinition.dll` or of Harmony is named here. A new setting for the dashboard goes
  through `DashboardSettings` and a seam in `src/Common/HostSeams.cs`.
