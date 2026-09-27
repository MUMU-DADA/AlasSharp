"""Offline audit: native Config binding versus the host's screenshot override.

Only this check's ignored output directory may be written. Config storage,
physical devices, benchmark timings, screenshots and logger are in-memory fakes.
Native Config methods are imported unchanged; native screenshot/benchmark methods
and current host device functions are compiled from their unmodified source AST.
"""
from __future__ import annotations

import ast
import copy
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import sys
import types
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime/engine'
OUTPUT = ROOT / '.runtime/verification/screenshot-auto'
OUTPUT.mkdir(parents=True, exist_ok=True)
SOURCE_HASHES = {}
IO = {'config_reads': 0, 'config_writes': 0, 'blocked_io': []}
sys.dont_write_bytecode = True


def guard(event, args):
    if event == 'open':
        path, mode, flags = args
        if isinstance(path, (str, bytes, os.PathLike)):
            target = Path(os.fsdecode(path)).resolve()
            writing = any(c in (mode or '') for c in 'wax+') or bool(
                (flags or 0) & (os.O_WRONLY | os.O_RDWR | os.O_CREAT | os.O_TRUNC))
            if target.is_relative_to(ENGINE / 'config'):
                IO['blocked_io'].append('account-config-open')
                raise AssertionError('Account config I/O is forbidden')
            if writing and not target.is_relative_to(OUTPUT):
                IO['blocked_io'].append('write-outside-audit')
                raise AssertionError('Writes must stay inside the audit output directory')
    if event in ('subprocess.Popen', 'os.system', 'socket.connect', 'socket.connect_ex'):
        IO['blocked_io'].append(event)
        raise AssertionError('Device/process/network I/O is forbidden')


sys.addaudithook(guard)
sys.path.insert(0, str(ENGINE))


class NoOpLogger:
    def __getattr__(self, name):
        return lambda *args, **kwargs: None


# Install before importing Config so importing the real logger cannot create logs.
sys.modules['module.logger'] = types.SimpleNamespace(logger=NoOpLogger())
from module.config.config import AzurLaneConfig  # noqa: E402
from module.config.config_updater import ConfigUpdater  # noqa: E402
from module.config.utils import parse_value  # noqa: E402


def source(path):
    content = path.read_bytes()
    SOURCE_HASHES[path.relative_to(ROOT).as_posix()] = hashlib.sha256(content).hexdigest()
    return ast.parse(content, filename=path.name)


def native_methods(path, class_name, names):
    tree = source(path)
    cls = next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == class_name)
    selected = [copy.deepcopy(n) for n in cls.body
                if isinstance(n, ast.FunctionDef) and n.name in names]
    assert {n.name for n in selected} == set(names)
    namespace = {'datetime': datetime, 'logger': NoOpLogger()}
    exec(compile(ast.Module(body=selected, type_ignores=[]), path.name, 'exec'), namespace)
    return {name: namespace[name] for name in names}, cls


native_shot, _ = native_methods(ENGINE / 'module/device/screenshot.py', 'Screenshot', ['screenshot'])
native_device, device_class = native_methods(ENGINE / 'module/device/device.py', 'Device',
                                            ['run_simple_screenshot_benchmark'])
native_init = next(n for n in device_class.body if isinstance(n, ast.FunctionDef) and n.name == '__init__')
auto_branch = [copy.deepcopy(n) for n in native_init.body if isinstance(n, ast.If)
               and 'self.config.Emulator_ScreenshotMethod' in ast.unparse(n.test)]
assert len(auto_branch) == 1, 'Native auto-selection branch changed; review the new source'
auto_wrapper = ast.FunctionDef(name='initialize_auto',
    args=ast.arguments(posonlyargs=[], args=[ast.arg(arg='self')], kwonlyargs=[], kw_defaults=[], defaults=[]),
    body=auto_branch, decorator_list=[])
auto_module = ast.fix_missing_locations(ast.Module(body=[auto_wrapper], type_ignores=[]))
auto_namespace = {}
exec(compile(auto_module, 'native-device-auto-branch', 'exec'), auto_namespace)

