# -*- coding: utf-8 -*-
"""R0：把已归档的实机日志与脱敏运行工件重新核对（结果证据链）。

为什么需要它：R0 的阶段门槛要求"至少一条真实成功结算和一条真实撤退证据都能从日志解释"。
文档里写着"通关了"不算证据 —— 要能从原始日志里指出**是哪一次战果点击、
哪一行 CAMPAIGN END、有没有撤退夹在中间**。这个脚本就是干这件事：
不改日志、不猜结论，只把每条记录声称的结果和原始证据对齐，对不上就报矛盾。

做法：
  1. 每个 `[plan ...]` 开头、`[结果 ...]` 结尾算一次出击窗口；
  2. 窗口里抓原始证据：`BATTLE_STATUS_x` 点击、`In stage.`、`CAMPAIGN END`、
     `WITHDRAW CALLED` / `MAP WITHDRAW`、`[错误]`、步骤行、失败帧路径；
  3. 按 sortie-result/1 的规则复核 `cleared` / `campaign_end` 声称；
  4. 撤退事件按**发生在哪**分类：进图前的上一局清理 / 导航期 / 本局过程；
  5. 对结构化工件复用 sortie-result/1，并核对索引、队列任务、战后抓帧、会话日志与归档完整性；
  6. 产出 `data/result_records_audit.json` 与 `docs/archive/reports/result-evidence.md`。

退出码非 0 = 有记录解释不通，或有门槛项缺失。
"""
from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path, PurePosixPath

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DATA = ROOT / 'data'
DOCS = ROOT / 'docs'
sys.path.insert(0, str(ROOT / 'tools'))
from sortie_contract import CONTRACT, evaluate

ARCHIVE = HERE / 'evidence'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

WIN_RANKS = ('S', 'A', 'B')
RANK_CLICK = re.compile(r'@\s*BATTLE_STATUS_([SABCD])\b')
RANK_CLICK_ALT = re.compile(r'BATTLE_STATUS_([SABCD])\b')
PLAN_LINE = re.compile(r'^\[plan\s*\]\s*(\S+)\s+stage=(\S+)\s+tier=(\S+)\s+dry_run=(\S+)')
RESULT_LINE = re.compile(r'^\[结果\s*\]\s*(.*)$')
STEP_LINE = re.compile(r'^\s+step=(\S+)(.*)$')
FRAME_PATH = re.compile(r'failure_frame=(\S+)')


def parse_kv(text: str) -> dict:
    """`elapsed=84.3s stopped_early=False outcome=cleared` → dict（值都是字符串）。"""
    out = {}
    for token in text.split():
        if '=' in token:
            key, _, value = token.partition('=')
            out[key] = value
    return out


def stage_windows(lines):
    """按 `[结果]` 行切窗口：一次出击的证据在结果行**之前**。

    为什么不能用 `[plan]` 当窗口开头：C# 的 stdout 与 Python 日志的 stderr 是两条流，
    落到同一个文件里顺序不可靠 —— 实测 `[plan]` 行经常出现在本次出击的原始日志**之后**。
    可靠的事实只有一个：`[結果]` 是本窗口的最后一行，窗口从上一行 `[結果]` 之后开始。
    章节名取窗口内（或窗口前最近）的 `[plan]` 行。
    """
    windows = []
    cursor = 0
    for index, line in enumerate(lines):
        if not RESULT_LINE.match(line):
            continue
        block = [(i, lines[i]) for i in range(cursor, index + 1)]
        plan = None
        for i in range(index, -1, -1):
            plan = PLAN_LINE.match(lines[i])
            if plan:
                break
        windows.append({
            'start': cursor,
            'end': index,
            'chapter': plan.group(1) if plan else '<未知>',
            'stage': plan.group(2) if plan else '<未知>',
            'tier': plan.group(3) if plan else '<未知>',
            'dry_run': (plan.group(4) == 'True') if plan else False,
            'lines': block,
            'reported': parse_kv(RESULT_LINE.match(line).group(1)),
        })
        cursor = index + 1
    return windows


