"""Unit tests of tools/checks/modsync.py: versions, the version resource of a DLL, the CKAN index, the catalogue parser, the diff reader
and the install (backup, preserved files, refusals, rollback) on a fake KSP folder. No network, no real KSP.

    python -m unittest tools.tests.test_modsync
"""
import io
import json
import os
import sys
import tarfile
import tempfile
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "checks"))
sys.path.insert(0, os.path.dirname(HERE))
import modsync as ms  # noqa: E402


class VersionTests(unittest.TestCase):
    def test_compare_numbers_and_trailing_zeros(self):
        self.assertEqual(ms.compare("2.15.4", "2.15.4.0"), 0)
        self.assertEqual(ms.compare("0.10.6", "1.0.2"), -1)
        self.assertEqual(ms.compare("1.1.10", "1.1.9"), 1)
        self.assertEqual(ms.compare("v1.3.0.0", "1.2.10.0"), 1)

    def test_tag_prefixes_are_dropped(self):
        self.assertEqual(ms.compare("RELEASE/2.2.1.8", "2.2.1.8"), 0)
        self.assertEqual(ms.compare("dev-2.16.0.0-2081", "2.16.0.0.2081"), 0)

    def test_epoch_counts_only_between_ckan_versions(self):
        self.assertEqual(ms.compare("1:1.41.1", "1.41.1"), 0)
        self.assertEqual(ms.compare("1:1.0", "2.0", use_epoch=True), 1)

    def test_prerelease_word_is_lower(self):
        self.assertEqual(ms.compare("112.0.2-bleeding-edge.1", "112.0.2"), -1)
        self.assertEqual(ms.compare("1.0.0", "1.0.0-rc.1"), 1)

    def test_last_number(self):
        self.assertEqual(ms.last_of("2:release-1.12.1-254"), "254")
        best, status = ms.newest([{"source": "x", "version": "release-254"}], "1.12.1.249", last_number=True)
        self.assertEqual(status, "newer")
        self.assertEqual(ms.newest([{"source": "x", "version": "release-249"}], "1.12.1.249", last_number=True)[1], "same")

    def test_remote_version_file_is_only_a_hint(self):
        found = [{"source": "remote version file", "version": "1.1.0", "hint": True}, {"source": "GitHub release", "version": "v1.0.2"}]
        best, status = ms.newest(found, "0.10.6")
        self.assertEqual(best["source"], "GitHub release")
        self.assertEqual(status, "newer")

    def test_avc_version_from_dict_and_string(self):
        self.assertEqual(ms.avc_version({"MAJOR": 3, "MINOR": 2, "PATCH": 2, "BUILD": 0}), "3.2.2.0")
        self.assertEqual(ms.avc_version("1.0.6.0"), "1.0.6.0")

    def test_meaningful_assembly_version(self):
        self.assertFalse(ms.meaningful("0.0.0.0"))
        self.assertFalse(ms.meaningful("1.0.0.0"))
        self.assertTrue(ms.meaningful("0.908.0.0"))
        self.assertTrue(ms.meaningful("2.15.4.0"))


class PeTests(unittest.TestCase):
    def blob(self, file_version, product):
        def entry(key, value):
            return key.encode("utf-16-le") + b"\x00\x00\x00\x00" + value.encode("utf-16-le") + b"\x00\x00"
        return b"MZ" + b"\x00" * 40 + entry("FileVersion", file_version) + entry("ProductVersion", product) + b"\x00" * 8

    def test_reads_both_strings(self):
        self.assertEqual(ms._version_strings(self.blob("2.15.4.0", "2.15.4+abc")), ("2.15.4.0", "2.15.4+abc"))

    def test_garbage_marker_is_ignored(self):
        self.assertEqual(ms._version_strings(self.blob("Assembly Version", "x"))[0], None)

    def test_no_resource(self):
        self.assertEqual(ms._version_strings(b"MZ" + b"\x00" * 100), (None, None))


