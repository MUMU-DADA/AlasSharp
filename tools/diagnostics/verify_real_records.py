"""结构化真机证据链回归：篡改/缺文件/字段分歧必须被独立核对发现。"""
from __future__ import annotations

import json
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
            r['stage_withdrawal'] and not r['cleared'] for r in archived))]
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
    for name, passed in checks:
        print(f'{"PASS" if passed else "FAIL"}: {name}')
    return 0 if all(passed for _, passed in checks) else 1


if __name__ == '__main__':
    sys.exit(main())