def withdraw_kind(window_lines, first_index, last_index):
    """撤退事件的性质：看它周围的步骤行/调用栈，而不是靠猜。

    取事件**前后各一段**文本：上游的 `=== WITHDRAW CALLED ===` 先把调用栈打在后面，
    而 `<<< MAP WITHDRAW >>>` 的调用栈在前面 —— 只看单向会把同一次撤退判成两种。
    """
    around = [line for index, line in window_lines
              if first_index - 40 <= index <= last_index + 40]
    context = '\n'.join(around)
    if 'prepare_campaign_navigation' in context and 'inst.withdraw()' in context:
        return 'cleanup_previous_sortie', '进图前清理上一局（prepare_campaign_navigation）'
    if 'campaign_ui.py' in context or 'handle_campaign_ui_additional' in context:
        return 'navigation', '章节导航期间上游自己调了 withdraw()'
    return 'stage', '本局过程中撤退'


def collect_withdraws(lines):
    """把相邻的撤退标记行并成**一次**撤退事件（同一次撤退会打 2~3 行标记）。"""
    markers = [index for index, line in lines
               if 'WITHDRAW CALLED' in line or 'MAP WITHDRAW' in line
               or '@ WITHDRAW' in line or 'POPUP_CONFIRM_WITHDRAW' in line]
    if not markers:
        return []
    groups, current = [], [markers[0]]
    for index in markers[1:]:
        if index - current[-1] <= 30:
            current.append(index)
        else:
            groups.append(current)
            current = [index]
    groups.append(current)

    events = []
    for group in groups:
        kind, why = withdraw_kind(lines, group[0], group[-1])
        events.append({
            'line': group[0],
            'lines': group,
            'kind': kind,
            'why': why,
            'evidence': [dict(lines)[i].strip()[:120] for i in group],
        })
    return events


def audit_window(window):
    """复核一次出击：声称的结果 vs 原始证据。"""
    lines = window['lines']
    reported = dict(window['reported'])
    reported['cleared'] = reported.get('cleared') == 'True'
    reported['campaign_end'] = reported.get('campaign_end') == 'True'

    ranks, in_stage, end_marker = [], [], []
    steps, frames, errors, traceback_lines = [], [], [], []
    for index, line in lines:
        match = RANK_CLICK.search(line) or RANK_CLICK_ALT.search(line)
        if match:
            ranks.append((index, match.group(1)))
        if 'In stage.' in line:
            in_stage.append(index)
        if 'CAMPAIGN END' in line:
            end_marker.append(index)
        step = STEP_LINE.match(line)
        if step:
            steps.append(step.group(1) + step.group(2).strip())
        frame = FRAME_PATH.search(line)
        if frame:
            frames.append(frame.group(1))
        if line.startswith('[错误'):
            errors.append(line.strip()[:200])
        if line.lstrip().startswith('|'):
            traceback_lines.append(line.strip()[:160])

    withdraws = collect_withdraws(lines)

    last_battle = ranks[-1][0] if ranks else None
    last_rank = ranks[-1][1] if ranks else None
    withdraw_after_battle = [w for w in withdraws
                             if last_battle is not None and w['line'] > last_battle]
    cleanup_withdraws = [w for w in withdraws if w['kind'] == 'cleanup_previous_sortie']

    problems = []
    if reported['cleared'] != (reported.get('outcome') == 'cleared'):
        problems.append('cleared 与 outcome 自相矛盾')
    if reported['cleared']:
        if last_rank not in WIN_RANKS:
            problems.append(f'声称通关但没有胜方战果点击（最后战果 {last_rank}）')
        if not in_stage:
            problems.append('声称通关但日志里没有 "In stage." 结算行')
        if not end_marker:
            problems.append('声称通关但没有 CAMPAIGN END 标记')
        if withdraw_after_battle:
            problems.append('最后一场战斗之后还出现过撤退')
    if reported.get('outcome') == 'error' and not errors:
        problems.append('声称报错但窗口里没有 [错误] 行')
    if reported.get('outcome') == 'withdrawn' and not withdraws:
        problems.append('声称撤退但窗口里没有撤退证据')

    explanation = []
    if reported['cleared']:
        explanation.append(f'战果 {last_rank} 已点击、In stage. 与 CAMPAIGN END 都在，'
                           f'最后一次战斗之后没有撤退')
    if cleanup_withdraws:
        explanation.append(f'进图前清理了上一局（{len(cleanup_withdraws)} 次撤退调用）')
    if not explanation:
        explanation.append(errors[0] if errors else '未通关，按原始证据记录')

    return {
        'log': window['log'],
        'chapter': window['chapter'],
        'stage': window['stage'],
        'tier': window['tier'],
        'dry_run': window['dry_run'],
        'line': window['end'] + 1,
        'reported': reported,
        'evidence': {
            'rank_clicks': [rank for _, rank in ranks],
            'last_rank': last_rank,
            'final_battle_line': last_battle,
            'in_stage_lines': in_stage,
            'campaign_end_lines': end_marker,
            'withdraw_events': withdraws,
            'withdraw_after_last_battle': withdraw_after_battle,
            'steps': steps,
            'failure_frames': frames,
            'errors': errors,
            'traceback_tail': traceback_lines[:4],
        },
        'verdict': 'consistent' if not problems else 'contradiction',
        'problems': problems,
        'explanation': '；'.join(explanation),
    }


