#!/usr/bin/env python3
"""The GPU/Unity lock of the Atmospheres work: one holder at a time may run the test player or the game (kind gpu) or a Unity
batch build (kind unity). Stdlib only.

  python tools/gpu_lock.py run --who <id> --max-minutes N [--exclusive] [--kind gpu|unity] -- <command ...>
  python tools/gpu_lock.py acquire --who <id> --max-minutes N [--exclusive] [--kind gpu|unity] [--pid N] [--wait-minutes N]
  python tools/gpu_lock.py release --who <id> [--kind gpu|unity] [--force]
  python tools/gpu_lock.py status [--json]
  python tools/gpu_lock.py selftest

The lock files are <MAINREPO>/.local/work/gpu.lock and unity.lock, by absolute path: MAINREPO is the environment variable
ATMO_MAINREPO, else the repository the script's git worktree belongs to. Every worktree therefore sees the same files. A lock file
holds JSON: id, pid, since, expires, exclusive, kind, command. It expires max-minutes plus a 5-minute margin after it was taken. A
lock whose process is dead or that has expired is stale; the next user takes it over (under a short guard file, so that two
takers do not both win). --exclusive marks a timing run: tools/dotnet_test.py and other builders wait while an exclusive lock is
held (status shows it). --lock-path is accepted only together with --selftest, for the temporary lock of the self test.
"""
import argparse
import ctypes
import json
import os
import subprocess
import sys
import tempfile
import time

MARGIN_SECONDS = 300
GUARD_STALE_SECONDS = 30
WAIT_REPORT_SECONDS = 30
KINDS = ("gpu", "unity")
FALLBACK_MAINREPO = r"C:\Users\franz\IdeaProjects\ReDefinition"


def main_repo():
    """The main repository by absolute path: ATMO_MAINREPO, else the parent of the git common dir of this script's worktree."""
    env = os.environ.get("ATMO_MAINREPO")
    if env:
        return os.path.abspath(env)
    here = os.path.dirname(os.path.abspath(__file__))
    try:
        out = subprocess.run(["git", "-C", here, "rev-parse", "--git-common-dir"], capture_output=True, text=True, timeout=20).stdout.strip()
        if out:
            common = out if os.path.isabs(out) else os.path.join(here, out)
            common = os.path.abspath(common)
            if os.path.basename(common) == ".git":
                return os.path.dirname(common)
    except (OSError, subprocess.TimeoutExpired):
        pass
    return FALLBACK_MAINREPO


def lock_path(kind, override=None):
    """The lock file of a kind. The override (selftest only) names the file itself."""
    if override:
        return os.path.abspath(override)
    return os.path.join(main_repo(), ".local", "work", "%s.lock" % kind)


def pid_alive(pid):
    """Whether a process with this id runs."""
    if not isinstance(pid, int) or pid <= 0:
        return False
    if os.name == "nt":
        kernel32 = ctypes.windll.kernel32
        kernel32.OpenProcess.restype = ctypes.c_void_p
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return ctypes.get_last_error() == 5 or kernel32.GetLastError() == 5  # access denied: it exists
        try:
            code = ctypes.c_ulong()
            if not kernel32.GetExitCodeProcess(ctypes.c_void_p(handle), ctypes.byref(code)):
                return True
            return code.value == 259  # STILL_ACTIVE
        finally:
            kernel32.CloseHandle(ctypes.c_void_p(handle))
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def read_lock(path):
    """The lock file's JSON as a dict, or None where there is none or it cannot be read."""
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else None
    except (OSError, ValueError):
        return None


def is_stale(info, now=None):
    """Whether a lock's holder is gone: its process is dead or its time is up."""
    now = time.time() if now is None else now
    return (not pid_alive(info.get("pid"))) or now > float(info.get("expires", 0))


def describe(info, now=None):
    """One line about a holder."""
    now = time.time() if now is None else now
    left = float(info.get("expires", 0)) - now
    state = "STALE" if is_stale(info, now) else "held"
    return "%s by %s (pid %s, %s%s, since %s, %s%.0f min left)" % (
        info.get("kind", "?"), info.get("id"), info.get("pid"), "exclusive" if info.get("exclusive") else "shared", "",
        time.strftime("%H:%M:%S", time.localtime(float(info.get("since", 0)))), "" if left >= 0 else "-", abs(left) / 60.0) + " " + state


