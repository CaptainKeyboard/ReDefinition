"""Unit tests of tools/vanilla_ground.py on a fake KSP folder: what is moved, that the install is byte for byte the same after every kind of run (a good one, a
failed one, a crash of the launcher, a crash of the script itself followed by `restore`), the hash check of the restore, the refusals, and that ProbeLite
references nothing of ReDefinition. No real KSP.

    python -m unittest tools.tests.test_vanilla_ground
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
REPO = os.path.dirname(TOOLS)
sys.path.insert(0, TOOLS)
import vanilla_ground as vg  # noqa: E402

SETTINGS = ("GAMESETTINGS\n{\n\tSCREEN_RESOLUTION_WIDTH = 1920\n\tSCREEN_RESOLUTION_HEIGHT = 1080\n\tFULLSCREEN = True\n\tUI_SCALE = 1\n\tSYNC_VBL = 1\n"
            "\tFRAMERATE_LIMIT = 120\nTEXTURE_QUALITY = 0\nLIGHT_QUALITY = 8\nTERRAIN_SHADER_QUALITY = 3\n}\n")
BUNDLED = ("ReDefinition\n{\n\tBEFORE_REDEFINITION\n\t{\n\t\tksp.TEXTURE_QUALITY = 1\n\t\tksp.TERRAIN_SHADER_QUALITY = 0\n\t\ttufx.profileMainMenu = Default-MainMenu\n"
           "\t\tksp.LIGHT_QUALITY = 8\n\t\tksp.FRAMERATE_LIMIT = 120\n\t\tparallax.maxTessellation = 64\n\t\tparallax.noSuchKey = 1\n\t\tscatterer.m_fourierGridSize = 256\n"
           "\t\tdistantobject.renderVessels = False\n\t\tdistantobject.renderMode = RenderTargetOnly\n\t\tdeferred.useHalfResolutionScreenSpaceReflections = True\n"
           "\t\tSAVE\n\t\t{\n\t\t\tname = ReDefinitionProbe\n\t\t\ttufx.profileFlight = Default-Flight\n\t\t}\n\t\tSAVE\n\t\t{\n\t\t\tname = Other\n\t\t\ttufx.profileFlight = X\n\t\t}\n\t}\n}\n")
SFS = "SCENARIO\n{\n\tname = TUFX\n\t\t\tFlightSceneProfile = Blackrack_TUFX\n\t\t\tIVAProfile = Blackrack_TUFX\n}\n"
MAIN = "ReDefinition\n{\n\traytracing = Light\n\tframeGeneration = True\n\tqualityAuto = True\n\tquality = Quality\n}\n"


def write(path, text, binary=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb" if binary else "w", encoding=None if binary else "utf-8", newline=None if binary else "") as f:
        f.write(text)


def manifest(root, skip=("KSP.log",)):
    """{relative path: sha256} of every file in a tree."""
    out = {}
    for r, _d, names in os.walk(root):
        for n in names:
            if n in skip:
                continue
            p = os.path.join(r, n)
            with open(p, "rb") as f:
                out[os.path.relpath(p, root)] = hashlib.sha256(f.read()).hexdigest()
    return out


def make_ksp(root):
    ksp = os.path.join(root, "ksp")
    for rel, text in {
        "KSP_x64.exe": "exe", "settings.cfg": SETTINGS, "UnityPlayer.dll": "u", "UnityPlayer.dll.release": "u", "PartDatabase.cfg": "parts",
        "dxgi.dll": "proxy", "version.dll": "ampere", "dlssg_sm86.ini": "ini", "ReDefinitionProxy.ini": "i", "ReDefinitionProxy.log": "l",
        "ReDefinitionProxy.defaults": "d", "sl.common.dll": "sl", "sl.interposer.dll": "sl2", "nvngx_dlss.dll": "ngx", "amd_fidelityfx_framegeneration_dx12.dll": "amd",
        "dlssg_sm86/runtime/a.bin": "x", "dlssg_sm86/logs/l.txt": "y",
        "GameData/ReDefinition/Main/PluginData/bundled.cfg": BUNDLED, "GameData/KSPCommunityFixes/PluginData/TextureCache/textureData.json": "index",
        "GameData/Kopernicus/Cache/k.bin": "k", "GameData/ReDefinition/Main/PluginData/settings.cfg": MAIN,"GameData/ReDefinition/Main/Plugins/ReDefinition.dll": "dll",
        "GameData/ReDefinition/Atmospheres/PluginData/settings.cfg": "atmo", "GameData/ReDefinition/Atmospheres/PluginData/cache.bin": "c",
        "GameData/EnvironmentalVisualEnhancements/a.cfg": "eve", "GameData/StockVolumetricClouds/b.cfg": "svc", "GameData/Squad/x.cfg": "squad",
        "GameData/ModuleManager.ConfigCache": "cache", "GameData/ModuleManager.ConfigSHA": "sha", "GameData/ModuleManager.Physics": "ph", "GameData/ModuleManager.TechTree": "tt",
        "GameData/BetterTimeWarp/PluginData/BetterTimeWarp.cfg": "old",
        "GameData/ParallaxContinued/Config/ParallaxGlobalSettings.cfg": "terrain\n{\n\t\tmaxTessellation = 32\n}\n",
        "GameData/scatterer/config/config.cfg": "\tm_fourierGridSize = 128\r\n\tother = 1\r\n", "GameData/DistantObject/PluginData/Settings.cfg": "\trenderVessels = True\n\trenderMode = 0\n",
        "GameData/zzz_Deferred/Deferred.cfg": "        useHalfResolutionScreenSpaceReflections = False\n",
        "GameData/ReDefinition/Main/Patches/P.cfg": "@PART[x]:FOR[ReDefinition]\n{\n}\n", "GameData/ReDefinition/Atmospheres/Audio/RSE.cfg": "@RSE_SETTINGS:NEEDS[RocketSoundEnhancement]\n{\n}\n",
        "GameData/ReDefinition/Atmospheres/Bodies/K.cfg": "@REDEFINITION_BODY[Kerbin]\n{\n}\n", "GameData/ReDefinition/Atmospheres/Clouds/E.cfg": "EVE_CLOUDS:NEEDS[X]\n{\n}\n",
        "GameData/ReDefinition/Atmospheres/Plugins/ReDefinition.Atmospheres.dll": "adll",
        "PluginData/DistantObject/x.cfg": "do", "saves/ReDefinitionProbe/probe.sfs": SFS, "saves/ReDefinitionProbe/persistent.sfs": SFS,
        "saves/ReDefinitionProbe/persistent.loadmeta": "meta", "saves/ReDefinitionProbe/Backup/b1.sfs": "b1", "saves/ReDefinitionProbe/Parsek/Recordings/r.txt": "rec",
        "saves/ReDefinitionProbe/Ships/VAB/a.craft": "craft",
    }.items():
        write(os.path.join(ksp, *rel.split("/")), text)
    return ksp


class FakeVm:
    def __init__(self):
        self.calls = []

    def begin(self):
        self.calls.append("begin")

    def end(self):
        self.calls.append("end")


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="vg-test-")
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.ksp = make_ksp(self.tmp)
        self.main = os.path.join(self.tmp, "main")
        write(os.path.join(self.main, ".local", "work", "profile-cache", "GameData", "BetterTimeWarp", "PluginData", "BetterTimeWarp.cfg"), "medium")
        write(os.path.join(self.main, ".local", "work", "profile-cache", "GameData", "ReDefinition", "Main", "PluginData", "profile.cfg"), "ours-profile")
        self.dll = os.path.join(self.tmp, "ProbeLite.dll")
        write(self.dll, b"MZ-fake", binary=True)
        self.before = manifest(self.ksp)
        self.vm = FakeVm()
        self.seen = {}

    def run_variant(self, variant, game=None, running=lambda: False, free=lambda: 40.0, **kw):
        # (kw: timeout_min, keep_eve, hover_m, flags, extra_args, use_profile)
        def launch(args, timeout):
            self.seen["args"] = args
            self.seen["timeout"] = timeout
            return (game or self.good_game(variant))(args, timeout)
        hooks = {"running": running, "free_gb": free, "build": lambda: self.dll, "launch": launch, "lock": False, "vm": self.vm,
                 "profile_cache": os.path.join(self.main, ".local", "work", "profile-cache")}
        run = vg.Run(self.ksp, variant, repo=REPO, mainrepo=self.main, hooks=hooks, log=lambda m: None, **kw)
        return run.execute()

    def good_game(self, variant, done="ok\nwarnings=0\n6 flight pictures\n"):
        def play(args, timeout):
            ksp = self.ksp
            self.seen["dxgi"] = os.path.exists(os.path.join(ksp, "dxgi.dll"))
            self.seen["redef"] = os.path.exists(os.path.join(ksp, "GameData", "ReDefinition"))
            self.seen["dlssg"] = os.path.exists(os.path.join(ksp, "dlssg_sm86"))
            self.seen["eve"] = os.path.exists(os.path.join(ksp, "GameData", "EnvironmentalVisualEnhancements"))
            self.seen["lite"] = os.path.exists(os.path.join(ksp, "GameData", "ProbeLite", "ProbeLite.dll"))
            with open(os.path.join(ksp, "settings.cfg"), "r", encoding="utf-8") as f:
                self.seen["settings"] = f.read()
            with open(os.path.join(ksp, "GameData", "ProbeLite", "PluginData", "config.txt"), "r", encoding="utf-8") as f:
                self.seen["config"] = f.read()
            # What the game does to the install while it runs.
            write(os.path.join(ksp, "settings.cfg"), "rewritten by the game")
            write(os.path.join(ksp, "GameData", "ModuleManager.ConfigCache"), "new cache")
            write(os.path.join(ksp, "GameData", "ModuleManager.ConfigSHA"), "new sha")
            write(os.path.join(ksp, "saves", "ReDefinitionProbe", "persistent.sfs"), "autosaved")
            write(os.path.join(ksp, "saves", "ReDefinitionProbe", "Backup", "b2.sfs"), "new backup")
            write(os.path.join(ksp, "PluginData", "New", "n.cfg"), "n")
            write(os.path.join(ksp, "GameData", "KSPCommunityFixes", "PluginData", "TextureCache", "textureData.json"), "rewritten index")
            write(os.path.join(ksp, "GameData", "Kopernicus", "Cache", "k.bin"), "rewritten")
            write(os.path.join(ksp, "KSP.log"), "log")
            data = os.path.join(ksp, "GameData", "ProbeLite", "PluginData")
            write(os.path.join(data, "shots", "far_p1.png"), "png")
            write(os.path.join(data, "shots", "results.tsv"),
                  "name\tscene\theading\televation\tcam_agl_pqs_m\tcam_over_collider_m\tlooks_at_object\tlooks_at_material\tlooks_at_shader\tunder_object\tsun_elev_deg\t"
                  "all_r\tall_g\tall_b\tlow_r\tlow_g\tlow_b\tlow_green\tsettle_s\tsettle\tframes\tfile\n"
                  "far_p1\tflight\t90\t5\t291.28\t-\tPQS\tmat\tParallax/Terrain\tPQS\t45\t1\t2\t3\t100\t110\t90\t15\t5\tstable\t200\tfar_p1.png\n")
            loaded = "mscorlib 4.0\nAssembly-CSharp 1.0\n"
            if os.path.exists(os.path.join(ksp, "GameData", "ReDefinition", "Main", "Plugins", "ReDefinition.dll")):
                loaded += "ReDefinition 0.1\n"
            if os.path.exists(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres", "Plugins", "ReDefinition.Atmospheres.dll")):
                loaded += "ReDefinition.Atmospheres 0.1\n"
            write(os.path.join(data, "assemblies.txt"), loaded)
            def slurp(rel):
                with open(os.path.join(ksp, *rel.split("/")), "r", encoding="utf-8", newline="") as fh:
                    return fh.read()
            self.seen["files"] = {k: slurp(k) for k in (
                "GameData/ParallaxContinued/Config/ParallaxGlobalSettings.cfg", "GameData/scatterer/config/config.cfg", "GameData/DistantObject/PluginData/Settings.cfg",
                "GameData/zzz_Deferred/Deferred.cfg", "saves/ReDefinitionProbe/probe.sfs")}
            self.seen["patches"] = [n for n in ("Main/Patches/P.cfg", "Atmospheres/Audio/RSE.cfg", "Atmospheres/Bodies/K.cfg", "Atmospheres/Clouds/E.cfg")
                                    if os.path.exists(os.path.join(ksp, "GameData", "ReDefinition", *n.split("/")))]
            self.seen["main"] = os.path.exists(os.path.join(ksp, "GameData", "ReDefinition", "Main"))
            self.seen["atmo"] = os.path.exists(os.path.join(ksp, "GameData", "ReDefinition", "Atmospheres"))
            write(os.path.join(data, "done"), done)
            return "quit", 0
        return play


class AsideTests(Base):
    def test_vanilla_lists_everything_of_ours_and_eve(self):
        names = [rel for rel, _k in vg.aside_items(self.ksp, "vanilla")]
        for want in ("dxgi.dll", "version.dll", "dlssg_sm86.ini", "ReDefinitionProxy.ini", "ReDefinitionProxy.log", "ReDefinitionProxy.defaults", "sl.common.dll",
                     "sl.interposer.dll", "nvngx_dlss.dll", "amd_fidelityfx_framegeneration_dx12.dll", "GameData/ReDefinition", "dlssg_sm86",
                     "GameData/EnvironmentalVisualEnhancements", "GameData/StockVolumetricClouds"):
            self.assertIn(want, names)
        self.assertNotIn("UnityPlayer.dll", names)
        self.assertNotIn("GameData/Squad", names)

    def test_ours_moves_only_eve_and_keep_eve_nothing(self):
        self.assertEqual([r for r, _k in vg.aside_items(self.ksp, "ours")], ["GameData/EnvironmentalVisualEnhancements", "GameData/StockVolumetricClouds"])
        self.assertEqual(vg.aside_items(self.ksp, "ours", keep_eve=True), [])

    def test_pins_equal_the_series_k_s2(self):
        path = os.path.join(REPO, "tests", "series", "K-S2.json")
        if not os.path.isfile(path):
            self.skipTest("no K-S2.json in this checkout")
        with open(path, "r", encoding="utf-8") as f:
            defn = json.load(f)
        start = [s for s in defn["starts"] if s["id"] == "ground_on"][0]
        pins = {}
        for entry in defn["settings"] + start["settings"]:
            pins.setdefault(entry["file"], {}).update(entry["set"])
        self.assertEqual(pins["settings.cfg"], vg.PINS_KSP)
        self.assertEqual(pins[vg.MAIN_SETTINGS], vg.PINS_MAIN)
        args = start["probeArgs"]
        for a in ("-screen-width", "2560", "-screen-height", "1440", "-screen-fullscreen", "0"):
            self.assertIn(a, args)
        self.assertEqual(vg.WINDOW_ARGS[0], "-popupwindow")


class RunTests(Base):
    def test_vanilla_run_hides_everything_and_leaves_the_install_as_it_was(self):
        result = self.run_variant("vanilla")
        self.assertTrue(result["ok"], result)
        self.assertEqual((self.seen["dxgi"], self.seen["redef"], self.seen["dlssg"], self.seen["eve"], self.seen["lite"]), (False, False, False, False, True))
        self.assertIn("UI_SCALE = 1.5", self.seen["settings"])
        self.assertIn("FULLSCREEN = False", self.seen["settings"])
        self.assertIn("variant=vanilla", self.seen["config"])
        self.assertEqual(self.seen["args"], vg.WINDOW_ARGS)
        self.assertEqual(manifest(self.ksp), self.before)
        self.assertFalse(os.path.exists(os.path.join(self.ksp, vg.POINTER)))
        self.assertFalse(os.path.exists(os.path.join(self.ksp, "GameData", "ProbeLite")))
        self.assertFalse(os.path.exists(os.path.join(self.ksp, "_atmo_disabled")))
        self.assertEqual(self.vm.calls, ["begin", "end"])

    def test_the_profile_is_not_applied_into_the_moved_folder_for_vanilla(self):
        self.run_variant("vanilla")
        # the file of the profile that lives under GameData/ReDefinition was skipped; had it been written, the restore would have deleted it, but the run's view shows it:
        self.assertFalse(os.path.exists(os.path.join(self.ksp, "GameData", "ReDefinition", "Main", "PluginData", "profile.cfg")))

    def test_ours_run_keeps_redefinition_and_pins_the_series_settings(self):
        seen_main = {}
        base = self.good_game("ours")

        def game(args, timeout):
            with open(os.path.join(self.ksp, "GameData", "ReDefinition", "Main", "PluginData", "settings.cfg"), "r", encoding="utf-8") as f:
                seen_main["text"] = f.read()
            seen_main["profile"] = os.path.exists(os.path.join(self.ksp, "GameData", "ReDefinition", "Main", "PluginData", "profile.cfg"))
            seen_main["atmo_side"] = os.path.exists(os.path.join(self.ksp, "GameData", "ReDefinition", "Atmospheres", "PluginData", "cache.bin"))
            return base(args, timeout)
        result = self.run_variant("ours", game=game)
        self.assertTrue(result["ok"], result)
        self.assertTrue(self.seen["redef"])
        self.assertTrue(self.seen["dxgi"])
        self.assertFalse(self.seen["eve"])
        self.assertIn("raytracing = Off", seen_main["text"])
        self.assertIn("frameGeneration = False", seen_main["text"])
        self.assertTrue(seen_main["profile"])
        self.assertFalse(seen_main["atmo_side"])
        self.assertEqual(manifest(self.ksp), self.before)

    def test_vanilla_original_pins_the_settings_redefinition_remembered(self):
        result = self.run_variant("vanilla-original")
        self.assertTrue(result["ok"], result)
        self.assertIn("TERRAIN_SHADER_QUALITY = 0", self.seen["settings"])
        self.assertIn("TEXTURE_QUALITY = 1", self.seen["settings"])
        self.assertIn("LIGHT_QUALITY = 8", self.seen["settings"])
        self.assertIn("UI_SCALE = 1.5", self.seen["settings"])
        self.assertFalse(self.seen["redef"])
        self.assertEqual(manifest(self.ksp), self.before)

    def test_plain_vanilla_keeps_the_values_redefinition_wrote_and_says_so(self):
        self.run_variant("vanilla")
        self.assertIn("TERRAIN_SHADER_QUALITY = 3", self.seen["settings"])
        plan = vg.build_plan(self.ksp, "vanilla", os.path.join(self.main, ".local", "work", "profile-cache"), True)
        self.assertTrue(any("TERRAIN_SHADER_QUALITY 3" in n and "originally 0" in n for n in plan["notes"]), plan["notes"])

    def test_original_settings_are_read_from_the_before_block_only(self):
        self.assertEqual(vg.original_ksp_settings(self.ksp), {"TEXTURE_QUALITY": "1", "TERRAIN_SHADER_QUALITY": "0", "LIGHT_QUALITY": "8"})

    def test_the_vm_is_stopped_before_the_memory_is_checked(self):
        order = []
        self.vm.begin = lambda: order.append("vm")
        with self.assertRaises(vg.Refusal):
            self.run_variant("vanilla", free=lambda: order.append("memory") or 5.0)
        self.assertEqual(order, ["vm", "memory"])

    def test_warnings_of_the_plugin_are_counted(self):
        result = self.run_variant("vanilla", game=self.good_game("vanilla", done="ok\nwarnings=3\n6 pictures\n"))
        self.assertTrue(result["ok"])
        self.assertEqual(result["warnings"], 3)
        self.assertEqual(vg.done_warnings("failed\nwarnings=0\nx"), 0)
        self.assertEqual(vg.done_warnings(None), 0)

    def test_an_unfinished_series_modset_blocks_the_run(self):
        write(os.path.join(self.ksp, "_atmo_disabled", "journal.json"), "[]")
        with self.assertRaises(vg.Refusal):
            self.run_variant("vanilla")
        shutil.rmtree(os.path.join(self.ksp, "_atmo_disabled"))

    def test_keep_eve_leaves_it_in(self):
        self.run_variant("ours", keep_eve=True)
        self.assertTrue(self.seen["eve"])
        self.assertEqual(manifest(self.ksp), self.before)

    def test_pictures_and_logs_are_collected(self):
        result = self.run_variant("vanilla")
        folder = result["pictures"]
        self.assertTrue(folder.endswith(os.path.join("VANILLA", "vanilla")))
        for rel in (os.path.join("shots", "far_p1.png"), os.path.join("shots", "results.tsv"), "assemblies.txt", "done", "KSP.log"):
            self.assertTrue(os.path.isfile(os.path.join(folder, rel)), rel)
        text = vg.summarize(folder)
        self.assertIn("far_p1", text)
        self.assertIn("named ReDefinition*: none", text)

    def test_a_failed_run_is_reported_and_restored(self):
        result = self.run_variant("vanilla", game=self.good_game("vanilla", done="failed\nwarnings=0\nno save\n"))
        self.assertFalse(result["ok"])
        self.assertEqual(manifest(self.ksp), self.before)

    def test_no_done_file_is_a_failure(self):
        def game(args, timeout):
            return "quit", 0
        result = self.run_variant("vanilla", game=game)
        self.assertFalse(result["ok"])
        self.assertIsNone(result["done"])
        self.assertEqual(manifest(self.ksp), self.before)

    def test_a_timeout_is_a_failure_and_restored(self):
        base = self.good_game("vanilla")

        def game(args, timeout):
            base(args, timeout)
            return "timeout after 5 s", None
        result = self.run_variant("vanilla", game=game)
        self.assertFalse(result["ok"])
        self.assertEqual(manifest(self.ksp), self.before)

    def test_an_exception_in_the_launcher_still_restores(self):
        def game(args, timeout):
            write(os.path.join(self.ksp, "settings.cfg"), "broken")
            raise RuntimeError("the launcher blew up")
        with self.assertRaises(RuntimeError):
            self.run_variant("vanilla", game=game)
        self.assertEqual(manifest(self.ksp), self.before)
        self.assertEqual(self.vm.calls, ["begin", "end"])

    def test_the_timeout_is_capped_at_twenty_minutes(self):
        self.run_variant("vanilla", timeout_min=90)
        self.assertEqual(self.seen["timeout"], 20 * 60)


class BisectVariantTests(Base):
    def moved(self, variant):
        return [rel for rel, _k in vg.aside_items(self.ksp, variant, keep_eve=True)]

    def test_each_variant_moves_only_its_component(self):
        self.assertEqual(self.moved("ours"), [])
        proxy = self.moved("no-proxy")
        self.assertIn("dxgi.dll", proxy)
        self.assertIn("dlssg_sm86", proxy)
        self.assertNotIn("GameData/ReDefinition", proxy)
        self.assertEqual(self.moved("no-atmo"), ["GameData/ReDefinition/Atmospheres"])
        self.assertEqual(self.moved("no-main"), ["GameData/ReDefinition/Main"])
        self.assertEqual(self.moved("settings-original"), [])
        self.assertEqual(self.moved("other-mod-files"), [])

    def test_patch_files_are_the_patches_of_other_things_only(self):
        self.assertEqual(sorted(vg.patch_files(self.ksp)), ["GameData/ReDefinition/Atmospheres/Audio/RSE.cfg", "GameData/ReDefinition/Main/Patches/P.cfg"])
        self.assertEqual(sorted(self.moved("no-patches")), sorted(vg.patch_files(self.ksp)))

    def test_every_variant_runs_and_the_install_comes_back(self):
        for variant in vg.BISECT_VARIANTS:
            with self.subTest(variant=variant):
                self.vm.calls = []
                result = self.run_variant(variant)
                self.assertTrue(result["ok"], (variant, result))
                self.assertEqual(manifest(self.ksp), self.before, variant)
                self.assertFalse(os.path.exists(os.path.join(self.ksp, vg.POINTER)))
                self.assertEqual((self.seen["dxgi"], self.seen["main"], self.seen["atmo"]),
                                 (variant != "no-proxy", variant != "no-main", variant != "no-atmo"), variant)
                self.assertEqual("UI_SCALE = 1.5" in self.seen["settings"], True)

    def test_no_patches_leaves_the_rest_of_ours(self):
        self.run_variant("no-patches")
        self.assertEqual(self.seen["patches"], ["Atmospheres/Bodies/K.cfg", "Atmospheres/Clouds/E.cfg"])
        self.assertTrue(self.seen["redef"] and self.seen["dxgi"])

    def test_settings_original_pins_the_originals_with_ours_in_place(self):
        self.run_variant("settings-original")
        self.assertIn("TERRAIN_SHADER_QUALITY = 0", self.seen["settings"])
        self.assertTrue(self.seen["redef"] and self.seen["dxgi"])

    def test_other_mod_files_are_set_back_and_restored(self):
        result = self.run_variant("other-mod-files")
        self.assertTrue(result["ok"], result)
        files = self.seen["files"]
        self.assertIn("maxTessellation = 64", files["GameData/ParallaxContinued/Config/ParallaxGlobalSettings.cfg"])
        self.assertIn("m_fourierGridSize = 256\r\n", files["GameData/scatterer/config/config.cfg"])
        self.assertIn("other = 1\r\n", files["GameData/scatterer/config/config.cfg"])
        self.assertIn("renderVessels = False", files["GameData/DistantObject/PluginData/Settings.cfg"])
        self.assertIn("renderMode = 0", files["GameData/DistantObject/PluginData/Settings.cfg"])
        self.assertIn("useHalfResolutionScreenSpaceReflections = True", files["GameData/zzz_Deferred/Deferred.cfg"])
        self.assertIn("FlightSceneProfile = Default-Flight", files["saves/ReDefinitionProbe/probe.sfs"])
        self.assertIn("IVAProfile = Blackrack_TUFX", files["saves/ReDefinitionProbe/probe.sfs"])
        self.assertEqual(manifest(self.ksp), self.before)

    def test_the_before_block_is_read_with_its_saves(self):
        top, saves = vg.read_before_block(self.ksp)
        self.assertEqual(top["parallax.maxTessellation"], "64")
        self.assertEqual(saves["ReDefinitionProbe"], {"tufx.profileFlight": "Default-Flight"})
        edits, notes = vg.mod_file_edits(self.ksp)
        self.assertNotIn("saves/ReDefinitionProbe/other.sfs", edits)
        self.assertTrue(any("renderMode" in n for n in notes))

    def test_edit_first_key_keeps_indent_and_line_ends(self):
        text, old = vg.edit_first_key("a\n\tkey = 1\r\n\tkey = 2\r\n", "key", "9")
        self.assertEqual((text, old), ("a\n\tkey = 9\r\n\tkey = 2\r\n", "1"))
        self.assertEqual(vg.edit_first_key("x = 1", "key", "9"), ("x = 1", None))

    def test_the_component_check_reports_a_variant_that_did_not_take(self):
        folder = os.path.join(self.tmp, "pics")
        write(os.path.join(folder, "assemblies.txt"), "mscorlib 4\nReDefinition 0.1\nReDefinition.Atmospheres 0.1\n")
        self.assertEqual(vg.component_problems("ours", folder), [])
        self.assertEqual(len(vg.component_problems("vanilla", folder)), 2)
        self.assertEqual(len(vg.component_problems("no-main", folder)), 1)
        self.assertEqual(len(vg.component_problems("no-atmo", folder)), 1)
        write(os.path.join(folder, "assemblies.txt"), "mscorlib 4\nReDefinition.Atmospheres 0.1\n")
        self.assertEqual(vg.component_problems("no-main", folder), [])
        self.assertTrue(vg.component_problems("ours", folder))

    def test_a_run_whose_component_did_not_load_fails(self):
        base = self.good_game("no-main")

        def game(args, timeout):
            base(args, timeout)
            write(os.path.join(self.ksp, "GameData", "ProbeLite", "PluginData", "assemblies.txt"), "mscorlib 4\n")
            return "quit", 0
        result = self.run_variant("no-main", game=game)
        self.assertFalse(result["ok"])
        self.assertTrue(any("ReDefinition.Atmospheres did not load" in p for p in result["problems"]))
        self.assertEqual(manifest(self.ksp), self.before)


class HoverVariantTests(Base):
    def test_hover_variants_run_restore_and_write_the_height(self):
        for variant, metres, vanilla in (("ours-hover", 300, False), ("vanilla-hover", 300, True), ("ours-hover-lowfly", 60, False)):
            with self.subTest(variant=variant):
                result = self.run_variant(variant)
                self.assertTrue(result["ok"], (variant, result))
                self.assertIn("hoverFarM=%d" % metres, self.seen["config"])
                self.assertIn("variant=%s\n" % variant, self.seen["config"])
                self.assertEqual(self.seen["redef"], not vanilla)
                self.assertEqual(self.seen["dxgi"], not vanilla)
                self.assertTrue(result["pictures"].endswith(variant))
                self.assertEqual(manifest(self.ksp), self.before, variant)

    def test_plain_variants_hold_no_vessel(self):
        self.run_variant("ours")
        self.assertIn("hoverFarM=0\n", self.seen["config"])

    def test_hover_m_lifts_any_variant_and_labels_its_folder(self):
        result = self.run_variant("no-proxy", hover_m=300)
        self.assertIn("hoverFarM=300", self.seen["config"])
        self.assertIn("variant=no-proxy+hover300", self.seen["config"])
        self.assertTrue(result["pictures"].endswith("no-proxy+hover300"))
        self.assertEqual(manifest(self.ksp), self.before)

    def test_a_hover_variant_is_vanilla_or_ours_by_its_name(self):
        self.assertTrue(vg.is_vanilla("vanilla-hover"))
        self.assertFalse(vg.is_vanilla("ours-hover"))
        self.assertEqual(vg.component_problems("ours-hover", self.tmp), ["assemblies.txt is missing: what loaded is unknown"])


class FlagVariantTests(Base):
    def test_the_probe_presets_are_the_k_s2_arguments(self):
        self.assertEqual(vg.FLAG_PRESETS["probe+stay+clouds"], ["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeClouds"])
        # the K-S2 start's own line (tests/series/K-S2.json, ground_on): the preset arguments are in it, in the same order
        path = os.path.join(REPO, "tests", "series", "K-S2.json")
        if os.path.isfile(path):
            with open(path, "r", encoding="utf-8") as f:
                start = [s for s in json.load(f)["starts"] if s["id"] == "ground_on"][0]
            args = start["probeArgs"]
            positions = [args.index(a) for a in vg.FLAG_PRESETS["probe+stay+clouds"]]
            self.assertEqual(positions, sorted(positions))
            self.assertIn("-atmoSet", args)

    def test_flags_are_passed_to_the_game_and_the_plugin_leaves_the_loading_to_it(self):
        result = self.run_variant("ours", flags="probe+stay+clouds")
        self.assertTrue(result["ok"], result)
        self.assertEqual(self.seen["args"], vg.WINDOW_ARGS + ["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeClouds"])
        self.assertIn("menuLoad=0\n", self.seen["config"])
        self.assertIn("spaceCentre=0\n", self.seen["config"])
        self.assertIn("variant=ours+probe+stay+clouds\n", self.seen["config"])
        self.assertTrue(result["pictures"].endswith("ours+probe+stay+clouds"))
        self.assertEqual(manifest(self.ksp), self.before)

    def test_flags_without_a_probe_argument_leave_the_loading_to_the_plugin(self):
        self.run_variant("ours", flags="atmoset")
        self.assertEqual(self.seen["args"], vg.WINDOW_ARGS + ["-atmoSet", "weatherEffects=1,cloudSource=Ours,ownClouds=1"])
        self.assertIn("menuLoad=1\n", self.seen["config"])
        self.assertIn("spaceCentre=1\n", self.seen["config"])

    def test_ours_flags_is_ours_with_the_full_preset(self):
        result = self.run_variant("ours+flags")
        self.assertTrue(result["pictures"].endswith("ours+probe+stay+clouds"))
        self.assertTrue(self.seen["redef"] and self.seen["dxgi"])

    def test_custom_flags_need_extra_args_and_extra_args_are_added(self):
        with self.assertRaises(vg.Refusal):
            self.run_variant("ours", flags="custom")
        with self.assertRaises(vg.Refusal):
            self.run_variant("ours", flags="nonsense")
        self.run_variant("ours", flags="custom", extra_args=["-atmoSet", "enabled=0"])
        self.assertEqual(self.seen["args"], vg.WINDOW_ARGS + ["-atmoSet", "enabled=0"])
        self.assertTrue(os.path.isdir(os.path.join(self.main, *vg.PICTURES_REL, "ours+custom")))

    def test_the_plan_shows_the_flags(self):
        import argparse
        out = []
        original = vg.format_plan
        try:
            vg.format_plan = lambda plan: out.append(plan) or "x"
            vg.cmd_plan(argparse.Namespace(ksp=self.ksp, variant="ours+flags", flags=None, extra_args=None, profile="none", keep_eve=False))
        finally:
            vg.format_plan = original
        self.assertEqual(out[0]["launch_args"], vg.FLAG_PRESETS["probe+stay+clouds"])
        text = vg.format_plan({"variant": "ours", "ksp": "k", "moves": [], "pins": [], "notes": [], "profile": "none", "launch_args": ["-redefinitionProbe"]})
        self.assertIn("-redefinitionProbe", text)
        self.assertIn("waits for the flight", text)


class DirectVariantTests(Base):
    def test_direct_variants_skip_the_space_centre_and_keep_loading_themselves(self):
        for variant, vanilla in (("ours-direct", False), ("vanilla-direct", True)):
            with self.subTest(variant=variant):
                result = self.run_variant(variant)
                self.assertTrue(result["ok"], (variant, result))
                self.assertIn("menuLoad=1\n", self.seen["config"])
                self.assertIn("spaceCentre=0\n", self.seen["config"])
                self.assertEqual(self.seen["redef"], not vanilla)
                self.assertTrue(result["pictures"].endswith(variant))
                self.assertEqual(manifest(self.ksp), self.before, variant)

    def test_the_plain_variants_visit_the_space_centre(self):
        self.run_variant("ours")
        self.assertIn("spaceCentre=1\n", self.seen["config"])


class BisectCommandTests(Base):
    def setUp(self):
        super().setUp()
        self.base = os.path.join(self.main, *vg.PICTURES_REL)
        self.header = "name\tscene\tlow_r\tlow_g\tlow_b\tlow_green\tll_r\tll_g\tll_b\tll_green\thorizon_luma\tground_contrast\tverdict\n"
        self.runs = []
        self.original = (vg.do_run, vg.gpu_lock.main_repo)
        vg.gpu_lock.main_repo = lambda: self.main
        vg.do_run = self.fake_run
        self.greens = {}

    def tearDown(self):
        vg.do_run, vg.gpu_lock.main_repo = self.original

    def put(self, variant, green, ll=None):
        box = ("66\t71\t73\t%s" % ll) if ll is not None else "\t\t\t"
        write(os.path.join(self.base, variant, "shots", "results.tsv"),
              self.header + "far_p2\tflight\t90\t100\t80\t%s\t%s\t120\t9\tok\nsc_default\tspacecentre\t1\t2\t3\t4\t\t\t\t\t\t\tok\n" % (green, box))

    def fake_run(self, ksp, variant, profile="medium", timeout_min=20, extra_args=None, keep_eve=False, hover_m=None, flags=None):
        label = variant if hover_m is None or variant in vg.HOVER_M else "%s+hover%g" % (variant, hover_m)
        if flags:
            label += "+" + flags
        key = flags or variant
        self.runs.append(label)
        if key in self.greens and self.greens[key] is None:
            return 1, {"pictures": None}
        self.put(label, self.greens.get(key, 5.0), ll=self.greens.get(key, 5.0) if flags else None)
        return 0, {"pictures": os.path.join(self.base, label)}

    def args(self, **kw):
        import argparse
        d = dict(ksp=self.ksp, profile="medium", keep_eve=False, timeout_min=20, extra_arg=[], variants=None, keep_going=False, hover_m=None, presets=None)
        d.update(kw)
        return argparse.Namespace(**d)

    def test_it_stops_at_the_first_variant_that_turns_green(self):
        self.put("vanilla", 20.0)
        self.put("ours", 5.0)
        self.greens = {"no-atmo": 19.0}
        self.assertEqual(vg.cmd_bisect(self.args()), 0)
        self.assertEqual(self.runs, ["settings-original", "no-proxy", "no-patches", "no-atmo"])

    def test_keep_going_runs_the_whole_list(self):
        self.put("vanilla", 20.0)
        self.put("ours", 5.0)
        self.greens = {"no-proxy": 18.0}
        vg.cmd_bisect(self.args(keep_going=True))
        self.assertEqual(self.runs, list(vg.BISECT_ORDER))

    def test_ours_is_run_first_where_it_has_no_results(self):
        self.put("vanilla", 20.0)
        self.greens = {"ours": 5.0}
        vg.cmd_bisect(self.args(variants="no-proxy"))
        self.assertEqual(self.runs, ["ours", "no-proxy"])

    def test_it_refuses_without_vanilla_or_with_equal_grounds(self):
        self.assertEqual(vg.cmd_bisect(self.args()), 2)
        self.put("vanilla", 5.0)
        self.put("ours", 5.5)
        self.assertEqual(vg.cmd_bisect(self.args()), 2)
        self.assertEqual(self.runs, [])

    def test_a_failed_run_stops_the_list(self):
        self.put("vanilla", 20.0)
        self.put("ours", 5.0)
        self.greens = {"no-proxy": None}
        self.assertEqual(vg.cmd_bisect(self.args()), 1)
        self.assertEqual(self.runs, ["settings-original", "no-proxy"])

    def test_a_non_bisect_variant_is_refused(self):
        self.put("vanilla", 20.0)
        self.put("ours", 5.0)
        self.assertEqual(vg.cmd_bisect(self.args(variants="vanilla")), 2)

    def test_green_again_is_nearer_to_vanilla(self):
        self.assertTrue(vg.is_green_again(18.0, 20.0, 5.0))
        self.assertFalse(vg.is_green_again(6.0, 20.0, 5.0))
        self.assertFalse(vg.is_green_again(5.0, 5.5, 5.0))
        self.assertFalse(vg.is_green_again(float("nan"), 20.0, 5.0))
        self.assertTrue(vg.is_green_again(-1.0, 3.0, -10.0))
        self.assertFalse(vg.is_green_again(-9.0, 3.0, -10.0))

    def test_hover_bisect_uses_the_hover_references_and_labels(self):
        self.put("vanilla-hover", 20.0)
        self.put("ours-hover", 5.0)
        self.greens = {"no-proxy": 19.0}
        self.assertEqual(vg.cmd_bisect(self.args(hover_m=300.0, variants="no-proxy")), 0)
        self.assertEqual(self.runs, ["no-proxy+hover300"])

    def test_hover_bisect_runs_the_missing_reference_first_and_refuses_other_heights(self):
        self.put("vanilla-hover", 20.0)
        self.greens = {"ours-hover": 5.0}
        vg.cmd_bisect(self.args(hover_m=300.0, variants="no-proxy"))
        self.assertEqual(self.runs, ["ours-hover", "no-proxy+hover300"])
        self.assertEqual(vg.cmd_bisect(self.args(hover_m=100.0)), 2)

    def test_flagbisect_stops_at_the_first_preset_whose_ground_is_nearer_to_k_s2(self):
        # plain ours in the K-S2 box: green 20 (put without a box falls back to the lowest quarter's green); K-S2's capture_ab B: 71.5 - (65.7 + 72.5) / 2 = 2.4
        self.put("ours", 20.0)
        self.greens = {"probe": 19.0, "probe+stay": 3.0}
        self.assertEqual(vg.cmd_flagbisect(self.args()), 0)
        self.assertEqual(self.runs, ["ours+probe", "ours+probe+stay"])

    def test_flagbisect_keep_going_runs_every_preset_and_skips_failed_runs(self):
        self.put("ours", 20.0)
        self.greens = {"probe": None, "probe+stay": 3.0}
        vg.cmd_flagbisect(self.args(keep_going=True))
        self.assertEqual(self.runs, ["ours+" + p for p in vg.FLAG_ORDER])

    def test_flagbisect_runs_ours_first_and_refuses_when_ours_is_already_grey(self):
        self.greens = {"ours": 20.0}
        vg.cmd_flagbisect(self.args(presets="probe"))
        self.assertEqual(self.runs, ["ours", "ours+probe"])
        self.runs = []
        self.put("ours", 3.0)
        self.assertEqual(vg.cmd_flagbisect(self.args()), 2)
        self.assertEqual(self.runs, [])
        self.assertEqual(vg.cmd_flagbisect(self.args(presets="nonsense")), 2)

    def test_the_table_has_the_k_s2_box_and_the_haze_columns(self):
        self.put("ours", 20.0, ll=18.5)
        text = vg.bisect_table(self.main, [])
        header, row = text.splitlines()[0], text.splitlines()[1]
        for word in ("K-S2 box", "horizon", "contrast"):
            self.assertIn(word, header)
        self.assertIn("66,71,73", row)
        self.assertIn("18.5", row)
        self.assertIn("120", row)

    def test_the_table_lists_the_hover_runs_in_its_order(self):
        for v, g in (("vanilla", 20.0), ("ours", 5.0), ("ours-hover", 4.0), ("vanilla-hover", 6.0), ("no-proxy+hover300", 5.0)):
            self.put(v, g)
        text = vg.bisect_table(self.main, ["no-proxy+hover300"])
        order = [l.split()[0] for l in text.splitlines()[1:]]
        self.assertEqual(order, ["ours", "ours-hover", "vanilla", "vanilla-hover", "no-proxy+hover300"])

    def test_the_table_lists_variant_and_ground(self):
        self.put("vanilla", 20.0)
        self.put("ours", 5.0)
        self.put("no-proxy", 6.0)
        text = vg.bisect_table(self.main, ["no-proxy", "no-atmo"])
        self.assertIn("vanilla", text)
        self.assertIn("no-proxy", text)
        self.assertNotIn("no-atmo", text)
        self.assertIn("90,100,80", text)


class CrashTests(Base):
    """The script itself dies after the launch: the finally block does not run, as when the process is killed. `restore` must then put everything back."""

    def crash(self, variant="vanilla"):
        original = vg.restore
        vg.restore = lambda *a, **k: []
        try:
            self.run_variant(variant)
        finally:
            vg.restore = original

    def test_restore_after_a_crash_puts_everything_back(self):
        self.crash()
        self.assertNotEqual(manifest(self.ksp), self.before)
        self.assertTrue(os.path.isfile(os.path.join(self.ksp, vg.POINTER)))
        self.assertFalse(os.path.exists(os.path.join(self.ksp, "dxgi.dll")))
        problems = vg.restore(self.ksp, self.main, lambda: False, log=lambda m: None)
        self.assertEqual(problems, [])
        self.assertEqual(manifest(self.ksp), self.before)
        self.assertFalse(os.path.exists(os.path.join(self.ksp, vg.POINTER)))
        self.assertFalse(os.path.exists(os.path.join(self.ksp, "GameData", "ProbeLite")))

    def test_a_pointer_blocks_the_next_run(self):
        self.crash()
        with self.assertRaises(vg.Refusal):
            self.run_variant("ours")

    def test_restore_refuses_while_the_game_runs(self):
        self.crash()
        problems = vg.restore(self.ksp, self.main, lambda: True, log=lambda m: None)
        self.assertTrue(problems)
        self.assertFalse(os.path.exists(os.path.join(self.ksp, "dxgi.dll")))
        self.assertTrue(os.path.isfile(os.path.join(self.ksp, vg.POINTER)))
        self.assertEqual(vg.restore(self.ksp, self.main, lambda: False, log=lambda m: None), [])
        self.assertEqual(manifest(self.ksp), self.before)

    def test_a_changed_hash_keeps_the_journal_and_the_pointer(self):
        self.crash()
        with open(os.path.join(self.ksp, vg.POINTER), "r", encoding="utf-8") as f:
            journal = json.load(f)["journal"]
        aside = os.path.join(os.path.dirname(journal), "aside")
        write(os.path.join(aside, "dxgi.dll"), "tampered")
        problems = vg.restore(self.ksp, self.main, lambda: False, log=lambda m: None)
        self.assertTrue(any("dxgi.dll" in p for p in problems), problems)
        self.assertTrue(os.path.isfile(os.path.join(self.ksp, vg.POINTER)))
        self.assertTrue(os.path.isfile(journal))

    def test_restore_twice_is_harmless(self):
        self.crash()
        self.assertEqual(vg.restore(self.ksp, self.main, lambda: False, log=lambda m: None), [])
        self.assertEqual(vg.restore(self.ksp, self.main, lambda: False, log=lambda m: None), [])
        self.assertEqual(manifest(self.ksp), self.before)

    def test_restore_keeps_pictures_the_run_had_not_collected(self):
        original = vg.collect_pictures
        calls = []

        def fail(*a, **k):
            if not k.get("salvage"):
                raise OSError("disk full")
            calls.append(a)
            return original(*a, **k)
        vg.collect_pictures = fail
        try:
            self.crash()
            self.assertEqual(vg.restore(self.ksp, self.main, lambda: False, log=lambda m: None), [])
        finally:
            vg.collect_pictures = original
        self.assertTrue(calls)
        folder = os.path.join(self.main, ".local", "work", "series-pictures", "VANILLA")
        self.assertTrue(any("salvage" in n for n in os.listdir(folder)))


class RefusalTests(Base):
    def test_game_running(self):
        with self.assertRaises(vg.Refusal):
            self.run_variant("vanilla", running=lambda: True)
        self.assertEqual(manifest(self.ksp), self.before)

    def test_low_memory(self):
        with self.assertRaises(vg.Refusal):
            self.run_variant("vanilla", free=lambda: 8.0)
        self.assertEqual(manifest(self.ksp), self.before)

    def test_leftover_plugin_folder(self):
        write(os.path.join(self.ksp, "GameData", "ProbeLite", "x"), "x")
        with self.assertRaises(vg.Refusal):
            self.run_variant("vanilla")

    def test_missing_save(self):
        shutil.rmtree(os.path.join(self.ksp, "saves", "ReDefinitionProbe"))
        with self.assertRaises(vg.Refusal):
            self.run_variant("vanilla")


class ProbeLiteTests(unittest.TestCase):
    def test_sources_use_only_the_game(self):
        folder = os.path.join(REPO, "tools", "ProbeLite")
        for name in os.listdir(folder):
            if name.endswith(".cs"):
                with open(os.path.join(folder, name), "r", encoding="utf-8") as f:
                    text = f.read()
                self.assertNotIn("using ReDefinition", text, name)
                self.assertNotIn("ReDefinition.", text.replace("ReDefinition*", ""), name)
        with open(os.path.join(folder, "ProbeLite.csproj"), "r", encoding="utf-8") as f:
            proj = f.read()
        self.assertNotIn("ProjectReference", proj)
        self.assertNotIn("ReDefinition.", proj)

    def test_built_dll_references_no_redefinition_assembly(self):
        dll = os.path.join(REPO, "build", "ProbeLite", "ProbeLite.dll")
        if not os.path.isfile(dll):
            self.skipTest("ProbeLite is not built")
        command = ("[System.Reflection.Assembly]::ReflectionOnlyLoadFrom('%s').GetReferencedAssemblies() | ForEach-Object { $_.Name }" % dll.replace("'", "''"))
        try:
            out = subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, text=True, timeout=60).stdout
        except (OSError, subprocess.TimeoutExpired):
            self.skipTest("no powershell")
        names = [n.strip() for n in out.splitlines() if n.strip()]
        self.assertTrue(names, "no references read")
        self.assertFalse([n for n in names if n.lower().startswith("redefinition")], names)


class ResultsTests(unittest.TestCase):
    def test_compare_needs_both(self):
        main = tempfile.mkdtemp(prefix="vg-cmp-")
        self.addCleanup(shutil.rmtree, main, True)
        self.assertIn("compare needs both", vg.compare_text(main))

    def test_compare_prints_the_difference(self):
        main = tempfile.mkdtemp(prefix="vg-cmp-")
        self.addCleanup(shutil.rmtree, main, True)
        header = "name\tscene\tlow_r\tlow_g\tlow_b\n"
        for variant, row in (("vanilla", "far_p1\tflight\t100\t110\t90\n"), ("ours", "far_p1\tflight\t90\t100\t90\n")):
            write(os.path.join(main, *vg.PICTURES_REL, variant, "shots", "results.tsv"), header + row)
        text = vg.compare_text(main)
        self.assertIn("-10.0,-10.0,+0.0", text)


if __name__ == "__main__":
    unittest.main()
