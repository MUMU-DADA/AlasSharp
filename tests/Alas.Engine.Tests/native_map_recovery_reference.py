"""Run actual upstream Camera/UI recovery methods with deterministic I/O endpoints.

No device is accessed. Only recognition/transport endpoints are scripted; recovery
priority, helper loops, offsets, timers and exit exceptions execute upstream code.
"""
import contextlib
import importlib
import json
import os
from pathlib import Path
import random
import sys
from types import SimpleNamespace


def main():
    root, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    os.chdir(root)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import numpy as np
        import module.base.timer as timers
        from module.base.button import Button
        from module.map.camera import Camera
        from module.exception import MapDetectionError
        from module.logger import logger
        logger.setLevel('CRITICAL')
        identities = {}
        for path in sorted((root / 'module').rglob('assets.py')):
            name = path.relative_to(root).as_posix()[:-3].replace('/', '.')
            for key, value in vars(importlib.import_module(name)).items():
                if isinstance(value, Button): identities[id(value)] = name + '.' + key
        def key(button): return identities[id(button)]
        def offsets(value):
            if isinstance(value, tuple): return [-value[0], -value[1], *value] if len(value) == 2 else list(value)
            return [-3, -value, 3, value] if value else [0, 0, 0, 0]
        now, actor = [1000.], [None]
        timers.time = lambda: now[0]
        Button.match = lambda button, image, **kw: key(button) in actor[0].positive
        Button.appear_on = lambda button, image, **kw: key(button) in actor[0].positive
        def luma(button, image, offset=30, similarity=.85):
            actor[0].events.append(['appear', key(button), offsets(offset), 0, similarity, 10, 'Luma'])
            return key(button) in actor[0].positive
        Button.match_luma = luma
        class Reference(Camera):
            def appear(self, button, offset=0, interval=0, similarity=.85, threshold=10):
                self.events.append(['appear', key(button), offsets(offset), interval, similarity, threshold, 'Color'])
                return super().appear(button, offset=offset, interval=interval, similarity=similarity, threshold=threshold)
            def interval_reset(self, button, interval=3):
                self.events.append(['reset', key(button)])
                return super().interval_reset(button, interval)
            def info_bar_count(self):
                self.events.append(['info']); return self.info
            def is_stage_page_has_entrance(self):
                self.events.append(['entrance']); return self.entrance
            def handle_story_skip(self):
                self.events.append(['story']); return self.story
            def ensure_no_story(self, skip_first_screenshot=True):
                self.events.append(['ensure_story', skip_first_screenshot])
            def handle_popup_confirm(self, name):
                self.events.append(['popup']); return self.popup
            def _map_swipe(self, vector): return True
        cases = []
        def run(name, positive=(), operation='recover', info=0, entrance=True, story=False,
                popup=False, opsi=False, running=True, outside=False, hooks=False, after=None):
            sample = dict(name=name, positive=list(positive), operation=operation, info=info,
                entrance=entrance, story=story, popup=popup, opsi=opsi, running=running,
                outside=outside, hooks=hooks, after=after)
            c = Reference.__new__(Reference); actor[0] = c
            c.events, c.positive, c.interval_timer = [], set(positive), {}
            c.info, c.entrance, c.story, c.popup = info, entrance, story, popup
            c.config = SimpleNamespace(BUTTON_OFFSET=30, task=SimpleNamespace(command='Opsi' if opsi else 'Campaign'))
            def screenshot():
                c.events.append(['screenshot']); now[0] += .61
                if sum(e[0] == 'screenshot' for e in c.events) > 100: raise AssertionError('Recovery fixture stuck')
                c.positive = set(after or ['module.ui.assets.CAMPAIGN_CHECK']); c.entrance = True; c.info = 0
            def running_(): c.events.append(['running']); return running
            def load(image):
                raise MapDetectionError('Camera outside map: offset=(1, -2)' if outside else 'fixture geometry')
            c.view = SimpleNamespace(load=load)
            c.device = SimpleNamespace(image=np.zeros((720,1280,3),dtype=np.uint8), screenshot=screenshot,
                click=lambda b:c.events.append(['click',key(b)]), app_is_running=running_, stuck_record_add=lambda b:None)
            if hooks:
                c.os_auto_search_quit = lambda:c.events.append(['reward_exit'])
                c.os_mission_quit = lambda:c.events.append(['mission_exit'])
            value, error = None, None
            try:
                if operation == 'cancel': c.enter_map_cancel(); value = True
                elif operation == 'auto': value = c.ensure_auto_search_exit()
                elif operation == 'gate':
                    value = bool(c.is_in_map() or c.is_in_strategy_submarine_move() or c.is_in_strategy_mob_move() or c.is_in_strategy_air_strike())
                else: value = not c._update_view()
            except MapDetectionError: value = False
            except Exception as e: error = type(e).__name__
            cases.append(dict(sample=sample, expected=dict(value=value,error=error,events=c.events)))
        h='module.handler.assets.'; u='module.ui.assets.'; m='module.map.assets.'; o='module.os_handler.assets.'
        candidates=[h+'IN_MAP',h+'SUBMARINE_MOVE_CONFIRM',h+'MOB_MOVE_CANCEL',h+'AIR_STRIKE_CONFIRM',
            'module.combat.assets.GET_ITEMS_1','module.combat.assets.GET_ITEMS_1_RYZA',o+'GET_ADAPTABILITY',
            h+'GET_MISSION',u+'CAMPAIGN_CHECK',u+'EVENT_CHECK',u+'SP_CHECK',m+'MAP_PREPARATION',m+'MAP_PREPARATION_HARD',
            h+'AUTO_SEARCH_MENU_CONTINUE','module.os.assets.GLOBE_GOTO_MAP',o+'AUTO_SEARCH_REWARD',o+'MISSION_CHECK',
            'module.os_shop.assets.PORT_SUPPLY_CHECK',h+'GAME_TIPS']
        for i, button in enumerate(candidates):
            after=[h+'IN_MAP'] if button in candidates[14:17] else None
            run(f'branch-{i}',[button],after=after)
            run(f'gate-{i}',[button],operation='gate')
        run('none'); run('dead',running=False); run('outside',outside=True,running=False)
        run('info',candidates,info=2); run('story',[h+'GET_MISSION'],story=True)
        run('stage-loading',[u+'CAMPAIGN_CHECK'],entrance=False)
        run('popup',opsi=True,popup=True);run('non-os-popup',popup=True)
        for button in candidates[15:17]: run('os-hook',[button],opsi=True,hooks=True)
        for button in [m+'MAP_PREPARATION',m+'MAP_PREPARATION_HARD',m+'FLEET_PREPARATION']:
            run('cancel',[button],operation='cancel')
        run('auto',[h+'AUTO_SEARCH_MENU_CONTINUE',h+'AUTO_SEARCH_MENU_EXIT'],operation='auto')
        run('auto-absent',operation='auto')
        rng=random.Random(27419)
        for i in range(60):
            run(f'priority-{i}',rng.sample(candidates,6),info=int(i%11==0),story=i%9==0,
                opsi=i%2==0,popup=i%5==0,hooks=True)
        updates=[]
        for name,steps in [('transient',['error']*3+['success']),('confirmed',['error']*15),
                           ('handled',['error']*8+['handled']+['error']*8+['success']),('io',['io'])]:
            now[0]=1000.
            c=Reference.__new__(Reference)
            c.config=SimpleNamespace(MAP_GRID_CENTER_TOLERANCE=.2)
            c.view=SimpleNamespace(center_offset=np.array([.5,.5]))
            count=[0]
            def screenshot():now[0]+=.6;count[0]+=1
            c.device=SimpleNamespace(screenshot=screenshot,_screenshot_interval=SimpleNamespace(clear=lambda:None))
            def update_view():
                if count[0]>len(steps):raise AssertionError('Native update exceeded fixture')
                step=steps[count[0]-1]
                if step=='error':raise MapDetectionError('fixture geometry')
                if step=='io':raise IOError('fixture io')
                return step=='success'
            c._update_view=update_view;c._update_view_data=lambda:None
            error=None
            try:Camera.update(c)
            except Exception as e:error=type(e).__name__
            updates.append(dict(name=name,steps=steps,captures=count[0],error=error))
    output.write_text(json.dumps(dict(cases=cases,updates=updates)),encoding='utf-8')


if __name__ == '__main__': main()