def _write_new(path, info):
    """Creates the lock file with its whole content or fails with FileExistsError. A hard link of a finished temporary file
    keeps a reader from seeing half a file."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    text = json.dumps(info, indent=1)
    tmp = "%s.%d.%d.tmp" % (path, os.getpid(), int(time.time() * 1000) % 100000)
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
    try:
        try:
            os.link(tmp, path)
        except FileExistsError:
            raise
        except OSError:
            # No hard links on this file system: O_EXCL instead.
            fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write(text)
    finally:
        try:
            os.remove(tmp)
        except OSError:
            pass


def _take_over(path, seen):
    """Removes the stale lock the caller saw, under the guard file. Returns True where it was removed."""
    guard = path + ".guard"
    try:
        if time.time() - os.path.getmtime(guard) > GUARD_STALE_SECONDS:
            os.remove(guard)
    except OSError:
        pass
    try:
        fd = os.open(guard, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        os.close(fd)
    except OSError:
        return False
    try:
        now = read_lock(path)
        if now is None or now.get("id") != seen.get("id") or now.get("pid") != seen.get("pid") or now.get("since") != seen.get("since"):
            return False
        if not is_stale(now):
            return False
        os.remove(path)
        print("gpu_lock: took over the stale lock: %s" % describe(seen), flush=True)
        return True
    except OSError:
        return False
    finally:
        try:
            os.remove(guard)
        except OSError:
            pass


def try_acquire(path, who, max_minutes, exclusive, kind, pid, command=""):
    """Takes the lock if it is free or stale. Returns (info, None) on success, (None, holder info or None) otherwise."""
    now = time.time()
    info = {"id": who, "pid": pid, "since": now, "expires": now + max_minutes * 60 + MARGIN_SECONDS, "exclusive": bool(exclusive),
            "kind": kind, "command": command}
    for _ in range(4):
        try:
            _write_new(path, info)
            return info, None
        except FileExistsError:
            holder = read_lock(path)
            if holder is None:
                # Unreadable: an old damaged file is removed, a fresh one is given a moment.
                try:
                    if time.time() - os.path.getmtime(path) > GUARD_STALE_SECONDS:
                        os.remove(path)
                        continue
                except OSError:
                    continue
                return None, None
            if is_stale(holder):
                if _take_over(path, holder):
                    continue
            return None, holder
    return None, read_lock(path)


def release(path, who=None, pid=None, since=None, force=False):
    """Removes the lock if it is the caller's (same id, and same pid and time where given), or with force. Returns True where removed."""
    info = read_lock(path)
    if info is None:
        return False
    if not force:
        if who is not None and info.get("id") != who:
            return False
        if pid is not None and info.get("pid") != pid:
            return False
        if since is not None and info.get("since") != since:
            return False
    try:
        os.remove(path)
        return True
    except OSError:
        return False


def wait_acquire(path, who, max_minutes, exclusive, kind, pid, command="", poll=2.0, wait_minutes=180.0):
    """Waits for the lock and takes it; returns its info. Exits the program where the wait is over."""
    started = time.time()
    last_report = 0.0
    while True:
        info, holder = try_acquire(path, who, max_minutes, exclusive, kind, pid, command)
        if info is not None:
            return info
        now = time.time()
        if wait_minutes and now - started > wait_minutes * 60:
            print("gpu_lock: waited %.0f minutes for %s; giving up" % (wait_minutes, describe(holder) if holder else path), flush=True)
            sys.exit(75)
        if now - last_report >= WAIT_REPORT_SECONDS or last_report == 0.0:
            last_report = now
            print("gpu_lock: %s waits for %s" % (who, describe(holder) if holder else "a lock being written"), flush=True)
        time.sleep(poll)


