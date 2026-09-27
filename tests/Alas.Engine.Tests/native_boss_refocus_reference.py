"""Actual Fleet boss-refocus and Camera update/edge/focus replay; synthetic capture and swipe I/O."""
import contextlib
import hashlib
import itertools
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, output = [Path(arg).resolve() for arg in sys.argv[1:]]
    sys.path.insert(0, str(root))
    os.chdir(output.parent)
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import numpy as np
        import module.base.timer as timers
        from module.map.fleet import Fleet
        from module.map.camera import Camera
        from module.config.config_manual import ManualConfig
        from module.exception import MapDetectionError
        from module.logger import logger
        from module.map_detection.view import View
        from module.map_detection.grid import Grid
        logger.setLevel('CRITICAL')

        class Backend:
            left_edge = right_edge = lower_edge = upper_edge = False
            def load(self, image): pass
            def generate(self):
                def p(x, y): return [100 + 100*x + 8*y, 100 + 85*y]
                for y in range(3):
                    for x in range(3):
                        yield (x, y), np.array([p(x,y), p(x+1,y), p(x,y+1), p(x+1,y+1)], dtype=float)

        def view(success):
            v = object.__new__(View)
            v.config = SimpleNamespace(DETECTING_AREA=(0, 0, 1280, 720), SCREEN_CENTER=(262, 227.5),
                HOMO_TILE=(100, 100), GRID_IMAGE_A_MULTIPLY=1, MAP_ENEMY_TEMPLATE=[], MAP_HAS_SIREN=False)
            v.grid_class, v.backend = Grid, Backend()
            v._image_clear_ui = lambda image: image
            v.load(np.zeros((720, 1280, 3), dtype=np.uint8))
            v.left_edge = success in (1, 2)
            v.lower_edge = success == 2
            return v

        class Replay(Fleet):
            def __init__(self, sample):
                self.sample = sample
                self.config = ManualConfig()
                self.config.MAP_ENSURE_EDGE_INSIGHT_CORNER = 'bottom-left'
                self.config.MAP_SWIPE_OPTIMIZE = self.config.MAP_SWIPE_PREDICT = False
                self.config.DEVICE_CONTROL_METHOD = 'adb'
                self.config.MAP_BOSS_APPEAR_REFOCUS_SWIPE = sample['config']
                self.camera = tuple(np.subtract(sample['start'], (1, 1)))
                self.map = SimpleNamespace(shape=(8, 6))
                self.view = view(0)
                self._prev_view = self._prev_swipe = None
                self.frames = self.success = 0
                self.now = 100.
                self.swipes = []
                self.device = SimpleNamespace(screenshot=self.screenshot, swipe_vector=self.swipe,
                    _screenshot_interval=SimpleNamespace(clear=lambda: None))
            def screenshot(self):
                self.now += .4
                self.frames += 1
                if self.frames > 100: raise AssertionError('Boss refocus replay did not terminate')
            def _update_view(self):
                failure = self.sample['failure']
                if failure == 'io': raise OSError('Synthetic capture error')
                if ((failure in ('geometry', 'swipe') and self.frames <= 13) or
                        failure == 'transient' and self.frames <= 2 or
                        failure == 'edge' and self.success == 1 or
                        failure == 'focus' and self.success == 2):
                    raise MapDetectionError('Synthetic geometry error')
                self.success += 1
                self.view = view(self.success)
                return True
            def swipe(self, vector, **kwargs):
                self.swipes.append(vector.tolist())
                if self.sample['failure'] == 'swipe': raise OSError('Synthetic swipe error')
            def predict(self): pass
            def show_camera(self): pass

        results = []
        for preset, config, start, failure in itertools.product(
                [None, [0, 0], [-3, 0], [0, -2]], [[0, 0], [1, -1]], [[5, 4], [2, 2]],
                ['none', 'transient', 'geometry', 'io', 'swipe', 'edge', 'focus']):
            sample = dict(preset=preset, config=config, start=start, failure=failure)
            replay = Replay(sample)
            timers.time = lambda: replay.now
            error = None
            try: replay.handle_boss_appear_refocus(preset)
            except MapDetectionError: error = 'geometry'
            except OSError: error = 'io'
            results.append(dict(sample=sample, error=error, frames=replay.frames, swipes=replay.swipes,
                position=np.add(replay.camera, (1, 1)).tolist()))

        gates = []
        for waves, battle in itertools.product([[], [dict(battle=0, boss=1)],
                [dict(battle=0), dict(battle=2, boss=1), dict(battle=5, boss=2)],
                [dict(battle=0), dict(battle=1, enemy=2), dict(battle=3, boss=1)]], range(7)):
            replay = object.__new__(Fleet)
            replay.map = SimpleNamespace(spawn_data=waves)
            replay.battle_count = battle + 1
            gates.append(dict(waves=waves, battle=battle, refocus=replay.catch_camera_repositioning(None)))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in
            ['module/map/fleet.py', 'module/map/camera.py', 'module/config/config_manual.py']}
        output.write_text(json.dumps(dict(results=results, gates=gates, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
