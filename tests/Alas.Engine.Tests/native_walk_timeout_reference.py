"""Native Fleet._goto timeout/retry and current-marker traces; no real device."""
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
        from module.base.utils import node2location as loc
        from module.logger import logger
        import module.base.timer as timers
        import cv2
        import numpy as np
        from module.handler.assets import IN_MAP
        from module.base.utils import load_image
        logger.setLevel('CRITICAL')

        class Replay(Fleet):
            def __init__(self, sample):
                self.sample = sample
                self.config = SimpleNamespace(MAP_HAS_AMBUSH=False, MAP_HAS_LAND_BASED=False,
                    MAP_HAS_MOVABLE_ENEMY=False, MAP_HAS_MOVABLE_NORMAL_ENEMY=False, MAP_HAS_FORTRESS=False,
                    MAP_HAS_DECOY_ENEMY=False, MAP_HAS_BOUNCING_ENEMY=False, MAP_HAS_MAZE=False,
                    Campaign_UseFleetLock=False, Submarine_Mode='do_not_use', MAP_WALK_USE_CURRENT_FLEET=sample['useCurrent'])
                self.map = CampaignMap('offline-timeout')
                self.map.shape, self.map.map_data = 'B1', 'SP --'
                self.map.load_map_data()
                self.fleet_current_index = sample['fleet']
                self.fleet_1_location = loc('A1') if sample['fleet'] == 1 else ()
                self.fleet_2_location = loc('A1') if sample['fleet'] == 2 else ()
                self.map[self.fleet_current].is_fleet = True
                self.round_reset()
                self.battle_count = self.mystery_count = 0
                self.now, self.frames, self.attempt_frames = 100., 0, 0
                self.taps, self.recoveries = [], []
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)
                self.predicted = False
            def screenshot(self):
                self.frames += 1; self.attempt_frames += 1; self.now += .25
                if self.frames > 700: raise AssertionError('Native walk did not finish')
            def click(self, grid): self.taps.append(self.frames); self.attempt_frames = 0
            def hp_retreat_triggered(self): return False
            def fleet_ensure(self, *args): pass
            def in_sight(self, *args, **kwargs): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                def present(): return len(self.taps) > self.sample['missedTaps'] and self.attempt_frames >= self.sample['appearAt']
                grid.predict_fleet = lambda: present() and self.sample['marker'] == 'fleet'
                grid.predict_current_fleet = present
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
            def find_path_initial(self): pass
            def predict(self): self.predicted = True
            def ensure_edge_insight(self, skip_first_update=True):
                assert not skip_first_update and self.predicted
                self.predicted = False
                self.recoveries.append(self.frames)
                self.screenshot()

        results = []
        for sample in json.loads(inputs.read_text(encoding='utf-8')):
            replay = Replay(sample)
            timers.time = lambda: replay.now
            replay._goto(loc('B1'), expected=sample['expected'])
            results.append(dict(frames=replay.frames, taps=replay.taps, recoveries=replay.recoveries))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in ['module/map/fleet.py', 'module/map/camera.py']}
        image = np.zeros((720, 1280, 3), dtype=np.uint8)
        x, y, right, bottom = IN_MAP.area
        image[y:bottom, x:right] = load_image(IN_MAP.file)[y:bottom, x:right]
        assert IN_MAP.appear_on(image)
        cv2.imwrite(str(output.parent/'in-map.png'), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
    output.write_text(json.dumps(dict(results=results, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