def exclusive_holder(path):
    """The info of a live, unexpired EXCLUSIVE lock, or None. Builders and test runs wait while this is not None."""
    info = read_lock(path)
    if info is None or not info.get("exclusive") or is_stale(info):
        return None
    return info


def kill_tree(pid):
    """Ends a process and its children."""
    if os.name == "nt":
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], capture_output=True)
    else:
        try:
            os.kill(pid, 9)
        except OSError:
            pass


def cmd_run(args, command):
    if not command:
        sys.exit("gpu_lock run: no command after --")
    path = lock_path(args.kind, args.lock_path)
    info = wait_acquire(path, args.who, args.max_minutes, args.exclusive, args.kind, os.getpid(), " ".join(command), args.poll, args.wait_minutes)
    print("gpu_lock: %s holds the %s lock for at most %.0f min%s" % (args.who, args.kind, args.max_minutes, " (exclusive)" if args.exclusive else ""), flush=True)
    code = 1
    try:
        child = subprocess.Popen(command)
        try:
            code = child.wait(timeout=args.max_minutes * 60)
        except subprocess.TimeoutExpired:
            print("gpu_lock: %d minutes are up; ending the command" % args.max_minutes, flush=True)
            kill_tree(child.pid)
            child.wait()
            code = 124
        except KeyboardInterrupt:
            kill_tree(child.pid)
            child.wait()
            code = 130
    except OSError as e:
        print("gpu_lock: cannot start the command: %s" % e, flush=True)
        code = 127
    finally:
        release(path, args.who, os.getpid(), info["since"])
    sys.exit(code)


def cmd_acquire(args):
    path = lock_path(args.kind, args.lock_path)
    pid = args.pid if args.pid else os.getppid()
    info = wait_acquire(path, args.who, args.max_minutes, args.exclusive, args.kind, pid, "", args.poll, args.wait_minutes)
    print("gpu_lock: acquired %s for %s until %s" % (path, args.who, time.strftime("%H:%M:%S", time.localtime(info["expires"]))))


def cmd_release(args):
    path = lock_path(args.kind, args.lock_path)
    if release(path, args.who, None, None, args.force):
        print("gpu_lock: released")
    else:
        print("gpu_lock: nothing released (not held by %s)" % args.who)
        sys.exit(1)


def cmd_status(args):
    rows = []
    for kind in KINDS:
        path = lock_path(kind, args.lock_path if kind == "gpu" else None)
        info = read_lock(path)
        rows.append({"kind": kind, "path": path, "held": info is not None and not is_stale(info), "stale": info is not None and is_stale(info),
                     "holder": info})
        if args.lock_path and kind == "gpu":
            break
    if args.json:
        print(json.dumps(rows, indent=1))
        return
    for row in rows:
        if row["holder"] is None:
            print("%s: free (%s)" % (row["kind"], row["path"]))
        else:
            print("%s: %s" % (row["kind"], describe(row["holder"])))


# ---------------------------------------------------------------- self test

WORKER = '''import sys, time
name, seconds, log = sys.argv[1], float(sys.argv[2]), sys.argv[3]
with open(log, "a") as f: f.write("%s start %f\\n" % (name, time.time()))
time.sleep(seconds)
with open(log, "a") as f: f.write("%s end %f\\n" % (name, time.time()))
'''


