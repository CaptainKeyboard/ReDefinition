"""Builds and runs the weather test player (tests/WeatherPlayer), the spike of the atmosphere plan.

It copies the sources the player links (the proxy bridges, FSR 3's port, the storm noise and volume maths) and the shaders
(ReDefinition's own cloud slab, the storm curtain, the raytracing capture shader) into the player's Unity project and has
Unity 2019.4.18f1 build it in batch mode, as tools/build_rt_player.py does for the raytracing player. It puts the proxy
(dxgi.dll) beside the player and runs it:

  stills  the four views without the cell, with the curtain, the slab and both (colour, depth, motion frames)
  time    the GPU milliseconds of the slab's passes and the curtain through the proxy's timestamp queries
  turn    the camera turn through each upscaler (native, FSR 3, DLSS K/M/L, XeSS, AMD) with references

tools/analyze_weather_player.py measures the frames and prints the WEATHERPLAYER lines. KSP must not be running.

Usage: python tools/build_weather_player.py [--unity <Unity.exe>] [--ksp <KSP folder>] [--no-build] [--stage stills|time|turn|all]
                                            [--out <folder>] [--only <label>] [--sweep name=a,b,c]
"""

import argparse
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, 'tests', 'WeatherPlayer')
LINKED = os.path.join(PROJECT, 'Assets', 'Linked')
RESOURCES = os.path.join(LINKED, 'Resources')
OUTPUT = os.path.join(ROOT, 'build', 'WeatherPlayer')
try:
    MAIN_ROOT = os.path.dirname(os.path.abspath(subprocess.run(
        ['git', 'rev-parse', '--path-format=absolute', '--git-common-dir'], cwd=ROOT, capture_output=True,
        text=True, check=True).stdout.strip()))
except (OSError, subprocess.CalledProcessError):
    MAIN_ROOT = ROOT
PROXY = os.path.join(ROOT, 'build', 'DxgiProxy', 'Release', 'dxgi.dll')
REFERENCES = os.path.join(os.path.dirname(MAIN_ROOT), 'References', 'KspUpscaler')

SOURCES = [
    'src/Proxy/ProxyDll.cs',
    'src/Proxy/ProxyEvents.cs',
    'src/Proxy/RtNative.cs',
    'src/Proxy/PacketRing.cs',
    'src/Proxy/StreamlineCamera.cs',
    'src/Proxy/NativeText.cs',
    'src/Proxy/NativeUpscalerLink.cs',
    'src/Proxy/DlssBridge.cs',
    'src/Proxy/AmdUpscalerBridge.cs',
    'src/Proxy/XessUpscalerBridge.cs',
    'src/Fsr3/Fsr3Constants.cs',
    'src/Fsr3/Fsr3ShaderIDs.cs',
    'src/Fsr3/Fsr3Upscaler.cs',
    'src/Fsr3/Fsr3UpscalerShaders.cs',
    'src/Fsr3/Fsr3UpscalerContext.cs',
    'src/Fsr3/Fsr3UpscalerPass.cs',
    'src/Fsr3/Fsr3UpscalerResources.cs',
    'src/Fsr3/ResourceView.cs',
    'src/Common/CommandLine.cs',
    'src/Seasons/StormNoise.cs',
    'src/Seasons/Mathx.cs',
    'src/Seasons/StormVolumeMath.cs',
    # What the linked code names of the raytracing player's runner: the profiler's markers, the proxy's refusal, the rig's sharpness.
    'tests/RaytracingPlayer/Assets/Runner/GameStubs.cs',
    'tests/RaytracingPlayer/Assets/Runner/UpscalerRigStub.cs',
]
SHADERS = [
    'unity/Assets/ReDefinition/Shaders/ReDefinitionRtCapture.shader',
    'unity/Assets/ReDefinitionSeasons/Shaders/ReDefinitionSeasonsStormVolume.shader',
    'unity/Assets/ReDefinitionSeasons/Shaders/ReDefinitionSeasonsCloudSlab.shader',
]


def copy_in():
    """Replace the player's Assets/Linked with fresh copies of SOURCES, and the SHADERS in its Resources."""
    if os.path.isdir(LINKED):
        shutil.rmtree(LINKED)
    os.makedirs(RESOURCES)
    for source in SOURCES:
        shutil.copy2(os.path.join(ROOT, source), LINKED)
    for shader in SHADERS:
        shutil.copy2(os.path.join(ROOT, shader), RESOURCES)


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


def run(exe, label, extra, size=(1280, 720), timeout=1200):
    """Run the player once (20 minutes at most), print its WEATHERPLAYER lines; True where it ended with all checks passed."""
    log = os.path.join(OUTPUT, label + '.log')
    arguments = [exe, '-logFile', log, '-screen-width', str(size[0]), '-screen-height', str(size[1]),
                 '-screen-fullscreen', '0'] + list(extra)
    try:
        subprocess.run(arguments, timeout=timeout)
    except subprocess.TimeoutExpired:
        print('%s: the player did not finish within %d seconds' % (label, timeout))
        return False
    lines = [line[len('WEATHERPLAYER '):].rstrip() for line in open(log, encoding='utf-8', errors='replace').read().splitlines()
             if line.startswith('WEATHERPLAYER ')]
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


def main():
    """Build the player unless --no-build, then run the stage."""
    parser = argparse.ArgumentParser()
    parser.add_argument('--unity', default=r'C:\Program Files\Unity\Editor 2019.4.18f1\Editor\Unity.exe')
    parser.add_argument('--ksp', default=os.environ.get(
        'KspRoot', r'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program'))
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--stage', choices=['stills', 'time', 'turn', 'all', 'build'], default='all')
    parser.add_argument('--only', help='only the turn runs whose label contains this')
    parser.add_argument('--sweep', help='a slab field and its values for the stills, name=a,b,c')
    arguments = parser.parse_args()
    exe = os.path.join(OUTPUT, 'WeatherPlayer.exe')
    if not arguments.no_build:
        copy_in()
        exe = build(arguments.unity)
    os.makedirs(OUTPUT, exist_ok=True)
    shutil.copy2(PROXY, OUTPUT)
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