def audit_log(path: Path):
    text = path.read_text(encoding='utf-8', errors='replace')
    windows = stage_windows(text.splitlines())
    records = []
    for window in windows:
        window['log'] = path.name
        records.append(audit_window(window))
    return records


def artifact_name(value):
    return str(value or '').replace('\\', '/').rsplit('/', 1)[-1]


def audit_queue_chain(directory: Path, sortie_name: str, result: dict, session: list):
    """Corroborate a sortie with queue tasks, when the run has a queue artifact."""
    queue_path = directory / 'queue.json'
    if not queue_path.is_file():
        return {'present': False, 'post_campaign_page_verified': False, 'problems': []}

    queue = json.loads(queue_path.read_text(encoding='utf-8'))
    problems = []
    tasks = queue.get('tasks')
    if not isinstance(tasks, list) or not tasks:
        return {'present': True, 'post_campaign_page_verified': False,
                'problems': ['queue 缺少任务索引']}
    if queue.get('dry_run') is not False or queue.get('host_start_count') != 1 \
            or queue.get('device_configure_count') != 1:
        problems.append('queue 不是单会话真机运行')

    task_files = {}
    names = []
    for row in tasks:
        name = artifact_name(row.get('artifact'))
        names.append(name)
        if not name.startswith('task-') or not name.endswith('.json') or not (directory / name).is_file():
            problems.append(f'queue 任务工件缺失: {name}')
            continue
        task = json.loads((directory / name).read_text(encoding='utf-8'))
        task_files[name] = task
        for key in ('id', 'kind', 'outcome', 'error_kind', 'error', 'elapsed_s'):
            if task.get(key) != row.get(key):
                problems.append(f'queue 与 {name} 的 {key} 不一致')
        if task.get('outcome') == 'succeeded' \
                and (task.get('error_kind') != 'none' or task.get('error') is not None
                     or task.get('stop_reason') is not None):
            problems.append(f'queue 成功任务状态不一致: {name}')
    plan_path = directory / 'plan.json'
    if plan_path.is_file():
        plan = json.loads(plan_path.read_text(encoding='utf-8'))
        planned = plan.get('tasks')
        if (plan.get('generated_by') != 'alashub plan-queue'
                or plan.get('dry_run') != queue.get('dry_run')
                or not isinstance(planned, list) or len(planned) != len(tasks)):
            problems.append('生成计划与执行队列不一致')
        else:
            for row, item in zip(tasks, planned):
                task = task_files.get(artifact_name(row.get('artifact')))
                if not task or any(task.get(key) != item.get(key)
                                   for key in ('id', 'kind', 'input', 'required')):
                    problems.append(f'生成计划与实际任务输入不一致: {row.get("id")}')
    if len(names) != len(set(names)) or set(names) != {p.name for p in directory.glob('task-*.json')}:
        problems.append('queue 任务工件引用不完整或重复')
    ids = [row.get('id') for row in tasks]
    if len(ids) != len(set(ids)):
        problems.append('queue 任务 id 重复')
    if all(row.get('outcome') == 'succeeded' for row in tasks) \
            and (queue.get('outcome') != 'succeeded' or queue.get('stopped_early') is not False):
        problems.append('queue 总结果与全部成功任务不一致')

    def events(scope, field):
        return [(i, entry) for i, entry in enumerate(session)
                if entry.get('scope') == scope and field in (entry.get('fields') or {})]

    starts = events('session', 'host_start_ms')
    devices = events('session', 'configured')
    releases = [(i, entry) for i, entry in enumerate(session)
                if entry.get('scope') == 'session' and entry.get('message') == '识图宿主已释放']
    queue_starts = events('queue', 'kinds')
    queue_ends = events('queue', 'outcome')
    boundaries = events('queue', 'task')
    completions = [(i, entry) for i, entry in enumerate(session)
                   if entry.get('scope') == 'task']
    if not (len(starts) == len(devices) == len(releases) == len(queue_starts)
            == len(queue_ends) == 1 and len(boundaries) == len(completions) == len(tasks)):
        problems.append('queue 会话事件数量与任务数不一致')
    else:
        if not (starts[0][0] < devices[0][0] < queue_starts[0][0]
                < boundaries[0][0] < completions[-1][0] < queue_ends[0][0]
                < releases[0][0]):
            problems.append('queue 会话启动、任务和释放顺序不一致')
        if queue_starts[0][1]['fields'].get('tasks') != len(tasks) \
                or queue_ends[0][1]['fields'].get('tasks') != len(tasks) \
                or queue_ends[0][1]['fields'].get('outcome') != queue.get('outcome'):
            problems.append('queue 会话日志汇总与队列不一致')
        for position, row in enumerate(tasks):
            begin, boundary = boundaries[position]
            end, completion = completions[position]
            fields = completion.get('fields') or {}
            if (boundary['fields'].get('task') != row.get('id')
                    or boundary['fields'].get('kind') != row.get('kind')
                    or fields.get('outcome') != row.get('outcome')
                    or fields.get('error_kind') != row.get('error_kind')
                    or fields.get('error') != row.get('error')
                    or fields.get('elapsed_s') != row.get('elapsed_s')
                    or not (begin < end < (boundaries[position + 1][0]
                                           if position + 1 < len(tasks) else queue_ends[0][0]))):
                problems.append(f'queue 任务事件或顺序不一致: {row.get("id")}')

    campaign_matches = []
    page_verified = False
    seen_indexes = set()
    for position, row in enumerate(tasks):
        task = task_files.get(artifact_name(row.get('artifact')))
        if not task or task.get('kind') != 'campaign_batch':
            continue
        evidence = task.get('evidence') or {}
        index_name = artifact_name(evidence.get('index_artifact'))
        if (not re.fullmatch(r'index(?:-\d+)?\.json', index_name)
                or not (directory / index_name).is_file()):
            problems.append('战役任务未指向本次批次索引')
            continue
        if index_name in seen_indexes:
            problems.append('多个战役任务共用同一批次索引')
        seen_indexes.add(index_name)
        index = json.loads((directory / index_name).read_text(encoding='utf-8'))
        if evidence.get('batch_outcome') != index.get('outcome') \
                or evidence.get('cleared') != index.get('cleared') \
                or evidence.get('stopped_early') != index.get('stopped_early'):
            problems.append('战役任务与批次结论不一致')
        stages = evidence.get('stages')
        indexed = index.get('stages')
        if not isinstance(stages, list) or not isinstance(indexed, list) \
                or len(stages) != len(indexed):
            problems.append('战役任务与批次关卡数量不一致')
            continue
        if (task.get('input') or {}).get('chapters') != [s.get('chapter') for s in indexed]:
            problems.append('战役任务输入章节与批次顺序不一致')
        for stage, batch_stage in zip(stages, indexed):
            for key in ('chapter', 'stage', 'outcome', 'cleared', 'failed', 'skipped',
                        'error_kind', 'error'):
                if stage.get(key) != batch_stage.get(key):
                    problems.append(f'战役任务与批次关卡的 {key} 不一致')
            if artifact_name(stage.get('artifact')) != artifact_name(batch_stage.get('artifact')):
                problems.append('战役任务与批次关卡工件引用不一致')
            if artifact_name(stage.get('artifact')) == sortie_name:
                campaign_matches.append(stage)
                for key in ('chapter', 'stage', 'outcome', 'cleared'):
                    if stage.get(key) != result.get(key):
                        problems.append(f'战役任务与单关结果的 {key} 不一致')
                if stage.get('contract_violations') != (result.get('contract_violations') or []):
                    problems.append('战役任务与单关合同违例不一致')
        if not any(artifact_name(s.get('artifact')) == sortie_name for s in stages):
            continue
        if len(boundaries) == len(completions) == len(tasks):
            stage_events = [(i, event) for i, event in enumerate(session)
                            if event.get('scope') == 'stage'
                            and (event.get('fields') or {}).get('chapter') == result.get('chapter')
                            and (event.get('fields') or {}).get('stage') == result.get('stage')]
            if len(stage_events) != 1 or not (boundaries[position][0] < stage_events[0][0]
                                              < completions[position][0]):
                problems.append('关卡结论未发生在对应战役任务期间')
        if result.get('cleared') is True and (task.get('outcome') != 'succeeded'
                                             or task.get('error_kind') != 'none'):
            problems.append('通关结果与 queue 战役任务状态不一致')
        # A capture immediately after the campaign can corroborate the return page.
        if position + 1 < len(tasks):
            next_task = task_files.get(artifact_name(tasks[position + 1].get('artifact')))
            if next_task and next_task.get('kind') == 'account_state' \
                    and (next_task.get('input') or {}).get('capture') is True:
                frame = (next_task.get('evidence') or {}).get('frame') or {}
                observed = next_task.get('evidence') or {}
                shape = frame.get('shape')
                campaign = observed.get('campaign') or {}
                allowed_pages = {'page_campaign'}
                # Event chapters can return to their dedicated event page.
                # Keep the page rule generic by chapter family, never by a
                # specific event/map identifier.
                if str(result.get('chapter', '')).startswith('campaign.event_'):
                    allowed_pages.add('page_event')
                page_verified = (next_task.get('outcome') == 'succeeded'
                                 and observed.get('source') == 'device_capture'
                                 and frame.get('available') is True
                                 and isinstance(shape, list) and len(shape) == 3
                                 and all(type(size) is int and size > 0 for size in shape)
                                 and observed.get('page_errors') == []
                                 and bool(allowed_pages & set(observed.get('pages') or []))
                                 and observed.get('in_map') is False
                                 and campaign.get('chapter') == result.get('chapter'))
                if not page_verified:
                    problems.append('战后抓帧未证实返回章节页')
    if len(campaign_matches) != 1:
        problems.append('queue 战役任务必须唯一关联本关工件')
    return {'present': True, 'post_campaign_page_verified': page_verified, 'problems': problems}


