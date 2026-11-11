"""The aurora shell's series of the weather player (package D38).

  python tools/build_weather_player.py --series aurora        scene aurora-check
  python tools/build_weather_player.py --series aurora-time   scene aurora-time (a timing run, exclusive lock)

aurora-check draws the shell's own pass (AuroraPass and its shader, the classes the mod uses) over the fake world and judges itself
from the contribution mask (the shell on minus off, identical camera, state and clock): the lid metric (the largest step between
adjacent row means over the peak row mean), the drip metric (columns whose lowest lit row hangs below the median of their
neighbours), the horizon band (the mean mask below 3 degrees of elevation over the peak), the occlusion by a body in front of the
view, the identical repeat and the empty day. The grid is sun -6/-12/-25 x activity 0.2/0.6/1.0 x latitude 60/70/78 at 25 degrees
and the three latitudes at 10 and 40 degrees at sun -12 and activity 0.6.

aurora-time measures the GPU milliseconds of a frame with and without the shell through the proxy's timestamp queries and judges
four times the difference (1280 x 720 against 2560 x 1440) against 0.15 ms.

Open, not part of these series (owner E40, proxy runs): the upscaler and RT variants (DLSS K/M/L, FSR 3, XeSS: trail pixels of the
shell against the baseline + 20 %) and the three discriminators of the volumetric variant (the variant is not built).
tests/series/rows/D38.json lists them as rows that are not required.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

register_series("aurora", [Stage("scene", ["-mode", "scene", "-scene", "aurora-check"])], player_judged("scene"), exclusive=False)
register_series("aurora-time", [Stage("scene", ["-mode", "scene", "-scene", "aurora-time"])], player_judged("scene"), exclusive=True)