class CkanTests(unittest.TestCase):
    def archive(self):
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
            for name, body in [("CKAN-meta-master/Foo/Foo-1.0.ckan", {"version": "1.0", "ksp_version": "1.12"}),
                               ("CKAN-meta-master/Foo/Foo-2.0.ckan", {"version": "2.0", "ksp_version_min": "1.12", "ksp_version_max": "1.12.5", "resources": {"repository": "https://github.com/a/b"}}),
                               ("CKAN-meta-master/Foo/Foo-3.0.ckan", {"version": "3.0", "ksp_version_min": "1.13", "ksp_version_max": "1.13"}),
                               ("CKAN-meta-master/Bar/Bar-1.ckan", {"version": "1"})]:
                data = json.dumps(body).encode()
                info = tarfile.TarInfo(name)
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))
        return buffer.getvalue()

    def test_slim_index_keeps_wanted_identifiers(self):
        index = ms.slim_ckan(self.archive(), {"Foo"})
        self.assertEqual(sorted(m["version"] for m in index["Foo"]), ["1.0", "2.0", "3.0"])
        self.assertNotIn("Bar", index)

    def test_compatibility_with_ksp(self):
        index = ms.slim_ckan(self.archive(), {"Foo"})
        ok = [m["version"] for m in index["Foo"] if ms.ksp_compatible(m, "1.12.5")]
        self.assertEqual(sorted(ok), ["1.0", "2.0"])

    def test_upstream_prefers_compatible(self):
        index = ms.slim_ckan(self.archive(), {"Foo"})
        row = {"ckan": [{"id": "Foo", "version": "1.0", "resources": {}}], "avc": [], "repo": None}
        found = ms.upstream(row, "1.12.5", offline=True, index=index)
        self.assertEqual(found[0]["version"], "2.0")


CATALOGUE = '''
namespace X {
    internal static class A {
        private const string Clouds = "Atmosphere.Clouds";
        public static readonly ForeignAssumption One =
            new ForeignAssumption("eve-one", "EVE", "what it does; with a, comma")
                .Type(Clouds).Type("Utils.VRUtils")
                .Method(Clouds, "OnPreRender")
                .Field(Clouds, "targetCamera", "UnityEngine.Camera")
                .Literal("Utils.ShaderProperties", "scattererReconstructedCloud", "other")
                .Tested("6f92b507f52f") // EVE 3.2.2
                .OnChange(WhenChanged.Fallback, "text");
    }
}
'''


class CatalogueTests(unittest.TestCase):
    def test_entry_is_parsed(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, "A.cs"), "w", encoding="utf-8") as f:
                f.write(CATALOGUE)
            entries = ms.parse_catalogue(d)
        self.assertEqual(len(entries), 1)
        e = entries[0]
        self.assertEqual((e["id"], e["mod"]), ("eve-one", "EVE"))
        self.assertIn("Atmosphere.Clouds", e["types"])
        self.assertEqual(e["members"], ["OnPreRender", "targetCamera"])
        self.assertEqual(e["literals"], ["other", "scattererReconstructedCloud"])
        self.assertEqual(e["tested"], ["6f92b507f52f"])
        self.assertIn(("Atmosphere.Clouds", "OnPreRender", "member"), e["pairs"])

    def test_touchpoints_carry_owner_types(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, "A.cs"), "w", encoding="utf-8") as f:
                f.write(CATALOGUE)
            names = ms.touchpoints({"catalogue_mods": ["EVE"], "label": "zz-none"}, d)
        self.assertEqual(names["OnPreRender"]["owners"], {"Clouds"})
        self.assertEqual(names["Clouds"]["kind"], "type")
        self.assertEqual(names["other"]["kind"], "literal")


DIFF = """diff --git a/A.cs b/A.cs
--- a/A.cs
+++ b/A.cs
@@ -10,2 +10,2 @@
-    public int Old = 1;
-    public float Mode = 2;
+    public int Old = 3;
+    public float Mode = 2;
"""


class DiffTests(unittest.TestCase):
    def test_hits_carry_file_line_and_sign(self):
        hits = ms.diff_hits(DIFF, "Old")
        self.assertEqual(hits, [("A.cs", 10, "-", "public int Old = 1;"), ("A.cs", 10, "+", "public int Old = 3;")])

    def test_declaration_pattern(self):
        pattern = ms.re.compile(ms.DECLARATION.pattern % "OnPreRender")
        self.assertTrue(pattern.search("public void OnPreRender()"))
        self.assertFalse(pattern.search("    camera.OnPreRender();"))


def make_zip(path, members):
    with zipfile.ZipFile(path, "w") as z:
        for name, data in members.items():
            z.writestr(name, data)


