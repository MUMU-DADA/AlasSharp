"""Actual native goto/_goto maze replay with synthetic images, input and time only."""
import contextlib
import hashlib
import inspect
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
        from module.exception import MapEnemyMoved
        from module.logger import logger
        import module.base.timer as timers
        logger.setLevel('CRITICAL')

        class Replay(Map):
            def __init__(self, case):
                self.case = case
                self.config = SimpleNamespace(MAP_HAS_LAND_BASED=False, MAP_HAS_MOVABLE_ENEMY=False,
                    MAP_HAS_MOVABLE_NORMAL_ENEMY=False, MAP_HAS_MAZE=True, MAP_HAS_DECOY_ENEMY=False,
                    MAP_HAS_BOUNCING_ENEMY=False, MAP_HAS_FORTRESS=False, MAP_HAS_AMBUSH=False,
                    MAP_HAS_FLEET_STEP=case['step'] > 0, MAP_HAS_PORTAL=False, Campaign_UseFleetLock=False,
                    MAP_WALK_USE_CURRENT_FLEET=case['currentOnly'], Submarine_Mode='do_not_use',
                    Fleet_Fleet1Step=case['step'] or 3, Fleet_Fleet2Step=2)
                self.map = CampaignMap('offline-maze')
                self.map.shape, self.map.map_data = 'G1', '-- -- -- -- -- -- --'
                self.map.load_map_data()
                self.map.grid_connection_initial()
                self.map._load_maze_data([('D1',), ('A1',), ('G1',)])
                self.fleet_current_index = self.fleet_show_index = 1
                self.fleet_1_location = loc(case['start'])
                self.fleet_2_location = loc(case['second']) if case['second'] else ()
                self.map[self.fleet_1_location].is_fleet = True
                if self.fleet_2_location: self.map[self.fleet_2_location].is_fleet = True
                self.battle_count = self.siren_count = self.mystery_count = 0
                self.round_reset()
                self.round = case['round']
                self.now, self.frames = 100., 0
                self.taps, self.waits = [], []
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)
                self.find_path_initial()

            @property
            def fleets_reversed(self): return False
            def screenshot(self):
                self.frames += 1
                self.now += .25
                if self.frames > 200: raise AssertionError('Native maze replay exceeded frame budget')
            def click(self, grid): self.taps.append(node(grid.location))
            def hp_retreat_triggered(self): return False
            def fleet_ensure(self, index): return False
            def in_sight(self, *args, **kwargs): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                grid.predict_fleet = lambda: not self.case['currentOnly']
                grid.predict_current_fleet = lambda: True
                return grid
            def ambush_color_initial(self): pass
            def enemy_searching_color_initial(self): pass
            def combat_appear(self): return False
            def handle_ambush(self): return False
            def handle_mystery(self, **kwargs): return False
            def handle_map_cat_attack(self): return False
            def handle_guild_popup_cancel(self): return False
            def handle_walk_out_of_step(self): return False
            def is_in_map(self): return True
            def _goto(self, location, expected=''):
                # Inspect which actual native goto branch called _goto; do not replace its decision.
                waiting = 'grids' in inspect.currentframe().f_back.f_locals
                origin, before, frames = node(self.fleet_current), self.round, self.frames
                try:
                    return super()._goto(location, expected)
                finally:
                    if waiting:
                        self.waits.append(dict(origin=origin, target=node(self.fleet_current), before=before,
                            after=self.round, frames=self.frames - frames))

        results = []
        for case in json.loads(inputs.read_text(encoding='utf-8')):
            replay = Replay(case)
            timers.time = lambda: replay.now
            moved = False
            try: replay.goto(loc('D1'))
            except MapEnemyMoved: moved = True
            results.append(dict(moved=moved, taps=replay.taps, frames=replay.frames, waits=replay.waits,
                fleet=node(replay.fleet_current), round=replay.round, costs=[g.cost for g in replay.map]))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in
            ['module/map/fleet.py', 'module/map/map_base.py', 'module/map/map_grids.py']}
        output.write_text(json.dumps(dict(results=results, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
