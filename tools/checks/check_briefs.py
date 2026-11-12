#!/usr/bin/env python3
"""The orchestrator's consistency check of the Atmospheres briefs. Stdlib only.

  python tools/checks/check_briefs.py [--briefs <folder>] [--plan <master plan>] [--repo <checkout>] [--scan <file> ...]
  python tools/checks/check_briefs.py --selftest

It reads the briefs (MAINREPO\\.local\\plans\\atmospheres\\briefs) and the master plan by absolute path and checks:

  ownership    the backticked paths of each brief's first "Files you own:" sentence (minus its EXCEPT list) against every other brief's:
               a path that two packages can both match is a conflict (glob-aware, segment by segment);
  waves        the wave in each brief's title ("(wave W1 ...") against the wave column of master plan 7.1;
  estimates    "Estimate: build X-Y, verify Z" in each brief against the build and verify columns of master plan 7.1;
  phrases      the regular expressions of tools/checks/forbidden_phrases.txt (assurance forms of standing rule 3) in the briefs, the
               master plan and any --scan files;
  contract     the type names in the tables of src/Atmospheres/CONTRACT.md against the declarations in the source, and backticked names in
               the briefs that differ from a declared contract type only by case or one character (a probable misspelling);
  keys         backticked camelCase tokens in the briefs' lines about keys, settings or switches that are not a pre-declared setting key
               (AtmospheresSettings.Keys.cs); information only;
  paths        backticked repository paths without a wildcard that do not exist and that no brief owns; information only.

It is a name and path check. It cannot see a signature mismatch, a changed meaning or a wrong number inside a sentence.
Exit code 1 when an ownership overlap, a wave or estimate mismatch, a forbidden phrase or a contract name problem is found.
"""
import argparse
import fnmatch
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
THIS_REPO = os.path.dirname(os.path.dirname(HERE))
FALLBACK_MAINREPO = r"C:\Users\franz\IdeaProjects\ReDefinition"


def main_repo():
    env = os.environ.get("ATMO_MAINREPO")
    if env:
        return os.path.abspath(env)
    try:
        out = subprocess.run(["git", "-C", HERE, "rev-parse", "--git-common-dir"], capture_output=True, text=True, timeout=20).stdout.strip()
        if out:
            common = os.path.abspath(out if os.path.isabs(out) else os.path.join(HERE, out))
            if os.path.basename(common) == ".git":
                return os.path.dirname(common)
    except (OSError, subprocess.TimeoutExpired):
        pass
    return FALLBACK_MAINREPO


def read(path):
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


# ---------------------------------------------------------------- briefs

def load_briefs(folder):
    """{id: text} of the briefs (files like A02-platform.md; _common.md is not a brief)."""
    out = {}
    for name in sorted(os.listdir(folder)):
        m = re.match(r"^([A-Z]\d{2})-.*\.md$", name)
        if m:
            out[m.group(1)] = read(os.path.join(folder, name))
    return out


def first_sentence_of_owns(text):
    """The text of the 'Files you own:' line up to the end of its first sentence (a closing backtick followed by '. ')."""
    for line in text.splitlines():
        if line.startswith("Files you own:"):
            body = line[len("Files you own:"):]
            m = re.search(r"`\.(\s|$)", body)
            return body[:m.start() + 1] if m else body
    return None


def path_tokens(text):
    out = []
    for tok in re.findall(r"`([^`]+)`", text):
        tok = tok.strip()
        if tok.startswith("MAINREPO") or "\\" in tok or " " in tok or "(" in tok:
            continue
        if "/" in tok or re.search(r"\.[A-Za-z]{1,5}$", tok):
            out.append(tok.replace("\\", "/"))
    return out


def owned_paths(text):
    """(owned, excluded) path patterns of a brief; (None, None) where it has no 'Files you own' line (A00 names none)."""
    sentence = first_sentence_of_owns(text)
    if sentence is None:
        return None, None
    parts = re.split(r"\bEXCEPT\b", sentence, maxsplit=1)
    owned = path_tokens(parts[0])
    excluded = path_tokens(parts[1]) if len(parts) > 1 else []
    return owned, excluded


def seg_overlap(a, b):
    """Whether two path segments (literal or with *) can match the same name."""
    if "*" not in a and "*" not in b:
        return a == b
    if "*" in a and "*" not in b:
        return fnmatch.fnmatchcase(b, a)
    if "*" in b and "*" not in a:
        return fnmatch.fnmatchcase(a, b)
    pa, pb = a.split("*")[0], b.split("*")[0]
    return pa.startswith(pb) or pb.startswith(pa)


