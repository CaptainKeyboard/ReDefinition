/* Navball: a sphere seen from the front, turned by the vessel's attitude.
   Directions are [north, east, up] unit vectors in the vessel's local frame, as ReDefinition
   sends them. The look follows KSP's ball: a thin horizon, the north meridian in orange,
   thin meridians every 45 degrees, short pitch ticks every 5 degrees on meridians every 22.5, and
   pitch numbers painted on the ball between the meridians.

   Everything painted on the ball is drawn once into a texture (an equirectangular map: longitude
   across, latitude down). Each frame the graphics card lays that texture onto the turned sphere
   (WebGL 2); without WebGL the same texture is looked up per pixel on the CPU. What stands upright
   to the player (heading numbers, markers, the vessel's own marker) is drawn on a 2D canvas on top.
   Nothing is drawn again while the attitude and the markers stay the same. */
(function () {
	"use strict";
	const DEG = 180 / Math.PI, RAD = Math.PI / 180;
	const SKY = [24, 168, 218], GROUND = [176, 92, 16];
	// The camera sits CAM ball radii from the ball's centre. Far away, so the ball is the plain
	// sphere seen from the front (the whole hemisphere shows); a smaller value would move the
	// camera closer and show only the middle of it.
	const CAM = 20;
	const LIMB = CAM / Math.sqrt(CAM * CAM - 1); // screen radius of the ball's outline
	const NEAR = 1 / CAM;                        // z of the outline: points with less are hidden

	// The texture: 4096 x 2048, about 11 pixels per degree.
	const TEX_W = 4096, TEX_H = 2048;
	const PX = TEX_W / 360; // texture pixels per degree, the same across and down
	// Half-widths of the painted lines, in degrees of arc.
	const HW = 0.28;
	const HORIZON_HW = HW * 0.9, MERIDIAN_HW = HW * 0.8, NORTH_HW = HW * 0.9 + 0.01, TICK_HW = HW * 0.7;
	const GLYPH_DEG = 3.4; // height of a pitch number's characters, in degrees of arc
	const CPU_MAX = 440;   // the CPU fallback draws at most this many pixels across

	const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
	const valid = (v) => Array.isArray(v) && v.length === 3 && v.every((x) => typeof x === "number" && isFinite(x));
	const rgb = (c) => "rgb(" + c[0] + "," + c[1] + "," + c[2] + ")";

	function direction(latDeg, lonDeg) {
		const la = latDeg * RAD, lo = lonDeg * RAD;
		return [Math.cos(la) * Math.cos(lo), Math.cos(la) * Math.sin(lo), Math.sin(la)];
	}

	// Where a world direction lands on the canvas, and how many pixels one degree is there.
	function project(att, S, dir) {
		const x = dot(dir, att.right), y = dot(dir, att.top), z = dot(dir, att.nose);
		if (z <= NEAR + 0.02) return null;
		const k = CAM / (CAM - z);
		const half = S / 2 / LIMB;
		const scale = half * RAD * CAM * (CAM * z - 1) / ((CAM - z) * (CAM - z));
		return { x: (x * k / LIMB * 0.5 + 0.5) * S, y: (0.5 - y * k / LIMB * 0.5) * S, scale, z };
	}

	// ---- The texture, painted once ----------------------------------------------------------------

	let texture = null;
	function paintTexture() {
		if (texture) return texture;
		const c = document.createElement("canvas");
		c.width = TEX_W; c.height = TEX_H;
		const g = c.getContext("2d");
		const X = (lon) => lon * PX, Y = (lat) => (90 - lat) * PX;

		g.fillStyle = rgb(SKY); g.fillRect(0, 0, TEX_W, TEX_H / 2);
		g.fillStyle = rgb(GROUND); g.fillRect(0, TEX_H / 2, TEX_W, TEX_H / 2);

		// Meridians every 45 degrees. Their width is in degrees of arc, so on the map they widen
		// towards the poles, row by row.
		for (let row = 0; row < TEX_H; row++) {
			const lat = 90 - (row + 0.5) / PX;
			const cosLat = Math.cos(lat * RAD);
			if (cosLat < 0.03) continue;
			for (let m = 0; m < 360; m += 45) {
				const hw = (m === 0 ? NORTH_HW : MERIDIAN_HW) / cosLat;
				g.fillStyle = m === 0 ? "#ff8c00" : "rgba(255,255,255,0.85)";
				g.fillRect(X(m - hw), row, 2 * hw * PX, 1);
				if (m === 0) g.fillRect(X(360 - hw), row, 2 * hw * PX, 1); // the seam at 0/360
			}
		}

		// Pitch ticks every 5 degrees: on every 45-degree meridian and halfway between two of them,
		// and on the numbered lines also a quarter of the way. Their lengths are in degrees of
		// longitude, so they shrink towards the poles with the spacing between them.
		g.fillStyle = "rgba(255,255,255,0.95)";
		const tick = (lat, lon, halfLon) => {
			g.fillRect(X(lon - halfLon), Y(lat) - TICK_HW * PX, 2 * halfLon * PX, 2 * TICK_HW * PX);
			if (lon - halfLon < 0) g.fillRect(X(360 + lon - halfLon), Y(lat) - TICK_HW * PX, 2 * halfLon * PX, 2 * TICK_HW * PX);
		};
		for (let lat = -85; lat <= 85; lat += 5) {
			if (lat === 0) continue;
			const absLat = Math.abs(lat);
			const long = absLat % 10 === 0;
			const numbered = long && absLat <= 80;
			for (let k = 0; k < 16; k++) {
				const lon = k * 22.5;
				const halfway = k % 2 === 1;
				// Halfway on a numbered line the number stands instead of the tick; at 80 only every
				// other gap carries a number.
				const hasNumber = numbered && (absLat < 75 || k % 4 === 1);
				if (!(halfway && hasNumber)) tick(lat, lon, long ? 3.4 : 1.7);
				// From 70 degrees on the number fills the space up to the quarter: no quarter tick.
				if (numbered && absLat < 65) tick(lat, lon + 11.25, 2.5);
			}
		}

		// The horizon: a clear but thin line.
		g.fillStyle = "#ffffff";
		g.fillRect(0, Y(0) - HORIZON_HW * PX, TEX_W, 2 * HORIZON_HW * PX);

		// Pitch numbers, white, centred halfway between two meridians, top towards the zenith. On the
		// map they are stretched across by 1/cos(latitude), which the sphere takes back: on the ball
		// they keep their shape and bend with their circle.
		g.fillStyle = "#ffffff"; g.textAlign = "center"; g.textBaseline = "middle";
		for (let lon = 0; lon < 360; lon += 45) {
			for (let lat = 10; lat <= 80; lat += 10) {
				// The circle at 80 is small: there only every other gap carries its number, a bit smaller.
				if (lat === 80 && lon % 90 !== 0) continue;
				const size = GLYPH_DEG * (lat === 80 ? 0.85 : 1) * PX;
				g.font = "bold " + Math.round(size) + "px 'Segoe UI', sans-serif";
				for (const [la, text] of [[lat, "+" + lat], [-lat, "−" + lat]]) {
					g.setTransform(1 / Math.cos(la * RAD), 0, 0, 1, X(lon + 22.5), Y(la));
					g.fillText(text, 0, 0);
				}
				g.setTransform(1, 0, 0, 1, 0, 0);
			}
		}
		texture = c;
		return c;
	}

	// ---- GPU: WebGL 2 ------------------------------------------------------------------------------

	const VERTEX = "#version 300 es\nin vec2 p; void main() { gl_Position = vec4(p, 0.0, 1.0); }";
	const FRAGMENT = `#version 300 es
precision highp float;
uniform sampler2D tex;
uniform vec3 uRight, uTop, uNose;
uniform vec2 uSize;
uniform float uCam, uLimb, uNear;
out vec4 color;
void main() {
	float sx = (gl_FragCoord.x / uSize.x * 2.0 - 1.0) * uLimb;
	float sy = (gl_FragCoord.y / uSize.y * 2.0 - 1.0) * uLimb;
	// Ray from the camera through this pixel, cut with the unit sphere.
	float a = sx / uCam, b = sy / uCam;
	float L = sqrt(a * a + b * b + 1.0);
	float disc = (uCam / L) * (uCam / L) - (uCam * uCam - 1.0);
	if (disc < 0.0) { color = vec4(0.0); return; }
	float t = uCam / L - sqrt(disc);
	vec3 v = vec3(t * a / L, t * b / L, uCam - t / L);
	vec3 w = v.x * uRight + v.y * uTop + v.z * uNose; // north, east, up
	float lat = asin(clamp(w.z, -1.0, 1.0));
	float lon = atan(w.y, w.x);
	// Two versions of the longitude, one with its seam at 0 and one at 180 degrees; the one that
	// does not jump at this pixel keeps the texture's filtering clean along the seam.
	float u1 = fract(lon / 6.28318531);
	float u2 = fract(lon / 6.28318531 + 0.5) - 0.5;
	float u = fwidth(u1) <= fwidth(u2) + 1e-6 ? u1 : u2;
	vec3 c = texture(tex, vec2(u, 0.5 - lat / 3.14159265)).rgb;
	// The ground and the sky darken a little towards the rim; the white lines do not.
	float light = 0.8 + 0.2 * (v.z - uNear) / (1.0 - uNear);
	float white = smoothstep(0.12, 0.9, min(c.r, min(c.g, c.b)));
	color = vec4(mix(c * light, c, white), 1.0);
}`;

	function makeGpu(stack, overlay) {
		if (window.NAVBALL_FORCE_CPU) return null;
		const glCanvas = document.createElement("canvas");
		fill(glCanvas);
		const gl = glCanvas.getContext("webgl2", { premultipliedAlpha: true, antialias: false });
		if (!gl) return null;
		const shader = (type, src) => {
			const s = gl.createShader(type);
			gl.shaderSource(s, src); gl.compileShader(s);
			if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
			return s;
		};
		try {
			const prog = gl.createProgram();
			gl.attachShader(prog, shader(gl.VERTEX_SHADER, VERTEX));
			gl.attachShader(prog, shader(gl.FRAGMENT_SHADER, FRAGMENT));
			gl.linkProgram(prog);
			if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
			gl.useProgram(prog);

			const buf = gl.createBuffer();
			gl.bindBuffer(gl.ARRAY_BUFFER, buf);
			gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
			const loc = gl.getAttribLocation(prog, "p");
			gl.enableVertexAttribArray(loc);
			gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

			const tex = gl.createTexture();
			gl.bindTexture(gl.TEXTURE_2D, tex);
			gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, paintTexture());
			gl.generateMipmap(gl.TEXTURE_2D);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
			gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
			const aniso = gl.getExtension("EXT_texture_filter_anisotropic");
			if (aniso) gl.texParameterf(gl.TEXTURE_2D, aniso.TEXTURE_MAX_ANISOTROPY_EXT, Math.min(8, gl.getParameter(aniso.MAX_TEXTURE_MAX_ANISOTROPY_EXT)));

			const u = (name) => gl.getUniformLocation(prog, name);
			const uni = { right: u("uRight"), top: u("uTop"), nose: u("uNose"), size: u("uSize") };
			gl.uniform1i(u("tex"), 0);
			gl.uniform1f(u("uCam"), CAM); gl.uniform1f(u("uLimb"), LIMB); gl.uniform1f(u("uNear"), NEAR);

			// The GL canvas lies under the overlay canvas, in the same frame.
			stack.insertBefore(glCanvas, overlay);

			return {
				draw(att, S) {
					if (glCanvas.width !== S) { glCanvas.width = S; glCanvas.height = S; }
					gl.viewport(0, 0, S, S);
					gl.uniform3fv(uni.right, att.right); gl.uniform3fv(uni.top, att.top); gl.uniform3fv(uni.nose, att.nose);
					gl.uniform2f(uni.size, S, S);
					gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
					gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
				},
				clear() { gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT); },
			};
		} catch (e) {
			console.warn("Navball: WebGL failed, drawing on the CPU instead.", e);
			return null;
		}
	}

	// ---- CPU fallback: the same texture, looked up per pixel ----------------------------------------

	let texPixels = null;
	const CPU_TEX_W = 2048, CPU_TEX_H = 1024;
	function cpuBall(ctx, att, S) {
		if (!texPixels) {
			const c = document.createElement("canvas");
			c.width = CPU_TEX_W; c.height = CPU_TEX_H;
			const g = c.getContext("2d");
			g.imageSmoothingQuality = "high";
			g.drawImage(paintTexture(), 0, 0, CPU_TEX_W, CPU_TEX_H);
			texPixels = g.getImageData(0, 0, CPU_TEX_W, CPU_TEX_H).data;
		}
		const { nose, right, top } = att;
		const img = ctx.createImageData(S, S);
		const px = img.data;
		for (let yy = 0; yy < S; yy++) {
			const sy = (1 - ((yy + 0.5) / S) * 2) * LIMB;
			for (let xx = 0; xx < S; xx++) {
				const sx = (((xx + 0.5) / S) * 2 - 1) * LIMB;
				const a = sx / CAM, b = sy / CAM;
				const L = Math.sqrt(a * a + b * b + 1);
				const disc = (CAM / L) * (CAM / L) - (CAM * CAM - 1);
				if (disc < 0) continue;
				const t = CAM / L - Math.sqrt(disc);
				const vx = (t * a) / L, vy = (t * b) / L, vz = CAM - t / L;
				const n = vx * right[0] + vy * top[0] + vz * nose[0];
				const e = vx * right[1] + vy * top[1] + vz * nose[1];
				const up = vx * right[2] + vy * top[2] + vz * nose[2];
				const lat = Math.asin(Math.max(-1, Math.min(1, up)));
				const lon = Math.atan2(e, n);
				let tx = Math.floor(((lon / (2 * Math.PI)) + 1) % 1 * CPU_TEX_W);
				let ty = Math.floor((0.5 - lat / Math.PI) * CPU_TEX_H);
				if (ty >= CPU_TEX_H) ty = CPU_TEX_H - 1;
				if (tx >= CPU_TEX_W) tx = CPU_TEX_W - 1;
				const j = (ty * CPU_TEX_W + tx) * 4;
				const r = texPixels[j], g = texPixels[j + 1], bl = texPixels[j + 2];
				const light = 0.8 + 0.2 * (vz - NEAR) / (1 - NEAR);
				const white = Math.max(0, Math.min(1, (Math.min(r, g, bl) / 255 - 0.12) / 0.78));
				const f = light + (1 - light) * white;
				const i = (yy * S + xx) * 4;
				px[i] = r * f; px[i + 1] = g * f; px[i + 2] = bl * f; px[i + 3] = 255;
			}
		}
		ctx.putImageData(img, 0, 0);
	}

	// ---- What stands upright to the player ----------------------------------------------------------

	function overlay(ctx, att, S) {
		ctx.save();
		ctx.beginPath(); ctx.arc(S / 2, S / 2, S / 2 - 4, 0, 2 * Math.PI); ctx.clip();

		// Heading numbers: large, black, centred where the meridian crosses the pitch line of 45, 0 or
		// minus 45 degrees, and always upright to the player.
		ctx.textBaseline = "middle"; ctx.textAlign = "center"; ctx.fillStyle = "#000000";
		for (let lon = 0; lon < 360; lon += 45)
			for (const lat of [45, 0, -45]) {
				const p = project(att, S, direction(lat, lon));
				if (!p || p.z < 0.3) continue;
				const size = Math.round(Math.max(14, Math.min(S * 0.11, p.scale * 3.6 * 2.0)));
				ctx.font = "bold " + size + "px 'Segoe UI', sans-serif";
				ctx.fillText(lon === 0 ? "N" : String(lon), p.x, p.y);
			}

		// Vector markers.
		const put = (vec, kind, color) => {
			if (!valid(vec)) return;
			const p = project(att, S, vec);
			if (p) marker(ctx, S, p.x, p.y, kind, color);
		};
		const neg = (v) => (valid(v) ? [-v[0], -v[1], -v[2]] : null);
		put(att.normal, "normal", "#c28cff"); put(neg(att.normal), "anti", "#c28cff");
		put(att.radial, "radout", "#5ee1ff"); put(neg(att.radial), "radin", "#5ee1ff");
		put(neg(att.prograde), "retro", "#c6f000"); put(att.prograde, "pro", "#c6f000");
		put(att.maneuver, "mnvr", "#3d8bff"); // the first maneuver node's burn direction
		// Where a drag on the ball has turned the vessel; held until the player steers with keys or joystick.
		targetMarker(ctx, att, S);
		ctx.restore();

		// The ball is seen through a round window with a dark rim.
		ctx.beginPath(); ctx.arc(S / 2, S / 2, S / 2 - 3, 0, 2 * Math.PI);
		ctx.lineWidth = 8; ctx.strokeStyle = "#1c2630"; ctx.stroke();

		// The vessel's own marker: wings and a diamond, in the middle. With the ball seen from somewhere
		// else (att.craft, the vessel's nose and top), half as wide where the vessel's nose points,
		// turned as its top is there; not drawn while the nose is on the far side.
		let x = S / 2, y = S / 2, turn = 0, span = 1;
		const craft = att.craft;
		if (craft) {
			if (!valid(craft.nose) || !valid(craft.top)) return;
			const p = project(att, S, craft.nose);
			if (!p) return;
			const q = project(att, S, [0, 1, 2].map((i) => craft.nose[i] + 0.05 * craft.top[i]));
			x = p.x; y = p.y; span = 0.5;
			if (q) turn = Math.atan2(q.x - p.x, p.y - q.y);
		}
		const wing = S * 0.012;
		ctx.save();
		ctx.translate(x, y); ctx.rotate(turn);
		ctx.fillStyle = "#ff9d1a";
		ctx.fillRect(-S * 0.30 * span, -wing / 2, S * 0.19 * span, wing);
		ctx.fillRect(S * 0.11 * span, -wing / 2, S * 0.19 * span, wing);
		ctx.beginPath(); ctx.moveTo(0, -S * 0.022); ctx.lineTo(S * 0.022, 0); ctx.lineTo(0, S * 0.022); ctx.lineTo(-S * 0.022, 0); ctx.closePath(); ctx.fill();
		ctx.restore();
	}

	// The target: a ring on the ball with a notch pointing where the vessel's top will be there. Drawn
	// on the ball, so it bends with it.
	function targetMarker(ctx, att, S) {
		const T = att.target, U = att.targetTop;
		if (!valid(T) || !valid(U)) return;
		const R = [U[1] * T[2] - U[2] * T[1], U[2] * T[0] - U[0] * T[2], U[0] * T[1] - U[1] * T[0]]; // the target's right
		// d degrees from the target, a degrees round from its top towards its right.
		const at = (a, d) => {
			const ca = Math.cos(a * RAD), sa = Math.sin(a * RAD), cd = Math.cos(d * RAD), sd = Math.sin(d * RAD);
			return [0, 1, 2].map((i) => T[i] * cd + (U[i] * ca + R[i] * sa) * sd);
		};
		const path = (dirs, close) => {
			ctx.beginPath();
			let open = false;
			for (const d of dirs) {
				const p = project(att, S, d);
				if (!p) { open = false; continue; }
				if (open) ctx.lineTo(p.x, p.y); else { ctx.moveTo(p.x, p.y); open = true; }
			}
			if (close) ctx.closePath();
		};
		const steps = (a, b, n) => Array.from({ length: n + 1 }, (_, i) => a + (b - a) * i / n);
		ctx.lineCap = "round"; ctx.lineJoin = "round";
		for (const [c, w] of [[RIM, 0.018], ["#ffffff", 0.009]]) { // white, with the markers' dark rim
			ctx.strokeStyle = ctx.fillStyle = c; ctx.lineWidth = S * w;
			path(steps(0, 360, 60).map((a) => at(a, 5)), true); ctx.stroke();
			path([at(0, 9), at(-18, 5.4), at(18, 5.4)], true); ctx.stroke(); ctx.fill();
		}
	}

	// The navball's markers, drawn after KSP's: a ring of radius R with ticks and inner strokes,
	// in units of R so they scale with the ball.
	// The dark rim of our own target marker (KSP's markers have none).
	const RIM = "#101418";
	function marker(ctx, S, cx, cy, kind, color) {
		markerShape(ctx, S, cx, cy, kind, color, 0);
	}
	function markerShape(ctx, S, cx, cy, kind, color, extra) {
		const R = S * 0.04, base = Math.max(2.5, S * 0.009);
		ctx.save();
		ctx.translate(cx, cy);
		ctx.strokeStyle = color; ctx.fillStyle = color;
		ctx.lineWidth = base + extra; ctx.lineCap = "round"; ctx.lineJoin = "round";
		const line = (x1, y1, x2, y2) => { ctx.moveTo(x1 * R, y1 * R); ctx.lineTo(x2 * R, y2 * R); };
		const ring = (r) => { ctx.moveTo(r * R, 0); ctx.arc(0, 0, r * R, 0, 2 * Math.PI); };
		const dot = () => { ctx.save(); ctx.beginPath(); ctx.arc(0, 0, R * 0.2 + extra / 2, 0, 2 * Math.PI); ctx.fill(); ctx.restore(); };
		const polar = (deg, r) => [Math.cos(deg * Math.PI / 180) * r, Math.sin(deg * Math.PI / 180) * r]; // y down
		ctx.beginPath();
		if (kind === "pro") {
			// Ring, centre dot, ticks up, left and right.
			ring(1); line(0, -1, 0, -1.7); line(-1, 0, -1.7, 0); line(1, 0, 1.7, 0); ctx.stroke(); dot();
		} else if (kind === "retro") {
			// Ring with three ticks outward, up and 120 degrees either side, and a thin X inside.
			ring(1);
			for (const a of [-90, 30, 150]) { const [x1, y1] = polar(a, 1), [x2, y2] = polar(a, 1.7); line(x1, y1, x2, y2); } // as long as prograde's
			ctx.stroke();
			ctx.beginPath(); ctx.lineWidth = base / 2 + extra;
			const d = Math.SQRT1_2;
			line(-d, -d, d, d); line(d, -d, -d, d);
			ctx.stroke();
		} else if (kind === "normal") {
			// Upward triangle with a centre dot.
			const p = [-90, 30, 150].map((a) => polar(a, 1.15));
			ctx.moveTo(p[0][0] * R, p[0][1] * R); p.slice(1).forEach((q) => ctx.lineTo(q[0] * R, q[1] * R)); ctx.closePath();
			ctx.stroke(); dot();
		} else if (kind === "anti") {
			// Downward triangle, a tick outward from the middle of each side, and a centre dot.
			const p = [90, 210, 330].map((a) => polar(a, 1.11));
			ctx.moveTo(p[0][0] * R, p[0][1] * R); p.slice(1).forEach((q) => ctx.lineTo(q[0] * R, q[1] * R)); ctx.closePath();
			for (const a of [-90, 30, 150]) { const [x1, y1] = polar(a, 0.555), [x2, y2] = polar(a, 1.15); line(x1, y1, x2, y2); }
			ctx.stroke(); dot();
		} else if (kind === "radout") {
			// Ring, centre dot, four ticks outward on the diagonals.
			ring(1);
			for (const a of [45, 135, 225, 315]) { const [x1, y1] = polar(a, 1), [x2, y2] = polar(a, 1.65); line(x1, y1, x2, y2); }
			ctx.stroke(); dot();
		} else if (kind === "radin") {
			// Ring with four ticks inward on the diagonals.
			ring(1);
			for (const a of [45, 135, 225, 315]) { const [x1, y1] = polar(a, 1), [x2, y2] = polar(a, 0.45); line(x1, y1, x2, y2); }
			ctx.stroke();
		} else if (kind === "tgt") {
			// Target: four arcs round a large dot, open at the four sides.
			for (let q = 0; q < 4; q++) { const a0 = q * Math.PI / 2 + 0.2, a1 = (q + 1) * Math.PI / 2 - 0.2; ctx.moveTo(Math.cos(a0) * 1.25 * R, Math.sin(a0) * 1.25 * R); ctx.arc(0, 0, 1.25 * R, a0, a1); }
			ctx.stroke();
			ctx.beginPath(); ctx.arc(0, 0, R * 0.375 + extra / 2, 0, 2 * Math.PI); ctx.fill();
		} else if (kind === "atgt") {
			// Anti-target: a large dot and three ticks, down and 120 degrees either side.
			for (const a of [90, 210, 330]) { const [x1, y1] = polar(a, 0.75), [x2, y2] = polar(a, 1.75); line(x1, y1, x2, y2); }
			ctx.stroke();
			ctx.beginPath(); ctx.arc(0, 0, R * 0.375 + extra / 2, 0, 2 * Math.PI); ctx.fill();
		} else if (kind === "stab") {
			// Stability assist: a ring with a wave through it.
			ring(1.15);
			ctx.moveTo(-0.75 * R, 0);
			ctx.bezierCurveTo(-0.4 * R, -0.65 * R, -0.05 * R, -0.65 * R, 0, 0);
			ctx.bezierCurveTo(0.05 * R, 0.65 * R, 0.4 * R, 0.65 * R, 0.75 * R, 0);
			ctx.stroke();
		} else if (kind === "mnvr") {
			// Maneuver: three T shapes round a centre dot, their bars outward (up and 120 degrees
			// either side), their thin stems pointing in.
			for (const a of [-90, 30, 150]) {
				const [cx1, cy1] = polar(a, 1.05), [tx, ty] = polar(a + 90, 0.65);
				line(cx1 - tx, cy1 - ty, cx1 + tx, cy1 + ty);
			}
			ctx.stroke();
			ctx.beginPath(); ctx.lineWidth = base / 2 + extra;
			for (const a of [-90, 30, 150]) { const [x1, y1] = polar(a, 1.05), [x2, y2] = polar(a, 0.4); line(x1, y1, x2, y2); }
			ctx.stroke(); dot();
		}
		ctx.restore();
	}

	// ---- Entry point ------------------------------------------------------------------------------

	// Per overlay canvas: its frame, its GPU renderer (or null for the CPU) and what was drawn last.
	const states = new WeakMap();

	function fill(el) {
		const s = el.style;
		s.position = "absolute"; s.left = "0"; s.top = "0"; s.width = "100%"; s.height = "100%";
		s.maxWidth = "none"; s.pointerEvents = "none";
	}

	// The overlay canvas moves into a square frame that takes its place; the GL canvas lies in the
	// same frame, so the two always cover each other exactly.
	function setUp(canvas) {
		const stack = document.createElement("div");
		stack.style.position = "relative"; stack.style.flex = "0 0 auto";
		canvas.parentNode.insertBefore(stack, canvas);
		stack.appendChild(canvas);
		fill(canvas);
		return { stack, gpu: makeGpu(stack, canvas), key: null, room: null, cssSize: 0 };
	}

	function readouts(att) {
		const { nose, right, top } = att;
		return {
			pitch: Math.asin(Math.max(-1, Math.min(1, nose[2]))) * DEG,
			heading: (Math.atan2(nose[1], nose[0]) * DEG + 360) % 360,
			roll: Math.atan2(right[2], top[2]) * DEG,
		};
	}

	function draw(canvas, att) {
		let st = states.get(canvas);
		if (!st) { st = setUp(canvas); states.set(canvas, st); }
		const ok = att && valid(att.nose) && valid(att.right) && valid(att.top);

		// The frame is the largest square that fits the space it sits in, and the canvases are drawn
		// at the size they are shown at (the CPU keeps to CPU_MAX). The space is looked at again only
		// when it changes.
		const parent = st.stack.parentNode;
		const room = parent.clientWidth + "x" + parent.clientHeight;
		if (st.room !== room) {
			st.room = room;
			st.cssSize = Math.max(1, Math.min(parent.clientWidth, parent.clientHeight));
			st.stack.style.width = st.cssSize + "px"; st.stack.style.height = st.cssSize + "px";
			st.key = null;
		}
		const shown = Math.round(st.cssSize * (window.devicePixelRatio || 1));
		const S = st.gpu ? Math.max(200, Math.min(1400, shown)) : Math.min(CPU_MAX, shown);

		// Nothing to do while nothing changed.
		const key = !ok ? "none" : S + "|" + ["nose", "right", "top", "prograde", "normal", "radial", "maneuver", "target", "targetTop"]
			.map((k) => (valid(att[k]) ? att[k].map((x) => x.toFixed(4)).join(",") : "-")).join("|")
			+ (att.craft ? "|" + [att.craft.nose, att.craft.top].map((v) => (valid(v) ? v.map((x) => x.toFixed(4)).join(",") : "-")).join("|") : "");
		if (key === st.key) return ok ? readouts(att) : null;
		st.key = key;

		if (canvas.width !== S) { canvas.width = S; canvas.height = S; }
		const ctx = canvas.getContext("2d");
		ctx.clearRect(0, 0, S, S);
		if (!ok) { if (st.gpu) st.gpu.clear(); return null; }

		if (st.gpu) st.gpu.draw(att, S);
		else cpuBall(ctx, att, S);
		overlay(ctx, att, S);
		return readouts(att);
	}

	// A marker as a picture (a data URL, white on transparent) for buttons.
	function icon(kind, size) {
		const c = document.createElement("canvas");
		c.width = c.height = size;
		markerShape(c.getContext("2d"), size * 6, size / 2, size / 2, kind, "#ffffff", 0); // the marker spans about 4 R of the 6 R wide picture
		return c.toDataURL();
	}

	window.Navball = { draw, icon, mode: (canvas) => (states.get(canvas) && states.get(canvas).gpu ? "gpu" : "cpu") };
})();
