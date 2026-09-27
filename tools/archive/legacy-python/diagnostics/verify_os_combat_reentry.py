#!/usr/bin/env python3
"""Check the upstream OS combat handoff when auto-search skips preparation."""
from __future__ import annotations

import os
import sys
from pathlib import Path
from types import SimpleNamespace


ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
CALLER_CWD = Path.cwd()
FRAME_FIXTURE = ROOT / 'tools' / 'diagnostics' / 'fixtures' / 'os_combat_pause_redacted.png'
sys.path.insert(0, str(ROOT / 'tools'))
os.chdir(ENGINE)

import alas_vision as vision  # noqa: E402
from module.combat.assets import BATTLE_PREPARATION_WITH_OVERLAY  # noqa: E402
from module.combat.combat import Combat as BaseCombat  # noqa: E402
from module.os_combat.assets import SIREN_PREPARATION  # noqa: E402
from module.os_combat.combat import Combat  # noqa: E402
from module.os.map import OSMap  # noqa: E402
from module.os.tasks.stronghold import OpsiStronghold  # noqa: E402


class State:
    def __init__(self, *, map_=False, loading=False, executing=False, preparation=False):
        self.map = map_
        self.loading = loading
        self.executing = executing
        self.preparation = preparation

    def is_in_map(self):
        return self.map

    def is_combat_loading(self):
        return self.loading

    def is_combat_executing(self):
        return self.executing

    def appear(self, button, **kwargs):
        return self.preparation

    def handle_combat_automation_confirm(self):
        return False


class OverlayState(State):
    """替身：执行态与准备覆盖层同时命中，记录上游确认分支有没有被调用。

    上游 `combat_appear()` 的最后一条准备分支是
    `appear(BATTLE_PREPARATION_WITH_OVERLAY) and handle_combat_automation_confirm()`，
    后者会点一次 `AUTOMATION_CONFIRM`。AzurPilot 257bef255d 把执行态判据放在准备态**之前**，
    所以"执行态 + 覆盖层"重叠时不应走到那次点击；没打补丁的顺序会走进去。
    """

    def __init__(self, *, executing=False, confirm=True):
        super().__init__(executing=executing)
        self.confirm_result = confirm
        self.confirm_calls = 0

    def appear(self, button, **kwargs):
        return button is BATTLE_PREPARATION_WITH_OVERLAY

    def handle_combat_automation_confirm(self):
        self.confirm_calls += 1
        return self.confirm_result


class SirenPreparationState(State):
    def __init__(self, *, executing=False):
        super().__init__(executing=executing)
        self.siren_offsets = []

    def appear(self, button, **kwargs):
        if button is SIREN_PREPARATION:
            self.siren_offsets.append(kwargs.get('offset'))
            return kwargs.get('offset') == (20, 20)
        return False


class LoadingState(State):
    def __init__(self):
        super().__init__(loading=True, executing=True)
        self.executing_calls = 0

    def is_combat_executing(self):
        self.executing_calls += 1
        return self.executing


class DaemonState(State):
    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.combat_calls = 0
        self.device = SimpleNamespace(stuck_record_clear=lambda: None)

    def on_auto_search_battle_count_reset(self):
        pass

    def on_auto_search_battle_count_add(self):
        pass

    def hp_reset(self):
        pass

    def loop(self):
        yield None

    def handle_os_auto_search_map_option(self, **kwargs):
        return False

    def handle_retirement(self):
        return False

    def combat_appear(self):
        return Combat.combat_appear(self)

    def auto_search_combat(self, **kwargs):
        self.combat_calls += 1
        return True

    def handle_map_event(self):
        return False


def check(label, passed):
    print(f'{"PASS" if passed else "FAIL"}: {label}')
    return passed


def main():
    results = []
    vision.apply_os_combat_reentry_compat()
    patched = Combat.combat_appear
    results.append(check('running combat is reacquired',
                         patched(State(executing=True)) is True))
    results.append(check('native stronghold inherits the patched OS combat handler',
                         Combat in OpsiStronghold.__mro__
                         and OpsiStronghold.combat_appear is patched))
    running = DaemonState(executing=True)
    results.append(check('native auto-search daemon hands running combat to its handler',
                         OSMap.os_auto_search_daemon(running) == 1
                         and running.combat_calls == 1))
    mapped = DaemonState(map_=True, executing=True)
    results.append(check('native auto-search daemon does not enter combat from the map',
                         OSMap.os_auto_search_daemon(mapped) == 0
                         and mapped.combat_calls == 0))
    results.append(check('map remains excluded even with a pause-like control',
                         patched(State(map_=True, executing=True)) is False))
    results.append(check('loading and preparation retain their original result',
                         patched(State(loading=True)) is True
                         and patched(State(preparation=True)) is True))
    siren = SirenPreparationState()
    results.append(check('native siren preparation retains its upstream offset',
                         patched(siren) is True and siren.siren_offsets == [(20, 20)]))
    siren_running = SirenPreparationState(executing=True)
    results.append(check('executing combat precedes siren preparation',
                         patched(siren_running) is True and not siren_running.siren_offsets))
    loading = LoadingState()
    results.append(check('loading precedes the running-combat detector',
                         patched(loading) is True and loading.executing_calls == 0))
    results.append(check('non-combat remains excluded',
                         patched(State()) is False))
    overlay = OverlayState(executing=True)
    overlay_wins = patched(overlay) is True and overlay.confirm_calls == 0
    results.append(check('executing combat wins over the preparation overlay '
                         f'(native confirm calls: {overlay.confirm_calls})',
                         overlay_wins))
    overlay_only = OverlayState()
    overlay_native = patched(overlay_only) is True and overlay_only.confirm_calls == 1
    results.append(check('preparation overlay still takes the native confirm branch '
                         f'(native confirm calls: {overlay_only.confirm_calls})',
                         overlay_native))
    overlay_refused = OverlayState(confirm=False)
    overlay_stays_false = (patched(overlay_refused) is False
                           and overlay_refused.confirm_calls == 1)
    results.append(check('overlay without confirmation keeps the original result '
                         f'(native confirm calls: {overlay_refused.confirm_calls})',
                         overlay_stays_false))
    vision.apply_os_combat_reentry_compat()
    results.append(check('compatibility hook is idempotent',
                         Combat.combat_appear is patched))

    frame = FRAME_FIXTURE
    frame_label = 'redacted failure-frame fixture'
    if len(sys.argv) > 1:
        given = Path(sys.argv[1])
        frame = given if given.is_absolute() else CALLER_CWD / given
        frame_label = 'provided failure frame'
    if not frame.is_file():
        results.append(check(f'{frame_label} is available', False))
        return 1

    loaded = vision.op_screenshot_load({'path': str(frame)})
    image = vision._state.get('image')
    pause = vision._resolve('combat_ui/PAUSE_New')
    matched = loaded.get('shape') == list(image.shape) and pause.match_template_color(
        image, offset=(10, 10))
    results.append(check(f'{frame_label} has upstream running-combat control', matched))
    state = State()
    state.device = SimpleNamespace(image=image, stuck_record_add=lambda _: None)
    state.config = SimpleNamespace(SERVER='cn')
    state.is_combat_executing = lambda: BaseCombat.is_combat_executing(state)
    detected = state.is_combat_executing()
    results.append(check(f'upstream combat detector recognizes {frame_label}',
                         detected is pause))
    results.append(check(f'{frame_label} enters reacquisition path',
                         matched and patched(state) is True))

    return 0 if all(results) else 1


if __name__ == '__main__':
    sys.exit(main())
