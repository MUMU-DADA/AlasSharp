"""Offline actual Fleet rounds/tracking and match_movable; no device or account I/O."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, inputs, output = [Path(arg).resolve() for arg in sys.argv[1:]]
    sys.path.insert(0, str(root))
    os.chdir(output.parent)
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        from module.map.fleet import Fleet
        from module.map.map_base import CampaignMap
        from module.map.map_grids import SelectedGrids
        from module.map.utils import match_movable
        from module.base.utils import node2location as loc
        from module.base.utils import location2node as node
        from module.logger import logger
        import numpy as np
        logger.setLevel('CRITICAL')
        cases = json.loads(inputs.read_text(encoding='utf-8'))

        def config(c):
            return SimpleNamespace(MAP_HAS_MOVABLE_ENEMY=c['siren'], MAP_HAS_MOVABLE_NORMAL_ENEMY=c['normal'],
                MAP_HAS_MAZE=c.get('maze', False), MAP_HAS_BOUNCING_ENEMY=c.get('bounce', False),
                MOVABLE_ENEMY_TURN=c.get('turns', [2]), MOVABLE_NORMAL_ENEMY_TURN=c.get('normalTurns', [1]),
                MAP_SIREN_MOVE_WAIT=c.get('wait', 1.5), MOVABLE_ENEMY_FLEET_STEP=c.get('step', 2),
                MAP_HAS_WALL=c.get('walls', False), MAP_HAS_PORTAL=c.get('portal', False), MAP_HAS_AMBUSH=False,
                MAP_ENEMY_TEMPLATE=['Light'] if c.get('templates', True) else [],
                MAP_HAS_FORTRESS=False, FLEET_2=2, MAP_HAS_DECOY_ENEMY=False)

        def make(c):
            f = Fleet.__new__(Fleet)
            f.config = config(c)
            f.map = CampaignMap('offline-movable')
            f.map.shape = 'E5'
            f.map.map_data = cases['tiles']
            f.map.load_map_data()
            f.map.spawn_data = [dict(battle=w['battle'], enemy=w['enemy'], siren=w['siren'], boss=w['boss'], mystery=w['mystery']) for w in cases['waves']]
            f.map.load_spawn_data()
            f.map._map_covered = SelectedGrids([f.map[loc('B3')]])
            # Equivalent compiled topology; walls are one edge, portal is directed.
            f.map.grid_connection_initial()
            f.map[loc('A3')].is_portal = c.get('portal', False)
            f.map[loc('A3')].portal_link = loc('E3') if c.get('portal') else None
            f.map.portal_data = [('A3', 'E3')]
            # Native wall declarations are ASCII; tracker rebuilding calls this same builder.
            initial = f.map.grid_connection_initial
            def connections(wall=False, portal=False):
                initial(wall=False, portal=portal)
                if wall:
                    f.map.grid_connection[loc('C2')].remove(loc('D2'))
                    f.map.grid_connection[loc('D2')].remove(loc('C2'))
            f.map.grid_connection_initial = connections
            connections(wall=c.get('walls', False), portal=c.get('portal', False))
            f.map.bouncing_enemy_data = [SelectedGrids([f.map[loc('D5')], f.map[loc('E5')]])]
            if c.get('bounce'):
                f.map[loc('D5')].may_bouncing_enemy = True
            f.map.maze_round = 9
            f.map[loc('D4')].is_maze = True
            f.map[loc('D4')].maze_round = [3, 4, 5]
            f.fleet_1_location, f.fleet_2_location = loc('C4'), loc('E5')
            f.fleet_current_index = 1
            for cell in [f.fleet_1_location, f.fleet_2_location]: f.map[cell].is_fleet = True
            f.battle_count = f.siren_count = f.mystery_count = f.carrier_count = 0
            f.is_map_data_poor = False
            return f

        matches = []
        for c in cases['matches']:
            # Capture the actual native objective and secondary order without changing its search.
            n, m = len(c['before']) + len(c['spawn']), len(c['after']) + len(c['fleets'])
            metric = dict(score=-10000*n, columnSum=n*m)
            original_sort, original_max = np.argsort, np.argmax
            sorted_sums = []
            def argsort(values, *args, **kwargs):
                order = original_sort(values, *args, **kwargs)
                sorted_sums[:] = values[order].tolist()
                return order
            def argmax(values, *args, **kwargs):
                index = original_max(values, *args, **kwargs)
                metric.update(score=int(values[index]), columnSum=int(sorted_sums[index]))
                return index
            np.argsort, np.argmax = argsort, argmax
            try:
                a, b = match_movable(*[[loc(x) for x in c[key]] for key in ['before', 'spawn', 'after', 'fleets']], fleet_step=c['step'])
            finally:
                np.argsort, np.argmax = original_sort, original_max
            matches.append(dict(before=[node(x) for x in a], after=[node(x) for x in b], **metric))

        rounds = []
        for c in cases['rounds']:
            f = make(c)
            f.map[loc('B3')].is_siren = True
            f.map[loc('A1')].is_enemy = True
            f.round_reset()
            f.round_battle(after_battle=False)
            sequence = []
            for i in range(12):
                if i:
                    f.map[loc('B3')].is_siren = i % 4 != 0
                    f.map[loc('A1')].is_enemy = i % 5 != 0
                    f.battle_count = i // 3
                    if i % 3 == 0: f.round_battle()
                sequence.append(dict(round=f.round, enemies=f.enemy_round.copy(), wait=f.round_wait,
                    moved=f.round_is_new, maze=f.round_maze_changed, active=f.maze_active_on(loc('D4'))))
                f.round_next()
            rounds.append(sequence)

        scans = []
        for c in cases['scans']:
            f = make(c)
            f.map[loc('B3')].is_siren = True
            f.map[loc('A1')].is_enemy = True
            f.movable_before = f.map.select(is_siren=True)
            f.movable_before_normal = f.map.select(is_enemy=True)
            calls = []
            def full_scan(queue=None, must_scan=None, mode='normal'):
                calls.append(dict(queue=None if queue is None else [node(g.location) for g in queue],
                    must=None if must_scan is None else [node(g.location) for g in must_scan], mode=mode,
                    sirens=[node(g.location) for g in f.map.select(is_siren=True)],
                    enemies=[node(g.location) for g in f.map.select(is_enemy=True)]))
                f.map[loc('A3')].is_siren = True
                f.map[loc('B1')].is_enemy = True
            f.full_scan = full_scan
            f.full_scan_movable(enemy_cleared=c['cleared'])
            scans.append(dict(calls=calls, cells=[dict(enemy=g.is_enemy, siren=g.is_siren, movable=g.is_movable) for g in f.map]))

        tracks = []
        for c in cases['tracks']:
            f = make(c)
            f.battle_count = c['battle']
            f.siren_count = c['sirenCount']
            for cell in c['afterSirens']: f.map[loc(cell)].is_siren = True
            for cell in c['afterEnemies']: f.map[loc(cell)].is_enemy = True
            f.movable_before = SelectedGrids([f.map[loc(x)] for x in c['before']])
            f.movable_before_normal = f.movable_before
            f.find_path_initial()
            f.track_movable(enemy_cleared=c['cleared'], siren=c['trackSiren'])
            tracks.append([dict(enemy=g.is_enemy, siren=g.is_siren, movable=g.is_movable) for g in f.map])
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in
            ['module/map/fleet.py', 'module/map/utils.py', 'module/map/map_base.py', 'module/map_detection/grid_info.py']}
        output.write_text(json.dumps(dict(matches=matches, rounds=rounds, tracks=tracks, scans=scans, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
