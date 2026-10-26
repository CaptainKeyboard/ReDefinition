"""GPU time per pass of a RenderDoc capture, to see where a frame's GPU time goes.
It runs inside RenderDoc's own Python.

Each action's EventGPUDuration (RenderDoc replays the frame and times every draw,
dispatch and copy) is summed three ways. The ways are by the marker the action stands
under (Unity's camera and pass markers, e.g. Camera.Render > Drawing >
Render.OpaqueGeometry), by the pixel or compute shader it ran, and by its render
target. The report lists each, most costly first, with its share of the frame's GPU
time. It also lists the sums by the mod a shader belongs to, by its name (MODS), with
the draws and the time a draw.

RenderDoc times each action on its own, with the GPU idle around it. Many small draws
come out dearer than they are in the game (605 depth-only draws of a vessel took 1.7 ms
here and under 0.04 ms in the game, GpuProfiler). The share of a mod with few large
draws is the one to go by; for many small ones, the game's own measurement.

Usage, inside RenderDoc:

    set RDC=<capture.rdc>& set RDC_OUT=<report.txt>
    qrenderdoc.exe --python tools\\renderdoc_gpu_times.py
"""
import os
import traceback
from collections import defaultdict

import renderdoc as rd

# A shader's mod, by the start of its name; the first that fits.
MODS = [
    ('Parallax', ('Custom/Parallax', 'TerrainScatters', 'Parallax')),
    ('EVE', ('Scatterer-EVE/', 'EVE/')),
    ('Scatterer', ('Scatterer/', 'Proland/', 'Scatterer')),
    ('Deferred', ('Deferred/',)),
    ('ReDefinition', ('Hidden/ReDefinition', 'ReDefinition', 'ffx_')),
    ('TUFX', ('Hidden/PostProcessing',)),
    ('Waterfall', ('Waterfall',)),
    ('KSP and Unity', ('KSP/', 'Legacy Shaders/', 'Standard', 'UI/', 'TextMeshPro', 'Hidden/', 'Unlit/', 'Particles/', 'GenerateHiZ')),
]


def mod_of(shader):
    """The mod a shader belongs to by MODS; copies, clears and actions without a shader are named apart."""
    for mod, prefixes in MODS:
        if shader.startswith(prefixes):
            return mod
    return 'other (' + shader + ')' if shader in ('copy', 'clear', '-') else 'other'

CAPTURE = os.environ['RDC']
OUT = os.environ['RDC_OUT']
out = open(OUT, 'w')


def log(line):
    """Write a line to the output file at once, so a crash of the replay leaves what came before."""
    out.write(line + '\n')
    out.flush()


try:
    capture = rd.OpenCaptureFile()
    assert capture.OpenFile(CAPTURE, '', None) == rd.ResultCode.Succeeded, 'cannot open ' + CAPTURE
    status, ctl = capture.OpenCapture(rd.ReplayOptions(), None)
    assert status == rd.ResultCode.Succeeded, 'cannot replay ' + CAPTURE
    names = {r.resourceId: r.name for r in ctl.GetResources()}
    sdfile = ctl.GetStructuredFile()

    leaves = []

    def walk(action, path):
        name = action.GetName(sdfile)
        if action.children:
            for child in action.children:
                walk(child, path + [name])
        else:
            leaves.append((action, path))

    for root in ctl.GetRootActions():
        walk(root, [])

    counters = ctl.EnumerateCounters()
    assert rd.GPUCounter.EventGPUDuration in counters, 'no GPU duration counter'
    results = ctl.FetchCounters([rd.GPUCounter.EventGPUDuration])
    seconds = {}
    for r in results:
        seconds[r.eventId] = r.value.d

    by_marker = defaultdict(float)
    by_shader = defaultdict(float)
    by_target = defaultdict(float)
    by_mod = defaultdict(float)
    count_mod = defaultdict(int)
    count_shader = defaultdict(int)
    total = 0.0
    for action, path in leaves:
        t = seconds.get(action.eventId, 0.0)
        if t <= 0.0:
            continue
        total += t
        marker = ' > '.join(path[-3:]) if path else '(no marker)'
        by_marker[marker] += t
        shader = '-'
        if action.flags & (rd.ActionFlags.Drawcall | rd.ActionFlags.Dispatch):
            ctl.SetFrameEvent(action.eventId, False)
            state = ctl.GetPipelineState()
            stage = rd.ShaderStage.Compute if action.flags & rd.ActionFlags.Dispatch else rd.ShaderStage.Pixel
            reflection = state.GetShaderReflection(stage)
            if reflection is not None:
                shader = names.get(reflection.resourceId, str(reflection.resourceId))
        elif action.flags & rd.ActionFlags.Copy:
            shader = 'copy'
        elif action.flags & rd.ActionFlags.Clear:
            shader = 'clear'
        by_shader[shader] += t
        count_shader[shader] += 1
        by_mod[mod_of(shader)] += t
        count_mod[mod_of(shader)] += 1
        target = '-'
        for o in action.outputs:
            if o != rd.ResourceId.Null():
                target = names.get(o, str(o))
                break
        by_target[target] += t

    log('frame GPU time %.3f ms over %d actions' % (total * 1000.0, len(leaves)))
    log('')
    log('== by mod (draws, ms a draw)')
    for key, t in sorted(by_mod.items(), key=lambda kv: -kv[1]):
        log('%8.3f ms %5.1f %%  %s (%d, %.4f)' % (t * 1000.0, 100.0 * t / total, key, count_mod[key], t * 1000.0 / count_mod[key]))
    for title, table in (('by marker', by_marker), ('by shader', by_shader), ('by target', by_target)):
        log('')
        log('== ' + title)
        for key, t in sorted(table.items(), key=lambda kv: -kv[1])[:40]:
            extra = ' (%d)' % count_shader[key] if table is by_shader else ''
            log('%8.3f ms %5.1f %%  %s%s' % (t * 1000.0, 100.0 * t / total, key, extra))
    ctl.Shutdown()
    capture.Shutdown()
    log('DONE')
except Exception:
    log(traceback.format_exc())
out.close()
os._exit(0)
