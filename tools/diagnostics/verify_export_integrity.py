"""Corrupt synthetic exports and require both Python and C# to reject them.

Uses a tiny source repository, no account/config/device and no production data
mutation. The baseline is emitted by the real exporter, not hand-made JSON.
"""
from contextlib import redirect_stdout
import copy
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from export_upstream_data import export_assets, export_campaign
from verify_export import check


class ExportIntegrityTests(unittest.TestCase):
    def test_consumers_reject_incomplete_variants_sources_inventory_and_manifest(self):
        scratch = ROOT / '.runtime/verification'
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='export-integrity-', dir=scratch) as directory:
            repo, data = Path(directory) / 'repo', Path(directory) / 'data'
            for module in ('one', 'two', 'nested/three'):
                source = repo / f'module/{module}/assets.py'
                source.parent.mkdir(parents=True)
                source.write_text('BUTTON = Button(area=' + repr({s: (0, 0, 2, 2) for s in ('cn','en','jp','tw')})
                                  + ', color=' + repr({s: (1, 2, 3) for s in ('cn','en','jp','tw')})
                                  + ', button=' + repr({s: (0, 0, 2, 2) for s in ('cn','en','jp','tw')})
                                  + ', file=' + repr({s: './assets/sample.png' for s in ('cn','en','jp','tw')})
                                  + ')\n', encoding='utf-8')
            (repo / 'assets').mkdir()
            (repo / 'assets/sample.png').write_bytes(b'fixture: existence only')
            for name in ('first', 'second'):
                source = repo / f'campaign/fixture/{name}.py'
                source.parent.mkdir(parents=True, exist_ok=True)
                source.write_text('class Config:\n    FLAG = True\nclass Campaign:\n    pass\n', encoding='utf-8')
            manifest = {'errors': []}
            export_assets(str(repo), str(data), manifest)
            export_campaign(str(repo), str(data), manifest)
            (data / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
            baseline = {name: json.loads((data / name).read_text(encoding='utf-8'))
                        for name in ('assets.json', 'campaign_index.json', 'manifest.json')}

            def missing_server(values):
                values['assets.json']['assets']['one/BUTTON']['file'].pop('jp')

            def wrong_source(values):
                values['assets.json']['assets']['one/BUTTON']['source'] = 'module/two/assets.py'

            def duplicate_server(values):
                values['assets.json']['assets']['one/BUTTON']['servers'].append('cn')

            def missing_index(values):
                values['campaign_index.json']['chapters'].pop()

            def duplicate_index(values):
                values['campaign_index.json']['chapters'].append(values['campaign_index.json']['chapters'][0])

            def wrong_ir(values):
                rows = values['campaign_index.json']['chapters']
                rows[0]['json'] = rows[1]['json']

            def wrong_count(values):
                values['manifest.json']['assets']['count'] += 1

            def wrong_hash(values):
                values['manifest.json']['assets']['source_hashes']['module/one/assets.py'] = '0' * 64

            def unresolved(values):
                values['manifest.json']['assets']['unresolved'].append({'asset': 'fixture'})

            def wrong_kind(values):
                values['assets.json']['assets']['one/BUTTON']['kind'] = 'Unknown'

            def missing_error_list(values):
                values['manifest.json'].pop('errors')

            def missing_unresolved_list(values):
                values['manifest.json']['assets'].pop('unresolved')

            def duplicate_catalog_server(values):
                values['assets.json']['servers'].append('cn')

            def missing_catalog_server(values):
                values['assets.json']['servers'].pop()

            def malformed_area(values):
                values['assets.json']['assets']['one/BUTTON']['area']['cn'] = [0, 1, 2]

            def malformed_color(values):
                values['assets.json']['assets']['one/BUTTON']['color']['jp'] = [1, 2]

            def wrong_source_count(values):
                values['manifest.json']['campaign']['source_files'] += 1

            def wrong_complete_count(values):
                values['manifest.json']['assets']['all_four_servers'] += 1

            cases = [None, missing_server, wrong_source, duplicate_server, missing_index,
                     duplicate_index, wrong_ir, wrong_count, wrong_hash, unresolved, wrong_kind,
                     missing_error_list, missing_unresolved_list, duplicate_catalog_server,
                     missing_catalog_server, malformed_area, malformed_color, wrong_source_count,
                     wrong_complete_count]
            exe = ROOT / 'src/Alas.DataTool/bin/Release/net10.0/alashub.exe'
            self.assertTrue(exe.is_file(), 'Build Release alashub before verification')
            for corrupt in cases:
                with self.subTest(case=corrupt.__name__ if corrupt else 'valid'):
                    values = copy.deepcopy(baseline)
                    if corrupt:
                        corrupt(values)
                    for name, value in values.items():
                        (data / name).write_text(json.dumps(value), encoding='utf-8')
                    with redirect_stdout(io.StringIO()):
                        python = check(str(repo), str(data))
                    result = subprocess.run([str(exe), 'verify', '--repo', str(repo), '--data', str(data)],
                                            capture_output=True, text=True, encoding='utf-8', errors='replace',
                                            timeout=30, cwd=ROOT)
                    self.assertEqual(python['ok'], corrupt is None, python)
                    self.assertEqual(result.returncode, 0 if corrupt is None else 1, result.stdout + result.stderr)
            print(f'Python/C# export integrity: {len(cases)} shared cases')


if __name__ == '__main__':
    unittest.main()
