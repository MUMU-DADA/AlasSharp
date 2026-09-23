#!/usr/bin/env python3
"""Regression checks for the real queue evidence audit (offline, no device I/O)."""
from __future__ import annotations

import copy
import json
import shutil
import tempfile
from pathlib import Path

import audit_queue_evidence as audit


def main():
    archives = sorted(audit.ARCHIVE.glob('*/archive.json'))
    audit.require(len(archives) >= 3, 'real observe/navigation/periodic archives missing')
    facts = [audit.audit_archive(p.parent) for p in archives]
    kinds = {task['kind'] for fact in facts for task in fact['tasks']}
    audit.require(kinds == audit.KINDS, 'real queue evidence domain coverage missing')
    audit.require(audit.DOC.read_text(encoding='utf-8') == audit.report(facts), 'documentation drift')
    runs = [audit.load_run(p.parent) for p in archives]
    observed = next(run for run in runs if any(t['kind'] == 'account_state' for t in run['queue.json']['tasks']))
    navigated = next(run for run in runs if any(t['kind'] == 'navigate' for t in run['queue.json']['tasks']))
    periodic = next(run for run in runs if any(t['kind'] == 'periodic_run' for t in run['queue.json']['tasks']))
    checks = ['real archives + generated report']

    def rejected(name, run, change, expected):
        broken = copy.deepcopy(run)
        change(broken)
        try:
            audit.validate_run(broken)
        except (audit.AuditError, KeyError, TypeError) as error:
            audit.require(expected in str(error), f'{name}: unexpected error {error}')
            checks.append(name)
        else:
            raise audit.AuditError(f'{name}: invalid evidence accepted')

    def task_file(run, kind):
        row = next(row for row in run['queue.json']['tasks'] if row['kind'] == kind)
        return audit.basename(row['artifact'])

    observed_file = task_file(observed, 'observe')
    navigation_file = task_file(navigated, 'navigate')
    account_file = task_file(observed, 'account_state')
    plan_file = task_file(periodic, 'periodic_plan')
    periodic_file = task_file(periodic, 'periodic_run')
    rounds_file = next(audit.basename(row['artifact']) for row in navigated['queue.json']['tasks']
                       if row['kind'] == 'navigate' and navigated[audit.basename(row['artifact'])]['input'].get('rounds') == 2)
    rejected('missing task', observed, lambda r: r.pop(observed_file), 'missing or unindexed')
    rejected('zero ticks despite succeeded', observed,
             lambda r: r[observed_file]['evidence'].update(ticks=0), 'positive ticks')
    rejected('capture count differs', observed,
             lambda r: r[observed_file]['evidence']['capture'].update(attempts=7), 'counts mismatch')
    rejected('short observation', observed,
             lambda r: r[observed_file]['evidence'].update(elapsed_seconds=0.1), 'duration mismatch')
    rejected('account uses stored frame', observed,
             lambda r: r[account_file]['evidence'].update(source='screenshot_load'), 'capture provenance')
    rejected('account detected no page', observed,
             lambda r: r[account_file]['evidence'].update(pages=[]), 'page evidence missing')
    rejected('navigation final page differs', navigated,
             lambda r: r[navigation_file]['evidence'].update(final_pages=['page_campaign']), 'final page mismatch')
    rejected('navigation leg target differs', navigated,
             lambda r: r[navigation_file]['evidence']['rounds'][0]['to_target'].update(final_pages=[]),
             'final page mismatch')
    rejected('navigation lacks arrival trajectory', navigated,
             lambda r: (r[rounds_file]['evidence']['rounds'][0]['to_target'].update(hops=[]),
                        r[rounds_file]['evidence'].update(hops=r[rounds_file]['evidence']['hops'][2:])),
             'hops do not prove arrival')
    rejected('missing second round return', navigated,
             lambda r: r[rounds_file]['evidence']['rounds'][1].pop('return_to_main'), 'return_to_main')
    rejected('session count differs', observed,
             lambda r: r['queue.json'].update(host_start_count=2), 'session counts')
    rejected('host log removed', observed,
             lambda r: r['session-log.jsonl'].pop(0), 'session log counts')
    rejected('queue kind summary differs', periodic,
             lambda r: next(row for row in r['session-log.jsonl']
                            if row['scope'] == 'queue' and 'kinds' in row['fields'])['fields'].update(kinds='observe'),
             'queue log summary mismatch')
    rejected('missing task log', observed,
             lambda r: r['session-log.jsonl'].remove(next(v for v in r['session-log.jsonl'] if v['scope'] == 'task')),
             'task log count')
    rejected('state request differs', observed,
             lambda r: r['state.json']['completed']['observe']['identity']['input'].update(seconds=99),
             'state request identity')
    rejected('state predecessor lost', observed,
             lambda r: r['state.json']['completed']['observe']['identity'].update(preceding_tasks=[]),
             'state request identity')
    rejected('periodic plan lacks requested binding', periodic,
             lambda r: r[plan_file]['evidence']['plans'].pop(), 'periodic plan missing bindings')
    rejected('periodic native method differs from plan', periodic,
             lambda r: r[periodic_file]['evidence']['target'].update(method='unbound'),
             'native dispatcher binding mismatch')
    rejected('periodic input gates differ', periodic,
             lambda r: r[periodic_file]['evidence'].update(confirm_matches=False),
             'periodic input gates mismatch')
    rejected('periodic native call did not succeed', periodic,
             lambda r: r[periodic_file]['evidence'].update(native_success=False),
             'native execution not proven')

    with tempfile.TemporaryDirectory(prefix='queue-evidence-', dir=audit.ROOT / '.runtime') as tmp:
        folder = Path(tmp)
        source = archives[0].parent
        copied = folder / source.name
        shutil.copytree(source, copied)
        # A checkout without ignored originals can check its committed evidence, explicitly
        # without claiming that original bytes or the device were re-verified.
        portable = audit.audit_archive(copied, source_root=folder / 'missing-originals')
        audit.require(portable['original_available'] is False, 'missing original incorrectly reported checked')
        checks.append('portable archive without originals')
        queue_path = copied / 'queue.json'
        original = queue_path.read_bytes()
        queue_path.write_bytes(original + b' ')
        try:
            audit.audit_archive(copied, source_root=folder / 'missing-originals')
        except audit.AuditError as error:
            audit.require('sanitized checksum mismatch' in str(error), 'tampering failed for unrelated reason')
            checks.append('tampered checksum')
        else:
            raise audit.AuditError('checksum tampering accepted')
        queue_path.write_bytes(original)
        # Rehashing a changed summary is insufficient: the remaining independent
        # task/state/session records must still reject the contradiction.
        queue = json.loads(original)
        queue['host_start_count'] = 2
        queue_path.write_bytes(audit.encoded(queue, 'queue.json'))
        metadata = audit.read_json(copied / 'archive.json')
        metadata['files']['queue.json'] = audit.digest(queue_path.read_bytes())
        (copied / 'archive.json').write_bytes(audit.encoded(metadata, 'archive.json'))
        try:
            audit.audit_archive(copied, source_root=folder / 'missing-originals')
        except audit.AuditError as error:
            audit.require('session counts' in str(error), 'rehash failed for unrelated reason')
            checks.append('rehash cannot hide contradictory session counts')
        else:
            raise audit.AuditError('rehashed contradiction accepted')

    # Privacy replacements use synthetic data and may never depend on a real developer name.
    synthetic_root = Path('X:/workspace')
    sanitized = audit.sanitize({'serial': 'test-device', 'configured': 'serial=test-device,control=MaaTouch',
                                'path': 'X:/workspace/data/run/task.json',
                                'config': {'personal': 'not-for-archive'}}, synthetic_root)
    audit.require(sanitized == {'serial': '<device>', 'configured': 'serial=<device>,control=MaaTouch',
                                'path': '<project>/data/run/task.json', 'config': '<redacted-account-config>'},
                  'privacy redaction mismatch')
    audit.require(audit.private_text({'path': 'X:/private/evidence.json'}), 'private path escaped detection')
    checks.append('deterministic privacy redaction')
    print(f'OK: {len(checks)} evidence checks (offline; no device actions)')
    for check in checks:
        print(f'  PASS {check}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
