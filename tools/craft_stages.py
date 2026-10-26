"""Stages of stock crafts, worked out offline from their files.

Reads a craft file (Ships/VAB/*.craft) and the parts as the game loads them
(GameData/ModuleManager.ConfigCache), or with --stock Squad's own part files, which
give the same on every installation of the same KSP version. It burns the stages one
after the other. A part drops when the first decoupler between it and the
root fires. Liquid fuel flows through crossfeeding parts and fuel lines, the tanks
dropped soonest first. Solid fuel stays in its part. A stage ends when its engines
are dry, or when the engines the next stage drops are. Air-breathing engines are
left out, as KSP gives them no thrust in vacuum; a RAPIER counts in its
air-breathing mode, KSP's default, so not at all. Writes each craft's stage rows,
part facts and the parts as the planner's own simulation takes them (StageSim) as
C# for the planner's tests (tests/ReDefinition.Tests/StockCrafts.cs). Crafts are
looked for in Ships/VAB, then Ships/SPH.

Usage: python tools/craft_stages.py [--stock] <KSP folder> <output .cs> <craft name>...
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
    """The first value of the key in the node, or the default."""
    for k, v in node[0]:
        if k == key:
            return v
    return default


def values(node, key):
    """Every value of the key in the node, in order."""
    return [v for k, v in node[0] if k == key]


def children(node, name):
    """The node's child nodes of that name."""
    return [n for k, n in node[1] if k == name]


def number(text, default=0.0):
    """The text as a float, or the default where it is missing or no number."""
    try:
        return float(text)
    except (TypeError, ValueError):
        return default


# ------------------------------------------------------------------- the game

STOCK_FOLDERS = ('Squad', 'SquadExpansion')


class Game:
    """The installed game's part configs by name and its resources' density and flow mode."""
    def __init__(self, ksp, stock=False):
        """The parts and resources as the game loads them (ModuleManager.ConfigCache),
        or with stock, from Squad's own files alone, as KSP without mods has them."""
        self.ksp = ksp
        self.loose = None
        self.parts = {}
        self.resources = {}
        if stock:
            nodes = []
            for top in STOCK_FOLDERS:
                for folder, dirs, files in os.walk(os.path.join(ksp, 'GameData', top)):
                    dirs[:] = sorted(d for d in dirs if d != 'zDeprecated')
                    for file in sorted(files):
                        if file.endswith('.cfg'):
                            text = open(os.path.join(folder, file), encoding='utf-8-sig', errors='replace').read()
                            nodes.extend(parse_nodes(text)[1])
        else:
            cache = open(os.path.join(ksp, 'GameData', 'ModuleManager.ConfigCache'), encoding='utf-8',
                         errors='replace').read()
            nodes = [entry for url in children(parse_nodes(cache), 'UrlConfig') for entry in url[1]]
        for name, node in nodes:
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


AIR = ('IntakeAir',)
IGNORED = ('IntakeAir', 'ElectricCharge')


