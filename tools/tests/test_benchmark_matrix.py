"""The log parser and the matrix of tools/benchmark_matrix.py, on excerpts of the logs a
probe start writes (tools/tests/fixtures).

    python -m unittest
"""
import copy
import os
import re
import shutil
import tempfile
import unittest

from tools import benchmark_matrix as bm

HERE = os.path.dirname(os.path.abspath(__file__))


def fixture(name):
    with open(os.path.join(HERE, "fixtures", name), encoding="utf-8") as f:
        return f.read()


def scenario(fg, fg_requested, latency, log_name="AMD FSR (DLL)"):
    return {"technique": "amd", "technique_name": "AMD DLL", "technique_log_name": log_name, "raytracing": "Off",
            "ray_reconstruction": None, "fg": fg, "fg_name": "off" if fg == "off" else "FSR", "fg_requested": fg_requested,
            "fg_kind": "fsr", "latency": latency, "latency_name": latency}


def fixture_start(log_name="AMD FSR (DLL)"):
    return {"id": "amd-rtoff-fsr", "phases": [
        {"scenario": scenario("off", False, "none", log_name),
         "keys": {"fgMinimumFps": "1000", "lowLatency": "0", "latencyBrake": "0", "antiLag2": "0"}},
        {"scenario": scenario("fsr", True, "reflex+brake", log_name),
         "keys": {"fgMinimumFps": "0", "lowLatency": "1", "latencyBrake": "1", "latencyBrakeTargetMs": "4", "antiLag2": "0"}},
    ]}


class ConfigLineTest(unittest.TestCase):
    def test_values_with_a_space_stay_whole(self):
        values = bm.parse_config("enabled=1 pacing=0.100000/0.100000/spin 2 lowLatency=0 latencyBrake=1/4.000000")
        self.assertEqual(values["pacing"], "0.100000/0.100000/spin 2")
        self.assertEqual(values["lowLatency"], "0")
        self.assertEqual(values["latencyBrake"], "1/4.000000")

    def test_the_reload_note_is_not_a_value(self):
        values = bm.parse_config("reportSeconds=2 streamlineDirectory=(none)   (enabled, allowTearing, measureOnly as at load)")
        self.assertEqual(values, {"reportSeconds": "2", "streamlineDirectory": "(none)"})


class ProxyLogTest(unittest.TestCase):
    def setUp(self):
        self.log = bm.parse_proxy_log(fixture("bench.proxy.log"))

    def test_load_and_reloads(self):
        self.assertFalse(self.log["configs"][0]["reloaded"])
        self.assertEqual(self.log["configs"][0]["values"]["dlssFrameGeneration"], "0")
        self.assertEqual([c["reloaded"] for c in self.log["configs"]], [False, True, True, True])

    def test_reports(self):
        first = self.log["reports"][0]
        self.assertEqual(first["frames"], 140)
        self.assertAlmostEqual(first["mean_ms"], 14.29)
        self.assertEqual(first["displayed_per_s"], 70)
        self.assertEqual(first["refresh_hz"], 120)
        self.assertEqual(len(self.log["reports"]), 13)

    def test_latency_lines(self):
        self.assertEqual(self.log["latency"][0]["lowered_by"], "nothing")
        self.assertEqual(self.log["latency"][-1]["lowered_by"], "Reflex and the brake")
        self.assertEqual(len(self.log["pcl"]), 3)
        self.assertAlmostEqual(self.log["brake"][0]["wait_ms"], 4.8)

    def test_frame_generation_kind(self):
        self.assertEqual(bm._fg_kind_ran(self.log), "FSR")


class RtBuildTest(unittest.TestCase):
    LOG = ("19:40:24.558  Raytracing: 150 meshes built in one frame, 54272 triangles, 33774 vertices, the largest 5952"
           " triangles, 1.151 ms of the CPU (rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2)\n"
           "19:40:24.591  Raytracing: the 150 meshes built in one frame (54272 triangles) took 0.616000 ms of the GPU"
           " and 0.960000 ms of the CPU\n"
           "19:40:25.700  Raytracing: the 600 meshes built in one frame (244404 triangles) took 1.190000 ms of the GPU"
           " and 3.587000 ms of the CPU\n")

    def test_the_mesh_builds_of_a_start(self):
        log = bm.parse_proxy_log(self.LOG)
        self.assertEqual([b["meshes"] for b in log["rt_builds"]], [150, 600])
        builds = bm.rt_builds_of(log["rt_builds"])
        self.assertEqual(builds["frames"], 2)
        self.assertEqual(builds["triangles"], 298676)
        self.assertEqual(builds["slowest_gpu_ms"], 1.19)
        self.assertEqual(builds["slowest_cpu_ms"], 3.587)
        self.assertIsNone(bm.rt_builds_of([]))

    def test_the_default_matrix_pins_the_build_keys(self):
        matrix = bm.load_matrix(bm.DEFAULT_MATRIX)
        for key in ("rtBlasBatch", "rtBlasPool", "rtBlasBudget", "rtBlasCompaction", "rtBlasRefineBudget", "rtTlasFastTrace",
                    "rtMegaGeometry"):
            self.assertIn(key, matrix["proxy_load"])


class KspLogTest(unittest.TestCase):
    def test_probe_lines(self):
        log = bm.parse_ksp_log(fixture("bench.KSP.log"))
        self.assertEqual(log["gpu"], "NVIDIA RTX A4500 (20250MB)")
        self.assertEqual(log["upscaler"][0]["technique"], "AMD FSR (DLL)")
        self.assertEqual(log["upscaler"][0]["mode"], "Quality")
        self.assertEqual(log["upscaler"][0]["render"], [1707, 960])
        self.assertEqual(len(log["sets"]), 2)
        self.assertEqual([s["index"] for s in log["shots"]], [0, 1])


class PhaseTest(unittest.TestCase):
    def phases(self, start):
        return bm.phases_of_start(start, fixture("bench.KSP.log"), fixture("bench.proxy.log"), 3)

    def test_each_phase_measured_inside_its_window(self):
        result = self.phases(fixture_start())
        off, on = result["phases"]
        self.assertEqual(off["warnings"], [])
        self.assertEqual(off["measured"]["reports"], 4)
        self.assertEqual(off["measured"]["fps"], 74.0)
        self.assertEqual(off["measured"]["latency_median_ms"], 31.0)
        self.assertEqual(off["measured"]["latency_p90_ms"], 34.0)
        self.assertEqual(off["measured"]["latency_lowered_by"], "nothing")
        self.assertEqual(off["fg_kind_ran"], "none")
        # The report at 16:39:54 lies in the settling time and is left out.
        self.assertEqual(on["warnings"], [])
        self.assertEqual(on["measured"]["reports"], 4)
        self.assertEqual(on["measured"]["fps"], 63.7)
        self.assertEqual(on["measured"]["presented_per_rendered"], 2.0)
        self.assertEqual(on["measured"]["shown_fps"], 127.5)
        self.assertEqual(on["measured"]["latency_median_ms"], 26.0)
        self.assertEqual(on["measured"]["nvidia_pcl_median_ms"], 9.2)
        self.assertEqual(on["measured"]["brake_sleep_ms"], 0.4)
        self.assertEqual(on["fg_kind_ran"], "FSR")
        self.assertEqual(on["proxy_config"]["fgMinimumFps"], "0")
        self.assertEqual(result["gpu"], "NVIDIA RTX A4500 (20250MB)")
        self.assertEqual(result["proxy_load_config"]["amdUpscalerDirectory"], "(set)")

    def test_what_did_not_run_as_asked_is_named(self):
        start = fixture_start(log_name="DLSS")
        start["phases"][0]["keys"]["lowLatency"] = "1"
        start["phases"][0]["scenario"]["fg_requested"] = True
        off = self.phases(start)["phases"][0]
        self.assertIn("asked for DLSS, ran AMD FSR (DLL)", off["warnings"])
        self.assertIn("lowLatency=1 asked for, the proxy has 0", off["warnings"])
        self.assertIn("frame generation asked for, presented/rendered 1.00", off["warnings"])

    def test_mod_keys_and_the_settling_time_of_a_phase(self):
        start = fixture_start()
        start["mod_settings"] = {"raytracing": "Off"}
        start["phases"][1]["keys"]["mod.raytracing"] = "Full"
        start["phases"][1]["scenario"]["raytracing"] = "Full"
        start["phases"][1]["settle"] = 6
        ksp = fixture("bench.KSP.log").replace(
            "[LOG 16:39:52.120] [ReDefinition] Probe: ReDefinitionProxy.ini set to",
            "[LOG 16:39:52.110] [ReDefinition] Raytracing set to Full, 2 bounce(s).\n"
            "[LOG 16:39:52.120] [ReDefinition] Probe: ReDefinitionProxy.ini set to")
        result = bm.phases_of_start(start, ksp, fixture("bench.proxy.log"), 3)
        off, on = result["phases"]
        self.assertEqual(off["warnings"], [])
        self.assertEqual(on["warnings"], [])
        self.assertEqual(on["settle_seconds"], 6)
        # From 16:39:58.8 on, each report covering the two seconds before it: 16:40:02 and :04.
        self.assertEqual(on["measured"]["reports"], 2)
        start["phases"][1]["scenario"]["raytracing"] = "Light"
        on = bm.phases_of_start(start, ksp, fixture("bench.proxy.log"), 3)["phases"][1]
        self.assertIn("raytracing Light asked for, Full set", on["warnings"])

    def test_a_setting_the_probe_does_not_know_is_a_problem(self):
        ksp = fixture("bench.KSP.log") + "[WRN 16:40:05.000] [ReDefinition] Probe: no setting technik takes Dlss.\n"
        result = bm.phases_of_start(fixture_start(), ksp, fixture("bench.proxy.log"), 3)
        self.assertEqual(result["problems"], ["Probe: no setting technik takes Dlss."])

    def test_a_missing_phase_is_a_problem(self):
        start = fixture_start()
        start["phases"].append(copy.deepcopy(start["phases"][1]))
        start["phases"][2]["scenario"]["latency"] = "brake"
        result = self.phases(start)
        self.assertEqual(len(result["phases"]), 2)
        self.assertEqual(result["problems"], ["the probe set 2 of 3 key sets"])

    def test_repeated_phases_are_one(self):
        merged = bm._merge_repeats([
            {"key": "a", "window": [1, 2], "warnings": ["x"]},
            {"key": "a", "window": [3, 4], "warnings": ["y"]},
            {"key": "b", "window": [5, 6], "warnings": []}])
        self.assertEqual(len(merged), 2)
        self.assertEqual(merged[0]["window"], [1, 4])
        self.assertEqual(merged[0]["repeats"], 2)
        self.assertEqual(merged[0]["warnings"], ["x", "y"])


