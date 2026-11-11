"""The series of package C23 (the cloud-map painter, src/Atmospheres/Eve): the scene eve-painter of the weather player.

  python tools/build_weather_player.py --series eve-painter

The scene (tests/WeatherPlayer/Assets/Runner/EvePainterScene.cs) paints five test fields with the pass shader on the GPU, reads the
targets back and prints, as {metric,value,limit,pass} lines:

  gpu_cpu_corr_coverage_field0..4, gpu_cpu_corr_type_field0..4   the GPU map against the CPU twin (the mirror's rule), >= 0.99
  gpu_cpu_corr_coverage_min, gpu_cpu_corr_type_min               the worst of the five
  slew_step_gpu_vs_twin_levels                                   one limited step, the GPU against the twin, <= 2 levels
  painter_ms_per_update                                          both targets, copies and passes, per update, <= 0.3 ms
  painter_main_thread_ms_per_update                              the main thread's share (uploads and blits issued), <= 1 ms
  shell_vs_map_correlation                                       the own shell on a sphere against the map it shows, >= 0.95
  shell_ms                                                       the shell's cost per frame, <= 0.3 ms

It is a timing series (exclusive GPU lock). The player judges itself: the collector takes its verdicts as they are.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

register_series("eve-painter", [Stage("scene", ["-mode", "scene", "-scene", "eve-painter"])], player_judged("scene"), exclusive=True, who="C23")