class Engine:
    """One engine module of a part: thrust, Isp, propellants and their flow, from its config and the craft."""
    def __init__(self, module, craft_module):
        """From the part config's engine module, the thrust limited by the craft's thrustPercentage; Isp in vacuum
        and at 1 atm from the atmosphereCurve, solid where the throttle is locked or it cannot shut down."""
        self.thrust = number(value(module, 'maxThrust'))
        percent = number(value(craft_module, 'thrustPercentage', '100'), 100.0) if craft_module else 100.0
        self.thrust *= percent / 100.0
        self.solid = ((value(module, 'throttleLocked', 'False') or '').lower() == 'true'
                      or (value(module, 'allowShutdown', 'True') or '').lower() == 'false')
        vel = children(module, 'velCurve')
        self.vel = sorted(tuple(number(x) for x in k.split()[:2]) for k in values(vel[0], 'key')) if vel else []
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
        self.flows = {value(p, 'name'): value(p, 'resourceFlowMode') for p in children(module, 'PROPELLANT')}
        self.air = any(name in AIR for name, _ in self.propellants)
        self.title = value(module, 'engineID') or ''

    def mach_limit(self):
        """Where the thrust over Mach falls to half its peak, past the peak; no more
        than Mach 1 for an engine whose peak lies below Mach 1.5 (StageSim's
        AirLaunch.MachLimit, on the keys joined by straight lines)."""
        if len(self.vel) < 2:
            return 0.0
        peak_at, peak = max(self.vel, key=lambda k: k[1])
        limit = self.vel[-1][0]
        for (m0, v0), (m1, v1) in zip(self.vel, self.vel[1:]):
            if m1 <= peak_at:
                continue
            if v1 < peak / 2.0 <= v0 or (v0 >= peak / 2.0 and v1 < peak / 2.0):
                limit = m0 + (m1 - m0) * (v0 - peak / 2.0) / (v0 - v1)
                break
        if peak_at < 1.5:
            limit = min(limit, 1.0)
        return limit


class Part:
    """One part of a craft; load_craft sets its attributes: tree, mass, resources, engines, kind and decouple stage."""
    pass


def load_craft(game, path):
    """The craft file's parts with their tree, fuel lines, resources and engines, and per part the stage at which
    the first decoupler between it and the root fires (decouple)."""
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
        part.lift = (('ModuleLiftingSurface' in names or 'ModuleControlSurface' in names) and not part.command
                     and not part.heatshield)
        part.wheels = any(value(m, 'name') == 'ModuleWheelBase' and (value(m, 'wheelType') or '').upper() != 'LEG'
                          for m in modules)
        part.legs = any(value(m, 'name') == 'ModuleWheelBase' and (value(m, 'wheelType') or '').upper() == 'LEG'
                        for m in modules)
        part.engines = []
        seen_engine = False
        part.is_engine = False
        part.rocket = None
        part.air = None
        multi = 'MultiModeEngine' in names
        engine_modules = [m for m in modules if (value(m, 'name') or '').startswith('ModuleEngines')]
        for m in engine_modules:
            mname = value(m, 'name') or ''
            engine = Engine(m, craft_modules.get(mname))
            non_thrust = (value(m, 'nonThrustMotor', 'False') or '').lower() == 'true'
            if not non_thrust:
                part.is_engine = True
            if not seen_engine:
                seen_engine = True
                if engine.thrust > 0 and engine.isp > 0 and not engine.air:
                    part.engines.append(engine)
            # The planner's own simulation: the rocket mode, and the air-breathing
            # mode that goes furthest, of a multi-mode engine; else the first
            # engine, as KSP.
            if non_thrust or engine.thrust <= 0 or engine.isp <= 0:
                continue
            if engine.air and (multi or m is engine_modules[0]):
                if part.air is None or engine.mach_limit() > part.air.mach_limit():
                    part.air = engine
            elif not engine.air and part.rocket is None and (multi or m is engine_modules[0]):
                part.rocket = engine
        part.crossfeed_fuel_lines = []
        part.amount = {}
        for r in children(node, 'RESOURCE'):
            part.amount[value(r, 'name')] = number(value(r, 'amount'))
        part.initial = dict(part.amount)
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
    """The part's mass now: dry mass and the mass of the resources it holds, in tonnes."""
    return part.dry + sum(amount * game.resources.get(name, (0.0, ''))[0] for name, amount in part.amount.items())


def reachable(parts, start):
    """The parts fuel can flow from to the start part: through crossfeeding parts and fuel lines, never a clamp."""
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
    """Burn the stages from the highest down; the rows (masses, delta-v, thrust, burn time, solid) and, per part and
    stage that burnt from it, its resource mass at the start and the end."""
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
        first_lit = list(lit)
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
        solid = bool(first_lit) and all(e.solid for p in first_lit for e in p.engines) and dv > 0
        rows.append(dict(number=stage, start=start, end=end, dv=dv, thrust=thrust, surface=surface, time=time, solid=solid))
    return rows, fuel


