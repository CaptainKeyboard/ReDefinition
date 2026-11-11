"""The weather generator's series of the weather player (package C25).

  python tools/build_weather_player.py --series generator-golden     the golden values of tests/data/generator under the player's Mono

Series "generator-golden" runs the player's scene generator-golden: the generator core (tools/atmosphere-sim/AtmoSim.Core, linked by
tests/WeatherPlayer/linked/C25.txt) is evaluated under Unity 2019.4 Mono, the runtime of KSP, on the main thread and on a worker thread
and compared with the values the offline proof recorded: the proof's hash of 20000 queries (positions from proof-lat.bin), the same hash
with DMath.Asin positions, the map hash, 3 seeds x 20 points x 5 UTs x 9 values bit for bit and the surface layer. It measures the bits
of the core's answers on the machine that runs it; it needs no GPU and no proxy. The rows are tests/series/rows/C25.json (series
'player-C25').
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

GOLDEN = os.path.normpath(os.path.join(HERE, "..", "..", "tests", "data", "generator"))

register_series("generator-golden", [Stage("scene", ["-mode", "scene", "-scene", "generator-golden", "-golden", GOLDEN], proxy=False)],
                player_judged("scene"), exclusive=False)
