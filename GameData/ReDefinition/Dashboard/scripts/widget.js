// The dashboard's flight panels, its link to the mod and its demo; it shows the scene pages (menu.js, ksc.js,
// editor.js) in their scenes and gives them window.Dashboard.
(function () {
	"use strict";
	// ReDefinition's dashboard port. It is fixed because the manifest has to name it.
	// In iCUE the widget is a local file and talks to the mod on this computer; opened from the mod's
	// own web server (a phone's browser) it talks back to where it came from, with the key from its
	// address.
	const served = location.protocol === "http:" || location.protocol === "https:";
	const URL = served ? "ws://" + location.host + "/" + location.search : "ws://localhost:8765/";
	// Outside iCUE the dashboard keeps its layout and is scaled to the screen; drags are measured in
	// layout pixels, so screen distances are divided by this.
	let uiScale = 1;

	// Panel arrangements: for one, two and three rows, the rows top to bottom, each its panels left to
	// right. A panel keeps its width; one in a row shorter than the widest grows to fill it.
	const PANEL_W = { ctl: 630, agpanel: 230, ballpanel: 758, flight: 560, thr: 300 };
	const PANEL_H = 677, GAP = 10, PAD = 10;
	// An arrangement with f applied to every row of its one-, two- and three-row forms.
	const copyRows = (a, f) => ({ 1: a[1].map(f), 2: a[2].map(f), 3: a[3].map(f) });
	// An arrangement mirrored left to right: every row's panels in reverse order.
	const mirror = (a) => copyRows(a, (r) => r.slice().reverse());
	const STANDARD = { 1: [["ctl", "agpanel", "ballpanel", "flight", "thr"]], 2: [["ctl", "agpanel", "thr"], ["ballpanel", "flight"]], 3: [["ballpanel"], ["flight", "thr"], ["ctl", "agpanel"]] };
	// Handheld: the navball at one end, then the buttons, the action groups, the flight data and the throttle.
	const HANDHELD = { 1: [["ballpanel", "ctl", "agpanel", "flight", "thr"]], 2: [["ballpanel", "flight"], ["ctl", "agpanel", "thr"]], 3: [["ballpanel"], ["ctl", "agpanel"], ["flight", "thr"]] };
	const PRESETS = { default: STANDARD, defaultMirrored: mirror(STANDARD), handheld: HANDHELD, handheldMirrored: mirror(HANDHELD) };
	const PRESET_NAMES = { default: "Default", defaultMirrored: "Default (Mirrored)", handheld: "Handheld", handheldMirrored: "Handheld (Mirrored)", custom: "Custom" };
	// Names from before 0.30.
	const OLD_PRESETS = { standard: "default", mirrored: "defaultMirrored", navleft: "handheld", navright: "handheldMirrored" };
	// The choice is kept per browser; in iCUE it comes from the widget's settings (panelLayout).
	const LAYOUT_KEY = "kspdashboard.layout";
	const layout = { preset: "default", rows: "auto", custom: null };
	if (served) try { Object.assign(layout, JSON.parse(localStorage.getItem(LAYOUT_KEY)) || {}); } catch (e) { /* no storage: the default */ }
	layout.preset = OLD_PRESETS[layout.preset] || layout.preset;
	// Keeps the layout choice in the browser's storage, where there is one.
	const saveLayout = () => { try { localStorage.setItem(LAYOUT_KEY, JSON.stringify(layout)); } catch (e) { /* kept for this visit only */ } };
	// The arrangement shown: the custom one once edited, else the chosen preset's (Default for an unknown one).
	const arrangement = () => (layout.preset === "custom" && layout.custom ? layout.custom : PRESETS[layout.preset] || STANDARD);
	// A row's width in layout pixels: its panels and the gaps between them.
	const rowWidth = (row) => row.reduce((s, p) => s + PANEL_W[p], 0) + GAP * (row.length - 1);
	// The layout size [width, height] of an arrangement's rows, the padding round them included.
	const sizeOf = (rows) => [Math.max(...rows.map(rowWidth)) + 2 * PAD, rows.length * PANEL_H + (rows.length - 1) * GAP + 2 * PAD];
	let shownRows = 1;

	// The standard row is the stylesheet's grid, which keeps the navball in the middle of the Xeneon
	// Edge; every other arrangement is placed here.
	function place(rows, w, h) {
		const app = document.getElementById("app"), grid = rows === STANDARD[1];
		app.classList.toggle("placed", !grid);
		for (const id in PANEL_W) document.getElementById(id).style.cssText = "";
		if (grid) return;
		const ph = (h - 2 * PAD - GAP * (rows.length - 1)) / rows.length;
		rows.forEach((row, r) => {
			const base = rowWidth(row) - GAP * (row.length - 1), grow = w - 2 * PAD - rowWidth(row);
			let x = PAD;
			for (const id of row) {
				const pw = PANEL_W[id] + (grow * PANEL_W[id]) / base;
				document.getElementById(id).style.cssText = "position:absolute;left:" + x + "px;top:" + (PAD + r * (ph + GAP)) + "px;width:" + pw + "px;height:" + ph + "px";
				x += pw + GAP;
			}
		});
	}

	// Lays the panels out: in iCUE the one-row arrangement in the widget's own size; in a browser the
	// arrangement scaled to the window and centred in it.
	function fitScreen() {
		const app = document.getElementById("app"), arr = arrangement();
		// The ball takes the size of its new room at the next frame, also while no data changes.
		dirty = true;
		if (!served) { shownRows = 1; place(arr[1], app.clientWidth, app.clientHeight); return; }
		// In a browser: the number of rows asked for, or with "auto" the one that comes out largest.
		let best = null;
		for (const n of layout.rows === "auto" ? [1, 2, 3] : [Number(layout.rows)]) {
			const [w, h] = sizeOf(arr[n]), s = Math.min(innerWidth / w, innerHeight / h);
			if (!best || s > best.s) best = { n, w, h, s };
		}
		uiScale = best.s; shownRows = best.n;
		app.style.cssText = "position:absolute;left:" + (innerWidth - best.w * uiScale) / 2 + "px;top:" + (innerHeight - best.h * uiScale) / 2 +
			"px;width:" + best.w + "px;height:" + best.h + "px;transform:scale(" + uiScale + ");transform-origin:0 0";
		place(arr[best.n], best.w, best.h);
	}
	addEventListener("resize", fitScreen);

	// iCUE: the arrangement from the widget's settings.
	function applyIcueSettings(payload) {
		// iCUE keeps each setting in a global variable of its name and announces a change with
		// onDataUpdated; the variable need not be a property of window, so it is read by its name.
		/* global panelLayout */
		let v = typeof panelLayout === "string" ? panelLayout : (payload && payload.settings && payload.settings.panelLayout) || null;
		v = OLD_PRESETS[v] || v;
		if (served || !v || !PRESETS[v] || v === layout.preset) return;
		layout.preset = v;
		fitScreen();
	}

	// In a browser: buttons for the arrangement and full screen, in the navball panel's right column.
	const fullscreenOk = served && (document.fullscreenEnabled || document.webkitFullscreenEnabled);
	// Whether the page shows in full screen, WebKit's prefixed API included.
	const isFullscreen = () => !!(document.fullscreenElement || document.webkitFullscreenElement);
	// Adds the LAYOUT and full-screen buttons to the navball panel's right column; nothing in iCUE.
	function addBrowserButtons() {
		if (!served) return;
		const col = document.querySelector("#ballpanel .side.right");
		// Appends one button to the column: its id, lens content, tooltip and click.
		const add = (id, html, title, onClick) => {
			const b = document.createElement("button");
			b.id = id; b.className = "pb tool"; b.title = title; b.innerHTML = '<span class="lens">' + html + "</span>";
			b.addEventListener("click", onClick);
			col.appendChild(b);
			return b;
		};
		add("layoutBtn", "LAYOUT", "Arrange the panels", () => openArranger(!document.getElementById("arranger")));
		if (fullscreenOk) {
			const fs = add("fsBtn", "&#x26F6;", "Full screen", () => {
				const el = document.documentElement;
				if (isFullscreen()) (document.exitFullscreen || document.webkitExitFullscreen).call(document);
				else (el.requestFullscreen || el.webkitRequestFullscreen).call(el);
			});
			// Lights the full-screen button as the state is, and fits the layout to the new size.
			const mark = () => { fs.classList.toggle("lit", isFullscreen()); fitScreen(); };
			document.addEventListener("fullscreenchange", mark);
			document.addEventListener("webkitfullscreenchange", mark);
		}
	}

	// The arranger: a bar over the dashboard with the presets and the number of rows. While it is
	// open, tapping two panels swaps them, and the arrows move the chosen one to the row above or below.
	let chosen = null;
	// Opens the arranger bar, or closes it with open false; called again after each change to draw its buttons anew.
	function openArranger(open) {
		const app = document.getElementById("app");
		let bar = document.getElementById("arranger");
		app.classList.toggle("arranging", open);
		document.getElementById("layoutBtn").classList.toggle("lit", open);
		if (!open) { chosen = null; markChosen(); if (bar) bar.remove(); return; }
		if (!bar) { bar = document.createElement("div"); bar.id = "arranger"; document.body.appendChild(bar); }
		const presets = Object.keys(PRESETS).concat(layout.custom ? ["custom"] : []);
		// One of the bar's buttons, its data attribute carrying what it sets; on lights it.
		const button = (attr, value, label, on) => "<button " + attr + '="' + value + '"' + (on ? ' class="on"' : "") + ">" + label + "</button>";
		bar.innerHTML =
			'<div class="grp">' + presets.map((p) => button("data-preset", p, PRESET_NAMES[p], layout.preset === p)).join("") + "</div>" +
			'<div class="grp"><span>Rows</span>' + ["auto", "1", "2", "3"].map((r) => button("data-rows", r, r === "auto" ? "Auto" : r, String(layout.rows) === r)).join("") + "</div>" +
			'<div class="grp"><span class="hint">' + (chosen ? "Tap another panel to swap, or move it to another row" : "Tap two panels to swap them") + "</span>" +
			(chosen && shownRows > 1 ? button("data-move", "-1", "&#x25B2;") + button("data-move", "1", "&#x25BC;") : "") +
			button("data-done", "1", "Done", true) + "</div>";
		bar.onclick = (e) => {
			const b = e.target.closest("button"); if (!b) return;
			if (b.dataset.preset) { layout.preset = b.dataset.preset; chosen = null; }
			else if (b.dataset.rows) { layout.rows = b.dataset.rows; chosen = null; }
			else if (b.dataset.move) { moveRow(Number(b.dataset.move)); return; }
			else { openArranger(false); return; }
			saveLayout(); fitScreen(); markChosen(); openArranger(true);
		};
	}
	// Editing starts from what is shown: the preset becomes the custom arrangement.
	function editable() {
		if (layout.preset !== "custom" || !layout.custom) {
			layout.custom = copyRows(arrangement(), (r) => r.slice());
			layout.preset = "custom";
		}
		return layout.custom[shownRows];
	}
	// The row and place of a panel in the rows, null when it is in none.
	function where(rows, id) { for (let r = 0; r < rows.length; r++) { const i = rows[r].indexOf(id); if (i >= 0) return [r, i]; } return null; }
	// A tap on a panel while arranging: chooses it, unchooses it on a second tap, or swaps it with the chosen one.
	function choosePanel(id) {
		if (!chosen || chosen === id) chosen = chosen === id ? null : id;
		else {
			const rows = editable(), [ra, ia] = where(rows, chosen), [rb, ib] = where(rows, id);
			rows[ra][ia] = id; rows[rb][ib] = chosen;
			chosen = null;
			saveLayout(); fitScreen();
		}
		markChosen(); openArranger(true);
	}
	// Moves the chosen panel to the end of the row above (dir -1) or below (1); a row's last panel stays.
	function moveRow(dir) {
		if (!chosen) return;
		const rows = editable(), [r, i] = where(rows, chosen), to = r + dir;
		if (to < 0 || to >= rows.length || rows[r].length === 1) return; // no row may be left empty
		rows[r].splice(i, 1); rows[to].push(chosen);
		saveLayout(); fitScreen(); openArranger(true);
	}
	// Outlines the chosen panel, and no other.
	function markChosen() { for (const id in PANEL_W) document.getElementById(id).classList.toggle("chosen", id === chosen); }
	// While arranging, a tap on a panel chooses it instead of working its controls.
	function arrangeTaps() {
		const app = document.getElementById("app");
		// Whether the arranger is open.
		const arranging = () => app.classList.contains("arranging");
		// Swallows a touch on the panels while arranging, so no control under it works.
		const block = (e) => { if (arranging()) { e.stopPropagation(); if (e.type !== "pointerup") e.preventDefault(); } };
		for (const t of ["pointerdown", "pointermove", "touchstart", "touchmove", "mousedown", "wheel"]) app.addEventListener(t, block, { capture: true, passive: false });
		app.addEventListener("pointerup", (e) => {
			if (!arranging()) return;
			e.stopPropagation();
			const p = e.target.closest(".panel"); if (p) choosePanel(p.id);
		}, true);
		app.addEventListener("click", (e) => { if (arranging()) { e.stopPropagation(); e.preventDefault(); } }, true);
	}
	const RESOURCES = [["LiquidFuel", "L/F"], ["Oxidizer", "OX"], ["MonoPropellant", "MP"], ["ElectricCharge", "EC"], ["SolidFuel", "SRB"]];
	// SAS modes: the mode, its label, and the navball marker drawn on its button in its navball colour.
	const SAS = [["StabilityAssist", "STAB", "stab", "#e8ecef"], ["Prograde", "PRO", "pro", "#c6f000"], ["Retrograde", "RETRO", "retro", "#c6f000"],
		["Normal", "NORMAL", "normal", "#c28cff"], ["Antinormal", "ANTI-NRM", "anti", "#c28cff"], ["RadialIn", "RAD IN", "radin", "#5ee1ff"],
		["RadialOut", "RAD OUT", "radout", "#5ee1ff"], ["Target", "TARGET", "tgt", "#e05ce0"], ["AntiTarget", "ANTI-TGT", "atgt", "#e05ce0"], ["Maneuver", "MANEUVER", "mnvr", "#3d8bff"]];
	const TOGGLES = [["SAS", "f.sas", "f.sasEnabled"], ["RCS", "f.rcs", "v.rcsValue"], ["LIGHTS", "f.light", "v.lightValue"], ["GEAR", "f.gear", "v.gearValue"], ["BRAKES", "f.brake", "v.brakeValue"]];
	const PAUSE_TEXT = { 1: "GAME PAUSED", 5: "NOT IN FLIGHT" };
	const $ = (id) => document.getElementById(id);
	// Updates asked of the mod: one every 16 ms, as many as a 60 Hz screen can show.
	const RATE_MS = 16;
	let ws, data = {}, dragging = false, throttleSent = 0, connected = false, dirty = true;

	// A finite number as it is, anything else (missing, NaN, text) as null.
	const num = (v) => (typeof v === "number" && isFinite(v) ? v : null);
	// Readouts as number and unit; the unit is drawn small.
	const lcd = (value, unit) => (unit ? value + '<span class="u">' + unit + "</span>" : value);
	// A distance as an LCD readout: whole metres, then km from 10 km on, Mm and Gm; "-" without one.
	function dist(m) {
		const v = num(m); if (v === null) return "-";
		const a = Math.abs(v);
		if (a >= 1e9) return lcd((v / 1e9).toFixed(2), "Gm");
		if (a >= 1e6) return lcd((v / 1e6).toFixed(2), "Mm");
		if (a >= 1e4) return lcd((v / 1e3).toFixed(2), "km");
		return lcd(Math.round(v), "m");
	}
	// A speed as an LCD readout: m/s with one decimal, km/s with two from 10 km/s on; "-" without one.
	function speed(v) { v = num(v); return v === null ? "-" : (Math.abs(v) >= 1e4 ? lcd((v / 1e3).toFixed(2), "km/s") : lcd(v.toFixed(1), "m/s")); }
	// A time span as [d] hh:mm:ss with Kerbin's six-hour days; empty for none or a negative one.
	function clock(s) {
		s = num(s); if (s === null || s < 0) return "";
		const d = Math.floor(s / 21600), h = Math.floor((s % 21600) / 3600), m = Math.floor((s % 3600) / 60), r = Math.floor(s % 60);
		// A clock field with a leading zero.
		const two = (x) => (x < 10 ? "0" : "") + x;
		return (d ? d + "d " : "") + two(h) + ":" + two(m) + ":" + two(r);
	}
	// Whether a value the mod sends means on: true, 1 or the text True.
	const truthy = (v) => v === true || v === 1 || v === "True" || v === "true";

	// Sends an object to the mod as JSON while the connection is open; otherwise it is dropped.
	function send(obj) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj)); }
	// Commands go to the mod, or without it to the demo rocket.
	function run(cmd) {
		if (connected) send({ run: [cmd] });
		else { const p = scenePage(); if (!p) demoRun(cmd); else if (p.demoRun) p.demoRun(cmd); }
	}

	// Builds the flight panels' controls from the tables above (resource tapes, toggles, SAS modes, action
	// groups, warp, extras) and wires them and the levers, once at start.
	function buildUi() {
		for (const [key, label] of RESOURCES) {
			const t = document.createElement("div");
			t.className = "tape"; t.dataset.res = key;
			t.innerHTML = '<div class="plate sm">' + label + '</div><div class="bar"><div class="fill"></div><div class="ticks"></div><div class="marks"></div></div><div class="v">-</div>';
			$("tapes").appendChild(t);
		}
		// Adds a button to parent: its class, content and tap; key names the data that lights it (data-k).
		const mk = (parent, cls, html, onTap, key) => {
			const b = document.createElement("button");
			b.className = cls; b.innerHTML = html;
			b.addEventListener("click", onTap);
			if (key) b.dataset.k = key;
			$(parent).appendChild(b);
			return b;
		};
		for (const [t, cmd, st] of TOGGLES) mk("toggles", "tog", '<span class="l"></span>' + t, () => run(cmd), st);
		for (const [mode, t, ic, color] of SAS) {
			const b = mk("sasmodes", "pb", '<span class="lens"><i class="sasicon"></i><span>' + t + "</span></span>", () => { run("f.sas[true]"); run("f.setSASMode[" + mode + "]"); }, "sas:" + mode);
			const url = "url(" + Navball.icon(ic, 96) + ")";
			const i = b.querySelector(".sasicon"); i.style.webkitMaskImage = url; i.style.maskImage = url; i.style.background = color;
		}
		for (let i = 1; i <= 10; i++) mk("groups", "pb", '<span class="led"></span><span class="lens">' + i + "</span>", () => run("f.ag" + i), "v.ag" + i + "Value");
		mk("warp", "pb", '<span class="lens">1&#xD7;</span>', () => run("t.timeWarp[0]"));
		mk("warp", "pb", '<span class="lens">&#x25C0;&#x25C0;</span>', () => run("t.timeWarp[" + Math.max(0, (data["t.currentRateIndex"] || 0) - 1) + "]"));
		mk("warp", "pb", '<span class="lens">&#x25B6;&#x25B6;</span>', () => run("t.timeWarp[" + Math.min(7, (data["t.currentRateIndex"] || 0) + 1) + "]"));
		const w = document.createElement("div"); w.className = "lcd"; w.id = "warpval"; $("warp").appendChild(w);
		// Views and the game; the lit ones show a state.
		const extra = (label, cmd, lit) => { const b = mk("extras", "pb", '<span class="lens">' + label + "</span>", () => run(cmd)); if (lit) b.dataset.lit = lit; return b; };
		extra("MAP", "m.toggleMap", "m.mapIsEnabled");
		extra("CAM", "c.nextMode");
		extra("PREC", "f.precisionControl", "v.precisionControlValue");
		extra("PHYS<br />WARP", "t.physicsToggle", "t.physics");
		extra("QUICK<br />SAVE", "f.quicksave");
		extra("CIRC<br />@ AP", "o.circularizeAp"); // a maneuver node that makes the orbit circular at apoapsis
		for (const b of document.querySelectorAll("[data-cmd]:not(.hold)")) b.addEventListener("click", () => run(b.dataset.cmd));
		holdButton($("stage"));
		// A click handler that moves the node's lead (the share of the burn before the node) by step percent, within 0 to 100.
		const lead = (step) => () => run("o.nodeLead[" + Math.max(0, Math.min(100, (num(data["o.nodeLead"]) ?? 50) + step)) + "]");
		$("leadDn").addEventListener("click", lead(-5));
		$("leadUp").addEventListener("click", lead(5));
		throttleLever();
		abortLever();
	}

	// Stage fires only when held, so a stray touch cannot launch or separate anything.
	function holdButton(b) {
		let timer = 0, t0 = 0, raf = 0; const prog = b.querySelector(".prog"); const HOLD = 450;
		// Ends a hold early: the timer and the bar's animation stop, the bar empties.
		const stop = () => { clearTimeout(timer); cancelAnimationFrame(raf); prog.style.width = "0"; };
		b.addEventListener("pointerdown", () => {
			t0 = performance.now();
			// Fills the bar by the share of HOLD held so far, once per animation frame.
			const tick = () => { prog.style.width = Math.min(100, ((performance.now() - t0) / HOLD) * 100) + "%"; raf = requestAnimationFrame(tick); };
			tick();
			timer = setTimeout(() => { stop(); run(b.dataset.cmd); }, HOLD);
		});
		for (const e of ["pointerup", "pointerleave", "pointercancel"]) b.addEventListener(e, stop);
	}

	// Abort in two steps: the clear cover is pushed up (it shuts again by itself after a while),
	// then the lever under it is pulled all the way down. A lever let go early springs back.
	function abortLever() {
		const box = $("abort"), cover = $("cover"), lever = $("alever");
		const SHUT_AFTER = 5000;
		let shut = 0, drag = null;
		// Shuts the cover again.
		const close = () => { box.classList.remove("open"); clearTimeout(shut); };
		// Keeps the cover open for SHUT_AFTER from now.
		const keepOpen = () => { clearTimeout(shut); shut = setTimeout(close, SHUT_AFTER); };
		cover.addEventListener("pointerdown", (e) => {
			if (box.classList.contains("open")) return;
			cover.setPointerCapture(e.pointerId);
			drag = { id: e.pointerId, y0: e.clientY, what: "cover" };
			box.classList.add("dragging");
		});
		cover.addEventListener("pointermove", (e) => {
			if (!drag || drag.id !== e.pointerId || drag.what !== "cover") return;
			const LIFT = cover.offsetHeight - 18;
			cover.style.transform = "translateY(" + Math.max(-LIFT, Math.min(0, (e.clientY - drag.y0) / uiScale)) + "px)";
		});
		// Lets go of the cover: lifted more than halfway it stays open, else it falls shut.
		const coverUp = (e) => {
			if (!drag || drag.id !== e.pointerId || drag.what !== "cover") return;
			const lifted = (drag.y0 - e.clientY) / uiScale > (cover.offsetHeight - 18) * 0.5;
			drag = null; box.classList.remove("dragging"); cover.style.transform = "";
			if (lifted) { box.classList.add("open"); keepOpen(); }
		};
		cover.addEventListener("pointerup", coverUp); cover.addEventListener("pointercancel", coverUp);

		// The lever rests below the lifted cover's edge (dashboard.css .alever) and is pulled to 20 px above the bottom.
		const TOP = 34, BOTTOM = 20;
		lever.addEventListener("pointerdown", (e) => {
			if (!box.classList.contains("open")) return;
			lever.setPointerCapture(e.pointerId);
			drag = { id: e.pointerId, y0: e.clientY, what: "lever" };
			box.classList.add("held"); keepOpen();
		});
		lever.addEventListener("pointermove", (e) => {
			if (!drag || drag.id !== e.pointerId || drag.what !== "lever") return;
			const travel = box.clientHeight - lever.offsetHeight - TOP - BOTTOM;
			const d = Math.max(0, Math.min(travel, (e.clientY - drag.y0) / uiScale));
			lever.style.top = TOP + d + "px";
			if (d >= travel - 2) {
				run("f.abort");
				drag = null; box.classList.remove("held"); lever.style.top = ""; close();
			}
		});
		// Lets go of the lever before the bottom: it springs back and nothing fires.
		const leverUp = (e) => {
			if (!drag || drag.id !== e.pointerId || drag.what !== "lever") return;
			drag = null; box.classList.remove("held"); lever.style.top = "";
		};
		lever.addEventListener("pointerup", leverUp); lever.addEventListener("pointercancel", leverUp);
	}

	// The throttle lever: its handle follows the finger anywhere along the slot.
	function setThrottleView(v) {
		const slot = $("thrtrack"), h = $("thrhandle");
		const travel = slot.clientHeight - h.offsetHeight;
		h.style.bottom = v * travel + "px";
		$("thrfill").style.height = Math.max(0, v * (slot.clientHeight - 16)) + "px";
		$("thrv").innerHTML = lcd(Math.round(v * 100), "%");
	}
	// Wires the throttle slot: a press or drag puts the lever where the finger is and sends the throttle, at
	// most every 80 ms.
	function throttleLever() {
		const slot = $("thrtrack");
		// Sets the throttle at the finger's height on the slot.
		const apply = (e) => {
			const r = slot.getBoundingClientRect(), hh = $("thrhandle").offsetHeight;
			const v = Math.min(1, Math.max(0, 1 - (e.clientY - r.top - hh / 2) / (r.height - hh)));
			setThrottleView(v); shown.delete("thr"); // the lever shows the game's value again once let go
			const now = performance.now();
			if (now - throttleSent > 80) { throttleSent = now; run("f.setThrottle[" + v.toFixed(3) + "]"); }
		};
		slot.addEventListener("pointerdown", (e) => { dragging = true; slot.setPointerCapture(e.pointerId); apply(e); });
		slot.addEventListener("pointermove", (e) => { if (dragging) apply(e); });
		slot.addEventListener("pointerup", (e) => { if (!dragging) return; dragging = false; apply(e); });
		slot.addEventListener("pointercancel", () => { dragging = false; });
	}

	// The page is only written where what it shows changes: every write of a glowing LCD makes the
	// browser lay out and paint it again, the same number or not.
	const shown = new Map(), els = {};
	// An element by id, looked up once and kept.
	const el = (id) => els[id] || (els[id] = $(id));
	// Writes an element's HTML when it differs from what it shows.
	function put(id, html) { if (shown.get(id) !== html) { shown.set(id, html); el(id).innerHTML = html; } }
	// Writes an element's text when it differs from what it shows.
	function putText(id, text) { if (shown.get(id) !== text) { shown.set(id, text); el(id).textContent = text; } }
	// Sets one style property of a node when it differs from the value last set under key.
	function putStyle(node, key, prop, value) { if (shown.get(key) !== value) { shown.set(key, value); node.style[prop] = value; } }
	let tapes = null, keyed = null, litButtons = null;

	// Writes the flight panels from the data: readouts, node, orbit, stage, resource tapes, throttle, lit
	// buttons and warp.
	function render() {
		const d = data, has = d["v.name"] !== undefined && d["v.name"] !== null;
		const note = PAUSE_TEXT[d["p.paused"]];
		el("app").classList.toggle("off", !has);
		putText("link", !connected ? "DEMO" : note ? note : "● LINK");
		el("link").classList.toggle("ok", connected && !note);
		put("alt", dist(d["v.altitude"]));
		put("spd", speed(d["v.surfaceVelocity"]));
		// Maneuver node: delta-v still to burn (bar: of the node's total), time to the node, burn time,
		// time to start the burn, and the share of the burn to lie before the node.
		const nodeDv = num(d["o.nodeDv"]), nodeTotal = num(d["o.nodeDvTotal"]);
		el("node").classList.toggle("none", nodeDv === null);
		putText("nodestate", nodeDv === null ? "· NO NODE" : "");
		put("nodedv", nodeDv === null ? "-" : lcd(nodeDv.toFixed(1), "m/s"));
		putStyle(el("dvfill"), "dvfill", "width", nodeDv !== null && nodeTotal ? Math.min(100, (nodeDv / nodeTotal) * 100).toFixed(1) + "%" : "0");
		// Countdowns read "T- [d] hh:mm:ss" as at apoapsis and periapsis, and "T+" once passed.
		const tsigned = (t) => (num(t) === null ? "-" : (t < 0 ? "T+ " : "T- ") + clock(Math.abs(t)));
		putText("nodet", tsigned(d["o.nodeTime"]));
		putText("nodeburn", num(d["o.nodeBurnTime"]) === null ? (nodeDv === null ? "-" : "N/A") : clock(d["o.nodeBurnTime"]));
		putText("nodestart", tsigned(d["o.nodeStartIn"]));
		put("nodelead", num(d["o.nodeLead"]) === null ? "-" : lcd(Math.round(d["o.nodeLead"]), "%"));
		put("vs", speed(d["v.verticalSpeed"]));
		put("agl", dist(d["v.heightFromTerrain"]));
		put("apa", dist(d["o.ApA"])); putText("tap", tsigned(d["o.timeToAp"]));
		put("pea", dist(d["o.PeA"])); putText("tpe", tsigned(d["o.timeToPe"]));
		put("gee", num(d["v.geeForce"]) === null ? "-" : lcd(d["v.geeForce"].toFixed(2), "g"));
		putText("met", clock(d["v.missionTime"]) ? "MET " + clock(d["v.missionTime"]) : "");
		put("hs", speed(d["v.surfaceSpeed"]));
		const stage = num(d["v.currentStage"]);
		putText("stagenum", stage === null ? "--" : (stage < 10 ? "0" : "") + stage);
		if (!tapes) tapes = [...document.querySelectorAll(".tape")].map((t) => ({ t, res: t.dataset.res, fill: t.querySelector(".fill"), marks: t.querySelector(".marks"), v: t.querySelector(".v") }));
		for (const { t, res, fill, marks, v } of tapes) {
			const cur = num(d["r.resource[" + res + "]"]), max = num(d["r.resourceMax[" + res + "]"]);
			const p = cur !== null && max ? Math.max(0, Math.min(100, (cur / max) * 100)) : 0;
			putStyle(fill, "tape:" + res, "height", "calc(" + p.toFixed(1) + "% - 6px)");
			// Where engines burning it run dry: a line at that level, on the fill's scale; none at the bottom.
			const dry = Array.isArray(d["r.engineDry[" + res + "]"]) && max ? d["r.engineDry[" + res + "]"] : [];
			const lines = dry.map((a) => (a / max) * 100).filter((m) => m >= 1 && m <= 100)
				.map((m) => '<i class="mark" style="bottom:calc(3px + (100% - 6px) * ' + (m / 100).toFixed(4) + ')"></i>').join("");
			if (shown.get("tapemarks:" + res) !== lines) { shown.set("tapemarks:" + res, lines); marks.innerHTML = lines; }
			const empty = truthy(d["r.engineEmpty[" + res + "]"]);
			if (shown.get("tapeempty:" + res) !== empty) { shown.set("tapeempty:" + res, empty); t.classList.toggle("empty", empty); }
			const text = max ? String(Math.round(p)) : "-";
			if (shown.get("tapev:" + res) !== text) { shown.set("tapev:" + res, text); v.textContent = text; }
			putStyle(t, "tapeshow:" + res, "display", max ? "" : "none"); // a resource the vessel does not carry takes no room
		}
		const thr = num(d["f.throttle"]);
		if (!dragging && thr !== null && thr >= 0 && thr <= 1 && shown.get("thr") !== thr) { shown.set("thr", thr); setThrottleView(thr); }
		if (!keyed) { keyed = [...document.querySelectorAll("[data-k]")]; litButtons = [...document.querySelectorAll("[data-lit]")]; }
		for (const b of keyed) {
			const k = b.dataset.k;
			if (k.startsWith("sas:")) b.classList.toggle("lit", truthy(d["f.sasEnabled"]) && d["f.sasMode"] === k.slice(4));
			else b.classList.toggle("on", truthy(d[k]));
		}
		for (const b of litButtons) b.classList.toggle("lit", truthy(d[b.dataset.lit]));
		put("warpval", num(d["t.currentRate"]) === null ? "-" : lcd(d["t.currentRate"], "&#xD7;"));
	}

	// The navball and its readout follow every update; the panels around it at most 30 times a second,
	// and only after new data, writing only what changed.
	function renderBall() {
		const d = data, has = d["v.name"] !== undefined && d["v.name"] !== null;
		const T = d["n.steerTarget"], U = d["n.steerTargetTop"], hasTarget = has && Array.isArray(T) && Array.isArray(U);
		// VIEW TGT with a target: the ball is seen from the target, and the vessel is a marker on it.
		const view = hasTarget && steerPrefs.targetView && Array.isArray(d["n.nose"]) && Array.isArray(d["n.top"])
			? { nose: T, right: cross(U, T), top: U, craft: { nose: d["n.nose"], top: d["n.top"] } }
			: { nose: d["n.nose"], right: d["n.right"], top: d["n.top"] };
		const att = has ? Navball.draw($("ball"), Object.assign(view, { prograde: d["n.prograde"], normal: d["n.normal"], radial: d["n.radial"], maneuver: d["n.maneuver"], target: T, targetTop: U })) : Navball.draw($("ball"), null);
		// With a navball target the readout shows the target's attitude, in white.
		const deg = 180 / Math.PI;
		const show = hasTarget
			? { heading: (Math.atan2(T[1], T[0]) * deg + 360) % 360, pitch: Math.asin(Math.max(-1, Math.min(1, T[2]))) * deg, roll: Math.atan2(cross(U, T)[2], U[2]) * deg }
			: att;
		// Fixed width: every number padded to its longest form (HDG 359, PIT -90, ROL -180), signed.
		const signed = (v, w) => (v < 0 ? "-" : "+") + String(Math.abs(Math.round(v))).padStart(w, "0");
		putText("hdg", (hasTarget ? "TGT " : "") + (show
			? "HDG " + String(Math.round(show.heading) % 360).padStart(3, "0") + " · PIT " + signed(show.pitch, 2) + " · ROL " + signed(show.roll, 3)
			: "HDG --- · PIT --- · ROL ----"));
		el("hdg").classList.toggle("target", hasTarget);
		putText("smode", (d["n.speedMode"] || "").toUpperCase());
	}

	// The page follows the game's scene: each scene page (menu.js, ksc.js, editor.js) in its scenes, the
	// flight panels in every other until those scenes have pages of their own; between two scenes the page
	// stays. Each page is subscribed to its own keys, all to these. Without the game, a page's demo
	// shows at its address (index.html#menu, #loading, #ksc, #vab).
	const COMMON = ["p.paused", "g.scene"];
	const PAGES = [MenuPage, KscPage, EditorPage];
	let page = null, pageKeys = [];
	// The scene page shown; null while the flight panels show.
	const scenePage = () => PAGES.find((p) => p.id === page) || null;
	// A page's keys may change while it shows, with its tab: then they are a function.
	const keysOf = (p) => (typeof p.keys === "function" ? p.keys() : p.keys);
	// Swaps the subscription to the shown page's present keys, for a page whose keys change with its tab.
	function resubscribe() {
		const p = scenePage();
		if (!connected || !p) return;
		const keys = keysOf(p);
		send({ "-": pageKeys, "+": keys });
		pageKeys = keys;
	}
	// The page the data calls for: by scene, the flight panels for scenes without a page, the page shown while
	// the scene is unknown or between two; without the game the demo the address asks for.
	function pageFor() {
		if (!connected) { const p = PAGES.find((x) => x.demos.includes(location.hash)); return p ? p.id : "flight"; }
		const scene = data["g.scene"];
		if (typeof scene !== "string" || scene === "LOADINGBUFFER") return page;
		const p = PAGES.find((x) => x.scenes.includes(scene));
		return p ? p.id : "flight";
	}
	// Shows a page and swaps the subscribed keys from the last page's to its own; the flight panels are
	// fitted to the screen anew.
	function showPage(next) {
		if (!next || next === page) return;
		page = next;
		document.body.dataset.page = page;
		const p = scenePage(), keys = p ? keysOf(p) : subscription();
		if (connected) send({ "-": pageKeys, "+": keys });
		pageKeys = connected ? keys : [];
		if (page === "flight") { fitScreen(); dirty = panelsDirty = true; }
	}

	// The keys the flight panels read.
	function subscription() {
		const keys = ["v.name", "v.body", "v.situationString", "v.altitude", "v.surfaceVelocity", "v.surfaceSpeed", "v.verticalSpeed", "v.heightFromTerrain", "o.ApA", "o.PeA", "o.timeToAp", "o.timeToPe", "v.geeForce", "v.missionTime", "v.currentStage", "f.throttle", "f.sasEnabled", "f.sasMode", "v.rcsValue", "v.lightValue", "v.gearValue", "v.brakeValue", "t.currentRate", "t.currentRateIndex", "n.nose", "n.right", "n.top", "n.prograde", "n.normal", "n.radial", "n.speedMode", "n.maneuver", "o.nodeDvTotal", "o.nodeDv", "o.nodeTime", "o.nodeBurnTime", "o.nodeStartIn", "o.nodeLead", "n.steerTarget", "n.steerTargetTop", "n.steerAltitude", "n.steerSCurve", "n.steerCurve", "n.steerArmed", "t.physics", "m.mapIsEnabled", "v.precisionControlValue"];
		for (let i = 1; i <= 10; i++) keys.push("v.ag" + i + "Value");
		for (const [r] of RESOURCES) keys.push("r.resource[" + r + "]", "r.resourceMax[" + r + "]", "r.engineDry[" + r + "]", "r.engineEmpty[" + r + "]");
		return keys;
	}

	// Connects to the mod, and again every 3 s while it cannot; asks for COMMON at RATE_MS.
	function connect() {
		try { ws = new WebSocket(URL); } catch (e) { setTimeout(connect, 3000); return; }
		// The page's keys follow once the scene is known.
		ws.onopen = () => { connected = true; data = {}; page = null; pageKeys = []; send({ "+": COMMON, rate: RATE_MS }); };
		ws.onmessage = (m) => { try { Object.assign(data, JSON.parse(m.data)); dirty = panelsDirty = true; } catch (e) { /* ignore malformed frame */ } };
		// No empty frame when a connection fails or ends: the demo loop takes over with its next tick.
		ws.onclose = () => { connected = false; setTimeout(connect, 3000); };
	}

	// While KSP is not connected the widget flies a made-up rocket, so the layout can be judged and
	// every control tried without the game: the commands it would send act on this demo instead.
	const WARP_RATES = [1, 5, 10, 50, 100, 1000, 10000, 100000], PHYS_RATES = [1, 2, 3, 4];
	const demo = {
		on: { "f.sasEnabled": true, "v.rcsValue": false, "v.lightValue": true, "v.gearValue": false, "v.brakeValue": false, "v.abortValue": false },
		sasMode: "Prograde", ag: [false, false, false, false, false, false, false, false, false, false],
		throttle: 0.6, warp: 0, stage: 4, fuel: 1, solid: 1, abortUntil: 0,
		nose: null, top: null, target: null, targetTop: null, altitude: null, curve: "s", last: 0,
	};
	// A vector scaled to length 1.
	const unit = (v) => { const l = Math.hypot(v[0], v[1], v[2]) || 1; return v.map((x) => x / l); };
	// Turns v towards to by at most maxRad, at an even pace at any angle (about the axis between
	// them; straight opposite, about axis). Blending the two vectors instead would hardly move v when
	// they point nearly opposite.
	const turnTowards = (v, to, maxRad, axis) => {
		const c = Math.max(-1, Math.min(1, dot3(v, to))), a = Math.acos(c);
		if (a <= maxRad || a < 1e-6) return to.slice();
		let k = cross(v, to), l = Math.hypot(k[0], k[1], k[2]);
		if (l < 1e-6) { k = axis || [0, 0, 1]; l = Math.hypot(k[0], k[1], k[2]); }
		k = k.map((x) => x / l);
		const kv = cross(k, v), kd = dot3(k, v), cs = Math.cos(maxRad), sn = Math.sin(maxRad);
		return unit(v.map((x, i) => x * cs + kv[i] * sn + k[i] * kd * (1 - cs)));
	};
	// Carries out a command on the demo rocket, as the mod would on the vessel.
	function demoRun(cmd) {
		const m = /^([^\[]+)(?:\[(.*)\])?$/.exec(cmd);
		if (!m) return;
		const name = m[1], args = m[2] ? m[2].split(",").map(Number) : [];
		const toggle = { "f.sas": "f.sasEnabled", "f.rcs": "v.rcsValue", "f.light": "v.lightValue", "f.gear": "v.gearValue", "f.brake": "v.brakeValue" }[name];
		if (toggle) { demo.on[toggle] = args.length ? !!args[0] || m[2] === "true" : !demo.on[toggle]; return; }
		if (name === "f.setSASMode") { demo.sasMode = m[2]; return; }
		if (/^f\.ag\d+$/.test(name)) { const i = +name.slice(4) - 1; demo.ag[i] = !demo.ag[i]; return; }
		if (name === "f.setThrottle") { demo.throttle = Math.max(0, Math.min(1, args[0])); return; }
		if (name === "f.throttleZero") { demo.throttle = 0; return; }
		if (name === "f.throttleFull") { demo.throttle = 1; return; }
		if (name === "t.timeWarp") { demo.warp = Math.max(0, Math.min((demo.physics ? PHYS_RATES : WARP_RATES).length - 1, args[0] | 0)); return; }
		if (name === "t.physicsToggle") { demo.physics = !demo.physics; demo.warp = 0; return; }
		if (name === "m.toggleMap") { demo.map = !demo.map; return; }
		if (name === "f.precisionControl") { demo.precision = !demo.precision; return; }
		if (name === "f.stage") { demo.stage = demo.stage > 0 ? demo.stage - 1 : 4; demo.fuel = 1; demo.solid = demo.stage === 4 ? 1 : 0; return; }
		if (name === "f.abort") { demo.abortUntil = performance.now() + 3000; demo.throttle = 0; return; }
		if (name === "v.steerTo") { demo.target = unit(args.slice(0, 3)); demo.targetTop = unit(args.slice(3, 6)); return; }
		if (name === "v.steerRoll") {
			if (!demo.target && demo.nose) { demo.target = demo.nose.slice(); demo.targetTop = demo.top.slice(); }
			if (demo.target) {
				const a = Math.sign(args[0]) * 45 * Math.PI / 180 * 0.05, r = cross(demo.targetTop, demo.target);
				demo.targetTop = unit(demo.targetTop.map((x, i) => x * Math.cos(a) + r[i] * Math.sin(a)));
			}
			return;
		}
		if (name === "v.steerAltitude") { demo.altitude = args[0]; return; }
		if (name === "v.steerCurve") { demo.curve = m[2]; return; }
		if (name === "o.nodeLead") { demo.lead = args[0]; return; }
		if (name === "v.steerArm") { demo.armed = !!args[0]; return; }
		if (name === "v.steerReset") { demo.target = demo.targetTop = demo.altitude = null; return; }
	}

	// The demo rocket's frame at the moment t (ms): attitude, orbit, node, resources and switches.
	function demoFrame(t) {
		const s = t / 1000, rad = Math.PI / 180, dt = Math.min(0.1, demo.last ? s - demo.last : 0);
		demo.last = s;
		// Without a target the demo rocket swings about by itself; with one it turns there at 20 degrees
		// a second and stays.
		if (demo.target && demo.nose && demo.armed !== false) {
			demo.nose = turnTowards(demo.nose, demo.target, 20 * rad * dt);
			const wantedTop = unit(demo.targetTop.map((x, i) => x - dot3(demo.targetTop, demo.nose) * demo.nose[i]));
			const top = turnTowards(demo.top, wantedTop, 20 * rad * dt, demo.nose); // rolls about the nose
			demo.top = unit(top.map((x, i) => x - dot3(top, demo.nose) * demo.nose[i]));
		} else {
			const h = (s * 12) % 360 * rad, p = (25 + 40 * Math.sin(s * 0.35)) * rad, r = 25 * Math.sin(s * 0.5) * rad;
			const r0 = [-Math.sin(h), Math.cos(h), 0];
			const t0 = [-Math.sin(p) * Math.cos(h), -Math.sin(p) * Math.sin(h), Math.cos(p)];
			demo.nose = [Math.cos(p) * Math.cos(h), Math.cos(p) * Math.sin(h), Math.sin(p)];
			demo.top = t0.map((v, i) => v * Math.cos(r) - r0[i] * Math.sin(r));
		}
		const f = demo.nose, top = demo.top, right = cross(top, f);
		// A direction off the nose by a, for the demo's markers.
		const off = (a) => unit([f[0] + a, f[1] - a * 0.5, f[2] + a * 0.3]);
		demo.fuel = Math.max(0, demo.fuel - demo.throttle * dt / 90);
		// Boosters lit with the first stage: liquid ones that run dry at 2/3 and 1/3 of the fuel, small
		// solid ones at half the solid fuel, the rest of the solid with the stage; each empty a while
		// before it would be dropped.
		demo.solid = demo.stage === 4 ? Math.max(0, demo.solid - dt / 40) : 0;
		// The marks (shares of the fuel left) at which boosters still burning run dry; emptyAt: whether one
		// ran dry within the last 6 % of the fuel, or the fuel is gone.
		const dryAt = (marks, left) => marks.filter((m) => m < left), emptyAt = (marks, left) => left === 0 || marks.some((m) => left <= m && left > m - 0.06);
		const liquid = [2 / 3, 1 / 3], solid = [1 / 2];
		const aborting = t < demo.abortUntil;
		const d = {
			"p.paused": 0, "v.name": aborting ? "Demo Rocket — ABORT" : "Demo Rocket", "v.body": "Kerbin", "v.situationString": "Sub-orbital (demo)",
			"v.altitude": 38000 + 30000 * Math.sin(s * 0.1), "v.surfaceVelocity": 1200 + 600 * Math.sin(s * 0.15),
			"v.verticalSpeed": 180 * Math.cos(s * 0.1), "v.surfaceSpeed": 1150 + 550 * Math.sin(s * 0.15), "v.heightFromTerrain": 37000 + 30000 * Math.sin(s * 0.1),
			"o.ApA": 82000, "o.PeA": -420000, "o.timeToAp": 95 - (s % 95), "o.timeToPe": 600 - (s % 600),
			"v.geeForce": 1 + 2 * demo.throttle, "v.missionTime": 300 + s, "v.currentStage": demo.stage, "f.throttle": demo.throttle,
			"f.sasMode": demo.sasMode, "t.currentRate": (demo.physics ? PHYS_RATES : WARP_RATES)[demo.warp], "t.physics": !!demo.physics, "m.mapIsEnabled": !!demo.map, "v.precisionControlValue": !!demo.precision, "t.currentRateIndex": demo.warp,
			"n.nose": f, "n.right": right, "n.top": top, "n.prograde": off(0.05 * Math.sin(s)), "n.normal": off(0.9), "n.radial": off(-0.8), "n.maneuver": off(-0.35), "o.nodeDvTotal": 2000, "o.nodeDv": 1646.8, "o.nodeTime": 234 - (s % 234), "o.nodeBurnTime": 67, "o.nodeStartIn": 234 - (s % 234) - 67 * (demo.lead ?? 50) / 100, "o.nodeLead": demo.lead ?? 50, "n.speedMode": "Orbit",
			"n.steerTarget": demo.target, "n.steerTargetTop": demo.targetTop, "n.steerAltitude": demo.altitude, "n.steerSCurve": demo.curve === "s", "n.steerCurve": demo.curve, "n.steerArmed": demo.armed !== false,
			"r.resource[LiquidFuel]": 1800 * demo.fuel, "r.resourceMax[LiquidFuel]": 1800,
			"r.resource[Oxidizer]": 2200 * demo.fuel, "r.resourceMax[Oxidizer]": 2200,
			"r.resource[MonoPropellant]": 90, "r.resourceMax[MonoPropellant]": 120,
			"r.resource[ElectricCharge]": 350 + 40 * Math.sin(s), "r.resourceMax[ElectricCharge]": 400,
			"r.resource[SolidFuel]": 600 * demo.solid, "r.resourceMax[SolidFuel]": demo.stage === 4 ? 600 : 0,
			"r.engineDry[LiquidFuel]": dryAt(liquid, demo.fuel).map((m) => 1800 * m), "r.engineDry[Oxidizer]": dryAt(liquid, demo.fuel).map((m) => 2200 * m),
			"r.engineDry[SolidFuel]": dryAt(solid, demo.solid).map((m) => 600 * m),
			"r.engineEmpty[LiquidFuel]": emptyAt(liquid, demo.fuel), "r.engineEmpty[Oxidizer]": emptyAt(liquid, demo.fuel),
			"r.engineEmpty[SolidFuel]": demo.stage === 4 && emptyAt(solid, demo.solid),
		};
		Object.assign(d, demo.on);
		for (let i = 1; i <= 10; i++) d["v.ag" + i + "Value"] = demo.ag[i - 1];
		return d;
	}

	buildUi();
	// The menu page sends its commands through the same connection.
	// The scene pages send their commands through the same connection, and read the data as it stands.
	window.Dashboard = { run, data: () => data, resubscribe, connected: () => connected };
	for (const p of PAGES) p.build();
	addBrowserButtons();
	arrangeTaps();
	fitScreen();
	// Two switches beside the ball, kept per browser. ROLL LOCK holds the target's roll against the
	// horizon while dragging (a drag over the zenith then turns the target round). VIEW TGT shows the
	// ball from the target, with the vessel as a marker on it: drags and taps then move the target as
	// the ball shows it, and a straight drag adds no roll while the vessel still lags behind.
	const STEER_KEY = "kspdashboard.steer";
	const steerPrefs = { rollLock: false, targetView: false };
	try { Object.assign(steerPrefs, JSON.parse(localStorage.getItem(STEER_KEY)) || {}); } catch (e) { /* no storage: the default */ }
	// Keeps the two switches in the browser's storage, where there is one.
	const saveSteerPrefs = () => { try { localStorage.setItem(STEER_KEY, JSON.stringify(steerPrefs)); } catch (e) { /* kept for this visit only */ } };
	setupSteering();
	setupAltitude();
	// Steering by touch. The navball is turned with the finger like a ball floating on water: the
	// spot under the finger stays under it, and the attitude that ends up in the middle of the ball
	// (direction and roll) is where the vessel turns and stays (the mod steers there). The two round
	// buttons under the ball turn that target about its axis while held. Two fingers do all three
	// axes at once: moving them together turns the ball like one finger, twisting them rolls. Input
	// is sent every 50 ms while a finger is down.
	const steer = { drag: null, rollL: false, rollR: false, active: false, lastSent: 0 };

	// A point on the screen as a direction from the ball's centre, in the ball's screen frame
	// (right, up, towards the viewer); points off the ball are pulled onto its rim.
	function onBall(x, y) {
		const r = $("ball").getBoundingClientRect();
		let sx = (x - (r.left + r.width / 2)) / (r.width / 2), sy = -(y - (r.top + r.height / 2)) / (r.height / 2);
		const len = Math.hypot(sx, sy);
		if (len > 1) { sx /= len; sy /= len; }
		return [sx, sy, Math.sqrt(Math.max(0, 1 - sx * sx - sy * sy))];
	}
	// The cross product of two 3-vectors.
	const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
	// The dot product of two 3-vectors.
	const dot3 = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

	// The roll as the readout shows it, against the horizon. Straight up or down it has no meaning;
	// topForRoll then keeps the turned top as it is.
	const rollOf = (T, U) => Math.atan2(cross(U, T)[2], U[2]);
	// The top that gives a roll at the nose T; straight up or down, fallback.
	function topForRoll(T, roll, fallback) {
		if (Math.abs(T[2]) > 0.995) return fallback;
		const k = T[2], z0 = [-k * T[0], -k * T[1], 1 - k * T[2]], l = Math.hypot(z0[0], z0[1], z0[2]);
		const z = z0.map((x) => x / l), r0 = cross(z, T), a = -roll;
		return z.map((x, i) => x * Math.cos(a) + r0[i] * Math.sin(a));
	}
	// The ball turned the finger (or the fingers' midpoint) from v0 to v1, and two fingers turned its
	// picture by phi counter-clockwise. What lies in the middle now (and what lies above it) was,
	// before, the middle (and the top) turned back by both; in north/east/up through the attitude at
	// the start. Returns the target's nose and top, six numbers. Turning the picture counter-clockwise
	// is what rolling right looks like on the navball. The top turns with the ball, so the target
	// goes over the zenith without turning round; with ROLL LOCK the roll is held instead.
	function dragTarget(d) {
		const turned = turnTarget(d, d.phi || 0);
		if (!steerPrefs.rollLock) return turned;
		const T = turned.slice(0, 3), U = turned.slice(3);
		const B = d.base, baseRoll = Math.abs(B.nose[2]) > 0.995 ? rollOf(T, U) : rollOf(B.nose, B.top);
		// A twist adds what it turned the target by; a drag alone adds nothing.
		let delta = 0;
		if (d.phi) { const s0 = turnTarget(d, 0); delta = rollOf(T, U) - rollOf(s0.slice(0, 3), s0.slice(3)); }
		return T.concat(topForRoll(T, baseRoll + delta, U));
	}
	// The target's nose and top the drag d gives, the picture turned by phi: what dragTarget sends without
	// ROLL LOCK.
	function turnTarget(d, phi) {
		const v0 = d.v0, v1 = onBall(d.x, d.y);
		let k = cross(v0, v1);
		const s = Math.hypot(k[0], k[1], k[2]), c = Math.max(-1, Math.min(1, dot3(v0, v1)));
		// Turns a direction in the ball's screen frame back by the drag's turn, then by phi about the line of sight.
		const back = (w) => {
			if (s > 1e-6) {
				const kn = k.map((x) => x / s), kc = cross(kn, w), kw = dot3(kn, w);
				w = w.map((x, i) => x * c - kc[i] * s + kn[i] * kw * (1 - c)); // Rodrigues, by minus the angle
			}
			return [w[0] * Math.cos(phi) + w[1] * Math.sin(phi), -w[0] * Math.sin(phi) + w[1] * Math.cos(phi), w[2]];
		};
		// The turn is applied to the target the mod holds (or to the present attitude), so dragging
		// again moves an existing target on from where it is. It turns about the axes the ball is seen
		// from (d.axes): the vessel's, or with VIEW TGT the target's own.
		const a = d.axes, world = (w) => [0, 1, 2].map((i) => w[0] * a.right[i] + w[1] * a.top[i] + w[2] * a.nose[i]);
		// A world direction in the frame the ball is seen from (right, top, nose).
		const screen = (v) => [dot3(v, a.right), dot3(v, a.top), dot3(v, a.nose)];
		return world(back(screen(d.base.nose))).concat(world(back(screen(d.base.top))));
	}

	// A short tap on the ball: the direction under the finger becomes the target. The target (or the
	// vessel's attitude) is turned the shortest way onto it, so its roll carries over; with ROLL LOCK
	// its roll against the horizon does.
	const TAP_MS = 300, TAP_PX = 10;
	// The target's nose and top a tap at d.x0, d.y0 gives.
	function tapTarget(d) {
		const a = d.axes, w = onBall(d.x0, d.y0);
		const to = [0, 1, 2].map((i) => w[0] * a.right[i] + w[1] * a.top[i] + w[2] * a.nose[i]);
		const from = d.base.nose;
		let k = cross(from, to);
		const s = Math.hypot(k[0], k[1], k[2]), c = Math.max(-1, Math.min(1, dot3(from, to)));
		let top = d.base.top;
		if (s > 1e-6) {
			k = k.map((x) => x / s);
			const kc = cross(k, top), kt = dot3(k, top);
			top = top.map((x, i) => x * c + kc[i] * s + k[i] * kt * (1 - c)); // Rodrigues, by the angle
		}
		return to.concat(steerPrefs.rollLock ? topForRoll(to, rollOf(d.base.nose, d.base.top), top) : top);
	}

	// Sets the target's roll (as the readout shows it) to what `to` makes of the present one, in radians
	// (snapRoll and levelRoll pass theirs); without a target the vessel's attitude is the one turned.
	function setRoll(to) {
		const has = Array.isArray(data["n.steerTarget"]) && Array.isArray(data["n.steerTargetTop"]);
		const T = has ? data["n.steerTarget"] : data["n.nose"], U = has ? data["n.steerTargetTop"] : data["n.top"];
		if (!Array.isArray(T) || !Array.isArray(U)) return;
		const roll = to(Math.atan2(cross(U, T)[2], U[2]));
		// The top at no roll: towards the zenith (towards north when the nose points straight up or down).
		const ref = Math.abs(T[2]) < 0.999 ? [0, 0, 1] : [1, 0, 0];
		const k = dot3(ref, T), z0 = ref.map((x, i) => x - k * T[i]), l = Math.hypot(z0[0], z0[1], z0[2]);
		const z = z0.map((x) => x / l), r0 = cross(z, T);
		const a = -roll; // the readout's roll turns the other way round than this angle
		const top = z.map((x, i) => x * Math.cos(a) + r0[i] * Math.sin(a));
		run("v.steerTo[" + T.concat(top).map((x) => x.toFixed(4)).join(",") + "]");
	}
	// Snaps the target's roll to the nearest 45 degrees.
	function snapRoll() { setRoll((roll) => Math.round(roll / (Math.PI / 4)) * (Math.PI / 4)); }
	// Levels the target's roll to 0.
	function levelRoll() { setRoll(() => 0); }

	// Wires the steering by touch: drags and taps on the ball, the roll buttons, snap, level, ROLL LOCK and
	// VIEW TGT.
	function setupSteering() {
		const wrap = $("ballwrap"), dot = $("dragdot");
		// Shows the drag dot under the finger, or under the two fingers' midpoint.
		const showDot = (e) => {
			const r = $("ballpanel").getBoundingClientRect();
			dot.style.display = "block"; dot.style.left = e.clientX - r.left + "px"; dot.style.top = e.clientY - r.top + "px";
		};
		// One finger drags the ball. With a second finger the gesture starts afresh from the present
		// attitude: the midpoint of the two drags the ball, the angle between them rolls. After a
		// two-finger gesture nothing more happens until all fingers are off, so lifting one finger
		// makes no jump.
		const fingers = new Map();
		// The two fingers' midpoint and the angle between them.
		const pair = () => {
			const [a, b] = [...fingers.values()];
			return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, angle: Math.atan2(-(b.y - a.y), b.x - a.x) }; // angle with y up
		};
		// The attitude a gesture starts from: the vessel's, the target (or the vessel's attitude) as the base
		// to turn, and the axes the ball is seen from.
		const present = () => {
			const att = { nose: data["n.nose"].slice(), right: data["n.right"].slice(), top: data["n.top"].slice() };
			const has = Array.isArray(data["n.steerTarget"]) && Array.isArray(data["n.steerTargetTop"]);
			att.base = has ? { nose: data["n.steerTarget"].slice(), top: data["n.steerTargetTop"].slice() } : { nose: att.nose, top: att.top };
			att.axes = has && steerPrefs.targetView ? { nose: att.base.nose, top: att.base.top, right: cross(att.base.top, att.base.nose) } : att;
			return att;
		};
		wrap.addEventListener("pointerdown", (e) => {
			if (!Array.isArray(data["n.nose"]) || fingers.size >= 2) return;
			wrap.setPointerCapture(e.pointerId);
			fingers.set(e.pointerId, { x: e.clientX, y: e.clientY });
			if (fingers.size === 1 && !steer.pairDone) {
				steer.drag = { id: e.pointerId, v0: onBall(e.clientX, e.clientY), att: present(), x: e.clientX, y: e.clientY, x0: e.clientX, y0: e.clientY, t0: performance.now() };
				steer.drag.base = steer.drag.att.base; steer.drag.axes = steer.drag.att.axes;
				showDot(e);
			} else if (fingers.size === 2) {
				const m = pair();
				steer.drag = { id: null, v0: onBall(m.x, m.y), att: present(), x: m.x, y: m.y, a0: m.angle, phi: 0 };
				steer.drag.base = steer.drag.att.base; steer.drag.axes = steer.drag.att.axes;
				showDot({ clientX: m.x, clientY: m.y });
			}
		});
		wrap.addEventListener("pointermove", (e) => {
			const f = fingers.get(e.pointerId);
			if (!f || !steer.drag) return;
			f.x = e.clientX; f.y = e.clientY;
			if (steer.drag.id === null && fingers.size === 2) {
				const m = pair();
				steer.drag.x = m.x; steer.drag.y = m.y;
				let phi = m.angle - steer.drag.a0;
				while (phi > Math.PI) phi -= 2 * Math.PI;
				while (phi < -Math.PI) phi += 2 * Math.PI;
				steer.drag.phi = phi;
				showDot({ clientX: m.x, clientY: m.y });
			} else if (e.pointerId === steer.drag.id) { steer.drag.x = e.clientX; steer.drag.y = e.clientY; showDot(e); }
		});
		// A finger lifted: a short, still tap sets the target under it; the drag ends with its finger, a
		// two-finger drag with either.
		const end = (e) => {
			if (!fingers.delete(e.pointerId)) return;
			if (steer.drag && (steer.drag.id === null || e.pointerId === steer.drag.id)) {
				if (steer.drag.id === null) steer.pairDone = true;
				const dr = steer.drag;
				if (dr.id !== null && e.type === "pointerup" && performance.now() - dr.t0 < TAP_MS && Math.hypot(e.clientX - dr.x0, e.clientY - dr.y0) < TAP_PX)
					run("v.steerTo[" + tapTarget(dr).map((x) => x.toFixed(4)).join(",") + "]");
				steer.drag = null; dot.style.display = "none";
			}
			if (fingers.size === 0) steer.pairDone = false;
		};
		wrap.addEventListener("pointerup", end); wrap.addEventListener("pointercancel", end);
		$("snapBtn").addEventListener("click", snapRoll);
		$("levelBtn").addEventListener("click", levelRoll);
		// Lights ROLL LOCK and VIEW TGT as they are set, and names the view.
		const showPrefs = () => {
			$("lockBtn").classList.toggle("lit", steerPrefs.rollLock);
			$("viewBtn").classList.toggle("lit", steerPrefs.targetView);
			$("viewName").textContent = steerPrefs.targetView ? "TGT" : "CRAFT";
		};
		$("lockBtn").addEventListener("click", () => { steerPrefs.rollLock = !steerPrefs.rollLock; saveSteerPrefs(); showPrefs(); });
		$("viewBtn").addEventListener("click", () => { steerPrefs.targetView = !steerPrefs.targetView; saveSteerPrefs(); showPrefs(); dirty = true; });
		showPrefs();
		for (const key of ["rollL", "rollR"]) {
			const b = $(key);
			b.addEventListener("pointerdown", (e) => { b.setPointerCapture(e.pointerId); steer[key] = true; b.classList.add("on"); });
			// The roll button let go.
			const off = () => { steer[key] = false; b.classList.remove("on"); };
			b.addEventListener("pointerup", off); b.addEventListener("pointercancel", off);
		}
	}
	// Sends the drag's target and the held roll every 50 ms while they last, and v.steerLetGo once the drag ends.
	function steerTick(now) {
		const roll = (steer.rollR ? 1 : 0) - (steer.rollL ? 1 : 0);
		const dragging = steer.drag !== null;
		if ((dragging || roll !== 0) && now - steer.lastSent >= 50) {
			steer.lastSent = now;
			if (dragging) run("v.steerTo[" + dragTarget(steer.drag).map((x) => x.toFixed(4)).join(",") + "]");
			if (roll !== 0) run("v.steerRoll[" + roll + "]");
		}
		if (!dragging && steer.wasDragging) run("v.steerLetGo");
		steer.wasDragging = dragging;
	}

	// The target altitude: by it the turn to the next target is done. The slider beside the ball
	// springs back to the middle when let go; pushed up the altitude steps up, pulled down it steps
	// down, stepping as described at JOG_TICK below. How far it is pushed sets the size of
	// the steps, not their pace: a little gives fine steps, halfway ten times larger, near the end a
	// hundred times; each step lands on the grid of its size, so round values are hit exactly.
	// Fine steps are 100 m below 100 km, 1 km below 1000 km and 10 km above. Until it is moved the
	// number is the present altitude, and a target is turned to straight away.
	// Like a held key: pushing the slider out of the middle gives one step at once; held, one step
	// every JOG_TICK. Steps never come closer than JOG_TICK, also not from quick pushes one after
	// another or from moving into another zone.
	const JOG_DEAD = 0.12, JOG_MID = 0.45, JOG_COARSE = 0.8, JOG_TICK = 250;
	let lastStepAt = -1e9;
	const jog = { id: null, y0: 0, d: 0, value: null, last: 0, sent: 0, acc: 0, zone: 0 };
	// The fine step at an altitude: 100 m below 100 km, 1 km below 1000 km, 10 km above.
	const fineStep = (m) => (m < 100000 ? 100 : m < 1e6 ? 1000 : 10000);
	// The slider's zone at a deflection: 0 in the middle, then fine steps, ten times and a hundred times them.
	const jogZone = (a) => (a < JOG_DEAD ? 0 : a < JOG_MID ? 1 : a < JOG_COARSE ? 2 : 3);
	// One step from altitude m towards dir at the zone's size: onto its grid where m is off it, else one
	// along it; never below 0.
	function stepAltitude(m, dir, zone) {
		const size = fineStep(dir > 0 ? m : Math.max(0, m - 1)) * Math.pow(10, zone - 1); // going down, the step below a boundary
		const onGrid = Math.abs(m / size - Math.round(m / size)) < 1e-6;
		if (!onGrid) return Math.max(0, (dir > 0 ? Math.ceil(m / size) : Math.floor(m / size)) * size);
		return Math.max(0, Math.round(m / size) * size + dir * size);
	}
	// A step size as the knob shows it: 100m, 10km.
	const stepLabel = (m) => (m >= 1000 ? m / 1000 + "km" : m + "m");
	// The target altitude as its steps show it: metres below 10 km, then km with one decimal, then whole km.
	// With step (metres) the digit that the next step changes is marked, as a cursor under it.
	function altText(m, step) {
		const v = num(m); if (v === null) return "-";
		const [text, unit, scale] = v < 10000 ? [String(Math.round(v)), "m", 1]
			: v < 100000 ? [(v / 1000).toFixed(1), "km", 1000] : [String(Math.round(v / 1000)), "km", 1000];
		if (!step) return lcd(text, unit);
		const dot = text.includes(".") ? text.indexOf(".") : text.length;
		let html = "";
		for (let i = 0; i < text.length; i++) {
			const ch = text[i];
			const place = i < dot ? Math.pow(10, dot - 1 - i) * scale : i > dot ? Math.pow(10, dot - i) * scale : 0;
			html += /[0-9]/.test(ch) && Math.abs(place - step) < 1e-6 ? '<span class="cur">' + ch + "</span>" : ch;
		}
		return lcd(html, unit);
	}
	// Wires the altitude slider, the curve switch, reset and play/pause.
	function setupAltitude() {
		const track = $("altjog"), knob = $("altknob");
		track.addEventListener("pointerdown", (e) => {

			track.setPointerCapture(e.pointerId);
			jog.id = e.pointerId; jog.y0 = e.clientY; jog.d = 0; jog.last = performance.now(); jog.next = 0; jog.zone = 0; jog.dir = 0;
			track.classList.add("held");
		});
		track.addEventListener("pointermove", (e) => {
			if (e.pointerId !== jog.id) return;
			// The knob travels to the ends of the track, whatever its length.
			const travel = Math.max(20, (track.clientHeight - knob.offsetHeight) / 2 - 4);
			const dy = Math.max(-travel, Math.min(travel, (e.clientY - jog.y0) / uiScale));
			knob.style.transform = "translateY(" + dy + "px)";
			jog.d = -dy / travel;
		});
		// The slider let go: it springs back to the middle, and the altitude reached is sent.
		const end = (e) => {
			if (e.pointerId !== jog.id) return;
			jog.id = null; jog.d = 0;
			knob.style.transform = ""; knob.textContent = "";
			track.classList.remove("held");
			if (jog.value !== null) run("v.steerAltitude[" + jog.value.toFixed(0) + "]");
		};
		track.addEventListener("pointerup", end); track.addEventListener("pointercancel", end);
		$("curveBtn").addEventListener("click", () => run("v.steerCurve[" + CURVE_NEXT[curveShown()] + "]"));
		$("resetBtn").addEventListener("click", () => { run("v.steerReset"); jog.value = null; });
		// Pause: a target can be set and adjusted without being flown; play flies it.
		$("playBtn").addEventListener("click", () => run("v.steerArm[" + (data["n.steerArmed"] === false ? 1 : 0) + "]"));
	}
	// Steps the altitude each frame while the slider is held, and shows it: the stepped value, the mod's
	// target or the present altitude.
	function altitudeTick(now) {
		const set = num(data["n.steerAltitude"]);
		if (jog.id !== null) {
			const zone = jogZone(Math.abs(jog.d)), dir = Math.sign(jog.d);
			let step = false;
			if (zone !== jog.zone || (zone && dir !== jog.dir)) {
				step = zone !== 0 && (jog.zone === 0 || dir !== jog.dir); // out of the middle: one step now
				jog.next = now + JOG_TICK;
				jog.zone = zone; jog.dir = dir;
			} else if (zone && now >= jog.next) { step = true; jog.next = now + JOG_TICK; }
			if (step && now - lastStepAt < JOG_TICK) { step = false; jog.next = lastStepAt + JOG_TICK; }
			if (step) lastStepAt = now;
			const base = jog.value !== null ? jog.value : set !== null ? set : num(data["v.altitude"]) || 0;
			jog.step = zone ? fineStep(dir > 0 ? base : Math.max(0, base - 1)) * Math.pow(10, zone - 1) : 0;
			$("altknob").textContent = zone ? stepLabel(jog.step) : "";
			let changed = false;
			if (step) {
				jog.value = stepAltitude(jog.value !== null ? jog.value : base, dir, zone);
				changed = true;
			}
			if (changed && now - jog.sent >= 50) { jog.sent = now; run("v.steerAltitude[" + jog.value.toFixed(0) + "]"); }
		} else if (set === null || (jog.value !== null && Math.abs(set - jog.value) < 1)) jog.value = null; // the mod has it (or reset it)
		const shown = jog.value !== null ? jog.value : set !== null ? set : num(data["v.altitude"]);
		$("altval").innerHTML = jog.value !== null || set !== null || jog.id !== null ? altText(shown, jog.id !== null ? jog.step : 0) : dist(shown);
		$("altbox").classList.toggle("set", jog.value !== null || set !== null);
	}
	// The curve switch shows the curve the turn follows over the way to the target altitude.
	const CURVE_ICONS = {
		linear: '<svg viewBox="0 0 46 34"><path d="M5 29 L41 5" stroke="#fff" stroke-width="3.5" fill="none" stroke-linecap="round"/></svg>',
		s: '<svg viewBox="0 0 46 34"><path d="M5 29 C25 29 21 5 41 5" stroke="#fff" stroke-width="3.5" fill="none" stroke-linecap="round"/></svg>',
		// Auto: the gravity turn, steep at first and flattening out, with an A for its own choice of shape.
		auto: '<svg viewBox="0 0 46 34"><path d="M5 29 C7 12 18 6 41 5" stroke="#fff" stroke-width="3.5" fill="none" stroke-linecap="round"/><text x="34" y="31" fill="#ffb23e" font-size="13" font-weight="700" font-family="Segoe UI, Arial" text-anchor="middle">A</text></svg>',
	};
	const CURVE_TITLES = { linear: "Linear", s: "S-curve", auto: "Auto: gravity turn for the climb" };
	const CURVE_NEXT = { linear: "s", s: "auto", auto: "linear" };
	// A mod from before auto sends only whether the curve is an S-curve.
	const curveShown = () => (CURVE_ICONS[data["n.steerCurve"]] ? data["n.steerCurve"] : data["n.steerSCurve"] === false ? "linear" : "s");
	// Shows the play/pause button and the curve switch as the mod has them, writing them only on a change.
	function renderSteerButtons() {
		const playing = data["n.steerArmed"] !== false;
		const pb = $("playBtn");
		if (pb.dataset.mode !== String(playing)) {
			pb.dataset.mode = String(playing);
			pb.classList.toggle("playing", playing); pb.classList.toggle("paused", !playing);
			pb.innerHTML = '<span class="lens">' + (playing ? "&#x25B6;" : "&#x275A;&#x275A;") + "</span>";
			pb.title = playing ? "Steering to the target" : "Target paused";
		}
		const curve = curveShown();
		if ($("curveBtn").dataset.mode !== curve) {
			$("curveBtn").dataset.mode = curve;
			$("curveBtn").innerHTML = CURVE_ICONS[curve];
			$("curveBtn").title = CURVE_TITLES[curve];
		}
	}

	// Without WebGL the ball is drawn on the CPU, about 12 ms a frame: there it is kept to 15 frames
	// a second.
	let lastPanels = 0, lastBall = 0, panelsDirty = true;
	// One drawing loop at the display's pace: whatever arrived since the last frame is drawn once.
	function frame(now) {
		if (!connected) {
			const p = PAGES.find((x) => x.id === pageFor());
			data = p ? p.demo(now) : demoFrame(now);
			dirty = panelsDirty = true;
		}
		showPage(pageFor());
		const p = scenePage();
		if (p) {
			if (panelsDirty && now - lastPanels >= 33) { panelsDirty = false; lastPanels = now; p.render(data, connected); }
			requestAnimationFrame(frame);
			return;
		}
		steerTick(now);
		altitudeTick(now);
		const ballDue = Navball.mode($("ball")) === "gpu" || now - lastBall >= 66;
		if (dirty && ballDue) { dirty = false; lastBall = now; renderBall(); }
		if (panelsDirty && now - lastPanels >= 33) { panelsDirty = false; lastPanels = now; render(); renderSteerButtons(); }
		requestAnimationFrame(frame);
	}
	// iCUE draws widgets at 30 frames a second by default (iCUE.fpsLimit). Whether a widget may raise
	// it is not documented; the attempt costs nothing if it may not.
	try { if (window.iCUE && iCUE.fpsLimit < 60) iCUE.fpsLimit = 60; } catch (e) { /* read-only */ }
	requestAnimationFrame(frame);
	render();
	connect();
	// As iCUE's own template does it: by name, where iCUE may have declared the variable itself.
	const icueHandlers = { onICUEInitialized: applyIcueSettings, onDataUpdated: applyIcueSettings };
	/* global icueEvents: writable */
	try { icueEvents = icueHandlers; } catch (e) { /* not declared: the property below */ }
	window.icueEvents = icueHandlers;
	applyIcueSettings();
})();
