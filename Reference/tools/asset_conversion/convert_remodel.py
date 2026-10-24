# convert_remodel.py
# A remodelled room's new .aem files -> the .fbx + .gof2mesh.json pairs in Assets/Models (the Vossk hangar, 2026-10-24).
# aem2fbx.py writes the FBX; its own sidecar is the conversion's first format ('animation' blocks), which the game no
# longer reads, so this writes the one PartAnimation reads: per part bsphere and 'channels' {target, keys [{t, v}]}
# with targets posX/Y/Z, rotX/Y/Z, sclX/Y/Z (vector keys split per axis), extra, v5_0..v5_6, raw file values.
#
# Usage (Blender 4+ / 5):
#   blender -b --factory-startup --python convert_remodel.py -- <dir with the .aem files> <out dir> <source prefix> <name> [<name> ...]
#   e.g. ... -- unpacked/ out/ assets/main/3d/meshes/hangars/ hangar_vossk hangar_vossk_anim_add
# Then copy the pairs over the old ones in Assets/Models (keep the .meta files: same GUIDs, the prefabs keep working)
# and give new parts their file's material in the per-mesh prefabs.

import sys, os, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import aem2fbx, aem

args = sys.argv[sys.argv.index('--') + 1:]
SRC, OUT, PREFIX, NAMES = args[0], args[1], args[2], args[3:]


def channels(sm):
    a = sm.get('anim', {})
    out = []

    def add(name, keys):
        if keys:
            out.append({'target': name, 'keys': [{'t': float(t), 'v': float(v)} for t, v in keys]})

    for k, pre in (('position', 'pos'), ('rotation', 'rot'), ('scale', 'scl')):
        v = a.get(k) or {}
        if v.get('mode') == 1 and v.get('keys'):
            for ax, n in enumerate('XYZ'):
                add(pre + n, [(t, vec[ax]) for t, vec in v['keys']])
        elif v.get('mode') == 0:
            for ax, n in enumerate('XYZ'):
                add(pre + n, v['channels'][ax])
    add('extra', a.get('extra'))
    for i, c in enumerate(a.get('v5channels') or []):
        add('v5_%d' % i, c)
    return out


def convert(base):
    path = os.path.join(SRC, base + '.aem')
    aem2fbx.D = SRC
    aem2fbx.OUT = OUT
    m = aem.load(path)
    meta = {'source': PREFIX + base + '.aem', 'version': m['version'], 'flags': m['flags'],
            'truncated': m.get('truncated', False), 'parts': []}
    counter = [0]

    def walk(sm, parent):
        name = f'{base}_part{counter[0]}'   # aem2fbx's object names, in its order
        counter[0] += 1
        meta['parts'].append({'name': name, 'parent': parent, 'vertices': int(sm['nv']), 'pivot': sm.get('pivot'),
                              'hasUV': 'uv' in sm, 'hasNormals': 'nrm' in sm, 'hasColors': 'col' in sm,
                              'bsphere': sm.get('anim', {}).get('bsphere'), 'channels': channels(sm)})
        for c in sm['children']:
            walk(c, name)

    for sm in m['meshes']:
        walk(sm, base)
    out = aem2fbx.convert(path)   # the fbx (and its old-format sidecar, replaced here)
    with open(os.path.splitext(out)[0] + '.gof2mesh.json', 'w') as f:
        json.dump(meta, f)
    print('converted', out)


for n in NAMES:
    convert(n)
