#!/usr/bin/env python3
"""dotnet test for the Atmospheres work, the way agents run it. Stdlib only.

  python tools/dotnet_test.py [--smoke] [--filter EXPR] [--include CATEGORY ...] [--project PATH] [-- extra dotnet test arguments]

It waits while an exclusive timing lock is held (tools/gpu_lock.py status shows who), refuses to build while KSP_x64 runs, and passes
  -p:DeployToGameData=false          (the main project deploys into the game by default, and the test project builds it)
  --filter "TestCategory!=Docs&TestCategory!=Bench&TestCategory!=Explore&TestCategory!=Profile"
                                     (Docs runs once per merge, by the orchestrator; Bench holds wall-clock gates that are judged only
                                     under the exclusive timing lock; Explore and Profile are measurement runs that need their
                                     environment variables)
  -c Release                         (the Atmospheres half took 13 min 35 s in Debug and 3 min 27 s in Release on 2026-11-24, all passed in
                                     both; the players get Release; --debug runs Debug)
  --logger trx                       (every run leaves its durations in TestResults)
unless the caller gives its own. --include CATEGORY takes one of those four back into the default filter (repeatable), e.g. under the
exclusive lock: python tools/gpu_lock.py run --exclusive ... -- python tools/dotnet_test.py --include Bench. --smoke runs only the
category Smoke.

Without --filter (and without --smoke or a --filter after "--") the suite runs as two halves, one dotnet test invocation each, because one
test host crashed after about 2955 tests when it ran everything: the tests of Atmospheres first, then all the others. Each half prints its
summary line, then a combined total follows. The exit code is non-zero if either half fails, crashes ("Test host process crashed", "test
run was aborted") or prints no summary line; a crash is reported as such. --filter or --smoke keeps a single run, whose exit code gets the
same crash check. --halves A B (for checking the script) replaces the two default filter expressions.

 The dotnet SDK is .dotnet\\dotnet.exe in the user's profile where it
exists. The test project is relative to this script's worktree.
"""
import argparse
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gpu_lock  # noqa: E402

DEFAULT_PROJECT = os.path.join(os.path.dirname(HERE), "tests", "ReDefinition.Tests", "ReDefinition.Tests.csproj")
DEFAULT_DOTNET = os.path.join(os.path.expanduser("~"), ".dotnet", "dotnet.exe")
NO_DEPLOY = "-p:DeployToGameData=false"
EXCLUDED = ("Docs", "Bench", "Explore", "Profile")


def default_filter(include=()):
    """The default filter: every category of EXCLUDED left out but those in include."""
    return "&".join("TestCategory!=" + c for c in EXCLUDED if c not in include)


def halves_of(filt):
    """The two halves of the suite under a filter: the tests of Atmospheres, then all the others."""
    return ((filt + "&" if filt else "") + "FullyQualifiedName~Atmospheres", (filt + "&" if filt else "") + "FullyQualifiedName!~Atmospheres")


DEFAULT_FILTER = default_filter()
HALVES = halves_of(DEFAULT_FILTER)
SUMMARY = re.compile(r"(Passed|Failed)!\s+-\s+Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)")
CRASH = ("test host process crashed", "test run was aborted", "the active test run was aborted")


def ksp_running():
    """Whether KSP_x64.exe runs, as tasklist sees it; False where tasklist cannot be run."""
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KSP_x64.exe"], capture_output=True, text=True).stdout
    except OSError:
        return False
    return "KSP_x64.exe" in out


def wait_while_exclusive(path, poll):
    """Blocks while a live exclusive lock is held; says so every 30 seconds."""
    last = 0.0
    while True:
        holder = gpu_lock.exclusive_holder(path)
        if holder is None:
            return
        if time.time() - last >= 30 or last == 0.0:
            last = time.time()
            print("dotnet_test: waits for the exclusive run of %s (%s)" % (holder.get("id"), gpu_lock.describe(holder)), flush=True)
        time.sleep(poll)


def build_command(dotnet, project, filt, smoke, extra, configuration="Release"):
    """The dotnet test command line."""
    if smoke:
        filt = "TestCategory=Smoke"
    command = [dotnet, "test", project]
    flags = " ".join(extra)
    if "--filter" not in extra and not any(a.startswith("--filter") for a in extra):
        command += ["--filter", filt]
    if "DeployToGameData" not in flags:
        command.append(NO_DEPLOY)
    if not any(a in ("-c", "--configuration") or a.startswith("--configuration=") for a in extra):
        command += ["-c", configuration]
    if not any(a == "--logger" or a.startswith("--logger") or a == "-l" for a in extra):
        command += ["--logger", "trx"]
    return command + list(extra)


