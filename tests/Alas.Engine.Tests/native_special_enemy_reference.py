"""Offline actual Map rescue/bouncing/goto/_goto replay; only I/O is synthetic."""
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
        from module.map.map import Map
        from module.map.map_base import CampaignMap
        from module.base.utils import location2node as node, node2location as loc
        from module.logger import logger
        import module.base.timer as timers
        logger.setLevel('CRITICAL')

        class Replay(Map):
            def __init__(self, case):
                self.case, self.trace = case, []
                rescue = case['kind'] == 'rescue'
                self.config = SimpleNamespace(MAP_HAS_SIREN=case['siren'], MAP_HAS_MOVABLE_ENEMY=case['movable'],
                    MAP_HAS_MOVABLE_NORMAL_ENEMY=False, MAP_HAS_MAZE=False, MAP_HAS_DECOY_ENEMY=False,
                    MAP_HAS_BOUNCING_ENEMY=case['enabled'] and not rescue, MAP_HAS_FORTRESS=False,
                    MAP_HAS_AMBUSH=False, MAP_HAS_FLEET_STEP=False, MAP_HAS_PORTAL=False,
                    MAP_HAS_LAND_BASED=False, MAP_FOCUS_ENEMY_AFTER_BATTLE=False,
                    Campaign_UseFleetLock=case['locked'], MAP_WALK_USE_CURRENT_FLEET=False,
                    Submarine_Mode='do_not_use', EnemyPriority_EnemyScaleBalanceWeight='default',
                    MOVABLE_ENEMY_TURN=(2,), MAP_SIREN_MOVE_WAIT=1.5,
                    FLEET_2=2 if case['second'] else 0, FLEET_BOSS=case['boss'])
                self.map = CampaignMap('offline-special-enemies')
                self.map.shape, self.map.map_data = 'E2', 'SP -- -- -- SP\n-- ME ME ME MB'
                self.map.load_map_data()
                self.map.spawn_data = [dict(battle=0, siren=1), dict(battle=1)] if rescue else [dict(battle=0)]
                self.map.grid_connection_initial()
                self.map.bouncing_enemy_data = [['B2', 'C2', 'D2'], ['C2', 'E2']]
                if not rescue and case['enabled']:
                    self.map.load_mechanism(bouncing_enemy=True)
                for cell in case['inactive']: self.map[loc(cell)].may_bouncing_enemy = False
                self.fleet_current_index = self.fleet_show_index = 1
                self.fleet_1_location = loc(case['start'])
                self.fleet_2_location = loc(case['second']) if case['second'] else ()
                self.map[self.fleet_1_location].is_fleet = True
                if self.fleet_2_location: self.map[self.fleet_2_location].is_fleet = True
                for cell in case['caught']: self.map[loc(cell)].is_caught_by_siren = True
                self.battle_count = self.siren_count = self.mystery_count = 0
                self.fleet_ammo, self.ammo_count = 5, 3
                self.round_reset()
                self.round_battle(after_battle=False)
                self.now, self.frames, self.visits = 100., 0, 0
                self.destination, self.pending = None, False
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)
                self.emotion = SimpleNamespace(is_calculate=case['calculate'],
                    wait=lambda fleet_index: self.trace.append('emotion:' + str(fleet_index)))
                self.find_path_initial()

            def screenshot(self):
                self.now += .25
                self.frames += 1
                if self.frames > 500: raise AssertionError('Native special enemy replay exceeded frame budget')
            def click(self, grid):
                self.destination = grid.location
                self.visits += 1
                self.pending = self.case['kind'] == 'rescue' or self.visits == self.case['hit']
                self.trace.append('tap:' + node(grid.location))
            def fleet_ensure(self, index):
                if self.fleet_current_index == index: return False
                self.trace.append('switch:' + str(index))
                self.fleet_current_index = self.fleet_show_index = index
                self.find_path_initial()
                return True
            def ensure_edge_insight(self, skip_first_update=True):
                assert skip_first_update
                self.trace.append('edges')
            def full_scan(self): self.trace.append('scan')
            def hp_retreat_triggered(self): return False
            def in_sight(self, *args, **kwargs): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                grid.predict_fleet = grid.predict_current_fleet = lambda: True
                return grid
            def ambush_color_initial(self): pass
            def enemy_searching_color_initial(self): pass
            def combat_appear(self): return self.pending
            def combat(self, **kwargs): self.pending = False
            def hp_get(self): pass
            def lv_get(self, **kwargs): pass
            def catch_camera_repositioning(self, grid): return False
            def predict(self): pass
            def _submarine_mode(self, expected): return None
            def handle_ambush(self): return False
            def handle_mystery(self, **kwargs): return False
            def handle_map_cat_attack(self): return False
            def handle_guild_popup_cancel(self): return False
            def handle_walk_out_of_step(self): return False
            def is_in_map(self): return True

        results = []
        for case in json.loads(inputs.read_text(encoding='utf-8')):
            replay = Replay(case)
            timers.time = lambda: replay.now
            result = replay.fleet_2_break_siren_caught() if case['kind'] == 'rescue' else replay.clear_bouncing_enemy()
            results.append(dict(result=result, trace=replay.trace, frames=replay.frames,
                fleet1=node(replay.fleet_1_location), fleet2=node(replay.fleet_2_location) if replay.fleet_2_location else None,
                index=replay.fleet_current_index, battle=replay.battle_count, siren=replay.siren_count,
                ammo=replay.fleet_ammo, round=replay.round,
                caught=[node(g.location) for g in replay.map if g.is_caught_by_siren],
                active=[node(g.location) for g in replay.map if g.may_bouncing_enemy],
                cleared=[node(g.location) for g in replay.map if g.is_cleared]))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in
            ['module/map/map.py', 'module/map/fleet.py', 'module/map/map_base.py', 'module/map_detection/grid_info.py']}
        output.write_text(json.dumps(dict(results=results, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
