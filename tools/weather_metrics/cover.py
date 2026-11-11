"""The vehicle cover's series of the weather player (package D34, the L2 shell spike).

  python tools/build_weather_player.py --series shell        the checks of the shell and of the screen-space variant
  python tools/build_weather_player.py --series shell-time   the cost of 100 and 200 shells at 2560x1440 (exclusive GPU lock)

Series "shell" runs the player's scene shell-check, which draws the mod's own classes (ShellMesh, the shell shader, CoverScreenPass and its
shader) on stand-in parts and judges itself: the cover share of the roof at 10, 30 and 100 m and none on a vertical tank; the dark pixels
of the part showing through the cover (z-fight) per distance; the motion vectors of the shell against the part's own on a moving stand-in
(at least 98 % within 2 % or a quarter pixel); Unity's deferred path against forward; variant (f), the screen-space pass, against the shell
(roof share, none on the tank, no change of the motion vectors).

Series "shell-time" runs the scene shell-time under the exclusive lock: the GPU milliseconds of 100 and of 200 shells at 2560x1440 against
the frames without them (limits 0.3 and 0.6 ms). It needs the proxy beside the player (the GPU timer); without it the metric fails, it is
not skipped.

Not measured by these series: the smear of an upscaler (FSR 3, DLSS) over the shell and the duplicates of frame generation. The scene mode
of the player has no upscaler and the turn mode draws the cloud slab's scene; the motion-vector match above is the cause the ghost would
come from, and the two figures themselves are left to the K-W3b start of the game (limit: baseline + 20 %, see tests/series/rows/D34.json).
Deferred in the player is Unity's own deferred path, not the installed Deferred mod's lighting shaders; that case is a K-W3b row too.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

register_series("shell", [Stage("scene", ["-mode", "scene", "-scene", "shell-check"])], player_judged("scene"), exclusive=False)
register_series("shell-time", [Stage("scene", ["-mode", "scene", "-scene", "shell-time"])], player_judged("scene"), exclusive=True)
