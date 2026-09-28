"""Actual Fleet.goto/_goto and multi-fleet bookkeeping for shared final destinations; synthetic I/O."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, inputs, output = (Path(p).resolve() for p in sys.argv[1:])
    os.chdir(root)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        from module.map.fleet import Fleet
        from module.map.map_base import CampaignMap
        from module.base.utils import node2location as loc, location2node as node
        from module.logger import logger
        import module.base.timer as timers
        logger.setLevel('CRITICAL')

        class Replay(Fleet):
            def __init__(self, sample):
                self.sample = sample
                self.config = SimpleNamespace(FLEET_2=2, MAP_HAS_AMBUSH=sample['turning'], MAP_HAS_FLEET_STEP=sample['step'] > 0,
                    Fleet_Fleet1Step=sample['step'], Fleet_Fleet2Step=sample['step'], MAP_HAS_PORTAL=False,
                    Fleet_FleetOrder='fleet1_mob_fleet2_boss',
                    MAP_HAS_MAZE=False, MAP_HAS_LAND_BASED=False, MAP_HAS_MOVABLE_ENEMY=False, MAP_HAS_MOVABLE_NORMAL_ENEMY=False,
                    MAP_HAS_FORTRESS=False, MAP_HAS_DECOY_ENEMY=False, MAP_HAS_BOUNCING_ENEMY=False,
                    MAP_FOCUS_ENEMY_AFTER_BATTLE=False, MAP_WALK_USE_CURRENT_FLEET=False, Campaign_UseFleetLock=False,
                    Submarine_Mode='do_not_use')
                self.map = CampaignMap('offline-shared-fleet')
                self.map.shape, self.map.map_data = 'G1', 'SP -- -- SP -- -- --'
                self.map.load_map_data()
                self.map.grid_connection_initial()
                self.fleet_current_index = sample['active']
                self.fleet_1_location, self.fleet_2_location = (loc('A1'), loc('D1')) if sample['active'] == 1 else (loc('D1'), loc('A1'))
                self.battle_count = self.siren_count = self.mystery_count = self.carrier_count = 0
                self.fleet_ammo, self.ammo_count = 5, 3
                self.round_reset()
                self.frames, self.now, self.target = 0, 100., None
                self.taps, self.snapshots = [], []
                self.handled = False
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)
                target = self.map[loc('D1')]
                kind = sample['kind']
                if kind == 'enemy': target.is_enemy = target.may_enemy = True
                if kind == 'siren': target.is_siren = target.may_siren = True
                if kind == 'mystery': target.is_mystery = target.may_mystery = True
                if kind == 'ammo': target.is_ammo = target.may_ammo = True
                self.find_path_initial()
            def screenshot(self):
                self.frames += 1; self.now += .25
                if self.frames > 1000: raise AssertionError('Native shared-fleet replay stalled')
            def click(self, grid):
                self.taps.append(f'{self.fleet_current_index}:{node(grid.location)}')
                self.target, self.handled = grid.location, False
            def hp_retreat_triggered(self): return False
            def fleet_ensure(self, index):
                self.fleet_current_index = index
                self.find_path_initial()
            def in_sight(self, *args, **kwargs): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                grid.predict_fleet = grid.predict_current_fleet = lambda: True
                return grid
            def ambush_color_initial(self): pass
            def enemy_searching_color_initial(self): pass
            def combat_appear(self):
                grid = self.map[self.target]
                return not self.handled and (grid.is_enemy or grid.is_siren)
            def combat(self, **kwargs): self.handled = True
            def _expected_end(self, expected): return None
            def _submarine_mode(self, expected): return None
            def hp_get(self): pass
            def lv_get(self, **kwargs): pass
            def catch_camera_repositioning(self, grid): return False
            def predict(self): pass
            def handle_ambush(self): return False
            def handle_mystery(self, **kwargs):
                if not self.handled and self.map[self.target].is_mystery:
                    self.handled = True
                    return 'get_item'
                return False
            def handle_map_cat_attack(self): return False
            def handle_guild_popup_cancel(self): return False
            def handle_walk_out_of_step(self): return False
            def is_in_map(self): return True
            def save(self):
                self.snapshots.append(dict(active=self.fleet_current_index, fleet1=node(self.fleet_1_location),
                    fleet2=node(self.fleet_2_location), battles=self.battle_count, sirens=self.siren_count,
                    mysteries=self.mystery_count, ammo=self.fleet_ammo, stock=self.ammo_count,
                    flags=[[g.is_fleet, g.is_enemy, g.is_siren, g.is_mystery, g.is_ammo, g.is_cleared] for g in self.map],
                    costs=[[g.cost, g.cost_1, g.cost_2] for g in self.map]))

        results = []
        for sample in json.loads(inputs.read_text(encoding='utf-8')):
            replay = Replay(sample)
            timers.time = lambda: replay.now
            expected = dict(enemy='combat', siren='combat_siren', mystery='mystery').get(sample['kind'], '')
            replay.goto(loc('D1'), expected=expected)
            replay.save()
            replay.goto(loc('G1'))
            replay.save()
            replay.fleet_ensure(3 - sample['active'])
            replay.goto(loc('F1'))
            replay.save()
            results.append(dict(taps=replay.taps, snapshots=replay.snapshots))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest()
                   for p in ['module/map/fleet.py', 'module/map/map_base.py', 'module/map_detection/grid_info.py']}
        import cv2
        import numpy as np
        from module.handler.assets import IN_MAP
        from module.map.assets import FLEET_NUM_1
        from module.base.utils import load_image
        image = np.zeros((720, 1280, 3), dtype=np.uint8)
        x, y, right, bottom = IN_MAP.area
        image[y:bottom, x:right] = load_image(IN_MAP.file)[y:bottom, x:right]
        assert IN_MAP.appear_on(image)
        x, y, right, bottom = FLEET_NUM_1.area
        image[y:bottom, x:right] = load_image(FLEET_NUM_1.file)[y:bottom, x:right]
        cv2.imwrite(str(output.parent / 'in-map.png'), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
    output.write_text(json.dumps(dict(results=results, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
