"""Execute native map popup handlers and Fleet._goto; synthetic observations/devices only."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, server_name, inputs, output = Path(sys.argv[1]).resolve(), sys.argv[2], Path(sys.argv[3]).resolve(), Path(sys.argv[4]).resolve()
    os.chdir(root)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        import module.config.server as server
        server.server = server_name
        import module.base.timer as timers
        from module.map.fleet import Fleet
        from module.map.map_base import CampaignMap
        from module.map.map_operation import MapOperation
        from module.map.assets import MAP_CAT_ATTACK, MAP_CAT_ATTACK_MIRROR
        from module.handler.assets import GUILD_POPUP_CONFIRM, GUILD_POPUP_CANCEL
        from module.base.utils import node2location as loc, location2node as node
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class Replay(Fleet):
            def __init__(self, sample):
                self.sample = sample
                self.config = SimpleNamespace(MAP_HAS_PORTAL=False, MAP_HAS_MAZE=False, MAP_HAS_AMBUSH=False,
                    MAP_HAS_LAND_BASED=False, MAP_HAS_MOVABLE_ENEMY=False, MAP_HAS_MOVABLE_NORMAL_ENEMY=False,
                    MAP_HAS_DECOY_ENEMY=False, MAP_HAS_BOUNCING_ENEMY=False, MAP_HAS_FORTRESS=False,
                    MAP_WALK_USE_CURRENT_FLEET=False, Campaign_UseFleetLock=False, Submarine_Mode='do_not_use')
                self.map = CampaignMap('offline-popups')
                self.map.shape = 'B1'
                self.map.map_data = 'SP --'
                self.map.load_map_data()
                self.map.grid_connection_initial()
                self.fleet_current_index = 1
                self.fleet_1_location = loc('A1')
                self.map[self.fleet_current].is_fleet = True
                self.battle_count = self.mystery_count = 0
                self.round_reset()
                self.map_is_clear_mode = sample['clear']
                self.map_cat_attack_timer = timers.Timer(2)
                self.guild_timer = timers.Timer(2)
                self.now, self.frames = 100., 0
                self.clicks, self.queries = [], []
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)

            @property
            def signal(self): return self.sample['signals'][min(max(self.frames-1, 0), len(self.sample['signals'])-1)]
            def screenshot(self):
                self.frames += 1
                self.now += .25
                if self.frames > 500: raise AssertionError('Walk did not finish')
            def click(self, button):
                self.clicks.append([self.frames, node(button.location) if hasattr(button, 'location') else getattr(button, 'name', 'grid')])
            def image_color_count(self, button, color, threshold, count):
                assert color == (255, 231, 123) and threshold == 30
                name = 'cat' if button is MAP_CAT_ATTACK else 'mirror'
                self.queries.append([self.frames, name])
                return self.signal[name] > count
            def appear(self, button, offset, interval=0):
                assert offset == (3, 30)
                if interval and not self.guild_timer.reached(): return False
                name = 'confirm' if button is GUILD_POPUP_CONFIRM else 'cancel'
                self.queries.append([self.frames, name])
                found = self.signal[name]
                if found and interval: self.guild_timer.reset()
                return found
            def hp_retreat_triggered(self): return False
            def fleet_ensure(self, *args): pass
            def in_sight(self, *args, **kwargs): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                grid.predict_fleet = grid.predict_current_fleet = lambda: self.signal['marker']
                return grid
            def ambush_color_initial(self): pass
            def enemy_searching_color_initial(self): pass
            def combat_appear(self): return False
            def handle_ambush(self): return False
            def handle_mystery(self, **kwargs): return False
            def handle_walk_out_of_step(self): return False
            def is_in_map(self): return True
            def find_path_initial(self): pass
            def predict(self): pass

        results = []
        for sample in json.loads(inputs.read_text(encoding='utf-8')):
            replay = Replay(sample)
            timers.time = lambda: replay.now
            if sample['walk']:
                replay._goto(loc('B1'))
            else:
                for _ in sample['signals']:
                    replay.screenshot()
                    if not replay.handle_map_cat_attack(): replay.handle_guild_popup_cancel()
            results.append(dict(frames=replay.frames, clicks=replay.clicks, queries=replay.queries,
                fleet=node(replay.fleet_current), battle=replay.battle_count, mystery=replay.mystery_count))

        class PixelReplay(MapOperation):
            def __init__(self, image, clear):
                self.device = SimpleNamespace(image=image, click=lambda button: None)
                self.map_cat_attack_timer = timers.Timer(2)
                self.map_is_clear_mode = clear

        from module.map.assets import FLEET_NUM_1
        from module.base.utils import load_image
        fixtures = []
        for clear in [False, True]:
            for mirror in [False, True]:
                for count in [99, 100, 101, 199, 200, 201]:
                    image = np.zeros((720, 1280, 3), dtype=np.uint8)
                    x, y, right, bottom = FLEET_NUM_1.area
                    image[y:bottom, x:right] = load_image(FLEET_NUM_1.file)[y:bottom, x:right]
                    button = MAP_CAT_ATTACK_MIRROR if mirror else MAP_CAT_ATTACK
                    x, y, right, bottom = button.area
                    coords = np.arange(count)
                    image[y + coords // (right-x), x + coords % (right-x)] = (255, 231, 123)
                    view = PixelReplay(image, clear)
                    matched = bool(view.handle_map_cat_attack())
                    filename = f'popup-{server_name}-{clear}-{mirror}-{count}.png'
                    cv2.imwrite(str(output.parent / filename), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
                    fixtures.append(dict(image=filename, clear=clear, matched=matched))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in
            ['module/map/fleet.py', 'module/map/map_operation.py', 'module/handler/info_handler.py']}
    output.write_text(json.dumps(dict(results=results, fixtures=fixtures, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