class ExtractTests(unittest.TestCase):
    def test_extracts_only_allowed_folders_and_keeps_preserved(self):
        with tempfile.TemporaryDirectory() as d:
            ksp = os.path.join(d, "ksp")
            os.makedirs(os.path.join(ksp, "GameData", "Mod", "PluginData"))
            with open(os.path.join(ksp, "GameData", "Mod", "PluginData", "settings.cfg"), "w") as f:
                f.write("mine")
            z = os.path.join(d, "m.zip")
            make_zip(z, {"GameData/Mod/Mod.dll": "new", "GameData/Mod/PluginData/settings.cfg": "theirs"})
            written = ms.extract_zip(z, ksp, ["Mod"], ["PluginData/*"])
            self.assertEqual(written, ["Mod/Mod.dll"])
            self.assertEqual(open(os.path.join(ksp, "GameData", "Mod", "PluginData", "settings.cfg")).read(), "mine")
            self.assertEqual(open(os.path.join(ksp, "GameData", "Mod", "Mod.dll")).read(), "new")

    def test_refuses_traversal_and_other_folders(self):
        with tempfile.TemporaryDirectory() as d:
            ksp = os.path.join(d, "ksp")
            os.makedirs(os.path.join(ksp, "GameData"))
            for name in ("GameData/Other/x.dll", "GameData/Mod/../../evil.dll", "GameData/C:/x"):
                z = os.path.join(d, "bad.zip")
                make_zip(z, {name: "x"})
                with self.assertRaises(ms.Refusal):
                    ms.extract_zip(z, ksp, ["Mod"], [])

    def test_pick_asset_needs_the_repository_prefix_and_one_match(self):
        assets = [{"name": "Mod-1.0.zip", "browser_download_url": "https://github.com/a/b/releases/download/1.0/Mod-1.0.zip"},
                  {"name": "Mod-1.0.src.zip", "browser_download_url": "https://github.com/a/b/releases/download/1.0/Mod-1.0.src.zip"}]
        self.assertEqual(ms.pick_asset(assets, "a/b", r"^Mod-[0-9.]+\.zip$")["name"], "Mod-1.0.zip")
        with self.assertRaises(ms.Refusal):
            ms.pick_asset(assets, "a/other", r"^Mod-[0-9.]+\.zip$")
        with self.assertRaises(ms.Refusal):
            ms.pick_asset(assets, "a/b", r"zip$")