host_tree = source(ROOT / 'tools/alas_vision.py')
host_nodes = [n for n in host_tree.body if isinstance(n, ast.FunctionDef)
              and n.name in ('_device_engine', '_device_config_identity')]
assert len(host_nodes) == 2
host_code = compile(ast.Module(body=host_nodes, type_ignores=[]), 'alas_vision.py', 'exec')
source(ENGINE / 'module/config/config.py')
source(ENGINE / 'module/config/config_updater.py')
descriptor_path = ENGINE / 'module/config/argument/args.json'
catalog = json.loads(descriptor_path.read_text(encoding='utf-8'))
SOURCE_HASHES[descriptor_path.relative_to(ROOT).as_posix()] = hashlib.sha256(descriptor_path.read_bytes()).hexdigest()


def defaults():
    result = {task: {group: {key: parse_value(arg['value'], arg) for key, arg in fields.items()}
                             for group, fields in groups.items()} for task, groups in catalog.items()}
    result['Alas']['Emulator'].update(Serial='fixture-device', ScreenshotMethod='auto',
                                    ControlMethod='MaaTouch', PackageName='com.bilibili.azurlane')
    result['Alas']['Error'].update(SaveError=False, OnePushConfig='')
    result['Alas']['Emulator']['ScreenshotDedithering'] = False
    return result


class MemoryStore:
    def __init__(self):
        self.data = defaults()
        self.saves = []

    def read(self, config_name, is_template=False):
        assert config_name == 'fixture'
        IO['config_reads'] += 1
        return copy.deepcopy(self.data)

    def write(self, config_name, data, mod_name='alas'):
        assert config_name == 'fixture' and mod_name == 'alas'
        IO['config_writes'] += 1
        self.data = copy.deepcopy(data)
        self.saves.append(self.data['Alas']['Emulator']['ScreenshotMethod'])


class LazyDevice:
    """No Device superclass or OS handle; only the native selection code is used."""
    screenshot = native_shot['screenshot']
    run_simple_screenshot_benchmark = native_device['run_simple_screenshot_benchmark']

    def __init__(self, config, events):
        self.config = config
        self.events = events
        self.events.append('device.construct')
        self.screenshot_method_override = ''
        self._screenshot_interval = types.SimpleNamespace(wait=lambda: None, reset=lambda: None)
        self.screenshot_methods = {'ADB': self.screenshot_adb, 'scrcpy': self.screenshot_scrcpy}
        auto_namespace['initialize_auto'](self)

    def resolution_check_uiautomator2(self):
        self.events.append('resolution.synthetic')

    def screenshot_adb(self):
        self.events.append('screenshot.ADB')
        return 'synthetic-frame'

    def screenshot_scrcpy(self):
        self.events.append('screenshot.scrcpy')
        return 'synthetic-frame'

    def _handle_orientated_image(self, image):
        return image

    def check_screen_size(self):
        return True

    def check_screen_black(self):
        return True


class SyntheticBenchmark:
    def __init__(self, config, device):
        self.device = device
        assert device.config is config

    def run_simple_screenshot_benchmark(self):
        self.device.events.append('benchmark.chosen.scrcpy')
        return 'scrcpy'


