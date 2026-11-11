"""The lamp scatter's series of the weather player (package D32).

  python tools/build_weather_player.py --series lamp        the checks of the pass against the maths
  python tools/build_weather_player.py --series lamp-time   the cost of four lamps at 2560x1440 (exclusive GPU lock)

Series "lamp" runs the player's scene lamp-check, which draws the mod's own pass (LampPass and its shader) and judges itself: nothing at
sigma 0 and an empty buffer; the beam's luminance rising with sigma over 0.002 to 0.05; the mean over the beam's region within 10 % of the
CPU march of LampMath.BeamRadiance; the lamp's colour staying warm; a sigma at the camera dimming the beam; the glints raising the fine
spread without moving the mean; the contribution mask being blue and only in the beam; a wall hiding the beam behind it; the Host
composite agreeing with the Scene composite within 5 %.

Series "lamp-time" runs the scene lamp-time under the exclusive lock: the GPU milliseconds of four lamps in snow at 2560x1440 against the
frames without them, at most 0.5 ms. It needs the proxy beside the player (the GPU timer); without it the metric fails, it is not skipped.

The scene lamp-time also reports the depth copy alone (gpu_ms.depth_copy_only, limit depth_copy_ms_2560x1440).

Not measured by these series: the smear of the beam under DLSS at 20 degrees a second, the ghost metrics and the frame-generation
duplicates (the scene mode of the player has no upscaler; the turn mode of the runner is not the lamp's). The probe of the game
(src/Atmospheres/Light/Lamp/Probe/LampProbe.cs, series K-W3b, start lights) measures smear and ghost under the upscaler the game runs;
the duplicates are decided by the replay of the proxy's dump (.local/plans/atmospheres/D32-kw3b-spec.md). tests/series/rows/D32.json
holds the rows.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

register_series("lamp", [Stage("scene", ["-mode", "scene", "-scene", "lamp-check"])], player_judged("scene"), exclusive=False)
register_series("lamp-time", [Stage("scene", ["-mode", "scene", "-scene", "lamp-time"])], player_judged("scene"), exclusive=True)
