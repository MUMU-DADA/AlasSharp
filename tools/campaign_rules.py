"""Read exported campaign metadata without importing a campaign or a device.

The JSON plan is an AST summary. S3 dispatches the original Campaign methods,
including inherited behavior and methods the exporter could not translate.
Consequently, an incomplete JSON plan is diagnostic metadata, not a partial
list of calls that is safe to replay.
"""
from __future__ import annotations

import json
from pathlib import Path
import re


CAMPAIGN_DATA = Path(__file__).resolve().parent.parent / 'data' / 'campaign'


class CampaignRuleError(ValueError):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def load_campaign_rules(chapter: str, data_dir: Path | str = CAMPAIGN_DATA) -> dict:
    """Load exactly ``campaign.<package>.<chapter>`` and check its source.

    No basename search: ``event_x.a1`` and ``event_y.a1`` are different rules.
    Even a correctly named JSON file is rejected when its exported source
    identifies a different module.
    """
    parts = chapter.split('.')
    if len(parts) != 3 or parts[0] != 'campaign' or not all(
            part.isidentifier() for part in parts):
        raise CampaignRuleError('invalid_chapter',
                                f'章节必须是完整的 campaign 模块名: {chapter}')
    path = Path(data_dir).joinpath(*parts[1:]).with_suffix('.json')
    expected_source = '/'.join(parts) + '.py'
    try:
        with path.open(encoding='utf-8') as stream:
            ir = json.load(stream)
    except FileNotFoundError as exc:
        raise CampaignRuleError('ir_not_found', f'找不到该章节 IR: {path}') from exc
    except (OSError, ValueError) as exc:
        raise CampaignRuleError('ir_invalid', f'无法读取 IR {path}: {exc}') from exc
    if not isinstance(ir, dict):
        raise CampaignRuleError('ir_invalid', f'IR 根节点必须是对象: {path}')
    source = str(ir.get('source') or '').replace('\\', '/')
    if source != expected_source:
        raise CampaignRuleError('ir_source_mismatch',
                                f'IR 来源不匹配: 需要 {expected_source}, 实际 {source!r}')
    if ir.get('module') is not None and ir['module'] != chapter:
        raise CampaignRuleError('ir_source_mismatch',
                                f'IR 模块不匹配: 需要 {chapter}, 实际 {ir["module"]!r}')
    campaign = ir.get('campaign')
    if not isinstance(campaign, dict) or not isinstance(campaign.get('battles'), list):
        raise CampaignRuleError('ir_invalid', f'IR 缺少 campaign.battles 数组: {path}')
    battles = campaign['battles']
    if any(not isinstance(b, dict) or not isinstance(b.get('method'), str)
           or not isinstance(b.get('calls', []), list) for b in battles):
        raise CampaignRuleError('ir_invalid', f'IR battle 方法定义无效: {path}')
    battles = [b for b in battles if b['method'].startswith('battle_')]

    def method_order(battle):
        number = re.fullmatch(r'battle_(\d+)', battle['method'])
        return (int(number[1]) if number else float('inf'), battle['method'])

    battles.sort(key=method_order)
    planned = [{'method': b['method'], 'calls': list(b.get('calls') or []),
                'plan_complete': b.get('plan_complete') is True} for b in battles]
    incomplete = [b['method'] for b in planned if not b['plan_complete']]
    complete = bool(planned) and not incomplete and campaign.get('plan_complete') is True
    config = ir.get('config', {})
    config_meta = ir.get('config_meta', {})
    if not isinstance(config, dict) or not isinstance(config_meta, dict):
        raise CampaignRuleError('ir_invalid', f'IR config/config_meta 必须是对象: {path}')
    origins = config_meta.get('origins') or {}
    if not isinstance(origins, dict) or any(not isinstance(value, dict)
                                           for value in origins.values()):
        raise CampaignRuleError('ir_invalid', f'IR config_meta.origins 必须是对象: {path}')
    config_sources = sorted({f'{origin["module"]}.{origin["class"]}'
                             for origin in origins.values()
                             if origin.get('module') and origin.get('class')})
    config_complete = (None if 'present' not in config_meta or 'complete' not in config_meta
                       else config_meta.get('present') is True
                       and config_meta.get('complete') is True)
    folder, name = parts[1:]
    # Display metadata follows CampaignRun.load_campaign. Live navigation uses
    # the actual loader.stage so inherited event navigation remains upstream.
    stage = '-'.join(name.split('_')[1:3]) if folder.startswith('campaign_') else name
    return {
        'chapter': chapter,
        'stage': stage,
        'campaign_folder': folder,
        'ir_path': str(path.resolve()),
        'ir_source': source,
        'tier': campaign.get('tier'),
        'planned_methods': planned,
        # Historical API name: these are available methods, not a replay order.
        'plan_steps': [b['method'] for b in planned],
        'semantic_trace': [c for b in planned for c in b['calls']],
        'ir_plan_complete': complete,
        'ir_plan_status': ('complete' if complete else
                           'incomplete' if planned else 'no_exported_battle_methods'),
        'incomplete_methods': incomplete,
        'native_overrides': list(campaign.get('native_overrides') or []),
        # Export evidence is visible even when the native Campaign remains executable.
        # A legacy IR without metadata is unknown, never implicitly complete.
        'config_present': config_meta.get('present'),
        'config_complete': config_complete,
        'config_count': len(config),
        'config_origins': origins,
        'config_sources': config_sources,
        'runtime_config_source': chapter + '.Config',
        'execution_mode': 'upstream_campaign',
        'runtime_entrypoint': 'Campaign.run',
        'runtime_dispatch': 'execute_a_battle',
        'map_rule_source': chapter + '.Campaign.MAP',
        'json_plan_replayed': False,
    }
