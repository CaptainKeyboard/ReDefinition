#!/usr/bin/env python3
"""The loader guard of a game series start: it reads the KSP.log while the game loads and says at once when the load is lost, so that
the runner kills the game instead of waiting for the timeout (starts 37 and 38 of K-T2 each cost 20+ minutes).

A load is lost when
  - more than LOAD_FAILED_MAX 'LOAD FAILED' lines appear (a healthy load has none; KSPCF logs one per texture it believes cached),
  - 'Cannot replace texture ... as cannot find texture' was logged and a NullReferenceException in PartLoader.ReplaceTextures follows,
  - the loading scene lasts longer than LOADING_MAX_SECONDS and the log has had no new line for STALL_SECONDS.
The loading scene ends with the line 'Scene Change : From LOADING to ...'. The class takes the time as an argument, so tests need no clock."""
import os

LOAD_FAILED_MAX = 20
LOADING_MAX_SECONDS = 240
STALL_SECONDS = 60
NRE_LOOKAHEAD = 4  # lines after the NullReferenceException line in which PartLoader.ReplaceTextures must stand

LOAD_FAILED = "LOAD FAILED"
CANNOT_REPLACE = "Cannot replace texture"
CANNOT_FIND = "as cannot find texture"
NRE = "NullReferenceException"
REPLACE_TEXTURES = "PartLoader.ReplaceTextures"
LOADING_DONE = "Scene Change : From LOADING to"


class LogGuard:
    def __init__(self, now=0.0):
        self.load_failed = 0
        self.cannot_replace = False
        self.nre_lines_left = 0
        self.loading = True
        self.started = now
        self.last_growth = now
        self.reason = None

    def feed_line(self, line):
        """One log line. Returns the reason to abort, or None."""
        if self.reason:
            return self.reason
        if LOAD_FAILED in line:
            self.load_failed += 1
            if self.load_failed > LOAD_FAILED_MAX:
                self.reason = "%d 'LOAD FAILED' lines (more than %d): the texture cache lists files that do not exist" % (
                    self.load_failed, LOAD_FAILED_MAX)
        if CANNOT_REPLACE in line and CANNOT_FIND in line:
            self.cannot_replace = True
        if self.nre_lines_left > 0:
            self.nre_lines_left -= 1
            if REPLACE_TEXTURES in line and self.cannot_replace and not self.reason:
                self.reason = "'Cannot replace texture ... cannot find texture' followed by a NullReferenceException in PartLoader.ReplaceTextures"
        if NRE in line:
            self.nre_lines_left = NRE_LOOKAHEAD
        if LOADING_DONE in line:
            self.loading = False
        return self.reason

    def feed_text(self, text, now):
        """New log text (any number of lines). Returns the reason to abort, or None."""
        if text:
            self.last_growth = now
        for line in text.splitlines():
            self.feed_line(line)
        return self.tick(now)

    def tick(self, now):
        """The stall rule; call it with every poll, also when the log did not grow."""
        if self.reason:
            return self.reason
        if self.loading and now - self.started > LOADING_MAX_SECONDS and now - self.last_growth > STALL_SECONDS:
            self.reason = "the loading scene lasts %d s and the log has had no new line for %d s" % (now - self.started, now - self.last_growth)
        return self.reason


class LogTail:
    """Reads what a growing log file gained since the last call (whole lines only; a file that shrank is read from its start)."""

    def __init__(self, path):
        self.path = path
        self.offset = 0
        self.rest = b""

    def read_new(self):
        try:
            size = os.path.getsize(self.path)
            if size < self.offset:
                self.offset, self.rest = 0, b""
            with open(self.path, "rb") as f:
                f.seek(self.offset)
                data = f.read()
        except OSError:
            return ""
        self.offset += len(data)
        data = self.rest + data
        cut = data.rfind(b"\n")
        if cut < 0:
            self.rest = data
            return ""
        self.rest = data[cut + 1:]
        return data[:cut + 1].decode("utf-8", errors="replace")
