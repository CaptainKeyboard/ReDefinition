(function () {
	"use strict";
	// What the scene pages (menu.js, ksc.js) share: their hold buttons, how they write numbers and
	// times, and text made safe for their HTML.
	const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
	const num = (v) => (typeof v === "number" && isFinite(v) ? v : null);
	const whole = (v) => (num(v) === null ? "-" : Math.round(v).toLocaleString());

	// A button that fires once held for ms; its bar (.prog) fills while it is held.
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
		const two = (x) => String(x).padStart(2, "0");
		return (y ? y + "y " : "") + (y || d ? d + "d " : "") + two(Math.floor(r / 3600)) + ":" + two(Math.floor((r % 3600) / 60)) + ":" + two(r % 60);
	}
	// T- before a moment, T+ after it.
	const countdown = (ut, now, calendar) => (num(ut) === null || num(now) === null ? "-" : (ut >= now ? "T- " : "T+ ") + span(ut - now, calendar));

	// KSP's rich text, as the mod's own panels write it (the planner's rows), as HTML: its colours,
	// bold and underline; a <pos=..> column as a gap; sizes and alignment dropped. Everything else
	// stays text.
	function rich(text) {
		return esc(text == null ? "" : text)
			.replace(/&lt;color=(#[0-9a-fA-F]{3,8})&gt;/g, '<span style="color:$1">')
			.replace(/&lt;\/color&gt;/g, "</span>")
			.replace(/&lt;(\/?)(b|u|i)&gt;/g, "<$1$2>")
			.replace(/&lt;pos=[^&]*&gt;/g, '<span class="rgap"></span>')
			.replace(/&lt;\/?(size|align)(=[^&]*)?&gt;/g, "");
	}

	window.Pages = { esc, num, whole, hold, holdButton, ago, minutes, span, countdown, rich };
})();
