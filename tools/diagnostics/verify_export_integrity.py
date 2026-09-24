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
            map_source = repo / 'module/map/map_base.py'
            map_source.parent.mkdir(parents=True)
            map_source.write_text('class CampaignMap:\n    def ignore_prediction(self, grid, **kwargs): pass\n', encoding='utf-8')
            for name in ('first', 'second'):
                source = repo / f'campaign/fixture/{name}.py'
                source.parent.mkdir(parents=True, exist_ok=True)
                source.write_text('from module.map.map_base import CampaignMap\nclass Grid: pass\nMAP = CampaignMap("fixture")\nMAP.shape = "A1"\nMAP.grid_class = Grid\nA1, = MAP.flatten()\nMAP.fortress_data = [A1, (A1,)]\nMAP.ignore_prediction(A1, is_siren=True)\nclass Config:\n    FLAG = True\nclass Campaign:\n    grid_class = Grid\n    MACHINE_FORTRESS = [A1]\n    def battle_0(self): return True\n    battle_1 = battle_0\n', encoding='utf-8')
            manifest = {'errors': []}
            export_assets(str(repo), str(data), manifest)
            export_campaign(str(repo), str(data), manifest)
            (data / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
            baseline = {name: json.loads((data / name).read_text(encoding='utf-8'))
                        for name in ('assets.json', 'campaign_index.json', 'manifest.json',
                                     'campaign/fixture/first.json', 'campaign/fixture/second.json')}

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

            def no_map_metadata(values):
                values['campaign/fixture/first.json'].pop('map_meta')

            def no_grid_class(values):
                values['campaign/fixture/first.json']['map'].pop('grid_class')

            def wrong_grid_reference(values):
                values['campaign/fixture/first.json']['map']['fortress_data'][0] = 'B1'

            def wrong_grid_type(values):
                values['campaign/fixture/first.json']['map_meta']['typed_values']['fortress_data']['items'][0]['type'] = 'unknown'

            def missing_map_origin(values):
                values['campaign/fixture/first.json']['map_meta']['origins'].pop('grid_class')

            def wrong_map_source(values):
                values['campaign/fixture/first.json']['map_meta']['source_files'].remove('module/map/map_base.py')
                values['campaign/fixture/first.json']['map_meta']['derived_from'] = 'module/map/map_base.py'

            def incomplete_map(values):
                values['campaign/fixture/first.json']['map_meta']['complete'] = False

            def map_unresolved(values):
                values['campaign/fixture/first.json']['map_meta']['unresolved'].append({'reason': 'fixture'})

            def map_index_keys(values):
                values['campaign_index.json']['chapters'][0]['map_keys'].pop()

            def map_manifest_count(values):
                values['manifest.json']['campaign']['map_fields'] += 1

            def missing_map_calls(values):
                values['campaign/fixture/first.json']['map_meta'].pop('calls')

            def wrong_call_argument(values):
                values['campaign/fixture/first.json']['map_meta']['calls'][0]['args'][0] = 'B1'

            def missing_campaign_metadata(values):
                values['campaign/fixture/first.json']['campaign'].pop('attributes_meta')

            def missing_campaign_reference(values):
                values['campaign/fixture/first.json']['campaign']['attributes'].pop('grid_class')

            def wrong_campaign_grid(values):
                values['campaign/fixture/first.json']['campaign']['attributes']['MACHINE_FORTRESS'] = ['B1']

            def wrong_campaign_type(values):
                values['campaign/fixture/first.json']['campaign']['attributes_meta']['typed_values']['grid_class']['type'] = 'unknown'

            def missing_campaign_origin(values):
                values['campaign/fixture/first.json']['campaign']['attributes_meta']['origins'].pop('grid_class')

            def missing_campaign_alias(values):
                values['campaign/fixture/first.json']['campaign']['attributes_meta']['method_aliases'].clear()

            def wrong_campaign_alias_source(values):
                values['campaign/fixture/first.json']['campaign']['attributes_meta']['method_aliases']['battle_1']['module'] = 'missing'

            def wrong_campaign_scope(values):
                values['campaign/fixture/first.json']['campaign']['attributes_meta']['scope'] = 'effective'

            def unresolved_campaign(values):
                values['campaign/fixture/first.json']['campaign']['attributes_meta']['unresolved'].append({'reason': 'missing'})

            def incomplete_campaign(values):
                values['campaign/fixture/first.json']['campaign']['attributes_meta']['complete'] = False

            def wrong_campaign_index(values):
                values['campaign_index.json']['chapters'][0]['campaign_attributes'].pop()

            def wrong_campaign_count(values):
                values['manifest.json']['campaign']['campaign_attributes'] += 1

            cases = [None, missing_server, wrong_source, duplicate_server, missing_index,
                     duplicate_index, wrong_ir, wrong_count, wrong_hash, unresolved, wrong_kind,
                     missing_error_list, missing_unresolved_list, duplicate_catalog_server,
                     missing_catalog_server, malformed_area, malformed_color, wrong_source_count,
                     wrong_complete_count, no_map_metadata, no_grid_class, wrong_grid_reference,
                     wrong_grid_type, missing_map_origin, wrong_map_source, incomplete_map,
                     map_unresolved, map_index_keys, map_manifest_count, missing_map_calls, wrong_call_argument,
                     missing_campaign_metadata, missing_campaign_reference, wrong_campaign_grid,
                     wrong_campaign_type, missing_campaign_origin, missing_campaign_alias, wrong_campaign_alias_source,
                     wrong_campaign_scope, unresolved_campaign, incomplete_campaign, wrong_campaign_index, wrong_campaign_count]
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
