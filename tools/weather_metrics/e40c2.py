"""E40 C2: what the clouds cost in the player on the maps a game start marched (series e40-c2-game-cost).

    python tools/weather_metrics/e40c2.py --prepare-all <series-pictures/maps>        every baseline dump of a soft60 start (fw_off / k_off windows) as a scene of its own view
    python tools/weather_metrics/e40c2.py --prepare-game <dump-folder> [<name>]      copy one dump of the game (OwnCloudModule.DumpMaps) into the player's inputs
    python tools/build_weather_player.py --series e40-c2-game-cost [--no-build]
    python tools/weather_metrics/e40c2.py --selftest

The game's K-T2 probe writes, at the head of every "T09 view begin", the coverage and type bytes of each layer as the pass marched them and the humidity of the time
(<game>/GameData/ReDefinition/Atmospheres/PluginData/series-pictures/maps/<view>-u<ut>/<layer>-cover.u8, -type.u8, humidity.u8; the series collects the folder). The player's own
reproduction of the K-T2 scenes (e40c1.py, the generator's maps of the explorer test) is not the game's sky (cloud share inside 0.34 against 0.71, sky3 0.87 against 0.67 at the clock),
so the ratio of the game's pass milliseconds to the player's could not be trusted; this series draws the game's own maps at the K-T2 views, with the cases of e40c1.CASES_D.
"""
import argparse
import json
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics import e40c1 as c1  # noqa: E402
from weather_metrics import e40gc as gc  # noqa: E402

BUILD = c1.BUILD
IN = os.path.join(BUILD, "e40c2-in")
RESULTS = os.path.join(BUILD, "e40c2-game-cost-results.json")
SCENES_JSON = os.path.join(IN, "game-scenes.json")


def scenes():
    """The scenes of the inputs: name -> the K-T2 view the dump was taken in (None: all four views)."""
    if not os.path.isfile(SCENES_JSON):
        return {}
    with open(SCENES_JSON, encoding="utf-8") as f:
        data = json.load(f)
    return data if isinstance(data, dict) else {n: None for n in data}


def scene_views(scene):
    view = scenes().get(scene)
    return [view] if view in c1.VIEWS else list(c1.VIEWS)


def prepare_all(maps_folder):
    """Imports every baseline dump of a start (<maps_folder>/<fw_off|k_off>-<view>-u<ut>, T09ProbeSoft.SoftCostWindow) as a scene drawn in its own view; the type views' dumps stand at other places and are left out."""
    names = []
    for entry in sorted(os.listdir(maps_folder)):
        parts = entry.rsplit("-", 2)
        if len(parts) != 3 or not parts[0].endswith("off") or parts[1] not in c1.VIEWS or not os.path.isdir(os.path.join(maps_folder, entry)):
            continue
        names.append(prepare_game(os.path.join(maps_folder, entry), entry, parts[1]))
    return names


def prepare_game(dump, name=None, view=None):
    """Copies one dump folder (the layers' -cover.u8 and -type.u8 and humidity.u8) as the scene `name` (default: the folder's name without the minus and underscore signs), drawn in `view` (None: all four)."""
    name = (name or os.path.basename(os.path.normpath(dump))).replace("-", "").replace("_", "")
    os.makedirs(IN, exist_ok=True)
    for layer in c1.STACK:
        for kind in ("cover", "type"):
            src = os.path.join(dump, "%s-%s.u8" % (layer, kind))
            if not os.path.isfile(src):
                raise SystemExit("the dump has no %s" % src)
            shutil.copyfile(src, os.path.join(IN, c1.map_name(name, "cover", layer, kind)))
    shutil.copyfile(os.path.join(dump, "humidity.u8"), os.path.join(IN, "%s-humidity.r8" % name))
    known = scenes()
    known[name] = view
    with open(SCENES_JSON, "w", encoding="utf-8") as f:
        json.dump(known, f)
    write_inputs()
    return name


