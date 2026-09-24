"""离线验证所选实例与常驻设备的绑定，不导入宿主或读取账号。

默认检查本项目 tools/alas_vision.py；--host 可指定待集成的宿主源码。
只编译四个目标函数，配置、设备、原生任务及其导入均由替身提供。
此检查证明适配器的数据流与拒绝/恢复行为，不证明真实设备后端可用。
"""

from __future__ import annotations

import argparse
import ast
import builtins
import contextlib
import copy
import json
import logging
import os
from pathlib import Path
import tempfile
import types


ROOT = Path(__file__).resolve().parents[2]
FUNCTIONS = {'_map_config', '_device_config_identity', '_device_engine', 'op_periodic_run'}
TRANSPORT = {'serial': 'fixture-device', 'screenshot': 'scrcpy', 'control': 'MaaTouch'}


class Fixture:
    def __init__(self, host: Path, root: Path):
        self.root = root
        self.configs = []
        self.devices = []
        self.writes = []
        self.overrides = []
        self.events = []
        self.mode = 'success'
        self.settings = {}
        (root / 'config').mkdir()
        for name in ('alas', 'fixture-alpha', 'fixture-beta'):
            (root / 'config' / f'{name}.json').write_text('{"fixture":true}\n', encoding='utf-8')
        self.files_before = self.files()
        state = self

        class Config:
            def __init__(self, name, task=None):
                object.__setattr__(self, '_ready', False)
                self.config_name = name
                self.task = types.SimpleNamespace(command=task or 'Alas')
                self.SERVER = 'cn'
                self.bound = {}
                self.overridden = {}
                self.data = {'Alas': {'Emulator': {
                    'Serial': TRANSPORT['serial'], 'ScreenshotMethod': 'adb', 'ControlMethod': 'ADB',
                    'PackageName': name + '.package', 'ServerName': 'fixture-server',
                }, 'EmulatorInfo': {'Emulator': 'fixture-emulator', 'Path': 'fixture-path',
                                    'Name': 'fixture-name'}}}
                for group, values in self.data['Alas'].items():
                    for key, value in values.items():
                        field = f'{group}_{key}'
                        self.bound[field] = f'Alas.{group}.{key}'
                        setattr(self, field, value)
                for field, value in state.settings.items():
                    setattr(self, field, value)
                    if field in self.bound:
                        _, group, key = self.bound[field].split('.')
                        self.data['Alas'][group][key] = value
                object.__setattr__(self, '_ready', True)
                state.configs.append(self)

            def __setattr__(self, key, value):
                if getattr(self, '_ready', False) and key in self.bound:
                    state.writes.append((self.config_name, key, value))
                object.__setattr__(self, key, value)

            def override(self, **values):
                state.overrides.append((self.config_name, dict(values)))
                self.overridden.update(values)
                for key, value in values.items():
                    object.__setattr__(self, key, value)

            @contextlib.contextmanager
            def multi_set(self):
                yield

        class Device:
            def __init__(self, config):
                self.config = config
                self.serial = config.Emulator_Serial
                self.package = config.Emulator_PackageName
                self.server = config.SERVER
                self.screenshot_backend = config.Emulator_ScreenshotMethod
                self.control_backend = config.Emulator_ControlMethod
                state.devices.append(self)
                state.events.append('device.construct')

            def stuck_record_clear(self):
                state.events.append('device.clear_stuck')

            def click_record_clear(self):
                state.events.append('device.clear_click')
                if state.mode == 'pre_dispatch_failure':
                    raise RuntimeError('fixture record reset failed')

            def screenshot(self):
                state.events.append('device.screenshot')

        class Runner:
            def __init__(self, instance):
                self.instance = instance

            def run(self, method):
                state.events.append(('runner.run', self.instance, method, self.config,
                                     self.device.config, self.device.package,
                                     self.device.config.Emulator_ScreenshotMethod))
                self.device.screenshot()
                if state.mode == 'exception':
                    raise RuntimeError('fixture native failure')
                if state.mode == 'system_exit':
                    raise SystemExit(1)
                return state.mode != 'native_false'

        class Failure(logging.Handler):
            kind = None
            traceback_tail = None
            error_directory = None

            def emit(self, record):
                pass

        logger = logging.Logger('instance-device-fixture')
        modules = {
            'module.device.pkg_resources': types.SimpleNamespace(),
            'module.device.device': types.SimpleNamespace(Device=Device),
            'module.config.config': types.SimpleNamespace(AzurLaneConfig=Config),
            'module.api.config_service': types.SimpleNamespace(validate_name=lambda name: name),
            'module.logger': types.SimpleNamespace(logger=logger),
            'alas': types.SimpleNamespace(AzurLaneAutoScript=Runner),
        }

        def import_fixture(name, globals=None, locals=None, fromlist=(), level=0):
            if name in modules:
                return modules[name]
            if name == 'time' or name == 'traceback':
                return builtins.__import__(name, globals, locals, fromlist, level)
            raise AssertionError(f'离线替身未声明的导入: {name}')

        # Do not let the host's optional ADB PATH setup touch the caller's environment.
        fake_path = types.SimpleNamespace(**{
            name: getattr(os.path, name) for name in ('abspath', 'dirname', 'join', 'normpath')
        }, isdir=lambda path: False)
        fake_os = types.SimpleNamespace(path=fake_path, environ={}, pathsep=os.pathsep)
        tree = ast.parse(host.read_text(encoding='utf-8'), filename=host.name)
        selected = [node for node in tree.body
                    if isinstance(node, ast.FunctionDef) and node.name in FUNCTIONS]
        if {node.name for node in selected} != FUNCTIONS:
            raise AssertionError('宿主缺少实例设备绑定目标函数')
        self.env = {
            '__builtins__': {**vars(builtins), '__import__': import_fixture},
            '__file__': str(root / 'tools' / 'alas_vision.py'), 'FORK': str(root),
            'os': fake_os, 'Path': Path, 'json': json,
            '_DEVICE_OBJ': None, '_DEVICE_KEY': None, '_DEVICE_ARGS': dict(TRANSPORT),
            '_LoggedNativeFailure': Failure,
            'native_task_runtime': contextlib.nullcontext,
            'op_periodic_plan': lambda args: {
                'found': True, 'method': args['task'].lower(), 'scheduler_command': args['task'],
            },
        }
        exec(compile(ast.Module(body=selected, type_ignores=[]), host.name, 'exec'), self.env)
        self.Config = Config

    def files(self):
        return {path.name: path.read_bytes() for path in (self.root / 'config').glob('*.json')}

    def run(self, instance='fixture-alpha', task='Reward'):
        result = self.env['op_periodic_run']({
            'task': task, 'instance': instance, 'allow_actions': True, 'confirm': task,
        })
        assert self.files() == self.files_before, '假账号文件被修改'
        return result


