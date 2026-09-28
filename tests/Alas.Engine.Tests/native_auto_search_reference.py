"""Offline oracle only: execute upstream auto-search loops against synthetic observations."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, output = (Path(p).resolve() for p in sys.argv[1:])
    output.parent.mkdir(parents=True, exist_ok=True)
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import module.base.timer as timers
        from module.base.base import ModuleBase
        from module.combat.auto_search_combat import AutoSearchCombat
        from module.handler.auto_search import AutoSearchHandler, AUTO_SEARCH_SETTINGS
        from module.exception import CampaignEnd
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class Replay(AutoSearchCombat):
            def __init__(self, case):
                self.case, self.frame, self.now = case, 1, 100.
                self.clicks, self.handled, self.intervals = [], set(), {}
                self.config = SimpleNamespace(Submarine_Fleet=0, Fleet_Fleet1Mode='combat_auto',
                                              Fleet_Fleet2Mode='combat_auto')
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click,
                    sleep=self.sleep, stuck_record_clear=lambda: None, click_record_clear=lambda: None,
                    screenshot_interval_set=lambda *a: None)
                self.emotion = SimpleNamespace(is_calculate=False)
                timers.time = lambda: self.now
                self._auto_search_in_stage_timer = timers.Timer(3, count=6).start()
                self._auto_search_status_confirm = False
                self.auto_skip_timer = timers.Timer(1)
                self.auto_click_interval_timer = timers.Timer(1)
                self.auto_mode_click_timer = timers.Timer(5)
            @property
            def data(self):
                return self.case['frames'][min(self.frame - 1, len(self.case['frames']) - 1)]
            def screenshot(self):
                self.frame += 1
                self.now += self.case['step']
                if self.frame > 180:
                    raise TimeoutError('synthetic frame limit')
            def sleep(self, value):
                self.now += sum(value) / 2 if isinstance(value, tuple) else value
            def click(self, button):
                self.clicks.append(dict(asset=button.name, frame=self.frame))
            def appear(self, button, offset=0, interval=0, threshold=10, **kwargs):
                if interval and not self.intervals.setdefault(button.name, timers.Timer(interval)).reached():
                    return False
                present = button.name in self.data.get('assets', [])
                if button.name.startswith('EXP_INFO_') and self.data.get('weak') and threshold < 30:
                    present = False
                if present and interval:
                    self.intervals[button.name].reset()
                return present
            appear_then_click = ModuleBase.appear_then_click
            def interval_reset(self, button, interval=3):
                self.intervals.setdefault(button.name, timers.Timer(interval)).reset()
            def is_in_stage(self):
                return self.data.get('stage', False)
            def is_in_auto_search_menu(self):
                return self.data.get('menu', False)
            def is_combat_executing(self):
                return 'PAUSE' in self.data.get('assets', [])
            def is_combat_loading(self):
                return self.data.get('loading', False)
            def once(self, name):
                key = (self.frame, name)
                if self.data.get(name) and key not in self.handled:
                    self.handled.add(key)
                    return True
                return False
            def handle_retirement(self): return self.once('retire')
            def handle_combat_low_emotion(self): return self.once('low')
            def handle_story_skip(self): return self.once('story')
            def handle_map_cat_attack(self): return self.once('cat')
            def handle_popup_confirm(self, *args): return self.once('confirm')
            def handle_urgent_commission(self): return self.once('urgent')
            def handle_guild_popup_cancel(self): return self.once('guild')
            def handle_mission_popup_ack(self): return self.once('mission')
            def auto_search_watch_fleet(self, checked=False): return True
            def auto_search_watch_oil(self, checked=False): return True
            def auto_search_watch_coin(self, checked=False): return True
            def submarine_call_reset(self): pass
            def handle_submarine_call(self, mode): return False
            def combat_manual_reset(self): pass
            def handle_combat_manual(self, auto): return False

        def a(*names, **flags): return dict(assets=list(names), **flags)
        cases = []
        for step in [.25, .5, 1.1]:
            for low in [False, True]:
                for rank in ['S', 'A', 'B']:
                    cases.append(dict(step=step, frames=[a('AUTO_SEARCH_MAP_OPTION_OFF'),
                        a('AUTO_SEARCH_MAP_OPTION_ON'), a(low=low, story=not low), a(cat=True),
                        a(loading=True), a('AUTOMATION_CONFIRM_CHECK', 'AUTOMATION_CONFIRM'),
                        a('PAUSE'), a('PAUSE', 'COMBAT_AUTO'), a('PAUSE', confirm=True),
                        a('PAUSE', urgent=True), a('PAUSE', guild=True), a('PAUSE', mission=True),
                        a('BATTLE_STATUS_' + rank), a('EXP_INFO_' + rank, weak=low), a('GET_ITEMS_2'),
                        a('GET_SHIP', 'NEW_SHIP'), a('AUTO_SEARCH_MAP_OPTION_ON')]))
                for end in ['moving', 'loading', 'executing', 'status']:
                    prefix = [a('AUTO_SEARCH_MAP_OPTION_ON')]
                    if end != 'moving': prefix += [a(loading=True)]
                    if end in ['executing', 'status']: prefix += [a('PAUSE')]
                    if end == 'status': prefix += [a('BATTLE_STATUS_S')]
                    for menu in [False, True]:
                        cases.append(dict(step=step, frames=prefix + [a(menu=menu, stage=not menu)] * 18))
                cases.append(dict(step=step, frames=[a(retire=True), a('AUTO_SEARCH_MAP_OPTION_OFF'),
                    *[a('AUTO_SEARCH_MAP_OPTION_ON')] * 15, a(loading=True), a('PAUSE'),
                    a('EXP_INFO_S'), a('AUTO_SEARCH_MAP_OPTION_ON')]))
        results = []
        for case in cases:
            actor = Replay(case)
            ended = False
            try:
                actor.auto_search_moving()
                actor.auto_search_combat(fleet_index=1)
            except CampaignEnd:
                ended = True
            results.append(dict(case=case, ended=ended, frames=actor.frame, clicks=actor.clicks,
                                confirm=actor._auto_search_status_confirm))

        # Native role selection with all six targets, existing values and absent controls.
        class Settings(AutoSearchHandler):
            def __init__(self, active):
                self.active, self.clicks = set(active), []
                self.device = SimpleNamespace(screenshot=lambda: None, sleep=lambda _: None, click=self.click)
            def image_color_count(self, area, color, threshold, count):
                return any(i in self.active and b.button == area for i, b in enumerate(AUTO_SEARCH_SETTINGS))
            def click(self, button):
                index = AUTO_SEARCH_SETTINGS.index(button)
                self.clicks.append(index)
                self.active -= set(range(4)) if index < 4 else {4, 5}
                self.active.add(index)
        names = ['fleet1_mob_fleet2_boss', 'fleet1_boss_fleet2_mob', 'fleet1_all_fleet2_standby',
                 'fleet1_standby_fleet2_all', 'sub_auto_call', 'sub_standby']
        settings = []
        for target, name in enumerate(names):
            for active in [[], [0, 4], [1, 5], [2, 4], [3, 5]]:
                actor = Settings(active)
                confirmed = actor.auto_search_setting_ensure(name)
                settings.append(dict(target=target, active=active, confirmed=confirmed, clicks=actor.clicks))
        files = ['module/combat/auto_search_combat.py', 'module/handler/auto_search.py', 'module/campaign/campaign_status.py']
        payload = dict(results=results, settings=settings,
            sources={name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in files})
        # A static synthetic frame for the real C# session + pure CV worker + simulated ADB path.
        # It contains only declared asset pixels and carries no account or device evidence.
        os.chdir(root)
        import numpy as np
        from PIL import Image
        from module.handler import assets as handler_assets
        image = np.zeros((720, 1280, 3), dtype=np.uint8)
        for button in [handler_assets.IN_MAP, handler_assets.AUTO_SEARCH_MENU_CONTINUE, handler_assets.AUTO_SEARCH_MENU_EXIT]:
            x, y, right, bottom = button.area
            pixels = np.array(Image.open(button.file).convert('RGB'))
            image[y:bottom, x:right] = pixels[y:bottom, x:right]
        Image.fromarray(image).save(output.parent / 'auto-search-menu.png')
    output.write_text(json.dumps(payload, ensure_ascii=False), encoding='utf-8')
    print(f'Native auto-search: {len(results)} flow traces, {len(settings)} role settings')


if __name__ == '__main__':
    main()
