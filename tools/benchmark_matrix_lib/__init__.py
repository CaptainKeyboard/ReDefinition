"""The code of tools/benchmark_matrix.py, in modules along its stages.
The script keeps the command line's name and re-exports everything here.
So `python tools/benchmark_matrix.py ...` works as it did when it was one file.
The import `from tools import benchmark_matrix` works as well.

    Constants    paths, timings, the snapshot's rules and the page's markers.
    Scenarios    a scenario's row label and key.
    Matrix       the scenario matrix and its groups, expanded into starts and phases.
    Probe_plan   the probe's arguments for a start and its expected duration.
    Files        edits of the files a start writes (settings.cfg, the proxy's ini, the package's ini).
    Logs         ReDefinitionProxy.log and KSP.log read into timed entries, one window's numbers.
    Phases       the phases of a start measured in those logs.
    Install      the foreground window, the install's snapshot and restore, the launch and the DLSS-G package.
    Machine      the GPUs, drivers, CPU and the repository's commit.
    Runner       the run command.
    Record       a run's record built from its raw folder.
    Tables       a record's rows and the comparison table of a group.
    Report       benchmarks.md: the run sections, the tables and the page.
    Cli          the command line.
"""

from .constants import (
    DEFAULT_KSP, DEFAULT_MATRIX, DEFAULT_SCENE, DOC, FOREGROUND_RETRY_SECONDS, FOREGROUND_SECONDS, MAIN_GROUP,
    MAX_COMMAND_CHARS, PACKAGE_DIR, PACKAGE_FILES, PACKAGE_KEYS_SHOWN, PACKAGE_SUBDIRS, PROBE_FLIGHT_SECONDS,
    PROBE_SET_SECONDS, PROFILE_START_SECONDS, PROXY_INI, REPO, RESULTS, RUNS_END, RUNS_START, SETTINGS_REL, SHOT,
    SNAP_EXT, SNAP_SKIP, START_OVERHEAD_SECONDS, STEP_TOLERANCE, TABLE_END, TABLE_START,
)
from .scenarios import (
    scenario_key, scenario_label,
)
from .matrix import (
    expand, GROUP_EXTRA, group_list, GROUP_LISTS, GROUP_MERGED, GROUP_META, load_matrix, resolve_group, select_groups,
    _allowed, _as_list, _entry, _expand_cell, _fill, _mod_values, _split_value, _start, _vendor_ok,
)
from .probe_plan import (
    command_length, estimate_seconds, ini_sets_argument, probe_arguments, start_seconds,
)
from .files import (
    parse_cfg_values, parse_ini_sections, set_cfg_values, set_ini_values, set_package_ini_value,
)
from .logs import (
    aggregate, parse_config, parse_ksp_log, parse_proxy_log, scene_unavailable, _align, _BRAKE, _fg_kind_ran,
    _in_window, _KSP_TIME, _last_before, _LATENCY, _PCL, _PROXY_TIME, _REPORT, _RT_BUILD, _SCENE_REASON, _seconds,
    _STAGES, _timed_lines, _unfocused_at, _UPSCALER, _with_parts,
)
from .phases import (
    phases_of_start, rt_builds_of, _merge_repeats, _ratio_findings, _same_number, _step_check,
)
from .install import (
    bring_to_front, Foreground, Install, _Win32,
)
from .machine import (
    git_record, machine_record, nvidia_driver_from_windows, vendor_of,
)
from .runner import (
    apply_profile, matrix_label, parallax_folder, resources_of, run, run_start, _save_plan,
)
from .record import (
    build_record,
)
from .tables import (
    group_definition, record_groups, rows_of, summary_table, _baseline_key, _fmt, _pct, _reference_key,
)
from .report import (
    group_ids_of, load_records, render, render_doc, run_section, tables_of, write_results, _machine_line,
    _matrix_shown, _replace_between, _settling, _wrapped,
)
from .cli import (
    main, parse_command, plan_command,
)

__all__ = [
    "aggregate", "apply_profile", "bring_to_front", "build_record", "command_length", "DEFAULT_KSP", "DEFAULT_MATRIX",
    "DEFAULT_SCENE", "DOC", "estimate_seconds", "expand", "Foreground", "FOREGROUND_RETRY_SECONDS",
    "FOREGROUND_SECONDS", "git_record", "group_definition", "GROUP_EXTRA", "group_ids_of", "group_list",
    "GROUP_LISTS", "GROUP_MERGED", "GROUP_META", "ini_sets_argument", "Install", "load_matrix", "load_records",
    "machine_record", "main", "MAIN_GROUP", "matrix_label", "MAX_COMMAND_CHARS", "nvidia_driver_from_windows",
    "PACKAGE_DIR", "PACKAGE_FILES", "PACKAGE_KEYS_SHOWN", "PACKAGE_SUBDIRS", "parallax_folder", "parse_cfg_values",
    "parse_command", "parse_config", "parse_ini_sections", "parse_ksp_log", "parse_proxy_log", "phases_of_start",
    "plan_command", "probe_arguments", "PROBE_FLIGHT_SECONDS", "PROBE_SET_SECONDS", "PROFILE_START_SECONDS",
    "PROXY_INI", "record_groups", "render", "render_doc", "REPO", "resolve_group", "resources_of", "RESULTS",
    "rows_of", "rt_builds_of", "run", "run_section", "run_start", "RUNS_END", "RUNS_START", "scenario_key",
    "scenario_label", "scene_unavailable", "select_groups", "set_cfg_values", "set_ini_values",
    "set_package_ini_value", "SETTINGS_REL", "SHOT", "SNAP_EXT", "SNAP_SKIP", "START_OVERHEAD_SECONDS",
    "start_seconds", "STEP_TOLERANCE", "summary_table", "TABLE_END", "TABLE_START", "tables_of", "vendor_of",
    "write_results", "_align", "_allowed", "_as_list", "_baseline_key", "_BRAKE", "_entry", "_expand_cell",
    "_fg_kind_ran", "_fill", "_fmt", "_in_window", "_KSP_TIME", "_last_before", "_LATENCY", "_machine_line",
    "_matrix_shown", "_merge_repeats", "_mod_values", "_PCL", "_pct", "_PROXY_TIME", "_ratio_findings",
    "_reference_key", "_replace_between", "_REPORT", "_RT_BUILD", "_same_number", "_save_plan", "_SCENE_REASON",
    "_seconds", "_settling", "_split_value", "_STAGES", "_start", "_step_check", "_timed_lines", "_unfocused_at",
    "_UPSCALER", "_vendor_ok", "_Win32", "_with_parts", "_wrapped",
]