def patterns_overlap(p, q):
    """Whether some path matches both patterns. '**' as the last segment matches everything below."""
    sp, sq = p.split("/"), q.split("/")
    for i in range(max(len(sp), len(sq))):
        a = sp[i] if i < len(sp) else None
        b = sq[i] if i < len(sq) else None
        if a == "**" or b == "**":
            return True
        if a is None or b is None:
            return False
        if not seg_overlap(a, b):
            return False
    return True


def covers(excl, pattern):
    """Whether an exclusion pattern removes the pattern entirely (the exclusion is the pattern or a '**' ancestor of it)."""
    se, sp = excl.split("/"), pattern.split("/")
    if se[-1] == "**":
        base = se[:-1]
        return len(sp) >= len(base) and all(seg_overlap(x, y) and ("*" not in x or x == y) for x, y in zip(base, sp[:len(base)]))
    return excl == pattern


def check_ownership(briefs):
    owns, excludes = {}, {}
    for bid, text in briefs.items():
        owned, excluded = owned_paths(text)
        if owned is None:
            continue
        owns[bid], excludes[bid] = owned, excluded
    problems = []
    ids = sorted(owns)
    for i, a in enumerate(ids):
        for b in ids[i + 1:]:
            for p in owns[a]:
                for q in owns[b]:
                    if not patterns_overlap(p, q):
                        continue
                    # What an owner excludes is not owned: a path of the other inside an exclusion is no conflict.
                    if any(covers(e, q) for e in excludes[a]) or any(covers(e, p) for e in excludes[b]):
                        continue
                    problems.append("%s owns %s and %s owns %s" % (a, p, b, q))
    return problems, owns


# ---------------------------------------------------------------- the master plan table

def plan_table(plan_text):
    """{id: {wave, build, verify}} from the package table of master plan 7.1."""
    rows = {}
    for line in plan_text.splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) >= 7 and re.match(r"^[A-Z]\d{2}$", cells[0]) and re.match(r"^W\d$", cells[2]):
            rows[cells[0]] = {"wave": cells[2], "build": cells[4], "verify": cells[5]}
    return rows


def check_waves_and_estimates(briefs, table):
    waves, estimates = [], []
    for bid, text in briefs.items():
        row = table.get(bid)
        if row is None:
            waves.append("%s: not in the package table of master plan 7.1" % bid)
            continue
        title = text.splitlines()[0] if text else ""
        m = re.search(r"\bwave (W\d)", title)
        if not m:
            waves.append("%s: no 'wave Wn' in the title" % bid)
        elif m.group(1) != row["wave"]:
            waves.append("%s: title says %s, master plan 7.1 says %s" % (bid, m.group(1), row["wave"]))
        m = re.search(r"Estimate(?: \([^)]*\))?:\s*build\s+([0-9.]+(?:-[0-9.]+)?),\s*verify\s+([0-9.]+(?:-[0-9.]+)?)", text)
        if not m:
            estimates.append("%s: no 'Estimate: build X, verify Y' line" % bid)
        else:
            if m.group(1) != row["build"]:
                estimates.append("%s: build %s, master plan %s" % (bid, m.group(1), row["build"]))
            if m.group(2) != row["verify"]:
                estimates.append("%s: verify %s, master plan %s" % (bid, m.group(2), row["verify"]))
    for pid in table:
        if pid not in briefs:
            waves.append("%s: in master plan 7.1 but no brief" % pid)
    return waves, estimates


# ---------------------------------------------------------------- forbidden phrases

def load_phrases(path):
    out = []
    for line in read(path).splitlines():
        s = line.strip()
        if not s or s.startswith("# ") or s == "#":
            continue
        out.append(re.compile(s, re.IGNORECASE))
    return out


def check_phrases(named_texts, phrases):
    hits = []
    for name, text in named_texts:
        for number, line in enumerate(text.splitlines(), 1):
            for rx in phrases:
                m = rx.search(line)
                if m:
                    hits.append("%s:%d: '%s'" % (name, number, m.group(0).strip()))
    return hits


# ---------------------------------------------------------------- contract names

