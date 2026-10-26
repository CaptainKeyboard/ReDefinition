"""Exports a test scene for the raytracing harness from KSP's own files.

The raytracing harness (docs/development/raytracing.md, "Testing without the game")
renders a scene file when REDEFINITION_RT_SCENE names one. This script writes that
file from the meshes of the installed game: the launch pad as the VAB's scenery holds
it (level10) and the SPH's level 3 exterior (sharedassets9), placed beside it. The
file holds Squad's geometry, so it stays on this machine: write it outside the
repository.

    python tools/export_rt_scene.py <KSP folder> <output.rtscene>

Needs UnityPy (pip install UnityPy).

The file is little-endian. It starts with 'RTSC', version 1, mesh count and instance
count (uint32 each). Per mesh it holds vertex count and index count (uint32), then
positions and normals (3 floats a vertex), texture coordinates (2 floats a vertex) and
indices (uint32). Per instance it holds the mesh index (uint32), the transform (3x4
floats, rows first, mesh to world), albedo (3 floats) and smoothness (float).
"""

import os
import struct
import sys
import zlib

import UnityPy


def load(ksp, name):
    """One of the game's asset files under KSP_x64_Data, opened with UnityPy."""
    return UnityPy.load(os.path.join(ksp, 'KSP_x64_Data', name))


def objects_of(env):
    """The objects of the environment's first file, by path id."""
    return list(env.files.values())[0].objects


def quaternion_matrix(q):
    """The 3x3 rotation matrix of a unit quaternion."""
    x, y, z, w = q.x, q.y, q.z, q.w
    return [
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ]


def local_matrix(transform):
    """A Transform's local rotation, scale and position as a 4x4 matrix, parent from local."""
    r = quaternion_matrix(transform.m_LocalRotation)
    s = transform.m_LocalScale
    p = transform.m_LocalPosition
    scale = (s.x, s.y, s.z)
    m = [[r[i][j] * scale[j] for j in range(3)] + [(p.x, p.y, p.z)[i]] for i in range(3)]
    return m + [[0.0, 0.0, 0.0, 1.0]]


def multiply(a, b):
    """The product a b of two 4x4 matrices."""
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def mesh_arrays(mesh):
    """Positions, normals, uvs and indices of a mesh, from UnityPy's OBJ export,
    with the x axis the export mirrors put back."""
    positions, normals, uvs = [], [], []
    out_positions, out_normals, out_uvs, indices = [], [], [], []
    remap = {}
    for line in mesh.export().splitlines():
        parts = line.split()
        if not parts:
            continue
        if parts[0] == 'v':
            positions.append((-float(parts[1]), float(parts[2]), float(parts[3])))
        elif parts[0] == 'vn':
            normals.append((-float(parts[1]), float(parts[2]), float(parts[3])))
        elif parts[0] == 'vt':
            uvs.append((float(parts[1]), float(parts[2])))
        elif parts[0] == 'f':
            face = []
            for corner in parts[1:4]:
                key = corner
                if key not in remap:
                    fields = corner.split('/')
                    v = int(fields[0]) - 1
                    t = int(fields[1]) - 1 if len(fields) > 1 and fields[1] else -1
                    n = int(fields[2]) - 1 if len(fields) > 2 and fields[2] else -1
                    remap[key] = len(out_positions)
                    out_positions.append(positions[v])
                    out_normals.append(normals[n] if 0 <= n < len(normals) else (0.0, 1.0, 0.0))
                    out_uvs.append(uvs[t] if 0 <= t < len(uvs) else (0.0, 0.0))
                face.append(remap[key])
            indices.extend(face)
    return out_positions, out_normals, out_uvs, indices


