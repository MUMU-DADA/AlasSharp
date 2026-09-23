"""离线验证真机冒烟的拒绝路径；合成工件仅测试审计器，不作为实机证据。"""
from __future__ import annotations

import json
from pathlib import Path
from tempfile import TemporaryDirectory

from device_smoke import CHAPTER, audit_smoke_run


def write(path, value):
    path.write_text(json.dumps(value), encoding='utf-8')


def change(path, mutate):
    value = json.loads(path.read_text(encoding='utf-8'))
    mutate(value)
    write(path, value)


def fixture(root, mode):
    """构造独立的协议样本；不导入宿主、不连接设备、不读取本机账号配置。"""
    root.mkdir()
    live = mode != 'dry-run'
    session = [{'scope': 'session', 'fields': {'dry_run': not live}},
               {'scope': 'session', 'fields': {'serial': '<device>'}}]
    docs = {'live-state': {
        'id': 'live-state', 'kind': 'account_state',
        'outcome': 'succeeded' if live else 'skipped',
        'unmet_preconditions': [] if live else ['capture requires a live session'],
        'evidence': {'source': 'device_capture', 'frame': {'available': True, 'shape': [720, 1280, 3]},
                     'pages': ['page_main'], 'page_errors': [], 'in_map_tolerance': 37.5},
    }}
    if mode == 'observe':
        docs['observe'] = {
            'id': 'observe', 'kind': 'observe', 'outcome': 'succeeded',
            'evidence': {'ticks': 4, 'errors': 0, 'read_only': True, 'cancelled': False,
                         'warmup': {'ok': True},
                         'capture': {'attempts': 4, 'succeeded': 4, 'failed': 0},
                         'page_detection': {'attempts': 4, 'failed': 0, 'skipped_capture_failed': 0}},
        }
    if mode == 'campaign':
        stage = {'chapter': CHAPTER, 'stage': '1-1', 'outcome': 'cleared', 'cleared': True,
                 'failed': False, 'skipped': False, 'error_kind': 'none', 'error': None,
                 'contract_violations': [], 'artifact': str(root / 'sortie-1-1.json')}
        docs['campaign-smoke'] = {
            'id': 'campaign-smoke', 'kind': 'campaign_batch', 'outcome': 'succeeded',
            'input': {'chapters': [CHAPTER]},
            'evidence': {'batch_outcome': 'cleared', 'cleared': True, 'stages': [stage],
                         'stopped_early': False,
                         'index_artifact': str(root / 'index.json')},
        }
        write(root / 'index.json', {'contract': 'sortie-result/1', 'dry_run': False,
                                   'outcome': 'cleared', 'cleared': True,
                                   'stopped_early': False, 'stages': [stage]})
        write(root / 'sortie-1-1.json', {
            'chapter': CHAPTER, 'stage': '1-1', 'cleared': True,
            'result': {'contract': 'sortie-result/1', 'dry_run': False, 'chapter': CHAPTER,
                       'stage': '1-1', 'outcome': 'cleared', 'cleared': True, 'campaign_end': True,
                       'steps': [{'step': 'execute_a_battle'}],
                       'end_evidence': {'battle_rank': 'S', 'rank_source': 'BATTLE_STATUS_S',
                                        'combat_status': True, 'stage_observed': True, 'withdrawn': False}},
        })
    for doc in docs.values():
        if live:
            doc.update(error_kind='none', error=None, stop_reason=None)
        doc['elapsed_s'] = 1.0
    entries = []
    for task_id, doc in docs.items():
        path = root / f'task-{task_id}.json'
        write(path, doc)
        entries.append({'id': task_id, 'kind': doc['kind'], 'outcome': doc['outcome'],
                        'error_kind': doc.get('error_kind'), 'error': doc.get('error'),
                        'elapsed_s': doc['elapsed_s'], 'artifact': str(path)})
    write(root / 'queue.json', {'dry_run': not live, 'outcome': 'succeeded' if live else 'partial',
                               'host_start_count': 1, 'device_configure_count': int(live),
                               'stopped_early': False, 'tasks': entries})
    if mode == 'campaign':
        def event(scope, fields, message=''):
            return {'scope': scope, 'fields': fields, 'message': message}

        session = [
            event('session', {'dry_run': False, 'host_start_ms': 1}),
            event('session', {'serial': '<device>', 'configured': 'device'}),
            event('queue', {'tasks': len(entries), 'kinds': 'account_state,campaign_batch'}),
            event('queue', {'task': 'live-state', 'kind': 'account_state'}),
            event('task', {'outcome': 'succeeded', 'error_kind': 'none', 'error': None,
                           'elapsed_s': 1.0}),
            event('queue', {'task': 'campaign-smoke', 'kind': 'campaign_batch'}),
            event('stage', {'chapter': CHAPTER, 'stage': '1-1', 'outcome': 'cleared',
                            'cleared': True, 'violations': 0}),
            event('task', {'outcome': 'succeeded', 'error_kind': 'none', 'error': None,
                           'elapsed_s': 1.0}),
            event('queue', {'outcome': 'succeeded', 'tasks': len(entries)}),
            event('session', {}, '识图宿主已释放'),
        ]
    write(root / 'state.json', {'completed': {
        task_id: {'outcome': 'succeeded', 'identity': {'id': task_id}} for task_id in docs if live}})
    (root / 'session-log.jsonl').write_text('\n'.join(json.dumps(entry) for entry in session), encoding='utf-8')
    report = root / 'report.json'
    write(report, {'dry_run': not live, 'queue_outcome': 'succeeded' if live else 'partial',
                   'host_start_count': 1, 'device_configure_count': int(live),
                   'has_failures': False, 'evidence_complete': True, 'findings': [],
                   'totals': {'log_entries': len(session)}})
    return report


