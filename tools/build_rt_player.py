"""Builds and runs the raytracing test player (docs/development/raytracing.md,
"Testing without the game").

It copies ReDefinition's raytracing sources and shaders into the player's Unity
project, has Unity 2019.4.18f1 build the player in batch mode, puts the proxy
(dxgi.dll) beside it, and runs it four times: with forward rendering, with Unity's own
deferred lighting, with Deferred's lighting shaders from the installed game (skipped
where Deferred is not installed), and the budget at DLSS Quality's render size. The
player's checks come back from its log.

    python tools/build_rt_player.py [--unity <Unity.exe>] [--ksp <KSP folder>] [--no-build]

Exits 0 when every check of every run passed.
"""

import argparse
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, 'tests', 'RaytracingPlayer')
LINKED = os.path.join(PROJECT, 'Assets', 'Linked')
RESOURCES = os.path.join(LINKED, 'Resources')
OUTPUT = os.path.join(ROOT, 'build', 'RaytracingPlayer')
# The folder the main checkout lies in, also from a git worktree of it, beside which
# References holds the vendors' SDKs.
try:
    MAIN_ROOT = os.path.dirname(os.path.abspath(subprocess.run(
        ['git', 'rev-parse', '--path-format=absolute', '--git-common-dir'], cwd=ROOT, capture_output=True,
        text=True, check=True).stdout.strip()))
except (OSError, subprocess.CalledProcessError):
    MAIN_ROOT = ROOT
PROXY = os.path.join(ROOT, 'build', 'DxgiProxy', 'Release', 'dxgi.dll')

SOURCES = [
    'src/Proxy/ProxyDll.cs',
    'src/Proxy/ProxyEvents.cs',
    'src/Proxy/RtNative.cs',
    'src/Raytracing/RtScene.cs',
    'src/Raytracing/RtMaterial.cs',
    'src/Raytracing/RtMaterialIntent.cs',
    'src/Raytracing/RtForwardHighlight.cs',
    'src/Raytracing/RtTerrainColour.cs',
    'src/Raytracing/RtStockTerrain.cs',
    'src/Raytracing/RtDeferredLook.cs',
    'src/Raytracing/RtLighting.cs',
    # RtLighting asks ForeignAssumptions before it takes Deferred's shader.
    'src/Core/CommandLine.cs',
    'src/Core/ForeignCode.cs',
    'src/Core/ForeignAssumption.cs',
    'src/Core/ForeignAssumptions.cs',
    'src/Core/TypeLookup.cs',
    'src/Raytracing/RtCamera.cs',
    'src/Proxy/PacketRing.cs',
    'src/Proxy/StreamlineCamera.cs',
    # The upscalers, for the turn through each (RtTurnUpscale.cs).
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
    'src/Upscaler/Rig/RcasSharpener.cs',
]
SHADERS = [
    'unity/Assets/ReDefinition/Shaders/ReDefinitionRtCapture.shader',
    'unity/Assets/ReDefinition/Shaders/ReDefinitionMoonGlow.shader',
    'unity/Assets/ReDefinition/Shaders/ReDefinitionScreenSpaceShadows.shader',
    'unity/Assets/ReDefinition/Shaders/ReDefinitionDeferredReflections.shader',
    'unity/Assets/ReDefinition/Shaders/ReDefinitionRtReproject.cginc',
    'unity/Assets/ReDefinition/Shaders/ReDefinitionRtForwardLight.shader',
    'unity/Assets/ReDefinition/Shaders/ReDefinitionRtScatter.compute',
]

def copy_in():
    """Replace the player's Assets/Linked with fresh copies of SOURCES, and the SHADERS in its Resources."""
    if os.path.isdir(LINKED):
        shutil.rmtree(LINKED)
    os.makedirs(RESOURCES)
    for source in SOURCES:
        shutil.copy2(os.path.join(ROOT, source), LINKED)
    # In Resources, so the player carries them and Shader.Find finds them.
    for shader in SHADERS:
        shutil.copy2(os.path.join(ROOT, shader), RESOURCES)



