"""Exercise every native scheduled/tool entry with isolated config and inert domain endpoints.

The native config updater/binder, alas.py methods and run() are real. Only the
domain endpoint, device, notification transport and config file location are
substituted. This proves dispatch, not business behavior or game completion.
"""
from __future__ import annotations

import ast
import copy
import hashlib
import importlib
import inspect
import json
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime/engine'
sys.path.insert(0, str(ROOT / 'tools'))
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import alas_vision as av
import alas
import inflection
from module.config.config import AzurLaneConfig, TaskEnd
import module.config.config as config_module
import module.config.config_updater as updater_module
from module.exception import GameNotRunningError
from module.submodule.utils import get_available_func


def endpoint_spec(method):
    """Accept current native entry grammar; unfamiliar upstream changes fail closed."""
    aliases = {}
    expressions = []
    for statement in method.body:
        if isinstance(statement, ast.ImportFrom) and statement.level == 0:
            for alias in statement.names:
                assert alias.name != '*', 'Wildcard dispatch import needs an explicit audit'
                aliases[alias.asname or alias.name] = (statement.module, alias.name)
        elif isinstance(statement, ast.Expr) and isinstance(statement.value, ast.Call):
            expressions.append(statement.value)
        elif isinstance(statement, ast.Expr) and isinstance(statement.value, ast.Constant) \
                and isinstance(statement.value.value, str):
            continue
        else:
            raise AssertionError(f'Unaudited native dispatch shape at {method.name}:{statement.lineno}')
    assert len(expressions) == 1, f'{method.name}: expected one native endpoint'
    expression = expressions[0]
    if isinstance(expression.func, ast.Attribute) and isinstance(expression.func.value, ast.Call):
        constructor = expression.func.value
        assert isinstance(constructor.func, ast.Name) and constructor.func.id in aliases
        return (*aliases[constructor.func.id], constructor, expression.func.attr, expression)
    assert isinstance(expression.func, ast.Name) and expression.func.id in aliases, method.name
    return (*aliases[expression.func.id], None, None, expression)


def evaluate(node, config, device):
    if isinstance(node, ast.Constant):
        return node.value
    if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) and node.value.id == 'self':
        assert node.attr in ('config', 'device'), ast.unparse(node)
        return config if node.attr == 'config' else device
    if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Attribute) \
            and isinstance(node.value.value, ast.Name) and node.value.value.id == 'self' \
            and node.value.attr == 'config':
        return getattr(config, node.attr)
    raise AssertionError(f'Unaudited argument expression: {ast.unparse(node)}')


def arguments(call, config, device):
    assert not any(isinstance(arg, ast.Starred) for arg in call.args)
    assert all(keyword.arg is not None for keyword in call.keywords)
    return ([evaluate(arg, config, device) for arg in call.args],
            {keyword.arg: evaluate(keyword.value, config, device) for keyword in call.keywords})


