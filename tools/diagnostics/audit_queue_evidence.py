#!/usr/bin/env python3
"""Read real queue artifacts, archive redacted copies, and derive their evidence report.

This never starts a host or touches a device. Only completed successful account_state,
observe, navigate, OS state and planned native execution queues are supported; failed runs remain
local and cannot be made into a success record by supplying a completion label.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[2]
ARCHIVE = Path(__file__).resolve().parent / 'queue-evidence'
DOC = ROOT / 'docs' / 'archive/reports/queue-evidence.md'
SCHEMA = 'queue-evidence/1'
KINDS = {'account_state', 'observe', 'navigate', 'os_state', 'os_action',
         'periodic_plan', 'periodic_preflight', 'periodic_run'}
REDACTIONS = [
    'Absolute project paths replaced with <project>/relative paths; other absolute paths removed.',
    'Device serial and configured endpoint replaced with <device>.',
    'Account configuration snapshot and configuration name removed; no pixels or raw console logs archived.',
    'Results, inputs, page ids, timings, rounds, clicks and timestamps otherwise retained.',
]


class AuditError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise AuditError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def basename(path):
    return str(path).replace('\\', '/').rsplit('/', 1)[-1]


def relative_id(value):
    path = PurePosixPath(value)
    return bool(value) and not path.is_absolute() and ':' not in value and '\\' not in value \
        and '..' not in path.parts and '<' not in value


def private_text(value):
    """Fail closed for leftover local paths, endpoints, email or credential fields."""
    text = json.dumps(value, ensure_ascii=False)
    return bool(re.search(r'[A-Za-z]:[\\/]|/(?:Users|home|root)/|\\\\\\\\|'
                          r'\b(?:\d{1,3}\.){3}\d{1,3}:\d+|'
                          r'[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}|'
                          r'-----BEGIN .*PRIVATE KEY-----|'
                          r'"(?:password|token|api_key|secret|username|email)"\s*:', text, re.I))


def sanitize(value, project=ROOT, key=''):
    if key in {'config', 'config_name'} and value is not None:
        return '<redacted-account-config>'
    if key == 'serial' and value is not None:
        return '<device>'
    if isinstance(value, dict):
        return {k: sanitize(v, project, k) for k, v in value.items()}
    if isinstance(value, list):
        return [sanitize(item, project) for item in value]
    if not isinstance(value, str):
        return value
    text = value.replace('\\', '/')
    prefix = str(project).replace('\\', '/').rstrip('/')
    text = re.sub(re.escape(prefix), '<project>', text, flags=re.I)
    text = re.sub(r'(?i)serial=[^,\s]+', 'serial=<device>', text)
    if re.match(r'^(?:[A-Za-z]:/|//|/)', text):
        return '<absolute-path>/' + basename(text)
    return text


def encoded(value, name):
    if name.endswith('.jsonl'):
        text = ''.join(json.dumps(row, ensure_ascii=False, separators=(',', ':')) + '\n'
                       for row in value)
    else:
        text = json.dumps(value, ensure_ascii=False, indent=2) + '\n'
    return text.encode('utf-8')


def load_file(path):
    if path.suffix == '.jsonl':
        return [json.loads(row) for row in path.read_text(encoding='utf-8-sig').splitlines() if row]
    return read_json(path)


def load_run(path):
    names = ['queue.json', 'state.json', 'session-log.jsonl']
    names += sorted(p.name for p in path.glob('task-*.json'))
    return {name: load_file(path / name) for name in names}


def frame_ok(shape):
    return isinstance(shape, list) and len(shape) == 3 and all(type(v) is int and v > 0 for v in shape)


def task_proof(task, session, plan=None, preflight=None):
    evidence, request, kind = task['evidence'], task['input'], task['kind']
    require(isinstance(evidence, dict) and isinstance(request, dict), 'missing task evidence/input')
    if kind == 'account_state':
        require(request.get('capture') is True and evidence.get('source') == 'device_capture',
                'account_state lacks device capture provenance')
        require(evidence['frame']['available'] is True and frame_ok(evidence['frame']['shape']),
                'account_state lacks an available frame')
        require(evidence.get('page_errors') == [] and isinstance(evidence.get('pages'), list)
                and evidence['pages'] and isinstance(evidence.get('in_map'), bool)
                and isinstance(evidence.get('server'), str) and evidence['server'],
                'account_state page evidence missing or failed')
        return f"实时抓帧；页面 {', '.join(evidence['pages']) or '未识别'}；in_map={evidence['in_map']}"
    if kind == 'os_state':
        frame = evidence.get('frame', {})
        require(request.get('capture') is True and request.get('detect', 'map') == 'map'
                and evidence.get('source') == 'device_capture' and evidence.get('mode') == 'os'
                and evidence.get('detect') == 'map' and frame_ok(frame.get('shape', []))
                and frame.get('error') is None, 'os state lacks device map capture')
        require(evidence.get('detected') is True and evidence.get('in_map') is True
                and evidence.get('backend') in {'perspective', 'homography'}
                and type(evidence.get('grid_count')) is int and evidence['grid_count'] > 0
                and isinstance(evidence.get('center_loca'), list)
                and len(evidence['center_loca']) == 2
                and all(type(v) is int for v in evidence['center_loca'])
                and evidence.get('reason') is None, 'os state does not prove a detected map')
        return f"实时海域抓帧；上游在图；{evidence['grid_count']} 格；{evidence['backend']}"
    if kind == 'observe':
        ticks, capture, page = evidence['ticks'], evidence['capture'], evidence['page_detection']
        require(type(ticks) is int and ticks > 0, 'observe requires positive ticks')
        require(evidence.get('read_only') is True and evidence.get('cancelled') is False,
                'observe not a completed read-only observation')
        require(evidence.get('errors') == 0 and evidence.get('error_details') == []
                and evidence.get('page_errors') == [], 'observe contains errors')
        require(evidence['warmup']['ok'] is True and evidence['warmup']['error'] is None,
                'observe warmup failed')
        require(evidence['requested_seconds'] == request.get('seconds', 20)
                and evidence['tick_seconds'] == request.get('tick_seconds', 0.5)
                and evidence['elapsed_seconds'] >= evidence['requested_seconds'],
                'observe request or duration mismatch')
        require(capture['attempts'] == ticks == capture['succeeded']
                == capture['timing_ms']['count'] == page['attempts'] == page['timing_ms']['count']
                and capture['failed'] == page['failed'] == page['skipped_capture_failed'] == 0,
                'observe capture/page counts mismatch')
        require(frame_ok(capture['shape']) and evidence['pages_tick'] == ticks
                and all(type(n) is int and 0 < n <= ticks for n in evidence['page_hits'].values())
                and all(p in evidence['page_hits'] for p in evidence['pages']),
                'observe frame/page ticks mismatch')
        require(evidence.get('map_mode') == request.get('map'), 'observe map mode mismatch')
        if evidence.get('map_mode') is not None:
            mapping = evidence['map']
            require(mapping['mode'] == evidence['map_mode']
                    and mapping['attempts'] == ticks == mapping['timing_ms']['count']
                    and mapping['errors'] == mapping['skipped_capture_failed'] == 0,
                    'observe map counts mismatch')
            require(type(mapping['detected_hits']) is int
                    and 0 <= mapping['detected_hits'] <= ticks
                    and (mapping['last_grid_count'] is None
                         or type(mapping['last_grid_count']) is int and mapping['last_grid_count'] >= 0),
                    'observe map hits mismatch')
            map_proof = f"；地图 {mapping['mode']} 命中 {mapping['detected_hits']}/{ticks}"
        else:
            map_proof = ''
        return (f"{ticks} tick；抓帧 {capture['succeeded']}/{capture['attempts']}；错误 0；"
                f"{evidence['elapsed_seconds']} 秒{map_proof}")
    if kind == 'periodic_plan':
        plans = evidence['plans']
        require(evidence['missing'] == [] and evidence['count'] == len(plans) > 0,
                'periodic plan missing bindings')
        require(len({item['task'] for item in plans}) == len(plans)
                and all(item['found'] is True and item['scheduler_command']
                        and item['method'] and item['error'] is None
                        and ((not item['method'].startswith('opsi_')
                              and item['calls_run'] is True)
                             or (item['method'].startswith('opsi_')
                                 and item['calls_run'] is False
                                 and any('OSCampaignRun' in source for source in item['imports'])))
                        for item in plans),
                'periodic plan binding incomplete')
        requested = request.get('tasks')
        if requested is not None:
            require(isinstance(requested, list) and requested
                    and set(requested) == {item['task'] for item in plans},
                    'requested periodic tasks differ from plan')
        else:
            requested = [request['task']]
            require(any(item['task'] == requested[0] for item in plans),
                    'requested periodic task absent from plan')
        return f"上游绑定 {len(plans)} 项；请求 {', '.join(requested)} 已找到"
    if kind == 'periodic_preflight':
        name = request['task']
        require(request.get('allow_actions') is True and request.get('confirm') == name
                and evidence['task'] == name and evidence['allow_actions'] is True
                and evidence['confirm_matches'] is True, 'periodic preflight input gates mismatch')
        require(plan is not None and plan['task'] == name, 'periodic preflight lacks preceding plan')
        binding = evidence['plan']
        require(evidence['decision'] == 'allowed' and evidence['executes'] is False
                and isinstance(evidence['reason'], str) and evidence['reason']
                and binding['found'] is True
                and binding['lineno'] == plan['lineno']
                and binding['imports'] == plan['imports']
                and binding['calls_run'] == plan['calls_run'], 'periodic preflight binding mismatch')
        return f"请求 {name} 已放行；executes=false；上游绑定一致"
    if kind in {'periodic_run', 'os_action'}:
        require(session['allow_actions'] is True and session['read_only_device'] is False,
                'periodic action missing session authorization')
        name = request['task']
        require(request.get('allow_actions') is True and request.get('confirm') == name
                and evidence['task'] == name and evidence['allow_actions'] is True
                and evidence['confirm_matches'] is True, 'periodic input gates mismatch')
        require(plan is not None and plan['task'] == name, 'periodic run lacks preceding plan')
        if preflight is not None:
            require(preflight['decision'] == 'allowed' and preflight['executes'] is False
                    and preflight['task'] == name, 'periodic run preflight mismatch')
        target = evidence['target']
        require(target['module'] == 'alas' and target['class'] == 'AzurLaneAutoScript'
                and target['scheduler_command'] == plan['scheduler_command']
                and target['method'] == plan['method'], 'periodic native dispatcher binding mismatch')
        if kind == 'os_action':
            os_plan = evidence.get('os_plan', {})
            require(target['method'].startswith('opsi_')
                    and os_plan.get('task') == name and os_plan.get('found') is True
                    and os_plan.get('scheduler_command') == target['scheduler_command']
                    and os_plan.get('method') == target['method']
                    and os_plan.get('lineno') == plan['lineno']
                    and os_plan.get('error') is None, 'os action plan/native binding mismatch')
        require(evidence['decision'] == 'ran' and evidence['constructed'] is True
                and evidence['ran'] is True and evidence['native_success'] is True
                and evidence['error'] is None and evidence['reason'] is None
                and evidence['traceback_tail'] == [], 'periodic native execution not proven')
        proof = f"上游 {target['class']}.{target['method']}；decision=ran；native_success=true"
        return proof + ('；仅证明原生调度返回' if kind == 'os_action' else '')
    require(kind == 'navigate', 'unsupported task kind')
    require(session['allow_actions'] is True and session['read_only_device'] is False,
            'navigate missing action authorization')
    if 'final_page' in evidence:
        target, count = request['to'], request.get('rounds', 1)
        require(type(count) is int and count > 0 and evidence['target'] == target
                and evidence['rounds_requested'] == count == evidence['rounds_completed']
                == len(evidence['rounds']) and evidence['success'] is True
                and evidence['final_page'] == target, 'native navigation target or rounds mismatch')
        for index, round_ in enumerate(evidence['rounds'], 1):
            require(round_.get('round') == index and round_.get('success') is True,
                    'native navigation round mismatch')
            legs = [('return_to_main', 'page_main')] if index > 1 else []
            legs.append(('to_target', target))
            for leg_name, destination in legs:
                leg = round_.get(leg_name, {})
                require(leg.get('destination') == destination and leg.get('arrived') is True
                        and leg.get('final_page') == destination
                        and leg.get('error') is None and leg.get('error_kind') is None
                        and type(leg.get('elapsed_ms')) in (int, float)
                        and leg['elapsed_ms'] >= 0, 'native navigation arrival mismatch')
        return f"上游原生导航到 {target}；完成 {count}/{count} 轮；最终 {evidence['final_page']}"
    target, count = request['to'], request.get('rounds', 1)
    require(type(count) is int and count > 0 and evidence['target'] == target
            and evidence['rounds_requested'] == count == evidence['rounds_completed']
            == len(evidence['rounds']) and evidence['success'] is True,
            'navigate target or round count mismatch')
    hops = []
    prior_pages = task.get('boundary_state', {}).get('pages') or []
    for index, round_ in enumerate(evidence['rounds'], 1):
        require(round_['round'] == index and round_['success'] is True, 'navigate round mismatch')
        legs = [('return_to_main', 'page_main')] if index > 1 else []
        legs += [('to_target', target)]
        for name, destination in legs:
            leg = round_[name]
            require(leg['target'] == destination and leg['success'] is True
                    and destination in leg['final_pages'] and leg['failure'] is None,
                    'navigate final page mismatch')
            require(len(leg['hops']) <= evidence['max_hops']
                    and (not prior_pages or destination in prior_pages or bool(leg['hops']))
                    and (not leg['hops'] or destination in leg['hops'][-1]['ArrivedPages']),
                    'navigate hops do not prove arrival')
            require(leg['graph_nodes'] == evidence['graph_nodes'] > 0
                    and leg['graph_edges'] == evidence['graph_edges'] > 0,
                    'navigate graph mismatch')
            hops.extend(leg['hops'])
            prior_pages = leg['final_pages']
    require(hops == evidence['hops'] and evidence['max_hops'] == request.get('max_hops', 8),
            'navigate hops mismatch')
    require(evidence['final_pages'] == evidence['rounds'][-1]['to_target']['final_pages']
            and target in evidence['final_pages'], 'navigate final page mismatch')
    return f"目标 {target}；完成 {count}/{count} 轮；{len(hops)} 次跳转；最终 {', '.join(evidence['final_pages'])}"


def validate_run(files):
    """Cross-check independent runtime outputs; return facts only after all agree."""
    queue, state, logs = files['queue.json'], files['state.json'], files['session-log.jsonl']
    entries = queue['tasks']
    require(isinstance(entries, list) and entries, 'queue has no tasks')
    ids = [row['id'] for row in entries]
    require(len(ids) == len(set(ids)), 'duplicate task ids')
    require(queue['dry_run'] is False and queue['outcome'] == 'succeeded'
            and queue['stopped_early'] is False and queue['stop_reason'] is None,
            'not a completed successful real queue')
    require(queue['host_start_count'] == queue['device_configure_count'] == 1,
            'session counts differ from one')
    require(set(state['completed']) == set(ids), 'state completed tasks mismatch')
    starts = [row for row in logs if row['scope'] == 'session' and 'host_start_ms' in row['fields']]
    devices = [row for row in logs if row['scope'] == 'session' and 'configured' in row['fields']]
    releases = [row for row in logs if row['scope'] == 'session' and row['message'] == '识图宿主已释放']
    require(len(starts) == len(devices) == len(releases) == 1, 'session log counts mismatch')
    require(not any(row['level'] in {'ERROR', 'FATAL'} for row in logs), 'session log contains errors')
    require(logs.index(starts[0]) < logs.index(devices[0]) < logs.index(releases[0]),
            'session lifetime order mismatch')
    start, device = starts[0]['fields'], devices[0]['fields']
    require(start['dry_run'] is False and (start['allow_actions'] or start['read_only_device']),
            'session log not a real authorized device session')
    queue_starts = [row['fields'] for row in logs if row['scope'] == 'queue' and 'kinds' in row['fields']]
    queue_ends = [row['fields'] for row in logs if row['scope'] == 'queue' and 'outcome' in row['fields']]
    require(len(queue_starts) == len(queue_ends) == 1, 'queue log count mismatch')
    require(queue_starts[0]['tasks'] == queue_ends[0]['tasks'] == len(entries)
            and queue_starts[0]['kinds'] == ','.join(dict.fromkeys(row['kind'] for row in entries))
            and queue_starts[0]['dry_run'] is False
            and queue_ends[0]['outcome'] == queue['outcome']
            and queue_ends[0]['failed'] == queue_ends[0]['skipped'] == 0
            and queue_ends[0]['elapsed_s'] == queue['elapsed_s'], 'queue log summary mismatch')
    task_logs = [row for row in logs if row['scope'] == 'task']
    boundaries = [row['fields'] for row in logs if row['scope'] == 'queue' and 'task' in row['fields']]
    require(len(task_logs) == len(boundaries) == len(entries), 'task log count mismatch')
    artifacts = [basename(row['fields']['path']) for row in logs if row['scope'] == 'artifacts']
    expected_names = [basename(row['artifact']) for row in entries]
    require(len(expected_names) == len(set(expected_names)) and artifacts == expected_names + ['queue.json'],
            'artifact log mismatch')
    require(set(files) == set(expected_names) | {'queue.json', 'state.json', 'session-log.jsonl'},
            'missing or unindexed task artifact')
    previous, proofs, reference_session, plans, preflights = [], [], None, {}, {}
    for index, row in enumerate(entries):
        task = files[expected_names[index]]
        require(task['kind'] in KINDS, 'unsupported task kind')
        for key in ('id', 'kind', 'outcome', 'error_kind', 'error', 'elapsed_s'):
            require(task[key] == row[key], f'task/index {key} mismatch')
        require(task['outcome'] == 'succeeded' and task['error_kind'] == 'none'
                and task['error'] is None and task['stop_reason'] is None
                and task['unmet_preconditions'] == [], 'task not successful')
        completed = state['completed'][task['id']]
        identity, session = completed['identity'], completed['identity']['session']
        require(completed['outcome'] == task['outcome'], 'state outcome mismatch')
        require(all(identity[key] == task[key] for key in ('kind', 'required', 'input'))
                and identity['preceding_tasks'] == previous, 'state request identity mismatch')
        require(reference_session is None or session == reference_session, 'state sessions differ')
        reference_session = session
        require(all(session[key] == start[key] for key in ('dry_run', 'allow_actions', 'read_only_device'))
                and session['repo_directory'] == start['repo']
                and session['serial'] == device['serial']
                and session['screenshot_backend'] == device['screenshot']
                and session['control_backend'] == device['control'], 'state/session mismatch')
        log = task_logs[index]
        require(log['message'] == f"{task['kind']}:{task['id']} → {task['outcome']}"
                and all(log['fields'][key] == task[key] for key in ('outcome', 'error_kind', 'error', 'elapsed_s'))
                and log['fields']['preconditions'] == 0, 'task/session log mismatch')
        require(boundaries[index]['task'] == task['id'] and boundaries[index]['kind'] == task['kind'],
                'task boundary order mismatch')
        if task['kind'] == 'periodic_plan':
            plans.update({item['task']: item for item in task['evidence']['plans']})
        name = task['input'].get('task')
        proof = task_proof(task, session, plans.get(name), preflights.get(name))
        if task['kind'] == 'periodic_preflight':
            preflights[name] = task['evidence']
        previous.append({key: task[key] for key in ('id', 'kind', 'required', 'input')})
        proofs.append({'id': task['id'], 'kind': task['kind'], 'proof': proof})
    return {'tasks': proofs, 'elapsed_s': queue['elapsed_s'], 'read_only_device': start['read_only_device'],
            'host_start_count': len(starts), 'device_configure_count': len(devices)}


def archive_run(source, archive_root=ARCHIVE):
    source = source.resolve()
    source_id = source.relative_to(ROOT).as_posix()
    require(relative_id(source_id) and source_id.startswith(('data/', 'runs/')),
            'source must be inside local data/ or runs/')
    files = load_run(source)
    validate_run(files)
    output = archive_root / source.name
    require(not output.exists(), 'archive already exists; never overwrite evidence')
    payloads = {name: encoded(sanitize(value), name) for name, value in files.items()}
    sanitized = {name: sanitize(value) for name, value in files.items()}
    require(not private_text(sanitized), 'unredacted private content; archive refused')
    validate_run(sanitized)
    metadata = {'schema': SCHEMA, 'source_run': source_id, 'redactions': REDACTIONS,
                'source_files_sha256': {name: digest((source / name).read_bytes()) for name in files},
                'files': {name: digest(data) for name, data in payloads.items()}}
    output.mkdir(parents=True)
    for name, data in payloads.items():
        (output / name).write_bytes(data)
    (output / 'archive.json').write_bytes(encoded(metadata, 'archive.json'))
    return output


def audit_archive(path, source_root=ROOT):
    meta = read_json(path / 'archive.json')
    require(meta['schema'] == SCHEMA and relative_id(meta['source_run']), 'invalid archive source/schema')
    require(meta['redactions'] == REDACTIONS, 'redaction contract mismatch')
    names = set(meta['files'])
    require(names == set(meta['source_files_sha256']), 'source/sanitized file list mismatch')
    require(names == {p.name for p in path.iterdir() if p.is_file() and p.name != 'archive.json'},
            'archive file inventory mismatch')
    files = {}
    available = 0
    for name in names:
        require(basename(name) == name and name.endswith(('.json', '.jsonl')), 'invalid archive filename')
        raw = (path / name).read_bytes()
        require(digest(raw) == meta['files'][name], 'sanitized checksum mismatch')
        require(re.fullmatch(r'[0-9a-f]{64}', meta['source_files_sha256'][name]), 'invalid source checksum')
        files[name] = load_file(path / name)
        source = source_root / meta['source_run'] / name
        if source.is_file():
            available += 1
            require(digest(source.read_bytes()) == meta['source_files_sha256'][name], 'original checksum mismatch')
            require(encoded(sanitize(load_file(source), source_root), name) == raw, 'source redaction mismatch')
    require(available in (0, len(names)), 'partially missing original source files')
    require(not private_text(files) and not private_text(meta), 'archive contains private content')
    facts = validate_run(files)
    return dict(facts, archive=path.name, source_run=meta['source_run'], original_available=bool(available))


def report(facts):
    lines = ['# 真实队列证据', '',
             '由 `python tools/diagnostics/audit_queue_evidence.py` 从脱敏工件重建；不手写完成结论。', '',
             '核对 queue、逐任务工件、断点身份与顺序、会话日志、宿主/设备次数及各任务事实；',
             '归档保留原件和脱敏件 SHA-256。原件存在时还会验证原件校验和及可重复脱敏。',
             '离线检出只有脱敏件时只能验证归档完整性与交叉一致性，不能代替现场重跑。', '',
             '账号配置、设备标识和本机绝对路径已脱敏，截图与原始控制台日志留在忽略目录。',
             '周期任务的 `native_success=true` 只证明原生调度返回，不证明资源实际到账。',
             '这些记录只覆盖表中实际执行的任务；不能证明未解锁功能、其他周期任务或战役通关。', '',
             '| 归档 | 会话 | 耗时 |', '| --- | --- | --- |']
    for fact in facts:
        mode = '只读设备' if fact['read_only_device'] else '动作授权'
        lines.append(f"| `{fact['archive']}` | {mode}；宿主 {fact['host_start_count']} / 设备配置 {fact['device_configure_count']} | {fact['elapsed_s']} 秒 |")
    lines += ['', '| 归档 / 任务 | 任务域 | 已核对事实 |', '| --- | --- | --- |']
    for fact in facts:
        for task in fact['tasks']:
            lines.append(f"| `{fact['archive']}/{task['id']}` | `{task['kind']}` | {task['proof']} |")
    lines += ['', '来源目录（项目相对路径，原件不入库）：', '']
    lines.extend(f"- `{fact['source_run']}`" for fact in facts)
    return '\n'.join(lines) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive-run', type=Path, help='read local run and create immutable sanitized archive')
    parser.add_argument('--check', action='store_true', help='verify generated documentation without writing')
    args = parser.parse_args(argv)
    try:
        if args.archive_run:
            archive_run(args.archive_run)
        facts = [audit_archive(path.parent) for path in sorted(ARCHIVE.glob('*/archive.json'))]
        require(bool(facts), 'no archived queue evidence')
        document = report(facts)
        if args.check:
            require(DOC.is_file() and DOC.read_text(encoding='utf-8') == document, 'queue evidence document drift')
        else:
            DOC.write_text(document, encoding='utf-8', newline='\n')
        print(f"OK: {len(facts)} real queues / {sum(len(f['tasks']) for f in facts)} tasks; "
              f"original sources rechecked: {sum(f['original_available'] for f in facts)}")
        return 0
    except (AuditError, OSError, ValueError, KeyError, TypeError) as error:
        # OSError can include private absolute paths; retain its category only.
        detail = type(error).__name__ if isinstance(error, OSError) else str(error)
        print(f'FAIL: {detail}')
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