def run_case(name, transport=None, stored_method='auto', default_entry=False):
    store, events, states, devices = MemoryStore(), [], [], []
    store.data['Alas']['Emulator']['ScreenshotMethod'] = stored_method

    def make_device(config):
        device = LazyDevice(config, events)
        devices.append(device)
        return device

    # The extracted host sees its usual imports, both replaced before execution.
    modules = {
        'module.device.pkg_resources': types.ModuleType('module.device.pkg_resources'),
        'module.device.device': types.SimpleNamespace(Device=make_device),
        'module.daemon.benchmark': types.SimpleNamespace(Benchmark=SyntheticBenchmark),
    }
    env = {'__file__': str(ROOT / 'tools/alas_vision.py'), 'json': json,
           '_DEVICE_OBJ': None, '_DEVICE_KEY': None,
           '_DEVICE_ARGS': dict(serial='fixture-device', screenshot=transport, control='MaaTouch'),
           '_map_config': lambda: config,
           'os': types.SimpleNamespace(path=types.SimpleNamespace(
               normpath=os.path.normpath, join=os.path.join, dirname=os.path.dirname,
               abspath=os.path.abspath, isdir=lambda path: False), environ={}, pathsep=os.pathsep)}
    exec(host_code, env)

    def observe(phase, config, device):
        device.screenshot()
        states.append(dict(phase=phase, native_bound=config.Emulator_ScreenshotMethod,
                           persisted=store.data['Alas']['Emulator']['ScreenshotMethod'],
                           overridden=config.overridden.get('Emulator_ScreenshotMethod'),
                           selected=events[-1]))

    with patch.object(ConfigUpdater, 'args', property(lambda self: catalog)), \
            patch.object(ConfigUpdater, 'read_file', lambda self, *a, **kw: store.read(*a, **kw)), \
            patch.object(ConfigUpdater, 'write_file', staticmethod(store.write)), \
            patch.dict(sys.modules, modules):
        config = AzurLaneConfig('fixture', task='Reward')
        if transport is None:
            device = make_device(config)
        else:
            device = env['_device_engine']() if default_entry else env['_device_engine'](config=config)
        observe('first_device_after_native_benchmark', config, device)
        config.bind(config.task)
        observe('same_config_after_native_bind', config, device)
        reloaded = AzurLaneConfig('fixture', task='Dorm')
        persisted_before_reacquire = reloaded.Emulator_ScreenshotMethod
        if transport is not None:
            assert env['_device_engine'](config=reloaded) is device
        device.config = reloaded  # Native scheduler does this after reacquisition.
        observe('next_task_after_config_reload', reloaded, device)
        assert len(devices) == 1
    return dict(name=name, transport_request=transport, states=states, events=events,
                persisted_before_reacquire=persisted_before_reacquire,
                config_screenshot_writes=store.saves, physical_device_io=0)


def main():
    cases = [run_case('native_baseline'), run_case('host_auto', 'auto'),
             run_case('host_explicit_scrcpy', 'scrcpy'),
             run_case('host_auto_already_resolved', 'auto', stored_method='scrcpy'),
             run_case('default_auto', 'auto', default_entry=True),
             run_case('default_explicit_scrcpy', 'scrcpy', default_entry=True)]
    baseline, automatic, explicit, resolved, default_auto, default_explicit = cases
    for case in cases:
        assert all(x['selected'] == 'screenshot.scrcpy' for x in case['states']), case
    assert all(x['native_bound'] == 'scrcpy' for x in automatic['states']), automatic
    assert all(x['persisted'] == 'scrcpy' for x in automatic['states']), automatic
    assert all(x['overridden'] is None for x in automatic['states']), automatic
    assert automatic['events'].count('benchmark.chosen.scrcpy') == 1
    assert automatic['persisted_before_reacquire'] == 'scrcpy'
    assert 'benchmark.chosen.scrcpy' not in explicit['events']
    assert 'benchmark.chosen.scrcpy' not in resolved['events']
    for case in (explicit, default_explicit):
        assert not case['config_screenshot_writes'], case
        assert all(x['persisted'] == 'auto' for x in case['states']), case
    assert default_auto['events'].count('benchmark.chosen.scrcpy') == 1
    assert not IO['blocked_io'], IO
    result = dict(passed=True, basis='native Config.bind/update/override and native Screenshot.screenshot',
                  cases=cases, io=IO, source_sha256=SOURCE_HASHES,
                  limits=['No physical benchmark or device was run; scrcpy is a synthetic winner.',
                          'Config reads/writes were in-memory; no account or engine file was written.'])
    (OUTPUT / 'results.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                                         encoding='utf-8')
    print('6/6 native screenshot configuration cases, 18 dispatch observations passed; no device/account I/O.')


if __name__ == '__main__':
    main()
