"""Execute native fleet-lock walk, map_offensive and combat_appear with synthetic I/O."""
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
        from module.combat.combat import Combat
        from module.map.map_base import CampaignMap
        from module.base.utils import node2location as loc
        from module.logger import logger
        import module.base.timer as timers
        logger.setLevel('CRITICAL')

        class CombatStarted(Exception): pass

        class Replay(Fleet):
            def __init__(self, sample):
                self.sample, self.frames, self.now, self.trace = sample, 0, 100., []
                self.intervals = {}
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.config = SimpleNamespace(Campaign_UseFleetLock=sample['locked'], MAP_HAS_AMBUSH=False,
                    MAP_HAS_LAND_BASED=False, MAP_HAS_MOVABLE_ENEMY=False, MAP_HAS_MOVABLE_NORMAL_ENEMY=False,
                    MAP_HAS_FORTRESS=False, MAP_HAS_DECOY_ENEMY=False, MAP_HAS_BOUNCING_ENEMY=False,
                    MAP_HAS_MAZE=False, MAP_WALK_USE_CURRENT_FLEET=False, Submarine_Mode='do_not_use')
                self.map = CampaignMap('offline-walk-interruptions')
                self.map.shape, self.map.map_data = 'B1', 'SP --'
                self.map.load_map_data()
                self.fleet_current_index = 1
                self.fleet_1_location, self.fleet_2_location = loc('A1'), ()
                self.map[self.fleet_current].is_fleet = True
                self.round_reset()
                self.battle_count = self.mystery_count = 0
                self.view = SimpleNamespace(update=lambda **kwargs: None)
            @property
            def scene(self): return self.sample['scenes'][min(self.frames, len(self.sample['scenes'])-1)]
            def screenshot(self):
                self.frames += 1
                self.now += .25
                if self.frames > 700: raise AssertionError('Native walk stalled')
            def click(self, button): self.trace.append(f'click:{getattr(button, "name", "grid")}:{self.frames}')
            def appear(self, asset, offset=0, interval=0, threshold=10):
                if interval:
                    timer = self.intervals.setdefault(asset.name, timers.Timer(interval))
                    if timer.limit != interval: timer = self.intervals[asset.name] = timers.Timer(interval)
                    if not timer.reached(): return False
                found = asset.name in self.scene
                if found and interval: self.intervals[asset.name].reset()
                return found
            def appear_then_click(self, asset, **kwargs):
                if not self.appear(asset, **kwargs): return False
                self.click(asset)
                return True
            def interval_reset(self, asset):
                self.trace.append(f'reset:{asset.name}:{self.frames}')
                self.intervals.setdefault(asset.name, timers.Timer(3)).reset()
            def handle_retirement(self):
                self.trace.append(f'retirement:{self.frames}')
                if '$retire' in self.scene: self.now += self.sample['retirementSeconds']; return True
                return False
            def handle_combat_low_emotion(self):
                self.trace.append(f'emotion:{self.frames}')
                return '$emotion' in self.scene
            def is_in_map(self): return '$map' in self.scene
            def is_combat_loading(self):
                self.trace.append(f'loading:{self.frames}')
                return '$loading' in self.scene
            def combat(self, **kwargs): raise CombatStarted()
            def _expected_end(self, expected): return None
            def _submarine_mode(self, expected): return None
            def hp_retreat_triggered(self): return False
            def fleet_ensure(self, *args): pass
            def in_sight(self, *args, **kwargs): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                grid.predict_fleet = lambda: '$marker' in self.scene
                grid.predict_current_fleet = lambda: '$marker' in self.scene
                return grid
            def ambush_color_initial(self): pass
            def enemy_searching_color_initial(self): pass
            def handle_ambush(self): return False
            def handle_mystery(self, **kwargs): return False
            def handle_map_cat_attack(self): return False
            def handle_guild_popup_cancel(self): return False
            def handle_walk_out_of_step(self): return False
            def find_path_initial(self): pass
            def predict(self): raise AssertionError('Unexpected timeout recovery')

        results = []
        for sample in json.loads(inputs.read_text(encoding='utf-8')):
            replay = Replay(sample)
            timers.time = lambda: replay.now
            result = None
            try:
                if sample['method'] == 'appearance': result = Combat.combat_appear(replay)
                elif sample['method'] == 'offensive': Combat.map_offensive(replay)
                else:
                    replay._goto(loc('B1'))
                    result = 'arrived'
            except CombatStarted: result = 'combat'
            results.append(dict(result=result, frames=replay.frames, trace=replay.trace))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest()
                   for p in ['module/map/fleet.py', 'module/combat/combat.py']}
        import cv2
        import numpy as np
        from module.handler.assets import IN_MAP, POPUP_CANCEL, POPUP_CONFIRM
        from module.retire.assets import RETIRE_APPEAR_1
        from module.template.assets import TEMPLATE_COMBAT_LOADING
        from module.base.utils import load_image
        for name, assets in [('map', [IN_MAP]), ('emotion', [POPUP_CANCEL, POPUP_CONFIRM]), ('dock', [RETIRE_APPEAR_1]), ('loading', [])]:
            image = np.zeros((720, 1280, 3), dtype=np.uint8)
            for button in assets:
                x, y, right, bottom = button.area
                image[y:bottom, x:right] = load_image(button.file)[y:bottom, x:right]
            if name == 'loading':
                raw = TEMPLATE_COMBAT_LOADING.image
                if raw.ndim == 2: raw = np.repeat(raw[:, :, None], 3, axis=2)
                h, w = raw.shape[:2]
                image[627:627+h, 400:400+w] = raw
            cv2.imwrite(str(output.parent / (name + '.png')), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
    output.write_text(json.dumps(dict(results=results, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
