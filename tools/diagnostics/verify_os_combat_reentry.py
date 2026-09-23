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
sys.path.insert(0, str(ROOT / 'tools'))
os.chdir(ENGINE)

import alas_vision as vision  # noqa: E402
from module.combat.combat import Combat as BaseCombat  # noqa: E402
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
    results.append(check('non-combat remains excluded',
                         patched(State()) is False))
    vision.apply_os_combat_reentry_compat()
    results.append(check('compatibility hook is idempotent',
                         Combat.combat_appear is patched))

    if len(sys.argv) > 1:
        given = Path(sys.argv[1])
        frame = given if given.is_absolute() else CALLER_CWD / given
        loaded = vision.op_screenshot_load({'path': str(frame)})
        image = vision._state.get('image')
        pause = vision._resolve('combat_ui/PAUSE_New')
        matched = loaded.get('shape') == list(image.shape) and pause.match_template_color(
            image, offset=(10, 10))
        results.append(check('saved failure frame has upstream running-combat control', matched))
        state = State()
        state.device = SimpleNamespace(image=image, stuck_record_add=lambda _: None)
        state.config = SimpleNamespace(SERVER='cn')
        state.is_combat_executing = lambda: BaseCombat.is_combat_executing(state)
        detected = state.is_combat_executing()
        results.append(check('upstream combat detector recognizes saved failure frame',
                             detected is pause))
        results.append(check('saved failure frame enters reacquisition path',
                             matched and patched(state) is True))

    return 0 if all(results) else 1


if __name__ == '__main__':
    sys.exit(main())