class ClockTest(unittest.TestCase):
    def test_midnight(self):
        lines = bm._timed_lines("23:59:59.500  a\n00:00:00.500  b\n", bm._PROXY_TIME)
        self.assertAlmostEqual(lines[1][0] - lines[0][0], 1.0)

    def test_a_stale_line_of_another_day_does_not_move_the_ksp_clock(self):
        # KSP.log holds ModuleManager's log of an earlier start: its patch lines carry the
        # old start's time, 23:49, and name the mod in NEEDS[ReDefinition].
        text = ("[LOG 00:08:20.418] [ReDefinition] Upscaler ready.\n"
                "[LOG 23:49:20.330] Applying update ReDefinition/Patches/X/@PART[*]:NEEDS[ReDefinition]\n"
                "[LOG 00:09:56.826] [ReDefinition] Probe: ReDefinitionProxy.ini set to a=1.\n")
        ksp = bm.parse_ksp_log(text)
        self.assertAlmostEqual(ksp["sets"][0]["time"] - ksp["first_time"], 96.408, places=2)


class MatrixTest(unittest.TestCase):
    ALL = {"dlssg_package": "p", "amd_dll_dir": "d", "xess_dll_dir": "x"}

    def setUp(self):
        self.matrix = bm.load_matrix(bm.DEFAULT_MATRIX)

    def test_default_matrix_on_nvidia(self):
        starts, skipped = bm.expand(self.matrix, "NVIDIA", self.ALL)
        self.assertEqual([s["id"] for s in starts], ["fsr", "dlssg", "dlssg-c"])
        # Nine renderings: FSR 3, the AMD DLL and XeSS with RT Off and Full, DLSS with RT
        # Off, Full with Ray Reconstruction and Full without; and FSR 3 at RT Full with the
        # radiance cache, then with the opacity micromaps, each without a latency model alone.
        # The first phase after a change of the rendering is held twice as long.
        self.assertEqual(len(starts[0]["phases"]), 9 * 8 + 8 + 2 + 1 + 2 + 1)
        self.assertEqual(len(starts[1]["phases"]), 9 * 4 + 8 + 1 + 1 + 1 + 1)
        self.assertIn(("latency antilag2", "only on AMD"), skipped)
        fsr = starts[0]
        self.assertEqual(fsr["proxy_load"]["amdUpscalerDirectory"], "d")
        self.assertEqual(fsr["proxy_load"]["xessDirectory"], "x")
        self.assertEqual(fsr["proxy_load"]["frameGenerationTechnique"], "fsr")
        self.assertFalse(fsr["dlssg_package"])
        self.assertTrue(starts[1]["dlssg_package"])
        self.assertEqual(starts[1]["dlssg_ini"], {"Logging": {"FrameTiming": "1"}})
        labels = {bm.scenario_label(p["scenario"]) for p in fsr["phases"]}
        self.assertIn("DLSS · RT Full, RR off · FG FSR · Reflex", labels)
        self.assertIn("XeSS · RT Off · FG off · none", labels)
        self.assertNotIn("FSR 3 · RT Full, RR on · FG off · none", labels)

    def test_preset_c_is_a_start_of_its_own_with_the_rows_it_names(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        preset = starts[2]
        self.assertEqual(preset["id"], "dlssg-c")
        self.assertTrue(preset["dlssg_package"])
        self.assertEqual(preset["dlssg_ini"], {"DLSSG": {"Preset": "C"}, "Logging": {"FrameTiming": "1"}})
        # A kind of DLSS-G for the checks, its rows its own.
        self.assertEqual(preset["fg_kind"], "dlssg")
        self.assertEqual({p["scenario"]["fg_kind"] for p in preset["phases"]}, {"dlssg"})
        self.assertEqual({p["scenario"]["fg"] for p in preset["phases"]}, {"dlssg-c"})
        # FSR 3 at RT Off and Full, FG on, no latency model; the first after the change held twice, and
        # neither the radiance cache's nor the micromaps' rendering.
        self.assertEqual({p["scenario"]["technique"] for p in preset["phases"]}, {"fsr3"})
        self.assertEqual([p["scenario"]["raytracing"] for p in preset["phases"]], ["Off", "Full", "Full"])
        self.assertEqual({p["scenario"]["latency"] for p in preset["phases"]}, {"none"})
        self.assertFalse(any(p["scenario"]["radiance_cache"] or p["scenario"]["opacity_micromaps"] for p in preset["phases"]))
        self.assertEqual(bm.scenario_label(preset["phases"][0]["scenario"]), "FSR 3 · RT Off · FG DLSS-G preset C · none")
        self.assertEqual(bm.scenario_key(preset["phases"][0]["scenario"]), "fsr3/Off/rr-na/fg-dlssg-c/lat-none")
        # Everything else is the plain DLSS-G start's: the package, the frame generation kind.
        self.assertEqual(preset["proxy_load"]["frameGenerationTechnique"], "dlss")
        self.assertEqual(preset["max_generated_frames"], starts[1]["max_generated_frames"])

    def test_the_first_phase_after_a_change_settles_longer(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        phases = starts[0]["phases"]
        self.assertFalse(phases[0]["rebuild"])
        self.assertEqual(phases[0]["settle"], 2)
        self.assertEqual(phases[0]["keys"]["mod.technique"], "Fsr3")
        self.assertEqual(phases[0]["keys"]["mod.raytracing"], "Off")
        change = phases[8]
        self.assertTrue(change["rebuild"])
        self.assertEqual(change["settle"], 8)
        self.assertEqual(change["keys"]["mod.raytracing"], "Full")
        self.assertEqual(phases[9]["keys"], change["keys"])
        self.assertFalse(phases[10]["rebuild"])

    def test_the_start_begins_with_its_first_rendering(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        self.assertEqual(starts[0]["mod_settings"]["technique"], "Fsr3")
        self.assertEqual(starts[0]["mod_settings"]["raytracing"], "Off")
        self.assertEqual(starts[0]["mod_settings"]["frameGeneration"], "True")

    def test_without_the_files_their_starts_are_skipped(self):
        starts, skipped = bm.expand(self.matrix, "NVIDIA", {})
        self.assertEqual([s["id"] for s in starts], ["fsr"])
        self.assertEqual({p["scenario"]["technique"] for p in starts[0]["phases"]}, {"fsr3", "dlss"})
        self.assertIn(("technique amd", "needs amd_dll_dir"), skipped)
        self.assertIn(("technique xess", "needs xess_dll_dir"), skipped)
        self.assertIn(("frame generation dlssg", "needs dlssg_package"), skipped)

    def test_on_amd(self):
        starts, _ = bm.expand(self.matrix, "AMD", self.ALL)
        self.assertEqual([s["id"] for s in starts], ["fsr"])
        self.assertEqual({p["scenario"]["technique"] for p in starts[0]["phases"]}, {"fsr3", "amd", "xess"})
        self.assertEqual({p["scenario"]["latency"] for p in starts[0]["phases"]}, {"none", "brake", "antilag2"})

    def test_separate_starts(self):
        self.matrix["separate_starts"] = ["technique"]
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        self.assertEqual([s["id"] for s in starts], ["fsr3-fsr", "dlss-fsr", "amd-fsr", "xess-fsr",
                                                     "fsr3-dlssg", "dlss-dlssg", "amd-dlssg", "xess-dlssg",
                                                     "fsr3-dlssg-c"])
        self.assertEqual(starts[1]["mod_settings"]["technique"], "Dlss")
        self.assertNotIn("xessDirectory", starts[1]["proxy_load"])

    def test_a_technique_is_one_line(self):
        self.matrix["techniques"].append({"id": "other", "name": "Other", "settings": {"technique": "Other"}})
        starts, _ = bm.expand(self.matrix, "NVIDIA", {})
        self.assertEqual({p["scenario"]["raytracing"] for p in starts[0]["phases"] if p["scenario"]["technique"] == "other"},
                         {"Off", "Full"})

    def test_every_set_names_every_key(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", {})
        sets = bm.ini_sets_argument(starts[0]["phases"]).split(";")
        self.assertEqual(len(sets), len(starts[0]["phases"]))
        self.assertEqual(sets[0], "reportSeconds=1,mod.technique=Fsr3,mod.raytracing=Off,mod.rayReconstruction=True,"
                                  "fgMinimumFps=1000,lowLatency=0,latencyBrake=0,antiLag2=0,rtRadianceCache=0,"
                                  "rtOpacityMicromaps=0,rtCutoutTest=0")
        for s in sets:
            for key in ("reportSeconds=1", "mod.technique=", "mod.raytracing=", "mod.rayReconstruction=", "fgMinimumFps=",
                        "lowLatency=", "latencyBrake=", "rtRadianceCache=", "rtOpacityMicromaps=", "rtCutoutTest="):
                self.assertIn(key, s)

    def test_the_radiance_cache_is_a_rendering_of_its_own(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        cached = [p for p in starts[0]["phases"] if p["scenario"]["radiance_cache"]]
        # FG off and on, without a latency model; the first held twice.
        self.assertEqual(len(cached), 3)
        self.assertEqual({p["keys"]["rtRadianceCache"] for p in cached}, {"1"})
        self.assertEqual({p["keys"]["mod.technique"] for p in cached}, {"Fsr3"})
        self.assertEqual({p["keys"]["mod.raytracing"] for p in cached}, {"Full"})
        self.assertEqual({p["scenario"]["latency"] for p in cached}, {"none"})
        self.assertTrue(cached[0]["rebuild"])
        self.assertEqual(bm.scenario_label(cached[0]["scenario"]), "FSR 3 · RT Full, radiance cache · FG off · none")
        self.assertEqual(bm.scenario_key(cached[0]["scenario"]), "fsr3/Full/rr-na/rc-on/fg-off/lat-none")
        # Right after FSR 3 at RT Full without it, whose rows keep their keys.
        plain = [p for p in starts[0]["phases"] if not p["scenario"]["radiance_cache"]]
        self.assertEqual({p["keys"]["rtRadianceCache"] for p in plain}, {"0"})
        self.assertIn("fsr3/Full/rr-na/fg-off/lat-none", {bm.scenario_key(p["scenario"]) for p in plain})
        index = starts[0]["phases"].index(cached[0])
        self.assertEqual(starts[0]["phases"][index - 1]["scenario"]["technique"], "fsr3")
        self.assertEqual(starts[0]["phases"][index - 1]["scenario"]["raytracing"], "Full")
        # XeSS-FG's start takes it with XeLL, the model it runs with.
        resources = dict(self.ALL, xess_fg_dll_dir="f")
        starts, _ = bm.expand(self.matrix, "NVIDIA", resources)
        xessfg = [s for s in starts if s["id"] == "xessfg"][0]
        self.assertEqual({p["scenario"]["latency"] for p in xessfg["phases"] if p["scenario"]["radiance_cache"]}, {"xell"})

    def test_the_opacity_micromaps_are_the_last_rendering_of_the_start(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        for start in starts:
            phases = start["phases"]
            micromaps = [p for p in phases if p["scenario"]["opacity_micromaps"]]
            # The preset C start names its renderings, and not these.
            if start["id"] == "dlssg-c":
                self.assertEqual(micromaps, [])
                continue
            self.assertTrue(micromaps)
            # The last phases of the start, so that no row after them has the pipelines made with
            # micromap support, which stay.
            self.assertEqual(phases[-len(micromaps):], micromaps)
            self.assertEqual({p["keys"]["rtOpacityMicromaps"] for p in micromaps}, {"1"})
            self.assertEqual({p["keys"]["rtRadianceCache"] for p in micromaps}, {"0"})
            self.assertEqual({p["keys"]["rtOpacityMicromaps"] for p in phases if not p["scenario"]["opacity_micromaps"]}, {"0"})
            self.assertEqual({p["keys"]["mod.technique"] for p in micromaps}, {"Fsr3"})
            self.assertEqual({p["keys"]["mod.raytracing"] for p in micromaps}, {"Full"})
            self.assertEqual({p["scenario"]["latency"] for p in micromaps}, {"none"})
            self.assertTrue(micromaps[0]["rebuild"])
        label = [p for p in starts[0]["phases"] if p["scenario"]["opacity_micromaps"]][0]["scenario"]
        self.assertEqual(bm.scenario_label(label), "FSR 3 · RT Full, opacity micromaps · FG off · none")
        self.assertEqual(bm.scenario_key(label), "fsr3/Full/rr-na/omm-on/fg-off/lat-none")
        # Separate starts keep it with its technique's.
        self.matrix["separate_starts"] = ["technique"]
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        self.assertEqual([s["id"] for s in starts if any(p["scenario"]["opacity_micromaps"] for p in s["phases"])],
                         ["fsr3-fsr", "fsr3-dlssg"])

    def test_xessfg_runs_with_xell_alone(self):
        resources = dict(self.ALL, xess_fg_dll_dir="f")
        starts, _ = bm.expand(self.matrix, "NVIDIA", resources)
        self.assertEqual([s["id"] for s in starts], ["fsr", "dlssg", "dlssg-c", "xessfg"])
        xessfg = starts[3]
        self.assertEqual(xessfg["proxy_load"]["frameGenerationTechnique"], "xess")
        self.assertEqual(xessfg["proxy_load"]["xessFgDirectory"], "f")
        self.assertEqual({p["scenario"]["latency"] for p in xessfg["phases"]}, {"xell"})

        # Eleven renderings with the radiance cache's and the micromaps', FG off (XeSS-FG loaded)
        # and on, the first after a change held twice.
        self.assertEqual(len(xessfg["phases"]), 11 * 2 + 10)
        self.assertIn("off-xessfg-loaded", {p["scenario"]["fg"] for p in xessfg["phases"]})
        for other in starts[:3]:
            self.assertNotIn("xell", {p["scenario"]["latency"] for p in other["phases"]})

    def test_dlssg_rows_are_their_own(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        dlssg = starts[1]
        self.assertEqual({p["scenario"]["fg"] for p in dlssg["phases"]}, {"dlssg"})
        keys = [bm.scenario_key(p["scenario"]) for s in starts for p in s["phases"]]
        merged = [k for i, k in enumerate(keys) if i == 0 or keys[i - 1] != k]
        self.assertEqual(len(merged), len(set(merged)))

    def test_estimate(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        minutes = bm.estimate_seconds(starts) / 60.0
        self.assertGreater(minutes, 25)
        self.assertLess(minutes, 35)


class FileEditTest(unittest.TestCase):
    def test_ini(self):
        text = "; head\r\n[Proxy]\r\nenabled=1\r\n; reportSeconds=5\r\nreportSeconds=10\r\nreportSeconds=11\r\n[Other]\r\nx=1\r\n"
        out = bm.set_ini_values(text, {"reportSeconds": "2", "lowLatency": "0"})
        self.assertEqual(out, "; head\r\n[Proxy]\r\nenabled=1\r\n; reportSeconds=5\r\nreportSeconds=2\r\nreportSeconds=11\r\n"
                              "lowLatency=0\r\n[Other]\r\nx=1\r\n")

    def test_cfg(self):
        text = "ReDefinition\n{\n\tenabled = True\n\traytracing = Full\n}\n"
        out = bm.set_cfg_values(text, {"raytracing": "Off", "rayReconstruction": "False"})
        self.assertEqual(out, "ReDefinition\n{\n\tenabled = True\n\traytracing = Off\n\trayReconstruction = False\n}\n")

    def test_driver(self):
        self.assertEqual(bm.nvidia_driver_from_windows("32.0.15.9716"), "597.16")
        self.assertIsNone(bm.nvidia_driver_from_windows(None))


class InstallTest(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.ksp = os.path.join(self.root, "ksp")
        os.makedirs(os.path.join(self.ksp, "GameData", "ReDefinition", "PluginData"))
        self.write(os.path.join(self.ksp, "settings.cfg"), "ksp")
        self.write(os.path.join(self.ksp, bm.PROXY_INI), "[Proxy]\nenabled=1\n")
        self.write(os.path.join(self.ksp, bm.SETTINGS_REL), "ReDefinition\n{\n}\n")
        self.write(os.path.join(self.ksp, "dxgi.dll"), "not a settings file")
        self.install = bm.Install(self.ksp)

    def tearDown(self):
        shutil.rmtree(self.root)

    @staticmethod
    def write(path, text):
        with open(path, "w") as f:
            f.write(text)

    def test_snapshot_and_restore(self):
        snap = os.path.join(self.root, "snap")
        self.assertEqual(self.install.snap(snap), 3)
        self.write(os.path.join(self.ksp, bm.PROXY_INI), "[Proxy]\nenabled=0\n")
        report = self.install.restore(snap)
        self.assertEqual(report, ["restored " + bm.PROXY_INI])
        with open(os.path.join(self.ksp, bm.PROXY_INI)) as f:
            self.assertEqual(f.read(), "[Proxy]\nenabled=1\n")

    def test_restore_removes_settings_files_the_run_made(self):
        snap = os.path.join(self.root, "snap")
        self.install.snap(snap)
        made = os.path.join("GameData", "ReDefinition", "PluginData", "bundled.cfg")
        self.write(os.path.join(self.ksp, made), "new")
        self.write(os.path.join(self.ksp, "squadcorefx.ksp"), "KSP's own")
        report = self.install.restore(snap)
        self.assertIn("removed " + made, report)
        self.assertFalse(os.path.exists(os.path.join(self.ksp, made)))
        self.assertTrue(os.path.exists(os.path.join(self.ksp, "squadcorefx.ksp")))
        self.assertIn("written since, not kept: squadcorefx.ksp", report)

    def package(self, complete=True):
        package = os.path.join(self.root, "package")
        for sub in bm.PACKAGE_SUBDIRS + ("sr",):
            if sub == "runtime" and not complete:
                continue
            os.makedirs(os.path.join(package, bm.PACKAGE_DIR, sub), exist_ok=True)
            self.write(os.path.join(package, bm.PACKAGE_DIR, sub, "a.bin"), sub)
        self.write(os.path.join(package, "version.dll"), "dll")
        self.write(os.path.join(package, "dlssg_sm86.ini"), "[FrameGeneration]\nMaxGeneratedFrames=3\n")
        return package

    def test_a_partial_package_is_removed_after_the_start(self):
        install = self.install
        install.launch = lambda args, timeout: self.fail("KSP started without its package")
        start = {"id": "dlssg", "phases": [], "mod_settings": {}, "proxy_load": {}, "dlssg_package": True,
                 "max_generated_frames": 1}
        raw = os.path.join(self.root, "raw")
        os.makedirs(raw)
        with self.assertRaises(OSError):
            bm.run_start(install, start, raw, "ReDefinition\n{\n}\n", "[Proxy]\n", self.package(complete=False))
        self.assertEqual(install.package_in_place(), [])
        # A copy left half done by anything else is cleared by the next one.
        self.write(os.path.join(self.ksp, "version.dll"), "stale")
        os.makedirs(os.path.join(self.ksp, bm.PACKAGE_DIR, "backend"))
        shutil.rmtree(os.path.join(self.root, "package"))
        install.put_package(self.package(), 1)
        with open(os.path.join(self.ksp, "version.dll")) as f:
            self.assertEqual(f.read(), "dll")

    def test_the_starts_begin_from_the_settings_the_profile_left(self):
        settings = os.path.join(self.ksp, bm.SETTINGS_REL)

        def profile_start(args, timeout):
            with open(settings) as f:
                text = f.read()
            self.write(settings, text.replace("dlssModel = High", "dlssModel = Default"))
            self.write(os.path.join(self.ksp, "KSP.log"), "[LOG 10:00:00.000] [ReDefinition] Probe: profile medium applied.\n")
            return "quit"
        self.install.launch = profile_start
        raw = os.path.join(self.root, "raw")
        os.makedirs(raw)
        lines, template = bm.apply_profile(self.install, "medium", "ReDefinition\n{\n\tdlssModel = High\n}\n", raw)
        self.assertEqual(lines, ["[LOG 10:00:00.000] [ReDefinition] Probe: profile medium applied."])
        self.assertIn("dlssModel = Default", template)
        out = bm.set_cfg_values(template, {"technique": "Dlss"})
        self.assertIn("dlssModel = Default", out)
        self.assertIn("technique = Dlss", out)

    def test_a_profile_that_did_not_apply_stops_the_run(self):
        self.install.launch = lambda args, timeout: "quit"
        raw = os.path.join(self.root, "raw")
        os.makedirs(raw)
        with self.assertRaises(RuntimeError):
            bm.apply_profile(self.install, "medium", "ReDefinition\n{\n}\n", raw)

    def test_a_start_writes_its_values_into_the_packages_ini(self):
        package = os.path.join(self.root, "package")
        for sub in bm.PACKAGE_SUBDIRS:
            os.makedirs(os.path.join(package, bm.PACKAGE_DIR, sub))
            self.write(os.path.join(package, bm.PACKAGE_DIR, sub, "a.bin"), sub)
        self.write(os.path.join(package, "version.dll"), "dll")
        ini = ("[FrameGeneration]\r\n; ceiling\r\nMaxGeneratedFrames=3\r\n\r\n[DLSSG]\r\nOptimized=2\r\nPreset=Auto\r\n\r\n"
               "[Logging]\r\nLevel=1\r\nDirectory=dlssg_sm86\\logs\r\n\r\n[SuperResolution]\r\nMode=Off\r\n")
        with open(os.path.join(package, "dlssg_sm86.ini"), "w", encoding="utf-8", newline="") as f:
            f.write(ini)
        self.install.put_package(package, 1, {"DLSSG": {"Preset": "C"}, "Logging": {"FrameTiming": "1"}})
        with open(os.path.join(self.ksp, "dlssg_sm86.ini"), encoding="utf-8", newline="") as f:
            written = f.read()
        self.assertEqual(written, ini.replace("MaxGeneratedFrames=3", "MaxGeneratedFrames=1").replace("Preset=Auto", "Preset=C")
                         .replace("Directory=dlssg_sm86\\logs\r\n", "Directory=dlssg_sm86\\logs\r\nFrameTiming=1\r\n"))

    def test_the_packages_ini_values(self):
        text = "[A]\nx=1\n\n[B]\ny=2\n"
        self.assertEqual(bm.set_package_ini_value(text, "A", "x", "9"), "[A]\nx=9\n\n[B]\ny=2\n")
        self.assertEqual(bm.set_package_ini_value(text, "A", "z", "9"), "[A]\nx=1\nz=9\n\n[B]\ny=2\n")
        self.assertEqual(bm.set_package_ini_value(text, "C", "z", "9"), text + "\n[C]\nz=9\n")
        self.assertEqual(bm.set_package_ini_value("[A]\n; x=1\nx=2", "A", "x", "5"), "[A]\n; x=1\nx=5")
        # A key of another section is not the section's.
        self.assertEqual(bm.set_package_ini_value(text, "B", "x", "9"), "[A]\nx=1\n\n[B]\ny=2\nx=9\n")

    def test_package_in_and_out(self):
        package = os.path.join(self.root, "package")
        for sub in bm.PACKAGE_SUBDIRS + ("sr",):
            os.makedirs(os.path.join(package, bm.PACKAGE_DIR, sub))
            self.write(os.path.join(package, bm.PACKAGE_DIR, sub, "a.bin"), sub)
        self.write(os.path.join(package, "version.dll"), "dll")
        self.write(os.path.join(package, "dlssg_sm86.ini"), "[FrameGeneration]\nMaxGeneratedFrames=3\n")
        self.install.put_package(package, 1)
        self.assertEqual(sorted(self.install.package_in_place()), ["dlssg_sm86", "dlssg_sm86.ini", "version.dll"])
        with open(os.path.join(self.ksp, "dlssg_sm86.ini")) as f:
            self.assertIn("MaxGeneratedFrames=1", f.read())
        self.assertFalse(os.path.exists(os.path.join(self.ksp, bm.PACKAGE_DIR, "sr")))
        self.install.remove_package()
        self.assertEqual(self.install.package_in_place(), [])


class FakeWindows:
    """The calls of bm._Win32, with a window that Windows lets through after `allow` of them."""

    def __init__(self, front, minimised=False, allow=None):
        self.hwnd = 7
        self.front = front
        self.is_minimised = minimised
        self.allow = allow
        self.calls = []

    def find_window(self, pid):
        return self.hwnd

    def foreground(self):
        return self.front

    def minimised(self, hwnd):
        return self.is_minimised

    def restore(self, hwnd):
        self.calls.append("restore")
        self.is_minimised = False

    def _try(self, name):
        self.calls.append(name)
        if self.allow == name:
            self.front = self.hwnd
        return self.front == self.hwnd

    def set_foreground(self, hwnd):
        return self._try("plain")

    def set_foreground_attached(self, hwnd):
        return self._try("attached")

    def alt_tap(self, hwnd):
        return self._try("alt")


class StagesLineTest(unittest.TestCase):
    def test_the_parser_reads_the_line_the_proxy_writes(self):
        """The five labels are the proxy's (LowLatency.cpp, names[]): both change together."""
        source = os.path.join(os.path.dirname(HERE), "..", "src", "DxgiProxy", "LowLatency.cpp")
        with open(source, encoding="utf-8") as f:
            text = f.read()
        block = text[text.index("names[kLatencyStages]"):]
        block = block[:block.index("};")]
        labels = re.findall(r'"((?:[^"\\]|\\.)*)"', block)
        self.assertEqual(len(labels), 5)
        line = "Latency: the frame's stages, medians in ms -- " + ", ".join(
            "{} {}".format(label, value) for label, value in zip(labels, ("1.5", "2.5", "3.5", "4.5", "5.5")))
        log = bm.parse_proxy_log("12:00:00.000  " + line + "\n")
        self.assertEqual(len(log["stages"]), 1)
        self.assertEqual(log["stages"][0]["render_thread"], 5.5)
        self.assertEqual(log["stages"][0]["simulation"], 1.5)


class ForegroundTest(unittest.TestCase):
    def test_no_window_of_a_process_that_is_not_there(self):
        self.assertFalse(bm.bring_to_front(0x7FFFFFF0))

    def test_a_window_in_front_is_left_alone(self):
        win = FakeWindows(front=7, minimised=True)
        self.assertTrue(bm.Foreground(1, win).keep())
        self.assertEqual(win.calls, [])

    def test_the_gentlest_way_that_works_is_the_one_taken(self):
        win = FakeWindows(front=3, allow="plain")
        self.assertTrue(bm.Foreground(1, win).keep())
        self.assertEqual(win.calls, ["plain"])
        win = FakeWindows(front=3, allow="attached")
        self.assertTrue(bm.Foreground(1, win).keep())
        self.assertEqual(win.calls, ["plain", "attached"])

    def test_a_window_is_restored_only_where_it_is_minimised(self):
        win = FakeWindows(front=3, allow="plain")
        bm.Foreground(1, win).keep()
        self.assertNotIn("restore", win.calls)
        win = FakeWindows(front=3, minimised=True, allow="plain")
        bm.Foreground(1, win).keep()
        self.assertEqual(win.calls, ["restore", "plain"])

    def test_alt_is_tapped_once_and_last(self):
        now = [100.0]
        win = FakeWindows(front=3)
        keeper = bm.Foreground(1, win, clock=lambda: now[0])
        self.assertFalse(keeper.keep())
        self.assertEqual(win.calls, ["plain", "attached", "alt"])
        # Refused: left alone for a while, then tried again without the Alt press.
        now[0] += bm.FOREGROUND_RETRY_SECONDS / 2
        self.assertFalse(keeper.keep())
        self.assertEqual(len(win.calls), 3)
        now[0] += bm.FOREGROUND_RETRY_SECONDS
        self.assertFalse(keeper.keep())
        self.assertEqual(win.calls, ["plain", "attached", "alt", "plain", "attached"])
        self.assertEqual(win.calls.count("alt"), 1)

    def test_the_wait_ends_once_the_window_is_in_front(self):
        now = [100.0]
        win = FakeWindows(front=3)
        keeper = bm.Foreground(1, win, clock=lambda: now[0])
        keeper.keep()
        win.front = win.hwnd
        self.assertTrue(keeper.keep())
        win.front = 3
        win.allow = "plain"
        self.assertTrue(keeper.keep())
        self.assertEqual(win.calls[-1], "plain")

    def test_the_help_says_the_machine_is_not_to_be_used(self):
        import contextlib
        import io
        out = io.StringIO()
        with contextlib.redirect_stdout(out), self.assertRaises(SystemExit):
            bm.main(["--help"])
        self.assertIn("Do not use the machine while a run is going", out.getvalue())

    def test_the_games_window_out_of_the_foreground_is_told_by_the_proxys_last_word(self):
        proxy = {"lines": {"dlssg": [
            {"time": 10.0, "text": "DLSS frame generation on, 1 generated frame(s) per rendered"},
            {"time": 20.0, "text": "DLSS frame generation: the game's window is not in the foreground -- Streamline's DLSS-G"
                                   " generates no frames while it is not"},
            {"time": 40.0, "text": "DLSS frame generation: the game's window is in the foreground again"},
        ]}}
        self.assertFalse(bm._unfocused_at(proxy, 15.0))
        self.assertTrue(bm._unfocused_at(proxy, 30.0))
        self.assertFalse(bm._unfocused_at(proxy, 50.0))
        self.assertFalse(bm._unfocused_at({"lines": {}}, 50.0))


class DocTest(unittest.TestCase):
    def record(self, stamp, fps_scale=1.0):
        parsed = bm.phases_of_start(fixture_start(), fixture("bench.KSP.log"), fixture("bench.proxy.log"), 3)
        for p in parsed["phases"]:
            p["measured"]["fps"] = round(p["measured"]["fps"] * fps_scale, 1)
        start = {"id": "amd-rtoff-fsr"}
        start.update(parsed)
        return {"stamp": stamp, "date": "2026-10-20T18:00:00", "finished": "2026-10-20T18:40:00",
                "git": {"short": "abc1234", "branch": "feat/raytracing", "dirty": False},
                "machine": {"adapters": [{"name": "NVIDIA RTX A4500", "driver": "32.0.15.9716", "nvidia_driver": "597.16",
                                          "refresh_hz": 120}], "cpu": "AMD Ryzen 5 5600X", "memory_gb": 31.9},
                "vendor": "NVIDIA", "profile": "medium", "profile_lines": ["Probe: profile medium applied."],
                "matrix_file": "tools/benchmark_matrix.json", "settle_seconds": 3,
                "matrix": {"baseline": {"technique": "amd", "raytracing": "Off", "ray_reconstruction": None, "fg": "off",
                                        "latency": "none"}},
                "starts": [start]}

    def test_table_and_runs(self):
        doc = "head\n{}\nold\n{}\nmiddle\n{}\nold\n{}\ntail\n".format(bm.TABLE_START, bm.TABLE_END, bm.RUNS_START, bm.RUNS_END)
        out = bm.render_doc([("2026-10-19-1800.json", self.record("2026-10-19-1800", 0.5)),
                             ("2026-10-20-1800.json", self.record("2026-10-20-1800"))], doc)
        self.assertTrue(out.startswith("head\n" + bm.TABLE_START))
        self.assertTrue(out.endswith(bm.RUNS_END + "\ntail\n"))
        self.assertNotIn("old", out)
        self.assertIn("| AMD DLL · RT Off · FG off · none | 74.0 | 74.0 | 31.0 | 34.0 | +0.0 % | +0.0 ms |", out)
        self.assertIn("| AMD DLL · RT Off · FG FSR · reflex+brake | 63.7 | 127.5 | 26.0 | 28.0 | -13.9 % | -5.0 ms "
                      "| -13.9 % | -5.0 ms | +99.7 % |", out)
        self.assertIn("| Machine | NVIDIA RTX A4500, driver 597.16", out)
        self.assertLess(out.index("### Run 2026-10-20-1800"), out.index("### Run 2026-10-19-1800"))


REPO_ROOT = os.path.dirname(os.path.dirname(HERE))


def read_source(*parts):
    with open(os.path.join(REPO_ROOT, *parts), encoding="utf-8") as f:
        return f.read()


class GroupTest(unittest.TestCase):
    ALL = {"dlssg_package": "p", "amd_dll_dir": "d", "xess_dll_dir": "x", "xess_fg_dll_dir": "f", "parallax": "g"}

    def setUp(self):
        self.matrix = bm.load_matrix(bm.DEFAULT_MATRIX)

    def starts(self, group):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL, [group])
        return starts

    def test_the_default_run_is_the_main_matrix(self):
        self.assertEqual([g["id"] for g in bm.select_groups(self.matrix)], ["main"])
        self.assertEqual({s["group"] for s in bm.expand(self.matrix, "NVIDIA", self.ALL)[0]}, {"main"})
        ids = [g["id"] for g in bm.group_list(self.matrix)]
        self.assertEqual(ids, ["main", "rt-modes", "frame-steps", "dlssg-presets", "latency-brake", "blas", "grass"])
        self.assertEqual([g["id"] for g in bm.select_groups(self.matrix, ["all"])], ids)
        self.assertEqual([g["id"] for g in bm.select_groups(self.matrix, ["blas", "main"])], ["main", "blas"])
        with self.assertRaises(ValueError):
            bm.select_groups(self.matrix, ["nothing"])
        # The big additions are not part of the default run.
        self.assertFalse(any(g.get("default") for g in self.matrix["groups"]))

    def test_every_group_expands_with_unique_rows_and_starts(self):
        for group in bm.group_list(self.matrix):
            starts = self.starts(group["id"])
            self.assertTrue(starts, group["id"])
            ids = [s["id"] for s in starts]
            self.assertEqual(len(ids), len(set(ids)), group["id"])
            self.assertEqual({s["group"] for s in starts}, {group["id"]})
            keys = [bm.scenario_key(p["scenario"]) for s in starts for p in s["phases"]]
            merged = [k for i, k in enumerate(keys) if i == 0 or keys[i - 1] != k]
            self.assertEqual(len(merged), len(set(merged)), group["id"])
            for s in starts:
                sets = bm.ini_sets_argument(s["phases"]).split(";")
                self.assertEqual(len(sets), len(s["phases"]))

    def test_every_phase_of_every_group_names_the_live_rendering_keys(self):
        """A group without the radiance cache, the micromaps or the cut-out tests still sets their keys to 0 in every
        phase: left out, the install's ini would decide, and a player with rtOpacityMicromaps=1 in it would
        measure every group with micromaps."""
        for group in bm.group_list(self.matrix):
            for start in self.starts(group["id"]):
                for p in start["phases"]:
                    s = p["scenario"]
                    expected = {"rtRadianceCache": "1" if s["radiance_cache"] else "0",
                                "rtOpacityMicromaps": "1" if s["opacity_micromaps"] else "0"}
                    if not s.get("tweak"):
                        expected["rtCutoutTest"] = "0"
                    for key, want in expected.items():
                        self.assertEqual(p["keys"].get(key), want, "{} {} {}".format(start["id"], bm.scenario_label(s), key))

    def test_a_key_no_group_has_is_refused(self):
        self.matrix["groups"].append({"id": "typo", "tecniques": ["fsr3"]})
        with self.assertRaises(ValueError) as caught:
            bm.expand(self.matrix, "NVIDIA", self.ALL, ["typo"])
        self.assertIn("tecniques", str(caught.exception))
        # The keys a group has of its own are not unknown.
        self.matrix["groups"][-1] = {"id": "fine", "techniques": ["fsr3"], "tweak_defaults": {"rtCutoutTest": "0"}, "tweaks": [],
                                     "scenes": ["pad"], "variants": [{"id": "v", "proxy_load": {}}], "show": ["ratio"]}
        self.assertTrue(bm.expand(self.matrix, "NVIDIA", self.ALL, ["fine"])[0])

    def test_no_start_has_a_command_line_past_what_windows_takes(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL, ["all"])
        for s in starts:
            self.assertLess(bm.command_length(s), bm.MAX_COMMAND_CHARS, s["id"])
            self.assertEqual(bm.probe_arguments(s)[-1], bm.ini_sets_argument(s["phases"]))
            self.assertEqual(bm.probe_arguments(s)[:3], ["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeClouds"])

    def test_raytracing_modes(self):
        starts = self.starts("rt-modes")
        self.assertEqual([s["id"] for s in starts], ["rt-modes-fsr"])
        phases = starts[0]["phases"]
        self.assertEqual({p["scenario"]["technique"] for p in phases}, {"fsr3", "dlss"})
        self.assertEqual({p["keys"]["mod.raytracing"] for p in phases}, {"Off", "Shadows", "Light", "Full"})
        # Ray Reconstruction on and off for DLSS wherever the raytracing is on; FG off and FSR's, no latency model.
        dlss = [p["scenario"] for p in phases if p["scenario"]["technique"] == "dlss"]
        self.assertEqual({(s["raytracing"], s["ray_reconstruction"]) for s in dlss},
                         {("Off", None)} | {(m, rr) for m in ("Shadows", "Light", "Full") for rr in (True, False)})
        self.assertEqual({p["scenario"]["fg"] for p in phases}, {"off", "fsr"})
        self.assertEqual({p["scenario"]["latency"] for p in phases}, {"none"})
        self.assertIn("FSR 3 · RT Shadows · FG FSR · none", {bm.scenario_label(p["scenario"]) for p in phases})

    def test_frame_generation_steps(self):
        starts = self.starts("frame-steps")
        self.assertEqual([s["id"] for s in starts], ["frame-steps-fsr", "frame-steps-dlssg"])
        fsr, dlssg = starts
        # The package's ceiling raised for the start that sets the multipliers live, to 6x.
        self.assertEqual(dlssg["max_generated_frames"], 5)
        self.assertTrue(dlssg["dlssg_package"])
        self.assertEqual(dlssg["dlssg_ini"], {"Logging": {"FrameTiming": "1"}})
        self.assertEqual(dlssg["mod_settings"]["frameGenerationMultiplier"], "2x")
        steps = [(p["scenario"]["raytracing"], p["scenario"].get("step")) for p in dlssg["phases"]]
        self.assertEqual(sorted(set(steps), key=steps.index),
                         [("Off", s) for s in ("2x", "3x", "4x", "5x", "6x", "Dynamic")]
                         + [("Full", s) for s in ("2x", "3x", "4x", "5x", "6x", "Dynamic")])
        for p in dlssg["phases"]:
            self.assertEqual(p["keys"]["mod.frameGenerationMultiplier"], p["scenario"]["step"])
            self.assertEqual(p["mod_settings"]["frameGenerationMultiplier"], p["scenario"]["step"])
            self.assertEqual(p["keys"]["fgMinimumFps"], "0")
            self.assertEqual(p["settle"], 8 if p["rebuild"] else 4)
        # FSR's start has the rows without frame generation, each phase naming the multiplier as well.
        self.assertEqual([p["scenario"]["fg"] for p in fsr["phases"]][:2], ["off", "fsr"])
        self.assertEqual({p["keys"]["mod.frameGenerationMultiplier"] for p in fsr["phases"]}, {"2x"})
        label = bm.scenario_label(dlssg["phases"][1]["scenario"])
        self.assertEqual(label, "FSR 3 · RT Off · FG DLSS-G 3x · none")
        self.assertEqual(bm.scenario_key(dlssg["phases"][1]["scenario"]), "fsr3/Off/rr-na/fg-dlssg/x-3x/lat-none")
        # The step is not part of the row it is compared with: the same rendering without frame generation.
        self.assertEqual(bm._reference_key(dlssg["phases"][1]["scenario"]), "fsr3/Off/rr-na/fg-off/lat-none")

    def test_the_main_matrix_has_no_steps(self):
        for s in bm.expand(self.matrix, "NVIDIA", self.ALL)[0]:
            for p in s["phases"]:
                self.assertNotIn("mod.frameGenerationMultiplier", p["keys"])

    def test_presets_across_the_four_upscalers(self):
        starts = self.starts("dlssg-presets")
        self.assertEqual([s["id"] for s in starts], ["dlssg-presets-fsr", "dlssg-presets-dlssg-auto", "dlssg-presets-dlssg-c"])
        auto, preset_c = starts[1], starts[2]
        self.assertEqual(auto["dlssg_ini"]["DLSSG"], {"Preset": "Auto"})
        self.assertEqual(preset_c["dlssg_ini"]["DLSSG"], {"Preset": "C"})
        for start in (auto, preset_c):
            self.assertTrue(start["dlssg_package"])
            self.assertEqual({p["scenario"]["fg_kind"] for p in start["phases"]}, {"dlssg"})
            self.assertEqual({p["scenario"]["technique"] for p in start["phases"]}, {"fsr3", "dlss", "amd", "xess"})
            self.assertEqual({p["scenario"]["raytracing"] for p in start["phases"]}, {"Off", "Full"})
            self.assertEqual({p["scenario"]["latency"] for p in start["phases"]}, {"none"})
            self.assertFalse(any(p["scenario"]["radiance_cache"] or p["scenario"]["opacity_micromaps"] for p in start["phases"]))
        self.assertEqual({p["scenario"]["fg"] for p in auto["phases"]}, {"dlssg-auto"})
        self.assertEqual({p["scenario"]["fg"] for p in preset_c["phases"]}, {"dlssg-c"})
        self.assertEqual(bm.scenario_label(preset_c["phases"][0]["scenario"]), "FSR 3 · RT Off · FG DLSS-G preset C · none")
        # The references without frame generation are FSR's start's rows.
        self.assertIn("off", {p["scenario"]["fg"] for p in starts[0]["phases"]})

    def test_latency_brake_targets(self):
        starts = self.starts("latency-brake")
        self.assertEqual([s["id"] for s in starts], ["latency-brake-fsr", "latency-brake-dlssg"])
        for start in starts:
            targets = {p["scenario"]["latency"]: p["keys"].get("latencyBrakeTargetMs") for p in start["phases"]}
            self.assertEqual(targets, {"none": None, "brake": "4", "brake2": "2", "brake1": "1"})
            self.assertEqual({p["keys"]["latencyBrake"] for p in start["phases"] if p["scenario"]["latency"] != "none"}, {"1"})
        self.assertEqual({p["scenario"]["fg"] for p in starts[0]["phases"]}, {"off", "fsr"})
        self.assertEqual({p["scenario"]["fg"] for p in starts[1]["phases"]}, {"dlssg"})
        self.assertEqual({p["scenario"]["raytracing"] for p in starts[1]["phases"]}, {"Off", "Full"})
        self.assertIn("FSR 3 · RT Full · FG DLSS-G · brake 2 ms", {bm.scenario_label(p["scenario"]) for p in starts[1]["phases"]})

    def test_build_variants_are_starts_of_their_own(self):
        starts = self.starts("blas")
        self.assertEqual([s["id"] for s in starts], ["blas-fsr-" + v for v in (
            "defaults", "legacy", "no-batch", "no-pool", "compaction-0", "compaction-1", "budget-20000", "tlas-fast-trace",
            "mega-geometry")])
        defaults = {"rtBlasBatch": "1", "rtBlasPool": "1", "rtBlasBudget": "0", "rtBlasCompaction": "2", "rtTlasFastTrace": "0",
                    "rtMegaGeometry": "0"}
        by_id = {s["variant"]: s for s in starts}
        for variant, changed in (("defaults", {}), ("legacy", {"rtBlasBatch": "0", "rtBlasPool": "0", "rtBlasCompaction": "0"}),
                                 ("compaction-1", {"rtBlasCompaction": "1"}), ("budget-20000", {"rtBlasBudget": "20000"}),
                                 ("tlas-fast-trace", {"rtTlasFastTrace": "1"}), ("mega-geometry", {"rtMegaGeometry": "1"})):
            expected = dict(defaults, **changed)
            self.assertEqual({k: by_id[variant]["proxy_load"][k] for k in defaults}, expected, variant)
        # One rendering, two phases: no frame generation, then FSR's.
        for start in starts:
            self.assertEqual([p["scenario"]["fg"] for p in start["phases"]], ["off", "fsr"])
            self.assertEqual({p["scenario"]["raytracing"] for p in start["phases"]}, {"Full"})
            self.assertFalse(any(p["rebuild"] for p in start["phases"]))
        self.assertEqual(bm.scenario_key(by_id["legacy"]["phases"][0]["scenario"]), "var-legacy/fsr3/Full/rr-na/fg-off/lat-none")
        self.assertEqual(bm.scenario_label(by_id["mega-geometry"]["phases"][1]["scenario"]),
                         "Mega Geometry · FSR 3 · RT Full · FG FSR · none")
        self.assertLess(sum(bm.start_seconds(s) for s in starts) / 60.0, 25)

    def test_the_grass_scene_is_a_dimension(self):
        starts = self.starts("grass")
        self.assertEqual([s["id"] for s in starts], ["grass-fsr-pad", "grass-fsr-grass"])
        pad, grass = starts
        self.assertEqual(pad["scene"], "pad")
        self.assertEqual(pad["probe_args"], [])
        self.assertEqual(grass["scene"], "grass")
        self.assertEqual(grass["probe_args"], ["-redefinitionProbeGrass"])
        self.assertIn("-redefinitionProbeGrass", read_source("src", "Probe", "SceneProbe.cs"))
        self.assertEqual([bm.scenario_key(p["scenario"]) for p in pad["phases"]][:1], ["fsr3/Off/rr-na/fg-off/lat-none"])
        self.assertTrue(bm.scenario_key(grass["phases"][0]["scenario"]).startswith("scene-grass/"))
        self.assertTrue(bm.scenario_label(grass["phases"][0]["scenario"]).startswith("grass · "))
        # The radiance cache, the cut-out tests and the micromaps at raytracing Full on both scenes; the micromaps last.
        for start in starts:
            labels = [bm.scenario_label(p["scenario"]) for p in start["phases"]]
            for part in ("RT Full, radiance cache", "RT Full, cut-out test 1", "RT Full, cut-out test 2", "RT Full, opacity micromaps"):
                self.assertTrue(any(part in l for l in labels), part)
            phases = start["phases"]
            micromaps = [p for p in phases if p["scenario"]["opacity_micromaps"]]
            self.assertEqual(phases[-len(micromaps):], micromaps)
            # Every phase names the cut-out test, and the others' have it at 0.
            self.assertEqual({p["keys"]["rtCutoutTest"] for p in phases if not p["scenario"].get("tweak")}, {"0"})
            self.assertEqual({p["keys"]["rtCutoutTest"] for p in phases if p["scenario"].get("tweak") == "cutout2"}, {"2"})
            self.assertEqual({p["keys"]["rtRadianceCache"] for p in phases if p["scenario"]["radiance_cache"]}, {"1"})
            self.assertEqual({p["keys"]["rtRadianceCache"] for p in phases if not p["scenario"]["radiance_cache"]}, {"0"})
        self.assertEqual({p["scenario"]["raytracing"] for p in pad["phases"]}, {"Off", "Shadows", "Light", "Full"})

    def test_the_grass_scene_needs_parallax(self):
        resources = dict(self.ALL)
        del resources["parallax"]
        starts, skipped = bm.expand(self.matrix, "NVIDIA", resources, ["grass"])
        self.assertEqual([s["id"] for s in starts], ["grass-fsr-pad"])
        self.assertIn(("scene grass", "needs parallax"), skipped)
        # Every other group is the same with or without it.
        self.assertEqual(len(bm.expand(self.matrix, "NVIDIA", resources, ["rt-modes"])[0]), 1)

    def test_a_start_carries_the_line_its_scene_must_leave_in_the_log(self):
        pad, grass = self.starts("grass")
        self.assertIsNone(pad["scene_ready"])
        line = ("[LOG 09:36:33.409] [ReDefinition] Probe: grass scene, ReDefinition grass probe LANDED, 0.4 m over the ground, "
                "121.5 m above the sea, biome Grasslands.")
        self.assertIsNone(bm.scene_unavailable(grass, line + "\n"))
        self.assertIsNone(bm.scene_unavailable(grass, line + "\r\n"))
        # The pad's scene, a pod that is not on the ground, a field that is not grassland: not the scene.
        self.assertIn("unavailable", bm.scene_unavailable(grass, "[LOG 09:36:33.409] [ReDefinition] Probe: the flight is the player's.\n"))
        self.assertIn("unavailable", bm.scene_unavailable(grass, line.replace("LANDED", "FLYING")))
        self.assertIn("unavailable", bm.scene_unavailable(grass, line.replace("Grasslands", "Highlands")))
        reason = bm.scene_unavailable(grass, "[ERR 09:36:21.548] [ReDefinition] Probe: grass scene unavailable: Parallax is not installed.\r\n")
        self.assertEqual(reason, "scene grass unavailable: Parallax is not installed.")
        # The probe prints what the matrix looks for.
        source = read_source("src", "Probe", "SceneProbe.cs")
        self.assertIn('" Probe: grass scene, "', source)
        self.assertGreaterEqual(source.count("Probe: grass scene unavailable: "), 4)

    def test_a_scene_that_was_not_made_has_no_rows(self):
        start = fixture_start()
        start.update(scene="grass", scene_ready=self.matrix["scenes"]["grass"]["ready"])
        result = bm.phases_of_start(start, fixture("bench.KSP.log"), fixture("bench.proxy.log"), 3)
        self.assertEqual(result["phases"], [])
        self.assertEqual(result["problems"], ["scene grass unavailable: the probe's line that says it stands there is not in KSP.log"])
        # The probe says why when it can.
        ksp = fixture("bench.KSP.log") + "[ERR 16:38:56.000] [ReDefinition] Probe: grass scene unavailable: no field of grassland found around the pad.\n"
        result = bm.phases_of_start(start, ksp, fixture("bench.proxy.log"), 3)
        self.assertEqual(result["problems"], ["scene grass unavailable: no field of grassland found around the pad."])
        # With its line, the start is measured as before.
        ksp = fixture("bench.KSP.log") + ("[LOG 16:39:30.000] [ReDefinition] Probe: grass scene, ReDefinition grass probe LANDED, 0.4 m over "
                                          "the ground, 121.5 m above the sea, biome Grasslands.\n")
        result = bm.phases_of_start(start, ksp, fixture("bench.proxy.log"), 3)
        self.assertEqual(len(result["phases"]), 2)
        self.assertEqual(result["problems"], [])

    def test_a_start_without_rows_is_said_in_its_groups_table(self):
        record = GroupDocTest().group_record("2026-10-20-1900", "steps")
        record["starts"].append({"id": "steps-grass", "group": "steps", "phases": [],
                                 "problems": ["scene grass unavailable: Parallax is not installed."]})
        out = bm.render_doc([("2026-10-20-1900.json", record)], "{}\n{}\n{}\n{}\n".format(
            bm.TABLE_START, bm.TABLE_END, bm.RUNS_START, bm.RUNS_END))
        self.assertIn("Not measured, start `steps-grass`: scene grass unavailable: Parallax is not installed.", out)

    def test_parallax_is_found_by_its_plugin(self):
        root = tempfile.mkdtemp()
        try:
            self.assertIsNone(bm.parallax_folder(root))
            # The texture packs are not the plugin.
            os.makedirs(os.path.join(root, "GameData", "Parallax_StockScatterTextures", "Plugins"))
            with open(os.path.join(root, "GameData", "Parallax_StockScatterTextures", "Plugins", "Parallax.dll"), "w"):
                pass
            self.assertIsNone(bm.parallax_folder(root))
            os.makedirs(os.path.join(root, "GameData", "ParallaxContinued", "Plugins"))
            self.assertIsNone(bm.parallax_folder(root))
            with open(os.path.join(root, "GameData", "ParallaxContinued", "Plugins", "ParallaxContinued.dll"), "w"):
                pass
            self.assertEqual(bm.parallax_folder(root), os.path.join(root, "GameData", "ParallaxContinued"))
        finally:
            shutil.rmtree(root)

    def test_a_scene_the_matrix_does_not_define_is_refused(self):
        self.matrix["groups"][-1]["scenes"] = ["moon"]
        with self.assertRaises(ValueError):
            bm.expand(self.matrix, "NVIDIA", self.ALL, ["grass"])

    def test_a_group_may_extend_and_name_entries(self):
        self.matrix["groups"].append({"id": "mine", "techniques": ["fsr3"], "raytracing": ["Off"], "frame_generation": [
            {"extends": "fsr", "name": "FSR (mine)"}], "latency": ["none", {"extends": "brake", "id": "b9", "name": "brake 9 ms"}],
            "radiance_cache": None, "opacity_micromaps": None})
        starts = self.starts("mine")
        self.assertEqual([s["id"] for s in starts], ["mine-fsr"])
        self.assertEqual({p["scenario"]["fg_name"] for p in starts[0]["phases"]}, {"off", "FSR (mine)"})
        self.assertEqual({p["scenario"]["latency"] for p in starts[0]["phases"]}, {"none", "b9"})
        self.matrix["groups"].append({"id": "bad", "techniques": ["nothing"]})
        with self.assertRaises(ValueError):
            bm.expand(self.matrix, "NVIDIA", self.ALL, ["bad"])

    def test_the_plan_names_each_groups_phases_and_minutes(self):
        import contextlib
        import io
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            bm.main(["plan", "--group", "all", "--vendor", "NVIDIA", "--dlssg-package", self.ALL["dlssg_package"],
                     "--amd-dll-dir", "none", "--xess-dll-dir", "none", "--xess-fg-dll-dir", "none"])
        text = out.getvalue()
        self.assertRegex(text, r"(?m)^group +starts +phases +minutes")
        for group in bm.group_list(self.matrix):
            self.assertRegex(text, r"(?m)^{} +\d+ +\d+ +\d+ ".format(re.escape(group["id"])))
        # The groups left out of a plan are named.
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            bm.main(["plan", "--group", "blas", "--vendor", "NVIDIA"])
        self.assertIn("the other groups: main, rt-modes", out.getvalue())

    def test_the_help_names_the_groups(self):
        import contextlib
        import io
        out = io.StringIO()
        with contextlib.redirect_stdout(out), self.assertRaises(SystemExit):
            bm.main(["run", "--help"])
        self.assertIn("--group", out.getvalue())
        self.assertIn("run --group", bm.__doc__)

    def test_the_estimate_of_the_default_run_stays(self):
        starts, _ = bm.expand(self.matrix, "NVIDIA", self.ALL)
        self.assertLess(bm.estimate_seconds(starts) / 60.0, 60)


class StepCheckTest(unittest.TestCase):
    def test_a_step_the_gpu_did_not_deliver_is_named(self):
        scenario = {"step": "3x", "fg_requested": True}
        self.assertTrue(bm._step_check(scenario, 3.0)["delivered"])
        self.assertTrue(bm._step_check(scenario, 2.9)["delivered"])
        check = bm._step_check(scenario, 2.0)
        self.assertFalse(check["delivered"])
        self.assertEqual(check["shown_as"], 2)
        self.assertEqual(check["expected"], 3)
        # Dynamic has no number to be held to; a phase without a step, or without frame generation, none.
        self.assertIsNone(bm._step_check({"step": "Dynamic", "fg_requested": True}, 3.2)["delivered"])
        self.assertIsNone(bm._step_check({"fg_requested": True}, 2.0))
        self.assertIsNone(bm._step_check({"step": "2x", "fg_requested": False}, 1.0))

    def test_the_phase_carries_the_check_and_the_warning(self):
        start = fixture_start()
        start["phases"][1]["scenario"]["step"] = "3x"
        result = bm.phases_of_start(start, fixture("bench.KSP.log"), fixture("bench.proxy.log"), 3)
        on = result["phases"][1]
        self.assertEqual(on["fg_step"]["asked"], "3x")
        self.assertFalse(on["fg_step"]["delivered"])
        self.assertIn("3x asked for, presented/rendered 2.00: the GPU or the driver delivered 2x", on["warnings"])
        start["phases"][1]["scenario"]["step"] = "2x"
        on = bm.phases_of_start(start, fixture("bench.KSP.log"), fixture("bench.proxy.log"), 3)["phases"][1]
        self.assertTrue(on["fg_step"]["delivered"])
        self.assertEqual(on["warnings"], [])

    def test_a_step_that_is_over_delivered_is_named_as_well(self):
        # 2x asked for and 3x shown: the multiplier did not reach the proxy (a stale Dynamic, a step of the row before).
        check = bm._step_check({"step": "2x", "fg_requested": True}, 3.0)
        self.assertFalse(check["delivered"])
        self.assertEqual(check["shown_as"], 3)
        self.assertTrue(bm._step_check({"step": "2x", "fg_requested": True}, 2.2)["delivered"])
        self.assertFalse(bm._step_check({"step": "2x", "fg_requested": True}, 2.3)["delivered"])
        # The whole number nearest is rounded half up, not to the even one.
        self.assertEqual(bm._step_check({"step": "6x", "fg_requested": True}, 2.5)["shown_as"], 3)
        self.assertEqual(bm._step_check({"step": "6x", "fg_requested": True}, 4.5)["shown_as"], 5)

    def test_a_repeated_phase_is_judged_on_the_merged_window(self):
        """The first pass of a rendering's first phase has the frame generation coming up (1.7 shown per rendered frame);
        the phase is both passes, 1.85, and its step check, its notes and the frame generation that ran are of that."""
        ksp = "".join("[LOG {}] [ReDefinition] {}\n".format(t, text) for t, text in (
            ("12:00:00.000", "Upscaler running on 'Camera 00': 1707x960 -> 2560x1440 (Quality, AMD FSR (DLL))"),
            ("12:00:10.000", "Probe: ReDefinitionProxy.ini set to fgMinimumFps=0."),
            ("12:00:22.000", "Probe: shot bench-ini0 with fgMinimumFps=0."),
            ("12:00:22.100", "Probe: ReDefinitionProxy.ini set to fgMinimumFps=0."),
            ("12:00:34.000", "Probe: shot bench-ini1 with fgMinimumFps=0.")))
        lines = ["11:59:59.000  Config: enabled=1 reportSeconds=1 fgMinimumFps=0",
                 "11:59:59.500  Frame generation context created for 2560x1440, depthInverted=1",
                 "12:00:10.500  Config reloaded: enabled=1 reportSeconds=1 fgMinimumFps=0",
                 "12:00:22.600  Config reloaded: enabled=1 reportSeconds=1 fgMinimumFps=0"]
        for second in list(range(13, 23)) + list(range(25, 35)):
            ratio = "1.00" if second < 16 else "2.00"
            lines.append("12:00:{:02d}.000  D3D12 proxy: 100 frames, mean 10.00 ms (100.0 fps), p50 10.00, p95 12.00, p99 14.00,"
                         " max 20.00, presented/rendered {}, displayed 100/s at 120 Hz, sync 0".format(second, ratio))
        scenario_on = dict(scenario("fsr", True, "none"), step="2x")
        phase = {"scenario": scenario_on, "keys": {"fgMinimumFps": "0"}, "settle": 1}
        start = {"id": "s", "phases": [dict(phase), dict(phase)]}
        # The window's first pass alone: 1.7, a step not delivered.
        alone = bm.phases_of_start({"id": "s", "phases": [dict(phase)]}, ksp, "\n".join(lines) + "\n", 3)["phases"][0]
        self.assertFalse(alone["fg_step"]["delivered"])
        self.assertAlmostEqual(alone["measured"]["presented_per_rendered"], 1.7)
        merged = bm.phases_of_start(start, ksp, "\n".join(lines) + "\n", 3)["phases"]
        self.assertEqual(len(merged), 1)
        phase = merged[0]
        self.assertEqual(phase["repeats"], 2)
        self.assertAlmostEqual(phase["measured"]["presented_per_rendered"], 1.85)
        self.assertEqual(phase["fg_step"]["shown"], phase["measured"]["presented_per_rendered"])
        self.assertTrue(phase["fg_step"]["delivered"])
        self.assertEqual(phase["warnings"], [])
        self.assertEqual(phase["fg_kind_ran"], "FSR")
        self.assertNotIn("_ratio_warnings", phase)
        # The same phase asked for 3x: the merged ratio decides the note, once.
        start["phases"][0]["scenario"] = start["phases"][1]["scenario"] = dict(scenario_on, step="3x")
        phase = bm.phases_of_start(start, ksp, "\n".join(lines) + "\n", 3)["phases"][0]
        self.assertEqual(phase["warnings"], ["3x asked for, presented/rendered 1.85: the GPU or the driver delivered 2x"])

    def test_the_multiplier_lines_of_the_phase_are_kept(self):
        proxy = fixture("bench.proxy.log")
        ksp = fixture("bench.KSP.log")
        log = bm.parse_proxy_log("12:00:00.000  Frame generation multiplier requested: 3x\n")
        self.assertEqual(log["lines"]["fg_multiplier"][0]["text"], "Frame generation multiplier requested: 3x")
        self.assertEqual(bm.parse_ksp_log("[LOG 12:00:00.000] [ReDefinition] Frame generation multiplier 3x\n")["multiplier"][0]["choice"], "3x")
        on = bm.phases_of_start(fixture_start(), ksp, proxy, 3)["phases"][1]
        self.assertIn("fg_lines", on)


class OptionsRecordTest(unittest.TestCase):
    def test_composite_values_have_their_parts(self):
        values = bm.parse_config("latencyBrake=1/4.000000/1/dlssg dlssgMode=on/3f lowLatency=0")
        self.assertEqual(values["latencyBrake"], "1/4.000000/1/dlssg")
        self.assertEqual(values["latencyBrakeTargetMs"], "4.000000")
        self.assertEqual(values["latencyBrakeWaits"], "1")
        self.assertEqual(values["latencyBrakeWithDlssg"], "1")
        self.assertEqual(values["dlssgFrames"], "3")
        values = bm.parse_config("latencyBrake=0/2.000000/1 dlssgMode=dynamic")
        self.assertEqual(values["latencyBrakeWithDlssg"], "0")
        self.assertEqual(values["dlssgFrames"], "0")

    def test_the_proxys_config_line_holds_every_key_it_reads(self):
        """Every ini key Config.cpp reads is in the line the record keeps: the options of a phase are the line's."""
        source = read_source("src", "DxgiProxy", "Config.cpp")
        read = set(re.findall(r'(?:ReadFlag|ReadFloat|GetPrivateProfileIntW|GetPrivateProfileStringW)\(\s*(?:L"Proxy",\s*)?L"(\w+)"', source))
        described = source[source.index("std::string Describe"):source.index("void ReadInto")]
        named = set(re.findall(r"(\w+)=", described))
        # The keys the line writes together with another's value: dlssgFrames with dlssgMode, the brake's parts with
        # latencyBrake, the pacing's with pacing (parse_config reads them back as keys of their own or as the value).
        together = {"dlssgFrames", "latencyBrakeTargetMs", "latencyBrakeWaits", "latencyBrakeWithDlssg", "fgPacingSafetyMarginMs",
                    "fgPacingVarianceFactor", "fgPacingHybridSpin", "fgPacingHybridSpinTime", "fgPacingWaitOnFence"}
        self.assertEqual(sorted(read - named - together - {"Proxy"}), [])
        for key in ("rtRadianceCache", "rtRadianceCacheEntries", "rtOpacityMicromaps", "rtCutoutTest", "rtBlasBatch", "rtBlasPool",
                    "rtBlasBudget", "rtBlasCompaction", "rtTlasFastTrace", "rtMegaGeometry", "xessFgFrames", "xell", "dlssgMode",
                    "latencyBrake", "frameGenerationTechnique"):
            self.assertIn(key, named)
        # The together-keys are read back from the line's values.
        values = bm.parse_config("latencyBrake=1/4.000000/1/dlssg dlssgMode=on/2f")
        for key in ("latencyBrakeTargetMs", "latencyBrakeWaits", "latencyBrakeWithDlssg", "dlssgFrames"):
            self.assertIn(key, values)

    def test_the_mods_multiplier_is_a_setting_the_probe_sets_by_key(self):
        settings = read_source("src", "OwnSettings.cs")
        self.assertIn('Key = "frameGenerationMultiplier"', settings)
        probe = read_source("src", "Probe", "SceneProbe.cs")
        self.assertIn('key.StartsWith("mod."', probe)
        self.assertIn("wanted.SetValue(modKey, text)", probe)

    def test_the_package_ini_is_read_by_section(self):
        text = "[FrameGeneration]\r\n; comment\r\nMaxGeneratedFrames=5\r\n\r\n[DLSSG]\r\nPreset = C\r\nOptimized=2\r\n"
        self.assertEqual(bm.parse_ini_sections(text), {"FrameGeneration": {"MaxGeneratedFrames": "5"},
                                                       "DLSSG": {"Preset": "C", "Optimized": "2"}})

    def test_a_record_names_the_package_ini_and_the_scene_of_each_start(self):
        raw = tempfile.mkdtemp()
        try:
            matrix = bm.load_matrix(bm.DEFAULT_MATRIX)
            starts, _ = bm.expand(matrix, "NVIDIA", {"dlssg_package": "p"}, ["frame-steps"])
            start = [s for s in starts if s["dlssg_package"]][0]
            plan = {"matrix": matrix, "matrix_file": "m.json", "groups": ["frame-steps"], "starts": [start], "stamp": "s",
                    "settle_seconds": 2}
            with open(os.path.join(raw, "plan.json"), "w", encoding="utf-8") as f:
                import json
                json.dump(plan, f)
            base = os.path.join(raw, start["id"])
            for name, text in ((".proxy.log", fixture("bench.proxy.log")), (".KSP.log", fixture("bench.KSP.log")),
                               (".settings.cfg", "ReDefinition\n{\n\tfoo = 1\n}\n"), (".proxy.ini", "[Proxy]\nenabled=1\n"),
                               (".dlssg_sm86.ini", "[FrameGeneration]\nMaxGeneratedFrames=5\n[DLSSG]\nPreset=Auto\n")):
                with open(base + name, "w", encoding="utf-8") as f:
                    f.write(text)
            record = bm.build_record(raw)
            self.assertEqual(record["groups"], ["frame-steps"])
            entry = record["starts"][0]
            self.assertEqual(entry["group"], "frame-steps")
            self.assertEqual(entry["scene"], "pad")
            self.assertEqual(entry["package_ini"]["FrameGeneration"]["MaxGeneratedFrames"], "5")
            self.assertEqual(entry["proxy_ini_at_start"], {"enabled": "1"})
            for p in entry["phases"]:
                self.assertEqual(p["package_ini"]["DLSSG"]["Preset"], "Auto")
                self.assertEqual(p["mod_settings"]["foo"], "1")
                self.assertIn("frameGenerationMultiplier", p["mod_settings"])
        finally:
            shutil.rmtree(raw)


class GroupDocTest(DocTest):
    def group_record(self, stamp, group, fps_scale=1.0):
        record = self.record(stamp, fps_scale)
        record["groups"] = [group]
        record["matrix"]["groups"] = [{"id": group, "name": "Steps", "description": "The steps, one after the other.",
                                       "show": ["ratio"], "scenes": ["pad"]}]
        record["matrix"]["scenes"] = {"pad": {"name": "the pad"}}
        for p in record["starts"][0]["phases"]:
            p["scenario"] = dict(p["scenario"], group=group)
        step = {"asked": "3x", "expected": 3, "shown": 2.0, "shown_as": 2, "delivered": False}
        record["starts"][0]["phases"][1]["fg_step"] = step
        return record

    def test_one_table_a_group_with_its_own_latest_run(self):
        doc = "head\n{}\nold\n{}\nmiddle\n{}\nold\n{}\ntail\n".format(bm.TABLE_START, bm.TABLE_END, bm.RUNS_START, bm.RUNS_END)
        out = bm.render_doc([("2026-10-19-1800.json", self.group_record("2026-10-19-1800", "steps", 0.5)),
                             ("2026-10-20-1800.json", self.record("2026-10-20-1800")),
                             ("2026-10-20-1900.json", self.group_record("2026-10-20-1900", "steps"))], doc)
        main, steps = out.index("### Main matrix (`main`)"), out.index("### Steps (`steps`)")
        self.assertLess(main, steps)
        # The main table is the main run's, the group's the later one's; each says which run it is.
        self.assertEqual(out[main:steps].count("Run `2026-10-20-1800`."), 1)
        self.assertEqual(out[steps:out.index(bm.TABLE_END)].count("Run `2026-10-20-1900`."), 1)
        self.assertIn("The steps, one after the other.", out)
        self.assertIn("python tools/benchmark_matrix.py run --group steps", out)
        # The shown-per-rendered and the delivered columns for a group that asks for them; the previous run of the group
        # is the one before it in the group, not the main run between them.
        self.assertIn("| Scenario | fps | Shown fps | Shown per rendered | Delivered |", out)
        self.assertIn("not delivered", out)
        self.assertIn("+100.0 %", out[steps:])
        self.assertIn("| Groups | `steps` |", out)

    def test_the_rt_build_columns(self):
        record = self.group_record("2026-10-20-1900", "steps")
        record["matrix"]["groups"][0]["show"] = ["rt_builds"]
        record["starts"][0]["rt_builds"] = {"frames": 3, "meshes": 668, "triangles": 236309, "slowest_gpu_ms": 1.25952,
                                            "slowest_cpu_ms": 5.318}
        table = bm.summary_table(record, None, "steps")
        self.assertIn("| Slowest build GPU ms | Slowest build CPU ms | Meshes | Triangles |", table)
        self.assertIn("| 1.26 | 5.32 | 668 | 236309 |", table)

    def test_a_scene_compares_with_its_own_baseline(self):
        record = self.group_record("s", "steps")
        record["starts"][0]["phases"][1]["scenario"]["scene"] = "grass"
        record["starts"][0]["phases"][1]["key"] = "scene-grass/amd/Off/rr-na/fg-fsr/lat-reflex+brake"
        key = bm._baseline_key(record, "steps", record["starts"][0]["phases"][1]["scenario"])
        self.assertEqual(key, "scene-grass/amd/Off/rr-na/fg-off/lat-none")


if __name__ == "__main__":
    unittest.main()
