"""The camera frost's series of the weather player (package D37).

  python tools/build_weather_player.py --series frost

Stage "scene" runs the player's scene frost-check, which draws the frost's own pass (FrostPass, FrostAtlas and the shader, the classes the
mod uses) onto an image of the scene's size and judges itself: the atlas is 4 MB and builds in under 60 ms; the contribution-mask coverage
at exposure 0 / 0.4 / 1 against the design table 0 / 12.4 / 33.3 % (16:9, +-10 %) and against the CPU arithmetic (2 %); not one mask pixel
in the clear centre; the picture outside the mask identical with the frost on and off; a flake's size and refraction; a drop's trail
against the gravity direction (down, and rolled to the right) and its wiping of the frost; an empty command buffer when nothing is on the
glass; the cost of an active draw at 2560 x 1440 (at most 0.25 ms, reported as the frame loop minus the empty-clear baseline).

Open, not part of this series (owner E40, K-W1/K-W3b proxy run): the upscaler rows of the brief (ghost metrics with DLSS, FSR 3 and XeSS
against the no-effect baseline + 20 %, and the FG replay). They need the proxy with an upscaler and a scene that draws the pass after it;
the host composite is drawn onto the upscaled image, so the upscaler never sees the frost (nothing for it to smear). The rows are in
tests/series/rows/D37.json as non-required rows.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

register_series("frost", [Stage("scene", ["-mode", "scene", "-scene", "frost-check"])], player_judged("scene"), exclusive=False)
