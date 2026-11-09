// The dashboard's Space Center page, published as window.KscPage for widget.js.
(function () {
	"use strict";
	// The Space Center page: the program's funds, science and reputation, time warp and the crew; the
	// buildings, entered with a tap as with a click on them; the vessels, flown by holding FLY; the next
	// alarms, each warped to with a tap; the active contracts. Lists are drawn when they change, the
	// countdowns and mission times with every new g.ut.
	const { $, MODES, stat, putText, putClass, esc, num, whole, hold, holdButton, span, countdown } = Pages;
	const SCENES = ["SPACECENTER"];
	const KEYS = ["g.mode", "g.funds", "g.science", "g.reputation", "g.ut", "g.date", "g.calendar", "t.currentRate", "t.currentRateIndex",
		"k.buildings", "k.vessels", "k.alarms", "k.contracts", "k.crew"];
	// The buildings in the order they stand on the page: the facility, its short name and its full one.
	const BUILDINGS = [
		["VehicleAssemblyBuilding", "VAB", "Vehicle Assembly Building"], ["SpaceplaneHangar", "SPH", "Spaceplane Hangar"],
		["LaunchPad", "PAD", "Launch Pad"], ["Runway", "RUNWAY", "Runway"], ["TrackingStation", "TRACKING", "Tracking Station"],
		["AstronautComplex", "CREW", "Astronaut Complex"], ["ResearchAndDevelopment", "R&amp;D", "Research &amp; Development"],
		["MissionControl", "MISSION", "Mission Control"], ["Administration", "ADMIN", "Administration"],
	];
	const SITUATIONS = { LANDED: "Landed", SPLASHED: "Splashed", PRELAUNCH: "On the pad", FLYING: "Flying", SUB_ORBITAL: "Sub-orbital", ORBITING: "Orbiting", ESCAPING: "Escaping", DOCKED: "Docked" };
	const HOLD_FLY = 600, HOLD_LEAVE = 1200;
	const PENDING_MS = 10000;
	const shown = {};
	let pending = null;

	// The buildings' buttons in BUILDINGS' order, a tap entering one; destroyed ones marked, levels as pips
	// in a career. Buildings the mod does not send are left out.
	function renderBuildings(list, career) {
		const byId = {};
		for (const b of list) byId[b.id] = b;
		$("kbgrid").innerHTML = BUILDINGS.map(([id, short, name]) => {
			const b = byId[id];
			if (!b) return "";
			// The levels as pips, in a career; a level of 0 of 2 shows one lit of three.
			const pips = career && num(b.max) !== null && b.max > 0
				? '<span class="kpips">' + Array.from({ length: b.max + 1 }, (_, i) => "<i" + (i <= b.level ? ' class="on"' : "") + "></i>").join("") + "</span>"
				: "";
			return '<button class="pb kbuilding' + (b.open ? "" : " down") + '" data-enter="' + id + '"><span class="lens"><b>' + short + "</b><small>" + name + "</small>" +
				(b.open ? pips : '<em>DESTROYED</em>') + "</span></button>";
		}).join("");
		for (const b of document.querySelectorAll("#kbgrid [data-enter]")) b.addEventListener("click", () => window.Dashboard.run("k.enter[" + b.dataset.enter + "]"));
	}

	// The vessels in flight, each with its body, situation, mission time and a FLY button held to switch to it.
	function renderVessels(list) {
		$("kvcount").textContent = list.length ? String(list.length) : "";
		$("kvlist").innerHTML = list.map((v) =>
			'<div class="krow kvessel" data-id="' + esc(v.id) + '"><div class="kmain"><div class="kname">' + esc(v.name) + ' <span class="ktag">' + esc(v.type) + "</span></div>" +
			'<div class="ksub">' + esc((v.body || "") + " · " + (SITUATIONS[v.situation] || v.situation)) + '</div><div class="ksub">MET <span data-since="' + v.launched + '"></span></div></div>' +
			holdButton("FLY", ' data-fly="' + esc(v.id) + '" data-name="' + esc(v.name) + '"') + "</div>").join("") || '<div class="snote">No vessels in flight.</div>';
		for (const b of document.querySelectorAll("#kvlist [data-fly]"))
			hold(b, HOLD_FLY, () => {
				pending = { text: "Flying " + b.dataset.name + " ...", at: performance.now() };
				window.Dashboard.run("k.fly[" + b.dataset.fly + "]");
			});
	}

	// The alarms, each with its countdown and a WARP TO button that warps to it at a tap.
	function renderAlarms(list) {
		$("kacount").textContent = list.length ? String(list.length) : "";
		$("kalarms").innerHTML = list.map((a) =>
			'<div class="krow"><div class="kmain"><div class="kname">' + esc(a.title || "Alarm") + "</div>" + (a.vessel ? '<div class="ksub">' + esc(a.vessel) + "</div>" : "") + "</div>" +
			'<div class="lcd kcount-down" data-ut="' + a.ut + '"></div><button class="pb kwarpto" data-alarm="' + a.id + '"><span class="lens">WARP<br />TO</span></button></div>').join("") ||
			'<div class="snote">No alarms set.</div>';
		for (const b of document.querySelectorAll("#kalarms [data-alarm]")) b.addEventListener("click", () => window.Dashboard.run("k.warpTo[" + b.dataset.alarm + "]"));
	}

	// The active contracts, each with its rewards and, where it has one, the countdown to its deadline.
	function renderContracts(list) {
		$("kccount").textContent = list.length ? String(list.length) : "";
		// A contract's funds, science and reputation, the ones it gives.
		const reward = (c) => [num(c.funds) ? "Funds " + whole(c.funds) : "", num(c.science) ? "Science " + whole(c.science) : "", num(c.reputation) ? "Reputation " + whole(c.reputation) : ""]
			.filter((s) => s).join("  ·  ");
		$("kcontracts").innerHTML = list.map((c) =>
			'<div class="krow"><div class="kmain"><div class="kname">' + esc(c.title) + '</div><div class="ksub">' + esc(reward(c)) + "</div></div>" +
			(num(c.deadline) !== null ? '<div class="lcd kcount-down" data-ut="' + c.deadline + '"></div>' : "") + "</div>").join("") || '<div class="snote">No active contracts.</div>';
	}

	// Each list drawn again only when the mod sends it anew.
	function changed(key, value) {
		if (value === shown[key]) return false;
		shown[key] = value;
		return Array.isArray(value) || (value && typeof value === "object");
	}

	// Draws the page from the frame's data: figures, warp and crew where they changed, lists sent anew,
	// the clocks when g.ut moves on. connected is false in the demo.
	function render(d, connected) {
		const mode = d["g.mode"], career = mode === "CAREER", science = career || mode === "SCIENCE_SANDBOX";
		const now = num(d["g.ut"]), calendar = d["g.calendar"];
		putText("kdate", d["g.date"] || "-");
		putText("klink", connected ? "● LINK" : "DEMO");
		putClass("klink", "ok", connected);
		putText("kmode", mode ? MODES[mode] || mode : "");
		if (pending && performance.now() - pending.at > PENDING_MS) pending = null;
		putText("khint", pending ? pending.text : "");

		const stats = (career ? stat("Funds", whole(d["g.funds"])) : "") + (science ? stat("Science", whole(d["g.science"])) : "") +
			(career ? stat("Reputation", whole(d["g.reputation"])) : "");
		if (shown.stats !== stats) { shown.stats = stats; $("kstats").innerHTML = stats || '<div class="snote">Sandbox: no funds, science or reputation to keep.</div>'; }
		const rate = num(d["t.currentRate"]);
		const warp = rate === null ? "-" : rate + '<span class="u">&#xD7;</span>';
		if (shown.warp !== warp) { shown.warp = warp; $("kwarp").innerHTML = warp; }
		const c = d["k.crew"];
		const crew = c ? stat("Available", whole(c.available)) + stat("Assigned", whole(c.assigned)) + stat("Lost", whole(c.lost)) : "";
		if (shown.crew !== crew) { shown.crew = crew; $("kcrew").innerHTML = crew; }

		if (changed("buildings", d["k.buildings"]) || shown.career !== career) { shown.career = career; renderBuildings(d["k.buildings"] || [], career); }
		if (changed("vessels", d["k.vessels"])) renderVessels(d["k.vessels"]);
		if (changed("alarms", d["k.alarms"])) renderAlarms(d["k.alarms"]);
		if (changed("contracts", d["k.contracts"])) renderContracts(d["k.contracts"]);
		for (const b of document.querySelectorAll("#kvlist [data-fly]")) b.disabled = !!pending;
		$("kmenu").disabled = !!pending;

		// The clocks: what counts down to a moment, and the time since each vessel's launch.
		if (shown.now !== now) {
			shown.now = now;
			for (const e of document.querySelectorAll("#ksc [data-ut]")) e.textContent = countdown(Number(e.dataset.ut), now, calendar);
			for (const e of document.querySelectorAll("#ksc [data-since]")) e.textContent = now === null ? "-" : span(now - Number(e.dataset.since), calendar);
		}
	}

	// Wires the warp, quicksave and main-menu buttons, once when the page is set up.
	function build() {
		// A click handler that asks for the warp rate index to() gives, never below 1x.
		const warp = (to) => () => window.Dashboard.run("t.timeWarp[" + Math.max(0, to()) + "]");
		// The warp rate index KSP is at now, from the last data.
		const index = () => num(window.Dashboard.data()["t.currentRateIndex"]) || 0;
		$("kw1").addEventListener("click", warp(() => 0));
		$("kwdn").addEventListener("click", warp(() => index() - 1));
		$("kwup").addEventListener("click", warp(() => index() + 1));
		$("kquick").addEventListener("click", () => window.Dashboard.run("g.quicksave"));
		hold($("kmenu"), HOLD_LEAVE, () => { pending = { text: "Saving, to the main menu ...", at: performance.now() }; window.Dashboard.run("g.mainMenu"); });
	}

	// Without the game: a career at the Space Center (index.html#ksc).
	const DEMOS = ["#ksc"];
	const demoState = { warp: 0 };
	// The made-up frame for the demo at the moment now (ms): game time runs at the demo's warp rate.
	function demo(now) {
		const rates = [1, 5, 10, 50, 100, 1000, 10000, 100000];
		demoState.ut = (demoState.ut || 2.31e6) + (rates[demoState.warp] * (now - (demoState.last || now))) / 1000;
		demoState.last = now;
		const ut = Math.floor(demoState.ut), day = 21600, year = 426 * day;
		const date = "Year " + (Math.floor(ut / year) + 1) + ", Day " + (Math.floor((ut % year) / day) + 1) + " - " + span(ut % day, { day, year });
		return {
			"g.scene": "SPACECENTER", "g.mode": "CAREER", "g.funds": 287450, "g.science": 412.5, "g.reputation": 183.2,
			"g.ut": ut, "g.date": date, "g.calendar": { day, year }, "t.currentRate": rates[demoState.warp], "t.currentRateIndex": demoState.warp,
			"k.buildings": demo.buildings || (demo.buildings = BUILDINGS.map(([id], i) => ({ id, level: i % 3, max: 2, open: id !== "Runway" }))),
			"k.vessels": demo.vessels || (demo.vessels = [
				{ id: "a1", name: "Kerbal X", type: "Ship", situation: "PRELAUNCH", body: "Kerbin", launched: 2.3e6 },
				{ id: "b2", name: "Mun Lander II", type: "Lander", situation: "LANDED", body: "Mun", launched: 1.1e6 },
				{ id: "c3", name: "Relay Kerbin-1", type: "Relay", situation: "ORBITING", body: "Kerbin", launched: 4.2e5 },
				{ id: "d4", name: "Jebediah Kerman", type: "EVA", situation: "ORBITING", body: "Minmus", launched: 2.0e6 },
				{ id: "e5", name: "Duna Probe", type: "Probe", situation: "ESCAPING", body: "Sun", launched: 1.9e6 },
			]),
			"k.alarms": demo.alarms || (demo.alarms = [
				{ id: 1, title: "Mun Lander II: Maneuver", vessel: "Mun Lander II", ut: 2.3115e6 },
				{ id: 2, title: "Duna transfer window", vessel: null, ut: 2.6e6 },
			]),
			"k.contracts": demo.contracts || (demo.contracts = [
				{ title: "Rescue Bill Kerman from orbit of the Mun", deadline: 2.9e6, funds: 48000, science: 0, reputation: 12 },
				{ title: "Test RT-10 \"Hammer\" at the launch pad", deadline: null, funds: 6800, science: 2, reputation: 1 },
				{ title: "Explore Minmus", deadline: 5.1e6, funds: 92000, science: 15, reputation: 30 },
			]),
			"k.crew": { available: 6, assigned: 3, lost: 1 },
		};
	}
	// The demo's time warp; every other command does nothing without the game.
	function demoRun(cmd) {
		const m = /^t\.timeWarp\[(\d+)\]$/.exec(cmd);
		if (m) demoState.warp = Math.min(7, Number(m[1]));
	}

	window.KscPage = { id: "ksc", scenes: SCENES, keys: KEYS, demos: DEMOS, build, render, demo, demoRun };
})();