def _selftest():
    import shutil
    import threading
    failures = []

    def check(name, ok, detail=""):
        print("selftest %-58s %s %s" % (name, "PASS" if ok else "FAIL", detail), flush=True)
        if not ok:
            failures.append(name)

    root = tempfile.mkdtemp(prefix="atmo-lock-selftest-")
    try:
        here = os.path.abspath(__file__)
        trees = []
        for n in ("wt1", "wt2"):
            d = os.path.join(root, n, "tools")
            os.makedirs(d)
            shutil.copy2(here, os.path.join(d, "gpu_lock.py"))
            trees.append(os.path.join(d, "gpu_lock.py"))
        worker = os.path.join(root, "worker.py")
        with open(worker, "w") as f:
            f.write(WORKER)
        lock = os.path.join(root, "main", ".local", "work", "gpu.lock")
        log = os.path.join(root, "log.txt")
        py = sys.executable

        def launch(tree, who, seconds, name, extra=()):
            cmd = [py, tree, "--selftest", "--lock-path", lock, "--poll", "0.2", "run", "--who", who, "--max-minutes", "1"] + list(extra) + \
                  ["--", py, worker, name, str(seconds), log]
            return subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

        def events():
            out = {}
            if os.path.exists(log):
                for line in open(log):
                    n, kind, t = line.split()
                    out[(n, kind)] = float(t)
            return out

        # 1. Two processes from two worktrees serialise, and status shows the holder with its exclusive flag meanwhile.
        a = launch(trees[0], "A", 2.0, "A", ["--exclusive"])
        deadline = time.time() + 15
        while not os.path.exists(lock) and time.time() < deadline:
            time.sleep(0.05)
        b = launch(trees[1], "B", 0.3, "B")
        time.sleep(0.8)
        status = subprocess.run([py, trees[1], "--selftest", "--lock-path", lock, "status", "--json"], capture_output=True, text=True)
        try:
            holder = json.loads(status.stdout)[0]["holder"]
        except (ValueError, IndexError, TypeError):
            holder = None
        check("status shows the holder and the exclusive flag", bool(holder) and holder.get("id") == "A" and holder.get("exclusive") is True, str(holder and holder.get("id")))
        check("exclusive_holder sees the exclusive lock", (exclusive_holder(lock) or {}).get("id") == "A")
        a.communicate(timeout=60)
        b.communicate(timeout=60)
        ev = events()
        check("two worktrees serialise (B starts after A ends)", ("B", "start") in ev and ("A", "end") in ev and ev[("B", "start")] >= ev[("A", "end")],
              "B.start - A.end = %.2f s" % (ev.get(("B", "start"), 0) - ev.get(("A", "end"), 0)))
        check("lock file is gone after the runs", not os.path.exists(lock))

        # 2. A killed holder is taken over.
        os.remove(log)
        k = launch(trees[0], "K", 120, "K")
        deadline = time.time() + 15
        while not os.path.exists(lock) and time.time() < deadline:
            time.sleep(0.05)
        time.sleep(0.5)
        kill_tree(k.pid)
        k.wait(timeout=20)
        t0 = time.time()
        t = launch(trees[1], "T", 0.2, "T")
        out = t.communicate(timeout=60)[0]
        check("a killed holder is taken over", t.returncode == 0 and "took over" in out and time.time() - t0 < 20, "%.1f s" % (time.time() - t0))

        # 3. An expired lock of a live process is taken over.
        now = time.time()
        os.makedirs(os.path.dirname(lock), exist_ok=True)
        with open(lock, "w") as f:
            json.dump({"id": "old", "pid": os.getpid(), "since": now - 7200, "expires": now - 60, "exclusive": False, "kind": "gpu"}, f)
        t = launch(trees[0], "E", 0.1, "E")
        out = t.communicate(timeout=60)[0]
        check("an expired lock is taken over", t.returncode == 0 and "took over" in out)

        # 4. A held lock blocks a second user for its whole time (lock of a live process, not expired).
        with open(lock, "w") as f:
            json.dump({"id": "live", "pid": os.getpid(), "since": time.time(), "expires": time.time() + 600, "exclusive": False, "kind": "gpu"}, f)
        got, holder = try_acquire(lock, "X", 1, False, "gpu", os.getpid())
        check("a held lock blocks a second user", got is None and holder and holder["id"] == "live")
        os.remove(lock)

        # 5. The timeout ends the command and frees the lock.
        cmd = [py, trees[0], "--selftest", "--lock-path", lock, "--poll", "0.2", "run", "--who", "S", "--max-minutes", "0.03", "--",
               py, worker, "S", "60", log]
        t0 = time.time()
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=90)
        check("the command is ended when its time is up", r.returncode == 124 and time.time() - t0 < 30 and not os.path.exists(lock), "exit %d" % r.returncode)

        # 6. --lock-path is refused without --selftest.
        r = subprocess.run([py, here, "--lock-path", lock, "status"], capture_output=True, text=True)
        check("--lock-path without --selftest is refused", r.returncode != 0)

        # 7. dotnet_test.py waits while an exclusive lock is held, and not for a shared one.
        dt = os.path.join(os.path.dirname(here), "dotnet_test.py")
        if os.path.exists(dt):
            shutil.copy2(dt, os.path.join(root, "wt2", "tools", "dotnet_test.py"))
            shutil.copy2(here, os.path.join(root, "wt2", "tools", "gpu_lock.py"))
            dtc = os.path.join(root, "wt2", "tools", "dotnet_test.py")

            def fake_holder(exclusive, seconds):
                with open(lock, "w") as f:
                    json.dump({"id": "timing", "pid": os.getpid(), "since": time.time(), "expires": time.time() + 600, "exclusive": exclusive, "kind": "gpu"}, f)
                threading.Timer(seconds, lambda: os.path.exists(lock) and os.remove(lock)).start()

            fake_holder(True, 2.5)
            t0 = time.time()
            r = subprocess.run([py, dtc, "--selftest", "--lock-path", lock, "--poll", "0.2", "--print-command"], capture_output=True, text=True, timeout=60)
            waited = time.time() - t0
            check("dotnet_test waits while an exclusive lock is held", waited >= 2.3 and r.returncode == 0, "%.1f s" % waited)
            check("dotnet_test passes the default filter and no-deploy flag", "TestCategory!=Docs" in r.stdout and "-p:DeployToGameData=false" in r.stdout)
            time.sleep(0.5)
            fake_holder(False, 5)
            t0 = time.time()
            r = subprocess.run([py, dtc, "--selftest", "--lock-path", lock, "--poll", "0.2", "--print-command"], capture_output=True, text=True, timeout=60)
            check("dotnet_test does not wait for a shared lock", time.time() - t0 < 2.0 and r.returncode == 0, "%.1f s" % (time.time() - t0))
        else:
            check("dotnet_test.py present", False)
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print("selftest: %s" % ("PASSED" if not failures else "FAILED (%s)" % ", ".join(failures)))
    return 0 if not failures else 1


