"""The precipitation renderer's series of the weather player (package C22).

  python tools/build_weather_player.py --series precip

Stage "scene" runs the player's scene precip-check, which draws the renderer's own pass (PrecipPass and its shader, the classes the mod
uses) and judges itself: per type the luminance shift sign, the changed pixels, the fall direction and the drift with a wind; nothing
drawn below level 0.04; Host against Scene mean luminance within 5 % at levels 0.3, 0.6 and 1.0; and a 0.5 / 0.5 cross-fade covering the
veil as one veil.

Open, not part of this series (owner E40, K-W1 proxy run): the upscaler rows of the brief (DLSS K/M/L, FSR 3, XeSS at levels 0.3/0.6/1.0,
trail pixels at 0.3 <= 1.5 x the level-1.0 baseline, ground-edge sharpness within 5 %, flake smear, 10 s fly-by), the host composite cost
(< 0.5 ms at 2560x1440 with 20k billboards, a timing run under the exclusive lock) and the FG replay with the near-layer decision. They
need the proxy with an upscaler and a scene that draws the pass under it; tests/series/rows/C22.json lists them as non-required rows.
Until they are measured, weatherCompose Auto stays Scene (Tuning precip.hostProven.<vendor> is set by E40 from those numbers).
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

register_series("precip", [Stage("scene", ["-mode", "scene", "-scene", "precip-check"])], player_judged("scene"), exclusive=False)