def require_success(result):
    assert result.get('decision') == 'ran' and result.get('native_success') is True, result


def verify_first_instance(fixture):
    require_success(fixture.run())
    assert [config.config_name for config in fixture.configs] == ['fixture-alpha']
    assert not fixture.writes, '显式实例路径不得通过默认账号或 setattr 写回设备参数'
    device = fixture.devices[0]
    assert device.config is fixture.configs[0]
    assert device.package == 'fixture-alpha.package' and device.server == 'cn'
    assert device.screenshot_backend == device.config.Emulator_ScreenshotMethod == 'scrcpy'
    assert device.control_backend == device.config.Emulator_ControlMethod == 'MaaTouch'


def verify_reuse(fixture):
    require_success(fixture.run())
    device, key, original = fixture.devices[0], fixture.env['_DEVICE_KEY'], fixture.devices[0].config
    require_success(fixture.run(task='Dorm'))
    assert len(fixture.devices) == 1 and fixture.env['_DEVICE_KEY'] == key
    assert device.config is original, '下一任务完成后必须恢复原共享配置'
    runs = [event for event in fixture.events if isinstance(event, tuple)]
    assert len(runs) == 2 and runs[-1][3] is runs[-1][4] is fixture.configs[-1]
    assert runs[-1][-1] == device.screenshot_backend
    assert not fixture.writes


