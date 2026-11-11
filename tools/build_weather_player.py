"""Builds and runs the weather test player (tests/WeatherPlayer), the spike of the atmosphere plan and the player of its series.

It copies what the player links into the player's Unity project and has Unity 2019.4.18f1 build it in batch mode, as
tools/build_rt_player.py does for the raytracing player. What is linked is not listed here: every package has a manifest
tests/WeatherPlayer/linked/<Id>.txt, one glob per line (repo-relative, ** for any folders, # comments). A .cs file is matched by
file name, so that a git mv of the source does not break the line; it goes into Assets/Linked. A .shader, .cginc or .hlsl file goes
into Assets/Linked/Resources. A glob that matches nothing is an error, and so are two different files of the same name. Scenes
register themselves by attribute ([WeatherScene("id")] in tests/WeatherPlayer/Assets/Runner), so a package adds a manifest and a scene
file and edits nothing here. The proxy (dxgi.dll) goes beside the player. Modes of the player:

  stills  the four views without the cell, with the curtain, the slab and both (colour, depth, motion frames)
  time    the GPU milliseconds of the slab's passes and the curtain through the proxy's timestamp queries
  turn    the camera turn through each upscaler (native, FSR 3, DLSS K/M/L, XeSS, AMD) with references
  scene   a scene registered by attribute (-scene <id>), which prints its WEATHERPLAYER {metric,value,limit,pass} lines

tools/analyze_weather_player.py measures the frames. KSP must not be running.

A series runs player stages and judges them as ONE table of named metrics (tools/weather_metrics, see its __init__.py):

  python tools/build_weather_player.py --series smoke

The build takes the Unity lock and the runs take the GPU lock (tools/gpu_lock.py; exclusive for a timing series), so a second
invocation waits. Exit code 0 only if every metric passed.

Usage: python tools/build_weather_player.py [--unity <Unity.exe>] [--ksp <KSP folder>] [--no-build] [--stage stills|time|turn|all|build]
                                            [--out <folder>] [--only <label>] [--sweep name=a,b,c]
       python tools/build_weather_player.py --series <name> [--no-build] [--ksp <KSP folder>]
       python tools/build_weather_player.py --list-linked
       python tools/build_weather_player.py --selftest
"""

import argparse
import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
PROJECT = os.path.join(ROOT, 'tests', 'WeatherPlayer')
MANIFESTS = os.path.join(PROJECT, 'linked')
LINKED = os.path.join(PROJECT, 'Assets', 'Linked')
RESOURCES = os.path.join(LINKED, 'Resources')
OUTPUT = os.path.join(ROOT, 'build', 'WeatherPlayer')
try:
    MAIN_ROOT = os.path.dirname(os.path.abspath(subprocess.run(
        ['git', 'rev-parse', '--path-format=absolute', '--git-common-dir'], cwd=ROOT, capture_output=True,
        text=True, check=True).stdout.strip()))
except (OSError, subprocess.CalledProcessError):
    MAIN_ROOT = ROOT
# The proxy is built in the main repository; a worktree has no build of it.
PROXY = next((p for p in (os.path.join(ROOT, 'build', 'DxgiProxy', 'Release', 'dxgi.dll'),
                          os.path.join(MAIN_ROOT, 'build', 'DxgiProxy', 'Release', 'dxgi.dll')) if os.path.exists(p)),
             os.path.join(ROOT, 'build', 'DxgiProxy', 'Release', 'dxgi.dll'))
REFERENCES = os.path.join(os.path.dirname(MAIN_ROOT), 'References', 'KspUpscaler')
RESOURCE_EXTENSIONS = ('.shader', '.cginc', '.hlsl')
EXCLUDED_PARTS = ('Library', 'obj', 'bin', 'Temp', 'build', '.local', '.git')


def read_manifest(path):
    """The globs of one manifest: its lines without comments and blanks."""
    with open(path, encoding='utf-8') as file:
        lines = [line.strip() for line in file]
    return [line for line in lines if line and not line.startswith('#')]


