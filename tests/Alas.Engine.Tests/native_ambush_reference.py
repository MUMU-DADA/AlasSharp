"""Offline native ambush handlers and original-asset CV fixtures; no device/config I/O."""
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
    root, server_name, output = Path(sys.argv[1]).resolve(), sys.argv[2], Path(sys.argv[3]).resolve()
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
        from module.base.utils import crop, load_image
        from module.handler.info_handler import info_letter_preprocess
        from module.handler.enemy_searching import EnemySearchingHandler
        from module.map.fleet import Fleet
        from module.map.map_base import CampaignMap
        from module.base.utils import node2location as loc, location2node as node
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class Replay(ambush.AmbushHandler):
            def __init__(self, sample):
                self.sample = sample
                self.config = SimpleNamespace(Campaign_AmbushEvade=sample['evade'], MAP_HAS_AMBUSH=True)
                self.fleet_show_index = 2
                self.frames, self.now, self.clicks, self.trace, self.intervals = 0, 100., [], [], {}
                self.device = SimpleNamespace(screenshot=self.screenshot, click=self.click)

            def screenshot(self):
                self.frames += 1
                self.now += .25
                if self.frames > 150: raise AssertionError('Ambush replay did not terminate')

            def appear(self, button, offset=0, interval=0, **kwargs):
                assert offset == (30, 30)
                timer = self.intervals.setdefault(button.name, timers.Timer(interval)) if interval else None
                if timer is not None and not timer.reached(): return False
                visible = self.frames >= self.sample['delay'] + 1
                if visible and timer is not None: timer.reset()
                return visible

            def click(self, button):
                self.clicks.append(self.frames)
                self.trace.append(f'click:{self.frames}')

            def info_bar_count(self):
                if not self.clicks:
                    return int(self.sample['stale'] and self.sample['delay'] + 1 <= self.frames < self.sample['delay'] + 4)
                end = self.clicks[0] + 17
                return int(end <= self.frames < end + 2)

            def image_crop(self, *args, **kwargs): return np.zeros((2, 2, 3), np.uint8)
            def combat_appear(self):
                return bool(self.clicks and self.frames >= self.clicks[0] + 17 and
                    (not self.sample['evade'] or self.sample['message'] == 'unknown_combat'))
            def combat(self, expected_end=None, **kwargs):
                assert kwargs['fleet_index'] == 2
                self.trace.append('combat:' + str(expected_end or 'default'))
            def handle_combat_low_emotion(self):
                self.trace.append(f'emotion:{self.frames}')
                return self.frames % 2 == 0
            def handle_retirement(self):
                self.trace.append(f'retirement:{self.frames}')
                return True
            def _air_raid_appear(self): return False
            def _ambush_appear(self): return self.sample['overlay']

        cases = []
        for evade, delay, stale, overlay in itertools.product([True, False], [0, 3], [False, True], [False, True]):
            for message in (['evaded', 'failed', 'unknown', 'unknown_combat'] if evade else ['unknown']):
                sample = dict(evade=evade, delay=delay, stale=stale, overlay=overlay, message=message)
                replay = Replay(sample)
                timers.time = lambda: replay.now
                # handle_ambush button-only probe sees the entry button, while its
                # native preparation wait still consumes the scripted screenshots.
                def entry_appear(button, **kwargs):
                    return True if replay.frames == 0 else Replay.appear(replay, button, **kwargs)
                replay.appear = entry_appear
                with patch.object(ambush, 'TEMPLATE_AMBUSH_EVADE_SUCCESS', SimpleNamespace(match=lambda _: message == 'evaded')), \
                        patch.object(ambush, 'TEMPLATE_AMBUSH_EVADE_FAILED', SimpleNamespace(match=lambda _: message == 'failed')):
                    handled = replay.handle_ambush()
                cases.append(dict(sample=sample, frames=replay.frames, clicks=replay.clicks, trace=replay.trace, handled=handled))

        # Actual native map-return timer, with only observations and popup I/O mocked.
        returns = []
        for seconds, interruption in itertools.product([.25, 1.], [None, 'story', 'vote', 'guild', 'urgent']):
            class ReturnReplay(EnemySearchingHandler):
                def __init__(self):
                    self.frames, self.now = 0, 100.
                    self.device = SimpleNamespace(screenshot=self.screenshot)
                def screenshot(self): self.frames += 1; self.now += seconds
                def is_in_map(self): return True
                def handle_in_stage(self): return False
                def handle_auto_search_exit(self, **kwargs): return False
                def handle_vote_popup(self): return interruption == 'vote' and self.frames == 3
                def handle_story_skip(self): return interruption == 'story' and self.frames == 3
                def ensure_no_story(self): pass
                def handle_guild_popup_cancel(self): return interruption == 'guild' and self.frames == 3
                def handle_urgent_commission(self, **kwargs): return interruption == 'urgent' and self.frames == 3
            replay = ReturnReplay()
            timers.time = lambda: replay.now
            replay.handle_in_map_no_enemy_searching()
            returns.append(dict(seconds=seconds, interruption=interruption, frames=replay.frames))

        # Actual _goto owns counts, fleet coordinates, retry and HP/level prelude.
        # Ambush/combat I/O is replaced here; real ambush handler order is covered above.
        class WalkReplay(Fleet):
            def __init__(self, sample):
                self.sample = sample
                self.config = SimpleNamespace(MAP_HAS_MOVABLE_ENEMY=False, MAP_HAS_MOVABLE_NORMAL_ENEMY=False,
                    MAP_HAS_DECOY_ENEMY=False, MAP_HAS_MAZE=False, MAP_HAS_FORTRESS=False,
                    MAP_HAS_BOUNCING_ENEMY=False, MAP_HAS_LAND_BASED=False, MAP_FOCUS_ENEMY_AFTER_BATTLE=False,
                    Campaign_UseFleetLock=False, MAP_WALK_USE_CURRENT_FLEET=False, Submarine_Mode='do_not_use', MAP_HAS_AMBUSH=True)
                self.map = CampaignMap('offline-ambush')
                self.map.shape, self.map.map_data = 'B1', 'SP ME'
                self.map.load_map_data()
                self.map.grid_connection_initial()
                self.fleet_current_index = self.fleet_show_index = 1
                self.fleet_1_location, self.fleet_2_location = loc('A1'), ()
                self.map[loc('A1')].is_fleet = True
                self.map[loc('B1')].is_enemy = sample['target']
                self.battle_count = self.siren_count = self.mystery_count = 0
                self.fleet_ammo = 5
                self.round_reset()
                self.now, self.frames, self.taps, self.hp, self.lv = 100., 0, 0, 0, 0
                self.ambushed, self.battled = False, False
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)
                self.find_path_initial()
            def screenshot(self):
                self.frames += 1; self.now += .25
                if self.frames > 100: raise AssertionError('Walk replay did not terminate')
            def click(self, grid): self.taps += 1
            def hp_retreat_triggered(self): return False
            def fleet_ensure(self, index): return False
            def in_sight(self, *args, **kwargs): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                grid.predict_fleet = lambda: not self.sample['retry'] or self.taps > 1
                grid.predict_current_fleet = lambda: not self.sample['partial'] and grid.predict_fleet()
                return grid
            def ambush_color_initial(self): pass
            def enemy_searching_color_initial(self): pass
            def combat_appear(self): return self.sample['target'] and self.ambushed and not self.battled
            def combat(self, **kwargs): self.battled = True
            def hp_get(self): self.hp += 1
            def lv_get(self, **kwargs): self.lv += 1
            def catch_camera_repositioning(self, grid): return False
            def predict(self): pass
            def _submarine_mode(self, expected): return None
            def handle_ambush(self):
                if self.ambushed: return False
                self.ambushed = True
                if self.sample['fought']: self.screenshot()  # synthetic ambush battle has no map count ownership
                return self.sample['overlay']
            def handle_mystery(self, **kwargs): return False
            def handle_map_cat_attack(self): return False
            def handle_guild_popup_cancel(self): return False
            def handle_walk_out_of_step(self): return False
            def is_in_map(self): return True
        walks = []
        samples = [dict(overlay=o, fought=f, target=t, retry=False, partial=False)
                   for o, f, t in itertools.product([False, True], repeat=3)]
        samples += [dict(overlay=True, fought=f, target=False, retry=r, partial=not r)
                    for f, r in itertools.product([False, True], repeat=2)]
        for sample in samples:
            replay = WalkReplay(sample)
            timers.time = lambda: replay.now
            replay._goto(loc('B1'), expected='combat' if sample['target'] else '')
            walks.append(dict(sample=sample, battle=replay.battle_count, siren=replay.siren_count,
                ammo=replay.fleet_ammo, fleet=node(replay.fleet_current), taps=replay.taps, hp=replay.hp, lv=replay.lv))

        templates = [ambush.TEMPLATE_AMBUSH_EVADE_SUCCESS, ambush.TEMPLATE_AMBUSH_EVADE_FAILED]
        fixtures = []
        rng = np.random.default_rng(713)
        for kind, noise in itertools.product(['evaded', 'failed', 'both', 'unknown'], [0, 25, 90]):
            image = rng.integers(0, 80, (720, 1280, 3), dtype=np.uint8)
            x, y, right, bottom = ambush.INFO_BAR_DETECT.area
            for i, template in enumerate(templates):
                if kind == 'both' or kind == ['evaded', 'failed'][i]:
                    raw = load_image(template.file)
                    if raw.ndim == 2: raw = np.repeat(raw[:, :, None], 3, axis=2)
                    h, w = raw.shape[:2]
                    xx, yy = x + 20 + i * 430, y + 3
                    assert xx + w <= right and yy + h <= bottom
                    patch_image = np.clip(raw.astype(int) + rng.integers(-noise, noise + 1, raw.shape), 0, 255).astype(np.uint8)
                    image[yy:yy+h, xx:xx+w] = patch_image
            processed = info_letter_preprocess(crop(image, ambush.INFO_BAR_DETECT.area))
            matches = [t.match(processed) for t in templates]
            scores = [float(cv2.minMaxLoc(cv2.matchTemplate(processed, t.image, cv2.TM_CCOEFF_NORMED))[1]) for t in templates]
            filename = f'ambush-{server_name}-{kind}-{noise}.png'
            cv2.imwrite(str(output.parent / filename), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
            fixtures.append(dict(image=filename, message='evaded' if matches[0] else 'failed' if matches[1] else 'unknown', scores=scores))
        paths = ['module/handler/ambush.py', 'module/handler/info_handler.py', 'module/handler/enemy_searching.py', 'module/map/fleet.py']
        sources = {p: hashlib.sha256((root / p).read_bytes()).hexdigest() for p in paths}
    output.write_text(json.dumps(dict(cases=cases, returns=returns, walks=walks, fixtures=fixtures, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
