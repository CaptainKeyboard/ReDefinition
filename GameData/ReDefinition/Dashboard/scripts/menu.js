// The dashboard's menu page (KSP loading, main menu), published as window.MenuPage for widget.js.
(function () {
	"use strict";
	// The menu page: KSP's progress while it loads at its start, and in the main menu the saves, the
	// last one played first, each loaded by holding its button. widget.js shows it in the scenes below,
	// subscribes its keys and hands it the data; commands go out through Dashboard.run.
	const { esc, num, whole, hold, holdButton, ago, minutes } = Pages;
	const SCENES = ["LOADING", "PSYSTEM", "MAINMENU", "SETTINGS", "CREDITS"];
	const KEYS = ["g.versions", "g.loading", "g.saves"];
	const SCENE_TEXT = { LOADING: "LOADING", PSYSTEM: "LOADING", MAINMENU: "MAIN MENU", SETTINGS: "KSP SETTINGS", CREDITS: "CREDITS" };
	const MODES = { CAREER: "Career", SCIENCE_SANDBOX: "Science", SANDBOX: "Sandbox", MISSION: "Mission", MISSION_BUILDER: "Mission Builder", SCENARIO: "Scenario", SCENARIO_NON_RESUMABLE: "Scenario", TUTORIAL: "Tutorial" };
	// Held this long, a button fires: no stray touch loads a save or quits the game.
	const HOLD_LOAD = 600, HOLD_QUIT = 1200;
	// A load asked for shows on its card until the scene changes, or the mod is taken to have refused it.
	const PENDING_MS = 10000;
	const $ = (id) => document.getElementById(id);
	let shownSaves, shownAt = 0, pending = null, lastScene = null;

	// What a save's summary holds, as the game mode has it: funds, reputation and contracts in a career,
	// science there and in science mode, vessels in every mode.
	function stats(s) {
		const career = s.mode === "CAREER", science = career || s.mode === "SCIENCE_SANDBOX";
		// One figure of a card: its plate label over the value on an LCD.
		const cell = (label, value) => '<div class="sstat"><div class="plate sm">' + label + '</div><div class="lcd">' + value + "</div></div>";
		let html = "";
		if (career) html += cell("Funds", whole(s.funds));
		if (science) html += cell("Science", whole(s.science));
		if (career) html += cell("Reputation", num(s.reputation) === null ? "-" : Math.round(s.reputation) + '<span class="u">%</span>');
		if (career) html += cell("Contracts", whole(s.contracts));
		if (s.mode) html += cell("Vessels", whole(s.vessels));
		return html;
	}

	// A save's card: name, mode, in-game date, its figures, when it was played, the KSP version it was
	// made with where that differs, and its hold button; big is the Continue card of the latest save.
	function card(s, big) {
		const mode = s.mode ? MODES[s.mode] || s.mode : null;
		return '<div class="msave' + (big ? " big" : "") + '">' +
			'<div class="mhead"><div class="mname">' + esc(s.name) + "</div>" + (mode ? '<div class="mmode">' + esc(mode) + "</div>" : "") + "</div>" +
			(s.date ? '<div class="mdate lcd">' + esc(s.date) + "</div>" : "") +
			(s.mode ? '<div class="mstats">' + stats(s) + "</div>" : '<div class="snote">No summary yet: KSP writes one when it lists the save in its Load dialog.</div>') +
			'<div class="mfoot"><span>Played ' + ago(s.played) + "</span>" + (s.version ? '<span class="mwarn">Made with KSP ' + esc(s.version) + "</span>" : "") + "</div>" +
			holdButton(big ? "CONTINUE" : "LOAD", ' data-load="' + esc(s.name) + '"') +
			"</div>";
	}

	// Lays out the saves, the latest as Continue and the rest below, and gives each button its hold to load.
	function renderSaves(list) {
		const latest = list[0];
		$("mcontinue").innerHTML = '<div class="plate title">Continue</div>' + (latest ? card(latest, true) : '<div class="snote">No saves yet.</div>');
		$("mlist").innerHTML = list.slice(1).map((s) => card(s, false)).join("") || '<div class="snote">No other saves.</div>';
		$("msavecount").textContent = list.length ? list.length + (list.length === 1 ? " save" : " saves") : "";
		for (const b of document.querySelectorAll("#menu [data-load]"))
			hold(b, HOLD_LOAD, () => {
				pending = { name: b.dataset.load, at: performance.now() };
				window.Dashboard.run("g.load[" + encodeURIComponent(b.dataset.load) + "]");
			});
	}

	// Draws the page from the frame's data: loading progress while KSP loads, else the saves and whether
	// their buttons may load now. connected is false in the demo.
	function render(d, connected) {
		const scene = d["g.scene"] || "MAINMENU";
		if (lastScene && lastScene !== scene) {
			// KSP is ready: a phone put aside while it loaded says so.
			if ((lastScene === "LOADING" || lastScene === "PSYSTEM") && scene === "MAINMENU" && navigator.vibrate) try { navigator.vibrate([120, 80, 120]); } catch (e) { /* not allowed before a tap */ }
			pending = null;
		}
		lastScene = scene;
		if (pending && performance.now() - pending.at > PENDING_MS) pending = null;

		const loading = scene === "LOADING" || scene === "PSYSTEM";
		$("menu").classList.toggle("loading", loading);
		$("mquit").disabled = scene !== "MAINMENU" || !!pending;
		$("mscene").textContent = SCENE_TEXT[scene] || scene;
		const v = d["g.versions"] || {};
		$("mversions").textContent = (v.ksp ? "KSP " + v.ksp : "") + (v.mod ? "  ·  ReDefinition " + v.mod : "");
		$("mlink").textContent = connected ? "● LINK" : "DEMO";
		$("mlink").classList.toggle("ok", connected);

		if (loading) {
			const l = d["g.loading"];
			// After its loaders KSP builds the solar system, the last step before the main menu.
			const fraction = l ? num(l.fraction) || 0 : scene === "PSYSTEM" ? 1 : 0;
			$("mloadstep").textContent = l && l.title ? l.title : scene === "PSYSTEM" ? "Building the solar system" : "Starting";
			$("mloadbar").style.width = (fraction * 100).toFixed(1) + "%";
			$("mloadpct").innerHTML = Math.floor(fraction * 100) + '<span class="u">%</span>';
			$("mloadtime").textContent = l ? minutes(l.seconds) : "";
			return;
		}

		const saves = d["g.saves"];
		// Again a minute on as well, for the time since each was played.
		if (Array.isArray(saves) && (saves !== shownSaves || Date.now() - shownAt > 60000)) { shownSaves = saves; shownAt = Date.now(); renderSaves(saves); }
		// Loading goes through the main menu: in KSP's settings or the credits the buttons wait.
		const canLoad = scene === "MAINMENU" && !pending;
		for (const b of document.querySelectorAll("#menu [data-load]")) {
			b.disabled = !canLoad;
			b.closest(".msave").classList.toggle("pending", !!pending && pending.name === b.dataset.load);
		}
		$("mhint").textContent = pending ? "Loading " + pending.name + " ..." : scene === "MAINMENU" ? "" : "Back in KSP's main menu, a save can be loaded from here.";
	}

	// Gives the quit button its hold, once when the page is set up.
	function build() {
		hold($("mquit"), HOLD_QUIT, () => window.Dashboard.run("g.quit"));
	}

	// Without the game, so the page can be judged in a browser: made-up saves (index.html#menu), or
	// KSP loading, the bar running over and over (index.html#loading).
	const DEMO_START = Date.now();
	const DEMOS = ["#menu", "#loading"];
	// The made-up frame for the demo at the moment now (ms), the loading bar cycling every 20 s.
	function demo(now) {
		const h = 3600 * 1000, loading = location.hash === "#loading", t = (now % 20000) / 20000;
		return {
			"g.scene": loading ? "LOADING" : "MAINMENU",
			"g.versions": { ksp: "1.12.5", mod: "0.1.3" },
			"g.loading": loading ? { title: t < 0.6 ? "Loading parts" : "Compiling scripts", fraction: 0.15 + 0.85 * t, seconds: 40 + now / 1000 } : null,
			"g.saves": demo.saves || (demo.saves = [
				{ name: "Career", played: DEMO_START - 0.3 * h, mode: "CAREER", ut: 17293, date: "Year 1, Day 1 - 4h, 48m", funds: 143626, science: 8, reputation: 9, contracts: 0, vessels: 0 },
				{ name: "Duna Direct", played: DEMO_START - 30 * h, mode: "CAREER", ut: 9e6, date: "Year 2, Day 146 - 1h, 12m", funds: 2501522, science: 1170, reputation: 64, contracts: 7, vessels: 12 },
				{ name: "Orbin", played: DEMO_START - 75 * h, mode: "SANDBOX", ut: 1970645, date: "Year 1, Day 236 - 2h, 24m", funds: 0, science: 0, reputation: 0, contracts: 0, vessels: 3 },
				{ name: "Science run", played: DEMO_START - 400 * h, mode: "SCIENCE_SANDBOX", ut: 935, date: "Year 1, Day 1 - 0h, 15m", funds: 0, science: 117, reputation: 0, contracts: 0, vessels: 1, version: "1.12.3" },
				{ name: "old", played: DEMO_START - 2000 * h },
			]),
		};
	}

	window.MenuPage = { id: "menu", scenes: SCENES, keys: KEYS, demos: DEMOS, build, render, demo };
})();