DECL_RE = re.compile(r"\b(?:class|struct|interface|enum)\s+([A-Z][A-Za-z0-9_]*)|\bdelegate\s+[\w<>\[\],\s]+?\s+([A-Z][A-Za-z0-9_]*)\s*\(")


def declared_types(repo, with_settings=True):
    names = set()
    roots = [os.path.join(repo, "src", "Common", "Weather"), os.path.join(repo, "src", "Atmospheres", "Infrastructure"),
             os.path.join(repo, "src", "Atmospheres", "Bodies", "Schema"),
             os.path.join(repo, "src", "Atmospheres", "Generator"), os.path.join(repo, "src", "Atmospheres", "Field"),
             os.path.join(repo, "src", "Atmospheres", "Forecast")]
    if with_settings:
        roots.append(os.path.join(repo, "src", "Atmospheres", "Settings"))
    for root in roots:
        for r, _d, files in os.walk(root):
            for f in files:
                if f.endswith(".cs"):
                    for m in DECL_RE.finditer(read(os.path.join(r, f))):
                        names.add(m.group(1) or m.group(2))
    return names


def contract_names(contract_text):
    """The PascalCase names in the first column of the CONTRACT.md tables (comma-separated cells split, parentheses cut)."""
    out = []
    for line in contract_text.splitlines():
        if not line.startswith("|"):
            continue
        cells = line.strip().strip("|").split("|")
        if not cells:
            continue
        first = cells[0]
        # Only a first cell that is nothing but names (a row about a type), and not a type another package creates.
        rest = re.sub(r"\(.*?\)", "", re.sub(r"`[^`]*`", "", first)).replace(",", "").strip()
        if rest or "not part of" in first:
            continue
        for tok in re.findall(r"`([^`]+)`", first):
            tok = re.sub(r"\(.*?\)", "", tok).strip()
            if re.match(r"^[A-Z][A-Za-z0-9]+$", tok) and not tok.isupper():
                out.append(tok)
    return out


def edit_distance(a, b, limit=2):
    if abs(len(a) - len(b)) > limit:
        return limit + 1
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def check_contract(repo, briefs):
    problems = []
    contract_path = os.path.join(repo, "src", "Atmospheres", "CONTRACT.md")
    declared = declared_types(repo)
    if not os.path.exists(contract_path):
        return ["CONTRACT.md is missing: " + contract_path]
    for name in contract_names(read(contract_path)):
        if name not in declared:
            problems.append("CONTRACT.md names %s, which no source file declares" % name)
    seam = declared_types(repo, with_settings=False)
    near = sorted(seam)
    lower = {n.lower(): n for n in declared}
    for bid, text in briefs.items():
        for tok in set(re.findall(r"`([A-Z][A-Za-z0-9]{4,})`", text)):
            if tok in declared:
                continue
            if tok.lower() in lower:
                problems.append("%s: `%s` differs from the contract type %s only by case" % (bid, tok, lower[tok.lower()]))
                continue
            for n in near:
                if len(n) >= 9 and n != tok and tok[0] == n[0] and edit_distance(tok, n, 2) <= 2:
                    problems.append("%s: `%s` looks like the contract type %s" % (bid, tok, n))
                    break
    return problems


# ---------------------------------------------------------------- keys and paths (information)

def declared_keys(repo):
    path = os.path.join(repo, "src", "Atmospheres", "Settings", "AtmospheresSettings.Keys.cs")
    if not os.path.exists(path):
        return set()
    return set(re.findall(r'const string \w+ = "([A-Za-z0-9]+)"', read(path)))


def check_keys(repo, briefs):
    keys = declared_keys(repo)
    unknown = []
    if not keys:
        return unknown
    for bid, text in briefs.items():
        for number, line in enumerate(text.splitlines(), 1):
            if not re.search(r"\bkeys?\b|\bsettings?\b|\bswitch", line, re.IGNORECASE):
                continue
            for tok in re.findall(r"`([a-z][a-z0-9]*[A-Z][A-Za-z0-9]*)`", line):
                if tok not in keys:
                    unknown.append("%s:%d: `%s`" % (bid, number, tok))
    return unknown


