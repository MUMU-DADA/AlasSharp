"""Archive and verify real native scheduler boundary-stop evidence; never operates a device."""
from __future__ import annotations

import argparse
from datetime import datetime
import json
from pathlib import Path
import re

from audit_queue_evidence import AuditError, digest, encoded, private_text, read_json, require, sanitize

ROOT = Path(__file__).resolve().parents[2]
ARCHIVE = Path(__file__).parent / 'scheduler-evidence'
DOC = ROOT / 'docs/archive/reports/scheduler-evidence.md'
SCHEMA = 'scheduler-boundary-evidence/1'
REDACTIONS = [
    'Account and diagnostic configurations reduced to collection flags and Reward scheduling timestamps.',
    'Device identifiers and absolute paths removed; original pixels and full logs remain local.',
    'Native logs reduced to ordered page arrivals, task completion, and observed stop events; source hashes retained.',
    'Queue cancellation, native dispatch verdicts and timing remain unchanged; no reward or campaign completion inferred.',
]


def safe_relative(value):
    path = Path(value)
    require(isinstance(value, str) and value and not path.is_absolute() and ':' not in value
            and '\\' not in value and '..' not in path.parts, 'invalid relative source path')
    return path


def validate(proof):
    queue, task = proof['queue'], proof['task']
    evidence, request = task['evidence'], task['input']
    require(queue['outcome'] == 'cancelled' and queue['dry_run'] is False
            and queue['stopped_early'] is True and queue['stop_reason'] == 'cancelled', 'queue must remain cancelled')
    require(queue['host_start_count'] == queue['device_configure_count'] == 1, 'single-session counts differ')
    require(len(queue['tasks']) == 1 and proof['resume_state']['completed'] == {}, 'cancelled task cannot be completed')
    entry = queue['tasks'][0]
    for key in ('id', 'kind', 'required', 'input', 'outcome', 'error_kind', 'error', 'elapsed_s'):
        require(entry[key] == task[key], 'task/queue mismatch: ' + key)
    require(task['kind'] == 'scheduler_run' and task['outcome'] == 'skipped'
            and task['error_kind'] == task['stop_reason'] == 'cancelled'
            and task['unmet_preconditions'] == [], 'scheduler cancellation contract mismatch')
    require(request['allow_actions'] is True and request['confirm'] == request['instance'] == evidence['instance'],
            'scheduler authorization mismatch')
    require(evidence['constructed'] is True and evidence['ran'] is True
            and evidence['decision'] == 'stopped' and evidence['stop_observed'] is True
            and evidence['failed_dispatches'] == 0 and evidence['failure_frames'] == [], 'native stop not proven')
    records = proof['dispatches']
    require(len(records) == evidence['dispatch_count'] > 0, 'dispatch inventory mismatch')
    require([r['sequence'] for r in records] == list(range(1, len(records) + 1)), 'dispatch ordering mismatch')
    for record in records:
        require(record['instance'] == request['instance'] and record['native_success'] is True
                and record['returned'] is True and not record.get('error'), 'native dispatch failed or never returned')
        require(datetime.fromisoformat(record['finished_at']) >= datetime.fromisoformat(record['started_at']),
                'dispatch timestamps reversed')
    # This archive records a bounded, non-collection Reward validation, not arbitrary business effects.
    require(len(records) == 1 and records[0]['method'] == 'reward' and records[0]['scheduler_command'] == 'Reward',
            'unsupported validation task')
    native = proof['scheduler_state']
    require(native['phase'] == 'stopped' and native['instance'] == request['instance']
            and native['stop_observed'] is True and native['error'] is None
            and native['dispatch_count'] == len(records) and native['failed_dispatches'] == 0,
            'scheduler final state mismatch')
    require(proof['session']['starts'] == proof['session']['devices'] == proof['session']['releases'] == 1,
            'session event counts mismatch')
    require(proof['session']['allow_actions'] is True and proof['session']['read_only_device'] is False,
            'session action gate mismatch')
    require(proof['log_cursor'] == proof['native_log_count'] > 0, 'native log cursor mismatch')
    events = proof['events']
    required = ['Page arrive: page_reward', 'Page arrive: page_main', 'Scheduler: End task `Reward`', 'Update event detected']
    indices = []
    for message in required:
        matches = [index for index, event in enumerate(events) if event['message'] == message]
        require(len(matches) == 1, 'missing or duplicate native boundary event: ' + message)
        indices.append(matches[0])
    require(indices == sorted(indices), 'native arrival/return/stop ordering mismatch')
    require(all(a['id'] < b['id'] and datetime.fromisoformat(a['time']) <= datetime.fromisoformat(b['time'])
                for a, b in zip(events, events[1:])), 'native event sequence mismatch')
    flags = proof['collection_flags']
    expected = {'CollectOil', 'CollectCoin', 'CollectExp', 'CollectMission', 'CollectWeeklyMission'}
    require(set(flags['before']) == set(flags['after']) == expected
            and all(value is False for values in flags.values() for value in values.values()), 'resource collection was enabled')
    before, after = proof['reward_next_run']['before'], proof['reward_next_run']['after']
    require(datetime.fromisoformat(after) > datetime.fromisoformat(before), 'Reward was not delayed')
    require(proof['account_config_unchanged'] is True and proof['verification']['exit_code'] == 0
            and proof['verification']['stop_reason'] == 'reward_dispatch_started', 'original config or diagnostic exit mismatch')
    require(re.fullmatch(r'[0-9a-f]{7,40}', proof['verification']['product_base_commit']) is not None,
            'invalid product baseline commit')
    post = proof['post_state']
    require(post['kind'] == 'account_state' and post['outcome'] == 'succeeded'
            and post['input']['capture'] is True and post['evidence']['source'] == 'device_capture'
            and post['evidence']['in_map'] is False and 'page_main' in post['evidence']['pages']
            and post['evidence']['page_errors'] == [] and post['evidence']['frame']['available'] is True,
            'post-stop device capture does not prove homepage')
    require(not private_text(proof), 'projection contains private content')
    return dict(dispatches=len(records), elapsed_s=queue['elapsed_s'], next_run=after,
                product_base_commit=proof['verification']['product_base_commit'])