def main():
    checks = []
    with TemporaryDirectory(prefix='alas-smoke-audit-') as tmp:
        number = 0

        def run(name, mode='observe', mutate=None, queue_code=0, report_code=0, expected=None):
            nonlocal number
            number += 1
            root = Path(tmp) / str(number)
            report = fixture(root, mode)
            if mutate:
                mutate(root)
            failures = audit_smoke_run(root, queue_code, report_code, report,
                                       allow_actions=mode == 'campaign', read_only_device=mode == 'observe')
            ok = not failures if expected is None else any(expected in failure for failure in failures)
            checks.append((name, ok))
            if not ok:
                print(f'  实际失败: {failures}')

        for mode in ('dry-run', 'observe', 'campaign'):
            run(f'{mode} 完整证据通过', mode)
        run('非零队列退出码不得被成功工件掩盖', queue_code=1, expected='queue 退出码')
        run('非零报告退出码不得被旧报告掩盖', report_code=1, expected='report 退出码')
        run('报告没写出 JSON', mutate=lambda p: (p / 'report.json').unlink(), expected='report.json 缺失')
        run('报告 JSON 损坏', mutate=lambda p: (p / 'report.json').write_text('{'), expected='report.json 缺失')
        run('报告证据不完整', mutate=lambda p: change(p / 'report.json',
            lambda d: d.update(evidence_complete=False)), expected='证据不完整')
        run('报告 state_incomplete 不可忽略', mutate=lambda p: change(p / 'report.json',
            lambda d: d.update(findings=[{'code': 'state_incomplete'}])), expected='finding')
        run('缺失会话日志', mutate=lambda p: (p / 'session-log.jsonl').unlink(), expected='session-log.jsonl 缺失')
        run('报告借用旧日志计数', mutate=lambda p: change(p / 'report.json',
            lambda d: d['totals'].update(log_entries=99)), expected='日志计数')
        run('队列没有声明请求任务', mutate=lambda p: change(p / 'queue.json',
            lambda d: d['tasks'].pop()), expected='任务清单不完整')
        run('缺失观察任务', mutate=lambda p: (p / 'task-observe.json').unlink(), expected='task-observe.json 缺失')
        run('没有成功断点', mutate=lambda p: change(p / 'state.json',
            lambda d: d.update(completed={})), expected='完成断点')
        run('重复初始化宿主', mutate=lambda p: change(p / 'queue.json',
            lambda d: d.update(host_start_count=2)), expected='host_start_count')
        run('授权真跑实际却为 dry-run', mutate=lambda p: change(p / 'queue.json',
            lambda d: d.update(dry_run=True)), expected='运行模式')
        run('存盘帧不得冒充当场抓帧', mutate=lambda p: change(p / 'task-live-state.json',
            lambda d: d['evidence'].update(source='file:fixture.png')), expected='当场抓帧')
        run('空帧不得通过账号状态', mutate=lambda p: change(p / 'task-live-state.json',
            lambda d: d['evidence']['frame'].update(available=False)), expected='当场抓帧')
        run('缺少 IN_MAP 数值', mutate=lambda p: change(p / 'task-live-state.json',
            lambda d: d['evidence'].pop('in_map_tolerance')), expected='IN_MAP')
        run('识页失败不得只看 succeeded', mutate=lambda p: change(p / 'task-live-state.json',
            lambda d: d['evidence'].update(page_errors=['upstream failure'])), expected='页面识别有错误')
        run('观测未执行任何 tick', mutate=lambda p: change(p / 'task-observe.json',
            lambda d: d['evidence'].update(ticks=0)), expected='只读观测')
        run('观测声明零错误但部分抓帧失败', mutate=lambda p: change(p / 'task-observe.json',
            lambda d: d['evidence']['capture'].update(succeeded=3, failed=1)), expected='只读观测')
        run('战役工件缺失', 'campaign', lambda p: (p / 'task-campaign-smoke.json').unlink(), expected='task-campaign-smoke.json 缺失')
        run('战役原始工件缺失', 'campaign', lambda p: (p / 'sortie-1-1.json').unlink(), expected='战役原始工件缺失')
        run('CampaignEnd 不足以证明通关', 'campaign', lambda p: change(p / 'sortie-1-1.json',
            lambda d: d['result'].pop('end_evidence')), expected='合同/批次/会话核对')
        run('撤退不能伪装通关', 'campaign', lambda p: change(p / 'sortie-1-1.json',
            lambda d: d['result']['end_evidence'].update(withdrawn=True)), expected='合同/批次/会话核对')
        run('批次与通关原件不一致', 'campaign', lambda p: change(p / 'index.json',
            lambda d: d['stages'][0].update(outcome='withdrawn')), expected='合同/批次/会话核对')
        run('dry-run 原件不能冒充实机', 'campaign', lambda p: change(p / 'sortie-1-1.json',
            lambda d: d['result'].update(dry_run=True)), expected='合同/批次/会话核对')
        run('默认模式不能声称跑过', 'dry-run', lambda p: change(p / 'task-live-state.json',
            lambda d: d.update(outcome='succeeded')), expected='默认 dry-run')
    for name, passed in checks:
        print(f'{"PASS" if passed else "FAIL"}: {name}')
    print(f'冒烟证据审计：{sum(p for _, p in checks)}/{len(checks)} 通过（离线合成反例，无设备动作）')
    return 0 if all(passed for _, passed in checks) else 1


if __name__ == '__main__':
    raise SystemExit(main())