def check_paths(repo, briefs, owns):
    missing = []
    all_owned = [p for ps in owns.values() for p in ps]
    for bid, text in briefs.items():
        for tok in set(path_tokens(text)):
            if "*" in tok or "<" in tok or "..." in tok or not re.match(r"^(src|tools|tests|unity|GameData|docs)/", tok):
                continue
            if os.path.exists(os.path.join(repo, *tok.split("/"))):
                continue
            if any(patterns_overlap(tok, p) for p in all_owned):
                continue
            missing.append("%s: `%s`" % (bid, tok))
    return missing


# ---------------------------------------------------------------- run

def run(briefs_dir, plan_path, repo, phrases_path, scan, quiet=False):
    briefs = load_briefs(briefs_dir)
    plan_text = read(plan_path)
    overlaps, owns = check_ownership(briefs)
    waves, estimates = check_waves_and_estimates(briefs, plan_table(plan_text))
    texts = [(bid, t) for bid, t in briefs.items()] + [("master-plan", plan_text)]
    common = os.path.join(briefs_dir, "_common.md")
    if os.path.exists(common):
        texts.append(("_common", read(common)))
    for p in scan:
        texts.append((p, read(p)))
    phrases = check_phrases(texts, load_phrases(phrases_path))
    contract = check_contract(repo, briefs)
    keys = check_keys(repo, briefs)
    paths = check_paths(repo, briefs, owns)
    report = [("ownership overlaps", overlaps, True), ("wave mismatches", waves, True), ("estimate mismatches", estimates, True),
              ("forbidden phrases", phrases, True), ("contract name problems", contract, True),
              ("unknown settings-like tokens (information)", keys, False), ("missing unowned paths (information)", paths, False)]
    failed = False
    if not quiet:
        print("%d briefs, %d packages in master plan 7.1" % (len(briefs), len(plan_table(plan_text))))
    for title, items, hard in report:
        if not quiet:
            print("%-46s %d" % (title + ":", len(items)))
            for item in items[:40]:
                print("    " + item)
            if len(items) > 40:
                print("    ... %d more" % (len(items) - 40))
        if hard and items:
            failed = True
    if not quiet:
        print("RESULT: %s" % ("FAIL" if failed else "PASS"))
    return report, failed


# ---------------------------------------------------------------- self test

