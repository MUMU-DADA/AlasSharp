"""Exercise task-local native controls with bounded synthetic device feedback.

Only this diagnostic owns scenario inputs. Production keeps the original task
methods and controls. No device, account, exported coordinates or live outcome
is used here. Every discovered factory must have an executed source location.
"""
from __future__ import annotations

import ast
from collections import defaultdict
from contextlib import contextmanager
import inspect
import json
from pathlib import Path
import sys
import textwrap
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))


def compared_literals(method, argument):
    """Get finite scenario inputs from native branches, not a copied event list."""
    tree = ast.parse(textwrap.dedent(inspect.getsource(method)))
    return sorted({node.comparators[0].value for node in ast.walk(tree)
                   if isinstance(node, ast.Compare) and isinstance(node.left, ast.Name)
                   and node.left.id == argument and len(node.ops) == 1
                   and isinstance(node.ops[0], ast.Eq)
                   and isinstance(node.comparators[0], ast.Constant)
                   and isinstance(node.comparators[0].value, str)})


@contextmanager
def capture_constructor(cls, created):
    original = cls.__init__

    def construct(obj, *args, **kwargs):
        caller = inspect.currentframe().f_back
        location = (caller.f_globals['__name__'], caller.f_code.co_name, caller.f_lineno)
        del caller
        original(obj, *args, **kwargs)
        created(obj, location)

    with patch.object(cls, '__init__', construct):
        yield


def coalition_cases(av, record):
    from module.coalition.ui import CoalitionUI
    from module.config.config import AzurLaneConfig
    from module.ui.switch import Switch

    cases = 0
    for method_name in ('coalition_ensure_mode', 'coalition_set_fleet'):
        method = getattr(CoalitionUI, method_name)
        events = compared_literals(method, 'event')
        modes = compared_literals(method, 'mode')
        if not events or len(modes) != 2:
            raise AssertionError(f'Fixture must review native inputs for {method_name}')
        for event in events:
            for desired in modes:
                for initial in modes:
                    active, slots, clicks, screenshots, additional = [initial], [], [], [], []

                    def created(control, location):
                        slots.append((control, location))

                    def appear(button, **kwargs):
                        control = slots[-1][0]
                        data = control.get_data(active[0])
                        if 'offset' in kwargs:
                            expected = next(row['offset'] for row in control.state_list
                                            if row['check_button'] is button)
                            assert kwargs['offset'] == expected
                        return button is data['check_button']

                    def click(button):
                        control = slots[-1][0]
                        expected = desired if control.is_selector else active[0]
                        assert button is control.get_data(expected)['click_button']
                        clicks.append(button.name)
                        active[0] = desired
                        if len(clicks) > 2:
                            raise AssertionError('Switch did not converge after synthetic feedback')

                    def screenshot():
                        screenshots.append(True)
                        if len(screenshots) > 8:
                            raise AssertionError('Switch exceeded fixture frame bound')

                    def story_skip():
                        additional.append(True)
                        return len(additional) == 1

                    device = SimpleNamespace(image=None, screenshot=screenshot, click=click)
                    main = CoalitionUI(AzurLaneConfig('template'), device)
                    main.appear = appear
                    main.image_color_count = appear
                    main.handle_story_skip = story_skip
                    with capture_constructor(Switch, created):
                        result = method(main, event, desired)
                    cases += 1
                    if not slots:
                        assert not clicks and result is None
                        continue  # A native event explicitly has no mode switch.
                    assert len(slots) == 1
                    control, location = slots[0]
                    assert active[0] == desired
                    assert len(clicks) == int(initial != desired)
                    if method_name == 'coalition_set_fleet':
                        assert result is (initial != desired)
                    for state in control.state_list:
                        assert av._resolve(av._asset_id_map()[id(state['check_button'])]) is state['check_button']
                    record(location, dict(server=av.server_module.server, event=event, target=desired,
                                          initial=initial, native_kind=type(control).__name__,
                                          clicks=len(clicks), screenshots=len(screenshots),
                                          additional=len(additional)))
    return cases