def audit_artifact(path: Path):
    """复核运行时脱敏归档的工件；结果语义仍由 sortie-result/1 判定。

    index、单关工件与 session-log 必须指向同一次真实运行。旧机器绝对路径仅作为
    来源标识；同目录归档以文件名关联，不要求原机器的运行目录仍存在。
    """
    artifact = json.loads(path.read_text(encoding='utf-8'))
    result = artifact['result']
    matches = []
    for index_path in path.parent.glob('index*.json'):
        candidate = json.loads(index_path.read_text(encoding='utf-8'))
        if any(artifact_name(stage.get('artifact')) == path.name
               for stage in candidate.get('stages', [])):
            matches.append(candidate)
    if len(matches) != 1:
        raise ValueError(f'单关工件必须对应唯一批次索引: {path.name}')
    index = matches[0]
    session = [json.loads(line) for line in
               (path.parent / 'session-log.jsonl').read_text(encoding='utf-8').splitlines()
               if line.strip()]
    verdict = evaluate(result, path.parent)
    problems = list(verdict['details'])
    if result.get('contract') != CONTRACT or index.get('contract') != CONTRACT:
        problems.append('结构化归档缺少匹配的合同版本')
    if result.get('dry_run') is not False or index.get('dry_run') is not False:
        problems.append('结构化归档不是实际运行')
    for key in ('chapter', 'stage', 'cleared'):
        if artifact.get(key) != result.get(key):
            problems.append(f'单关工件包装与结果的 {key} 不一致')
    stages = [s for s in index.get('stages', [])
              if str(s.get('artifact', '')).replace('\\', '/').rsplit('/', 1)[-1] == path.name]
    if len(stages) != 1:
        problems.append('index 必须有且只有一条对应工件记录')
    else:
        for key in ('chapter', 'stage', 'outcome', 'cleared'):
            if stages[0].get(key) != result.get(key):
                problems.append(f'index 与单关结果的 {key} 不一致')
    matching = [entry for entry in session if entry.get('scope') == 'stage'
                and entry.get('fields', {}).get('chapter') == result.get('chapter')]
    if len(matching) != 1:
        problems.append('session-log 必须有且只有一条对应关卡结论')
    else:
        fields = matching[0]['fields']
        for key in ('stage', 'outcome', 'cleared'):
            if fields.get(key) != result.get(key):
                problems.append(f'session-log 与单关结果的 {key} 不一致')
        if fields.get('violations') != len(verdict['violations']):
            problems.append('session-log 合同违例数量与重新裁决不一致')
    configured = any(e.get('scope') == 'session'
                     and e.get('fields', {}).get('serial') for e in session)
    real_session = any(e.get('scope') == 'session'
                       and e.get('fields', {}).get('dry_run') is False for e in session)
    if not configured or not real_session:
        problems.append('session-log 缺少真实会话/设备配置记录')
    queue_chain = audit_queue_chain(path.parent, path.name, result, session)
    problems.extend(queue_chain['problems'])
    evidence = result.get('end_evidence') or {}
    steps = result.get('steps') or []
    stage_withdrawal = (result.get('outcome') == 'withdrawn'
                       and evidence.get('withdrawn') is True
                       and evidence.get('stage_observed') is True
                       and any(s.get('step') == 'withdraw' for s in steps)
                       and any(p.endswith('.withdraw') for p in evidence.get('call_path', [])))
    return {
        'artifact': path.relative_to(ROOT).as_posix() if path.is_relative_to(ROOT) else str(path),
        'chapter': result.get('chapter'), 'stage': result.get('stage'),
        'outcome': verdict['outcome'], 'cleared': verdict['cleared'],
        'contract_violations': verdict['violations'],
        'call_path': evidence.get('call_path', []),
        'stage_observed': evidence.get('stage_observed'),
        'withdrawn': evidence.get('withdrawn'),
        'stage_withdrawal': stage_withdrawal,
        'queue_chain': queue_chain['present'],
        'post_campaign_page_verified': queue_chain['post_campaign_page_verified'],
        'steps': [s.get('step') for s in steps],
        'session_time': matching[0].get('time') if len(matching) == 1 else None,
        'verdict': 'contradiction' if problems else 'consistent', 'problems': problems,
    }