def build(unity):
    """Build the player with Unity in batch mode and put the proxy beside it; exits with the log's errors if it fails."""
    exe = os.path.join(OUTPUT, 'RaytracingPlayer.exe')
    log = os.path.join(OUTPUT, 'build.log')
    os.makedirs(OUTPUT, exist_ok=True)
    result = subprocess.run([unity, '-batchmode', '-quit', '-projectPath', PROJECT, '-executeMethod',
                             'ReDefinition.RaytracingPlayer.BuildPlayer.Build', '-output', exe, '-logFile', log])
    text = open(log, encoding='utf-8', errors='replace').read() if os.path.exists(log) else ''
    if result.returncode != 0 or 'RaytracingPlayer: built' not in text:
        errors = [line for line in text.splitlines() if 'error' in line.lower()]
        print('\n'.join(errors[-30:]))
        raise SystemExit('the player did not build (%s)' % log)
    shutil.copy2(PROXY, OUTPUT)
    return exe


def run(exe, deferred_folder, label, extra=(), size=(1280, 720)):
    """Run the player once (five minutes at most), print its RTPLAYER lines and say whether all checks passed."""
    log = os.path.join(OUTPUT, label + '.log')
    out = os.path.join(OUTPUT, label)
    arguments = [exe, '-logFile', log, '-out', out, '-screen-width', str(size[0]), '-screen-height', str(size[1]),
                 '-screen-fullscreen', '0'] + list(extra)
    if deferred_folder:
        arguments += ['-deferred', deferred_folder]
    try:
        subprocess.run(arguments, timeout=300)
    except subprocess.TimeoutExpired:
        print('%s: the player did not finish within 5 minutes' % label)
        return False
    # Unity ends each logged line with a space.
    lines = [line[len('RTPLAYER '):].rstrip() for line in open(log, encoding='utf-8', errors='replace').read().splitlines()
             if line.startswith('RTPLAYER ')]
    print('--- ' + label)
    print('\n'.join(lines))
    return any(line == 'all checks passed' for line in lines)


def turn(exe, streamline, deferred):
    """Record the camera turn in each variant, measure them with analyze_rt_turn.py; 0 when all ran and passed."""
    # Streamline set up on the player's swapchain, as the game's proxy does with DLSS
    # frame generation, so that Ray Reconstruction runs; only for these runs.
    ini = os.path.join(OUTPUT, 'ReDefinitionProxy.ini')
    with open(ini, 'w') as file:
        file.write('[Proxy]' + chr(10) + 'enabled=1' + chr(10) + 'frameGeneration=1' + chr(10)
                   + 'frameGenerationTechnique=dlss' + chr(10) + 'streamlineDirectory=' + streamline + chr(10))
    # Forward without jitter; then as the game renders: the upscaler's jitter, and
    # Deferred's lighting where it is installed.
    runs = [('rr', ['-turn', 'rr']), ('denoiser', ['-turn', 'denoiser']), ('plain', ['-turn', 'plain']),
            ('rr-jitter', ['-turn', 'rr', '-jitter', '1'])]
    # The trace before the lighting of the same frame (RtCamera.TraceBeforeLighting).
    runs += [('rr-early', ['-turn', 'rr', '-early', '1']), ('denoiser-early', ['-turn', 'denoiser', '-early', '1'])]
    if os.path.isdir(deferred):
        runs += [('rr-jitter-deferred', ['-turn', 'rr', '-jitter', '1', '-deferred', deferred]),
                 ('rr-early-jitter-deferred', ['-turn', 'rr', '-early', '1', '-jitter', '1', '-deferred', deferred]),
                 ('deferred-plain', ['-turn', 'plain', '-deferred', deferred])]
    folders = []
    try:
        ok = True
        for label, extra in runs:
            folder = os.path.join(OUTPUT, 'turn-' + label)
            if os.path.isdir(folder):
                shutil.rmtree(folder)
            ok = run(exe, None, 'turn-' + label, extra) and ok
            folders.append(folder)
    finally:
        os.remove(ini)
    result = subprocess.run([sys.executable, os.path.join(ROOT, 'tools', 'analyze_rt_turn.py')] + folders)
    return 0 if ok and result.returncode == 0 else 1


