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
            "\tFRAMERATE_LIMIT = 120\n}\n")
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
        "GameData/ReDefinition/Main/PluginData/settings.cfg": MAIN, "GameData/ReDefinition/Main/Plugins/ReDefinition.dll": "dll",
        "GameData/ReDefinition/Atmospheres/PluginData/settings.cfg": "atmo", "GameData/ReDefinition/Atmospheres/PluginData/cache.bin": "c",
        "GameData/EnvironmentalVisualEnhancements/a.cfg": "eve", "GameData/StockVolumetricClouds/b.cfg": "svc", "GameData/Squad/x.cfg": "squad",
        "GameData/ModuleManager.ConfigCache": "cache", "GameData/ModuleManager.ConfigSHA": "sha", "GameData/ModuleManager.Physics": "ph", "GameData/ModuleManager.TechTree": "tt",
        "GameData/BetterTimeWarp/PluginData/BetterTimeWarp.cfg": "old",
        "PluginData/DistantObject/x.cfg": "do", "saves/ReDefinitionProbe/probe.sfs": "probe", "saves/ReDefinitionProbe/persistent.sfs": "persist",
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
            write(os.path.join(ksp, "KSP.log"), "log")
            data = os.path.join(ksp, "GameData", "ProbeLite", "PluginData")
            write(os.path.join(data, "shots", "far_p1.png"), "png")
            write(os.path.join(data, "shots", "results.tsv"),
                  "name\tscene\theading\televation\tcam_agl_pqs_m\tcam_over_collider_m\tlooks_at_object\tlooks_at_material\tlooks_at_shader\tunder_object\tsun_elev_deg\t"
                  "all_r\tall_g\tall_b\tlow_r\tlow_g\tlow_b\tlow_green\tsettle_s\tsettle\tframes\tfile\n"
                  "far_p1\tflight\t90\t5\t291.28\t-\tPQS\tmat\tParallax/Terrain\tPQS\t45\t1\t2\t3\t100\t110\t90\t15\t5\tstable\t200\tfar_p1.png\n")
            write(os.path.join(data, "assemblies.txt"), "mscorlib 4.0\nAssembly-CSharp 1.0\n")
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
