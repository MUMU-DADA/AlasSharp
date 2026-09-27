"""Offline upstream HP and withdrawal oracle. Synthetic images and device methods only."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, output = [Path(arg).resolve() for arg in sys.argv[1:]]
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        import module.base.timer as timers
        from module.combat.hp_balancer import HPBalancer
        from module.map.map_operation import MapOperation
        from module.handler.auto_search import AutoSearchHandler
        from module.exception import CampaignEnd
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class Health(HPBalancer):
            def __init__(self, server, weight):
                self.config = SimpleNamespace(SERVER=server, HpControl_HpBalanceWeight=weight,
                    HpControl_UseLowHpRetreat=True, HpControl_LowHpRetreatThreshold=.3)
                self.device = SimpleNamespace(image=None)
                self.hp_reset()

        images, results = [], []
        rng = np.random.default_rng(541)
        for server in ['cn', 'en', 'jp', 'tw']:
            source = Health(server, '1000')
            frames = []
            for index in range(8):
                image = np.zeros((720, 1280, 3), dtype=np.uint8)
                for slot, button in enumerate(source._hp_grid().buttons):
                    x1, y1, x2, y2 = button.area
                    width = [66, 21, 0, 60, 40, 1][slot] if index == 0 else (slot * 13 + index * 7) % 67
                    base = np.array((99, 44, 24) if (index + slot) % 2 else (156, 235, 57))
                    for x in range(width):
                        color = base if index < 3 else base + np.array([x // 3, x // 4, -x // 6])
                        image[y1:y2, x1+x] = np.clip(color, 0, 255)
                    if index == 6:
                        image[y1:y2, x1:x2] = rng.integers(0, 256, (4, 66, 3), dtype=np.uint8)
                    if index == 7:
                        image[y1:y2, x1:x2] = 0
                name = f'health-{server}-{index}.png'
                cv2.imwrite(str(output.parent / name), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
                source.device.image = image
                raw = [source._calculate_hp(b.area) for b in source._hp_grid().buttons]
                images.append(dict(server=server, file=name, raw=raw))
                frames.append(image)
            for weight in ['1000, 1000, 1000', '2000，1000，500', '1000, 0, 1000', '700']:
                driver = Health(server, weight)
                states = []
                for index, image in enumerate(frames):
                    # Switch away and back: masks must belong to the logical fleet.
                    driver.fleet_current_index = 2 if index in [1, 4, 6] else 1
                    driver.device.image = image
                    hp = driver.hp_get()
                    retreats = []
                    for threshold in [0., .3, .7, 1.]:
                        driver.config.HpControl_LowHpRetreatThreshold = threshold
                        retreats.append(driver.hp_retreat_triggered())
                    driver.config.HpControl_UseLowHpRetreat = False
                    disabled = driver.hp_retreat_triggered()
                    driver.config.HpControl_UseLowHpRetreat = True
                    states.append(dict(image=f'health-{server}-{index}.png', fleet=driver.fleet_current_index,
                        weighted=hp, hasShip=driver.hp_has_ship, retreats=retreats, disabled=disabled))
                results.append(dict(server=server, weight=weight, states=states))

        class Withdrawal(MapOperation):
            def __init__(self, frames):
                self.frames, self.frame, self.now = frames, 0, 100.
                self.calls, self.clicks = [], []
                self.in_stage_timer = timers.Timer(.5, count=2)
                self.device = SimpleNamespace(screenshot=self.screenshot, click=self.click)
            def positive(self, name): return name in self.frames[min(self.frame, len(self.frames)-1)]
            def screenshot(self):
                self.frame += 1
                self.now += .5
                if self.frame > 100: raise AssertionError('Native withdrawal fixture did not terminate')
            def appear(self, button, offset=0, interval=0, **kw):
                bounds = [-offset[0], -offset[1], *offset] if isinstance(offset, tuple) else [0, 0, 0, 0]
                self.calls.append(dict(asset=button.name, offset=bounds, interval=interval, frame=self.frame))
                return self.positive(button.name)
            def appear_then_click(self, button, **kw):
                if self.appear(button, **kw):
                    self.click(button)
                    return True
                return False
            def click(self, button): self.clicks.append(dict(asset=button.name, frame=self.frame))
            def handle_popup_confirm(self, name):
                self.calls.append(dict(asset='$popup', offset=[], interval=0, frame=self.frame))
                return self.positive('$popup')
            def handle_auto_search_exit(self):
                return AutoSearchHandler.handle_auto_search_exit(self)
            _auto_search_menu_offset = (250, 30)
            def interval_reset(self, button): pass
            def is_stage_page_has_entrance(self):
                self.calls.append(dict(asset='$entrance', offset=[], interval=0, frame=self.frame))
                return self.positive('$entrance')
            def info_bar_count(self):
                self.calls.append(dict(asset='$info', offset=[], interval=0, frame=self.frame))
                return int(self.positive('$info'))

        stage = ['CAMPAIGN_CHECK', '$entrance']
        withdraw = []
        for frames in [
            [['WITHDRAW'], ['$popup'], [], stage],
            [['$popup', 'WITHDRAW'], ['WITHDRAW', 'DAILY_CHECK'], ['DAILY_CHECK'], stage],
            [['WITHDRAW'], ['MAP_PREPARATION'], [], stage],
            [['WITHDRAW'], ['CAMPAIGN_CHECK'], [], stage],
            [['WITHDRAW'], stage+['$info'], stage],
            [['AUTO_SEARCH_MENU_EXIT'], stage],
        ]:
            driver = Withdrawal(frames)
            timers.time = lambda: driver.now
            error = None
            try: driver.withdraw()
            except CampaignEnd: error = 'CampaignEnd'
            withdraw.append(dict(frames=frames, calls=driver.calls, clicks=driver.clicks, frame=driver.frame, error=error))
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in [
            'module/combat/hp_balancer.py', 'module/base/utils.py', 'module/map/map_operation.py',
            'module/handler/enemy_searching.py']}
    output.write_text(json.dumps(dict(images=images, results=results, withdrawal=withdraw, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
