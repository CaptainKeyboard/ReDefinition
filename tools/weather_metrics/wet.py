"""The wet surfaces' and the window droplets' series of the weather player (package T08).

  python tools/build_weather_player.py --series wet        the scene wet-check: the overlay on Unity's deferred path
  python tools/build_weather_player.py --series droplet    the scene droplet-check: the droplet pass on a window picture
  python tools/build_weather_player.py --series wet-cost   the scene wet-cost: GPU milliseconds of both passes (exclusive GPU lock)

Series "wet" runs the player's scene wet-check, which draws the mod's own classes (WetSurfacesPass and its shader) on the player's terrain
and judges itself: the diffuse factor of wet ground against the configured wetDiffuse, the sky and a strength-0 draw untouched, the
share of puddles against the asked share, rain rings on liquid and not on frozen puddles, frozen water and snow cover darkening less,
and a puddle's mirror of the sun. It writes its pictures (natural and aid map, unique names t08-wet-*.png) beside the metric log.

Series "droplet" runs the scene droplet-check: nothing at wetness 0, the covered share against the configured coverage, the lens that
shows the picture upside down, trails along the sliding direction, the interior mask and the A/B outside the drops. It writes
t08-droplet-*.png.

Deferred in the player is Unity's own deferred path, not the installed Deferred mod's lighting shaders; that case is a K-T1 row of the
game (tests/series/rows/T08.json).
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

register_series("wet", [Stage("scene", ["-mode", "scene", "-scene", "wet-check"])], player_judged("scene"), exclusive=False, who="T08")
register_series("droplet", [Stage("scene", ["-mode", "scene", "-scene", "droplet-check"])], player_judged("scene"), exclusive=False, who="T08")
# The cost of the overlay and of the droplet pass: a timing series, the GPU lock exclusive; it needs the proxy beside the player.
register_series("wet-cost", [Stage("scene", ["-mode", "scene", "-scene", "wet-cost"])], player_judged("scene"), exclusive=True, who="T08")
