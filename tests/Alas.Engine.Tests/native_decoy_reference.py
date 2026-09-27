"""Run native Fleet._goto and full_scan gates. Image, device and battle I/O are synthetic."""
import contextlib
import hashlib
import itertools
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch


def main():
    root, output = [Path(arg).resolve() for arg in sys.argv[1:]]
    sys.path.insert(0, str(root))
    os.chdir(output.parent)
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        from module.map.fleet import Fleet
        from module.map.camera import Camera
        from module.map.map_base import CampaignMap
        from module.map_detection.grid_info import GridInfo
        from module.exception import MapEnemyMoved
        from module.base.utils import node2location as loc, location2node as node
        from module.logger import logger
        import module.base.timer as timers
        logger.setLevel('CRITICAL')

        class Replay(Fleet):
            def __init__(self, sample):
                self.sample = sample
                self.config = SimpleNamespace(MAP_HAS_MOVABLE_ENEMY=False, MAP_HAS_MOVABLE_NORMAL_ENEMY=False,
                    MAP_HAS_DECOY_ENEMY=sample['enabled'], MAP_HAS_MAZE=sample['round'] is not None,
                    MAP_HAS_FORTRESS=False, MAP_HAS_BOUNCING_ENEMY=False, MAP_HAS_LAND_BASED=False,
                    MAP_FOCUS_ENEMY_AFTER_BATTLE=False, Campaign_UseFleetLock=False,
                    MAP_WALK_USE_CURRENT_FLEET=False, Submarine_Mode='do_not_use', MAP_HAS_AMBUSH=False)
                self.map = CampaignMap('offline-decoy')
                self.map.shape, self.map.map_data = 'C1', 'SP ME --'
                self.map.load_map_data()
                self.map.grid_connection_initial()
                self.fleet_current_index = self.fleet_show_index = 1
                self.fleet_1_location, self.fleet_2_location = loc('A1'), ()
                self.map[self.fleet_1_location].is_fleet = True
                target = self.map[loc('B1')]
                setattr(target, 'is_' + sample['kind'], True)
                self.battle_count = self.siren_count = self.mystery_count = 0
                self.fleet_ammo = 5
                self.round_reset()
                self.round = sample['round'] or 0
                self.now, self.frames, self.pending = 100., 0, False
                self.taps = []
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)
                self.find_path_initial()

            def screenshot(self):
                self.frames += 1
                self.now += .25
                if self.frames > 100: raise AssertionError('Decoy replay did not terminate')
            def click(self, grid):
                self.taps.append(node(grid.location))
                self.pending = self.sample['combat']
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

        cases = []
        for enabled, kind, combat, phase in itertools.product([False, True],
                ['enemy', 'siren', 'boss', 'fortress'], [False, True], [None, 0, 2]):
            cases.append(dict(enabled=enabled, kind=kind, expectation=kind, combat=combat, round=phase))
        # Native expected-result strings come from the caller, not the observed flags.
        for kind, expectation, combat in itertools.product(['enemy', 'siren', 'boss', 'fortress'],
                ['enemy', 'siren', 'boss', 'fortress', 'none'], [False, True]):
            if kind != expectation:
                cases.append(dict(enabled=True, kind=kind, expectation=expectation, combat=combat, round=None))
        results = []
        for sample in cases:
            replay = Replay(sample)
            timers.time = lambda: replay.now
            moved = False
            expectation = sample['expectation']
            expected = '' if expectation == 'none' else 'combat' if expectation == 'enemy' else 'combat_' + expectation
            try: replay._goto(loc('B1'), expected=expected)
            except MapEnemyMoved: moved = True
            results.append(dict(sample=sample, moved=moved, fleet=node(replay.fleet_current),
                frames=replay.frames, taps=replay.taps, battle=replay.battle_count,
                siren=replay.siren_count, ammo=replay.fleet_ammo, round=replay.round,
                enemy=replay.map[loc('B1')].is_enemy, cleared=replay.map[loc('B1')].is_cleared))

        scans = []
        for enabled, mode in itertools.product([False, True], ['normal', 'init', 'movable', 'carrier', 'decoy']):
            replay = Replay(dict(enabled=enabled, kind='enemy', combat=False, round=None))
            replay.config.FLEET_2 = 0
            replay.carrier_count = 0
            effective = []
            def scan(self, **kwargs):
                effective.append(kwargs['mode'])
                observed = GridInfo()
                observed.is_enemy = True
                self.map[loc('C1')].merge(observed, mode=kwargs['mode'])
            with patch.object(Camera, 'full_scan', scan):
                replay.full_scan(mode=mode)
            scans.append(dict(enabled=enabled, mode=mode, effective=effective[0],
                enemy=replay.map[loc('C1')].is_enemy))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in
            ['module/map/fleet.py', 'module/map/map_base.py', 'module/map_detection/grid_info.py']}
        output.write_text(json.dumps(dict(results=results, scans=scans, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