def audit_archive(archive: Path = ARCHIVE, source_root: Path = ROOT):
    records, errors = [], []
    manifests = list(archive.rglob('archive.json'))
    if not manifests:
        errors.append(f'{archive}: 缺少结构化原始工件归档清单')
    for manifest in manifests:
        try:
            metadata = json.loads(manifest.read_text(encoding='utf-8'))
            files = metadata['files']
            source_id = metadata.get('source_run')
            source_path = PurePosixPath(source_id) if isinstance(source_id, str) else PurePosixPath()
            if (not source_id or source_path.is_absolute() or '..' in source_path.parts
                    or ':' in source_id or '\\' in source_id or not isinstance(files, dict)):
                raise ValueError('归档必须有原运行目录和文件校验和')
            if set(metadata.get('source_files_sha256', {})) != set(files):
                raise ValueError('脱敏副本必须逐文件保留原件校验和')
            required = {'index.json', 'session-log.jsonl'}
            if not required <= files.keys() or not any(n.startswith('sortie-') for n in files):
                raise ValueError('归档必须包括 index、会话日志与单关工件')
            for name, checksum in files.items():
                if Path(name).name != name or '/' in name or '\\' in name:
                    raise ValueError('归档文件名不得包含目录')
                if hashlib.sha256((manifest.parent / name).read_bytes()).hexdigest() != checksum:
                    raise ValueError(f'脱敏工件校验和不一致: {name}')
            source = source_root / source_path
            if source.is_dir():
                for name, checksum in metadata['source_files_sha256'].items():
                    if hashlib.sha256((source / name).read_bytes()).hexdigest() != checksum:
                        raise ValueError(f'本机原件校验和不一致: {name}')
            unlisted = {p.name for pattern in ('index*.json', 'sortie-*.json',
                                                'task-*.json', 'queue.json', 'plan.json')
                        for p in manifest.parent.glob(pattern)} - files.keys()
            if unlisted:
                raise ValueError(f'工件未登记在归档清单: {sorted(unlisted)}')
        except (KeyError, TypeError, ValueError, OSError, AttributeError) as error:
            errors.append(f'{manifest}: {type(error).__name__}: {error}')
    for path in sorted(archive.rglob('sortie-*.json')):
        try:
            if not (path.parent / 'archive.json').is_file():
                raise ValueError('单关工件缺少归档清单')
            records.append(audit_artifact(path))
        except (KeyError, TypeError, ValueError, OSError, AttributeError) as error:
            errors.append(f'{path}: {type(error).__name__}: {error}')
    return records, errors


