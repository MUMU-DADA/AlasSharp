"""结构化真机证据链回归：篡改/缺文件/字段分歧必须被独立核对发现。"""
from __future__ import annotations

import json
import hashlib
import shutil
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

from audit_real_records import ARCHIVE, audit_archive, audit_artifact

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def main():
    sources = sorted(ARCHIVE.rglob('sortie-*.json'))
    if not sources:
        print('FAIL: 没有可核验的原始工件')
        return 1
    archived, errors = audit_archive()
    checks = [('归档完整且结果一致', not errors and all(
        r['verdict'] == 'consistent' for r in archived)),
        ('本局撤退有调用链/步骤/章节页证据', any(
            r['stage_withdrawal'] and not r['cleared'] for r in archived)),
        ('战役队列有战后实时抓帧返页证据', any(
            r['cleared'] and r['queue_chain'] and r['post_campaign_page_verified']
            for r in archived))]
    events = [p for p in sources if json.loads(p.read_text(encoding='utf-8'))
              ['result']['chapter'].startswith('campaign.event_')]
    checks.append(('两个活动章节真实抓帧返回活动页', len(events) >= 2 and all(
        (record := audit_artifact(path))['verdict'] == 'consistent'
        and record['cleared'] and record['queue_chain']
        and record['post_campaign_page_verified'] for path in events)))
    event = events[0]
    source = next(p for p in sources if audit_artifact(p)['stage_withdrawal'])
    cases = (
        ('结果合同拒绝撤退伪装通关', source.name,
         lambda d: d['result'].update(outcome='cleared', cleared=True)),
        ('包装与结果不一致', source.name, lambda d: d.update(cleared=True)),
        ('批次与结果不一致', 'index.json', lambda d: d['stages'][0].update(outcome='cleared')),
        ('会话与结果不一致', 'session-log.jsonl',
         lambda d: next(e for e in d if e.get('scope') == 'stage')['fields'].update(outcome='cleared')),
        ('dry-run 不能冒充真机', source.name, lambda d: d['result'].update(dry_run=True)),
    )
    with TemporaryDirectory(prefix='alas-evidence-') as tmp:
        for i, (name, filename, mutate) in enumerate(cases):
            target = Path(tmp) / str(i)
            shutil.copytree(source.parent, target)
            path = target / filename
            is_log = path.suffix == '.jsonl'
            raw = path.read_text(encoding='utf-8')
            value = [json.loads(s) for s in raw.splitlines()] if is_log else json.loads(raw)
            mutate(value)
            path.write_text('\n'.join(json.dumps(e) for e in value) if is_log
                            else json.dumps(value), encoding='utf-8')
            checks.append((name, audit_artifact(target / source.name)['verdict'] == 'contradiction'))
            checks.append((name + '：原始文件校验和发现修改', bool(audit_archive(target)[1])))
        target = Path(tmp) / 'missing'
        shutil.copytree(source.parent, target)
        (target / 'session-log.jsonl').unlink()
        checks.append(('缺会话日志不得静默跳过', bool(audit_archive(target)[1])))
        checks.append(('归档整体缺失不得静默跳过', bool(audit_archive(Path(tmp) / 'absent')[1])))

        queued = next(p for p in sources if audit_artifact(p)['queue_chain'])
        queue_cases = (
            ('队列总结果与战役不一致', 'queue.json',
             lambda d: d.update(outcome='failed')),
            ('队列任务结果与工件不一致', 'queue.json',
             lambda d: d['tasks'][0].update(outcome='failed')),
            ('战役任务与单关结论不一致', 'task-campaign-smoke.json',
             lambda d: d['evidence']['stages'][0].update(cleared=False)),
            ('战后识别页不是章节页', 'task-after-campaign.json',
             lambda d: d['evidence'].update(pages=['page_main'])),
            ('战后抓帧仍在地图内', 'task-after-campaign.json',
             lambda d: d['evidence'].update(in_map=True)),
        )
        for i, (name, filename, mutate) in enumerate(queue_cases):
            target = Path(tmp) / f'queue-{i}'
            shutil.copytree(queued.parent, target)
            path = target / filename
            value = json.loads(path.read_text(encoding='utf-8'))
            mutate(value)
            path.write_text(json.dumps(value), encoding='utf-8')
            checks.append((name, audit_artifact(target / queued.name)['verdict'] == 'contradiction'))
            checks.append((name + '：归档校验和发现修改', bool(audit_archive(target)[1])))
        event_cases = (
            ('活动战后错误页面', lambda d: d['evidence'].update(pages=['page_main'])),
            ('活动战后仍在地图内', lambda d: d['evidence'].update(in_map=True)),
            ('活动战后章节关联错误', lambda d: d['evidence']['campaign'].update(chapter='campaign.other')),
        )
        for i, (name, mutate) in enumerate(event_cases):
            target = Path(tmp) / f'event-{i}'
            shutil.copytree(event.parent, target)
            path = target / 'task-after-event.json'
            value = json.loads(path.read_text(encoding='utf-8'))
            mutate(value)
            path.write_text(json.dumps(value), encoding='utf-8')
            checks.append((name, audit_artifact(target / event.name)['verdict'] == 'contradiction'))
            checks.append((name + '：归档校验和发现修改', bool(audit_archive(target)[1])))
        target = Path(tmp) / 'main-event-page'
        shutil.copytree(queued.parent, target)
        path = target / 'task-after-campaign.json'
        value = json.loads(path.read_text(encoding='utf-8'))
        value['evidence']['pages'] = ['page_event']
        path.write_text(json.dumps(value), encoding='utf-8')
        checks.append(('普通战役不能以活动页证明返页',
                       audit_artifact(target / queued.name)['verdict'] == 'contradiction'))
        target = Path(tmp) / 'missing-task'
        shutil.copytree(queued.parent, target)
        (target / 'task-after-campaign.json').unlink()
        checks.append(('缺队列任务工件不得静默跳过', bool(audit_archive(target)[1])))
        target = Path(tmp) / 'source-hash'
        shutil.copytree(queued.parent, target)
        manifest_path = target / 'archive.json'
        manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
        original = Path(tmp) / 'originals' / manifest['source_run']
        original.mkdir(parents=True)
        for name in manifest['files']:
            data = (target / name).read_bytes()
            (original / name).write_bytes(data)
            manifest['source_files_sha256'][name] = hashlib.sha256(data).hexdigest()
        manifest_path.write_text(json.dumps(manifest), encoding='utf-8')
        checks.append(('本机原件哈希一致', not audit_archive(target, Path(tmp) / 'originals')[1]))
        (original / 'queue.json').write_bytes(b'changed original')
        checks.append(('本机原件篡改被发现', bool(audit_archive(target, Path(tmp) / 'originals')[1])))
        target = Path(tmp) / 'reordered-session'
        shutil.copytree(queued.parent, target)
        log_path = target / 'session-log.jsonl'
        rows = [json.loads(line) for line in log_path.read_text(encoding='utf-8').splitlines()]
        task_positions = [i for i, row in enumerate(rows) if row.get('scope') == 'task']
        rows[task_positions[0]], rows[task_positions[1]] = (
            rows[task_positions[1]], rows[task_positions[0]])
        log_path.write_text('\n'.join(json.dumps(row) for row in rows), encoding='utf-8')
        checks.append(('会话任务先后顺序被篡改',
                       audit_artifact(target / queued.name)['verdict'] == 'contradiction'))
    for name, passed in checks:
        print(f'{"PASS" if passed else "FAIL"}: {name}')
    return 0 if all(passed for _, passed in checks) else 1


if __name__ == '__main__':
    sys.exit(main())
