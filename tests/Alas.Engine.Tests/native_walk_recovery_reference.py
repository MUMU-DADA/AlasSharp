"""Actual Fleet.goto/_goto and walk-message replay; all device I/O is synthetic."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, server_name, source, output = Path(sys.argv[1]).resolve(), sys.argv[2], Path(sys.argv[3]).resolve(), Path(sys.argv[4]).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    os.chdir(root)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        import module.config.server as server
        server.server = server_name
        import module.base.timer as timers
        import module.handler.ambush as ambush
        from module.handler.info_handler import info_letter_preprocess
        from module.base.utils import crop, load_image, node2location as loc, location2node as node
        from module.map.fleet import Fleet
        from module.map.map_base import CampaignMap
        from module.exception import MapWalkError
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class Replay(Fleet):
            def __init__(self, sample):
                self.sample = sample
                self.config = SimpleNamespace(MAP_HAS_FLEET_STEP=True, Fleet_Fleet1Step=sample['step'], Fleet_Fleet2Step=sample['step'],
                    MAP_HAS_PORTAL=False, MAP_HAS_MAZE=False, MAP_HAS_AMBUSH=False, MAP_HAS_LAND_BASED=False,
                    MAP_HAS_MOVABLE_ENEMY=False, MAP_HAS_MOVABLE_NORMAL_ENEMY=False, MAP_HAS_DECOY_ENEMY=False,
                    MAP_HAS_BOUNCING_ENEMY=False, MAP_HAS_FORTRESS=False, MAP_FOCUS_ENEMY_AFTER_BATTLE=False,
                    MAP_WALK_USE_CURRENT_FLEET=False, Campaign_UseFleetLock=False, Submarine_Mode='do_not_use')
                self.map = CampaignMap('offline-walk')
                self.map.shape = sample['target']
                self.map.map_data = sample['tiles']
                self.map.load_map_data()
                self.map.grid_connection_initial()
                self.fleet_current_index = self.fleet_show_index = sample['fleet']
                self.fleet_1_location = loc('A1') if sample['fleet'] == 1 else ()
                self.fleet_2_location = loc('A1') if sample['fleet'] == 2 else ()
                self.map[self.fleet_current].is_fleet = True
                self.battle_count = self.siren_count = self.mystery_count = 0
                self.fleet_ammo = 5
                self.round_reset()
                self.now, self.frames, self.recoveries = 100., 0, 0
                self.taps, self.calls = [], []
                self.pending_combat = self.pending_mystery = False
                self.delivered = False
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)
                self.find_path_initial()
            @property
            def fleets_reversed(self): return False
            def screenshot(self):
                self.frames += 1
                self.now += .25
                if self.frames > 1000: raise AssertionError('Walk replay exceeded frame budget')
            def click(self, grid):
                self.taps.append(node(grid.location))
                final = node(grid.location) == self.sample['target']
                rejected = len(self.taps) in self.sample['reject']
                allowed = not self.delivered and (not rejected or self.sample['beforeReject'])
                self.pending_combat = final and self.sample['kind'] not in ['raw', 'mystery'] and allowed
                self.pending_mystery = final and self.sample['kind'] == 'mystery' and allowed
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
            def combat_appear(self): return self.pending_combat
            def combat(self, **kwargs):
                self.pending_combat = False
                self.delivered = True
            def hp_get(self): pass
            def lv_get(self, **kwargs): pass
            def catch_camera_repositioning(self, grid): return False
            def handle_ambush(self): return False
            def handle_mystery(self, **kwargs):
                found, self.pending_mystery = self.pending_mystery, False
                if found: self.delivered = True
                return 'get_item' if found else False
            def handle_map_cat_attack(self): return False
            def handle_guild_popup_cancel(self): return False
            def handle_walk_out_of_step(self): return len(self.taps) in self.sample['reject']
            def is_in_map(self): return True
            def predict(self): pass
            def ensure_edge_insight(self, **kwargs): self.recoveries += 1
            def _submarine_mode(self, expected): return None
            def _goto(self, location, expected=''):
                self.calls.append(dict(target=node(location), expected=expected))
                return super()._goto(location, expected)

        results = []
        for sample in json.loads(source.read_text(encoding='utf-8')):
            replay = Replay(sample)
            timers.time = lambda: replay.now
            expected = {'raw': '', 'mystery': 'mystery', 'enemy': 'combat', 'boss': 'combat_boss', 'siren': 'combat_siren'}[sample['kind']]
            error = False
            try: replay.goto(loc(sample['target']), expected=expected)
            except MapWalkError: error = True
            results.append(dict(taps=replay.taps, calls=replay.calls, recoveries=replay.recoveries, error=error,
                fleet=node(replay.fleet_current), battle=replay.battle_count, siren=replay.siren_count,
                mystery=replay.mystery_count, ammo=replay.fleet_ammo))

        template = ambush.TEMPLATE_MAP_WALK_OUT_OF_STEP
        fixtures = []
        rng = np.random.default_rng(9873)
        for present in [False, True]:
            for noise in [0, 25, 90]:
                image = rng.integers(0, 80, (720, 1280, 3), dtype=np.uint8)
                x, y, right, bottom = ambush.INFO_BAR_DETECT.area
                # Real row-peak recognition sees one native blue information-bar border.
                bx, by, br, bb = ambush.INFO_BAR_AREA.area
                image[by+8:by+11, bx:br] = (107, 158, 255)
                if present:
                    raw = load_image(template.file)
                    if raw.ndim == 2: raw = np.repeat(raw[:, :, None], 3, axis=2)
                    h, w = raw.shape[:2]
                    assert x + 20 + w <= right and y + 3 + h <= bottom
                    image[y+3:y+3+h, x+20:x+20+w] = np.clip(raw.astype(int) + rng.integers(-noise, noise+1, raw.shape), 0, 255)
                processed = info_letter_preprocess(crop(image, ambush.INFO_BAR_DETECT.area))
                matched = template.match(processed)
                score = float(cv2.minMaxLoc(cv2.matchTemplate(processed, template.image, cv2.TM_CCOEFF_NORMED))[1])
                filename = f'walk-{server_name}-{present}-{noise}.png'
                cv2.imwrite(str(output.parent / filename), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
                fixtures.append(dict(image=filename, matched=matched, score=score))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in
            ['module/map/fleet.py', 'module/map/map_base.py', 'module/handler/ambush.py', 'module/handler/info_handler.py']}
    output.write_text(json.dumps(dict(results=results, fixtures=fixtures, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