def resolve(root, manifest_dir):
    """The files the manifests of manifest_dir name, as a list of (relative path, package id), sorted; exits on a glob that matches
    nothing or on two different files of one name (the player's Linked folder is flat)."""
    found = {}
    owner = {}
    for manifest in sorted(glob.glob(os.path.join(manifest_dir, '*.txt'))):
        package = os.path.splitext(os.path.basename(manifest))[0]
        for pattern in read_manifest(manifest):
            matches = [m for m in glob.glob(os.path.join(root, *pattern.split('/')), recursive=True)
                       if os.path.isfile(m) and not any(part in EXCLUDED_PARTS for part in os.path.relpath(m, root).split(os.sep)[:-1])]
            if not matches:
                raise SystemExit('%s: "%s" matches no file' % (os.path.basename(manifest), pattern))
            for match in matches:
                rel = os.path.relpath(match, root).replace(os.sep, '/')
                name = os.path.basename(rel)
                if name in found and found[name] != rel:
                    raise SystemExit('%s: "%s" and "%s" have the same file name (%s names the first, %s the second)'
                                     % (name, found[name], rel, owner[name], package))
                found[name] = rel
                owner[name] = package
    return sorted((rel, owner[os.path.basename(rel)]) for rel in found.values())


def copy_in(root=ROOT, manifest_dir=MANIFESTS, linked=LINKED):
    """Replace the player's Assets/Linked with fresh copies of the manifests' files; shaders into its Resources."""
    files = resolve(root, manifest_dir)
    resources = os.path.join(linked, 'Resources')
    if os.path.isdir(linked):
        shutil.rmtree(linked)
    os.makedirs(resources)
    for rel, _ in files:
        target = resources if rel.lower().endswith(RESOURCE_EXTENSIONS) else linked
        shutil.copy2(os.path.join(root, *rel.split('/')), target)
    return files


def build(unity):
    """Build the player with Unity in batch mode and put the proxy beside it; exits with the log's errors if it fails."""
    exe = os.path.join(OUTPUT, 'WeatherPlayer.exe')
    log = os.path.join(OUTPUT, 'build.log')
    os.makedirs(OUTPUT, exist_ok=True)
    result = subprocess.run([unity, '-batchmode', '-quit', '-projectPath', PROJECT, '-executeMethod',
                             'ReDefinition.WeatherPlayer.BuildWeatherPlayer.Build', '-output', exe, '-logFile', log])
    text = open(log, encoding='utf-8', errors='replace').read() if os.path.exists(log) else ''
    if result.returncode != 0 or 'WeatherPlayer: built' not in text:
        errors = [line for line in text.splitlines() if 'error' in line.lower()]
        print('\n'.join(errors[-30:]))
        raise SystemExit('the player did not build (%s)' % log)
    return exe


def write_ini(streamline=None, amd=None, xess=None):
    """The proxy's ini beside the player: on, without frame generation unless Streamline's folder is given."""
    lines = ['[Proxy]', 'enabled=1']
    if streamline:
        lines += ['frameGeneration=1', 'frameGenerationTechnique=dlss', 'streamlineDirectory=' + streamline,
                  'dlssDirectory=' + streamline]
    else:
        lines += ['frameGeneration=0']
    if amd:
        lines.append('amdUpscalerDirectory=' + amd)
    if xess:
        lines.append('xessDirectory=' + xess)
    with open(os.path.join(OUTPUT, 'ReDefinitionProxy.ini'), 'w') as file:
        file.write('\n'.join(lines) + '\n')


def run_lines(exe, label, extra, size=(1280, 720), timeout=1200):
    """Run the player once (20 minutes at most); the WEATHERPLAYER lines of its log, or None where it did not finish."""
    log = os.path.join(OUTPUT, label + '.log')
    arguments = [exe, '-logFile', log, '-screen-width', str(size[0]), '-screen-height', str(size[1]),
                 '-screen-fullscreen', '0'] + list(extra)
    try:
        subprocess.run(arguments, timeout=timeout)
    except subprocess.TimeoutExpired:
        print('%s: the player did not finish within %d seconds' % (label, timeout))
        return None
    if not os.path.exists(log):
        return None
    return [line[len('WEATHERPLAYER '):].rstrip() for line in open(log, encoding='utf-8', errors='replace').read().splitlines()
            if line.startswith('WEATHERPLAYER ')]


