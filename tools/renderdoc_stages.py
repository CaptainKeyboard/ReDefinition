"""Saves the scene colour of a RenderDoc capture after each stage of the frame, for
tools/analyze_renderdoc_pair.py. Runs inside RenderDoc's own Python:

    set RDC=<capture.rdc>& set RDC_TAG=with& set RDC_DIR=<folder>
    qrenderdoc.exe --python tools\\renderdoc_stages.py

A stage is a run of draws by one shader into the scene camera's colour target
(TempBuffer 57, Unity's name for it in KSP) or ReDefinition's colour after the
upscaler: the target is saved after the last draw of each run, as <tag>_<nn>.dds,
with <tag>.txt naming each (index, event, shader). The scene's depth after the
lighting goes to <tag>_depth.dds, the frame as it went to the screen to
<tag>_final.png. Shader names are Unity's, as RenderDoc shows them.
"""
import os
import traceback

import renderdoc as rd

CAPTURE = os.environ['RDC']
TAG = os.environ['RDC_TAG']
FOLDER = os.environ['RDC_DIR']
SCENE_TARGET = 'TempBuffer 57 2560x1440'
AFTER_UPSCALER = 'ReDefinition_TufxAfterFsr'

os.makedirs(FOLDER, exist_ok=True)
out = open(os.path.join(FOLDER, TAG + '.txt'), 'w')


def log(line):
    out.write(line + '\n')
    out.flush()


try:
    capture = rd.OpenCaptureFile()
    assert capture.OpenFile(CAPTURE, '', None) == rd.ResultCode.Succeeded, 'cannot open ' + CAPTURE
    status, ctl = capture.OpenCapture(rd.ReplayOptions(), None)
    assert status == rd.ResultCode.Succeeded, 'cannot replay ' + CAPTURE
    textures = {t.resourceId: t for t in ctl.GetTextures()}
    names = {r.resourceId: r.name for r in ctl.GetResources()}
    actions = []

    def walk(action):
        if action.children:
            for child in action.children:
                walk(child)
        else:
            actions.append(action)

    for root in ctl.GetRootActions():
        walk(root)

    colour = [r for r in textures if names.get(r, '') == SCENE_TARGET and textures[r].format.compByteWidth == 2]
    depth = [r for r in textures if names.get(r, '') == SCENE_TARGET and textures[r].format.compByteWidth != 2]
    targets = set(colour + [r for r in textures if names.get(r, '') == AFTER_UPSCALER])

    writes = []
    for action in actions:
        outputs = set(o for o in action.outputs if o != rd.ResourceId.Null())
        if action.flags & rd.ActionFlags.Copy:
            outputs = {action.copyDestination}
        hit = outputs & targets
        if hit and action.flags & (rd.ActionFlags.Drawcall | rd.ActionFlags.Copy):
            writes.append((action, list(hit)[0]))

    stages = []
    for action, target in writes:
        ctl.SetFrameEvent(action.eventId, True)
        shader = ctl.GetPipelineState().GetShaderReflection(rd.ShaderStage.Pixel)
        name = 'copy' if action.flags & rd.ActionFlags.Copy or shader is None else names.get(shader.resourceId, '?')
        if stages and stages[-1][1] == name and stages[-1][2] == target:
            stages[-1] = (action.eventId, name, target)
        else:
            stages.append((action.eventId, name, target))

    def save(event, resource, file_name):
        ctl.SetFrameEvent(event, True)
        settings = rd.TextureSave()
        settings.resourceId = resource
        settings.destType = rd.FileType.DDS
        settings.typeCast = rd.CompType.Float
        return ctl.SaveTexture(settings, os.path.join(FOLDER, file_name))

    log('stages %d' % len(stages))
    for i, (event, name, target) in enumerate(stages):
        log('%02d %d %s %s %s' % (i, event, name, names.get(target), save(event, target, '%s_%02d.dds' % (TAG, i))))
    lighting = [event for event, name, _ in stages if 'DeferredShading' in name]
    if depth:
        log('depth %s' % save(lighting[-1] if lighting else stages[0][0], depth[0], TAG + '_depth.dds'))
    presents = [a for a in actions if any(names.get(o, '').startswith('SwapChain-BackBuffer') for o in a.outputs)]
    if presents:
        settings = rd.TextureSave()
        settings.resourceId = [o for o in presents[0].outputs if o != rd.ResourceId.Null()][0]
        settings.destType = rd.FileType.PNG
        ctl.SetFrameEvent(presents[-1].eventId, True)
        log('final %s' % ctl.SaveTexture(settings, os.path.join(FOLDER, TAG + '_final.png')))
    ctl.Shutdown()
    capture.Shutdown()
    log('DONE')
except Exception:
    log(traceback.format_exc())
out.close()
os._exit(0)
