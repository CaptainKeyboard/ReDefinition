"""The KSP install a run uses: the window kept in front, the settings files' snapshot and restore, the launch and the
DLSS-G package. Windows only where the window is concerned."""
import datetime
import filecmp
import hashlib
import json
import os
import re
import shutil
import subprocess
import threading
import time

from .constants import (FOREGROUND_RETRY_SECONDS, FOREGROUND_SECONDS, PACKAGE_DIR, PACKAGE_FILES, PACKAGE_SUBDIRS,
                        SNAP_EXT, SNAP_SKIP)
from .files import set_package_ini_value


class _Win32:
    """The few user32 calls that bring a process's window forward. Windows only."""

    def __init__(self):
        """Loads user32 and kernel32 and declares the argument and return types of the calls used."""
        import ctypes
        from ctypes import wintypes
        self.ctypes = ctypes
        self.wintypes = wintypes
        self.user32 = user32 = ctypes.WinDLL("user32", use_last_error=True)
        self.kernel32 = kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        user32.GetWindowThreadProcessId.restype = wintypes.DWORD
        user32.GetForegroundWindow.restype = wintypes.HWND
        for name in ("IsWindowVisible", "IsIconic", "SetForegroundWindow", "GetWindowTextLengthW"):
            getattr(user32, name).argtypes = [wintypes.HWND]
        user32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
        user32.GetWindow.restype = wintypes.HWND
        user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
        user32.AttachThreadInput.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.BOOL]
        kernel32.GetCurrentThreadId.restype = wintypes.DWORD

    def find_window(self, pid):
        """The process's top-level window, or None."""
        ctypes, wintypes, user32 = self.ctypes, self.wintypes, self.user32
        found = []
        visit_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        def visit(hwnd, _):
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            # Visible, titled and not owned by another window: the game's own, not a tool window.
            if (owner.value == pid and user32.IsWindowVisible(hwnd) and not user32.GetWindow(hwnd, 4)
                    and user32.GetWindowTextLengthW(hwnd) > 0):
                found.append(hwnd)
            return True

        user32.EnumWindows.argtypes = [visit_type, wintypes.LPARAM]
        user32.EnumWindows(visit_type(visit), 0)
        return found[0] if found else None

    def foreground(self):
        """The window in front now."""
        return self.user32.GetForegroundWindow()

    def minimised(self, hwnd):
        """Whether the window is minimised to the taskbar."""
        return bool(self.user32.IsIconic(hwnd))

    def restore(self, hwnd):
        """Shows a minimised window again at its size and place."""
        self.user32.ShowWindow(hwnd, 9)   # SW_RESTORE

    def set_foreground(self, hwnd):
        """Asks Windows to put the window in front; True where it is in front afterwards."""
        self.user32.SetForegroundWindow(hwnd)
        return self.user32.GetForegroundWindow() == hwnd

    def set_foreground_attached(self, hwnd):
        """Sharing the input queue of the window in front is what lets this process take the foreground."""
        user32 = self.user32
        front = user32.GetForegroundWindow()
        front_thread = user32.GetWindowThreadProcessId(front, None) if front else 0
        mine = self.kernel32.GetCurrentThreadId()
        attached = bool(front_thread) and front_thread != mine and user32.AttachThreadInput(mine, front_thread, True)
        try:
            return self.set_foreground(hwnd)
        finally:
            if attached:
                user32.AttachThreadInput(mine, front_thread, False)

    def alt_tap(self, hwnd):
        """An Alt press, which Windows counts as input."""
        self.user32.keybd_event(0x12, 0, 0, 0)
        self.user32.keybd_event(0x12, 0, 2, 0)
        return self.set_foreground(hwnd)