def project(source, post, root=ROOT):
    hashes = {}

    def raw(path):
        path = path.resolve()
        require(path.is_relative_to(root.resolve()) and path.is_file(), 'source missing or outside project')
        data = path.read_bytes()
        hashes[path.relative_to(root).as_posix()] = digest(data)
        return data

    def load(path):
        return json.loads(raw(path).decode('utf-8-sig'))

    runs = list((source / 'artifacts').glob('*/queue.json'))
    require(len(runs) == 1, 'expected one scheduler run')
    run = runs[0].parent
    queue = load(run / 'queue.json')
    require(len(queue['tasks']) == 1, 'expected one scheduler task')
    task = load(run / ('task-' + queue['tasks'][0]['id'] + '.json'))
    directory = run / safe_relative(task['evidence']['artifacts'])
    dispatches = [load(path) for path in sorted(directory.glob('dispatch-*.json'))]
    lines = [json.loads(line) for line in raw(directory / 'native-log.jsonl').decode('utf-8').splitlines()]
    log_snapshot = load(directory / 'logs.json')
    require(log_snapshot['instance'] == task['input']['instance'], 'log snapshot instance mismatch')
    require([line['id'] for line in lines] == list(range(1, len(lines) + 1)), 'raw native log ids mismatch')
    require(log_snapshot['entries'] == [dict(line, message=line['message'][:12000]) for line in lines[-400:]],
            'native log tail differs from raw entries')
    native = load(directory / 'state.json')
    resume = load(run / 'state.json')
    session_lines = [json.loads(line) for line in raw(run / 'session-log.jsonl').decode('utf-8').splitlines()]
    starts = [line['fields'] for line in session_lines if line['scope'] == 'session' and 'host_start_ms' in line['fields']]
    require(len(starts) == 1, 'missing unique session start')
    session = dict(starts=len(starts), devices=sum('configured' in line['fields'] for line in session_lines),
                   releases=sum(line['message'] == '识图宿主已释放' for line in session_lines),
                   allow_actions=starts[0]['allow_actions'], read_only_device=starts[0]['read_only_device'])
    before = load(source / 'validation-config-before.json')
    after = load(source / 'validation-config-after.json')
    original_unchanged = raw(source / 'account-config-before.json') == raw(source / 'account-config-after.json')
    verification = load(source / 'verification.json')
    require(verification['account_config_unchanged'] is original_unchanged, 'diagnostic account checksum claim differs')
    require(verification['account_config_sha256'] == digest(raw(source / 'account-config-before.json')),
            'diagnostic account checksum mismatch')
    verification = {key: verification[key] for key in ('exit_code', 'stop_reason', 'product_base_commit')}
    require(bool(raw(source / 'stop.request')) and bool(raw(directory / 'stop.request')), 'stop markers missing')
    proof = dict(queue=sanitize(queue), task=sanitize(task), dispatches=dispatches, scheduler_state=native,
                 resume_state=resume, session=session, log_cursor=log_snapshot['cursor'], native_log_count=len(lines),
                 events=[dict(id=line['id'], time=line['time'], message=line['message'].strip()) for line in lines
                         if re.fullmatch(r'Page arrive: page_(?:reward|main)|Scheduler: End task `Reward`|Update event detected',
                                         line['message'].strip())],
                 collection_flags={phase: {key: values['Reward']['Reward'][key] for key in
                                    ('CollectOil', 'CollectCoin', 'CollectExp', 'CollectMission', 'CollectWeeklyMission')}
                                   for phase, values in (('before', before), ('after', after))},
                 reward_next_run={phase: values['Reward']['Scheduler']['NextRun']
                                  for phase, values in (('before', before), ('after', after))},
                 account_config_unchanged=original_unchanged, verification=verification,
                 post_state=sanitize(load(post)))
    validate(proof)
    return proof, hashes


