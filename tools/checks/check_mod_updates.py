#!/usr/bin/env python3
"""Compares the version of every mod ReDefinition is coupled to against its latest release on GitHub.

The mods and the version each was last checked against are in mod_updates.json beside this file.
When a mod is checked against a newer version, its "tested" entry is raised.
Nothing is read from a KSP install.
This runs anywhere with Python 3 and a network, which is how a scheduled run on a server can use it.

Run it without arguments for the report.
The exit code is 2 where a mod has a newer stable release than the tested one.
Run it with --selftest for the comparison of version numbers, without the network.

GITHUB_TOKEN or GH_TOKEN in the environment raises GitHub's limit of 60 requests an hour.
The run makes one request for each mod.
"""
import json
import os
import re
import sys
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))


def numbers(text):
    """The first dotted number in a tag or version, as a tuple without its trailing zeros; None where there is none."""
    found = re.search(r"\d+(?:\.\d+)*", text or "")
    if not found:
        return None
    parts = [int(part) for part in found.group(0).split(".")]
    while len(parts) > 1 and parts[-1] == 0:
        parts.pop()
    return tuple(parts)


def get(url):
    request = urllib.request.Request(url, headers={"User-Agent": "redefinition-mod-updates", "Accept": "application/vnd.github+json"})
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if token:
        request.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def latest(repo, tag_regex=None):
    """(latest stable, latest pre-release, how it was found): each a (tag, date, url) or None. Tags stand in where a repository has no releases.
    `tag_regex` keeps only the releases whose tag it matches, for a repository that also holds tags of another scheme."""
    releases = get("https://api.github.com/repos/%s/releases?per_page=30" % repo)
    stable = None
    pre = None
    for release in releases:
        if tag_regex and not re.search(tag_regex, release.get("tag_name") or ""):
            continue
        if release.get("draft") or numbers(release.get("tag_name")) is None and not release.get("prerelease"):
            continue
        entry = (release["tag_name"], (release.get("published_at") or "")[:10], release.get("html_url"))
        if release.get("prerelease"):
            if pre is None or entry[1] > pre[1]:
                pre = entry
        elif stable is None or numbers(entry[0]) > numbers(stable[0]):
            stable = entry
    if stable is not None or pre is not None:
        return stable, pre, "release"
    tags = [tag["name"] for tag in get("https://api.github.com/repos/%s/tags?per_page=50" % repo) if numbers(tag["name"]) is not None]
    if tags:
        best = max(tags, key=numbers)
        return (best, "", "https://github.com/%s/releases/tag/%s" % (repo, best)), None, "tag"
    return None, None, "none"


def selftest():
    assert numbers("v0.10.6") == (0, 10, 6)
    assert numbers("2.15.4.0") == (2, 15, 4)
    assert numbers("Parsek-v0.10.6") == (0, 10, 6)
    assert numbers("dev-2.16.0.0-1780") == (2, 16)
    assert numbers("3.2.2.0") == numbers("3.2.2")
    assert numbers("0.908") < numbers("0.909") < numbers("0.1000")
    assert numbers("1.0.0") < numbers("1.1.0")
    assert numbers("none") is None
    print("self-test passed")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return 0
    with open(os.path.join(HERE, "mod_updates.json"), encoding="utf-8") as file:
        mods = json.load(file)
    newer = []
    failed = []
    print("%-8s %-28s %-10s %-22s %s" % ("", "mod", "tested", "latest stable", "note"))
    for mod in mods:
        try:
            stable, pre, how = latest(mod["repo"], mod.get("tag_regex"))
        except (urllib.error.URLError, OSError, ValueError, KeyError) as error:
            failed.append(mod["name"])
            print("%-8s %-28s %-10s %s" % ("ERROR", mod["name"], mod["tested"], error))
            continue
        if stable is None:
            print("%-8s %-28s %-10s %-22s %s" % ("NONE", mod["name"], mod["tested"], "no stable release", "pre-release: %s" % pre[0] if pre else ""))
            continue
        is_newer = numbers(stable[0]) > numbers(mod["tested"])
        status = "NEWER" if is_newer else "ok"
        if is_newer:
            newer.append((mod, stable))
        print("%-8s %-28s %-10s %-22s %s" % (status, mod["name"], mod["tested"], "%s %s" % (stable[0], stable[1]), stable[2] if is_newer else mod.get("note", "")))
        if pre is not None and (not stable[1] or pre[1] > stable[1]):
            print("%-8s %-28s %-10s %-22s %s" % ("", "", "", "pre-release %s %s" % (pre[0], pre[1]), pre[2]))
    print()
    if newer:
        print("%d mod(s) with a newer stable release than the one checked: %s" % (len(newer), ", ".join(mod["name"] for mod, _ in newer)))
    else:
        print("every mod is at the version it was checked against" + (" (%d could not be read)" % len(failed) if failed else ""))
    return 2 if newer else (1 if len(failed) == len(mods) else 0)


if __name__ == "__main__":
    sys.exit(main())