def run(exe, label, extra, size=(1280, 720), timeout=1200):
    """Run the player once, print its WEATHERPLAYER lines; True where it ended with all checks passed."""
    lines = run_lines(exe, label, extra, size, timeout)
    if lines is None:
        return False
    print('--- ' + label)
    print('\n'.join(lines))
    return any(line == 'all checks passed' for line in lines)


def fresh(label):
    """An empty output folder for the run."""
    folder = os.path.join(OUTPUT, label)
    if os.path.isdir(folder):
        shutil.rmtree(folder)
    os.makedirs(folder)
    return folder


def stills(exe, sweep):
    """The four views in the variants; the frames into OUTPUT/stills (or stills-<sweep>)."""
    write_ini()
    label = 'stills' if not sweep else 'stills-' + sweep.split('=')[0]
    extra = ['-mode', 'stills', '-out', fresh(label)]
    if sweep:
        extra += ['-sweep', sweep]
    return run(exe, label, extra)


def timing(exe, ksp):
    """The GPU time of the slab's passes per view at the render size of DLSS Quality at 2560x1440, and at 2560x1440."""
    write_ini()
    ok = run(exe, 'time-1707', ['-mode', 'time', '-out', fresh('time-1707'), '-width', '1707', '-height', '960'], (1707, 960))
    ok = run(exe, 'time-2560', ['-mode', 'time', '-out', fresh('time-2560'), '-width', '2560', '-height', '1440'], (2560, 1440)) and ok
    return ok


def turns(exe, ksp, only):
    """The camera turn at the 25 km view through each upscaler, the slab with and without its analytic motion, and without it."""
    streamline = ksp
    amd = os.path.join(REFERENCES, 'FidelityFX-SDK', 'Kits', 'FidelityFX', 'signedbin')
    xess = os.path.join(REFERENCES, 'xess', 'bin')
    write_ini(streamline, amd, xess)
    bundle = os.path.join(ksp, 'GameData', 'ReDefinition', 'Main', 'Shaders', 'redefinition.shaders')
    runs = [('turn-native-slab', ['-upscale', 'native']),
            ('turn-native-noslab', ['-upscale', 'native', '-slab', '0'])]
    for scale in ('0.6666667', '0.5'):
        for upscaler in ('fsr3', 'dlss', 'xess'):
            runs.append(('turn-%s-%s' % (upscaler, scale), ['-upscale', upscaler, '-scale', scale, '-bundle', bundle]))
    for preset in ('K', 'M', 'L'):
        runs.append(('turn-dlss-%s-0.6666667' % preset, ['-upscale', 'dlss', '-scale', '0.6666667', '-preset', preset]))
    # The slab without the analytic motion and depth (Unity's own values, which have none for the slab), and without the slab.
    runs.append(('turn-dlss-noanalytic-0.6666667', ['-upscale', 'dlss', '-scale', '0.6666667', '-analytic', '0']))
    runs.append(('turn-dlss-noslab-0.6666667', ['-upscale', 'dlss', '-scale', '0.6666667', '-slab', '0']))
    runs.append(('turn-fsr3-noanalytic-0.6666667', ['-upscale', 'fsr3', '-scale', '0.6666667', '-analytic', '0', '-bundle', bundle]))
    runs.append(('turn-fsr3-noslab-0.6666667', ['-upscale', 'fsr3', '-scale', '0.6666667', '-slab', '0', '-bundle', bundle]))
    ok = True
    for label, extra in runs:
        if only and only not in label:
            continue
        ok = run(exe, label, ['-mode', 'turn', '-view', 'far', '-out', fresh(label)] + extra) and ok
    return ok


# ---- series -------------------------------------------------------------------------------------------------------------------

GPU_LOCK = os.path.join(HERE, 'gpu_lock.py')