class Foreground:
    """Keeps a process's window in front, as little intrusively as it can. NVIDIA's DLSS-G
    generates nothing while the game's window is not in front (Streamline: "DLSS-G disabled:
    window not focused"). A KSP that this tool starts is not given the foreground when another
    program's window is in front. The method keep() does nothing while the window is in front.
    Otherwise it restores the window only if it is minimised, asks plainly, then asks with the
    input queue of the window in front shared. When both failed, it taps Alt, only once for a
    process. After a failure it leaves the window alone for FOREGROUND_RETRY_SECONDS."""

    def __init__(self, pid, win=None, clock=time.monotonic):
        """For the process pid; win and clock stand in for _Win32 and time.monotonic in the tests."""
        self.pid = pid
        self.win = win
        self.clock = clock
        self.alt_tapped = False
        self.retry_at = 0.0

    def keep(self):
        """True where the window is in front afterwards; False where there is none, or Windows did
        not allow it."""
        if self.win is None:
            if os.name != "nt":
                return False
            self.win = _Win32()
        win = self.win
        hwnd = win.find_window(self.pid)
        if hwnd is None:
            return False
        if win.foreground() == hwnd:
            self.retry_at = 0.0
            return True
        if self.clock() < self.retry_at:
            return False
        if win.minimised(hwnd):
            win.restore(hwnd)
        ok = win.set_foreground(hwnd) or win.set_foreground_attached(hwnd)
        if not ok and not self.alt_tapped:
            self.alt_tapped = True
            ok = win.alt_tap(hwnd)
        self.retry_at = 0.0 if ok else self.clock() + FOREGROUND_RETRY_SECONDS
        return ok


def bring_to_front(pid):
    """Makes the top-level window of the process the foreground window once (Foreground). False
    where there is no such window, or Windows did not allow it. Windows only."""
    return Foreground(pid).keep()