def render_markdown(audit) -> str:
    lines = [
        '# 实机结果证据核对（R0）',
        '',
        '> 本页由 `tools/diagnostics/audit_real_records.py` 从 `data/*.log` 与 `tools/diagnostics/evidence/` 脱敏工件重新核对生成，**不手写**。',
        '> 每条记录都能指回原始日志的行号；对不上就报矛盾，不做解释性兜底。',
        '',
        f"归档日志：{len(audit['logs'])} 份；出击记录：{len(audit['records'])} 条；"
        f"矛盾：{audit['summary']['contradictions']} 条。",
        '',
        '## 逐条记录',
        '',
        '| 日志 | 关 | 声称结果 | 战果 | 结算行 | CAMPAIGN END | 撤退事件 | 裁决 |',
        '| --- | --- | --- | --- | --- | --- | --- | --- |',
    ]
    for record in audit['records']:
        rep, ev = record['reported'], record['evidence']
        events = ', '.join(w['kind'] for w in ev['withdraw_events']) or '—'
        lines.append(
            f"| `{record['log']}`:{record['line']} | {record['stage']} | "
            f"outcome={rep.get('outcome')} cleared={rep['cleared']} | "
            f"{ev['last_rank'] or '—'} | {'✓' if ev['in_stage_lines'] else '—'} | "
            f"{'✓' if ev['campaign_end_lines'] else '—'} | {events} | "
            f"{'一致' if record['verdict'] == 'consistent' else '**矛盾**'} |")
    lines += ['', '## 解释', '']
    for record in audit['records']:
        lines.append(f"- `{record['log']}`:{record['line']}（{record['stage']}）：{record['explanation']}")
        for problem in record['problems']:
            lines.append(f"  - ⚠️ {problem}")
    lines += ['', '## 撤退事件台账', '',
              '| 日志 | 行 | 性质 | 依据 |', '| --- | --- | --- | --- |']
    for event in audit['withdraw_events']:
        lines.append(f"| `{event['log']}` | {event['line'] + 1} | {event['kind']} | {event['why']} |")
    lines += ['', '## 结构化运行工件', '',
              '脱敏归档保留调用链与结果字段，同时检查结果合同、index、队列任务与会话日志。'
              '战后返回章节页只在下一任务实时抓帧、识别页面、非地图状态及章节关联均一致时记为已核验。下表不把撤退当成通关；'
              '它与上面的历史控制台日志分别计数。', '',
              '| 工件 | 会话时间 | 结果 | 本局撤退证据 | 战后章节页 | 裁决 |',
              '| --- | --- | --- | --- | --- | --- |']
    for record in audit['artifact_records']:
        lines.append(f"| `{record['artifact']}` | {record['session_time']} | "
                     f"{record['outcome']}, cleared={record['cleared']} | "
                     f"{'withdraw 步骤 + 上游调用链 + 返回章节页' if record['stage_withdrawal'] else '—'} | "
                     f"{'已核验' if record['post_campaign_page_verified'] else '未核验'} | "
                     f"{'一致' if record['verdict'] == 'consistent' else '**矛盾**'} |")
        for problem in record['problems']:
            lines.append(f'\n- ⚠️ {problem}')
    for error in audit['artifact_errors']:
        lines.append(f'\n- ⚠️ {error}')
    lines += ['', '## 门槛与缺口', '']
    for item in audit['summary']['gate']:
        lines.append(f"- {item}")
    lines += ['', '## 已知缺口', '']
    for item in audit['gaps']:
        lines.append(f"- {item}")
    lines += ['', '## 复现', '', '```powershell',
              'python tools/diagnostics/audit_real_records.py', '```', '']
    return '\n'.join(lines)