def hospital_cases(av, record):
    import numpy as np
    from module.base.button import Button
    from module.config.config import AzurLaneConfig
    from module.event_hospital.clue import HospitalClue
    from module.ui.scroll import Scroll

    for initial in (None, 0.0, 0.55, 1.0):
        position, controls, swipes, observed, frames, clock = [initial], [], [], [], [0], [100.0]
        device = SimpleNamespace(image=np.zeros((720, 1280, 3), dtype=np.uint8))

        def paint():
            rule = controls[0][0]
            device.image.fill(0)
            if position[0] is None:
                return
            x1, y1, x2, y2 = map(int, rule.area)
            length = max(1, rule.total // 4)
            start = int(round(position[0] * (rule.total - length)))
            if rule.is_vertical:
                device.image[y1 + start:y1 + start + length, x1:x2] = rule.color
            else:
                device.image[y1:y2, x1 + start:x1 + start + length] = rule.color

        def created(control, location):
            controls.append((control, location))
            assert len(controls) == 1
            paint()

        def screenshot():
            frames[0] += 1
            clock[0] += 2.0
            if frames[0] > 80:
                raise AssertionError('Scroll exceeded fixture frame bound')
            paint()

        def swipe(start, end, **kwargs):
            rule = controls[0][0]
            axis = 1 if rule.is_vertical else 0
            target = (end[axis] - rule.area[axis] - rule.length / 2) / (rule.total - rule.length)
            position[0] = min(1.0, max(0.0, float(target)))
            swipes.append(dict(target=position[0], name=kwargs['name']))
            assert kwargs['name'] == rule.name

        def invest():
            observed.append(position[0])
            if len(observed) > 20:
                raise AssertionError('Native iterator did not reach scroll end')
            return Button(area=(0, 0, 1, 1), button=(0, 0, 1, 1), color=(),
                          name=f'fixture-invest-{len(observed)}')

        device.screenshot = screenshot
        device.swipe = swipe
        main = HospitalClue(AzurLaneConfig('template'), device)
        main.get_invest_button = invest
        with capture_constructor(Scroll, created), patch('module.base.timer.time', side_effect=lambda: clock[0]):
            buttons = list(main.iter_invest())
        rule, location = controls[0]
        assert len(buttons) == len(observed) and buttons
        if initial is None:
            assert len(buttons) == 1 and not swipes
            assert not rule.appear(main)
        else:
            assert observed[0] == initial and rule.at_bottom(main)
            assert any(value < rule.edge_threshold for value in observed)
            assert all(observed[i + 1] > observed[i] for i in range(1, len(observed) - 1))
            assert swipes
        record(location, dict(server=av.server_module.server, initial=initial, native_kind=type(rule).__name__,
                              swipes=len(swipes), frames=frames[0], yielded=len(buttons),
                              final=position[0]))
    return 4


def check_factories(av, declarations, servers=None):
    from module.base.base import ModuleBase
    from module.device.device import Device

    evidence = defaultdict(list)
    failures = {}
    handlers = {'module.coalition.ui': coalition_cases, 'module.event_hospital.clue': hospital_cases}
    servers = list(av.server_module.VALID_SERVER if servers is None else servers)
    saved_server = av.server_module.server
    try:
        with patch.object(ModuleBase, 'EARLY_OCR_IMPORT', True), \
                patch.object(Device, '__init__', side_effect=AssertionError('No device allowed in factory fixtures')):
            for server in servers:
                av.op_set_server({'server': server})
                for module, handler in handlers.items():
                    try:
                        handler(av, lambda location, observation: evidence[location].append(observation))
                    except Exception as error:
                        failures.setdefault(module, []).append(f'{server}: {type(error).__name__}: {error}')
    finally:
        av.op_set_server({'server': saved_server})
    records = []
    for declaration in declarations:
        if declaration['scope'] != 'factory':
            continue
        row = dict(declaration)
        observations = evidence[(row['module'], row['attr'], row['line'])]
        errors = failures.get(row['module'], [])
        covered = {observation['server'] for observation in observations}
        if not errors and covered != set(servers):
            errors = [f'No executed factory fixture for all servers: {sorted(covered)}']
        row.update(status='failed' if errors else 'passed', synthetic_cases=len(observations),
                   observations=observations, evidence='native task/control loops with synthetic device feedback')
        if errors:
            row['error'] = '; '.join(errors)
        records.append(row)
    return records


def main():
    import alas_vision as av
    from module.logger import logger
    logger.setLevel(50)
    result = check_factories(av, av.op_ui_rule_list({})['declarations'])
    output = ROOT / '.runtime/verification/native-control-factories.json'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    failures = [row for row in result if row['status'] != 'passed']
    for failure in failures:
        print(f"FAIL {failure['module']}.{failure['attr']}:{failure['line']}: {failure['error']}")
    print(f"Native factory declarations: {len(result) - len(failures)}/{len(result)} passed; "
          f"{sum(row['synthetic_cases'] for row in result)} synthetic cases; no device")
    return int(bool(failures) or not result)


if __name__ == '__main__':
    raise SystemExit(main())