def run_checked(command):
    """Runs one dotnet test, echoing its output. Returns (exit code, crashed, (failed, passed, skipped, total) or None).
    A crash or a missing summary line makes the code non-zero even where dotnet itself returned 0."""
    proc = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace")
    crashed, counts = False, None
    for line in proc.stdout:
        sys.stdout.write(line)
        sys.stdout.flush()
        if any(c in line.lower() for c in CRASH):
            crashed = True
        m = SUMMARY.search(line)
        if m:
            counts = tuple(int(m.group(i)) for i in (2, 3, 4, 5))
    code = proc.wait()
    if crashed:
        print("dotnet_test: CRASH: the test host crashed or the run was aborted", flush=True)
    elif counts is None and code == 0:
        print("dotnet_test: no summary line found; treated as a failure", flush=True)
    if (crashed or counts is None) and code == 0:
        code = 1
    if counts and counts[0] > 0 and code == 0:
        code = 1
    return code, crashed, counts


def run_halves(dotnet, project, halves, extra, configuration="Release"):
    """Runs the halves one after the other; prints a total; returns the worst exit code."""
    total, worst, lines = [0, 0, 0, 0], 0, []
    for i, filt in enumerate(halves, 1):
        print("dotnet_test: half %d/%d: --filter \"%s\"" % (i, len(halves), filt), flush=True)
        code, crashed, counts = run_checked(build_command(dotnet, project, filt, False, extra, configuration))
        if counts:
            total = [a + b for a, b in zip(total, counts)]
        status = "CRASHED" if crashed else ("ok" if code == 0 else "FAILED (exit %d)" % code)
        lines.append("half %d: %s%s" % (i, status, "" if counts is None else " - Failed: %d, Passed: %d, Skipped: %d, Total: %d" % counts))
        worst = worst or code
    for line in lines:
        print("dotnet_test: " + line, flush=True)
    print("dotnet_test: total - Failed: %d, Passed: %d, Skipped: %d, Total: %d" % tuple(total), flush=True)
    return worst


def main(argv):
    extra = []
    if "--" in argv:
        i = argv.index("--")
        argv, extra = argv[:i], argv[i + 1:]
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--project", default=DEFAULT_PROJECT)
    p.add_argument("--filter", default=None, help="one run with this filter; default: two halves")
    p.add_argument("--halves", nargs=2, metavar=("A", "B"), help="two filter expressions to run as the halves (for checking the script)")
    p.add_argument("--smoke", action="store_true", help="only the category Smoke")
    p.add_argument("--debug", action="store_true", help="build and run the Debug configuration (default: Release)")
    p.add_argument("--include", action="append", default=[], choices=EXCLUDED, metavar="CATEGORY",
                   help="take a category the default filter leaves out (%s) back in; repeatable" % ", ".join(EXCLUDED))
    p.add_argument("--dotnet", default=DEFAULT_DOTNET if os.path.exists(DEFAULT_DOTNET) else "dotnet")
    p.add_argument("--poll", type=float, default=5.0)
    p.add_argument("--selftest", action="store_true", help="allows --lock-path and --print-command")
    p.add_argument("--lock-path", help="a temporary lock; only with --selftest")
    p.add_argument("--print-command", action="store_true", help="print the command instead of running it (selftest)")
    args = p.parse_args(argv)
    if (args.lock_path or args.print_command) and not args.selftest:
        sys.exit("dotnet_test: --lock-path and --print-command are accepted only together with --selftest")

    wait_while_exclusive(gpu_lock.lock_path("gpu", args.lock_path), args.poll)
    configuration = "Debug" if args.debug else "Release"
    own_filter = args.filter is not None or any(a.startswith("--filter") for a in extra)
    base = default_filter(args.include)
    halves = list(args.halves) if args.halves else (list(halves_of(base)) if not own_filter and not args.smoke else None)
    if args.print_command:
        for filt in halves or [args.filter or base]:
            print(" ".join(build_command(args.dotnet, args.project, filt, args.smoke, extra, configuration)))
        return 0
    if ksp_running():
        print("dotnet_test: KSP_x64 runs; a build must not run beside it", file=sys.stderr)
        return 3
    if halves:
        return run_halves(args.dotnet, args.project, halves, extra, configuration)
    code, _, _ = run_checked(build_command(args.dotnet, args.project, args.filter or base, args.smoke, extra, configuration))
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
