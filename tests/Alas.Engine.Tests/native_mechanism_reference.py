"""Offline native clear_mechanism/goto/_goto replay; I/O and time are synthetic."""
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
        from module.map.map_grids import SelectedGrids
        from module.base.utils import location2node, node2location
        from module.exception import MapEnemyMoved
        from module.logger import logger
        import module.base.timer as timers
        logger.setLevel('CRITICAL')

        class Replay(Map):
            def __init__(self, case):
                self.config = SimpleNamespace(MAP_HAS_LAND_BASED=case['enabled'], MAP_HAS_MOVABLE_ENEMY=False,
                    MAP_HAS_MOVABLE_NORMAL_ENEMY=False, MAP_HAS_MAZE=False, MAP_HAS_DECOY_ENEMY=False,
                    MAP_HAS_BOUNCING_ENEMY=False, MAP_HAS_FORTRESS=False, MAP_HAS_AMBUSH=False,
                    MAP_HAS_FLEET_STEP=False, MAP_HAS_PORTAL=False, Campaign_UseFleetLock=False,
                    MAP_WALK_USE_CURRENT_FLEET=False, Submarine_Mode=case.get('submarineMode', 'do_not_use'))
                self.map = CampaignMap('offline-mechanism')
                self.map.shape, self.map.map_data = 'E5', case['tiles']
                self.map.load_map_data()
                self.map.grid_connection_initial()
                self.map._load_land_base_data([['C3', case['direction']]])
                for i, grid in enumerate(self.map):
                    grid.weight = case['weights'][i]
                    grid.mechanism_wait = case['wait']
                self.fleet_current_index = self.fleet_show_index = 1
                self.fleet_1_location, self.fleet_2_location = node2location(case['start']), ()
                self.map[self.fleet_1_location].is_fleet = True
                self.battle_count = self.siren_count = self.mystery_count = 0
                self.now, self.frames = 100., 0
                self.taps = []
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)
                self.find_path_initial()

            def screenshot(self):
                self.frames += 1
                self.now += .25
                if self.frames > 100: raise AssertionError('Native mechanism replay did not terminate')
            def click(self, grid): self.taps.append(location2node(grid.location))
            def hp_retreat_triggered(self): return False
            def fleet_ensure(self, index): return False
            def in_sight(self, *args, **kwargs): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                grid.predict_fleet = grid.predict_current_fleet = lambda: True
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

        results = []
        for case in json.loads(inputs.read_text(encoding='utf-8')):
            replay = Replay(case)
            timers.time = lambda: replay.now
            grids = None if case['selection'] is None else SelectedGrids([replay.map[node2location(c)] for c in case['selection']])
            moved = False
            try:
                result = replay.clear_mechanism(grids)
                assert result is False
            except MapEnemyMoved:
                moved = True
            results.append(dict(moved=moved, taps=replay.taps, frames=replay.frames,
                fleet=location2node(replay.fleet_current), battle=replay.battle_count,
                cells=[dict(trigger=g.is_mechanism_trigger, block=g.is_mechanism_block, cost=g.cost) for g in replay.map],
                repeat=replay.clear_mechanism(grids)))
        sources = {path: hashlib.sha256((root/path).read_bytes()).hexdigest() for path in
            ['module/map/map.py', 'module/map/fleet.py', 'module/map_detection/grid_info.py', 'module/map/map_base.py']}
        output.write_text(json.dumps(dict(results=results, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