def jobs():
    out = []
    for scene in scenes():
        for view in scene_views(scene):
            out.append((scene, c1.build_job(scene, "now", view, "lum", c1.COST_SIZE, "gn-%s-prod-f0" % c1.tag(scene, "now", view))))
    return out


def write_inputs():
    listed = jobs()
    os.makedirs(IN, exist_ok=True)
    with open(os.path.join(IN, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": [j for _, j in listed]}, f)
    # The height variation of the game, as in e40c1.write_jobs (the humidity of the dump's time).
    specs = [dict(id=j["id"], variation=1, physics=1, topUpdraft=1, ragged=1, humidityMap="%s-humidity.r8" % scene, updraftMap="", seed=664216370 & 0xFFFFFF) for scene, j in listed]
    with open(os.path.join(IN, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": specs}, f)


CASES = [c1.BASE, c1.R33, c1.R33 + "+mn150", c1.R33 + "+lp100", c1.R33 + "+lp200"]


def stage_list():
    views = [c1.tag(s, "now", v) for s in scenes() for v in scene_views(s)]
    if not views:
        return []
    write_inputs()
    return [Stage("c2-game", ["-mode", "scene", "-scene", "soft-cost", "-in", IN, "-views", ",".join(views), "-media", "pr50", "-marches", ",".join(CASES)],
                  size=(1707, 960), proxy=True, timeout=1700)]


def collect(ctx):
    return gc.collect_cost(ctx, [("c2-game", "", "", [])], RESULTS)


def selftest():
    import tempfile
    global IN, SCENES_JSON
    old = IN, SCENES_JSON
    IN = tempfile.mkdtemp()
    SCENES_JSON = os.path.join(IN, "game-scenes.json")
    try:
        dump = tempfile.mkdtemp()
        for layer in c1.STACK:
            for kind in ("cover", "type"):
                with open(os.path.join(dump, "%s-%s.u8" % (layer, kind)), "wb") as f:
                    f.write(bytes(c1.MAP_W * c1.MAP_H))
        with open(os.path.join(dump, "humidity.u8"), "wb") as f:
            f.write(bytes(c1.MAP_W * c1.MAP_H))
        name = prepare_game(dump, "clock-u1686000")
        assert name == "clocku1686000" and list(scenes()) == [name]
        with open(os.path.join(IN, "jobs.json"), encoding="utf-8") as f:
            listed = json.load(f)["jobs"]
        assert len(listed) == len(c1.VIEWS) and all(j["layers"][0]["coverageMap"].startswith(name) for j in listed)
        assert len(stage_list()) == 1
        # A start's folder of dumps: the baseline windows' dumps become scenes of their own view, the type views' dumps are left out.
        maps = tempfile.mkdtemp()
        for entry in ("fw_off-ground-u1881019", "k_off-sky3-u1686000", "clock-humilis_ground-u1686100"):
            shutil.copytree(dump, os.path.join(maps, entry))
        imported = prepare_all(maps)
        assert imported == ["fwoffgroundu1881019", "koffsky3u1686000"], imported
        assert scenes()["koffsky3u1686000"] == "sky3" and scene_views("fwoffgroundu1881019") == ["ground"]
        with open(os.path.join(IN, "jobs.json"), encoding="utf-8") as f:
            assert len(json.load(f)["jobs"]) == len(c1.VIEWS) + 2
    finally:
        IN, SCENES_JSON = old
    print("selftest passed")


register_series("e40-c2-game-cost", stage_list(), collect, exclusive=True, who="e40-c2")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--prepare-game", nargs="+", metavar="DUMP")
    parser.add_argument("--prepare-all", metavar="MAPS", help="the folder series-pictures/maps of a start: every baseline dump as a scene of its own view")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.prepare_all:
        print("prepared scenes", ", ".join(prepare_all(a.prepare_all)))
    elif a.prepare_game:
        print("prepared scene", prepare_game(a.prepare_game[0], a.prepare_game[1] if len(a.prepare_game) > 1 else None))
