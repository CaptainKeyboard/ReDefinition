"""The log parser and the matrix of tools/benchmark_matrix.py, on excerpts of the logs a
probe start writes (tools/tests/fixtures).

    python -m unittest
"""
import copy
import os
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
                                  "rtOpacityMicromaps=0")
        for s in sets:
            for key in ("reportSeconds=1", "mod.technique=", "mod.raytracing=", "mod.rayReconstruction=", "fgMinimumFps=",
                        "lowLatency=", "latencyBrake=", "rtRadianceCache=", "rtOpacityMicromaps="):
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


if __name__ == "__main__":
    unittest.main()
