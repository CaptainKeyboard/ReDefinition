"""The standing comparison of upscalers, frame generation, raytracing and latency models.

Runs the scene probe's flight (the launch pad, or a field of grass) once per KSP start of a
scenario matrix (tools/benchmark_matrix.json). It sets the proxy's live keys in turn inside
each start and reads the frame rate and the latency of each phase from the logs. It writes the
run to docs/development/benchmarks/<date-time>.json and the tables in docs/development/benchmarks.md.
The matrix is split into groups: the main matrix runs by default, the others (raytracing
modes, frame generation steps, DLSS-G presets, latency brake targets, build variants, a
grass scene) each by --group NAME.

The KSP install's settings files are copied before the run and put back after it, also
when a start fails (Install.snap and Install.restore). Python's standard library only.

Usage: python tools/benchmark_matrix.py <command>. The command run runs the default groups (the
main matrix). The command run --group NAME runs one named group, and run --group all runs all. The command plan
prints the groups, starts and phases, with minutes, and runs nothing. The command parse <raw
folder> reads a run's logs again. The command render writes benchmarks.md from the JSON files.

The code lies in tools/benchmark_matrix_lib/ (its __init__.py lists the modules). This file keeps the
script's name and re-exports every name of the package, so that scripts and tests import it as before.
"""
import os
import sys

if __package__:
    # Imported as tools.benchmark_matrix (python -m unittest from the repository root).
    from .benchmark_matrix_lib import *  # noqa: F401,F403
else:
    # Run as a script, or imported by a script of tools/ (hud_probe.py), with tools/ on sys.path.
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from benchmark_matrix_lib import *  # noqa: F401,F403

if __name__ == "__main__":
    main()
