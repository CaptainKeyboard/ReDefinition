"""Stages of stock crafts as KSP's delta-v simulation gives them, worked out offline.

Reads a craft file (Ships/VAB/*.craft) and the parts as the game loads them
(GameData/ModuleManager.ConfigCache), and burns the stages one after the other as
KSP's simulation does: a part drops when the first decoupler between it and the
root fires; liquid fuel flows through crossfeeding parts and fuel lines, the tanks
dropped soonest first; solid fuel stays in its part; a stage ends when its engines
are dry, or when the engines the next stage drops are. Air-breathing engines are
left out. Writes each craft's stage rows and part facts as C# for the planner's
tests (tests/ReDefinition.Tests/StockCrafts.cs).

Usage: python tools/craft_stages.py <KSP folder> <output .cs> <craft name>...
"""
import math
import os
import re
import sys

G0 = 9.80665
STEP = 0.05


# ------------------------------------------------------------------ ConfigNode

def parse_nodes(text):
    """A ConfigNode text as (values, nodes): values a list of (key, value), nodes a
    list of (name, (values, nodes))."""
    tokens = []
    for raw in text.split('\n'):
        line = raw.split('//', 1)[0].strip()
        if not line:
            continue
        while line:
            if line[0] in '{}':
                tokens.append(line[0])
                line = line[1:].strip()
                continue
            brace = min([i for i in (line.find('{'), line.find('}')) if i >= 0] or [len(line)])
            tokens.append(line[:brace].strip())
            line = line[brace:].strip()
    root = ([], [])
    stack = [root]
    pending = None
    for token in tokens:
        if token == '{':
            node = ([], [])
            stack[-1][1].append((pending or '', node))
            stack.append(node)
            pending = None
        elif token == '}':
            if pending is not None:
                stack[-1][1].append((pending, ([], [])))
                pending = None
            if len(stack) > 1:
                stack.pop()
        elif '=' in token:
            if pending is not None:
                stack[-1][1].append((pending, ([], [])))
                pending = None
            key, value = token.split('=', 1)
            stack[-1][0].append((key.strip(), value.strip()))
        else:
            pending = token
    return root


def value(node, key, default=None):
    for k, v in node[0]:
        if k == key:
            return v
    return default


def values(node, key):
    return [v for k, v in node[0] if k == key]


def children(node, name):
    return [n for k, n in node[1] if k == name]


def number(text, default=0.0):
    try:
        return float(text)
    except (TypeError, ValueError):
        return default


# ------------------------------------------------------------------- the game

class Game:
    def __init__(self, ksp):
        cache = open(os.path.join(ksp, 'GameData', 'ModuleManager.ConfigCache'), encoding='utf-8', errors='replace').read()
        root = parse_nodes(cache)
        self.ksp = ksp
        self.loose = None
        self.parts = {}
        self.resources = {}
        for url in children(root, 'UrlConfig'):
            for name, node in url[1]:
                if name == 'PART':
                    part_name = (value(node, 'name') or '').replace('_', '.')
                    self.parts[part_name] = node
                elif name == 'RESOURCE_DEFINITION':
                    self.resources[value(node, 'name')] = (number(value(node, 'density')),
                                                           value(node, 'flowMode', 'ALL_VESSEL'))


    def part(self, name):
        """A part by the name a craft gives it; KSP loads crafts with old names under
        the name of the part that replaced them (such as RCSBlock as RCSBlock.v2)."""
        if name in self.parts:
            return self.parts[name]
        lower = {n.lower(): n for n in self.parts}
        for candidate in (name, name + '.v2', name + '.v3', name + '.v4'):
            if candidate.lower() in lower:
                return self.parts[lower[candidate.lower()]]
        # Parts KSP keeps for old crafts only (Squad/zDeprecated) are not in the cache.
        if self.loose is None:
            self.loose = {}
            for folder, _, files in os.walk(os.path.join(self.ksp, 'GameData', 'Squad', 'zDeprecated')):
                for file in files:
                    if file.endswith('.cfg'):
                        root = parse_nodes(open(os.path.join(folder, file), encoding='utf-8-sig', errors='replace').read())
                        for node in children(root, 'PART'):
                            self.loose[(value(node, 'name') or '').replace('_', '.')] = node
        return self.loose.get(name)


