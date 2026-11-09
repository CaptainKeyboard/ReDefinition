// The pages' shared helpers, published as window.Pages for widget.js, menu.js, ksc.js and editor.js.
(function () {
	"use strict";
	// What the pages (widget.js, menu.js, ksc.js, editor.js) share: their hold buttons, how they write
	// numbers and times, text made safe for their HTML, and the writes that skip what is already shown.
	const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
	// A finite number as it is, anything else (missing, NaN, text) as null.
	const num = (v) => (typeof v === "number" && isFinite(v) ? v : null);
	// A number rounded and grouped in the viewer's locale; "-" without one.
	const whole = (v) => (num(v) === null ? "-" : Math.round(v).toLocaleString());
	// An element by id.
	const $ = (id) => document.getElementById(id);
	// KSP's game modes (g.mode) as the pages name them.
	const MODES = { CAREER: "Career", SCIENCE_SANDBOX: "Science", SANDBOX: "Sandbox", MISSION: "Mission", MISSION_BUILDER: "Mission Builder", SCENARIO: "Scenario", SCENARIO_NON_RESUMABLE: "Scenario", TUTORIAL: "Tutorial" };
	// One figure: its plate label over the value on an LCD; over marks it red, past what a limit allows.
	const stat = (label, value, over) => '<div class="sstat' + (over ? " over" : "") + '"><div class="plate sm">' + label + '</div><div class="lcd">' + value + "</div></div>";

	// The page is only written where what it shows changes: every write of a glowing LCD makes the
	// browser lay out and paint it again, the same number or not. Everything written through these is
	// remembered by id (or by key), so nothing else may write the same element.
	const shown = new Map(), els = {};
	// An element by id, looked up once and kept.
	const el = (id) => els[id] || (els[id] = $(id));
	// Writes an element's HTML when it differs from what it shows.
	function put(id, html) { if (shown.get(id) !== html) { shown.set(id, html); el(id).innerHTML = html; } }
	// Writes an element's text when it differs from what it shows.
	function putText(id, text) { if (shown.get(id) !== text) { shown.set(id, text); el(id).textContent = text; } }
	// Sets a class of an element on or off when that differs from what it shows.
	function putClass(id, name, on) { const key = id + "." + name; on = !!on; if (shown.get(key) !== on) { shown.set(key, on); el(id).classList.toggle(name, on); } }
	// Sets one style property of a node when it differs from the value last set under key.
	function putStyle(node, key, prop, value) { if (shown.get(key) !== value) { shown.set(key, value); node.style[prop] = value; } }

	// A button that fires once held for ms; its bar (.prog) fills while it is held.
	function hold(b, ms, fire) {
		let timer = 0, raf = 0, t0 = 0;
		const prog = b.querySelector(".prog");
		// Ends a hold early: the timer and the bar's animation stop, the bar empties.
		const stop = () => { clearTimeout(timer); cancelAnimationFrame(raf); prog.style.width = "0"; };
		b.addEventListener("pointerdown", (e) => {
			if (b.disabled) return;
			b.setPointerCapture(e.pointerId);
			t0 = performance.now();
			// Fills the bar by the share of ms held so far, once per animation frame.
			const tick = () => { prog.style.width = Math.min(100, ((performance.now() - t0) / ms) * 100) + "%"; raf = requestAnimationFrame(tick); };
			tick();
			timer = setTimeout(() => { stop(); fire(); }, ms);
		});
		for (const e of ["pointerup", "pointercancel", "lostpointercapture"]) b.addEventListener(e, stop);
	}
	// The HTML of a hold button for hold(): its label, a HOLD hint and the empty bar; attrs go into the tag.
	const holdButton = (label, attrs) => '<button class="shold"' + (attrs || "") + "><b>" + label + '</b><span class="hint">HOLD</span><i class="prog"></i></button>';

	// Real time since a moment, for when a save was played.
	function ago(ms) {
		const s = (Date.now() - ms) / 1000;
		if (s < 90) return "just now";
		if (s < 3600) return Math.round(s / 60) + " min ago";
		if (s < 86400) return Math.round(s / 3600) + " h ago";
		if (s < 2 * 86400) return "yesterday";
		if (s < 14 * 86400) return Math.round(s / 86400) + " days ago";
		return new Date(ms).toLocaleDateString();
	}
	// Minutes and seconds, for KSP's loading.
	const minutes = (s) => { s = Math.max(0, Math.floor(num(s) || 0)); return Math.floor(s / 60) + ":" + String(s % 60).padStart(2, "0"); };
	// Game time in the game's calendar (g.calendar, Kerbin's without it): years and days, then hh:mm:ss.
	function span(s, calendar) {
		s = num(s);
		if (s === null) return "-";
		const day = (calendar && calendar.day) || 21600, year = (calendar && calendar.year) || 9201600;
		s = Math.floor(Math.abs(s));
		const y = Math.floor(s / year), d = Math.floor((s % year) / day), r = s % day;
		// A clock field with a leading zero.
		const two = (x) => String(x).padStart(2, "0");
		return (y ? y + "y " : "") + (y || d ? d + "d " : "") + two(Math.floor(r / 3600)) + ":" + two(Math.floor((r % 3600) / 60)) + ":" + two(r % 60);
	}
	// T- before a moment, T+ after it.
	const countdown = (ut, now, calendar) => (num(ut) === null || num(now) === null ? "-" : (ut >= now ? "T- " : "T+ ") + span(ut - now, calendar));

	// KSP's rich text, as the mod's own panels write it (the planner's rows), as HTML: its colours,
	// bold, italic and underline; a <pos=..> column as a gap; sizes and alignment dropped. Everything else
	// stays text.
	function rich(text) {
		return esc(text == null ? "" : text)
			.replace(/&lt;color=(#[0-9a-fA-F]{3,8})&gt;/g, '<span style="color:$1">')
			.replace(/&lt;\/color&gt;/g, "</span>")
			.replace(/&lt;(\/?)(b|u|i)&gt;/g, "<$1$2>")
			.replace(/&lt;pos=[^&]*&gt;/g, '<span class="rgap"></span>')
			.replace(/&lt;\/?(size|align)(=[^&]*)?&gt;/g, "");
	}

	window.Pages = { $, MODES, stat, shown, el, put, putText, putClass, putStyle, esc, num, whole, hold, holdButton, ago, minutes, span, countdown, rich };
})();
