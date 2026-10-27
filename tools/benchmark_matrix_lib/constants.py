"""The paths, timings, snapshot rules and page markers the benchmark tool shares."""
import os

# The repository: this file lies in tools/benchmark_matrix_lib/.
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_KSP = r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program"
DEFAULT_MATRIX = os.path.join(REPO, "tools", "benchmark_matrix.json")
DOC = os.path.join(REPO, "docs", "development", "benchmarks.md")
RESULTS = os.path.join(REPO, "docs", "development", "benchmarks")
SETTINGS_REL = os.path.join("GameData", "ReDefinition", "PluginData", "settings.cfg")
PROXY_INI = "ReDefinitionProxy.ini"
SHOT = "bench"

# The probe holds each set of live keys this long before it takes the next
# (SceneProbe.IniShots), and waits this long in the flight before the first.
PROBE_SET_SECONDS = 12.0
PROBE_FLIGHT_SECONDS = 30.0
# How far from the multiplier asked for the frames presented per rendered frame may lie, under
# or over, before the step counts as not delivered.
STEP_TOLERANCE = 0.25
# Longest command line the tool launches KSP with; CreateProcess takes 32767 characters.
MAX_COMMAND_CHARS = 30000
# Measured on the RTX A4500: from the process start to the flight, and the quit after. The six
# runs of 2026-10-21 (1 to 9 starts, 18 to 60 phases) fit 86 s a start and 62 s for the profile start.
START_OVERHEAD_SECONDS = 86.0
PROFILE_START_SECONDS = 60.0
# How often the game's window is looked at while it runs, and how long it is left alone after
# Windows refused to bring it forward.
FOREGROUND_SECONDS = 2.0
FOREGROUND_RETRY_SECONDS = 15.0

# What the snapshot keeps: every settings file of the install, by these extensions.
SNAP_EXT = (".cfg", ".xml", ".json", ".txt", ".ini", ".settings")
SNAP_SKIP = ("Logs", "saves", "ReDefinitionCaptures", "Screenshots")

# What the DLSS-G package for Ampere puts next to KSP_x64.exe (dlssg_sm86.ini's own
# instructions: version.dll, the ini and the dlssg_sm86 folder).
PACKAGE_FILES = ("version.dll", "dlssg_sm86.ini")
PACKAGE_DIR = "dlssg_sm86"
PACKAGE_SUBDIRS = ("backend", "kernels", "runtime")

# The package ini's keys the runs' sections name.
PACKAGE_KEYS_SHOWN = ("Engine", "MaxGeneratedFrames", "Preset", "Optimized", "FlowResolution", "UseHudless", "Mode", "FrameTiming", "Level")

TABLE_START = "<!-- benchmark-table:start -->"
TABLE_END = "<!-- benchmark-table:end -->"
RUNS_START = "<!-- benchmark-runs:start -->"
RUNS_END = "<!-- benchmark-runs:end -->"

# The group of the matrix's own lists, and the scene the groups run in unless they name others.
MAIN_GROUP = "main"
DEFAULT_SCENE = "pad"