def archive(source, post):
    proof, hashes = project(source, post)
    archive_id = next((source / 'artifacts').iterdir()).name
    target = ARCHIVE / archive_id
    require(not target.exists(), 'archive already exists; originals must not be overwritten')
    data = encoded(proof, 'proof.json')
    metadata = dict(schema=SCHEMA, source=source.relative_to(ROOT).as_posix(), post=post.relative_to(ROOT).as_posix(),
                    redactions=REDACTIONS, source_files_sha256=hashes, proof_sha256=digest(data))
    target.mkdir(parents=True)
    (target / 'proof.json').write_bytes(data)
    (target / 'archive.json').write_bytes(encoded(metadata, 'archive.json'))


def audit(path, root=ROOT):
    meta = read_json(path / 'archive.json')
    require(meta['schema'] == SCHEMA and meta['redactions'] == REDACTIONS, 'archive schema/redactions mismatch')
    require({p.name for p in path.iterdir()} == {'archive.json', 'proof.json'}, 'archive file inventory mismatch')
    data = (path / 'proof.json').read_bytes()
    require(digest(data) == meta['proof_sha256'], 'proof checksum mismatch')
    hashes = meta['source_files_sha256']
    require(bool(hashes), 'source inventory empty')
    existing = 0
    for name, expected in hashes.items():
        source_file = root / safe_relative(name)
        require(re.fullmatch(r'[0-9a-f]{64}', expected) is not None, 'invalid source checksum')
        if source_file.is_file():
            existing += 1
            require(digest(source_file.read_bytes()) == expected, 'original source checksum mismatch')
    require(existing in (0, len(hashes)), 'source evidence partially missing')
    if existing:
        proof, actual = project(root / safe_relative(meta['source']), root / safe_relative(meta['post']), root)
        require(actual == hashes and encoded(proof, 'proof.json') == data, 'source projection mismatch')
    require(not private_text(meta), 'metadata contains private content')
    return dict(validate(json.loads(data)), archive=path.name, originals_rechecked=bool(existing))


def report(facts):
    lines = ['# 原生连续调度边界停止证据', '',
             '由 `tools/diagnostics/audit_scheduler_evidence.py` 从真实工件与脱敏投影生成。', '',
             '核对队列取消、原生成功返回、分派顺序、日志游标、停止事件、配置延后及停止后的实时主页抓帧。',
             '账号与诊断配置只保留验证所需布尔开关和调度时间；原始截图、日志和配置均留在本地忽略目录。',
             '原件与投影均有 SHA-256；本地原件存在时重新计算投影。仅有归档时无法代替真机重跑。', '',
             '| 归档 | 原生分派 | 队列 | 耗时 | 配置与返页 |', '| --- | --- | --- | --- | --- |']
    for fact in facts:
        lines.append(f"| `{fact['archive']}` | Reward {fact['dispatches']} 次正常返回 | cancelled；completed 为空 | "
                     f"{fact['elapsed_s']} 秒 | 下次调度 {fact['next_run']}；原账号未变；主页且不在图 |")
    lines += ['', '本次 Reward 的油、物资、经验、每日与每周奖励领取开关均关闭，验证的是原生导航、调度持久化与边界停止。',
              '它不证明资源领取、全部周期任务完成、持续长时间运行或战役通关；正常停止不能改记为成功。',
              '原生 pending/waiting 是最近一次 get_next 的观测，停止前不强行重算，最终配置的 NextRun 单独核对。', '']
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive-source', type=Path)
    parser.add_argument('--post-state', type=Path)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    if args.archive_source:
        require(args.post_state is not None, 'post-stop capture required')
        archive(args.archive_source.resolve(), args.post_state.resolve())
    facts = [audit(path.parent) for path in sorted(ARCHIVE.glob('*/archive.json'))]
    require(bool(facts), 'scheduler archives missing')
    document = report(facts)
    if args.check:
        require(DOC.read_text(encoding='utf-8') == document, 'scheduler evidence document drift')
    else:
        DOC.write_text(document, encoding='utf-8', newline='\n')
    print(f'OK: {len(facts)} real scheduler boundary-stop archive(s); originals rechecked: '
          f'{sum(f["originals_rechecked"] for f in facts)}')


if __name__ == '__main__':
    try:
        main()
    except (AuditError, OSError, KeyError, ValueError, TypeError) as error:
        print('FAIL: ' + (type(error).__name__ if isinstance(error, OSError) else str(error)))
        raise SystemExit(1)
