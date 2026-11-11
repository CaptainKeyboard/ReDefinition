"""The metric contract of the weather player's series (tools/build_weather_player.py --series <name>).

A series is a list of player runs (stages) and a collector that turns what they wrote (frames, logs) into named metrics, each with a
limit and a verdict. The runner prints ONE table and exits 0 only if every metric passed. A package adds its own module here
(tools/weather_metrics/<package>.py) and registers its series; it edits neither this file nor build_weather_player.py:

    from weather_metrics import Metric, Stage, register_series

    def collect(ctx):
        frames = ctx.folder("stills")                    # the folder the stage labelled "stills" wrote
        return [Metric.at_most("storm_luma", 0.31, 0.35), Metric.reference("edge", 0.026, 0.026, rel=0.10)]

    register_series("storm", [Stage("stills", ["-mode", "stills", "-views", "far"])], collect, exclusive=False)

Verdict modes of a Metric: "max" (value <= limit), "min" (value >= limit), "ref" (|value - limit| <= max(rel * |limit|, floor): the
limit is the known value), "info" (no verdict; always passes) and "flag" (value is 1 where the condition holds, limit is 1). A value
that is NaN or missing fails.
"""
import glob
import importlib
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


class Metric(object):
    """One named figure of a series with its limit and its verdict."""

    def __init__(self, name, value, limit=None, mode="info", rel=0.0, floor=0.0):
        self.name = name
        self.value = value
        self.limit = limit
        self.mode = mode
        self.rel = rel
        self.floor = floor

    @staticmethod
    def at_most(name, value, limit):
        return Metric(name, value, limit, "max")

    @staticmethod
    def at_least(name, value, limit):
        return Metric(name, value, limit, "min")

    @staticmethod
    def reference(name, value, known, rel=0.10, floor=0.0):
        """Passes where the value lies within rel of the known value (or within floor, where that is larger)."""
        return Metric(name, value, known, "ref", rel, floor)

    @staticmethod
    def flag(name, ok):
        return Metric(name, 1.0 if ok else 0.0, 1.0, "flag")

    @staticmethod
    def info(name, value):
        return Metric(name, value, None, "info")

    @staticmethod
    def given(name, value, limit_text, verdict):
        """A metric the player judged itself: its limit text and its verdict (true, false or info) are taken as they are."""
        metric = Metric(name, value, limit_text, "given")
        metric.verdict = verdict
        return metric

    def passed(self):
        """The verdict; a value that is not a finite number fails every mode but info."""
        if self.mode == "info":
            return True
        if self.mode == "given":
            return self.verdict in ("true", "info")
        v = self.value
        if v is None or not isinstance(v, (int, float)) or math.isnan(v) or math.isinf(v):
            return False
        if self.mode == "max":
            return v <= self.limit
        if self.mode == "min":
            return v >= self.limit
        if self.mode == "ref":
            return abs(v - self.limit) <= max(self.rel * abs(self.limit), self.floor)
        if self.mode == "flag":
            return v == self.limit
        raise ValueError("unknown mode " + self.mode)

    def limit_text(self):
        if self.mode == "given":
            return self.limit
        if self.mode == "info" or self.limit is None:
            return "-"
        if self.mode == "max":
            return "<= %.4g" % self.limit
        if self.mode == "min":
            return ">= %.4g" % self.limit
        if self.mode == "flag":
            return "== 1"
        tol = max(self.rel * abs(self.limit), self.floor)
        return "%.4g +- %.2g" % (self.limit, tol)

    def as_dict(self):
        return {"metric": self.name, "value": self.value, "limit": self.limit_text(), "mode": self.mode, "pass": self.passed()}


class Stage(object):
    """One player run of a series: a label (the output folder), the player's arguments, the render size, the proxy ini and whether
    the run needs the Streamline folder (an upscaler stage)."""

    def __init__(self, label, extra, size=(1280, 720), upscaler=False, timeout=1200, proxy=True):
        self.label = label
        self.extra = list(extra)
        self.size = size
        self.upscaler = upscaler
        self.timeout = timeout
        # False: the run has no dxgi.dll beside the player (no timestamps, no upscaler through the proxy).
        self.proxy = proxy


class Context(object):
    """What a collector receives: the folders and the WEATHERPLAYER metric lines of each stage."""

    def __init__(self, series, folders, player_lines, ok):
        self.series = series
        self.folders = folders
        self.player_lines = player_lines
        self.stage_ok = ok

    def folder(self, label):
        return self.folders[label]

    def player_metrics(self, label):
        """The {name,value,limit,pass} lines the player wrote in that stage, as {name: (value, limit, pass)}."""
        out = {}
        for line in self.player_lines.get(label, []):
            if line.startswith("{") and line.endswith("}"):
                parts = line[1:-1].split(",")
                if len(parts) == 4:
                    try:
                        out[parts[0]] = (float(parts[1]), parts[2], parts[3])
                    except ValueError:
                        out[parts[0]] = (float("nan"), parts[2], parts[3])
        return out


SERIES = {}


def register_series(name, stages, collect, exclusive=False, who="B11"):
    """Registers a series: its stages, the collector (Context -> list of Metric) and whether it is a timing run (exclusive lock)."""
    SERIES[name] = {"stages": list(stages), "collect": collect, "exclusive": exclusive}


def discover():
    """Imports every module of this folder (core first), so that their series are registered; returns the registry."""
    parent = os.path.dirname(HERE)
    if parent not in sys.path:
        sys.path.insert(0, parent)
    names = ["core"] + sorted(os.path.splitext(os.path.basename(p))[0] for p in glob.glob(os.path.join(HERE, "*.py"))
                              if os.path.basename(p) not in ("__init__.py", "core.py"))
    for name in names:
        importlib.import_module("weather_metrics." + name)
    return SERIES


def table(metrics):
    """The one table of a series: metric, value, limit, PASS/FAIL."""
    width = max([len(m.name) for m in metrics] + [6])
    lines = ["%-*s %12s  %-16s %s" % (width, "metric", "value", "limit", "verdict")]
    for m in metrics:
        value = "-" if m.value is None else ("%.5g" % m.value)
        info = m.mode == "info" or (m.mode == "given" and m.verdict == "info")
        lines.append("%-*s %12s  %-16s %s" % (width, m.name, value, m.limit_text(), "info" if info else ("PASS" if m.passed() else "FAIL")))
    return "\n".join(lines)
