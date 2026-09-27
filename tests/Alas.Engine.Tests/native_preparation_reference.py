"""Offline oracle: native map preparation, switches and book loops with synthetic observations/images."""
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
        import module.base.timer as timers
        from module.base.base import ModuleBase
        from module.map.map_operation import MapOperation
        from module.map.assets import MAP_PREPARATION
        import module.handler.fast_forward as fast
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class Replay(MapOperation):
            def __init__(self, case):
                self.case, self.frame, self.now = case, 0, 100.
                self.clicks, self.intervals = [], {}
                self.device = SimpleNamespace(screenshot=self.screenshot, click=self.click)
                self.config = SimpleNamespace(
                    MAP_HAS_CLEAR_PERCENTAGE=case.get('hasPercentage', True),
                    MAP_CLEAR_PERCENTAGE_SHORT=case.get('short', False),
                    MAP_IS_ONE_TIME_STAGE=case.get('oneTime', False),
                    Campaign_Name='1-1', MAP_HAS_MAP_STORY=True, STAR_REQUIRE_3=3,
                    StopCondition_MapAchievement='non_stop', Campaign_UseClearMode=case.get('useClear', True),
                    Campaign_UseAutoSearch=False, Campaign_Use2xBook=case.get('useBook', False))
                self.map_clear_percentage_prev = -1
                timers.time = lambda: self.now
                self.map_clear_percentage_timer = timers.Timer(.3, count=1).start()
            @property
            def data(self):
                return self.case['frames'][min(max(self.frame - 1, 0), len(self.case['frames']) - 1)]
            def screenshot(self):
                self.frame += 1
                self.now += self.case.get('step', .5)
                if self.frame > 200:
                    raise TimeoutError('synthetic frame limit')
            def loop(self):
                while True:
                    self.screenshot()
                    yield
            def click(self, button):
                self.clicks.append(dict(asset=button.name, frame=self.frame))
            def appear(self, button, offset=0, interval=0):
                name = button.name
                if interval and not self.intervals.setdefault(name, timers.Timer(interval)).reached():
                    return False
                present = {
                    'MAP_PREPARATION': self.data.get('page', 'normal') == 'normal',
                    'MAP_PREPARATION_HARD': self.data.get('page') == 'hard',
                    'MAP_GREEN': self.data.get('safe', False),
                    'CLEAR_MODE_TITLE': self.data.get('clear') is not None,
                    'AUTO_SEARCH_TITLE': self.data.get('auto') is not None,
                    'AUTO_SEARCH_TITLE2': False, 'AUTO_SEARCH_TITLE3': False,
                    'BOOK_CHECK_PREP': self.data.get('book') is not None,
                }.get(name, False)
                if present and interval:
                    self.intervals[name].reset()
                return present
            def image_color_count(self, button, color, threshold=30, count=50):
                if hasattr(button, 'name') and button.name.startswith('MAP_STAR_'):
                    value = self.data.get('stars', [36, 35, 40])[int(button.name[-1]) - 1]
                else:
                    value = {
                        (130, 229, 255): 51 if self.data.get('clear') == 'on' else 0,
                        (255, 255, 255): 201 if self.data.get('clear') == 'off' else 0,
                        (158, 234, 94): 51 if self.data.get('auto') == 'on' else 0,
                        (156, 255, 82): 21 if self.data.get('book') == 'on' else 0,
                    }[color]
                return value > count
            def get_map_clear_percentage(self):
                return self.data.get('percent', .99) * (1.4 if self.config.MAP_CLEAR_PERCENTAGE_SHORT else 1)
            def info_bar_count(self):
                return self.data.get('info', 0)

        scenarios = []
        for step in [.125, .5, 2.]:
            for use_clear in [False, True]:
                for frames in [
                    [{}], [{'clear': 'off'}], [{'clear': 'on', 'auto': 'off'}],
                    [{'percent': .99}, {'percent': 0}, {'percent': .8}, {'percent': .99, 'clear': 'on'}],
                    [{'percent': .95}], [{'percent': .94}, {'percent': .96, 'clear': 'on'}],
                    [{'percent': .3}, {'percent': .32}, {'percent': .32}],
                    [{'page': 'none'}, {'info': 1}, {'page': 'hard', 'percent': .7}],
                    [{'clear': 'off'}] * 8 + [{'clear': 'unknown'}] * 4 + [{'clear': 'on', 'auto': 'on'}] * 8 + [{'clear': 'on', 'auto': 'off'}],
                ]:
                    # Terminal switch states must agree with the requested target.
                    if frames[-1].get('clear') in ['on', 'off']:
                        frames = frames + [dict(frames[-1], clear='on' if use_clear else 'off', auto='off')] * 4
                    scenarios.append(dict(kind='map', frames=frames, step=step, useClear=use_clear))
        scenarios += [
            dict(kind='map', frames=[dict(percent=.8)], short=True),
            dict(kind='map', frames=[dict(page='hard')], hasPercentage=False),
            dict(kind='map', frames=[dict(info=1)], oneTime=True),
        ]
        for step in [.125, .5, 2.]:
            for desired in [False, True]:
                scenarios += [dict(kind='book', step=step, useBook=desired, frames=frames) for frames in [
                    [{}], [dict(book='on' if desired else 'off')],
                    [dict(book='off' if desired else 'on')] * 20 + [dict(book='on' if desired else 'off')],
                    [dict(book='off' if desired else 'on')]]]
        results = []
        for case in scenarios:
            actor = Replay(case)
            if case['kind'] == 'map':
                while True:
                    actor.screenshot()
                    if actor.handle_map_preparation() is not None:
                        break
                actor.map_get_info()
                info = dict(frameSequence=actor.frame, clearPercentage=actor.map_clear_percentage,
                            star1=actor.map_achieved_star_1, star2=actor.map_achieved_star_2,
                            star3=actor.map_achieved_star_3, threatSafe=actor.map_is_threat_safe,
                            clearModeAvailable=actor.map_has_clear_mode)
                changed = actor.handle_fast_forward()
                available = fast.AUTO_SEARCH.appear(actor)
                auto_changed = fast.AUTO_SEARCH.set('off', actor) if available else False
                expected = dict(info=info, clearMode=actor.map_is_clear_mode, clearModeChanged=changed,
                                autoSearch=dict(available=available, changed=auto_changed, enabled=False))
            else:
                actor.screenshot()
                result = actor._set_2x_book_status('on' if case['useBook'] else 'off',
                                                  fast.BOOK_CHECK_PREP, fast.BOOK_BOX_PREP)
                expected = dict(confirmed=result, clicks=len(actor.clicks))
            results.append(dict(case=case, expected=expected, clicks=actor.clicks, frames=actor.frame))

        # Real image primitives, native per-server assets and inherited matching offsets.
        import cv2
        import numpy as np
        from PIL import Image
        os.chdir(root)
        pixels = []
        names = ['CLEAR_MODE_TITLE', 'CLEAR_MODE_CHECK', 'AUTO_SEARCH_TITLE', 'AUTO_SEARCH_TITLE2',
                 'AUTO_SEARCH_TITLE3', 'AUTO_SEARCH_CHECK', 'BOOK_CHECK_PREP', 'BOOK_BOX_PREP',
                 'MAP_STAR_1', 'MAP_STAR_2', 'MAP_STAR_3', 'MAP_GREEN', 'MAP_CLEAR_PERCENTAGE']
        originals = {name: getattr(fast, name) for name in names}
        for server in ['cn', 'en', 'jp', 'tw']:
            for name, asset in originals.items():
                setattr(fast, name, asset.split_server()[server])
            for index in range(6):
                image = np.zeros((720, 1280, 3), np.uint8)
                def template(button, dx=0, dy=0):
                    area = button.area
                    full = np.array(Image.open(button.file).convert('RGB'))
                    image[area[1]+dy:area[3]+dy, area[0]+dx:area[2]+dx] = full[area[1]:area[3], area[0]:area[2]]
                def fill(button, rgb, count, dx=0, dy=0):
                    x, y, r, b = button.area
                    patch = image[y+dy:b+dy, x+dx:r+dx].copy()
                    patch.reshape(-1, 3)[:count] = rgb
                    image[y+dy:b+dy, x+dx:r+dx] = patch
                template(MAP_PREPARATION)
                template(fast.CLEAR_MODE_TITLE, 5, -3)
                fill(fast.CLEAR_MODE_CHECK, (130,229,255) if index < 3 else (255,255,255),
                     [50,51,55,200,201,205][index], 5, -3)
                title = [fast.AUTO_SEARCH_TITLE, fast.AUTO_SEARCH_TITLE2, fast.AUTO_SEARCH_TITLE3][index % 3]
                template(title, -4, 2)
                fill(fast.AUTO_SEARCH_CHECK, (158,234,94), 50 + index % 2, -4, 2)
                template(fast.BOOK_CHECK_PREP, 7, 3)
                fill(fast.BOOK_BOX_PREP, (156,255,82), 20 + index % 2, 7, 3)
                for i in range(1,4):
                    fill(getattr(fast, 'MAP_STAR_' + str(i)), (250,232,140), 35 + (index + i) % 2)
                x, y, r, b = fast.MAP_CLEAR_PERCENTAGE.area
                image[y:b, x:x+int((r-x) * (.99 if index == 2 else .5))] = (231,170,82)
                class Pixels(fast.FastForwardHandler):
                    def __init__(self):
                        pass
                    device = SimpleNamespace(image=image)
                    config = SimpleNamespace(MAP_CLEAR_PERCENTAGE_SHORT=index % 2 == 1)
                    def appear(self, button, offset=0, interval=0):
                        return button.match(image, offset=offset)
                actor = Pixels()
                clear = fast.CLEAR_MODE.get(actor)
                auto = fast.AUTO_SEARCH.get(actor)
                book_present = actor.appear(fast.BOOK_CHECK_PREP, offset=(250,30))
                fast.BOOK_BOX_PREP.load_offset(fast.BOOK_CHECK_PREP)
                book = actor.image_color_count(fast.BOOK_BOX_PREP.button, (156,255,82), threshold=30, count=20)
                file = f'preparation-{server}-{index}.png'
                cv2.imwrite(str(output.parent / file), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
                pixels.append(dict(file=file, server=server, short=index % 2 == 1,
                                   clear=None if clear == 'unknown' else clear, auto=None if auto == 'unknown' else auto,
                                   bookPresent=bool(book_present), book=bool(book),
                                   stars=[bool(actor._is_map_star_active(getattr(fast, 'MAP_STAR_' + str(i)))) for i in range(1,4)],
                                   percentage=actor.get_map_clear_percentage()))
        for name, asset in originals.items():
            setattr(fast, name, asset)
        sources = {name: hashlib.sha256((root/name).read_bytes()).hexdigest()
                   for name in ['module/handler/fast_forward.py', 'module/map/map_operation.py']}
    output.write_text(json.dumps(dict(results=results, pixels=pixels, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