def under_lock(kind, who, max_minutes, exclusive, command):
    """Runs a command inside the lock of that kind (tools/gpu_lock.py run), waiting while another holder has it."""
    lock = [sys.executable, GPU_LOCK, 'run', '--who', who, '--max-minutes', str(max_minutes), '--kind', kind]
    if exclusive:
        lock.append('--exclusive')
    return subprocess.run(lock + ['--'] + command).returncode


def run_series_inner(name, ksp, who):
    """Runs the stages of a series (already inside the GPU lock), collects its metrics and prints the table. Returns 0 when all
    passed."""
    import weather_metrics
    series = weather_metrics.discover()
    if name not in series:
        raise SystemExit('no series %s; the series are %s' % (name, ', '.join(sorted(series))))
    spec = series[name]
    exe = os.path.join(OUTPUT, 'WeatherPlayer.exe')
    if not os.path.exists(exe):
        raise SystemExit('the player is not built (%s)' % exe)
    shutil.copy2(PROXY, OUTPUT)
    folders, player_lines, stage_ok = {}, {}, {}
    for stage in spec['stages']:
        label = '%s-%s' % (name, stage.label)
        if stage.upscaler:
            write_ini(ksp)
        else:
            write_ini()
        folder = fresh(label)
        lines = run_lines(exe, label, ['-out', folder] + stage.extra, stage.size, stage.timeout)
        folders[stage.label] = folder
        player_lines[stage.label] = lines or []
        stage_ok[stage.label] = lines is not None and any(line == 'all checks passed' for line in lines)
        print('stage %s: %s' % (stage.label, 'ended' if stage_ok[stage.label] else 'FAILED'))
    metrics = spec['collect'](weather_metrics.Context(name, folders, player_lines, stage_ok))
    for label, ok in stage_ok.items():
        metrics.append(weather_metrics.Metric.flag('stage.' + label, ok))
    print(weather_metrics.table(metrics))
    out = os.path.join(ROOT, 'build', 'weather-series')
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, '%s-%s.json' % (name, time.strftime('%Y%m%d-%H%M%S'))), 'w') as file:
        json.dump([m.as_dict() for m in metrics], file, indent=1)
    failed = [m.name for m in metrics if not m.passed()]
    print('Series %s: %s' % (name, 'PASSED' if not failed else 'FAILED (%s)' % ', '.join(failed)))
    return 0 if not failed else 1


def run_series(name, unity, ksp, no_build, who):
    """A series from the outside: the build in the Unity lock, the runs in the GPU lock (exclusive for a timing series)."""
    import weather_metrics
    series = weather_metrics.discover()
    if name not in series:
        raise SystemExit('no series %s; the series are %s' % (name, ', '.join(sorted(series))))
    script = os.path.abspath(__file__)
    if not no_build:
        code = under_lock('unity', who, 20, False, [sys.executable, script, '--stage', 'build', '--unity', unity])
        if code != 0:
            return code
    return under_lock('gpu', who, 20, series[name]['exclusive'], [sys.executable, script, '--series-inner', name, '--ksp', ksp, '--who', who])