def main() -> int:
    logs = sorted(DATA.glob('*.log'))
    records, all_events = [], []
    for path in logs:
        for record in audit_log(path):
            records.append(record)
            for event in record['evidence']['withdraw_events']:
                all_events.append({'log': record['log'], 'stage': record['stage'], **event})

    artifact_records, artifact_errors = audit_archive()
    artifact_contradictions = [r for r in artifact_records if r['verdict'] != 'consistent']
    clears = [r for r in records if r['reported']['cleared']]
    explained_clears = [r for r in clears if r['verdict'] == 'consistent']
    contradictions = [r for r in records if r['verdict'] != 'consistent']
    explained_events = [e for e in all_events if e['kind'] in
                        ('cleanup_previous_sortie', 'navigation', 'stage')]
    stage_level_withdrawn = [r for r in records if r['reported'].get('outcome') == 'withdrawn']
    artifact_withdrawn = [r for r in artifact_records
                         if r['verdict'] == 'consistent' and r['stage_withdrawal']]
    contradiction_count = len(contradictions) + len(artifact_contradictions) + len(artifact_errors)

    gate = []
    gate.append(f"真实成功结算：{len(explained_clears)} 条可解释"
                + ('（通过）' if explained_clears else '（**未通过**）'))
    gate.append(f"真实撤退证据：{len(explained_events)} 起可解释"
                + ('（通过）' if explained_events else '（**未通过**）'))
    gate.append(f"本局撤退结构化真机记录：{len(artifact_withdrawn)} 条可解释"
                + ('（通过）' if artifact_withdrawn else '（待补）'))
    gate.append(f"记录自相矛盾或工件无法核验：{contradiction_count} 条"
                + ('（通过）' if not contradiction_count else '（**未通过**）'))

    gaps = []
    if not stage_level_withdrawn and not artifact_withdrawn:
        gaps.append('归档日志里**没有**以 `outcome=withdrawn` 收尾的一局：'
                    + (f'现有 {len(all_events)} 起撤退都是进图前清理或导航期发生，'
                       '属于"上一局/客户端状态"清理，不是本局结论。'
                       if all_events else '归档里连撤退事件都没有。')
                    + '要补"本局撤退判为 withdrawn"的真机记录，需要一次真实出击后主动撤退；'
                      '那是消耗石油且改变账号状态的动作用户未授权，留给设备在线时补。')
    navigation_swallowed = [e for e in all_events if e['kind'] == 'navigation'
                            and 'swallowed' not in e['why']]
    if navigation_swallowed:
        gaps.append('导航期撤退目前只记在 `ensure_campaign_ui` 的步骤里'
                    '（`navigation_end`/`navigation_withdrawn`），不再被静默吞掉；'
                    '对应回归见 `verify_s3_plan.py`。')

    audit = {
        'generated_by': 'tools/diagnostics/audit_real_records.py',
        'contract': 'sortie-result/1',
        'logs': [p.name for p in logs],
        'records': records,
        'artifact_records': artifact_records,
        'artifact_errors': artifact_errors,
        'withdraw_events': all_events,
        'gaps': gaps,
        'summary': {
            'logs': len(logs),
            'records': len(records),
            'cleared_records': len(clears),
            'cleared_explained': len(explained_clears),
            'error_records': sum(1 for r in records if r['reported'].get('outcome') == 'error'),
            'withdraw_events': len(all_events),
            'withdraw_explained': len(explained_events),
            'stage_level_withdrawn': len(stage_level_withdrawn) + len(artifact_withdrawn),
            'artifact_records': len(artifact_records),
            'contradictions': contradiction_count,
            'gate': gate,
        },
    }

    (DATA / 'result_records_audit.json').write_text(
        json.dumps(audit, ensure_ascii=False, indent=1), encoding='utf-8')
    (DOCS / 'archive/reports/result-evidence.md').write_text(render_markdown(audit), encoding='utf-8')

    print('=== 实机结果证据核对（R0）===')
    for record in records:
        rep, ev = record['reported'], record['evidence']
        print(f"  {'一致' if record['verdict'] == 'consistent' else '矛盾'} "
              f"{record['log']}:{record['line']:<5} {record['stage']:<5} "
              f"outcome={str(rep.get('outcome')):<14} cleared={str(rep['cleared']):<5} "
              f"战果={str(ev['last_rank']):<4} 撤退={len(ev['withdraw_events'])}")
    print()
    for line in gate:
        print('  ' + line)
    for gap in gaps:
        print('  [缺口] ' + gap)
    print()
    print('证据已写入: data/result_records_audit.json')
    print('证据文档: docs/archive/reports/result-evidence.md')
    ok = (bool(explained_clears) and bool(explained_events or artifact_withdrawn)
          and bool(stage_level_withdrawn or artifact_withdrawn)
          and not contradiction_count)
    print()
    print('结果: ' + ('OK（真实通关与真实撤退都能从日志解释）' if ok else 'FAIL'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
