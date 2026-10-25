(function () {
	"use strict";
	// The VAB and SPH page, in two tabs. The Engineer: the vessel as built, its parts, mass, cost
	// and size against what the buildings allow, its stages in the situation KSP's delta-v app
	// has, its resources and crew. The planner: its plan as its rows show it in the game, KSP's
	// rich text and all, worked here as there -- every button sends the change the planner's own
	// button makes, every list is the one it folds out, picked by its place and its label.
	const { esc, num, whole, rich } = Pages;
	const SCENES = ["EDITOR"];
	const ENGINEER_KEYS = ["e.ship"], PLANNER_KEYS = ["e.ship", "e.plan", "e.choiceList"];
	const TAB_KEY = "kspdashboard.editorTab", FOLD_KEY = "kspdashboard.plannerFolded";
	const $ = (id) => document.getElementById(id);
	let tab = "engineer", folded = {};
	try { tab = localStorage.getItem(TAB_KEY) || tab; folded = JSON.parse(localStorage.getItem(FOLD_KEY)) || {}; } catch (e) { /* no storage */ }
	let shownShip, shownPlan, typing = false, picker = null;
	const run = (cmd) => window.Dashboard.run(cmd);
	const change = (...args) => run("e.change[" + args.join(",") + "]");

	// ------------------------------------------------------------ numbers

	const fixed = (v, digits) => (num(v) === null ? "-" : v.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits }));
	const tonnes = (v) => fixed(v, 2) + '<span class="u">t</span>';
	const speed = (v) => whole(v) + '<span class="u">m/s</span>';
	function burn(s) {
		s = num(s);
		if (s === null || s <= 0) return "-";
		const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = Math.floor(s % 60);
		return (h ? h + "h " : "") + (h || m ? m + "m " : "") + r + "s";
	}
	const stat = (label, value, over) => '<div class="sstat' + (over ? " over" : "") + '"><div class="plate sm">' + label + '</div><div class="lcd">' + value + "</div></div>";

	// ------------------------------------------------------------ Engineer

	function renderShip(s) {
		$("efacility").textContent = s.facility === "SPH" ? "Spaceplane Hangar" : "Vehicle Assembly Building";
		$("ename").textContent = s.name || "-";
		if (!s.parts) {
			$("ecraftstats").innerHTML = '<div class="snote">No vessel yet.</div>';
			$("estagetable").innerHTML = $("eresources").innerHTML = $("ecrew").innerHTML = "";
			$("esituation").textContent = "";
			return;
		}
		const l = s.limits || {};
		const size = s.size || [0, 0, 0], sizeOver = l.size && size.some((v, i) => v > l.size[i]);
		const of = (limit, text) => (limit != null ? text + '<span class="u">of ' + limit + "</span>" : text);
		const stages = s.stages || [];
		const dv = stages.reduce((sum, st) => sum + (num(st.dv) || 0), 0), dvVac = stages.reduce((sum, st) => sum + (num(st.dvVacuum) || 0), 0);
		$("ecraftstats").innerHTML =
			stat("Parts", of(l.parts, whole(s.parts)), l.parts != null && s.parts > l.parts) +
			stat("Mass", l.mass != null ? tonnes(s.mass) + '<span class="u">of ' + fixed(l.mass, 0) + "</span>" : tonnes(s.mass), l.mass != null && s.mass > l.mass) +
			stat("Dry mass", tonnes(s.dryMass)) + stat("Fuel", tonnes(s.fuelMass)) +
			stat("Cost", whole(s.cost)) + stat("Fuel cost", whole(s.fuelCost)) +
			stat("Δv", speed(dv)) + stat("Δv vacuum", speed(dvVac)) +
			'<div class="esize">' + stat("Height", fixed(size[1], 1) + '<span class="u">m</span>', l.size && size[1] > l.size[1]) +
			stat("Width", fixed(size[0], 1) + '<span class="u">m</span>', l.size && size[0] > l.size[0]) +
			stat("Length", fixed(size[2], 1) + '<span class="u">m</span>', l.size && size[2] > l.size[2]) + "</div>" +
			(l.size ? '<div class="snote' + (sizeOver ? " bad" : "") + '">Size allowed: ' + l.size.map((v) => fixed(v, 1)).join(" × ") + " m</div>" : "") +
			(s.stockOff ? '<div class="snote bad">KSP\'s delta-v calculations are off: no stages.</div>' : "");
		const sit = s.situation || {};
		$("esituation").textContent = [sit.body, sit.mode, num(sit.altitude) !== null ? whole(sit.altitude / 1000) + " km" : ""].filter((x) => x).join(" · ");
		$("estagetable").innerHTML = stages.length
			? '<table class="etable"><thead><tr><th>Stage</th><th>Δv</th><th>TWR</th><th>Isp</th><th>Thrust</th><th>Burn</th><th>Mass</th></tr></thead><tbody>' +
			stages.map((st) => "<tr><td><b>S" + st.stage + "</b></td><td>" + whole(st.dv) + '<span class="u">m/s</span></td><td>' +
				(num(st.twr) > 0 ? fixed(st.twr, 2) + (num(st.twrEnd) > 0 ? "–" + fixed(st.twrEnd, 2) : "") : "-") + "</td><td>" +
				(num(st.isp) > 0 ? whole(st.isp) + '<span class="u">s</span>' : "-") + "</td><td>" + (num(st.thrust) > 0 ? fixed(st.thrust, 1) + '<span class="u">kN</span>' : "-") +
				"</td><td>" + burn(st.burn) + "</td><td>" + fixed(st.startMass, 2) + "→" + fixed(st.endMass, 2) + '<span class="u">t</span></td></tr>').join("") +
			"</tbody></table>"
			: '<div class="snote">No stages with engines.</div>';
		$("eresources").innerHTML = (s.resources || []).map((r) => {
			const share = r.max > 0 ? Math.max(0, Math.min(1, r.amount / r.max)) : 0;
			return '<div class="eres"><span>' + esc(r.name) + '</span><div class="ebar"><i style="width:' + (share * 100).toFixed(1) + '%"></i></div><b>' +
				fixed(r.amount, r.max >= 100 ? 0 : 1) + '<span class="u">/ ' + fixed(r.max, r.max >= 100 ? 0 : 1) + "</span></b></div>";
		}).join("") || '<div class="snote">No resources.</div>';
		const crew = s.crew || [];
		$("ecrew").innerHTML = stat("Seats", whole(s.seats)) + stat("Aboard", whole(crew.length)) +
			(crew.length ? '<div class="ecrewlist">' + crew.map((n) => "<span>" + esc(n) + "</span>").join("") + "</div>" : "");
	}

	// ------------------------------------------------------------ planner

	// A part of the planner under its header bar, folded where the player folded it.
	function part(id, title, summary, body) {
		return '<div class="ephead" data-fold="' + id + '"><b>' + title + "</b><span>" + rich(summary || "") + "</span><i>" + (folded[id] ? "+" : "−") + "</i></div>" +
			'<div class="epbody' + (folded[id] ? " folded" : "") + '">' + body + "</div>";
	}
	const button = (label, cmd, attrs) => '<button class="pb epbtn" ' + (cmd ? 'data-cmd="' + esc(cmd) + '"' : "") + (attrs || "") + '><span class="lens">' + label + "</span></button>";
	const chooser = (label, list, a, b, cls) => '<button class="epchoose' + (cls ? " " + cls : "") + '" data-choices="' + list + "," + a + "," + b + '">' + rich(label) + "</button>";
	const choice = (label, on, cmd) => '<button class="epchoice' + (on ? " on" : "") + '" data-cmd="' + esc(cmd) + '">' + label + "</button>";
	const bar = (pieces) => '<div class="epbar">' + (pieces || []).map((p) => '<i style="flex:' + Math.max(0.002, p.share) + " 0 0;background:" + p.color + '"></i>').join("") +
		'<i style="flex:' + Math.max(0, 1 - (pieces || []).reduce((s, p) => s + p.share, 0)) + ' 0 0"></i></div>';

	function renderPlan(p) {
		if (!p.ready) {
			$("eplanner").innerHTML = '<section class="panel"><div class="snote">The planner is starting.</div></section>';
			return;
		}
		const stock = p.stockOff ? '<div class="snote bad">KSP\'s delta-v calculations are off: the planner has no stages to plan with.</div>' : "";
		// Craft: the vessel and its sections.
		const sections = (p.sections || []).map((s) =>
			'<div class="eprow"><span class="epname">' + rich(s.name) + "</span>" + chooser(s.parts, "parts", s.row, 0, "grow") +
			button("×", "e.change[removeSection," + s.row + "]", ' title="Remove this section"') + "</div>" +
			(s.deploy != null ? '<div class="eprow sub"><span class="epname dim">Deployed at</span>' + chooser(s.deploy, "deploy", s.row, 0, "grow") + "</div>" : "") +
			(s.warning ? '<div class="epwarn">' + rich(s.warning) + "</div>" : "")).join("");
		const craft = stock + '<div class="epline">' + rich(p.craft || "") + "</div>" + sections +
			(p.canAddSection ? '<div class="eprow">' + chooser("<b>+</b> Section", "newSection", 0, 0) + "</div>" : "");

		// Route: the settings, then each stop with the steps after it, then the balance.
		const settings = '<div class="eprow"><span class="epname dim">Fuel margin</span>' + button("−", "e.change[margin," + Math.max(0, p.margin - 1) + "]") +
			'<div class="lcd epmargin">' + whole(p.margin) + '<span class="u">%</span></div>' + button("+", "e.change[margin," + Math.min(50, p.margin + 1) + "]") +
			choice("Plane changes", p.planes, "e.change[planes," + (p.planes ? 0 : 1) + "]") + "</div>";
		const stops = (p.stops || []).map((s, i) =>
			'<div class="epstop"><div class="eprow"><b class="epletter">' + s.letter + "</b>" + chooser("<b>" + s.body + "</b>", "body", i, 0, "body") +
			choice("Surface", s.kind === "Surface", "e.change[kind," + i + ",0]") + choice("Orbit", s.kind === "Orbit", "e.change[kind," + i + ",1]") +
			(s.air ? choice(s.air.label, s.air.on, "e.change[air," + i + "]") : "") +
			(s.removable ? button("×", "e.change[removeStop," + i + "]", ' title="Remove this destination"') : "") + "</div>" +
			(s.km != null ? '<div class="eprow sub">' + button("−", "e.change[altitudeStep," + i + ",-1]") +
				'<input class="epalt" inputmode="decimal" data-stop="' + i + '" value="' + esc(s.km) + '" /><span class="dim">km</span>' +
				button("+", "e.change[altitudeStep," + i + ",1]") + "</div>" : "") + "</div>" +
			(s.steps || []).map(stepHtml).join("")).join("");
		const add = p.canAddStop ? '<div class="eprow"><b class="epletter dim">' + p.nextLetter + "</b>" + button("+", "e.change[addStop]", ' title="Add a destination"') + "</div>" : "";
		const balance = p.summary != null ? bar(p.total) + '<div class="epline">' + rich(p.summary) + "</div>" : "";
		const route = settings + stops + add + balance;

		$("eplanner").innerHTML =
			'<section class="panel epcol">' + part("craft", "Craft", "", craft) + "</section>" +
			'<section class="panel epcol wide">' + part("route", "Route", "", route) + "</section>" +
			'<section class="panel epcol">' + part("fuel", "Fuel plan", p.fuel ? p.fuel.summary : "", fuelHtml(p.fuel)) + "</section>" +
			'<section class="panel epcol">' + part("thrust", "Thrust", p.thrust ? p.thrust.summary : "", thrustHtml(p.thrust)) + "</section>";
	}

	// A step on the route: where it goes, who flies it, its bar and delta-v; the grey lines, its
	// choices, and in red what blocks it.
	function stepHtml(s) {
		const lines = [];
		if (s.separation) lines.push('<div class="epnote">' + rich(s.separation) + "</div>");
		if (s.docks) lines.push('<div class="eprow sub"><span class="epname dim">Docks with the rest</span>' +
			choice("Refuel", s.docks.refuel, "e.change[refuel," + s.leg + "," + s.role + "]") +
			(s.docks.refuel ? chooser(s.docks.tanker, "tanker", s.leg, s.role) : "") + "</div>");
		if (s.deployed) lines.push('<div class="epnote">' + rich(s.deployed) + "</div>");
		if (s.orbitFirst != null) lines.push('<div class="eprow sub"><span class="epname dim">Way down</span>' +
			choice("Straight in", !s.orbitFirst, "e.change[orbitFirst," + (s.leg + 1) + ",0]") + choice("Orbit first", s.orbitFirst, "e.change[orbitFirst," + (s.leg + 1) + ",1]") + "</div>");
		if (s.split != null) lines.push('<div class="eprow sub"><span class="epname dim">Burns</span>' +
			choice("Together", !s.split, "e.change[split," + (s.leg + 1) + ",0]") + choice("Split", s.split, "e.change[split," + (s.leg + 1) + ",1]") + "</div>");
		for (const w of s.warnings || []) lines.push('<div class="epwarn">' + rich(w) + "</div>");
		return '<div class="epstep"><div class="eprow"><span class="epplace">' + rich(s.place) + "</span>" +
			(s.flyer != null ? chooser(s.flyer, "flyer", s.leg, s.role, "flyer") : "") + bar(s.bar) + '<span class="epdv">' + rich(s.dv) + "</span></div>" + lines.join("") + "</div>";
	}

	// The fuel plan: each step with what it needs and leaves, its stages with Use and Drop after.
	function fuelHtml(f) {
		if (!f) return '<div class="snote">Pick a destination to plan the fuel.</div>';
		return f.steps.map((s, i) =>
			'<div class="epstep"><div class="eprow"><span class="epplace">' + rich(s.place) + '</span><span class="epflyer">' + rich(s.flyer) + "</span>" +
			'<span class="epdv">' + rich(s.needs) + '</span><span class="epdv">' + rich(s.left) + "</span></div>" +
			(s.stages.length ? '<div class="epscroll"><table class="eptable"><tr><th></th><th>Stage</th><th>Gives</th><th>Keeps</th><th>TWR</th><th></th></tr>' + s.stages.map((st) =>
				'<tr><td><i class="epswatch" style="background:' + st.color + '"></i></td><td>' + rich(st.name) + "</td><td>" + rich(st.gives) + "</td><td>" + rich(st.keeps) +
				"</td><td>" + rich(st.twr) + '</td><td class="epuse">' +
				(st.canUse ? choice("Use", st.use, "e.change[use," + i + "," + st.number + "]") : "") +
				(st.canDrop ? choice("Drop after", st.drop, "e.change[drop," + i + "," + st.number + "]") : "") + "</td></tr>").join("") +
				(s.rcs != null ? '<tr><td></td><td class="dim">RCS</td><td class="dim">0</td><td>' + esc(s.rcs) + "</td><td></td><td></td></tr>" : "") + "</table></div>" : "") +
			s.lines.map((l) => '<div class="eprow sub"><span class="epname">' + rich(l[0]) + '</span><span class="epdv">' + rich(l[1]) + "</span></div>").join("") +
			s.warnings.map((w) => '<div class="epwarn">' + rich(w) + "</div>").join("") + "</div>").join("") || '<div class="snote">No steps.</div>';
	}

	// The thrust part: each step that burns, with what it needs and has; its stages, burns, notes.
	function thrustHtml(t) {
		if (!t) return '<div class="snote">Pick a destination to check the thrust.</div>';
		const heads = "<tr><th>Stage</th><th>Mass</th><th>Isp</th><th>Start</th><th>End</th><th>In air</th><th>Thrust</th><th>Burns</th></tr>";
		const row = (cells) => "<tr>" + cells.map((c) => "<td>" + rich(c) + "</td>").join("") + "</tr>";
		return t.steps.map((s) =>
			'<div class="epstep"><div class="eprow"><span class="epplace">' + rich(s.place) + '</span><span class="epflyer">' + rich(s.flyer) + "</span>" +
			'<span class="epdv">' + rich(s.needs) + '</span><span class="epdv">' + rich(s.has) + "</span>" + (s.turn ? '<span class="epdv">' + rich(s.turn) + "</span>" : "") + "</div>" +
			(s.stages.length || s.rcs ? '<div class="epscroll"><table class="eptable">' + heads + s.stages.map(row).join("") + (s.rcs ? row(s.rcs) : "") + "</table></div>" : "") +
			s.burns.map((b) => '<div class="eprow sub"><span class="epname">' + rich(b[0]) + "</span><span>" + rich(b[1]) + "</span></div>").join("") +
			s.notes.map((n) => '<div class="epnote">' + rich(n) + "</div>").join("") + '<div class="epnote">' + rich(s.body) + "</div></div>").join("") ||
			'<div class="snote">No burns on the route.</div>';
	}

	// ------------------------------------------------------------ lists

	// The list a planner button folds out, asked of the mod and shown over the page; a tap picks.
	function openPicker(list, a, b) {
		const asked = (window.Dashboard.data()["e.choiceList"] || {}).asked || 0;
		picker = { list, a: Number(a), b: Number(b), after: asked, shown: false };
		$("epicker").hidden = false;
		$("epickerlist").innerHTML = '<div class="snote">…</div>';
		run("e.choices[" + list + "," + a + "," + b + "]");
	}
	function closePicker() { picker = null; $("epicker").hidden = true; }
	function renderPicker(answer) {
		if (!picker || picker.shown || !answer || answer.asked <= picker.after || answer.list !== picker.list || answer.a !== picker.a || answer.b !== picker.b) return;
		picker.shown = true;
		$("epickerlist").innerHTML = answer.entries.map((e, i) =>
			'<button class="eppick' + (e.current ? " on" : "") + '" data-pick="' + i + '" style="padding-left:' + (14 + (num(e.indent) || 0)) + 'px"' +
			(e.tooltip ? ' title="' + esc(e.tooltip) + '"' : "") + ">" + rich(e.label) + "</button>").join("") || '<div class="snote">Nothing to pick.</div>';
		picker.entries = answer.entries;
	}

	// ------------------------------------------------------------ page

	function showTab(next) {
		tab = next === "planner" ? "planner" : "engineer";
		try { localStorage.setItem(TAB_KEY, tab); } catch (e) { /* kept for this visit */ }
		$("editor").dataset.tab = tab;
		for (const b of document.querySelectorAll("#etabs button")) b.classList.toggle("on", b.dataset.tab === tab);
		shownPlan = undefined;
		window.Dashboard.resubscribe();
	}

	function render(d, connected) {
		$("elink").textContent = connected ? "● LINK" : "DEMO";
		$("elink").classList.toggle("ok", connected);
		if (d["e.ship"] && d["e.ship"] !== shownShip) { shownShip = d["e.ship"]; renderShip(shownShip); }
		if (tab === "planner") {
			// Not while an altitude is typed: the field would be made anew under the finger.
			if (d["e.plan"] && d["e.plan"] !== shownPlan && !typing) { shownPlan = d["e.plan"]; renderPlan(shownPlan); }
			renderPicker(d["e.choiceList"]);
		}
	}

	function build() {
		$("editor").dataset.tab = tab;
		for (const b of document.querySelectorAll("#etabs button")) {
			b.classList.toggle("on", b.dataset.tab === tab);
			b.addEventListener("click", () => showTab(b.dataset.tab));
		}
		const planner = $("eplanner");
		planner.addEventListener("click", (e) => {
			const fold = e.target.closest("[data-fold]");
			if (fold) {
				const id = fold.dataset.fold;
				folded[id] = !folded[id];
				try { localStorage.setItem(FOLD_KEY, JSON.stringify(folded)); } catch (x) { /* kept for this visit */ }
				fold.nextElementSibling.classList.toggle("folded", !!folded[id]);
				fold.querySelector("i").textContent = folded[id] ? "+" : "−";
				return;
			}
			const pick = e.target.closest("[data-choices]");
			if (pick) { const [list, a, b] = pick.dataset.choices.split(","); openPicker(list, a, b); return; }
			const cmd = e.target.closest("[data-cmd]");
			if (cmd) run(cmd.dataset.cmd);
		});
		planner.addEventListener("focusin", (e) => { if (e.target.matches(".epalt")) typing = true; });
		planner.addEventListener("focusout", (e) => { if (e.target.matches(".epalt")) { typing = false; shownPlan = undefined; } });
		planner.addEventListener("change", (e) => {
			if (!e.target.matches(".epalt")) return;
			change("altitude", e.target.dataset.stop, String(e.target.value).replace(",", "."));
		});
		planner.addEventListener("keydown", (e) => { if (e.target.matches(".epalt") && e.key === "Enter") e.target.blur(); });
		$("epicker").addEventListener("click", (e) => {
			const entry = e.target.closest("[data-pick]");
			if (entry && picker && picker.entries) {
				const i = Number(entry.dataset.pick);
				run("e.pick[" + picker.list + "," + picker.a + "," + picker.b + "," + i + "," + encodeURIComponent(picker.entries[i].label) + "]");
				closePicker();
			} else if (e.target === $("epicker") || e.target.closest("#epickerclose")) closePicker();
		});
	}

	// Without the game: the Kerbal X with a plan to the Mun and back (index.html#vab).
	const DEMOS = ["#vab"];
	const G = "#8fdc7a", D = "#9a9a9a", R = "#e6474d", S1 = "#5aa9e6", S2 = "#e6c05a", S3 = "#c77ae6", S4 = "#6ae6b8";
	const c = (colour, text) => "<color=" + colour + ">" + text + "</color>";
	function demo() {
		if (demo.data) return demo.data;
		const step = (leg, role, place, flyer, dv, bar, extra) => Object.assign({ leg, role, place, flyer, dv, bar, separation: "", deployed: "", warnings: [] }, extra || {});
		const fuelStage = (number, colour, gives, keeps, twr, use) => ({ number, name: "<b>" + c(colour, "S" + number) + "</b>", color: colour, gives: c(colour, gives), keeps, twr: twr ? c(D, twr) : "", use, canUse: true, drop: false, canDrop: true });
		demo.data = {
			"g.scene": "EDITOR",
			"e.ship": {
				facility: "VAB", name: "Kerbal X", parts: 52, mass: 134.37, dryMass: 37.2, fuelMass: 97.17, cost: 47828, dryCost: 39000, fuelCost: 8828, size: [8.4, 27.9, 8.4],
				limits: { parts: 255, mass: 140, size: [9.0, 36.0, 9.0] },
				situation: { body: "Kerbin", mode: "Sea level", altitude: null },
				stages: [
					{ stage: 6, dv: 1124, dvVacuum: 1290, twr: 1.51, twrEnd: 2.46, isp: 285, thrust: 1985.2, burn: 63, startMass: 134.37, endMass: 82.1 },
					{ stage: 5, dv: 1406, dvVacuum: 1580, twr: 1.32, twrEnd: 3.11, isp: 290, thrust: 1060.8, burn: 92, startMass: 82.1, endMass: 34.9 },
					{ stage: 3, dv: 1802, dvVacuum: 2410, twr: 0.81, twrEnd: 1.97, isp: 250, thrust: 167.9, burn: 158, startMass: 21.3, endMass: 8.8 },
					{ stage: 1, dv: 1520, dvVacuum: 1680, twr: 2.12, twrEnd: 4.38, isp: 280, thrust: 52.0, burn: 71, startMass: 4.6, endMass: 2.2 },
				],
				resources: [{ name: "Liquid Fuel", amount: 8640, max: 8640 }, { name: "Oxidizer", amount: 10560, max: 10560 }, { name: "Monopropellant", amount: 30, max: 30 }, { name: "Electric Charge", amount: 250, max: 250 }],
				seats: 3, crew: ["Jebediah Kerman", "Bill Kerman", "Bob Kerman"],
			},
			"e.plan": {
				vessel: "Kerbal X", ready: true, stockOff: false, hasSections: true,
				craft: c(D, "Mass") + " 134.37 t<pos=34%>" + c(D, "Fuel") + " 97.17 t<pos=67%>" + c(D, "Δv") + " 6,960 m/s",
				sections: [
					{ row: 0, name: c(D, "Capsule"), parts: c(G, "Automatic") + " " + c(D, "(Mk1-3 Command Pod, 4 parts)"), kind: "Capsule", warning: "" },
					{ row: 1, name: c(D, "Lander"), parts: c(G, "Automatic") + " " + c(D, "(Mk2 Lander Can, 9 parts)"), kind: "Lander", warning: "" },
				],
				canAddSection: true, margin: 10, planes: true,
				stops: [
					{ letter: "A", body: "Kerbin", hasBody: true, kind: "Surface", removable: true, steps: [step(0, 0, "To Kerbin orbit", c(G, "Ship"), "3,740 m/s", [{ share: 0.38, color: S1 }, { share: 0.45, color: S2 }, { share: 0.17, color: S3 }])] },
					{ letter: "B", body: "Mun", hasBody: true, kind: "Surface", removable: true, steps: [
						step(1, 1, "To Mun orbit", c(G, "Ship"), "1,020 m/s", [{ share: 0.27, color: S3 }], { split: false }),
						step(1, 3, "To Mun surface", "<b><u>" + c(G, "Lander") + "</u></b>", "640 m/s", [{ share: 0.17, color: S4 }], { separation: "The lander separates in Mun orbit" }),
					] },
					{ letter: "C", body: "Kerbin", hasBody: true, kind: "Surface", removable: true, air: { label: "Chutes", on: true }, steps: [
						step(2, 0, "To Mun orbit", c(G, "Lander"), "640 m/s", [{ share: 0.12, color: S4 }, { share: 0.05, color: R }], { warnings: ["42 m/s short"], docks: { refuel: false, tanker: "" } }),
						step(2, 1, "To Kerbin's atmosphere", c(G, "Ship"), "310 m/s", [{ share: 0.08, color: S3 }], { orbitFirst: false }),
					] },
				],
				canAddStop: true, nextLetter: "D",
				summary: c(D, "Left") + " <b>" + c("#f18a24", "-42 m/s") + "</b><pos=40%>" + c(D, "Needed") + " 6,350 m/s " + c(D, "of") + " 6,960 m/s",
				total: [{ share: 0.2, color: S1 }, { share: 0.24, color: S2 }, { share: 0.3, color: S3 }, { share: 0.2, color: S4 }, { share: 0.006, color: R }],
				fuel: {
					summary: c(R, "1 step short of fuel"),
					steps: [
						{ place: "To Kerbin orbit", flyer: c(D, "Ship"), needs: "3,740 m/s", left: c(G, "3,220 m/s"), stages: [fuelStage(6, S1, "1,290 m/s", c(D, "empty"), "1.51", true), fuelStage(5, S2, "1,580 m/s", c(D, "empty"), "", true), fuelStage(3, S3, "870 m/s", "1,540 m/s", "", true)], rcs: "180 m/s", lines: [[c(D, "Launch"), "3,400 m/s"], [c(D, "Margin 10 %"), "340 m/s"]], warnings: [] },
						{ place: "To Mun orbit", flyer: c(D, "Ship"), needs: "1,020 m/s", left: c(G, "2,200 m/s"), stages: [fuelStage(3, S3, "1,020 m/s", "520 m/s", "", true), fuelStage(1, S4, "0", "1,680 m/s", "", true)], rcs: null, lines: [[c(D, "Transfer"), "860 m/s"], [c(D, "Capture"), "70 m/s"]], warnings: [] },
						{ place: "To Mun surface", flyer: c(D, "Lander"), needs: "640 m/s", left: c(G, "1,040 m/s"), stages: [fuelStage(1, S4, "640 m/s", "1,040 m/s", "2.12", true)], rcs: null, lines: [[c(D, "Landing"), "580 m/s"]], warnings: [] },
						{ place: "To Mun orbit", flyer: c(D, "Lander"), needs: "640 m/s", left: c(R, "-42 m/s"), stages: [fuelStage(1, S4, "598 m/s", c(D, "empty"), "2.40", true)], rcs: null, lines: [[c(D, "Launch"), "580 m/s"]], warnings: ["42 m/s short"] },
					],
				},
				thrust: {
					summary: c(G, "Enough thrust on every step"),
					steps: [
						{ place: "To Kerbin orbit", flyer: c(D, "Ship"), needs: c(D, "TWR 1.20"), has: c(G, "S6  1.51"), turn: c(D, "6 s"), stages: [["<b>" + c(S1, "S6") + "</b>", c(D, "134.37 t"), c(D, "285 s"), c(D, "1.51"), c(D, "2.46"), c(D, "1.38"), c(D, "1985 kN"), c(D, "1m 03s")]], rcs: null, burns: [], notes: [], body: c(D, "Kerbin: surface gravity 9.81 m/s², atmosphere 1.00 atm") },
						{ place: "To Mun orbit", flyer: c(D, "Ship"), needs: c(D, "< 17% orbit"), has: c(G, "2m 40s 6%"), turn: c(D, "14 s"), stages: [["<b>" + c(S3, "S3") + "</b>", c(D, "21.30 t"), c(D, "250 s"), c(D, "0.81"), c(D, "1.97"), "", c(D, "168 kN"), c(D, "2m 40s")]], rcs: null, burns: [[c(D, "Transfer"), c(G, "2m 40s, 6% of 33m 20s")]], notes: [], body: c(D, "Kerbin: surface gravity 9.81 m/s², atmosphere 1.00 atm") },
					],
				},
			},
			"e.choiceList": null,
		};
		return demo.data;
	}
	// Without the game, a picked list shows the bodies.
	function demoRun(cmd) {
		const m = /^e\.choices\[([^,]*),(-?\d+),(-?\d+)\]$/.exec(cmd);
		if (!m) return;
		const bodies = [["Sun", 0], ["Moho", 12], ["Eve", 12], ["Gilly", 24], ["Kerbin", 12], ["Mun", 24], ["Minmus", 24], ["Duna", 12], ["Ike", 24]];
		const d = demo();
		d["e.choiceList"] = { asked: ((d["e.choiceList"] || {}).asked || 0) + 1, list: m[1], a: Number(m[2]), b: Number(m[3]), entries: bodies.map(([label, indent]) => ({ label, indent, current: label === "Mun", tooltip: null })) };
	}

	window.EditorPage = {
		id: "editor", scenes: SCENES, demos: DEMOS, build, render, demo, demoRun,
		keys: () => (tab === "planner" ? PLANNER_KEYS : ENGINEER_KEYS),
	};
})();
