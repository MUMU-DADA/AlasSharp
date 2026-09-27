# -*- coding: utf-8 -*-
"""Offline S3 preflight using exact rule identity and upstream chapter config.

Imports the chapter and reads an optional captured frame. Does not initialize a
device or infer chapter support from map dimensions, JSON tier, or missing frames.
"""
import argparse
import copy
import importlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / 'tools'))

import alas_vision as av
from campaign_rules import CampaignRuleError, load_campaign_rules
from module.config.config import AzurLaneConfig
from module.base.utils import node2location


def fixture_for_chapter(chapter):
    """Fixture identity includes the package; different event a1 files differ."""
    with (HERE / 'map_fixtures.json').open(encoding='utf-8') as stream:
        manifest = json.load(stream)
    for filename, info in manifest.items():
        if filename.startswith('_'):
            continue
        source = info.get('chapter', '')
        module = 'campaign.' + source.removesuffix('.json').replace('/', '.')
        if module == chapter:
            fixture = ROOT / 'data' / 'fixtures' / filename
            if fixture.is_file():
                return fixture
    return None


def op(name, **args):
    response = json.loads(av.handle_line(json.dumps({'id': 1, 'op': name, 'args': args})))
    if not response.get('ok'):
        raise RuntimeError(response.get('error'))
    return response['result']


def compare_exported_config(ir, config_class):
    """Compare exported data against Python's real public Config lookup.

    The independent oracle is the imported class, without constructing a
    Campaign, device or account config. Type evidence distinguishes tuple/list
    and nested dictionary key types which ordinary JSON cannot preserve.
    """
    native = {name: getattr(config_class, name) for name in dir(config_class)
              if not name.startswith('_') and not callable(getattr(config_class, name))}

    def typed(value):
        kind = type(value).__name__
        if value is None or type(value) in (bool, int, float, str):
            return {'type': kind, 'value': value}
        if type(value) in (list, tuple, set, frozenset):
            items = [typed(item) for item in value]
            if type(value) in (set, frozenset):
                items.sort(key=lambda item: json.dumps(item, sort_keys=True))
            return {'type': kind, 'items': items}
        if type(value) is dict:
            return {'type': kind, 'items': [
                {'key': typed(key), 'value': typed(item)} for key, item in value.items()]}
        raise TypeError(f'unsupported native Config value type: {kind}')

    differences = []
    meta = ir.get('config_meta') or {}
    exported = ir.get('config') or {}
    if meta.get('present') is not True or meta.get('complete') is not True:
        differences.append('配置导出缺失或不完整')
    missing = sorted(set(native) - set(exported))
    extra = sorted(set(exported) - set(native))
    differences += [f'缺少字段 {name}' for name in missing]
    differences += [f'多余字段 {name}' for name in extra]
    typed_values = meta.get('typed_values') or {}
    if set(typed_values) != set(native):
        differences.append('类型证据字段集合不一致')
    for name, value in native.items():
        if typed_values.get(name) != typed(value):
            differences.append(f'字段值或类型不一致 {name}')
        # Current upstream Config values are JSON-compatible. The typed check
        # above separately retains containers and dictionary key types.
        plain = json.loads(json.dumps(value))
        if name in exported and exported[name] != plain:
            differences.append(f'JSON 字段值不一致 {name}')
    return differences


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('chapter')
    parser.add_argument('--fixture', help='该关卡的已存图内帧；相对路径基于仓库根目录')
    args = parser.parse_args(argv)
    results = []

    def check(name, status, detail):
        results.append((name, status))
        print(f'[{status}] {name}: {detail}', flush=True)

    print(f'S3 离线预检：{args.chapter}')
    try:
        rules = load_campaign_rules(args.chapter)
        with Path(rules['ir_path']).open(encoding='utf-8') as stream:
            ir = json.load(stream)
        check('规则来源', 'PASS', rules['ir_source'])
    except CampaignRuleError as error:
        check('规则来源', 'FAIL', f'{error.code}: {error}')
        return 1

    try:
        module = importlib.import_module(args.chapter)
        shape = tuple(int(value) for value in module.Campaign.MAP.shape)
        expected = node2location(ir['map']['shape'])
        check('上游地图形状', 'PASS' if shape == expected else 'FAIL',
              f'Campaign.MAP={shape} IR={expected}')
        cfg = copy.deepcopy(AzurLaneConfig('template')).merge(module.Config())
        check('继承的上游 Config', 'PASS',
              f'{module.Config.__module__}.{module.Config.__name__}; '
              f'backend={cfg.DETECTION_BACKEND}; '
              f'line_threshold={cfg.INTERNAL_LINES_HOUGHLINES_THRESHOLD}')
        differences = compare_exported_config(ir, module.Config)
        check('导出 Config 与上游', 'FAIL' if differences else 'PASS',
              '; '.join(differences) if differences else
              f'{len(ir["config"])} 个字段及嵌套值类型一致')
        methods = rules['plan_steps']
        missing = [name for name in methods if not callable(getattr(module.Campaign, name, None))]
        check('原生战斗方法', 'FAIL' if missing else 'PASS',
              f'方法={methods}; 缺失={missing}')
        check('JSON 计划元信息', 'INFO',
              f'tier={rules["tier"]}; {rules["ir_plan_status"]}; '
              '运行时由上游调度，JSON 未完整导出不会限制原生方法执行')
    except Exception as error:
        check('上游加载链', 'FAIL', f'{type(error).__name__}: {error}')

    fixture = Path(args.fixture) if args.fixture else fixture_for_chapter(args.chapter)
    if fixture and not fixture.is_absolute():
        fixture = ROOT / fixture
    if fixture is None or not fixture.is_file():
        check('地图夹具', 'UNTESTED', '缺少本关图内帧；未验证，不据此判定不支持')
    else:
        try:
            op('screenshot_load', path=str(fixture))
            detection = op('map_detect', chapter=args.chapter, mode='main')
            reason = str(detection.get('reason') or detection.get('error') or '')
            if detection.get('detected'):
                check('地图夹具', 'PASS',
                      f'{fixture.name}: grids={detection.get("grid_count")}; '
                      f'shape={detection.get("shape")}; ships={detection.get("ships")}')
            elif reason.startswith('MapDetectionError: Camera outside map:'):
                check('地图夹具', 'RECOVERY',
                      f'{reason}；上游 Camera._update_view 已负责移动相机，需实战验证恢复')
            else:
                check('地图夹具', 'FAIL', f'{fixture.name}: {reason or detection}')
        except Exception as error:
            check('地图夹具', 'FAIL', f'{type(error).__name__}: {error}')

    failed = [name for name, status in results if status == 'FAIL']
    if failed:
        print('离线预检失败：' + '、'.join(failed))
        return 1
    unverified = any(status in ('UNTESTED', 'RECOVERY') for _, status in results)
    print('规则和上游加载链通过；' + ('地图实战行为尚待验证。' if unverified
                                    else '已有地图帧通过识别，通关结果仍以实战为准。'))
    return 0


if __name__ == '__main__':
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass
    sys.exit(main())
