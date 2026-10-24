(function () {
	"use strict";
	// The menu page: KSP's progress while it loads at its start, and in the main menu the saves, the
	// last one played first, each loaded by holding its button. widget.js shows it in the scenes below,
	// subscribes its keys and hands it the data; commands go out through Dashboard.run.
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

	const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
	const num = (v) => (typeof v === "number" && isFinite(v) ? v : null);
	const whole = (v) => (num(v) === null ? "-" : Math.round(v).toLocaleString());
	function ago(ms) {
		const s = (Date.now() - ms) / 1000;
		if (s < 90) return "just now";
		if (s < 3600) return Math.round(s / 60) + " min ago";
		if (s < 86400) return Math.round(s / 3600) + " h ago";
		if (s < 2 * 86400) return "yesterday";
		if (s < 14 * 86400) return Math.round(s / 86400) + " days ago";
		return new Date(ms).toLocaleDateString();
	}
	const clock = (s) => { s = Math.max(0, Math.floor(num(s) || 0)); return Math.floor(s / 60) + ":" + String(s % 60).padStart(2, "0"); };

	// A button that fires once held for ms; its bar fills while it is held.
	function hold(b, ms, fire) {
		let timer = 0, raf = 0, t0 = 0;
		const prog = b.querySelector(".prog");
		const stop = () => { clearTimeout(timer); cancelAnimationFrame(raf); prog.style.width = "0"; };
		b.addEventListener("pointerdown", (e) => {
			if (b.disabled) return;
			b.setPointerCapture(e.pointerId);
			t0 = performance.now();
			const tick = () => { prog.style.width = Math.min(100, ((performance.now() - t0) / ms) * 100) + "%"; raf = requestAnimationFrame(tick); };
			tick();
			timer = setTimeout(() => { stop(); fire(); }, ms);
		});
		for (const e of ["pointerup", "pointercancel", "lostpointercapture"]) b.addEventListener(e, stop);
	}

	// What a save's summary holds, as the game mode has it: funds, reputation and contracts in a career,
	// science there and in science mode, vessels in every mode.
	function stats(s) {
		const career = s.mode === "CAREER", science = career || s.mode === "SCIENCE_SANDBOX";
		const cell = (label, value) => '<div class="mstat"><div class="plate sm">' + label + '</div><div class="lcd">' + value + "</div></div>";
		let html = "";
		if (career) html += cell("Funds", whole(s.funds));
		if (science) html += cell("Science", whole(s.science));
		if (career) html += cell("Reputation", num(s.reputation) === null ? "-" : Math.round(s.reputation) + '<span class="u">%</span>');
		if (career) html += cell("Contracts", whole(s.contracts));
		if (s.mode) html += cell("Vessels", whole(s.vessels));
		return html;
	}

	function card(s, big) {
		const mode = s.mode ? MODES[s.mode] || s.mode : null;
		return '<div class="msave' + (big ? " big" : "") + '" data-name="' + esc(s.name) + '">' +
			'<div class="mhead"><div class="mname">' + esc(s.name) + "</div>" + (mode ? '<div class="mmode">' + esc(mode) + "</div>" : "") + "</div>" +
			(s.date ? '<div class="mdate lcd">' + esc(s.date) + "</div>" : "") +
			(s.mode ? '<div class="mstats">' + stats(s) + "</div>" : '<div class="mnote">No summary yet: KSP writes one when it lists the save in its Load dialog.</div>') +
			'<div class="mfoot"><span>Played ' + ago(s.played) + "</span>" + (s.version ? '<span class="mwarn">Made with KSP ' + esc(s.version) + "</span>" : "") + "</div>" +
			'<button class="mhold" data-load="' + esc(s.name) + '"><b>' + (big ? "CONTINUE" : "LOAD") + '</b><span class="hint">HOLD</span><i class="prog"></i></button>' +
			"</div>";
	}

	function renderSaves(list) {
		const latest = list[0];
		$("mcontinue").innerHTML = '<div class="plate title">Continue</div>' + (latest ? card(latest, true) : '<div class="mnote">No saves yet.</div>');
		$("mlist").innerHTML = list.slice(1).map((s) => card(s, false)).join("") || '<div class="mnote">No other saves.</div>';
		$("msavecount").textContent = list.length ? list.length + (list.length === 1 ? " save" : " saves") : "";
		for (const b of document.querySelectorAll("#menu [data-load]"))
			hold(b, HOLD_LOAD, () => {
				pending = { name: b.dataset.load, at: performance.now() };
				window.Dashboard.run("g.load[" + encodeURIComponent(b.dataset.load) + "]");
			});
	}

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
			$("mloadtime").textContent = l ? clock(l.seconds) : "";
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

	function build() {
		hold($("mquit"), HOLD_QUIT, () => window.Dashboard.run("g.quit"));
	}

	// Without the game, so the page can be judged in a browser: made-up saves (index.html#menu), or
	// KSP loading, the bar running over and over (index.html#loading).
	const DEMO_START = Date.now();
	const DEMOS = ["#menu", "#loading"];
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

	window.MenuPage = { scenes: SCENES, keys: KEYS, demos: DEMOS, build, render, demo };
})();
