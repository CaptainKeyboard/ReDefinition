"""GPU time per pass of a RenderDoc capture, to see where a frame's GPU time goes.
Runs inside RenderDoc's own Python:

    set RDC=<capture.rdc>& set RDC_OUT=<report.txt>
    qrenderdoc.exe --python tools\\renderdoc_gpu_times.py

Each action's EventGPUDuration (RenderDoc replays the frame and times every draw,
dispatch and copy), summed three ways: by the marker the action stands under (Unity's
camera and pass markers, e.g. Camera.Render > Drawing > Render.OpaqueGeometry), by the
pixel or compute shader it ran, and by its render target. The report lists each, most
costly first, with its share of the frame's GPU time.
"""
import os
import traceback
from collections import defaultdict

import renderdoc as rd

CAPTURE = os.environ['RDC']
OUT = os.environ['RDC_OUT']
out = open(OUT, 'w')


def log(line):
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
        target = '-'
        for o in action.outputs:
            if o != rd.ResourceId.Null():
                target = names.get(o, str(o))
                break
        by_target[target] += t

    log('frame GPU time %.3f ms over %d actions' % (total * 1000.0, len(leaves)))
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