def upscale(exe, streamline, amd, ksp):
    """Record the turn natively and through each upscaler at two scales, measure them with analyze_upscale_turn.py;
    0 when all ran and passed."""
    # The proxy with DLSS from the Streamline SDK's folder and AMD's upscaler, as a
    # player names them; only for these runs.
    ini = os.path.join(OUTPUT, 'ReDefinitionProxy.ini')
    with open(ini, 'w') as file:
        file.write('[Proxy]' + chr(10) + 'enabled=1' + chr(10) + 'frameGeneration=1' + chr(10)
                   + 'frameGenerationTechnique=dlss' + chr(10) + 'streamlineDirectory=' + streamline + chr(10)
                   + 'dlssDirectory=' + streamline + chr(10) + 'amdUpscalerDirectory=' + amd + chr(10))
    bundle = os.path.join(ksp, 'GameData', 'ReDefinition', 'Shaders', 'redefinition.shaders')
    runs = [('upscale-native', ['-upscale', 'native'])]
    # DLSS's own sizes for quality and performance, which the rig renders at.
    for scale in ('0.6666667', '0.5'):
        for upscaler in ('fsr3', 'dlss', 'amd'):
            runs.append(('upscale-%s-%s' % (upscaler, scale), ['-upscale', upscaler, '-scale', scale, '-bundle', bundle]))
    # AMD's upscaler with the masks the rig hands it, empty: as without them.
    runs.append(('upscale-amd-masks-0.5', ['-upscale', 'amd', '-scale', '0.5', '-masks', '1', '-bundle', bundle]))
    folders = []
    try:
        ok = True
        for label, extra in runs:
            folder = os.path.join(OUTPUT, label)
            if os.path.isdir(folder):
                shutil.rmtree(folder)
            ok = run(exe, None, label, ['-turn', 'plain'] + extra) and ok
            folders.append(folder)
    finally:
        os.remove(ini)
    result = subprocess.run([sys.executable, os.path.join(ROOT, 'tools', 'analyze_upscale_turn.py')] + folders)
    return 0 if ok and result.returncode == 0 else 1


def main():
    """Build the player unless --no-build, then run the turn, the upscaler turn, one check or all the runs."""
    parser = argparse.ArgumentParser()
    parser.add_argument('--unity', default=r'C:\Program Files\Unity\Editor\Unity.exe')
    # By default KspRoot, as the build takes it, or the Steam location.
    parser.add_argument('--ksp', default=os.environ.get(
        'KspRoot', r'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program'))
    parser.add_argument('--no-build', action='store_true')
    # One check alone, in the run with Deferred's lighting (RtPlayerRunner, -only).
    parser.add_argument('--only', choices=['ocean', 'glow'])
    parser.add_argument('--turn', action='store_true',
                        help='record the camera turn (RtTurnRunner) through DLSS Ray Reconstruction, the denoiser and'
                             ' without raytracing, and measure it with analyze_rt_turn.py, instead of the checks')
    parser.add_argument('--upscale', action='store_true',
                        help='record the camera turn through FSR 3, DLSS and the AMD upscaler at two render scales, and'
                             ' natively, and measure them with analyze_upscale_turn.py, instead of the checks')
    parser.add_argument('--amd', default=os.path.join(os.path.dirname(MAIN_ROOT), 'References', 'KspUpscaler', 'FSR4'),
                        help='the folder with amd_fidelityfx_upscaler_dx12.dll')
    parser.add_argument('--streamline', default=os.environ.get('REDEFINITION_STREAMLINE_DIR', os.path.join(
        os.path.dirname(MAIN_ROOT), 'References', 'KspUpscaler', 'streamline-sdk-v2.14.16', 'bin', 'x64')))
    arguments = parser.parse_args()
    exe = os.path.join(OUTPUT, 'RaytracingPlayer.exe')
    if not arguments.no_build:
        copy_in()
        exe = build(arguments.unity)
    else:
        shutil.copy2(PROXY, OUTPUT)
    if arguments.upscale:
        sys.exit(upscale(exe, arguments.streamline, arguments.amd, arguments.ksp))
    if arguments.turn:
        sys.exit(turn(exe, arguments.streamline, os.path.join(arguments.ksp, 'GameData', 'zzz_Deferred', 'Shaders')))
    deferred = os.path.join(arguments.ksp, 'GameData', 'zzz_Deferred', 'Shaders')
    if arguments.only:
        sys.exit(0 if run(exe, deferred if os.path.isdir(deferred) else None, 'deferred-' + arguments.only,
                          ['-only', arguments.only]) else 1)
    forward_ok = run(exe, None, 'forward')
    builtin_ok = run(exe, None, 'unity-deferred', ['-builtinDeferred', '1'])
    if os.path.isdir(deferred):
        deferred_ok = run(exe, deferred, 'deferred')
    else:
        print('--- deferred: skipped, Deferred is not installed (%s)' % deferred)
        deferred_ok = True
    # 2560 by 1440 with DLSS Quality renders 1707 by 960.
    budget_ok = run(exe, None, 'budget', ['-budget', '1'], (1707, 960))
    sys.exit(0 if forward_ok and builtin_ok and deferred_ok and budget_ok else 1)


if __name__ == '__main__':
    main()
