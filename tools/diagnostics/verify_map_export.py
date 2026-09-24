"""Static MAP declaration regressions; no account, device or game actions."""
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from upstream_map_export import MapResolver


class MapExportTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / '.runtime/verification'
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix='map-export-', dir=scratch)
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)
        self.source('module.map.map_base', 'class CampaignMap: pass\n')
        self.header = 'from module.map.map_base import CampaignMap\nMAP = CampaignMap("fixture")\n'

    def source(self, module, source):
        path = self.root.joinpath(*module.split('.')).with_suffix('.py')
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(source, encoding='utf-8')

    def resolve(self, source):
        self.source('campaign.fixture', source)
        return MapResolver(self.root).export('campaign.fixture')

    def test_grid_references_containers_and_class_alias(self):
        self.source('campaign.base', 'class CustomGrid: pass\n')
        result = self.resolve(self.header + '''
from .base import CustomGrid as G
MAP.grid_class = G
MAP.shape = 'b2'
A1, B1, A2, B2 = MAP.flatten()
MAP.bouncing_enemy_data = [(A1, B2), (A2, B1)]
MAP.fortress_data = [A1, (A2, B2)]
MAP.extension = {'nested': [B2], 'tuple': (A1,)}
''')
        self.assertTrue(result['complete'], result)
        self.assertEqual(result['values']['grid_class'], {'$ref': 'campaign.base.CustomGrid', 'kind': 'class'})
        self.assertEqual(result['values']['fortress_data'], ['A1', ['A2', 'B2']])
        self.assertEqual(result['values']['bouncing_enemy_data'], [['A1', 'B2'], ['A2', 'B1']])
        grid = result['typed_values']['fortress_data']['items'][0]
        self.assertEqual(grid, {'type': 'grid', 'location': [0, 0]})
        self.assertEqual(result['typed_values']['fortress_data']['items'][1]['type'], 'tuple')
        self.assertIn('campaign/base.py', result['source_files'])

    def test_copy_chains_preserve_source_fields_and_partial_overrides(self):
        self.source('campaign.base', self.header + "MAP.shape = 'A1'\nMAP.map_data = '--'\nMAP.camera_data = ['A1']\n")
        previous = 'base'
        for i in range(9):
            name = f'copy{i}'
            self.source('campaign.' + name, f'from .{previous} import MAP as SOURCE\n'
                        'from copy import copy as clone\nMAP = clone(SOURCE)\n'
                        f'MAP.name = "{name}"\nMAP.shape = "A1"\n')
            previous = name
        result = MapResolver(self.root).export('campaign.copy8')
        self.assertTrue(result['complete'], result)
        self.assertEqual(result['name'], 'copy8')
        self.assertEqual(result['values']['map_data'], '--')
        self.assertEqual(result['origins']['map_data']['module'], 'campaign.base')
        self.assertEqual(result['derived_from'], 'campaign/copy7.py')

    def test_source_order_rebinding_and_augmented_assignment(self):
        result = self.resolve(self.header + '''
COUNT = 2
MAP.value = COUNT
MAP.value += 3
MAP.copied = MAP.value
COUNT = 100
''')
        self.assertTrue(result['complete'], result)
        self.assertEqual(result['values'], {'value': 5, 'copied': 5})
        result = self.resolve(self.header + 'MAP.old = 10\nMAP = CampaignMap("new")\nMAP.new = 20\n')
        self.assertEqual(result['values'], {'new': 20})
        self.assertEqual(result['name'], 'new')

    def test_flatten_text_matches_native_literal_space_tokenization(self):
        result = self.resolve(self.header + "MAP.map_data = '--  --\\n\\n-- -- --'\n"
                              'A1, B1, C1, A2, B2, C2, A3, B3, C3 = MAP.flatten()\nMAP.route = (C1, A2, C3)\n')
        self.assertTrue(result['complete'], result)
        self.assertEqual(result['values']['route'], ['C1', 'A2', 'C3'])

    def test_shape_reassignment_preserves_native_grid_insertion_order(self):
        result = self.resolve(self.header + "MAP.shape = 'A2'\nMAP.shape = 'B2'\n"
                              'P, Q, R, S = MAP.flatten()\nMAP.route = (P, Q, R, S)\n')
        self.assertTrue(result['complete'], result)
        self.assertEqual(result['values']['route'], ['A1', 'A2', 'B1', 'B2'])

    def test_native_method_declaration_preserves_order_parameters_and_types(self):
        self.source('module.map.map_base', 'class CampaignMap:\n    def ignore_prediction(self, grid, **kwargs): pass\n')
        result = self.resolve(self.header + "MAP.shape = 'A1'\nA1, = MAP.flatten()\n"
                              'MAP.ignore_prediction(A1, enemy_scale=1)\nMAP.ignore_prediction(A1, is_siren=True)\n')
        self.assertTrue(result['complete'], result)
        self.assertEqual([c['kwargs'] for c in result['calls']], [{'enemy_scale': 1}, {'is_siren': True}])
        self.assertEqual(result['calls'][0]['typed_args']['items'][0]['type'], 'grid')

    def test_unknown_names_calls_and_overrides_fail_closed(self):
        for expression in ('MISSING', 'factory()', 'MissingClass', 'MAP.absent'):
            with self.subTest(expression=expression):
                result = self.resolve(self.header + 'MAP.value = 7\nMAP.value = ' + expression + '\n')
                self.assertFalse(result['complete'])
                self.assertNotIn('value', result['values'])
                self.assertEqual(result['unresolved'][0]['field'], 'value')

    def test_conditional_nested_method_and_alias_mutations_are_incomplete(self):
        for suffix in ('if enabled:\n    MAP.value = 9\n', 'MAP.value.append(2)\n',
                       'MAP.value[0] = 2\n', 'other = MAP\nother.value = [2]\n',
                       'mutate(MAP)\n', 'if enabled:\n    MAP.value.append(2)\n',
                       'del MAP.value\n'):
            with self.subTest(suffix=suffix):
                result = self.resolve(self.header + 'MAP.value = [1]\n' + suffix)
                self.assertFalse(result['complete'], result)
                self.assertTrue(result['unresolved'])

    def test_native_property_getter_is_not_replaced_by_declaration_value(self):
        self.source('module.map.map_base', 'class CampaignMap:\n    @property\n    def shape(self): pass\n')
        result = self.resolve(self.header + "MAP.shape = 'A1'\nMAP.snapshot = MAP.shape\n")
        self.assertFalse(result['complete'])
        self.assertNotIn('snapshot', result['values'])

    def test_imported_map_grids_and_missing_or_cyclic_imports(self):
        self.source('campaign.base', self.header + "MAP.shape = 'A2'\nA1, A2 = MAP.flatten()\n")
        result = self.resolve('from .base import MAP, A2\nMAP.route = (A2,)\n')
        self.assertTrue(result['complete'], result)
        self.assertEqual(result['values']['route'], ['A2'])
        self.assertFalse(self.resolve('from .missing import MAP\n')['complete'])
        self.source('campaign.other', 'from .fixture import MAP\n')
        self.assertFalse(self.resolve('from .other import MAP\n')['complete'])
        self.assertEqual(self.resolve('class Helper: pass\n')['present'], False)


if __name__ == '__main__':
    unittest.main()