class Engine:
    def __init__(self, module, craft_module):
        self.thrust = number(value(module, 'maxThrust'))
        percent = number(value(craft_module, 'thrustPercentage', '100'), 100.0) if craft_module else 100.0
        self.thrust *= percent / 100.0
        curve = children(module, 'atmosphereCurve')
        keys = [tuple(number(x) for x in k.split()[:2]) for k in values(curve[0], 'key')] if curve else []
        keys.sort()
        self.isp = keys[0][1] if keys else 0.0
        self.isp_sea = next((isp for pressure, isp in keys if pressure >= 1.0), keys[-1][1] if keys else 0.0)
        if keys and keys[0][0] < 1.0 < keys[-1][0]:
            for (p0, i0), (p1, i1) in zip(keys, keys[1:]):
                if p0 <= 1.0 <= p1:
                    self.isp_sea = i0 + (i1 - i0) * (1.0 - p0) / (p1 - p0)
        self.propellants = [(value(p, 'name'), number(value(p, 'ratio'))) for p in children(module, 'PROPELLANT')]


class Part:
    pass


def load_craft(game, path):
    root = parse_nodes(open(path, encoding='utf-8-sig', errors='replace').read())
    nodes = children(root, 'PART')
    ids = [value(n, 'part') for n in nodes]
    index = {pid: i for i, pid in enumerate(ids)}
    by_suffix = {pid.rsplit('_', 1)[1]: i for i, pid in enumerate(ids)}
    parts = []
    for i, node in enumerate(nodes):
        part = Part()
        part.name = ids[i].rsplit('_', 1)[0]
        cfg = game.part(part.name)
        if cfg is None:
            raise KeyError('part not in the game: ' + part.name)
        part.index = i
        part.istg = int(number(value(node, 'istg'), -1))
        part.parent = -1
        part.children = [index[link] for link in values(node, 'link') if link in index]
        modules = [m for m in children(cfg, 'MODULE')]
        names = [value(m, 'name') or '' for m in modules]
        craft_modules = {value(m, 'name'): m for m in children(node, 'MODULE')}
        part.dry = number(value(cfg, 'mass'))
        part.crossfeed = (value(cfg, 'fuelCrossFeed', 'True') or 'True').lower() == 'true'
        toggle = craft_modules.get('ModuleToggleCrossfeed')
        if toggle is not None and value(toggle, 'crossfeedStatus') is not None:
            part.crossfeed = value(toggle, 'crossfeedStatus').lower() == 'true'
        part.clamp = 'LaunchClamp' in names
        part.decoupler = 'ModuleDecouple' in names or 'ModuleAnchoredDecoupler' in names
        part.anchored = 'ModuleAnchoredDecoupler' in names
        part.docking = 'ModuleDockingNode' in names
        part.command = 'ModuleCommand' in names
        part.crew = int(number(value(cfg, 'CrewCapacity'), 0))
        part.parachute = 'ModuleParachute' in names
        part.heatshield = 'ModuleAblator' in names
        part.legs = any(value(m, 'name') == 'ModuleWheelBase' and (value(m, 'wheelType') or '').upper() == 'LEG'
                        for m in modules)
        part.engines = []
        seen_engine = False
        for m in modules:
            mname = value(m, 'name') or ''
            if mname.startswith('ModuleEngines') and not seen_engine:
                engine = Engine(m, craft_modules.get(mname))
                seen_engine = True
                if engine.thrust > 0 and engine.isp > 0 and all(p != 'IntakeAir' for p, _ in engine.propellants):
                    part.engines.append(engine)
        part.is_engine = seen_engine
        part.amount = {}
        for r in children(node, 'RESOURCE'):
            part.amount[value(r, 'name')] = number(value(r, 'amount'))
        part.fuel_line_target = None
        pdata = children(node, 'PARTDATA')
        if pdata and value(pdata[0], 'tgt') is not None:
            part.fuel_line_target = by_suffix.get(value(pdata[0], 'tgt'))
        part.fuel_line_source = None
        srf = values(node, 'srfN')
        if srf:
            host = srf[0].split(',', 1)[1].strip() if ',' in srf[0] else ''
            part.srf_host = index.get(host)
        else:
            part.srf_host = None
        parts.append(part)
    for part in parts:
        for child in part.children:
            parts[child].parent = part.index
    for part in parts:
        if part.fuel_line_target is not None and part.name.startswith('fuelLine'):
            source = part.parent
            if source >= 0:
                parts[part.fuel_line_target].fuel_line_source = (parts[part.fuel_line_target].fuel_line_source or []) + [source]
    # A part drops when the first decoupler between it and the root fires.
    for part in parts:
        stage = -1
        at = part.index
        while at >= 0:
            if parts[at].decoupler:
                stage = max(stage, parts[at].istg)
            at = parts[at].parent
        part.decouple = stage
    return parts


