# Dashboard

A web dashboard for a phone, a browser or a Corsair iCUE touch screen: the mod serves its pages
and one WebSocket, sends the values the page asks for and runs the commands it sends.

## Start here

| File | Read it for |
|---|---|
| `src/Dashboard/DashboardServer.cs` | The add-on: starting the server, queued messages, and the per-client update loop. |
| `src/Dashboard/Telemetry.cs` | The keys a page can read and the commands it can run, by name. |
| `src/Dashboard/WebSocketServer.cs` | The WebSocket and HTTP server, the handshake and who is let in. |
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
  the address for a phone in the settings window.
- `FlyByWire` turns rates and a target attitude from the page into the vessel's controls.
- In the browser, `widget.js` shows the flight panels and `navball.js` draws the navball. The
  scene pages `menu.js`, `ksc.js` and `editor.js` share the helpers of `pages.js`.
- `manifest.json` describes the dashboard to iCUE and names the port it may talk to.

The path of a value and of a command is in
[The dashboard](../../docs/development/flow-dashboard.md).

## What it uses and what uses it

It uses Core, Engineer and Modules. Modules uses it.

## Rules of the area

- Network threads only queue messages. Everything that touches the game runs in
  `DashboardServer.Update` on the main thread (`src/Dashboard/DashboardServer.cs`).
- The port is fixed at `DashboardServer.Port`, because `manifest.json` has to name it.
- Key names and the bracket syntax follow Telemachus' API, so dashboards written for it keep
  working (`src/Dashboard/Telemetry.cs`).
- A client without the dashboard key gets in only from this computer, and only with no origin,
  the origin `null` or a `file://` origin (`WebSocketServer.OriginAllowed`).
- A rate from the page holds for `FlyByWire.HoldSeconds`, so a dropped connection never leaves the
  controls deflected (`src/Dashboard/FlyByWire.cs`).
- In `index.html`, `pages.js` loads before `menu.js`, `ksc.js` and `editor.js`, and `widget.js`
  loads last (`GameData/ReDefinition/Dashboard/index.html`).