def selftest():
    """The manifest mechanism with a dummy package: a manifest and a source file, no edit of this script."""
    root = tempfile.mkdtemp(prefix='weather-linked-selftest-')
    try:
        manifests = os.path.join(root, 'tests', 'WeatherPlayer', 'linked')
        os.makedirs(manifests)
        os.makedirs(os.path.join(root, 'src', 'Dummy', 'Deep'))
        os.makedirs(os.path.join(root, 'unity', 'Shaders'))
        open(os.path.join(root, 'src', 'Dummy', 'Deep', 'Noise.cs'), 'w').write('class Noise {}')
        open(os.path.join(root, 'unity', 'Shaders', 'Dummy.shader'), 'w').write('Shader "Dummy" {}')
        with open(os.path.join(manifests, 'Z99.txt'), 'w') as file:
            file.write('# a dummy package\nsrc/Dummy/**/Noise.cs\nunity/**/Dummy.shader\n')
        linked = os.path.join(root, 'player', 'Assets', 'Linked')
        files = copy_in(root, manifests, linked)
        assert files == [('src/Dummy/Deep/Noise.cs', 'Z99'), ('unity/Shaders/Dummy.shader', 'Z99')], files
        assert os.path.exists(os.path.join(linked, 'Noise.cs')) and os.path.exists(os.path.join(linked, 'Resources', 'Dummy.shader'))
        # A git mv of the source keeps the line valid.
        os.makedirs(os.path.join(root, 'src', 'Moved'))
        os.replace(os.path.join(root, 'src', 'Dummy', 'Deep', 'Noise.cs'), os.path.join(root, 'src', 'Dummy', 'Noise.cs'))
        assert [f for f, _ in resolve(root, manifests)][0] == 'src/Dummy/Noise.cs'
        # A glob that matches nothing is an error; so are two files of one name.
        with open(os.path.join(manifests, 'Z98.txt'), 'w') as file:
            file.write('src/Nowhere/**/Missing.cs\n')
        try:
            resolve(root, manifests)
            raise AssertionError('an empty glob was accepted')
        except SystemExit as e:
            assert 'matches no file' in str(e)
        os.remove(os.path.join(manifests, 'Z98.txt'))
        open(os.path.join(root, 'src', 'Moved', 'Noise.cs'), 'w').write('class Noise2 {}')
        with open(os.path.join(manifests, 'Z98.txt'), 'w') as file:
            file.write('src/Moved/Noise.cs\n')
        try:
            resolve(root, manifests)
            raise AssertionError('two files of one name were accepted')
        except SystemExit as e:
            assert 'same file name' in str(e)
        # The real manifests resolve in this repository.
        real = resolve(ROOT, MANIFESTS)
        assert any(rel.endswith('ProxyDll.cs') for rel, _ in real)
        print('selftest passed (%d linked files in %d manifests)' % (len(real), len(glob.glob(os.path.join(MANIFESTS, '*.txt')))))
    finally:
        shutil.rmtree(root, ignore_errors=True)


def main():
    """Build the player unless --no-build, then run the stage or the series."""
    parser = argparse.ArgumentParser()
    parser.add_argument('--unity', default=r'C:\Program Files\Unity\Editor 2019.4.18f1\Editor\Unity.exe')
    parser.add_argument('--ksp', default=os.environ.get(
        'KspRoot', r'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program'))
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--stage', choices=['stills', 'time', 'turn', 'all', 'build'], default='all')
    parser.add_argument('--only', help='only the turn runs whose label contains this')
    parser.add_argument('--sweep', help='a slab field and its values for the stills, name=a,b,c')
    parser.add_argument('--series', help='run a series of tools/weather_metrics under the locks and print its table')
    parser.add_argument('--series-inner', help=argparse.SUPPRESS)
    parser.add_argument('--who', default='B11')
    parser.add_argument('--list-linked', action='store_true')
    parser.add_argument('--selftest', action='store_true')
    arguments = parser.parse_args()
    if arguments.selftest:
        selftest()
        return
    if arguments.list_linked:
        for rel, package in resolve(ROOT, MANIFESTS):
            print('%-6s %s' % (package, rel))
        return
    if arguments.series_inner:
        sys.exit(run_series_inner(arguments.series_inner, arguments.ksp, arguments.who))
    if arguments.series:
        sys.exit(run_series(arguments.series, arguments.unity, arguments.ksp, arguments.no_build, arguments.who))
    exe = os.path.join(OUTPUT, 'WeatherPlayer.exe')
    if not arguments.no_build:
        copy_in()
        exe = build(arguments.unity)
    os.makedirs(OUTPUT, exist_ok=True)
    shutil.copy2(PROXY, OUTPUT)
    if arguments.stage == 'build':
        return
    ok = True
    if arguments.stage in ('stills', 'all'):
        ok = stills(exe, arguments.sweep) and ok
    if arguments.stage in ('time', 'all'):
        ok = timing(exe, arguments.ksp) and ok
    if arguments.stage in ('turn', 'all'):
        ok = turns(exe, arguments.ksp, arguments.only) and ok
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