def verify_incompatible(fixture, *, settings=None, instance='fixture-alpha', transport=None):
    require_success(fixture.run())
    device = fixture.devices[0]
    original, key, events = device.config, copy.deepcopy(fixture.env['_DEVICE_KEY']), list(fixture.events)
    fixture.settings.update(settings or {})
    fixture.env['_DEVICE_ARGS'].update(transport or {})
    result = fixture.run(instance=instance)
    assert result.get('decision') in ('denied', 'error') and not result.get('ran'), result
    assert len(fixture.devices) == 1 and fixture.events == events, '不相容请求不得触发设备动作'
    assert device.config is original and fixture.env['_DEVICE_KEY'] == key


def verify_failure_restore(fixture, mode):
    require_success(fixture.run())
    device = fixture.devices[0]
    original, key = device.config, copy.deepcopy(fixture.env['_DEVICE_KEY'])
    fixture.mode = mode
    result = fixture.run(task='Dorm')
    assert result.get('decision') in ('failed', 'error') and result.get('native_success') is False, result
    assert device.config is original and fixture.env['_DEVICE_KEY'] == key
    assert not fixture.writes
    fixture.mode = 'success'
    require_success(fixture.run(task='Dorm'))
    assert len(fixture.devices) == 1 and device.config is original


def verify_legacy(fixture):
    device = fixture.env['_device_engine']()
    assert fixture.env['_device_engine']() is device
    assert len(fixture.devices) == 1 and [c.config_name for c in fixture.configs] == ['alas']
    assert device.config.Emulator_Serial == TRANSPORT['serial']
    assert device.config.Emulator_ScreenshotMethod == TRANSPORT['screenshot']
    assert device.config.Emulator_ControlMethod == TRANSPORT['control']
    assert {item[1] for item in fixture.writes} == {
        'Emulator_Serial', 'Emulator_ScreenshotMethod', 'Emulator_ControlMethod',
    }, '原无参设备初始化路径的 multi_set 语义应保持不变'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, default=ROOT / 'tools' / 'alas_vision.py')
    args = parser.parse_args()
    cases = [('首次显式实例不构造或写入默认账号', verify_first_instance),
             ('相同实例跨任务复用并恢复配置', verify_reuse),
             ('不同实例在设备动作前拒绝', lambda f: verify_incompatible(f, instance='fixture-beta')),
             ('不同服务器在设备动作前拒绝', lambda f: verify_incompatible(f, settings={'SERVER': 'jp'})),
             ('不同包名在设备动作前拒绝', lambda f: verify_incompatible(f, settings={'Emulator_PackageName': 'changed.package'})),
             ('模拟器初始化信息变化时拒绝', lambda f: verify_incompatible(f, settings={'EmulatorInfo_Path': 'changed-path'})),
             ('串号变化时拒绝', lambda f: verify_incompatible(f, transport={'serial': 'other-device'})),
             ('截图后端变化时拒绝', lambda f: verify_incompatible(f, transport={'screenshot': 'adb'})),
             ('输入后端变化时拒绝', lambda f: verify_incompatible(f, transport={'control': 'ADB'})),
             ('原无参 S3 设备初始化与复用不变', verify_legacy)]
    for mode in ('native_false', 'exception', 'system_exit', 'pre_dispatch_failure'):
        cases.append((f'{mode} 后恢复共享配置且可继续运行', lambda f, mode=mode: verify_failure_restore(f, mode)))
    failures = []
    parent = ROOT / '.runtime' / 'diagnostics'
    parent.mkdir(parents=True, exist_ok=True)
    for name, verify in cases:
        try:
            with tempfile.TemporaryDirectory(prefix='instance-device-', dir=parent) as temporary:
                verify(Fixture(args.host, Path(temporary)))
            print(f'PASS {name}')
        except Exception as error:
            # Only fake values appear in assertions; omit file-system tracebacks.
            failures.append(name)
            print(f'FAIL {name}: {type(error).__name__}: {error}')
    print(f'{len(cases) - len(failures)}/{len(cases)} 离线实例设备绑定检查通过；未使用真实账号或设备。')
    return int(bool(failures))


if __name__ == '__main__':
    raise SystemExit(main())