def collect(env, root_name, offset, meshes, instances, mesh_index):
    """Add the meshes and instances below the object of that name, moved by offset; each mesh is added once, its
    instances get a grey albedo from the mesh's name and smoothness 0.3."""
    objects = objects_of(env)
    transforms = {}
    children = {}
    for pid, obj in objects.items():
        if obj.type.name in ('Transform', 'RectTransform'):
            t = obj.read()
            transforms[pid] = t
            children.setdefault(t.m_Father.path_id, []).append(pid)
    by_object = {t.m_GameObject.path_id: pid for pid, t in transforms.items()}

    roots = [pid for pid, t in transforms.items() if t.m_GameObject.read().m_Name == root_name]
    if not roots:
        raise SystemExit('no ' + root_name)

    # The root may be off in the scene file, as the pad is until the VAB switches it on;
    # below it, what is off (the wrecked variants) stays out.
    def walk(pid, parent, root=False):
        t = transforms[pid]
        local = local_matrix(t)
        if root:
            # The root's place in its scene is left out: the offset places it.
            for i in range(3):
                local[i][3] = 0.0
        world = multiply(parent, local)
        game_object = t.m_GameObject.read()
        if root or game_object.m_IsActive:
            mesh = None
            for component in game_object.m_Components:
                pointer = component.component if hasattr(component, 'component') else component
                try:
                    if pointer.type.name == 'MeshFilter':
                        mesh = pointer.read().m_Mesh.read()
                except Exception:
                    mesh = None
            if mesh is not None:
                if mesh.m_Name not in mesh_index:
                    arrays = mesh_arrays(mesh)
                    if len(arrays[3]) >= 3:
                        mesh_index[mesh.m_Name] = len(meshes)
                        meshes.append(arrays)
                if mesh.m_Name in mesh_index:
                    shade = zlib.crc32(mesh.m_Name.encode()) & 0xFFFFFF
                    albedo = (0.35 + 0.4 * ((shade >> 16) & 255) / 255.0,
                              0.35 + 0.4 * ((shade >> 8) & 255) / 255.0,
                              0.35 + 0.4 * (shade & 255) / 255.0)
                    placed = [row[:] for row in world[:3]]
                    for i in range(3):
                        placed[i][3] += offset[i]
                    instances.append((mesh_index[mesh.m_Name], placed, albedo, 0.3))
            for child in children.get(pid, []):
                walk(child, world)

    identity = [[1.0 if i == j else 0.0 for j in range(4)] for i in range(4)]
    walk(roots[0], identity, True)


def main():
    """Collect the launch pad and the SPH's exterior and write them to the scene file."""
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    ksp, output = sys.argv[1], sys.argv[2]
    meshes, instances, mesh_index = [], [], {}
    collect(load(ksp, 'level10'), 'LaunchPadMedium', (0.0, 0.0, 0.0), meshes, instances, mesh_index)
    pad_instances = len(instances)
    # The SPH's exterior, beside the pad.
    env9 = load(ksp, 'sharedassets9.assets')
    collect(env9, 'SPHLevel3', (260.0, 0.0, 120.0), meshes, instances, mesh_index)
    with open(output, 'wb') as f:
        f.write(struct.pack('<4sIII', b'RTSC', 1, len(meshes), len(instances)))
        for positions, normals, uvs, indices in meshes:
            f.write(struct.pack('<II', len(positions), len(indices)))
            f.write(struct.pack('<%df' % (3 * len(positions)), *[c for p in positions for c in p]))
            f.write(struct.pack('<%df' % (3 * len(normals)), *[c for n in normals for c in n]))
            f.write(struct.pack('<%df' % (2 * len(uvs)), *[c for t in uvs for c in t]))
            f.write(struct.pack('<%dI' % len(indices), *indices))
        for mesh, matrix, albedo, smoothness in instances:
            f.write(struct.pack('<I', mesh))
            f.write(struct.pack('<12f', *[matrix[i][j] for i in range(3) for j in range(4)]))
            f.write(struct.pack('<3f', *albedo))
            f.write(struct.pack('<f', smoothness))
    triangles = sum(len(m[3]) // 3 for m in meshes)
    print('%d meshes, %d instances (%d of the launch pad), %d triangles -> %s'
          % (len(meshes), len(instances), pad_instances, triangles, output))


if __name__ == '__main__':
    main()
