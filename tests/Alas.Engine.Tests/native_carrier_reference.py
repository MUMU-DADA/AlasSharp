"""Native carrier handler/wait and original-template measurements with offline observations only."""
import contextlib
import hashlib
import itertools
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, server_name, output = Path(sys.argv[1]).resolve(), sys.argv[2], Path(sys.argv[3]).resolve()
    os.chdir(root)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        import module.config.server as server
        server.server = server_name
        import module.base.timer as timers
        from module.handler.mystery import MysteryHandler
        from module.handler.enemy_searching import EnemySearchingHandler
        from module.handler.assets import IN_MAP, MAP_ENEMY_SEARCHING
        from module.template.assets import TEMPLATE_COMBAT_LOADING
        from module.combat.combat import Combat
        from module.exception import CampaignEnd
        from module.map.fleet import Fleet
        from module.map.map_base import CampaignMap
        from module.base.utils import node2location as loc, location2node as node, load_image
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class Replay(MysteryHandler):
            def __init__(self, sample):
                self.sample = sample
                self.config = SimpleNamespace(MAP_MYSTERY_HAS_CARRIER=sample['enabled'])
                self.now, self.frames, self.carrier_count = 100., 0, 0
                self.trace = []
                self.device = SimpleNamespace(screenshot=self.screenshot, sleep=self.sleep)
            def screenshot(self):
                self.frames += 1
                self.now += self.sample['seconds']
                if self.frames > 150: raise AssertionError('Carrier wait did not terminate')
            def sleep(self, seconds):
                self.trace.append(f'sleep:{seconds:g}')
                self.now += seconds
            def is_in_map(self):
                return self.sample['inMap'] and not (self.sample['interruption'] == 'off-map' and 3 <= self.frames <= 5)
            def enemy_searching_appear(self):
                return self.is_in_map() and self.sample['search'] and self.frames < self.sample['until']
            def is_event_animation(self): return self.sample['interruption'] == 'event' and self.frames <= 4
            def handle_in_stage(self):
                if self.sample['interruption'] == 'stage' and self.frames == 3: raise CampaignEnd()
                return False
            def is_combat_loading(self): return self.sample['interruption'] == 'loading' and self.frames == 3
            def handle_auto_search_exit(self, **kwargs): return self.interrupt('auto')
            def handle_vote_popup(self): return False
            def handle_story_skip(self): return self.interrupt('story')
            def ensure_no_story(self, skip_first_screenshot=True):
                assert skip_first_screenshot
                self.trace.append('story-clear')
            def handle_guild_popup_cancel(self): return self.interrupt('guild')
            def handle_urgent_commission(self, **kwargs): return self.interrupt('urgent')
            def interrupt(self, kind):
                result = self.sample['interruption'] == kind and self.frames == 3
                if result: self.trace.append(kind)
                return result
        cases = []
        for seconds, until, interruption in itertools.product([.25, 1.], [1, 2, 7, 100],
                [None, 'auto', 'story', 'guild', 'urgent', 'off-map', 'event', 'loading', 'stage']):
            sample = dict(seconds=seconds, until=until, interruption=interruption, enabled=True, inMap=True, search=True)
            replay = Replay(sample)
            timers.time = lambda: replay.now
            error = None
            try: handled = replay.handle_mystery_carrier()
            except CampaignEnd: handled, error = False, 'stage'
            cases.append(dict(sample=sample, frames=replay.frames, trace=replay.trace, count=replay.carrier_count, handled=handled, error=error))
        for enabled, in_map, search in itertools.product([False, True], repeat=3):
            sample = dict(seconds=.25, until=2, interruption=None, enabled=enabled, inMap=in_map, search=search)
            replay = Replay(sample)
            timers.time = lambda: replay.now
            handled = replay.handle_mystery_carrier()
            cases.append(dict(sample=sample, frames=replay.frames, trace=replay.trace, count=replay.carrier_count, handled=handled, error=None))

        # Native _goto commits carrier scan after fleet relocation and before rounds.
        class Walk(Fleet):
            def __init__(self, kinds):
                self.kinds = list(kinds)
                self.config = SimpleNamespace(MAP_HAS_MOVABLE_ENEMY=False, MAP_HAS_MOVABLE_NORMAL_ENEMY=False,
                    MAP_HAS_DECOY_ENEMY=False, MAP_HAS_MAZE=False, MAP_HAS_FORTRESS=False, MAP_HAS_BOUNCING_ENEMY=False,
                    MAP_HAS_LAND_BASED=False, MAP_FOCUS_ENEMY_AFTER_BATTLE=False, Campaign_UseFleetLock=False,
                    MAP_WALK_USE_CURRENT_FLEET=False, Submarine_Mode='do_not_use', MAP_HAS_AMBUSH=False)
                self.map = CampaignMap('offline-carrier')
                self.map.shape, self.map.map_data = 'C1', 'SP MM --'
                self.map.load_map_data()
                self.map.grid_connection_initial()
                self.fleet_current_index = 1
                self.fleet_1_location, self.fleet_2_location = loc('A1'), ()
                self.map[loc('A1')].is_fleet = self.map[loc('B1')].is_mystery = True
                self.battle_count = self.siren_count = self.mystery_count = self.carrier_count = 0
                self.fleet_ammo = 5
                self.round_reset()
                self.now, self.frames, self.taps = 100., 0, 0
                self.trace = []
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.view = SimpleNamespace(update=lambda **kwargs: None)
                self.find_path_initial()
            def screenshot(self): self.frames += 1; self.now += .25
            def click(self, grid): self.taps += 1
            def hp_retreat_triggered(self): return False
            def fleet_ensure(self, index): return False
            def in_sight(self, *args, **kwargs): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                grid.predict_fleet = grid.predict_current_fleet = lambda: not self.kinds
                return grid
            def ambush_color_initial(self): pass
            def enemy_searching_color_initial(self): pass
            def combat_appear(self): return False
            def handle_ambush(self): return False
            def handle_mystery(self, **kwargs):
                if not self.kinds: return False
                kind = self.kinds.pop(0)
                if kind == 'get_carrier': self.carrier_count += 1
                return kind
            def handle_map_cat_attack(self): return False
            def handle_guild_popup_cancel(self): return False
            def handle_walk_out_of_step(self): return False
            def is_in_map(self): return True
            def full_scan_carrier(self):
                self.trace.append('scan:' + node(self.fleet_current))
                self.map[loc('C1')].is_enemy = self.map[loc('C1')].is_carrier = True
            def round_next(self): self.trace.append('round'); super().round_next()
        walks = []
        for kinds in [['get_carrier'], ['get_carrier', 'get_carrier'], ['get_item', 'get_carrier'],
                ['get_carrier', 'get_ammo'], ['get_carrier', 'get_item']]:
            replay = Walk(kinds)
            timers.time = lambda: replay.now
            replay._goto(loc('B1'), expected='mystery')
            walks.append(dict(kinds=kinds, trace=replay.trace, fleet=node(replay.fleet_current),
                carrier=replay.carrier_count, mystery=replay.mystery_count, battle=replay.battle_count, ammo=replay.fleet_ammo))

        fixtures = []
        rng = np.random.default_rng(7214)
        for kind, noise in itertools.product(['search', 'loading', 'both', 'absent', 'off-map'], [0, 30, 100]):
            image = rng.integers(0, 70, (720, 1280, 3), dtype=np.uint8)
            for button in ([IN_MAP] if kind != 'off-map' else []) + ([MAP_ENEMY_SEARCHING] if kind in ['search', 'both', 'off-map'] else []):
                x, y, right, bottom = button.area
                raw = load_image(button.file)[y:bottom, x:right]
                image[y:bottom, x:right] = np.clip(raw.astype(int) + rng.integers(-noise, noise + 1, raw.shape), 0, 255).astype(np.uint8)
            if kind in ['loading', 'both']:
                raw = TEMPLATE_COMBAT_LOADING.image
                if raw.ndim == 2: raw = np.repeat(raw[:, :, None], 3, axis=2)
                h, w = raw.shape[:2]
                image[627:627+h, 400:400+w] = np.clip(raw.astype(int) + rng.integers(-noise, noise + 1, raw.shape), 0, 255).astype(np.uint8)
            class View(EnemySearchingHandler):
                def __init__(self): self.device = SimpleNamespace(image=image)
                def is_in_map(self): return IN_MAP.appear_on(image)
                def image_crop(self, area, **kwargs):
                    x, y, right, bottom = area
                    return image[y:bottom, x:right]
            view = View()
            filename = f'carrier-{server_name}-{kind}-{noise}.png'
            cv2.imwrite(str(output.parent / filename), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
            fixtures.append(dict(image=filename, search=view.enemy_searching_appear(), loading=Combat.is_combat_loading(view)))
        paths = ['module/handler/mystery.py', 'module/handler/enemy_searching.py', 'module/map/fleet.py', 'module/combat/combat.py']
        sources = {p: hashlib.sha256((root / p).read_bytes()).hexdigest() for p in paths}
    output.write_text(json.dumps(dict(cases=cases, walks=walks, fixtures=fixtures, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
