#!/usr/bin/env python3
"""Gives a new git worktree the untracked Unity caches of the main repository, so that no agent reimports every asset.

  python tools/prepare_worktree.py <worktree folder> [--force] [--main <main repo>] [--dry]
  python tools/prepare_worktree.py --selftest

Copies unity/Library and tests/WeatherPlayer/Library (where they exist in the main repository and not yet in the worktree). The
orchestrator runs it after creating a worktree. The main repository is ATMO_MAINREPO, else the repository this script belongs to.
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gpu_lock  # noqa: E402

CACHES = ("unity/Library", "tests/WeatherPlayer/Library")


def copy_tree(src, dst):
    """Copies a folder tree; robocopy (multi-threaded) on Windows, shutil elsewhere."""
    if os.name == "nt" and shutil.which("robocopy"):
        r = subprocess.run(["robocopy", src, dst, "/E", "/MT:16", "/NFL", "/NDL", "/NJH", "/NJS", "/NP", "/R:1", "/W:1"], capture_output=True, text=True)
        if r.returncode >= 8:
            raise RuntimeError("robocopy failed (%d): %s" % (r.returncode, r.stdout[-400:]))
    else:
        shutil.copytree(src, dst)


def prepare(main, worktree, force=False, dry=False):
    """Copies the caches; returns the list of (relative path, what happened)."""
    done = []
    for rel in CACHES:
        src = os.path.join(main, *rel.split("/"))
        dst = os.path.join(worktree, *rel.split("/"))
        if not os.path.isdir(src):
            done.append((rel, "skipped: not in the main repository"))
            continue
        if os.path.exists(dst):
            if not force:
                done.append((rel, "skipped: already there"))
                continue
            if not dry:
                shutil.rmtree(dst)
        if dry:
            done.append((rel, "would copy"))
            continue
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        copy_tree(src, dst)
        done.append((rel, "copied"))
    return done


def selftest():
    root = tempfile.mkdtemp(prefix="atmo-prepare-selftest-")
    try:
        main, wt = os.path.join(root, "main"), os.path.join(root, "wt")
        for rel in CACHES:
            os.makedirs(os.path.join(main, *rel.split("/"), "sub"))
            with open(os.path.join(main, *rel.split("/"), "sub", "a.bin"), "wb") as f:
                f.write(b"cache " + rel.encode())
        os.makedirs(wt)
        first = prepare(main, wt)
        ok = all(what == "copied" for _, what in first)
        for rel in CACHES:
            ok = ok and open(os.path.join(wt, *rel.split("/"), "sub", "a.bin"), "rb").read() == b"cache " + rel.encode()
        second = prepare(main, wt)
        ok = ok and all(what.startswith("skipped") for _, what in second)
        print("selftest copies the caches and leaves existing ones: %s" % ("PASS" if ok else "FAIL"))
        print("selftest: %s" % ("PASSED" if ok else "FAILED"))
        return 0 if ok else 1
    finally:
        shutil.rmtree(root, ignore_errors=True)


def main(argv):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("worktree", nargs="?")
    p.add_argument("--main", default=None)
    p.add_argument("--force", action="store_true")
    p.add_argument("--dry", action="store_true")
    p.add_argument("--selftest", action="store_true")
    args = p.parse_args(argv)
    if args.selftest:
        return selftest()
    if not args.worktree:
        p.error("a worktree folder is required")
    main_repo = os.path.abspath(args.main) if args.main else gpu_lock.main_repo()
    worktree = os.path.abspath(args.worktree)
    if os.path.normcase(main_repo) == os.path.normcase(worktree):
        sys.exit("prepare_worktree: that is the main repository itself")
    if not os.path.isdir(worktree):
        sys.exit("prepare_worktree: no folder " + worktree)
    for rel, what in prepare(main_repo, worktree, args.force, args.dry):
        print("%-30s %s" % (rel, what))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