def check_entry(entry, workspace, native_config_path):
    domain, symbol, constructor, leaf, call = endpoint_spec(entry['node'])
    module = importlib.import_module(domain)
    original = getattr(module, symbol)
    if constructor is not None:
        assert inspect.isclass(original) and callable(getattr(original, leaf, None))
    else:
        assert callable(original) and not inspect.isclass(original)
    mode = {'name': 'normal'}
    prior_config = object()
    events = []
    configs = []
    captures = []
    device_requests = []
    notifications = []
    writes = []
    baseline = native_config_path.read_bytes()

    class Device:
        config = prior_config
        package = 'offline.fixture'

        def screenshot(self):
            captures.append('screenshot')

        def stuck_record_clear(self):
            pass

        def click_record_clear(self):
            pass

    device = Device()

    def bind_and_check(call_node, signature, args, kwargs, config):
        signature.bind(*args, **kwargs)
        want_args, want_kwargs = arguments(call_node, config, device)
        assert list(args) == want_args and kwargs == want_kwargs, (entry['command'], args, kwargs)

    def finish_endpoint(config):
        configs.append(config)
        if mode['name'] == 'task_end':
            raise TaskEnd
        if mode['name'] == 'retry':
            raise GameNotRunningError('synthetic stopped game')
        # Native run() ignores an ordinary False endpoint return; only native
        # exception/dispatch semantics may mark the whole invocation unsuccessful.
        return False if mode['name'] == 'leaf_false' else None

    class Endpoint:
        def __init__(self, *args, **kwargs):
            config = kwargs.get('config', args[0] if args else None)
            assert isinstance(config, AzurLaneConfig)
            assert device.config is config
            bind_and_check(constructor, inspect.signature(original), args, kwargs, config)
            self.config = config
            events.append(('construct', symbol))

        def __getattr__(self, name):
            assert name == leaf, f'Unexpected native endpoint: {name}'

            def invoke(*args, **kwargs):
                signature = inspect.signature(getattr(original, name))
                signature.bind(self, *args, **kwargs)
                want_args, want_kwargs = arguments(call, self.config, device)
                assert list(args) == want_args and kwargs == want_kwargs
                events.append(('call', name))
                return finish_endpoint(self.config)
            return invoke

    def function(*args, **kwargs):
        config = kwargs.get('config', args[0] if args else None)
        assert isinstance(config, AzurLaneConfig)
        bind_and_check(call, inspect.signature(original), args, kwargs, config)
        events.append(('call', symbol))
        return finish_endpoint(config)

    # Keep the loader hook shape when substituting a campaign dispatcher. The
    # leaf run remains inert; executing the real loader here would exceed this
    # test's dispatch boundary (covered by verify_native_campaign_runtime).
    if constructor is not None and hasattr(original, 'load_campaign'):
        def unexpected_load(*args, **kwargs):
            raise AssertionError('Dispatch fixture must not enter a campaign')
        Endpoint.load_campaign = unexpected_load

    def engine(config=None):
        assert isinstance(config, AzurLaneConfig)
        device_requests.append(config)
        return device

    def file_path(name, mod_name='alas'):
        assert name == 'fixture' and mod_name == 'alas', 'Unexpected config access'
        return str(native_config_path)

    original_write = updater_module.write_file

    def write_file(path, data):
        assert Path(path).resolve() == native_config_path.resolve()
        writes.append(copy.deepcopy(data))
        return original_write(path, data)

    scenarios = []
    with patch.object(av, 'FORK', str(workspace)), \
            patch.object(config_module, 'filepath_config', file_path), \
            patch.object(updater_module, 'filepath_config', file_path), \
            patch.object(updater_module, 'write_file', write_file), \
            patch.object(av, '_device_engine', side_effect=engine), \
            patch.object(av, '_DEVICE_ARGS', {'serial': 'offline-fixture'}), \
            patch.object(module, symbol, Endpoint if constructor is not None else function), \
            patch.object(alas, 'handle_notify', side_effect=lambda *a, **k: notifications.append(a)), \
            patch.object(alas.AzurLaneAutoScript, 'save_error_log', side_effect=AssertionError('Unexpected failure')):
        for variant in ('normal', 'leaf_false', 'task_end', 'retry'):
            mode['name'] = variant
            native_config_path.write_bytes(baseline)
            events.clear(); captures.clear(); configs.clear(); writes.clear(); device_requests.clear()
            device.config = prior_config
            supplied = entry['command']
            if entry['kind'] == 'periodic':
                result = av.op_periodic_run(dict(task=supplied, instance='fixture', confirm=supplied,
                                                allow_actions=True))
            else:
                result = av.op_tool_run(dict(task=supplied, instance='fixture', confirm=supplied,
                                           allow_actions=True, device_configured=True))
            assert result.get('decision') == ('failed' if variant == 'retry' else 'ran'), result
            assert result.get('native_success') is (variant != 'retry'), result
            assert result['target']['method'] == entry['method'], result
            assert len(configs) == 1 and events[-1] == ('call', leaf or symbol), events
            assert configs[0].task.command == (supplied if entry['kind'] == 'periodic' else 'Alas')
            assert configs[0].bound and not configs[0].is_template_config
            assert len(captures) == int(entry['kind'] == 'periodic'), captures
            needs_device = entry['kind'] == 'periodic' or any(
                isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name)
                and node.value.id == 'self' and node.attr == 'device'
                for node in ast.walk(entry['node']))
            assert len(device_requests) == int(needs_device), device_requests
            assert device.config is prior_config
            assert not notifications
            if variant == 'retry':
                after = json.loads(native_config_path.read_text(encoding='utf-8'))
                assert after['Restart']['Scheduler']['Enable'] is True
                assert after['Restart']['Scheduler']['NextRun'] != json.loads(baseline)['Restart']['Scheduler']['NextRun']
                assert writes, 'Native retry must schedule Restart via the real config writer'
                assert all(frame in result['error'] for frame in result['traceback_tail'])
            else:
                assert not result.get('failure_frames') and not result.get('traceback_tail'), result
            scenarios.append(variant)
    return dict(kind=entry['kind'], command=entry['command'], method=entry['method'],
                endpoint=f'{domain}.{symbol}' + (f'.{leaf}' if leaf else ''),
                scenarios=scenarios, passed=True)