def build_parser():
    p = argparse.ArgumentParser(description="The GPU/Unity lock of the Atmospheres work.")
    p.add_argument("--selftest", action="store_true", help="allows --lock-path (selftest only)")
    p.add_argument("--lock-path", help="a temporary lock file; only together with --selftest")
    p.add_argument("--poll", type=float, default=2.0, help="seconds between two tries while waiting")
    sub = p.add_subparsers(dest="cmd", required=True)
    for name in ("run", "acquire"):
        s = sub.add_parser(name)
        s.add_argument("--who", required=True)
        s.add_argument("--max-minutes", type=float, required=True)
        s.add_argument("--exclusive", action="store_true")
        s.add_argument("--kind", choices=KINDS, default="gpu")
        s.add_argument("--wait-minutes", type=float, default=180.0, help="give up waiting after this long (0 = never)")
        if name == "acquire":
            s.add_argument("--pid", type=int, default=0, help="the process whose life holds the lock; a lock is stale when it ends "
                           "(default: the parent process, which must then stay alive until release)")
    s = sub.add_parser("release")
    s.add_argument("--who", required=True)
    s.add_argument("--kind", choices=KINDS, default="gpu")
    s.add_argument("--force", action="store_true")
    s = sub.add_parser("status")
    s.add_argument("--json", action="store_true")
    sub.add_parser("selftest")
    return p


def main(argv):
    command = []
    if "--" in argv:
        i = argv.index("--")
        argv, command = argv[:i], argv[i + 1:]
    args = build_parser().parse_args(argv)
    if args.lock_path and not args.selftest and args.cmd != "selftest":
        sys.exit("gpu_lock: --lock-path is accepted only together with --selftest")
    if args.cmd == "selftest":
        return _selftest()
    if args.cmd == "run":
        return cmd_run(args, command)
    if args.cmd == "acquire":
        return cmd_acquire(args)
    if args.cmd == "release":
        return cmd_release(args)
    return cmd_status(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