# ------------------------------------------------------------------ simulation

def mass_of(game, part):
    return part.dry + sum(amount * game.resources.get(name, (0.0, ''))[0] for name, amount in part.amount.items())


def reachable(parts, start):
    seen = {start}
    todo = [start]
    while todo:
        at = todo.pop()
        part = parts[at]
        if at != start and not part.crossfeed:
            continue
        near = list(part.children) + ([part.parent] if part.parent >= 0 else []) + list(part.fuel_line_source or [])
        for other in near:
            if other not in seen and not parts[other].clamp:
                seen.add(other)
                todo.append(other)
    return seen


def simulate(game, parts):
    attached = set(p.index for p in parts if not p.clamp)
    top = max([p.istg for p in parts] + [0])
    rows = []
    fuel = {}  # (part, stage) -> [start mass of resources, end mass]
    for stage in range(top, -1, -1):
        attached -= {p.index for p in parts if p.decouple == stage}
        active = [p for p in parts if p.index in attached and p.engines and p.istg >= stage]
        start = sum(mass_of(game, parts[i]) for i in attached)
        start_fuel = {i: mass_of(game, parts[i]) - parts[i].dry for i in attached}
        lit = [p for p in active if feeds(game, parts, attached, p)]
        thrust = sum(e.thrust for p in lit for e in p.engines)
        surface = sum(e.thrust * e.isp_sea / e.isp for p in lit for e in p.engines)
        dropped_next = {p.index for p in parts if p.decouple == stage - 1 and p.index in attached}
        watched = [p for p in active if p.index in dropped_next]
        dv = 0.0
        time = 0.0
        mass = start
        while lit and time < 3600:
            flows = {}
            for p in lit:
                for e in p.engines:
                    rate = e.thrust / (e.isp * G0)  # t/s
                    total = sum(r * game.resources.get(n, (0.0, ''))[0] for n, r in e.propellants)
                    for name, ratio in e.propellants:
                        density = game.resources.get(name, (0.0, ''))[0]
                        if total <= 0 or density <= 0:
                            continue
                        units = rate * ratio / total * STEP
                        flows.setdefault((p.index, name), 0.0)
                        flows[(p.index, name)] += units
            burnt = 0.0
            for (engine_at, name), units in flows.items():
                burnt += draw(game, parts, attached, engine_at, name, units)
            push = sum(e.thrust for p in lit for e in p.engines)
            dv += push / mass * STEP
            mass -= burnt
            time += STEP
            lit = [p for p in lit if feeds(game, parts, attached, p)]
            if watched and not any(p in lit for p in watched):
                break
        end = sum(mass_of(game, parts[i]) for i in attached)
        for i in attached:
            now = mass_of(game, parts[i]) - parts[i].dry
            if start_fuel[i] - now > 1e-6:
                fuel[(i, stage)] = (start_fuel[i], now)
        rows.append(dict(number=stage, start=start, end=end, dv=dv, thrust=thrust, surface=surface, time=time))
    return rows, fuel


def sources(game, parts, attached, engine_at, name):
    density, mode = game.resources.get(name, (0.0, 'ALL_VESSEL'))
    if mode == 'NO_FLOW':
        pool = [engine_at]
    elif mode == 'STACK_PRIORITY_SEARCH':
        pool = [i for i in reachable(parts, engine_at) if i in attached]
    else:
        pool = list(attached)
    pool = [i for i in pool if parts[i].amount.get(name, 0.0) > 1e-9]
    if not pool:
        return []
    top = max(parts[i].decouple for i in pool)
    return [i for i in pool if parts[i].decouple == top]


