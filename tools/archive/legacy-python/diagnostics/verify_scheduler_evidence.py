"""Reject false scheduler-stop conclusions and altered source/projection evidence, offline."""
import copy
from pathlib import Path
import shutil
import tempfile

import audit_scheduler_evidence as audit


def main():
    archives = sorted(audit.ARCHIVE.glob('*/archive.json'))
    audit.require(bool(archives), 'real scheduler archive missing')
    facts = [audit.audit(path.parent) for path in archives]
    audit.require(audit.DOC.read_text(encoding='utf-8') == audit.report(facts), 'scheduler document drift')
    proof = audit.read_json(archives[0].parent / 'proof.json')
    mutations = [
        lambda p: p['queue'].update(outcome='succeeded'),
        lambda p: p['queue'].update(device_configure_count=2),
        lambda p: p['resume_state']['completed'].update({'native-scheduler-boundary': {}}),
        lambda p: p['task']['input'].update(allow_actions=False),
        lambda p: p['task']['evidence'].update(stop_observed=False),
        lambda p: p['task']['evidence'].update(dispatch_count=2),
        lambda p: p['dispatches'][0].update(native_success=False),
        lambda p: p['dispatches'][0].update(returned=False),
        lambda p: p['dispatches'][0].update(finished_at='2000-01-01T00:00:00+00:00'),
        lambda p: p['scheduler_state'].update(phase='running'),
        lambda p: p['session'].update(releases=0),
        lambda p: p.update(log_cursor=0),
        lambda p: p['events'].reverse(),
        lambda p: p['events'].pop(),
        lambda p: p['collection_flags']['before'].update(CollectOil=True),
        lambda p: p['reward_next_run'].update(after=p['reward_next_run']['before']),
        lambda p: p.update(account_config_unchanged=False),
        lambda p: p['post_state']['evidence'].update(in_map=True),
        lambda p: p['post_state']['evidence'].update(pages=['page_reward']),
        lambda p: p['post_state']['evidence'].update(source='fixture'),
    ]
    for index, mutate in enumerate(mutations):
        bad = copy.deepcopy(proof)
        mutate(bad)
        try:
            audit.validate(bad)
        except audit.AuditError:
            pass
        else:
            raise AssertionError(f'tamper case {index} accepted')
    with tempfile.TemporaryDirectory(dir=audit.ROOT / '.runtime/verification', prefix='scheduler-evidence-') as directory:
        root = Path(directory)
        cloned = root / 'archive'
        shutil.copytree(archives[0].parent, cloned)
        absent = root / 'absent'
        assert audit.audit(cloned, absent)['originals_rechecked'] is False
        meta = audit.read_json(cloned / 'archive.json')
        name = next(iter(meta['source_files_sha256']))
        source = absent / name
        source.parent.mkdir(parents=True)
        source.write_bytes(b'changed source')
        for mode in ('source_checksum', 'partial_source', 'proof_checksum', 'extra_archive'):
            if mode == 'partial_source':
                meta['source_files_sha256'][name] = audit.digest(source.read_bytes())
                (cloned / 'archive.json').write_bytes(audit.encoded(meta, 'archive.json'))
            elif mode == 'proof_checksum':
                source.unlink()
                (cloned / 'proof.json').write_bytes(b'{}')
            elif mode == 'extra_archive':
                shutil.copyfile(archives[0].parent / 'proof.json', cloned / 'proof.json')
                (cloned / 'unexpected.json').write_text('{}', encoding='utf-8')
            try:
                audit.audit(cloned, absent)
            except audit.AuditError:
                pass
            else:
                raise AssertionError(f'archive tamper {mode} accepted')
    print(f'PASS: {len(facts)} real scheduler archive(s), {len(mutations)} outcome counterexamples, '
          '4 checksum/inventory counterexamples, originals and offline projection checked')


if __name__ == '__main__':
    main()
