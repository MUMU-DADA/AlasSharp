"""Exhaustive offline coverage of native game rules and their exported metadata.

No accounts or devices: chapter initialization uses a template config and a
frame-only device. Native import failures are failures, never successful skips.
The report proves loading and binding, not combat completion or UI reachability.
"""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import redirect_stdout
import copy
import importlib
import hashlib
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))


def normalize(value):
    if isinstance(value, dict):
        return {str(k): normalize(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [normalize(v) for v in value]
    if hasattr(value, 'tolist'):
        return normalize(value.tolist())
    return value


def issue_text(error):
    return f'{type(error).__name__}: {error}'.replace(str(ROOT), '<project>').replace(
        str(ROOT).replace('\\', '/'), '<project>')


def check_campaigns(av):
    import numpy as np
    from module.base.base import ModuleBase
    from module.campaign.run import CampaignRun
    from module.config.config import AzurLaneConfig
    from module.map.map_base import CampaignMap
    from campaign_rules import load_campaign_rules
    from native_map_declarations import NativeMapDeclarations, check_campaign_declarations

    rows = json.loads((ROOT / 'data/campaign_index.json').read_text(encoding='utf-8'))['chapters']
    sources = {str(p.relative_to(av.FORK)).replace('\\', '/')
               for p in (Path(av.FORK) / 'campaign').rglob('*.py')
               if p.name != '__init__.py' and '__pycache__' not in p.parts}
    indexed = {r['source'] for r in rows}
    records = []
    if sources != indexed or len(indexed) != len(rows):
        records.append(dict(module='campaign_index', status='failed',
                            error=f'missing={sorted(sources-indexed)}, extra={sorted(indexed-sources)}'))
    device = SimpleNamespace(image=np.zeros((720, 1280, 3), dtype=np.uint8),
                             screenshot=lambda: None, stuck_record_clear=lambda: None,
                             click_record_clear=lambda: None)
    saved = dict(av._CAMPAIGN)
    template = AzurLaneConfig('template')
    # Compare the same explicit fleet selection on both sides. FLEET_BOSS=0 is
    # an upstream computed property whose value depends on Fleet_Fleet2.
    template.override(Fleet_Fleet1=1, Fleet_Fleet2=0, Submarine_Fleet=0)
    try:
        with NativeMapDeclarations(av.FORK) as map_declarations, patch.object(ModuleBase, 'EARLY_OCR_IMPORT', True), \
                patch.object(av, '_device_engine', return_value=device), \
                patch.object(av, '_map_config', side_effect=lambda *a: copy.deepcopy(template)):
            for row in rows:
                name = row['source'][:-3].replace('/', '.')
                record = dict(module=name)
                records.append(record)
                try:
                    module = importlib.import_module(name)
                except Exception as error:
                    record.update(status='upstream_error', error=issue_text(error))
                    continue
                try:
                    ir = json.loads((ROOT / 'data' / row['json']).read_text(encoding='utf-8'))
                    map_declarations.check(module, ir)
                    check_campaign_declarations(module, ir)
                except Exception as error:
                    record.update(status='failed', error=issue_text(error))
                    continue
                try:
                    cls = getattr(module, 'Campaign', None)
                    if cls is None or not isinstance(getattr(cls, 'MAP', None), CampaignMap):
                        record.update(status='support_module', reason='No native Campaign.MAP')
                        continue
                    expected_loader = CampaignRun(copy.deepcopy(template), device)
                    expected_loader.load_campaign(name.rsplit('.', 1)[1], folder=name.split('.')[1])
                except Exception as error:
                    record.update(status='upstream_error', error=issue_text(error))
                    continue
                try:
                    ir = json.loads((ROOT / 'data' / row['json']).read_text(encoding='utf-8'))
                    config = module.Config()
                    native_values = {k: normalize(getattr(config, k)) for k in dir(config)
                                     if not k.startswith('_') and not callable(getattr(config, k))}
                    if native_values != ir['config']:
                        raise AssertionError('Exported Config differs from native effective Config')
                    metadata = load_campaign_rules(name)
                    if metadata['json_plan_replayed']:
                        raise AssertionError('JSON plan must remain offline metadata')
                    initialized = av.op_s3_campaign_init({'chapter': name})
                    if initialized.get('instantiated') is not True:
                        raise AssertionError(initialized)
                    actual = av._CAMPAIGN['obj']
                    expected = expected_loader.campaign
                    if type(actual) is not type(expected) or actual.MAP is not expected.MAP:
                        raise AssertionError('Native class/MAP identity changed')
                    fields = [k for k in native_values if hasattr(expected.config, k)]
                    for field in fields:
                        if normalize(getattr(actual.config, field)) != normalize(getattr(expected.config, field)):
                            raise AssertionError(f'Native Config merge differs: {field}')
                    methods = [k for k in dir(cls) if callable(getattr(cls, k))
                               and not k.startswith('__')]
                    for method in methods:
                        a, b = getattr(actual, method), getattr(expected, method)
                        if getattr(a, '__func__', a) is not getattr(b, '__func__', b):
                            raise AssertionError(f'Native method replaced: {method}')
                    record.update(status='passed', config_fields=len(fields),
                                  native_methods=len(methods), tier=row['tier'])
                except Exception as error:
                    record.update(status='failed', error=issue_text(error))
    finally:
        av._CAMPAIGN.clear()
        av._CAMPAIGN.update(saved)
    return records


def check_assets(av):
    catalog = json.loads((ROOT / 'data/assets.json').read_text(encoding='utf-8'))
    records = []
    server = av.server_module.server
    try:
        for current in av.server_module.VALID_SERVER:
            av.op_set_server({'server': current})
            for asset_id, binding in catalog['assets'].items():
                row = dict(asset=asset_id, server=current)
                records.append(row)
                try:
                    obj = av._resolve(asset_id)
                    if type(obj).__name__ != binding['kind']:
                        raise AssertionError('Native asset kind differs')
                    for field in ('area', 'color', 'button', 'file'):
                        exported = binding.get(field)
                        if exported is not None:
                            raw = getattr(obj, 'raw_' + field)
                            native = obj.parse_property(raw)
                            if normalize(native) != exported[current]:
                                raise AssertionError(f'Native {field} differs')
                    info = av.op_asset_info({'asset': asset_id})
                    if info.get('image_error') or info.get('image_count', 0) < 1:
                        raise AssertionError(info.get('image_error', 'Empty native template'))
                    row['status'] = 'passed'
                    obj.resource_release()
                except Exception as error:
                    row.update(status='failed', error=issue_text(error))
    finally:
        av.op_set_server({'server': server})
    return records


def check_pages(av):
    records = []
    server = av.server_module.server
    try:
        for current in av.server_module.VALID_SERVER:
            av.op_set_server({'server': current})
            report = av.op_page_positive_control({})
            records.extend(dict(server=current, **row) for row in report['results'])
    finally:
        av.op_set_server({'server': server})
    return records


def check_tasks(av):
    import ast
    import inflection
    from alas import AzurLaneAutoScript

    source = Path(av.FORK) / 'module/config/argument/args.json'
    catalog = json.loads(source.read_text(encoding='utf-8'))
    records = []
    for section, groups in catalog.items():
        command = groups.get('Scheduler', {}).get('Command', {}).get('value')
        row = dict(section=section, command=command)
        records.append(row)
        if not command:
            row.update(status='configuration_group', reason='No Scheduler.Command')
            continue
        try:
            method = inflection.underscore(command)
            if not callable(getattr(AzurLaneAutoScript, method, None)):
                raise RuntimeError(f'Native scheduler has no {method} method')
            planned = av.op_periodic_plan({'task': command})
            if not planned.get('found') or planned.get('method') != method:
                raise AssertionError(planned.get('error', 'Scheduler binding differs'))
            # Import the dependencies declared by the native command, without
            # constructing task/device objects or invoking any game action.
            imports = []
            for statement in planned['imports']:
                node = ast.parse(statement).body[0]
                module = importlib.import_module(node.module)
                for alias in node.names:
                    if alias.name != '*' and not hasattr(module, alias.name):
                        raise ImportError(f'{node.module}.{alias.name} is missing')
                imports.append(node.module)
            row.update(status='passed', method=method, imports=imports)
        except Exception as error:
            row.update(status='failed', error=issue_text(error))
    return records


def check_controls(av):
    """Native controls can select server rules at import, so isolate each server."""
    records = []
    for server in av.server_module.VALID_SERVER:
        output = ROOT / '.runtime/verification' / f'upstream-controls-{server}.json'
        output.parent.mkdir(parents=True, exist_ok=True)
        # Never read a previous successful worker result after a failed launch.
        output.unlink(missing_ok=True)
        try:
            result = subprocess.run([sys.executable, str(Path(__file__).resolve()),
                                     '--control-server', server, '--output', str(output)],
                                    capture_output=True, text=True, encoding='utf-8',
                                    errors='replace', timeout=180)
            output.with_suffix('.process.log').write_text(result.stdout + '\n' + result.stderr, encoding='utf-8')
            if not output.is_file():
                raise RuntimeError(f'Control worker exited {result.returncode} without a report')
            rows = json.loads(output.read_text(encoding='utf-8'))
            if not isinstance(rows, list) or not rows:
                raise RuntimeError('Empty or invalid control worker report')
            if result.returncode and not any(row['status'] == 'failed' for row in rows):
                raise RuntimeError(f'Control worker exited {result.returncode}')
            records.extend(dict(row, server=server) for row in rows)
        except Exception as error:
            records.append(dict(server=server, status='failed', error=issue_text(error)))
    return records


def check_controls_current_server(av):
    import numpy as np
    from module.base.base import ModuleBase
    from verify_native_control_factories import check_factories

    saved_image = av._state['image']
    av._state['image'] = np.zeros((720, 1280, 3), dtype=np.uint8)
    records = []
    try:
        inventory = av.op_ui_rule_list({})
        for error in inventory['errors']:
            records.append(dict(status='failed', error=error))
        for declaration in inventory['declarations']:
            if declaration['scope'] == 'factory':
                continue
            row = dict(declaration)
            records.append(row)
            try:
                if declaration['scope'] == 'module':
                    checked = av.op_ui_rule_check(declaration)
                    if checked['errors']:
                        raise AssertionError(checked['errors'])
                    row.update(status='passed', methods=sorted(checked['results']))
                elif declaration['scope'] == 'property':
                    with patch.object(ModuleBase, 'EARLY_OCR_IMPORT', True):
                        checked = av.op_cached_rule_check(dict(module=declaration['module'],
                                                              **{'class': declaration['owner']},
                                                              attr=declaration['attr']))
                    if checked['errors']:
                        raise AssertionError(checked['errors'])
                    if not checked['controls']:
                        raise AssertionError('Lazy declaration contains no native control')
                    row.update(status='passed', native_kind=checked['class'],
                               controls=checked['controls'])
            except Exception as error:
                row.update(status='failed', error=issue_text(error))
        records.extend(check_factories(av, inventory['declarations'], servers=[av.server_module.server]))
    finally:
        av._state['image'] = saved_image
    return records


def check_navigation(av):
    """Run native UI.ui_ensure/ui_goto for every reachable graph pair.

    Recognition/device effects are synthetic. Shared buttons can lead to several
    event pages; the fixture assumes the requested native parent is available.
    Thus this verifies dispatch and graph semantics, not live page availability.
    """
    from module.ui.page import Page
    from module.ui.ui import UI

    records = []
    try:
        for destination in Page.all_pages.values():
            if destination.check_button is None:
                continue
            Page.init_connection(destination)
            starts = [page for page in Page.all_pages.values() if page.check_button is not None
                      and (page == destination or page.parent is not None)]
            for source in starts:
                row = dict(source=source.name, destination=destination.name)
                records.append(row)
                current, hops, screenshots = [source], [], [0]
                clears, settles = {'stuck': 0, 'click': 0}, []

                def screenshot():
                    screenshots[0] += 1
                    if screenshots[0] > len(Page.all_pages) * 2:
                        raise AssertionError('Native navigation exceeded synthetic graph bound')

                def click(button):
                    parent = current[0].parent
                    if parent is None or current[0].links.get(parent) is not button:
                        raise AssertionError('Click does not use the native parent-link object')
                    hops.append(dict(source=current[0].name, destination=parent.name))
                    current[0] = parent

                device = SimpleNamespace(config=SimpleNamespace(SERVER=av.server_module.server),
                                         screenshot=screenshot, click=click, has_cached_image=True,
                                         stuck_record_clear=lambda: clears.__setitem__('stuck', clears['stuck'] + 1),
                                         click_record_clear=lambda: clears.__setitem__('click', clears['click'] + 1))

                def initialize(ui, config, injected_device):
                    ui.config, ui.device = config, injected_device
                    ui.interval_timer = {}
                    ui.appear = lambda button, **kwargs: button is current[0].check_button

                try:
                    with patch.object(UI, '__init__', initialize), \
                            patch.object(av, '_device_engine', return_value=device), \
                            patch.object(av.time, 'sleep', lambda seconds: settles.append(seconds)):
                        result = av.op_ui_ensure(dict(destination=destination.name, allow_actions=True))
                    if not result['arrived'] or result.get('error'):
                        raise AssertionError(result)
                    if clears != {'stuck': 1, 'click': 1} or device.click is not click or settles != [1.0] * len(hops):
                        raise AssertionError('Native task-boundary guards or click stabilization diverged')
                    if any(page.parent is not None for page in Page.all_pages.values()) and hops:
                        raise AssertionError('Native connection state leaked after navigation')
                    row.update(status='passed', hops=len(hops))
                except Exception as error:
                    row.update(status='failed', error=issue_text(error))
    finally:
        Page.clear_connection()
    return records


def provenance(repo):
    root = Path(repo)
    sources = sorted(set(root.glob('*.py')) | set((root / 'campaign').rglob('*.py'))
                     | set((root / 'module').rglob('*.py')))
    hashes = {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
              for p in sources if '__pycache__' not in p.parts}
    # An extracted runtime lives under the adapter repository. Git would walk up
    # and report the adapter HEAD unless the runtime owns its Git metadata.
    commit = subprocess.run(['git', '-C', str(root), 'rev-parse', 'HEAD'],
                            capture_output=True, text=True, timeout=30) if (root / '.git').exists() else None
    return dict(upstream_commit=commit.stdout.strip() if commit and commit.returncode == 0 else None,
                source_files=len(hashes), source_tree_sha256=hashlib.sha256(
                    json.dumps(hashes, sort_keys=True).encode()).hexdigest(),
                verifier_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())


def write_summary(path, report):
    labels = dict(campaigns='关卡原生加载/Config/MAP 声明/继承方法', assets='素材原生加载及四服字段',
                  pages='页面四服合成正对照', navigation='原生导航可达图对',
                  controls='四服控件声明、识别及工厂合成循环', tasks='任务调度绑定及依赖导入')
    source = report['provenance']
    factory_rows = [row for row in report['sections'].get('controls', {}).get('records', [])
                    if row.get('scope') == 'factory']
    factory_count = len({(row['module'], row['attr'], row['line']) for row in factory_rows})
    factory_cases = sum(row.get('synthetic_cases', 0) for row in factory_rows)
    lines = ['# 上游自动化规则全量离线覆盖', '',
             '由 `tools/diagnostics/verify_upstream_coverage.py` 生成；无设备动作、无账号配置。',
             '生产流程继续由原生 `CampaignRun.load_campaign()`、`Campaign.run()`、`UI.ui_ensure()` 执行。',
             '本报告证明当前源的加载、绑定及合成输入语义，不证明所有关卡通关或所有页面实机可达。', '',
             f'- 上游提交：{source["upstream_commit"] or "运行时为无 Git 元数据的源快照，以实际内容哈希识别"}。',
             f'- 实际源文件：{source["source_files"]}；路径/内容哈希清单总摘要：`{source["source_tree_sha256"]}`。',
             f'- 验证器 SHA-256：`{source["verifier_sha256"]}`。',
             f'- 全部检查段执行：{report["exhaustive"]}；总体通过：{report["ok"]}。', '',
             '| 范围 | 当前结果 |', '| --- | --- |']
    for name, section in report['sections'].items():
        lines.append(f'| {labels[name]} | ' + '；'.join(
            f'{key}={value}' for key, value in section['counts'].items()) + ' |')
    lines += ['', '## 阻塞与失败', '']
    failures = []
    for name, section in report['sections'].items():
        for row in section['records']:
            if row.get('status', row.get('verdict')) in ('failed', 'upstream_error', 'fail', 'error'):
                identity = row.get('module') or row.get('asset') or row.get('page') or row.get('section') or name
                failures.append(f'- `{identity}`：{row.get("error", row.get("detail", "失败"))}')
    lines.extend(failures or ['无。'])
    lines += ['', '## 证据边界', '',
              '- 辅助模块通过导入后的 `Campaign.MAP` 类型识别，不按文件名排除；源导入失败会令检查退出码为 1。',
              '- MAP 静态声明与原生导入时的赋值和方法参数逐项对拍，覆盖格子/类引用、复制、声明顺序及原生调用；不执行 JSON 规则。',
              '- Campaign 自身数据声明与原生类字典逐项对拍，包含自定义格子类、符号格子、私有状态与方法别名；继承行为仍由原生类调度。',
              '- 页面正对照使用模板画布；`Page(None)` 无可识别素材，明确跳过。',
              '- 导航运行原生页面图和控制循环，识别与点击反馈为合成状态；共享活动入口假定目标活动可用。',
              '- 控件按服务器在独立进程导入，保留导入时的服务器分支；普通实例和延迟属性调用原生识别，包含容器内控件。',
              f'- 任务内工厂检查包含 {factory_count} 个声明、{factory_cases} 个四服合成场景；运行原生任务/控件循环，覆盖切换、原生附加处理、滚动到顶/分页到底及无滚动条，不证明真实点击或业务完成。',
              '- 调度检查覆盖 Scheduler.Command 方法及其声明依赖；原生任务返回不等于领取、购买或目标完成。',
              '- 错误中项目绝对路径替换为 `<project>`；不发布账号、帧或原始日志，不改变错误类型或判据。', '',
              '复现：`python tools/diagnostics/verify_upstream_coverage.py`。',
              '原始逐项报告在忽略目录 `.runtime/verification/upstream-coverage.json`，不进入提交。', '']
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text('\n'.join(lines), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sections', default='campaigns,assets,pages,navigation,controls,tasks')
    parser.add_argument('--control-server', choices=('cn', 'en', 'jp', 'tw'), help=argparse.SUPPRESS)
    parser.add_argument('--output', type=Path, default=ROOT / '.runtime/verification/upstream-coverage.json')
    parser.add_argument('--summary', type=Path, default=ROOT / 'docs/archive/reports/upstream-coverage.md')
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.summary = args.summary.resolve()
    if args.control_server:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.with_suffix('.log').open('w', encoding='utf-8') as log, redirect_stdout(log):
            import alas_vision as av
            av.op_set_server({'server': args.control_server})
            from module.logger import logger
            logger.setLevel(50)
            try:
                rows = check_controls_current_server(av)
            except Exception as error:
                rows = [dict(status='failed', error=issue_text(error))]
        args.output.write_text(json.dumps(rows, ensure_ascii=False, indent=2, default=av.json_default) + '\n',
                               encoding='utf-8')
        return int(not rows or any(row['status'] == 'failed' for row in rows))
    sections = args.sections.split(',')
    checks = dict(campaigns=check_campaigns, assets=check_assets, pages=check_pages,
                  navigation=check_navigation, controls=check_controls, tasks=check_tasks)
    if not sections or any(section not in checks for section in sections):
        parser.error('unknown section')
    # Upstream logging can include local paths; keep it in the ignored raw log.
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = dict(scope='offline native loading/binding; no device or completion claim',
                  exhaustive=set(sections) == set(checks), sections={})
    for section in sections:
        with args.output.with_suffix('.log').open('a', encoding='utf-8') as log, redirect_stdout(log):
            import alas_vision as av
            from module.logger import logger
            logger.setLevel(50)
            try:
                records = checks[section](av)
                if not records:
                    records = [dict(status='failed', error='Empty coverage section')]
            except Exception as error:
                records = [dict(status='failed', error=issue_text(error))]
        counts = Counter(row.get('status', row.get('verdict')) for row in records)
        report['sections'][section] = dict(counts=dict(counts), records=records)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(section, dict(counts), flush=True)
    report['ok'] = not any(row.get('status', row.get('verdict')) in ('failed', 'upstream_error', 'fail', 'error')
                           for section in report['sections'].values() for row in section['records'])
    report['provenance'] = provenance(av.FORK)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    write_summary(args.summary, report)
    return 0 if report['ok'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