def sources(game, parts, attached, engine_at, name):
    """The attached parts the engine draws the resource from now, by its flow mode: of those holding some, the ones
    dropped soonest."""
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
    """Whether every propellant of the part's engines still has a source."""
    return all(sources(game, parts, attached, part.index, name) for e in part.engines for name, _ in e.propellants)


def draw(game, parts, attached, engine_at, name, units):
    """Take the units of the resource evenly from the engine's sources, moving on as they run dry; the mass burnt."""
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
    """A number as a C# literal: floats rounded to four places."""
    return repr(round(x, 4)) if isinstance(x, float) else str(x)


FLOWS = {'NO_FLOW': 'SimFlow.Own', 'STACK_PRIORITY_SEARCH': 'SimFlow.Stack', 'STAGE_STACK_FLOW': 'SimFlow.Stack',
         'STAGE_STACK_FLOW_BALANCE': 'SimFlow.Stack'}


def sim_engine(game, engine, kind):
    """The engine as the planner's simulation takes it: a Rocket(...) or AirEngine(...) expression with its
    propellants, the ignored ones and those without mass left out."""
    props = []
    for pname, ratio in engine.propellants:
        density, mode = game.resources.get(pname, (0.0, 'ALL_VESSEL'))
        if pname in IGNORED or density <= 0:
            continue
        mode = engine.flows.get(pname) or mode
        props.append('Prop("%s", %s, %s)' % (pname, cs_number(ratio), FLOWS.get(mode, 'SimFlow.Vessel')))
    if kind == 'rocket':
        sea = engine.thrust * engine.isp_sea / engine.isp if engine.isp > 0 else 0.0
        return 'Rocket(%s, %s, %s, %s, %s)' % (cs_number(engine.thrust), cs_number(engine.isp), cs_number(sea),
                                               str(engine.solid).lower(), ', '.join(props))
    # Standing at sea level: the thrust and Isp the configs give at Mach 0 and 1 atm.
    return 'AirEngine(%s, %s, %s, %s)' % (cs_number(engine.thrust), cs_number(engine.isp_sea), cs_number(engine.mach_limit()),
                                         ', '.join(props))


def emit_sim(game, parts):
    """The C# lines of the craft's Sim list: one SimPartOf(...) per part."""
    out = ['                Sim = new List<SimPart>', '                {']
    for p in parts:
        amounts = ', '.join('{ "%s", %s }' % (n, cs_number(a)) for n, a in sorted(p.initial.items())
                            if game.resources.get(n, (0.0, ''))[0] > 0)
        sources = ', '.join(str(i) for i in (p.fuel_line_source or []))
        rocket = sim_engine(game, p.rocket, 'rocket') if p.rocket else 'null'
        air = sim_engine(game, p.air, 'air') if p.air else 'null'
        out.append('                    SimPartOf(%d, %d, %d, %s, %s, %s, new Dictionary<string, double> { %s }, new int[] { %s }, %s, %s),'
                   % (p.parent, p.decouple, p.istg, str(p.clamp).lower(), str(p.crossfeed).lower(), cs_number(p.dry),
                      amounts, sources, rocket, air))
    out.append('                },')
    return out


