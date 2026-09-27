"""Offline control discovery and recognition error contracts."""
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from ui_rule_catalog import discover_controls


class ControlCatalogTests(unittest.TestCase):
    def test_aliases_inheritance_annotations_and_lazy_factories(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sources = {
                'module/ui/switch.py': 'class Switch: pass',
                'module/fixture/base.py': 'from module.ui.switch import Switch as Native\nclass Toggle(Native): pass',
                'module/fixture/rules.py': '''
from .base import Toggle as Mode
import module.ui.switch as switches
from module.ui.scroll import Scroll
ONE = Mode()
TWO: object = switches.Switch()
if True:
    THREE = Scroll()
class UI:
    @cached_property
    def dynamic(self):
        return Mode()
    def prepare(self, main):
        rule = Mode()
        rule.set(main)
''',
            }
            for name, source in sources.items():
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(source, encoding='utf-8')
            inventory = discover_controls(root)
            self.assertFalse(inventory['errors'])
            records = inventory['declarations']
            self.assertEqual({r['name'] for r in records if r['scope'] == 'module'},
                             {'ONE', 'TWO', 'THREE'})
            self.assertEqual([(r['owner'], r['attr'], r['kind']) for r in records
                              if r['scope'] == 'property'], [('UI', 'dynamic', 'Switch')])
            self.assertEqual([r['attr'] for r in records if r['scope'] == 'factory'], ['prepare'])

    def test_native_inventory_resolves_every_discovered_module_control(self):
        import alas_vision as av
        inventory = av.op_ui_rule_list({})
        self.assertFalse(inventory['errors'], inventory['errors'])
        declared = {(r['module'], r['attr']) for r in inventory['declarations'] if r['scope'] == 'module'}
        self.assertEqual(declared, {(r['module'], r['attr']) for r in inventory['rules']})
        self.assertTrue(any(r['class'] != r['native_class'] for r in inventory['rules']))
        self.assertTrue(any(r['scope'] == 'factory' for r in inventory['declarations']))

    def test_recognition_error_has_a_separate_structured_field(self):
        import alas_vision as av
        import module.ui.switch as module
        import numpy as np
        rule = module.Switch('fixture')
        with patch.object(rule, 'get', side_effect=RuntimeError('fixture recognition failure')), \
                patch.object(module, 'fixture', rule, create=True), \
                patch.dict(av._state, image=np.zeros((720, 1280, 3), dtype=np.uint8)):
            result = av.op_ui_rule_check({'module': module.__name__, 'name': 'fixture'})
        self.assertNotIn('get', result['results'])
        self.assertEqual({r['method'] for r in result['errors']}, {'get', 'appear'})

    def test_partial_switch_positive_control_cannot_pass(self):
        import alas_vision as av
        from module.ui.switch import Switch
        import module.ui.switch as module
        import numpy as np
        button = SimpleNamespace(ensure_template=lambda: None,
                                 image=np.zeros((10, 10, 3), dtype=np.uint8), area=(0, 0, 10, 10))
        rule = Switch('fixture')
        rule.add_state('present', button)
        rule.add_state('broken', None)
        inventory = {'rules': [{'module': module.__name__, 'name': 'fixture'}], 'errors': []}
        with patch.object(av, 'op_ui_rule_list', return_value=inventory), \
                patch.object(module, 'fixture', rule, create=True), \
                patch.object(av, '_make_main_shim', return_value=SimpleNamespace(appear=lambda *a, **k: True)):
            result = av.op_rule_positive_control({})
        self.assertEqual(result['passed'], 0)
        self.assertEqual(result['failed'], 1)

    def test_lazy_scroll_and_nested_controls_use_native_recognition(self):
        import alas_vision as av
        import numpy as np
        from module.base.base import ModuleBase
        from module.ui.scroll import Scroll

        class Lazy(ModuleBase):
            @property
            def rules(self):
                return (2, {'scroll': Scroll((10, 10, 20, 110), color=(180, 180, 180)),
                            'metadata': 'not a control'})

        canvas = np.zeros((720, 1280, 3), dtype=np.uint8)
        canvas[10:35, 10:20] = (180, 180, 180)
        with patch.dict(sys.modules, {'module.fixture_lazy': SimpleNamespace(Lazy=Lazy)}), \
                patch.dict(av._state, image=canvas), \
                patch.object(ModuleBase, 'EARLY_OCR_IMPORT', True):
            result = av.op_cached_rule_check(dict(module='module.fixture_lazy', **{'class': 'Lazy'}, attr='rules'))
        self.assertFalse(result['errors'])
        self.assertTrue(result['hit'])
        self.assertEqual(len(result['controls']), 1)
        control = result['controls'][0]
        self.assertEqual(control['path'], "$[1]['scroll']")
        self.assertEqual(control['class'], 'Scroll')
        self.assertTrue(control['detail']['appear'])
        self.assertTrue(control['detail']['at_top'])
        self.assertFalse(control['detail']['at_bottom'])

    def test_lazy_config_matches_selected_server(self):
        import alas_vision as av
        import numpy as np
        from module.base.base import ModuleBase
        from module.ui.scroll import Scroll

        seen = []

        class Lazy(ModuleBase):
            @property
            def rule(self):
                seen.append(self.config.SERVER)
                return Scroll((10, 10, 20, 110), color=(180, 180, 180))

        saved = av.server_module.server
        try:
            with patch.dict(sys.modules, {'module.fixture_lazy': SimpleNamespace(Lazy=Lazy)}), \
                    patch.dict(av._state, image=np.zeros((720, 1280, 3), dtype=np.uint8)), \
                    patch.object(ModuleBase, 'EARLY_OCR_IMPORT', True):
                for server in av.server_module.VALID_SERVER:
                    av.op_set_server({'server': server})
                    av.op_cached_rule_check(dict(module='module.fixture_lazy', **{'class': 'Lazy'}, attr='rule'))
            self.assertEqual(seen, list(av.server_module.VALID_SERVER))
        finally:
            av.op_set_server({'server': saved})

    def test_lazy_partial_failure_preserves_observations_but_does_not_hit(self):
        import alas_vision as av
        import numpy as np
        from module.base.base import ModuleBase
        from module.ui.scroll import Scroll

        good = Scroll((10, 10, 20, 110), color=(180, 180, 180))
        broken = Scroll((30, 10, 40, 110), color=(180, 180, 180))

        class Lazy(ModuleBase):
            @property
            def rules(self):
                return [good, broken]

        canvas = np.zeros((720, 1280, 3), dtype=np.uint8)
        canvas[10:35, 10:20] = (180, 180, 180)
        with patch.dict(sys.modules, {'module.fixture_lazy': SimpleNamespace(Lazy=Lazy)}), \
                patch.dict(av._state, image=canvas), \
                patch.object(broken, 'appear', side_effect=RuntimeError('fixture missing resource')), \
                patch.object(ModuleBase, 'EARLY_OCR_IMPORT', True):
            result = av.op_cached_rule_check(dict(module='module.fixture_lazy', **{'class': 'Lazy'}, attr='rules'))
        self.assertFalse(result['hit'])
        self.assertTrue(result['controls'][0]['hit'])
        self.assertFalse(result['controls'][1]['hit'])
        self.assertIn('$[1]: RuntimeError: fixture missing resource', result['errors'])

    def test_absent_lazy_scroll_is_a_negative_observation_without_position(self):
        import alas_vision as av
        import numpy as np
        from module.ui.scroll import Scroll

        rule = Scroll((10, 10, 20, 110), color=(180, 180, 180))
        main = av._make_main_shim(np.zeros((720, 1280, 3), dtype=np.uint8))
        with patch.object(rule, 'cal_position', side_effect=AssertionError('absent thumb has no position')):
            result = av._observe_ui_rule(rule, main, '$')
        self.assertFalse(result['hit'])
        self.assertFalse(result['errors'])
        self.assertIsNone(result['detail']['position'])

    def test_container_cycle_and_duplicate_control_do_not_repeat_recognition(self):
        import alas_vision as av
        from module.ui.scroll import Scroll

        rule = Scroll((10, 10, 20, 110), color=(180, 180, 180))
        value = [rule, {'repeat': rule}]
        value.append(value)
        self.assertEqual(list(av._contained_ui_rules(value)), [('$[0]', rule)])

    def test_new_factory_without_executed_fixture_fails_coverage(self):
        import alas_vision as av
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import verify_native_control_factories as factories
        declaration = dict(module='module.fixture_new', owner='Task', attr='run',
                           line=12, scope='factory', kind='Switch')
        with patch.object(factories, 'coalition_cases'), patch.object(factories, 'hospital_cases'):
            results = factories.check_factories(av, [declaration], servers=['cn'])
        self.assertEqual(results[0]['status'], 'failed')
        self.assertEqual(results[0]['synthetic_cases'], 0)
        self.assertIn('No executed factory fixture', results[0]['error'])


if __name__ == '__main__':
    unittest.main()
