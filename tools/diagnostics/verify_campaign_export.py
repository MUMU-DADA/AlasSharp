"""Campaign declaration regressions; no devices and no runtime JSON execution."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from upstream_campaign_export import CampaignResolver
from export_upstream_data import export_campaign, CAMPAIGN_SCHEMA


class CampaignExportTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / '.runtime/verification'
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix='campaign-export-', dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / 'repo'
        self.source('module.map.map_base', 'class CampaignMap: pass')

    def source(self, module, text):
        path = self.repo.joinpath(*module.split('.')).with_suffix('.py')
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def resolve(self, text):
        self.source('campaign.fixture.chapter', text)
        return CampaignResolver(self.repo).export('campaign.fixture.chapter')

    def test_grid_class_imports_expressions_private_state_and_map_separation(self):
        self.source('campaign.fixture.base', 'class Grid: pass\nFILTER = "1L > " + "1M"')
        result = self.resolve('''
from module.map.map_base import CampaignMap
from .base import Grid as G, FILTER
MAP = CampaignMap('fixture')
MAP.shape = 'B1'
A1, B1 = MAP.flatten()
class Config:
    FLAG = True
class Campaign:
    MAP = MAP
    grid_class = G
    ENEMY_FILTER = FILTER
    MACHINE_FORTRESS = [A1, (B1,)]
    _visited = False
    x: int = 3
    x += 2
    y = z = x + 1
    def battle_0(self): return self.clear_boss()
''')
        self.assertTrue(result['complete'], result)
        self.assertEqual(result['values'], {'grid_class': {'$ref': 'campaign.fixture.base.Grid', 'kind': 'class'},
            'ENEMY_FILTER': '1L > 1M', 'MACHINE_FORTRESS': ['A1', ['B1']], '_visited': False,
            'x': 5, 'y': 6, 'z': 6})
        self.assertEqual(result['typed_values']['MACHINE_FORTRESS']['items'][0],
                         dict(type='grid', location=[0, 0]))
        self.assertNotIn('FLAG', result['values'])
        self.assertNotIn('MAP', result['origins'])

    def test_method_aliases_keep_binding_time_and_shadowed_attributes(self):
        result = self.resolve('''
class Campaign:
    replaced = 1
    def replaced(self): pass
    def battle_0(self): return True
    battle_1 = battle_0
    battle_2 = battle_1
    battle_0 = 9
''')
        self.assertTrue(result['complete'], result)
        self.assertEqual(result['values'], {'battle_0': 9})
        self.assertEqual(result['method_aliases'], {k: dict(module='campaign.fixture.chapter', name='Campaign.battle_0')
                                                   for k in ('battle_1', 'battle_2')})

    def test_reexport_declared_scope_does_not_claim_effective_inheritance(self):
        self.source('campaign.fixture.base', 'class Parent:\n    inherited = 1\nclass Campaign(Parent):\n    own = 2')
        result = self.resolve('from .base import Campaign as Campaign')
        self.assertTrue(result['complete'], result)
        self.assertEqual(result['scope'], 'declared')
        self.assertEqual(result['class_reference'], 'campaign.fixture.base.Campaign')
        self.assertEqual(result['values'], {'own': 2})
        self.assertIn('campaign/fixture/base.py', result['source_files'])

    def test_unknown_references_and_native_calls_never_disappear(self):
        for expression in ('UNKNOWN', '[UNKNOWN]', 'factory()'):
            result = self.resolve('class Campaign:\n    field = ' + expression)
            self.assertFalse(result['complete'], result)
            self.assertEqual(result['unresolved'][0]['field'], 'field')
            self.assertIn('field', result['origins'])
            self.assertNotIn('field', result['values'])

    def test_mutations_and_class_transformations_are_explicitly_incomplete(self):
        for text in ('class Campaign:\n    field=[]\n    field.append(1)',
                     'class Campaign:\n    field=[]\n    field[0]=1',
                     'class Campaign:\n    field=[]\n    alias=field\n    alias += [1]',
                     'class Campaign:\n    field=1\n    if condition:\n        field=2',
                     '@decorate\nclass Campaign:\n    field=1',
                     'class Campaign:\n    @property\n    def method(self): pass\n    alias=method'):
            result = self.resolve(text)
            self.assertFalse(result['complete'], result)
            self.assertTrue(result['unresolved'])

    def test_missing_class_and_missing_import_are_distinct(self):
        result = self.resolve('VALUE=1')
        self.assertFalse(result['present'])
        self.assertTrue(result['complete'])
        result = self.resolve('from .missing import Campaign')
        self.assertFalse(result['complete'])

    def test_export_metadata_keys_match_schema_index_and_manifest(self):
        self.resolve('class Campaign:\n    value=(1, 2)\n    def battle_0(self): return True\n    battle_1=battle_0')
        dest = Path(self.temp.name) / 'data'
        manifest = {'errors': []}
        rows = export_campaign(str(self.repo), str(dest), manifest)
        ir = json.loads((dest / rows[0]['json']).read_text(encoding='utf-8'))
        schema = CAMPAIGN_SCHEMA['properties']['campaign']
        self.assertTrue(set(schema['required']) <= ir['campaign'].keys())
        self.assertEqual(set(schema['properties']['attributes_meta']['required']),
                         set(ir['campaign']['attributes_meta']))
        self.assertEqual(rows[0]['campaign_attributes'], ['value'])
        self.assertEqual(rows[0]['campaign_aliases'], ['battle_1'])
        self.assertEqual(manifest['campaign']['campaign_attributes'], 1)
        self.assertEqual(manifest['campaign']['campaign_aliases'], 1)


if __name__ == '__main__':
    unittest.main()
