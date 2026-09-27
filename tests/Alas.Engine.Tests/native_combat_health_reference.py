"""Offline oracle: execute native HP balance, repair, color stability and ADB drag on synthetic inputs."""
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
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        import module.base.timer as timers
        from module.base.base import ModuleBase
        from module.base.button import Button
        from module.combat.hp_balancer import HPBalancer
        from module.combat.combat import Combat
        from module.device.control import Control
        from module.combat.assets import MAIN_FLEET_POWER_ZERO
        from module.logger import logger
        logger.setLevel('CRITICAL')

        source = object.__new__(HPBalancer)
        source.config = SimpleNamespace(HpControl_HpBalanceThreshold=.2, DEVICE_CONTROL_METHOD='ADB')
        orders = []
        values = [0., .001, .1, .2, .3, .4, .6, .8, 1.]
        for hp in itertools.product(values, repeat=3):
            for threshold in [0., .1, .2, 1.]:
                source.config.HpControl_HpBalanceThreshold = threshold
                orders.append(dict(hp=hp, threshold=threshold,
                    order=[int(i) for i in source._expected_scout_order(hp)]))
        exchanges = []
        for target in itertools.permutations(range(3)):
            for method in ['ADB', 'minitouch']:
                source.config.DEVICE_CONTROL_METHOD = method
                exchanges.append(dict(target=target, minitouch=method == 'minitouch',
                    steps=[[int(i) for i in pair] for pair in source._gen_exchange_step(target)]))

        drags = []
        actor = SimpleNamespace(config=SimpleNamespace(Emulator_ControlMethod='ADB'),
            handle_control_check=lambda name: None,
            swipe_adb=lambda p1, p2, duration: drags.append(dict(kind='swipe', start=list(p1), end=list(p2), seconds=duration)),
            click=lambda button, control_check: drags.append(dict(kind='tapArea', area=list(button.button), check=control_check)))
        Control.drag(actor, (403, 421), (821, 326), segments=3)

        now = [100.]
        timers.time = lambda: now[0]
        area = tuple(int(v) for v in MAIN_FLEET_POWER_ZERO.area)
        rng = np.random.default_rng(8045)
        images = []
        for i in range(3):
            image = np.zeros((720, 1280, 3), dtype=np.uint8)
            image[area[1]:area[3], area[0]:area[2]] = rng.integers(
                0, 256, (area[3]-area[1], area[2]-area[0], 3), dtype=np.uint8)
            if not cv2.imwrite(str(output.parent / f'power-{i}.png'), cv2.cvtColor(image, cv2.COLOR_RGB2BGR)):
                raise OSError('Cannot write synthetic stability image')
            images.append(image)

        class Stable(ModuleBase):
            def __init__(self, frames):
                self.frames, self.frame = frames, 0
                self.device = SimpleNamespace(image=images[frames[0]], screenshot=self.screenshot)
            def screenshot(self):
                self.frame += 1
                now[0] += .125
                self.device.image = images[self.frames[min(self.frame, len(self.frames)-1)]]
                if self.frame > 100: raise AssertionError('Stability did not terminate')
        stability = []
        stable_timer = timers.Timer(.3, count=1)
        for frames in [[0, 0], [0, 1, 1, 1, 1, 1], [2, 2], [0, 1, 2]*22, [1, 1, 1, 1, 1, 1]]:
            actor = Stable(frames)
            checker = Button(area=area, color=(), button=area, name='STABLE_CHECKER')
            ModuleBase.wait_until_stable(actor, checker, timer=stable_timer, timeout=timers.Timer(5, count=10))
            stability.append(dict(frames=frames, lastFrame=actor.frame,
                stable=actor.frame < 40))

        class Repair(Combat):
            def __init__(self, hp, frames, single=.3, multi=.6, enabled=True):
                self.config = SimpleNamespace(HpControl_UseEmergencyRepair=enabled,
                    HpControl_RepairUseSingleThreshold=single, HpControl_RepairUseMultiThreshold=multi)
                self.hp_reset()
                self.hp = hp
                self.frames, self.frame = frames, 0
                self.calls, self.clicks = [], []
                self.device = SimpleNamespace(image=images[0], screenshot=self.screenshot, click=self.click)
            def screenshot(self):
                self.frame += 1
                now[0] += .125
                if self.frame > 100: raise AssertionError('Repair did not terminate')
            def appear(self, button, offset=0, interval=0, threshold=10, **kw):
                bounds = ([-3, -30, 3, 30] if offset is True else
                    [-offset[0], -offset[1], *offset] if isinstance(offset, tuple) else [0, 0, 0, 0])
                self.calls.append(dict(asset=button.name, offset=bounds, interval=interval, threshold=threshold, frame=self.frame))
                return button.name in self.frames[min(self.frame, len(self.frames)-1)]
            def click(self, button): self.clicks.append(dict(asset=button.name, frame=self.frame))
            def interval_clear(self, button): self.calls.append(dict(clear=button.name, frame=self.frame))
            def wait_until_stable(self, button):
                # Execute native helper with an unstarted per-fixture timer, matching a fresh Engine session.
                ModuleBase.wait_until_stable(self, button, timers.Timer(.3, count=1), timers.Timer(5, count=10))

        available = ['BATTLE_PREPARATION', 'EMERGENCY_REPAIR_AVAILABLE']
        traces = []
        cases = [([.8, 0, 0, .2, .8, .8], [available, available+['MAIN_FLEET_POWER_ZERO'], available, available], True),
            ([.8, 0, 0, .2, .8, .8], [available, [], []], True),
            ([.8, 0, 0, .8, .8, .8], [available]*4, True),
            ([.8, 0, 0, .2, .8, .8], [['EMERGENCY_REPAIR_CONFIRM']], True),
            ([.8, 0, 0, .2, .8, .8], [available], False),
            ([], [available]*4, True), ([0]*6, [available]*4, True)]
        for hp, frames, enabled in cases:
            actor = Repair(hp, frames, enabled=enabled)
            result = actor.handle_emergency_repair_use()
            traces.append(dict(hp=hp, frames=frames, enabled=enabled, result=bool(result),
                calls=actor.calls, clicks=actor.clicks, lastFrame=actor.frame))

        decisions = []
        hp_cases = [[0]*6, [], [.001, 0, 0, .9, 0, 0], [.9, 0, 0, .001, 0, 0]]
        hp_cases += [[a, b, c, d, e, f] for a, b, c, d, e, f in
            itertools.product([0, .001, .3, .6, .9], repeat=6)]
        for hp in hp_cases:
            actor = Repair(hp, [available]*4)
            # Preserve the actual repair handler's branch and numeric comparisons;
            # only bypass waits for this large numeric matrix.
            actor.wait_until_disappear = lambda *a, **kw: None
            actor.wait_until_stable = lambda *a, **kw: None
            decisions.append(dict(hp=hp, single=.3, multi=.6, use=bool(actor.handle_emergency_repair_use())))
        for hp, single, multi in [([.8, 0, 0, .3, .6, .9], s, m)
                for s in [0., .3, float(np.nextafter(.3, 1)), .9, 1.]
                for m in [0., .6, .8, float(np.nextafter(.8, 1)), 1.]]:
            actor = Repair(hp, [available]*4, single=single, multi=multi)
            actor.wait_until_disappear = lambda *a, **kw: None
            actor.wait_until_stable = lambda *a, **kw: None
            decisions.append(dict(hp=hp, single=single, multi=multi, use=bool(actor.handle_emergency_repair_use())))

        actor = Repair([], [available])
        actor._automation_set_timer = timers.Timer(1)
        actor.device.sleep = lambda seconds: now.__setitem__(0, now[0]+seconds)
        automation = []
        for elapsed, assets in [(0., ['AUTOMATION_OFF']), (.5, ['AUTOMATION_OFF']),
                (.5, ['AUTOMATION_OFF']), (.125, ['AUTOMATION_ON']),
                (.125, ['AUTOMATION_CONFIRM_CHECK', 'AUTOMATION_CONFIRM']),
                (.5, ['AUTOMATION_OFF']), (.625, ['AUTOMATION_OFF'])]:
            now[0] += elapsed
            actor.frames = [assets]
            actor.calls, actor.clicks = [], []
            handled = actor.handle_combat_automation_set(auto=True)
            automation.append(dict(elapsed=elapsed, assets=assets, result=handled, calls=actor.calls, clicks=actor.clicks))

        class Preparation(Combat):
            def __init__(self):
                self.calls = []
                self.device = SimpleNamespace(stuck_record_clear=lambda: None, click_record_clear=lambda: None,
                    screenshot_interval_set=lambda value: self.calls.append('interval'))
            def hp_balance(self): self.calls.append('balance')
            def loop(self): yield None
            def appear(self, *a, **kw): return True
            def handle_combat_automation_set(self, auto): self.calls.append('automation'); return False
            def handle_retirement(self): self.calls.append('retirement'); return False
            def handle_combat_low_emotion(self): self.calls.append('emotion'); return False
            def handle_emergency_repair_use(self): self.calls.append('repair'); return False
            def handle_battle_preparation(self): self.calls.append('battle'); return False
            def handle_combat_automation_confirm(self): self.calls.append('confirm'); return False
            def handle_story_skip(self): self.calls.append('story'); return False
            def is_combat_loading(self): return False
            def is_combat_executing(self): self.calls.append('executing'); return True
        preparation = Preparation()
        preparation.combat_preparation(balance_hp=True)
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in [
            'module/combat/hp_balancer.py', 'module/combat/combat.py', 'module/base/base.py', 'module/device/control.py']}
    output.write_text(json.dumps(dict(orders=orders, exchanges=exchanges, drags=drags, area=area,
        stability=stability, traces=traces, decisions=decisions, automation=automation,
        preparation=preparation.calls, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