class InstallTests(unittest.TestCase):
    """The whole install on a fake KSP with every outside call replaced."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        d = self.tmp.name
        self.ksp = os.path.join(d, "ksp")
        folder = os.path.join(self.ksp, "GameData", "Firefly")
        os.makedirs(os.path.join(folder, "PluginData"))
        with open(os.path.join(folder, "Firefly.version"), "w") as f:
            json.dump({"NAME": "Firefly", "VERSION": "1.0.5.0"}, f)
        with open(os.path.join(folder, "old.txt"), "w") as f:
            f.write("old")
        with open(os.path.join(folder, "PluginData", "settings.cfg"), "w") as f:
            f.write("mine")
        os.makedirs(os.path.join(self.ksp, "CKAN"))
        self.zip = os.path.join(d, "Firefly-1.0.6.zip")
        make_zip(self.zip, {"GameData/Firefly/Firefly.version": json.dumps({"NAME": "Firefly", "VERSION": "1.0.6.0"}), "GameData/Firefly/new.txt": "new"})
        self.report = os.path.join(d, "report.json")
        with open(self.report, "w") as f:
            json.dump({"mod": "firefly", "version": "1.0.6", "verdict": "safe", "branch": "claude/mod-update-firefly-1.0.6", "worktree": "x"}, f)
        self.archive_root = os.path.join(d, "archive")
        self._saved = (ms.REPO, ms.ARCHIVE_DIR, ms.cache_dir)
        ms.REPO = d
        ms.ARCHIVE_DIR = "archive"
        ms.cache_dir = lambda: os.path.join(d, "cache")
        os.makedirs(os.path.join(d, "cache"), exist_ok=True)
        self.asset = {"name": "Firefly-1.0.6.zip", "browser_download_url": "https://github.com/M1rageDev/Firefly/releases/download/1.0.6/Firefly-1.0.6.zip"}
        self.calls = []

    def tearDown(self):
        ms.REPO, ms.ARCHIVE_DIR, ms.cache_dir = self._saved
        self.tmp.cleanup()

    def deps(self, **over):
        zip_path = self.zip

        def download(url, path):
            self.calls.append(("download", url))
            with open(zip_path, "rb") as src, open(path, "wb") as dst:
                dst.write(src.read())

        deps = {"latest_release": lambda entry: ("1.0.6", [self.asset]), "ksp_running": lambda: False, "locks_held": lambda: [],
                "branch_passes": lambda report: (True, ""), "foreign_check": lambda: ({"a": "PASS"}, 0, ""),
                "series_preflight": lambda series: (0, ""), "download": download}
        deps.update(over)
        return deps

    def run_install(self, **over):
        return ms.install("Firefly", report_path=self.report, ksp=self.ksp, deps=self.deps(**over))

    def folder(self, *parts):
        return os.path.join(self.ksp, "GameData", "Firefly", *parts)

    def test_installs_backs_up_and_keeps_settings(self):
        result = self.run_install()
        self.assertTrue(os.path.exists(self.folder("new.txt")))
        self.assertFalse(os.path.exists(self.folder("old.txt")))
        self.assertEqual(open(self.folder("PluginData", "settings.cfg")).read(), "mine")
        backup = os.path.join(self.tmp.name, "archive", "firefly-1.0.5.0")
        self.assertTrue(os.path.exists(os.path.join(backup, "Firefly", "old.txt")))
        record = json.load(open(os.path.join(backup, "install.json")))
        self.assertEqual(record["outcome"], "installed")
        self.assertEqual(len(record["sha256"]), 64)
        self.assertEqual(result["sha256"], record["sha256"])

    def test_dry_run_changes_nothing_and_downloads_nothing(self):
        self.run_install_dry = ms.install("Firefly", dry_run=True, report_path=self.report, ksp=self.ksp, deps=self.deps())
        self.assertTrue(os.path.exists(self.folder("old.txt")))
        self.assertEqual(self.calls, [])

    def test_new_fingerprint_failure_rolls_back(self):
        states = iter([({"a": "PASS"}, 0, ""), ({"a": "FAIL Changed"}, 1, "")])
        with self.assertRaises(ms.Refusal):
            self.run_install(foreign_check=lambda: next(states))
        self.assertTrue(os.path.exists(self.folder("old.txt")))
        self.assertFalse(os.path.exists(self.folder("new.txt")))
        self.assertEqual(open(self.folder("PluginData", "settings.cfg")).read(), "mine")

    def test_failure_that_was_there_before_is_not_new(self):
        self.run_install(foreign_check=lambda: ({"a": "FAIL Changed"}, 1, ""))
        self.assertTrue(os.path.exists(self.folder("new.txt")))

    def test_preflight_failure_rolls_back(self):
        with self.assertRaises(ms.Refusal):
            self.run_install(series_preflight=lambda s: (1, "bad"))
        self.assertTrue(os.path.exists(self.folder("old.txt")))

    def test_refusals_before_anything_changes(self):
        for over, text in [({"ksp_running": lambda: True}, "KSP_x64"), ({"locks_held": lambda: ["gpu by x"]}, "lock"),
                           ({"branch_passes": lambda r: (False, "red")}, "tests")]:
            with self.assertRaises(ms.Refusal) as caught:
                self.run_install(**over)
            self.assertIn(text, str(caught.exception))
            self.assertTrue(os.path.exists(self.folder("old.txt")))
            self.assertFalse(os.path.exists(os.path.join(self.tmp.name, "archive", "firefly-1.0.5.0")))

    def test_verdict_must_be_safe_and_match(self):
        for patch in ({"verdict": "breaking"}, {"verdict": "unclear"}, {"version": "1.0.7"}, {"mod": "tufx"}):
            data = json.load(open(self.report))
            data.update(patch)
            json.dump(data, open(self.report, "w"))
            with self.assertRaises(ms.Refusal):
                self.run_install()
            data.update({"verdict": "safe", "version": "1.0.6", "mod": "firefly"})
            json.dump(data, open(self.report, "w"))

    def test_no_feed_mods_are_never_installed(self):
        with self.assertRaises(ms.Refusal):
            ms.install("Scatterer", ksp=self.ksp, deps=self.deps())
        with self.assertRaises(ms.Refusal):
            ms.install("EVE (Redux, volumetric)", ksp=self.ksp, deps=self.deps())

    def test_branch_names_and_worktrees_are_checked(self):
        with self.assertRaises(ms.Refusal):
            ms.branch_passes({"branch": "main", "worktree": ms.REPO})
        with self.assertRaises(ms.Refusal):
            ms.branch_passes({"branch": "claude/mod-update-x-1", "worktree": os.path.join(os.path.dirname(ms.REPO), "elsewhere")})


class NotifyTests(unittest.TestCase):
    def test_summary_lines(self):
        results = [{"mod": "parsek", "checked": "0.10.6", "new": "1.0.2", "verdict": "breaking", "branch": "claude/mod-update-parsek-1.0.2", "install": "not installed", "report": "r.json"}]
        text = ms.summary_text("2026-11-16", results)
        self.assertIn("parsek", text)
        self.assertIn("0.10.6", text)
        self.assertIn("1.0.2", text)
        self.assertIn("breaking", text)
        self.assertEqual(ms.summary_text("2026-11-16", []), "")


class NightlyDryRunTests(unittest.TestCase):
    """The nightly script's -DryRun: the plan and the notification text, nothing sent, nothing installed."""

    def run_script(self, name, *args):
        import shutil
        import subprocess
        shell = shutil.which("powershell")
        if not shell:
            self.skipTest("no powershell")
        script = os.path.join(os.path.dirname(HERE), "nightly", name)
        result = subprocess.run([shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, *args], capture_output=True, text=True, timeout=300)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout

    def test_nightly_dry_run_prints_plan_and_message_format(self):
        out = self.run_script("modsync-nightly.ps1", "-DryRun", "-Date", "2099-01-01")
        self.assertIn("DRYRUN would: git worktree add --detach", out)
        self.assertIn("--model sonnet", out)
        self.assertIn("SendMessage ListAgents ToolSearch", out)
        self.assertIn("ModSync 2099-01-01:", out)
        self.assertIn("geprueft 0.10.6, neu 1.0.2, Urteil breaking, Branch claude/mod-update-parsek-1.0.2, Installation: nicht installiert", out)

    def test_register_dry_run_states_the_task(self):
        out = self.run_script("register-modsync-task.ps1", "-DryRun")
        self.assertIn("ReDefinition Nightly Mod Update", out)
        self.assertIn("daily at 03:00", out)
        self.assertIn("StartWhenAvailable=True", out)
        self.assertIn("MultipleInstances=IgnoreNew", out)
        self.assertIn("ExecutionTimeLimit=PT3H", out)
        self.assertIn("no password, no admin", out)

    def test_find_report_matches_version_without_trailing_zero(self):
        with tempfile.TemporaryDirectory() as d:
            saved = ms.REPO
            ms.REPO = d
            try:
                folder = os.path.join(d, ms.REPORT_DIR)
                os.makedirs(folder)
                with open(os.path.join(folder, "deferred-1.3.6.json"), "w") as f:
                    json.dump({"mod": "deferred", "version": "1.3.6", "verdict": "safe"}, f)
                self.assertTrue(ms.find_report("deferred", "1.3.6.0").endswith("deferred-1.3.6.json"))
                self.assertIsNone(ms.find_report("deferred", "1.3.7.0"))
                self.assertIsNone(ms.find_report("tufx", "1.3.6.0"))
            finally:
                ms.REPO = saved


class NoFeedTests(unittest.TestCase):
    def test_commit_of_version_by_needle_in_a_real_repo(self):
        import subprocess
        with tempfile.TemporaryDirectory() as d:
            def run(*args):
                subprocess.run(["git", "-C", d] + list(args), check=True, capture_output=True)
            run("init", "-q", "-b", "dev")
            run("config", "user.email", "t@example.invalid")
            run("config", "user.name", "t")
            for i, version in enumerate(("0.0905", "0.0908", "0.0908")):
                with open(os.path.join(d, "S.cs"), "w") as f:
                    f.write('x = 1;\nversionNumber = "%s"; // %d\n' % (version, i))
                run("add", "S.cs")
                run("commit", "-q", "-m", "c%d" % i)
            source = {"branch": "dev", "file": "S.cs", "kind": "needle", "needle_format": 'versionNumber = "{v}"'}
            commit = ms.commit_of_version(d, source, "0.908.0.0")
            self.assertEqual(ms.git(d, "log", "-1", "--format=%s", commit).strip(), "c1")
            self.assertIsNone(ms.commit_of_version(d, source, "0.909.0.0"))


if __name__ == "__main__":
    unittest.main()