def emit(name, parts, rows, fuel, game_parts_mass, game):
    """The C# block of one StockCraft: its stage rows, its part facts and its Sim list."""
    out = []
    out.append('            new StockCraft')
    out.append('            {')
    out.append('                Name = "%s",' % name)
    out.append('                Rows = new List<StageRow>')
    out.append('                {')
    for r in rows:
        out.append('                    Row(%d, %s, %s, %s, %s, %s, %s),' % (r['number'], cs_number(r['start']), cs_number(r['end']),
                                                                           cs_number(r['dv']), cs_number(r['thrust']), cs_number(r['surface']),
                                                                           str(r['solid']).lower()))
    out.append('                },')
    out.append('                Parts = new List<PartFacts>')
    out.append('                {')
    for p in parts:
        stage_fuel = ', '.join('{ %d, new double[] { %s, %s } }' % (s, cs_number(a), cs_number(b))
                               for (i, s), (a, b) in sorted(fuel.items()) if i == p.index)
        sep = ('Separation.DockingPort' if p.docking else 'Separation.Anchored' if p.anchored
               else 'Separation.Decoupler' if p.decoupler else 'Separation.None')
        out.append('                    Facts(%d, %d, %d, %s, %s, "%s", %s, %s, %s, %s, %s, %s, %s, %s, %d, %s, new Dictionary<int, double[]> { %s }),'
                   % (p.parent, p.decouple, p.istg, cs_number(game_parts_mass[p.index]), cs_number(p.dry), p.name,
                      str(p.command).lower(), str(p.is_engine).lower(), str(p.legs).lower(), str(p.parachute).lower(),
                      str(p.heatshield).lower(), str(p.lift).lower(), str(p.wheels).lower(), str(p.clamp).lower(), p.crew, sep,
                      stage_fuel))
    out.append('                },')
    out.extend(emit_sim(game, parts))
    out.append('            },')
    return '\n'.join(out)


def main():
    """Simulate every craft named, print its burning stages and write StockCrafts.cs with all of them."""
    arguments = sys.argv[1:]
    stock = '--stock' in arguments
    if stock:
        arguments.remove('--stock')
    if len(arguments) < 3:
        sys.exit(__doc__)
    ksp, target = arguments[0], arguments[1]
    game = Game(ksp, stock)
    blocks = []
    for name in arguments[2:]:
        path = os.path.join(ksp, 'Ships', 'VAB', name + '.craft')
        if not os.path.exists(path):
            path = os.path.join(ksp, 'Ships', 'SPH', name + '.craft')
        parts = load_craft(game, path)
        masses = {p.index: mass_of(game, p) for p in parts}
        rows, fuel = simulate(game, parts)
        blocks.append(emit(name, parts, rows, fuel, masses, game))
        print(name + ':')
        for r in rows:
            if r['dv'] > 0.5 or r['thrust'] > 0:
                print('  S%d  %7.2f t -> %7.2f t  %6.0f m/s  %6.0f kN  %5.1f s' % (r['number'], r['start'], r['end'], r['dv'], r['thrust'], r['time']))
    source = ("Squad's own part files (--stock)" if stock
              else 'the parts as the game loads them (GameData/ModuleManager.ConfigCache)')
    header = ('// Generated by tools/craft_stages.py from the stock crafts (Ships/VAB, Ships/SPH) and\n'
              '// %s. Do not edit.\n' % source +
              'using System.Collections.Generic;\nusing ReDefinition.Engineer;\n\n'
              'namespace ReDefinition.Tests\n{\n    internal static partial class StockCrafts\n    {\n'
              '        // The density of each resource with a mass, in tonnes a unit, as the game\'s resource definitions give it.\n'
              '        internal static readonly Dictionary<string, double> Density = new Dictionary<string, double>\n        {\n'
              + ''.join('            { "%s", %s },\n' % (n, cs_number(d)) for n, (d, _) in sorted(game.resources.items())
                        if d > 0 and n not in IGNORED)
              + '        };\n\n'
              '        // Every stock craft the tests fly: its stage rows as KSP counts them, its parts\' facts and its simulation parts.\n'
              '        internal static readonly List<StockCraft> All = new List<StockCraft>\n        {\n')
    footer = '\n        };\n    }\n}\n'
    open(target, 'w', encoding='utf-8', newline='\r\n').write(header + '\n'.join(blocks) + footer)


if __name__ == '__main__':
    main()
