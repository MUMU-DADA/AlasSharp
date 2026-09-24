"""Run all native tool constructors against an inert session device.

Real config/task binding, ModuleBase/DaemonBase constructors, tool dispatch and
Benchmark's function wrapper are retained. Only final domain run methods and
the physical Device constructor are replaced; no account or device is used.
"""
from __future__ import annotations

import importlib
import inspect
import json
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import alas_vision as av
import alas
import module.base.base as base_module
import module.config.config as config_module
import module.config.config_updater as updater_module
from module.config.config import AzurLaneConfig
from module.device.device import Device
from module.logger import logger
from verify_native_dispatch_catalog import endpoint_spec
import ast
import inflection
from module.submodule.utils import get_available_func


def main():
    catalog = json.loads((Path(av.FORK) / 'module/config/argument/args.json').read_bytes())
    source = (Path(av.FORK) / 'alas.py').read_bytes()
    script = next(node for node in ast.parse(source).body
                  if isinstance(node, ast.ClassDef) and node.name == 'AzurLaneAutoScript')
    methods = {node.name: node for node in script.body if isinstance(node, ast.FunctionDef)}
    defaults = {section: {group: {key: arg['value'] for key, arg in fields.items()}
                         for group, fields in groups.items()} for section, groups in catalog.items()}
    defaults['Alas']['Error'].update(SaveError=False, OnePushConfig='')
    defaults['Alas']['Emulator']['Serial'] = 'unselected-config-serial'
    output = ROOT / '.runtime/verification/tool-device-binding'
    output.mkdir(parents=True, exist_ok=True)
    failures, results = [], []
    with tempfile.TemporaryDirectory(prefix='constructors-', dir=output) as folder:
        workspace = Path(folder)
        (workspace / 'config').mkdir()
        (workspace / 'alas.py').write_bytes(source)
        config_path = workspace / 'config/fixture.json'
        config_path.write_text(json.dumps(defaults), encoding='utf-8')

        def filepath(name, mod_name='alas'):
            assert name == 'fixture' and mod_name == 'alas'
            return str(config_path)

        prior_config = object()
        device = object.__new__(Device)
        device.config = prior_config
        device.stuck_record_clear = lambda: None
        device.click_record_clear = lambda: None
        calls, endpoints, rogue = [], [], []
        state = dict(raise_error=False)

        def session_device(config=None):
            assert isinstance(config, AzurLaneConfig)
            config.override(Emulator_Serial='selected-session-serial')
            calls.append(config)
            return device

        def physical_device(*args, **kwargs):
            rogue.append('physical-device-constructor')
            raise AssertionError('Native tool bypassed the session device')

        def endpoint(self):
            assert self.device is device and self.device.config is self.config
            assert self.config.Emulator_Serial == 'selected-session-serial'
            endpoints.append(self.config.task.command)
            if state['raise_error']:
                raise RuntimeError('synthetic endpoint failure')

        original_alias = base_module.Device
        with patch.object(av, 'FORK', str(workspace)), \
                patch.object(av, '_DEVICE_ARGS', {'serial': 'selected-session-serial'}), \
                patch.object(av, '_device_engine', side_effect=session_device), \
                patch.object(config_module, 'filepath_config', filepath), \
                patch.object(updater_module, 'filepath_config', filepath), \
                patch.object(Device, '__init__', side_effect=physical_device), \
                patch.object(base_module.ModuleBase, 'EARLY_OCR_IMPORT', True), \
                patch.object(alas.AzurLaneAutoScript, 'save_error_log'), patch.object(alas, 'handle_notify'), \
                patch.object(logger, 'handlers', []), patch.object(logger, 'propagate', False):
            for command in get_available_func():
                method = inflection.underscore(command)
                module_name, symbol, constructor, leaf, _ = endpoint_spec(methods[method])
                module = importlib.import_module(module_name)
                if constructor is not None:
                    cls = getattr(module, symbol)
                else:
                    # The current registry has one function wrapper. Inspect it,
                    # rather than replacing it as the dispatch-only test does.
                    wrapper = ast.parse(inspect.getsource(getattr(module, symbol)))
                    targets = [call for call in ast.walk(wrapper) if isinstance(call, ast.Call)
                               and isinstance(call.func, ast.Attribute)
                               and isinstance(call.func.value, ast.Call)
                               and isinstance(call.func.value.func, ast.Name)]
                    assert len(targets) == 1, 'Unaudited native tool factory wrapper'
                    cls = getattr(module, targets[0].func.value.func.id)
                    leaf = targets[0].func.attr
                for variant in ('success', 'error', 'device_unconfigured'):
                    device.config = prior_config
                    # Preserve inherited versus instance-level method identity.
                    device.__dict__.pop('stuck_record_check', None)
                    device.__dict__.pop('click_record_check', None)
                    calls.clear(); endpoints.clear(); rogue.clear()
                    state['raise_error'] = variant == 'error'
                    request = dict(task=command, instance='fixture', confirm=command,
                                   allow_actions=True, device_configured=variant != 'device_unconfigured')
                    with patch.object(cls, leaf, endpoint):
                        result = av.op_tool_run(request)
                    try:
                        assert not rogue, rogue
                        assert result['decision'] == ('ran' if variant == 'success' else 'error'), result
                        assert endpoints == ([] if variant == 'device_unconfigured' else [command]), endpoints
                        assert len(calls) == int(variant != 'device_unconfigured'), calls
                        assert device.config is prior_config
                        assert 'stuck_record_check' not in device.__dict__
                        assert 'click_record_check' not in device.__dict__
                        assert base_module.Device is original_alias
                        results.append(dict(tool=command, variant=variant, passed=True))
                    except AssertionError as error:
                        failures.append(f'{command}/{variant}: {error}')
                        results.append(dict(tool=command, variant=variant, passed=False))
                # A pre-existing custom checker must also survive daemon tools.
                custom = lambda: 'prior-checker'
                device.stuck_record_check = custom
                device.click_record_check = custom
                state['raise_error'] = False
                with patch.object(cls, leaf, endpoint):
                    result = av.op_tool_run(dict(request, device_configured=True))
                try:
                    assert result['decision'] == 'ran', result
                    assert device.stuck_record_check is custom and device.click_record_check is custom
                    assert device.config is prior_config and base_module.Device is original_alias
                    results.append(dict(tool=command, variant='existing-checkers', passed=True))
                except AssertionError as error:
                    failures.append(f'{command}/existing-checkers: {error}')
                    results.append(dict(tool=command, variant='existing-checkers', passed=False))
    (output / 'results.json').write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
    for message in failures:
        print('FAIL: ' + message)
    print(f'{len(results)-len(failures)}/{len(results)} native tool constructor/device checks; no device actions')
    return int(bool(failures))


if __name__ == '__main__':
    raise SystemExit(main())
