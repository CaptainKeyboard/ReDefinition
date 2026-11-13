#!/usr/bin/env python3
"""dotnet test for the Atmospheres work, the way agents run it. Stdlib only.

  python tools/dotnet_test.py [--smoke] [--filter EXPR] [--project PATH] [-- extra dotnet test arguments]

It waits while an exclusive timing lock is held (tools/gpu_lock.py status shows who), refuses to build while KSP_x64 runs, and passes
  -p:DeployToGameData=false          (the main project deploys into the game by default, and the test project builds it)
  --filter "TestCategory!=Docs"      (the Docs category runs once per merge, by the orchestrator)
unless the caller gives its own. --smoke runs only the category Smoke. The dotnet SDK is .dotnet\\dotnet.exe in the user's profile where it
exists. The test project is relative to this script's worktree.
"""
import argparse
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gpu_lock  # noqa: E402

DEFAULT_PROJECT = os.path.join(os.path.dirname(HERE), "tests", "ReDefinition.Tests", "ReDefinition.Tests.csproj")
DEFAULT_DOTNET = os.path.join(os.path.expanduser("~"), ".dotnet", "dotnet.exe")
NO_DEPLOY = "-p:DeployToGameData=false"
DEFAULT_FILTER = "TestCategory!=Docs"


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


def build_command(dotnet, project, filt, smoke, extra):
    """The dotnet test command line."""
    if smoke:
        filt = "TestCategory=Smoke"
    command = [dotnet, "test", project]
    flags = " ".join(extra)
    if "--filter" not in extra and not any(a.startswith("--filter") for a in extra):
        command += ["--filter", filt]
    if "DeployToGameData" not in flags:
        command.append(NO_DEPLOY)
    return command + list(extra)


def main(argv):
    extra = []
    if "--" in argv:
        i = argv.index("--")
        argv, extra = argv[:i], argv[i + 1:]
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--project", default=DEFAULT_PROJECT)
    p.add_argument("--filter", default=DEFAULT_FILTER)
    p.add_argument("--smoke", action="store_true", help="only the category Smoke")
    p.add_argument("--dotnet", default=DEFAULT_DOTNET if os.path.exists(DEFAULT_DOTNET) else "dotnet")
    p.add_argument("--poll", type=float, default=5.0)
    p.add_argument("--selftest", action="store_true", help="allows --lock-path and --print-command")
    p.add_argument("--lock-path", help="a temporary lock; only with --selftest")
    p.add_argument("--print-command", action="store_true", help="print the command instead of running it (selftest)")
    args = p.parse_args(argv)
    if (args.lock_path or args.print_command) and not args.selftest:
        sys.exit("dotnet_test: --lock-path and --print-command are accepted only together with --selftest")

    wait_while_exclusive(gpu_lock.lock_path("gpu", args.lock_path), args.poll)
    command = build_command(args.dotnet, args.project, args.filter, args.smoke, extra)
    if args.print_command:
        print(" ".join(command))
        return 0
    if ksp_running():
        print("dotnet_test: KSP_x64 runs; a build must not run beside it", file=sys.stderr)
        return 3
    return subprocess.call(command)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