def feeds(game, parts, attached, part):
    return all(sources(game, parts, attached, part.index, name) for e in part.engines for name, _ in e.propellants)


def draw(game, parts, attached, engine_at, name, units):
    density = game.resources.get(name, (0.0, ''))[0]
    burnt = 0.0
    left = units
    for _ in range(4):
        pool = sources(game, parts, attached, engine_at, name)
        if not pool or left <= 1e-12:
            break
        share = left / len(pool)
        for i in pool:
            take = min(share, parts[i].amount[name])
            parts[i].amount[name] -= take
            left -= take
            burnt += take * density
    return burnt


# -------------------------------------------------------------------- C# out

def cs_number(x):
    return repr(round(x, 4)) if isinstance(x, float) else str(x)


def emit(name, parts, rows, fuel, game_parts_mass):
    out = []
    out.append('            new StockCraft')
    out.append('            {')
    out.append('                Name = "%s",' % name)
    out.append('                Rows = new List<StageRow>')
    out.append('                {')
    for r in rows:
        out.append('                    Row(%d, %s, %s, %s, %s, %s),' % (r['number'], cs_number(r['start']), cs_number(r['end']),
                                                                       cs_number(r['dv']), cs_number(r['thrust']), cs_number(r['surface'])))
    out.append('                },')
    out.append('                Parts = new List<PartFacts>')
    out.append('                {')
    for p in parts:
        stage_fuel = ', '.join('{ %d, new double[] { %s, %s } }' % (s, cs_number(a), cs_number(b))
                               for (i, s), (a, b) in sorted(fuel.items()) if i == p.index)
        sep = ('Separation.DockingPort' if p.docking else 'Separation.Anchored' if p.anchored
               else 'Separation.Decoupler' if p.decoupler else 'Separation.None')
        out.append('                    Facts(%d, %d, %d, %s, %s, "%s", %s, %s, %s, %s, %s, %s, %d, %s, new Dictionary<int, double[]> { %s }),'
                   % (p.parent, p.decouple, p.istg, cs_number(game_parts_mass[p.index]), cs_number(p.dry), p.name,
                      str(p.command).lower(), str(p.is_engine).lower(), str(p.legs).lower(), str(p.parachute).lower(),
                      str(p.heatshield).lower(), str(p.clamp).lower(), p.crew, sep, stage_fuel))
    out.append('                },')
    out.append('            },')
    return '\n'.join(out)


def main():
    ksp, target = sys.argv[1], sys.argv[2]
    game = Game(ksp)
    blocks = []
    for name in sys.argv[3:]:
        path = os.path.join(ksp, 'Ships', 'VAB', name + '.craft')
        parts = load_craft(game, path)
        masses = {p.index: mass_of(game, p) for p in parts}
        rows, fuel = simulate(game, parts)
        rows = [r for r in rows]
        blocks.append(emit(name, parts, rows, fuel, masses))
        print(name + ':')
        for r in rows:
            if r['dv'] > 0.5 or r['thrust'] > 0:
                print('  S%d  %7.2f t -> %7.2f t  %6.0f m/s  %6.0f kN  %5.1f s' % (r['number'], r['start'], r['end'], r['dv'], r['thrust'], r['time']))
    header = ('// Generated by tools/craft_stages.py from the stock crafts (Ships/VAB) and the parts as\n'
              '// the game loads them (GameData/ModuleManager.ConfigCache). Do not edit.\n'
              'using System.Collections.Generic;\nusing ReDefinition.Engineer;\n\n'
              'namespace ReDefinition.Tests\n{\n    internal static partial class StockCrafts\n    {\n'
              '        internal static readonly List<StockCraft> All = new List<StockCraft>\n        {\n')
    footer = '\n        };\n    }\n}\n'
    open(target, 'w', encoding='utf-8', newline='\r\n').write(header + '\n'.join(blocks) + footer)


if __name__ == '__main__':
    main()
