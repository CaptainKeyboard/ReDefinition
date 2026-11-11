"""The precipitation renderer's series of the weather player (package C22).

  python tools/build_weather_player.py --series precip

Stage "scene" runs the player's scene precip-check, which draws the renderer's own pass (PrecipPass and its shader, the classes the mod
uses) and judges itself: per type the luminance shift sign, the changed pixels, the fall direction and the drift with a wind; nothing
drawn below level 0.04; Host against Scene mean luminance within 5 % at levels 0.3, 0.6 and 1.0; and a 0.5 / 0.5 cross-fade covering the
veil as one veil. The upscaler rows (trail pixels, ground-edge sharpness, fly-by) need the proxy with an upscaler and are not part of
this series.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

register_series("precip", [Stage("scene", ["-mode", "scene", "-scene", "precip-check"])], player_judged("scene"), exclusive=False)