class Install:
    """The KSP install a run uses: its settings files' snapshot and restore, the launch, and the
    DLSS-G package put next to KSP_x64.exe and taken away again."""

    def __init__(self, ksp):
        """For the KSP folder ksp."""
        self.ksp = ksp

    def path(self, *parts):
        """A path inside the KSP folder."""
        return os.path.join(self.ksp, *parts)

    def settings_files(self):
        """The settings files of the install (SNAP_EXT), relative to its folder: those in the folder
        itself, and every one under GameData and PluginData."""
        for name in os.listdir(self.ksp):
            p = os.path.join(self.ksp, name)
            if os.path.isfile(p) and name.lower().endswith(SNAP_EXT):
                yield name
        for top in ("GameData", "PluginData"):
            for root, _dirs, names in os.walk(os.path.join(self.ksp, top)):
                for n in names:
                    if n.lower().endswith(SNAP_EXT):
                        yield os.path.relpath(os.path.join(root, n), self.ksp)

    def snap(self, out):
        """Copies every settings file into out/files and writes out/snap.json with the snapshot's time;
        returns the number of files copied."""
        count = 0
        for rel in self.settings_files():
            dst = os.path.join(out, "files", rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(os.path.join(self.ksp, rel), dst)
            count += 1
        # The time is the file system's own, as a file written later gets it: time.time()
        # runs ahead of a file's coarse clock, and a file written just after the snapshot
        # would then look older than it. A file made once the clock has ticked is later
        # than the marker, and every file made before it is not.
        marker = os.path.join(out, "snap.json")
        with open(marker, "w") as f:
            json.dump({"time": time.time()}, f)
        taken = os.path.getmtime(marker)
        probe = os.path.join(out, "tick")
        for _ in range(200):
            with open(probe, "w"):
                pass
            if os.path.getmtime(probe) > taken:
                break
            time.sleep(0.005)
        os.remove(probe)
        with open(marker, "w") as f:
            json.dump({"time": taken}, f)
        os.utime(marker, (taken, taken))
        return count

    def restore(self, out):
        """Puts the snapshot in out back: changed files copied over, settings files made since removed,
        other files written since only named. Returns the lines that say what was done."""
        with open(os.path.join(out, "snap.json")) as f:
            since = json.load(f)["time"]
        kept = os.path.join(out, "files")
        report = []
        for root, _dirs, names in os.walk(kept):
            for n in names:
                src = os.path.join(root, n)
                rel = os.path.relpath(src, kept)
                dst = os.path.join(self.ksp, rel)
                if not os.path.exists(dst) or not filecmp.cmp(src, dst, shallow=False):
                    shutil.copy2(src, dst)
                    report.append("restored " + rel)
        # A settings file the run made where there was none: the snapshot took every
        # file of its kind, so it did not exist before. The two seconds allow for the
        # file system's coarser clock.
        for rel in list(self.settings_files()):
            p = self.path(rel)
            if not os.path.exists(os.path.join(kept, rel)) and os.path.getmtime(p) >= since - 2.0:
                os.remove(p)
                report.append("removed " + rel)
        # The rest is KSP's own (squadcorefx.ksp, Launcher_Data's backups): only named.
        for root, _dirs, names in os.walk(self.ksp):
            rel_root = os.path.relpath(root, self.ksp)
            if rel_root.split(os.sep)[0] in SNAP_SKIP:
                continue
            for n in names:
                p = os.path.join(root, n)
                rel = os.path.relpath(p, self.ksp)
                if os.path.getmtime(p) > since and not os.path.exists(os.path.join(kept, rel)):
                    report.append("written since, not kept: " + rel)
        return report

    def running(self):
        """Whether a KSP_x64.exe is running, as tasklist sees it; False where tasklist cannot be run."""
        try:
            out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KSP_x64.exe"], capture_output=True, text=True).stdout
        except OSError:
            return False
        return "KSP_x64.exe" in out

    def launch(self, args, timeout):
        """KSP_x64.exe with these arguments, from its own folder, until it quits."""
        proc = subprocess.Popen([self.path("KSP_x64.exe")] + args, cwd=self.ksp,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        # The game's window stays in the foreground for as long as it runs (bring_to_front):
        # DLSS-G generates no frame otherwise, and a start of the run that no one watched
        # would measure the frames it renders alone. REDEFINITION_BENCH_NO_FOREGROUND leaves
        # the window where Windows puts it, to reproduce that.
        done = threading.Event()
        foreground = Foreground(proc.pid)

        def keep_in_front():
            while not done.wait(FOREGROUND_SECONDS):
                try:
                    foreground.keep()
                except Exception:
                    pass

        if not os.environ.get("REDEFINITION_BENCH_NO_FOREGROUND"):
            threading.Thread(target=keep_in_front, daemon=True).start()
        try:
            proc.wait(timeout=timeout)
            return "quit"
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
            return "killed after {} s".format(timeout)
        except KeyboardInterrupt:
            # KSP writes its settings when it quits: closed first, so that the restore
            # after this is the last to write them.
            proc.kill()
            proc.wait()
            raise
        finally:
            done.set()

    def package_in_place(self):
        """The DLSS-G package's files and folder that lie next to KSP_x64.exe now."""
        return [n for n in PACKAGE_FILES + (PACKAGE_DIR,) if os.path.exists(self.path(n))]

    def put_package(self, package, max_generated_frames, ini_values=None):
        """Copies the DLSS-G package from its folder next to KSP_x64.exe, its ini with MaxGeneratedFrames
        and the start's own sections, keys and values set."""
        # A copy an earlier start left half done goes first.
        self.remove_package()
        shutil.copy2(os.path.join(package, "version.dll"), self.path("version.dll"))
        with open(os.path.join(package, "dlssg_sm86.ini"), encoding="utf-8") as f:
            text = f.read()
        if max_generated_frames is not None:
            text = re.sub(r"(?m)^MaxGeneratedFrames=.*$", "MaxGeneratedFrames={}".format(max_generated_frames), text)
        # The start's own values: sections, keys and values the package's ini is read with.
        for section, keys in (ini_values or {}).items():
            for key, value in keys.items():
                text = set_package_ini_value(text, section, key, value)
        with open(self.path("dlssg_sm86.ini"), "w", encoding="utf-8") as f:
            f.write(text)
        for sub in PACKAGE_SUBDIRS:
            shutil.copytree(os.path.join(package, PACKAGE_DIR, sub), self.path(PACKAGE_DIR, sub))

    def remove_package(self):
        """Deletes the DLSS-G package's files and folder from next to KSP_x64.exe, those that are there."""
        for n in PACKAGE_FILES:
            if os.path.exists(self.path(n)):
                os.remove(self.path(n))
        if os.path.isdir(self.path(PACKAGE_DIR)):
            shutil.rmtree(self.path(PACKAGE_DIR))

    def build_identity(self):
        """The proxy's dxgi.dll and the mod's ReDefinition.dll in the install: the start of each one's
        SHA-256 and its modification time, by path."""
        out = {}
        for rel in ("dxgi.dll", os.path.join("GameData", "ReDefinition", "Plugins", "ReDefinition.dll")):
            p = self.path(rel)
            if os.path.exists(p):
                with open(p, "rb") as f:
                    digest = hashlib.sha256(f.read()).hexdigest()[:16]
                out[rel.replace(os.sep, "/")] = {
                    "sha256": digest,
                    "modified": datetime.datetime.fromtimestamp(os.path.getmtime(p)).isoformat(timespec="seconds")}
        return out

    def latest_capture_run(self):
        """The newest "-run" folder under ReDefinitionCaptures (where the probe's shots go), or None."""
        root = self.path("ReDefinitionCaptures")
        if not os.path.isdir(root):
            return None
        runs = [os.path.join(root, d) for d in os.listdir(root) if d.endswith("-run")]
        return max(runs, key=os.path.getmtime) if runs else None