def _selftest():
    failures = []

    def check(name, ok):
        print("selftest %-62s %s" % (name, "PASS" if ok else "FAIL"))
        if not ok:
            failures.append(name)

    root = tempfile.mkdtemp(prefix="atmo-briefs-selftest-")
    try:
        briefs = os.path.join(root, "briefs")
        os.makedirs(briefs)
        repo = os.path.join(root, "repo")
        os.makedirs(os.path.join(repo, "src", "Atmospheres", "Settings"))
        os.makedirs(os.path.join(repo, "src", "Common", "Weather"))
        with open(os.path.join(repo, "src", "Atmospheres", "Settings", "AtmospheresSettings.Keys.cs"), "w") as f:
            f.write('internal const string A = "lightCount";\n')
        with open(os.path.join(repo, "src", "Common", "Weather", "W.cs"), "w") as f:
            f.write("public interface IWeatherField {}\npublic struct WeatherSample {}\n")
        with open(os.path.join(repo, "src", "Atmospheres", "CONTRACT.md"), "w") as f:
            f.write("| Type | File |\n|---|---|\n| `IWeatherField` | x |\n| `WeatherSample` | y |\n| Wording with `Eve` in a cell | z |\n| `SeasonModel` (not part of A01) | later |\n")
        plan = os.path.join(root, "plan.md")
        with open(plan, "w") as f:
            f.write("| Id | Name | Wave | Depends | Build | Verify | GPU |\n|---|---|---|---|---|---|---|\n"
                    "| A01 | one | W0 | - | 0.6-0.9 | 0.15 | no |\n| B01 | two | W1 | - | 0.5 | 0.1 | no |\n| C01 | three | W1 | - | 1-2 | 0.2 | no |\n")
        phrases = os.path.join(root, "phrases.txt")
        with open(phrases, "w") as f:
            f.write("# comment\n\\bnothing was copied\\b\n")

        def brief(bid, wave, build, verify, owns, extra=""):
            with open(os.path.join(briefs, "%s-x.md" % bid), "w") as f:
                f.write("# Brief %s x (wave %s)\n\nFiles you own: %s\n\n%s\nLadder: L0. Estimate: build %s, verify %s sessions\n" % (bid, wave, owns, extra, build, verify))

        brief("A01", "W0", "0.6-0.9", "0.15", "`src/Atmospheres/Infrastructure/Core/**`, `src/Common/Weather/**`.")
        brief("B01", "W1", "0.5", "0.1", "`src/Atmospheres/Bodies/*.cs`, `src/Atmospheres/Bodies/Terrain/**`.")
        brief("C01", "W1", "1-2", "0.2", "`src/Atmospheres/Infrastructure/Bridge/**` EXCEPT `src/Atmospheres/Infrastructure/Bridge/Legacy/**`. Later text `a/b`.",
              "The `lightCount` key and the `IWeatherField` are used.")
        r, failed = run(briefs, plan, repo, phrases, [], quiet=True)
        check("a consistent set gives zero hard findings", not failed and all(not items for _t, items, hard in r if hard))
        # now seed one fault each
        with open(os.path.join(repo, "src", "Atmospheres", "CONTRACT.md"), "a") as f:
            f.write("| `WeatherSample`, `GhostType` | y |\n")
        brief("A01", "W1", "0.6-0.8", "0.15", "`src/Atmospheres/Infrastructure/Core/**`, `src/Common/Weather/**`.",
              "Nothing was copied. The `IWeatherFeild` and the `Iweatherfield`. The `somethingKey` setting. The `WeatherSample` is ok.")
        brief("B01", "W1", "0.5", "0.1", "`src/Atmospheres/Bodies/*.cs`, `src/Atmospheres/Infrastructure/Core/Foo.cs`.")
        brief("D01", "W3", "1", "1", "`src/Atmospheres/Infrastructure/Bridge/Legacy/X.cs`.")
        r, failed = run(briefs, plan, repo, phrases, [], quiet=True)
        found = {t: items for t, items, _h in r}
        check("an ownership overlap is found (A01 and B01 share Core/**)", any("Infrastructure/Core" in x for x in found["ownership overlaps"]))
        check("an exception (EXCEPT) removes a path from the owner", not any("Legacy" in x and "C01" in x for x in found["ownership overlaps"]))
        check("a wave mismatch is found", any(x.startswith("A01") for x in found["wave mismatches"]))
        check("a brief that is not in the plan is found", any(x.startswith("D01") for x in found["wave mismatches"]))
        check("an estimate mismatch is found", any(x.startswith("A01") and "build" in x for x in found["estimate mismatches"]))
        check("a forbidden phrase is found", len(found["forbidden phrases"]) == 1)
        check("a misspelled contract type is found", any("IWeatherFeild" in x for x in found["contract name problems"]))
        check("a case difference of a contract type is found", any("case" in x for x in found["contract name problems"]))
        check("a name in CONTRACT.md without a declaration is found", any("GhostType" in x for x in found["contract name problems"]))
        check("an unknown settings-like token is reported", any("somethingKey" in x for x in found["unknown settings-like tokens (information)"]))
        check("the run fails with hard findings", failed)
        check("glob overlap: Volume*.shader and Precip*.shader are disjoint",
              not patterns_overlap("u/Shaders/Volume*.shader", "u/Shaders/Precip*.shader"))
        check("glob overlap: Bodies/*.cs and Bodies/Terrain/** are disjoint", not patterns_overlap("s/Bodies/*.cs", "s/Bodies/Terrain/**"))
        check("glob overlap: X/** and X/a/b.cs overlap", patterns_overlap("s/X/**", "s/X/a/b.cs"))
    finally:
        import shutil
        shutil.rmtree(root, ignore_errors=True)
    print("selftest: %s" % ("PASSED" if not failures else "FAILED (%s)" % ", ".join(failures)))
    return 0 if not failures else 1


def main(argv):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    mr = main_repo()
    p.add_argument("--briefs", default=os.path.join(mr, ".local", "plans", "atmospheres", "briefs"))
    p.add_argument("--plan", default=os.path.join(mr, ".local", "plans", "atmospheres-master-plan.md"))
    p.add_argument("--repo", default=THIS_REPO, help="the checkout whose source and CONTRACT.md the contract check reads")
    p.add_argument("--phrases", default=os.path.join(HERE, "forbidden_phrases.txt"))
    p.add_argument("--scan", nargs="*", default=[], help="more files for the forbidden-phrase grep")
    p.add_argument("--selftest", action="store_true")
    args = p.parse_args(argv)
    if args.selftest:
        return _selftest()
    _report, failed = run(args.briefs, args.plan, args.repo, args.phrases, args.scan)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
