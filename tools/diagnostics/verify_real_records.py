"""结构化真机证据链回归：篡改/缺文件/字段分歧必须被独立核对发现。"""
from __future__ import annotations

import json
import hashlib
import shutil
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

import archive_real_run
from audit_real_records import ARCHIVE, audit_archive, audit_artifact

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def make_multi_index_fixture(destination: Path, events: list[Path]) -> None:
    """Combine two archived sorties into a temporary synthetic queue for contract tests."""
    by_stage = {}
    for path in events:
        if (path.parent / 'task-after-event.json').is_file():
            stage = json.loads(path.read_text(encoding='utf-8'))['result']['stage']
            by_stage.setdefault(stage, path)
    first, second = by_stage['a1'].parent, by_stage['a2'].parent
    shutil.copytree(first, destination)
    (destination / 'archive.json').unlink()
    for name in ('index.json', 'sortie-a2.json', 'task-event-a2.json'):
        shutil.copyfile(second / name, destination / ('index-2.json' if name == 'index.json' else name))

    task_path = destination / 'task-event-a2.json'
    task = json.loads(task_path.read_text(encoding='utf-8'))
    task['evidence']['index_artifact'] = 'index-2.json'
    task_path.write_text(json.dumps(task), encoding='utf-8')

    for stage, source in (('a1', first), ('a2', second)):
        capture = json.loads((source / 'task-after-event.json').read_text(encoding='utf-8'))
        capture['id'] = f'after-{stage}'
        (destination / f'task-after-{stage}.json').write_text(json.dumps(capture), encoding='utf-8')
    (destination / 'task-after-event.json').unlink()

    queue = json.loads((first / 'queue.json').read_text(encoding='utf-8'))
    other_queue = json.loads((second / 'queue.json').read_text(encoding='utf-8'))
    queue['tasks'] = queue['tasks'] + other_queue['tasks']
    queue['elapsed_s'] += other_queue['elapsed_s']
    for position, stage in ((1, 'a1'), (3, 'a2')):
        queue['tasks'][position]['id'] = f'after-{stage}'
        queue['tasks'][position]['artifact'] = f'task-after-{stage}.json'
    (destination / 'queue.json').write_text(json.dumps(queue), encoding='utf-8')

    def read_session(source: Path) -> list[dict]:
        return [json.loads(line) for line in
                (source / 'session-log.jsonl').read_text(encoding='utf-8').splitlines()]

    first_session, second_session = read_session(first), read_session(second)
    first_tasks, second_tasks = first_session[3:-3], second_session[3:-3]
    for stage, rows in (('a1', first_tasks), ('a2', second_tasks)):
        for event in rows:
            fields = event.get('fields') or {}
            if event.get('scope') == 'queue' and fields.get('task') == 'after-event':
                fields['task'] = f'after-{stage}'
    session = first_session[:3] + first_tasks + second_tasks + first_session[-3:]
    for event in session:
        fields = event.get('fields') or {}
        if event.get('scope') == 'queue' and 'kinds' in fields:
            fields.update(tasks=4, kinds='campaign_batch,account_state,campaign_batch,account_state')
        elif event.get('scope') == 'queue' and 'outcome' in fields:
            fields['tasks'] = 4
    (destination / 'session-log.jsonl').write_text(
        '\n'.join(json.dumps(event, ensure_ascii=False) for event in session), encoding='utf-8')

    planned = []
    for row in queue['tasks']:
        task = json.loads((destination / Path(row['artifact']).name).read_text(encoding='utf-8'))
        planned.append({key: task[key] for key in ('id', 'kind', 'input', 'required')})
    (destination / 'plan.json').write_text(json.dumps({
        'generated_by': 'alashub plan-queue', 'dry_run': False, 'tasks': planned,
    }), encoding='utf-8')


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
    checks.append(('三个活动章节真实抓帧返回活动页', len(events) >= 3 and all(
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
    with TemporaryDirectory(prefix='alas-archive-check-', dir=archive_real_run.ROOT / 'data') as tmp:
        root = Path(tmp)
        source_dir = root / 'source'
        shutil.copytree(event.parent, source_dir)
        (source_dir / 'archive.json').unlink()
        output_root = root / 'archives'
        output_root.mkdir()
        previous_archive = archive_real_run.ARCHIVE
        try:
            archive_real_run.ARCHIVE = output_root
            generated = archive_real_run.archive_run(source_dir, '5f87af9')
            generated_records, generated_errors = audit_archive(generated)
            checks.append(('新归档生成后可独立审计', not generated_errors and
                           len(generated_records) == 1 and
                           generated_records[0]['verdict'] == 'consistent'))
            try:
                archive_real_run.archive_run(source_dir, '5f87af9')
                duplicate_rejected = False
            except FileExistsError:
                duplicate_rejected = True
            checks.append(('现有归档不会被覆盖', duplicate_rejected))
        finally:
            archive_real_run.ARCHIVE = previous_archive
    with TemporaryDirectory(prefix='alas-multi-index-check-', dir=archive_real_run.ROOT / 'data') as tmp:
        root = Path(tmp)
        source_dir = root / 'synthetic-source'
        make_multi_index_fixture(source_dir, events)
        output_root = root / 'archives'
        output_root.mkdir()
        previous_archive = archive_real_run.ARCHIVE
        try:
            archive_real_run.ARCHIVE = output_root
            generated = archive_real_run.archive_run(source_dir, '5f87af9')
        finally:
            archive_real_run.ARCHIVE = previous_archive
        records, errors = audit_archive(generated)
        checks.append(('多批次索引及计划完整归档', not errors and
                       {'index.json', 'index-2.json', 'plan.json'} <=
                       {p.name for p in generated.iterdir()} and
                       len(records) == 2 and all(r['verdict'] == 'consistent' and
                                                 r['post_campaign_page_verified'] for r in records)))
        plan_path = generated / 'plan.json'
        original_plan = plan_path.read_bytes()
        plan = json.loads(original_plan)
        plan['tasks'][2]['input']['chapters'] = ['campaign.other']
        plan_path.write_text(json.dumps(plan), encoding='utf-8')
        checks.append(('计划章节篡改被逐关语义审计发现', all(
            audit_artifact(path)['verdict'] == 'contradiction'
            for path in generated.glob('sortie-*.json'))))
        checks.append(('计划篡改被归档校验和发现', bool(audit_archive(generated)[1])))
        plan_path.write_bytes(original_plan)
        task_path = generated / 'task-event-a2.json'
        original_task = task_path.read_bytes()
        task = json.loads(original_task)
        task['evidence']['index_artifact'] = 'index.json'
        task_path.write_text(json.dumps(task), encoding='utf-8')
        checks.append(('战役任务错指前一批次索引被发现',
                       audit_artifact(generated / 'sortie-a2.json')['verdict'] == 'contradiction'))
        task_path.write_bytes(original_task)
        index_path = generated / 'index-2.json'
        index_path.unlink()
        checks.append(('次批次索引缺失不得静默跳过', bool(audit_archive(generated)[1])))
    for name, passed in checks:
        print(f'{"PASS" if passed else "FAIL"}: {name}')
    return 0 if all(passed for _, passed in checks) else 1


if __name__ == '__main__':
    sys.exit(main())