def write_summary(report):
    records = report['records']
    passed = sum(row['passed'] for row in records)
    lines = [
        '# 原生任务全量分派验证', '',
        '由 `tools/diagnostics/verify_native_dispatch_catalog.py` 生成；不连接设备，不读取账号配置。', '',
        f"原生 `alas.py` SHA-256：`{report['source_sha256']}`。",
        f"原生任务参数 SHA-256：`{report['catalog_sha256']}`。", '',
        f"发现 {report['periodic_count']} 个周期任务和 {report['tool_count']} 个独立工具；"
        f"{passed}/{len(records)} 个入口通过，共 {report['scenario_count']} 个通过场景。", '',
        '每个入口运行正常返回、普通 False 返回、TaskEnd 和 GameNotRunningError 四种场景。',
        '使用真实 ConfigUpdater、AzurLaneConfig、原生 run() 与 alas.py 方法；只替换最终领域类/函数、设备、通知传输及配置文件位置。',
        '对照实际领域签名和入口 AST 核对参数，并验证任务绑定、共享设备恢复、截图次数以及原生 Restart 重试写入。',
        '配置由上游参数默认值生成，所有配置读写限制在独立临时目录；普通 False 领域返回不能被误判为原生调度失败。',
        '新入口自动纳入；未审阅的方法体、参数表达式、重复入口或空目录令检查失败。', '',
        '这不验证领域构造器和内部业务循环，不证明领取、购买、战斗或工具实际效果；工具构造器内的任务绑定也不在此范围。',
        '错误详情仅留在本地忽略目录；此表只保留上游符号、源码哈希及判据结果。', '',
        '| 类型 | 命令 | 原生领域入口 | 通过场景 |',
        '| --- | --- | --- | --- |',
    ]
    for row in records:
        lines.append(f"| {row['kind']} | `{row['command']}` | `{row.get('endpoint', '未解析')}` | "
                     f"{len(row.get('scenarios', []))}/4 {'通过' if row['passed'] else '失败'} |")
    path = ROOT / 'docs/archive/reports/native-dispatch.md'
    path.write_text('\n'.join(lines) + '\n', encoding='utf-8')


def main():
    catalog_source = (ENGINE / 'module/config/argument/args.json').read_bytes()
    catalog = json.loads(catalog_source)
    source = (ENGINE / 'alas.py').read_bytes()
    script = next(node for node in ast.parse(source).body
                  if isinstance(node, ast.ClassDef) and node.name == 'AzurLaneAutoScript')
    methods = {node.name: node for node in script.body if isinstance(node, ast.FunctionDef)}
    entries = []
    for section, groups in catalog.items():
        command = groups.get('Scheduler', {}).get('Command', {}).get('value')
        if command:
            method = inflection.underscore(command)
            assert command == section and method in methods, (section, command)
            entries.append(dict(kind='periodic', command=command, method=method, node=methods[method]))
    periodic_count = len(entries)
    tool_commands = list(get_available_func())
    assert periodic_count > 0 and tool_commands, 'Native entry discovery must not be empty'
    for command in tool_commands:
        method = inflection.underscore(command)
        assert method in methods
        entries.append(dict(kind='tool', command=command, method=method, node=methods[method]))
    assert len({(entry['kind'], entry['command']) for entry in entries}) == len(entries)

    # Read only generated defaults; never open or copy a user's account config.
    base = {section: {group: {key: arg['value'] for key, arg in fields.items()}
                      for group, fields in groups.items()} for section, groups in catalog.items()}
    for groups in base.values():
        if 'Scheduler' in groups:
            groups['Scheduler'].update(Enable=False, NextRun='2020-01-01 00:00:00')
    base['Alas']['Error'].update(SaveError=False, OnePushConfig='')
    base['Alas']['Emulator']['Serial'] = 'offline-fixture'
    output = ROOT / '.runtime/verification/native-dispatch-catalog.json'
    output.parent.mkdir(parents=True, exist_ok=True)
    records = []
    with tempfile.TemporaryDirectory(prefix='dispatch-catalog-', dir=output.parent) as directory:
        workspace = Path(directory)
        (workspace / 'config').mkdir()
        (workspace / 'module/config/argument').mkdir(parents=True)
        (workspace / 'alas.py').write_bytes(source)
        (workspace / 'module/config/argument/args.json').write_text(json.dumps(catalog), encoding='utf-8')
        path = workspace / 'config/fixture.json'
        path.write_text(json.dumps(base), encoding='utf-8')
        original = path.read_bytes()
        for entry in entries:
            path.write_bytes(original)
            try:
                records.append(check_entry(entry, workspace, path))
            except Exception as error:
                records.append(dict(kind=entry['kind'], command=entry['command'], passed=False,
                                    error=f'{type(error).__name__}: {error}'))
    report = dict(schema='native-dispatch-catalog/1', source_sha256=hashlib.sha256(source).hexdigest(),
                  catalog_sha256=hashlib.sha256(catalog_source).hexdigest(),
                  periodic_count=periodic_count, tool_count=len(tool_commands),
                  scenario_count=sum(len(row.get('scenarios', [])) for row in records),
                  scope='Native config/run/methods; inert domain endpoints and device; no business completion',
                  records=records)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2, default=str), encoding='utf-8')
    write_summary(report)
    failures = [row for row in records if not row['passed']]
    for row in failures:
        print(f'FAIL: {row["command"]}: {row["error"]}')
    print(f'{"FAIL" if failures else "PASS"}: {len(entries)} native entries, '
          f'{sum(len(row.get("scenarios", [])) for row in records)} dispatch scenarios; '
          'real config binding/retry writes, no device actions')
    return int(bool(failures))


if __name__ == '__main__':
    raise SystemExit(main())
