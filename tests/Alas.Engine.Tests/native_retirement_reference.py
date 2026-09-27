"""Offline retirement oracle. Executes upstream methods with synthetic screenshots/device boundaries only."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, output = [Path(p).resolve() for p in sys.argv[1:]]
    sys.path.insert(0, str(root))
    os.chdir(output.parent)
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        import module.base.timer as timers
        from module.base.base import ModuleBase
        from module.base.button import Button
        from module.base.utils import color_mask, load_image
        from module.retire.retirement import Retirement, CARD_GRIDS, CARD_RARITY_GRIDS, CARD_RARITY_COLORS
        from module.retire.dock import Dock
        from module.retire.setting import QuickRetireSettingHandler
        from module.retire import assets as assets
        from module.handler.info_handler import InfoHandler
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class Replay(Retirement):
            def __init__(self, frames, server='cn', mode='one_click_retire', keep=True):
                self.frames, self.frame, self.now = frames, 0, 100.
                self.config = SimpleNamespace(SERVER=server, Retirement_RetireMode=mode,
                    OldRetire_SR=False, OldRetire_SSR=False, OneClickRetire_KeepLimitBreak='keep_limit_break' if keep else 'do_not_keep',
                    is_task_enabled=lambda name: False, DOCK_FULL_TRIGGERED=False)
                self.device = SimpleNamespace(screenshot=self.screenshot, click=self.click, sleep=lambda value: None,
                    click_record_clear=lambda: None, stuck_record_clear=lambda: None)
                self.calls, self.clicks, self.intervals = [], [], {}
            def screenshot(self):
                self.frame += 1
                self.now += .5
                if self.frame > 180: raise TimeoutError('Synthetic retirement did not terminate')
            def appear(self, button, offset=0, interval=0, **kw):
                name = button.name
                if interval:
                    timer = self.get_interval_timer(button, interval, renew=True)
                    if not timer.reached(): return False
                result = name in self.frames[min(self.frame, len(self.frames)-1)]
                if result and interval: self.get_interval_timer(button).reset()
                return result
            def match_template_color(self, button, **kw): return self.appear(button, **kw)
            def click(self, button): self.clicks.append(dict(asset=button.name, frame=self.frame))
            @property
            def interval_timer(self): return self.intervals
            def handle_game_tips(self): return False
            def info_bar_count(self): return int('$info' in self.frames[min(self.frame, len(self.frames)-1)])

        confirmations = []
        for server in ['cn', 'en', 'jp', 'tw']:
            for frames in [
                [['SHIP_CONFIRM_2'], ['GET_ITEMS_1'], ['EQUIP_CONFIRM'], ['EQUIP_CONFIRM_2'], ['IN_RETIREMENT_CHECK']],
                [['SHIP_CONFIRM'], ['EQUIP_CONFIRM_2'], ['IN_RETIREMENT_CHECK']],
                [['SHIP_CONFIRM_2'], ['POPUP_CANCEL', 'POPUP_CONFIRM'], ['SR_SSR_CONFIRM'],
                    ['EQUIP_CONFIRM', 'IN_RETIREMENT_CHECK'], ['EQUIP_CONFIRM_2'], ['IN_RETIREMENT_CHECK']],
                [['SHIP_CONFIRM_2'], ['GET_ITEMS_1'], ['IN_RETIREMENT_CHECK']],
                [['IN_RETIREMENT_CHECK']],
                [['SHIP_CONFIRM_2'], ['IN_RETIREMENT_CHECK']],
            ]:
                actor = Replay(frames, server)
                timers.time = lambda: actor.now
                actor._retirement_confirm()
                confirmations.append(dict(server=server, frames=frames, clicks=actor.clicks, lastFrame=actor.frame,
                    strictReject=len(frames) <= 2 and frames[-1] == ['IN_RETIREMENT_CHECK']))

        # Actual handler fallback order, with ship algorithms replaced by their observed numeric results.
        pipelines = []
        for server in ['cn', 'en', 'jp', 'tw']:
            for keep in [True, False]:
                for success_at in range(6):
                    actor = Replay([[]], server, keep=keep)
                    calls, attempts = [], [0]
                    def one_click():
                        calls.append('one')
                        attempts[0] += 1
                        return 10 if attempts[0] == success_at else 0
                    actor.retire_ships_one_click = one_click
                    actor.dock_favourite_set = lambda *a, **kw: calls.append('favourite_off')
                    actor.dock_filter_set = lambda *a, **kw: calls.append('filter_defaults')
                    actor.quick_retire_setting_set = lambda filter_5='all': calls.append('quick_'+str(filter_5))
                    actor.retire_gems_farming_flagships = lambda **kw: 0
                    actor._retirement_quit = lambda: calls.append('quit')
                    error = None
                    try: actor._retire_handler()
                    except Exception as exc: error = type(exc).__name__
                    pipelines.append(dict(server=server, keep=keep, successAt=success_at, calls=calls,
                        error=error, dockFull=actor.config.DOCK_FULL_TRIGGERED))

        dock = object.__new__(Dock)
        setting = dock.dock_filter
        groups = [dict(name=name, default=default, options=[dict(name=option, area=list(button.area))
            for (group, option), button in setting.settings.items() if group == name])
            for name, default in setting.settings_default.items()]
        quick = object.__new__(QuickRetireSettingHandler).retire_setting
        quick_groups = [dict(name=name, default=default, options=[dict(name=option, asset=button.name)
            for (group, option), button in quick.settings.items() if group == name])
            for name, default in quick.settings_default.items()]

        settings = []
        for requested in [{}, {'rarity': ['common','rare']}, {'sort':None,'index':'cv','rarity':'common'},
                {'faction':['eagle','royal'],'extra':['not_level_max','enhanceable']}]:
            active = set()
            clicks = []
            clock = [100.]
            button_names = {id(button): group+'/'+name for (group,name),button in setting.settings.items()}
            def click(button):
                key = button_names[id(button)]
                group, option = key.split('/')
                if option in ['all','no_limit'] or group == 'sort':
                    active.difference_update([item for item in active if item.startswith(group+'/')])
                else: active.discard(group+'/all'); active.discard(group+'/no_limit')
                active.add(key)
                clicks.append(key)
            def shot(): clock[0] += .5
            setting.main = SimpleNamespace(device=SimpleNamespace(screenshot=shot, click=click))
            setting.is_option_active = lambda button: button_names[id(button)] in active
            timers.time = lambda: clock[0]
            setting.set(**requested)
            settings.append(dict(required=requested, clicks=clicks, active=sorted(active)))

        colors = list(CARD_RARITY_COLORS.values())
        selections = []
        for seed in range(8):
            image = np.zeros((720,1280,3),dtype=np.uint8)
            for i, button in enumerate(CARD_RARITY_GRIDS.buttons):
                x1,y1,x2,y2 = button.area
                color = colors[(seed+i)%4] if (seed+i)%5 else (0,0,0)
                image[y1:y2,x1:x2] = color
            name = f'rarity-{seed}.png'
            cv2.imwrite(str(output.parent/name), cv2.cvtColor(image,cv2.COLOR_RGB2BGR))
            for allowed in [('N',),('N','R'),('SR','SSR'),('N','R','SR','SSR')]:
                for amount in [1,10]:
                    actor = Replay([[]])
                    actor.device.image = image
                    picked = actor._retirement_choose(amount=amount,target_rarity=allowed)
                    selections.append(dict(file=name, allowed=allowed, amount=amount, picked=picked,
                        clicks=[entry['asset'] for entry in actor.clicks]))

        pixels = []
        rng = np.random.default_rng(1941)
        for threshold,count,color in [(20,250,(181,142,90)), (20,250,(74,117,189)),(30,50,(255,255,255)),(15,50,(40,40,40))]:
            for matched in [0,count,count+1,512]:
                image = np.zeros((720,1280,3),dtype=np.uint8)
                patch = rng.integers(0,256,(16,32,3),dtype=np.uint8)
                patch.reshape(-1,3)[:matched] = color
                image[10:26,20:52] = patch
                name=f'color-{len(pixels)}.png'
                cv2.imwrite(str(output.parent/name),cv2.cvtColor(image,cv2.COLOR_RGB2BGR))
                pixels.append(dict(file=name,color=color,threshold=threshold,count=count,
                    active=bool(cv2.countNonZero(color_mask(patch,color,threshold))>count)))

        templates=[]
        os.chdir(root)
        for name in ['SHIP_CONFIRM','SHIP_CONFIRM_2']:
            for server,button in getattr(assets,name).split_server().items():
                raw=load_image(button.file)
                x1,y1,x2,y2=button.area
                for dx,dy,bias in [(0,0,0),(10,-5,0),(10,-5,100)]:
                    image=np.zeros((720,1280,3),dtype=np.uint8)
                    patch=raw[y1:y2,x1:x2].copy()
                    if bias: patch[:,:,0]=np.clip(patch[:,:,0].astype(int)+bias,0,255).astype(np.uint8)
                    image[y1+dy:y2+dy,x1+dx:x2+dx]=patch
                    button.clear_offset()
                    matched=button.match_template_color(image,offset=(30,30))
                    file=f'template-{len(templates)}.png'
                    cv2.imwrite(str(output.parent/file),cv2.cvtColor(image,cv2.COLOR_RGB2BGR))
                    templates.append(dict(file=file,server=server,asset=name,matched=bool(matched),button=list(button.button)))

        low=[]
        for ignore in [False,True]:
            for visible in [[],['POPUP_CANCEL','POPUP_CONFIRM'],['POPUP_CONFIRM_WHITE']]:
                actor=Replay([visible]); actor.emotion=SimpleNamespace(is_ignore=ignore)
                timers.time=lambda:actor.now
                value=InfoHandler.handle_combat_low_emotion(actor)
                low.append(dict(ignore=ignore,visible=visible,result=bool(value),clicks=actor.clicks,
                    reset='AUTO_SEARCH_MAP_OPTION_OFF' in actor.intervals))
        sources={name:hashlib.sha256((root/name).read_bytes()).hexdigest() for name in [
            'module/retire/retirement.py','module/retire/dock.py','module/retire/setting.py','module/ui/setting.py',
            'module/ui/ui.py','module/base/base.py','module/handler/info_handler.py']}
    def encode(value):
        if isinstance(value, np.generic): return value.item()
        raise TypeError(f'Unsupported oracle value: {type(value).__name__}')
    output.write_text(json.dumps(dict(confirmations=confirmations,pipelines=pipelines,groups=groups,
        quickGroups=quick_groups,settings=settings,selections=selections,pixels=pixels,templates=templates,low=low,sources=sources),
        default=encode),encoding='utf-8')


if __name__=='__main__': main()
